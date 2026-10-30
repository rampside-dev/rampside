using System.Diagnostics;
using System.Threading.Channels;
using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.FlightPlans;
using Rampside.Core.Gsx;
using Rampside.Core.Orchestration;
using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;
using Rampside.Hosting.Audio;

namespace Rampside.Hosting;

public sealed class RampsideHostOptions
{
    /// <summary>Use the in-memory simulator instead of MSFS.</summary>
    public bool UseFakeSim { get; init; }

    /// <summary>Fake simulator with a fake PMDG 737 (implies <see cref="UseFakeSim"/>).</summary>
    public bool FakePmdg { get; init; }

    /// <summary>
    /// Only read the sim (stand, nearby objects, PMDG data), never act: no orchestrator, so no door automation or
    /// requests. For diagnostics next to a running Rampside, which would otherwise be doubled.
    /// </summary>
    public bool Passive { get; init; }

    public string ProfileDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "profiles");

    /// <summary>SimBrief pilot ID or username for loading the latest flight plan, or null.</summary>
    public string? SimBriefUser { get; init; }

    /// <summary>Folder with installed GSX Pro airport profiles (pushback targets per stand); null turns them off. Not used with the fake simulator.</summary>
    public string? GsxProfileFolder { get; init; } = GsxProfileLibrary.DefaultFolder;

    /// <summary>Folder with the ground crew voice clips; null turns the voice off completely.</summary>
    public string? CrewVoiceFolder { get; init; } = CrewVoiceLibrary.DefaultFolder;

    /// <summary>Whether the ground crew talks at start (can be changed later through <see cref="RampsideHost.CrewVoice"/>).</summary>
    public bool CrewVoiceEnabled { get; init; } = true;

    /// <summary>0..1.</summary>
    public double CrewVoiceVolume { get; init; } = 0.8;
}

/// <summary>
/// Runs Rampside on its own loop (20 Hz): connects the simulator, resolves profile and adapter for the
/// user aircraft and ticks the services. The orchestrator is not thread-safe, so every command from a UI
/// goes through <see cref="ExecuteAsync"/> or <see cref="InvokeAsync{T}"/> and runs on the loop.
/// </summary>
public sealed class RampsideHost : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(200);

    private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>();
    private readonly SimBriefClient _simBrief = new();
    private GroundServiceOrchestrator? _orchestrator;
    private volatile bool _resetOrchestrator;
    private volatile RampsideSnapshot _snapshot = RampsideSnapshot.Empty;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly RampsideHostOptions _options;

    public RampsideHost(RampsideHostOptions options)
    {
        _options = options;
        SimBriefUser = options.SimBriefUser;
        if (!options.UseFakeSim && !options.FakePmdg && options.GsxProfileFolder is { } gsxFolder && Directory.Exists(gsxFolder))
        {
            GsxProfiles = new GsxProfileLibrary(gsxFolder);
            GsxProfiles.Message += (_, message) => Write($"  [gsx] {message}");
        }

        Profiles = ProfileRepository.LoadFromDirectory(options.ProfileDirectory, error => Write($"! {error}"));
        CrewVoice = CreateCrewVoice(options);

        if (options.UseFakeSim || options.FakePmdg)
        {
            var state = FakeSimBridge.CreateDefaultState() with
            {
                Stand = FakeSimBridge.DemoStand,
                AirportIcao = FakeSimBridge.DemoStand.AirportIcao,
                AirportParkings = FakeSimBridge.DemoParkings(),
            };
            Fake = new FakeSimBridge(options.FakePmdg ? state with { Title = "737-800 PAX SSW (fake PMDG)" } : state);
            Sim = Fake;
            Pmdg = options.FakePmdg ? new FakePmdgLink() : null;
            PmdgInfo = options.FakePmdg ? "fake PMDG 737" : "not used";
        }
        else
        {
#if RAMPSIDE_SIMCONNECT
            var bridge = new Rampside.MsfsBridge.SimConnectBridge();
            Sim = bridge;
            var stands = new Rampside.MsfsBridge.StandMonitor();
            stands.Message += (_, message) => Write($"  [stand] {message}");
            bridge.AddExtension(stands);
            bridge.StandSource = () => stands.Stand;
            bridge.AirportSource = () => stands.Airport;
            bridge.ParkingSource = () => stands.Parkings;
            var nearby = new Rampside.MsfsBridge.NearbyObjectScanner();
            bridge.AddExtension(nearby);
            bridge.NearbySource = () => nearby.Objects;
            PmdgInfo = SetUpPmdg(bridge, out var link);
            Pmdg = link;
#else
            Write("! Built without the MSFS SDK, using the fake simulator.");
            Fake = new FakeSimBridge();
            Sim = Fake;
            PmdgInfo = "not available without the MSFS SDK";
#endif
        }

        Sim.Message += (_, message) => Write($"  [sim] {message}");
        Sim.ConnectionChanged += (_, connected) =>
        {
            if (!connected)
            {
                _resetOrchestrator = true;
            }

            if (_cts is { IsCancellationRequested: false })
            {
                Write(connected ? "Connected to simulator." : "Simulator disconnected, waiting for reconnect...");
            }
        };
    }

    /// <summary>Log lines for display (sim messages, service changes, connection changes). Raised on background threads.</summary>
    public event EventHandler<string>? Log;

    /// <summary>Raised on the host loop thread about five times per second.</summary>
    public event EventHandler<RampsideSnapshot>? SnapshotUpdated;

    public ISimBridge Sim { get; }
    public FakeSimBridge? Fake { get; }
    public IPmdgLink? Pmdg { get; }
    public string PmdgInfo { get; }

    /// <summary>Plays what the ground crew says; null when there are no clips or the system cannot play them.</summary>
    public CrewVoicePlayer? CrewVoice { get; }
    public ProfileRepository Profiles { get; }

    /// <summary>Installed GSX profiles, or null when GSX is not installed or the fake simulator runs.</summary>
    public GsxProfileLibrary? GsxProfiles { get; }
    public RampsideSnapshot Snapshot => _snapshot;

    /// <summary>SimBrief pilot ID or username, or null when not set.</summary>
    public string? SimBriefUser { get; set; }

    /// <summary>
    /// Fetches the latest SimBrief plan (off the loop) and hands it to the orchestrator, which checks it against
    /// the aircraft. <paramref name="force"/> loads it despite a type or airport mismatch.
    /// </summary>
    /// <summary>Fetches the latest SimBrief plan of <paramref name="user"/> without loading it. Throws <see cref="SimBriefException"/>.</summary>
    public Task<FlightPlan> FetchSimBriefAsync(string user, CancellationToken cancellationToken = default) =>
        _simBrief.FetchLatestAsync(user, cancellationToken);

    public async Task<CommandResult> LoadSimBriefAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        FlightPlan plan;
        try
        {
            plan = await FetchSimBriefAsync(SimBriefUser ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (SimBriefException ex)
        {
            return CommandResult.Rejected(ex.Message);
        }

        return await ExecuteAsync(o => o.LoadFlightPlan(plan, force)).ConfigureAwait(false);
    }

    /// <summary>Current orchestrator. Only use it inside <see cref="InvokeAsync{T}"/> or <see cref="ExecuteAsync"/>.</summary>
    public GroundServiceOrchestrator? Orchestrator => _orchestrator;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Write($"Rampside 0.1 ({(Fake is null ? "MSFS" : "fake sim")}), {Profiles.Profiles.Count} profile(s) loaded.");
        Write($"PMDG 737 SDK: {PmdgInfo}");
        await Sim.StartAsync(_cts.Token).ConfigureAwait(false);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Runs a command against the orchestrator on the host loop.</summary>
    public Task<CommandResult> ExecuteAsync(Func<GroundServiceOrchestrator, CommandResult> command) =>
        InvokeAsync(() => _orchestrator is null
            ? CommandResult.Rejected("Waiting for the simulator and the user aircraft.")
            : command(_orchestrator));

    /// <summary>Runs any code on the host loop, e.g. several orchestrator calls that belong together.</summary>
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _work.Writer.TryWrite(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        if (!queued)
        {
            completion.SetException(new ObjectDisposedException(nameof(RampsideHost)));
        }

        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _work.Writer.TryComplete();
        await Sim.DisposeAsync().ConfigureAwait(false);
        _simBrief.Dispose();
        CrewVoice?.Dispose();
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TickInterval);
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        var sinceSnapshot = SnapshotInterval;

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = clock.Elapsed;
                var elapsed = now - last;
                last = now;

                Fake?.Advance(elapsed);
                if (Pmdg is FakePmdgLink fakePmdg)
                {
                    fakePmdg.Advance(elapsed);
                    Fake?.Update(s => s with { NearbyObjects = fakePmdg.NearbyObjects(s) });
                }

                UpdateOrchestrator();
                _orchestrator?.Tick(elapsed);

                while (_work.Reader.TryRead(out var work))
                {
                    work();
                }

                sinceSnapshot += elapsed;
                if (sinceSnapshot >= SnapshotInterval)
                {
                    sinceSnapshot = TimeSpan.Zero;
                    _snapshot = BuildSnapshot();
                    SnapshotUpdated?.Invoke(this, _snapshot);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateOrchestrator()
    {
        if (_resetOrchestrator)
        {
            _resetOrchestrator = false;
            _orchestrator = null;
        }

        if (_options.Passive || _orchestrator is not null || !Sim.IsConnected || string.IsNullOrEmpty(Sim.State.Title))
        {
            return;
        }

        var profile = Profiles.Resolve(Sim.State.Title);
        var adapter = CreateAdapter(profile);
        _orchestrator = new GroundServiceOrchestrator(Sim, profile, adapter, GsxProfiles);
        _orchestrator.ServiceChanged += (_, service) => Write($"  [{service.DisplayName}] {service.Status}: {service.Detail}");
        if (CrewVoice is { } voice)
        {
            _orchestrator.CrewCalled += (_, call) => voice.Enqueue(call);
        }

        _orchestrator.Message += (_, text) => Write($"  [{adapter.Name}] {text}");
        Write($"Aircraft '{Sim.State.Title}' uses profile '{profile.Name}' ({adapter.Name}: {adapter.Summary}).");
    }

    /// <summary>
    /// One procedure per job, as the profile's family and overrides say. PMDG procedures get the SDK link (or a stand-in
    /// that explains why there is none); the catalog never lets a PMDG profile write sim variables (PMDG EULA).
    /// </summary>
    private Rampside.Core.Aircraft.ComposedAircraftAdapter CreateAdapter(AircraftProfile profile) =>
        Rampside.Core.Aircraft.Procedures.AircraftProcedureCatalog.Create(
            profile,
            new Rampside.Core.Aircraft.Procedures.ProcedureContext(
                Sim, profile, Pmdg ?? new UnavailablePmdgLink($"PMDG 737 SDK not available: {PmdgInfo}")));

    private RampsideSnapshot BuildSnapshot()
    {
        var o = _orchestrator;
        return new RampsideSnapshot
        {
            SimConnected = Sim.IsConnected,
            UsingFakeSim = Fake is not null,
            Aircraft = Sim.State,
            ProfileName = o?.Profile.Name,
            AdapterName = o?.Adapter.Name,
            AdapterProblem = o is { Adapter.IsReady: false } ? o.Adapter.NotReadyReason : null,
            PmdgInfo = PmdgInfo,
            PassengersOnBoard = o?.Passengers.PassengersOnBoard ?? 0,
            PassengerCapacity = o?.Profile.Passengers.Capacity ?? 0,
            FlightPlan = o?.FlightPlan,
            FlightPlanReview = o?.FlightPlanReview,
            Services = o?.Services
                .Select(s => new ServiceSnapshot(s.Id, s.DisplayName, s.Status, s.Detail, s.Progress, s.IsBusy))
                .ToArray() ?? Array.Empty<ServiceSnapshot>(),
            Doors = o?.Adapter.Doors
                .Select(d => new DoorSnapshot(d, GroundServiceOrchestrator.DoorName(d), o.Adapter.GetDoorState(d)))
                .ToArray() ?? Array.Empty<DoorSnapshot>(),
            PushbackSpeedMps = o?.Pushback.SpeedMps ?? 0,
            PushbackSteeringDeg = o?.Pushback.SteeringDeg ?? 0,
            PushbackOptions = o?.PushbackOptions() ?? Array.Empty<PushbackOption>(),
            PushbackPlanLabel = o?.Pushback.Plan?.Label,
        };
    }

    private void Write(string line) => Log?.Invoke(this, line);

    private CrewVoicePlayer? CreateCrewVoice(RampsideHostOptions options)
    {
        if (options.CrewVoiceFolder is null)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            Write("Ground crew voice: only available on Windows.");
            return null;
        }

        var library = CrewVoiceLibrary.Load(options.CrewVoiceFolder);
        if (library.IsEmpty)
        {
            Write($"Ground crew voice: no clips in {options.CrewVoiceFolder}.");
            return null;
        }

        var player = new CrewVoicePlayer(library, new WinMmAudioOutput())
        {
            Enabled = options.CrewVoiceEnabled,
            Volume = options.CrewVoiceVolume,
        };
        player.Speaking += (_, text) => Write($"  [Ground crew] \"{text}\"");
        player.Problem += (_, text) => Write($"! {text}");
        return player;
    }

#if RAMPSIDE_SIMCONNECT
    private static string SetUpPmdg(Rampside.MsfsBridge.SimConnectBridge bridge, out IPmdgLink? link)
    {
        link = null;
        var sdk = PmdgAircraftSdk.Pmdg737;
        var headerPath = PmdgSdkLocator.FindHeader(sdk, Environment.GetEnvironmentVariable("RAMPSIDE_PMDG737_HEADER"));
        if (headerPath is null)
        {
            return $"header {sdk.HeaderFile} not found in the MSFS package folders";
        }

        try
        {
            var simLink = new Rampside.MsfsBridge.PmdgSimConnectLink(sdk, PmdgSdkHeader.Load(headerPath), bridge);
            bridge.AddExtension(simLink);
            link = simLink;
            return $"{headerPath} ({simLink.DataSize} bytes of aircraft data)";
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            return $"cannot use {headerPath}: {ex.Message}";
        }
    }
#endif
}
