using System.Diagnostics;
using System.Threading.Channels;
using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Orchestration;
using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

namespace Rampside.Hosting;

public sealed class RampsideHostOptions
{
    /// <summary>Use the in-memory simulator instead of MSFS.</summary>
    public bool UseFakeSim { get; init; }

    /// <summary>Fake simulator with a fake PMDG 737 (implies <see cref="UseFakeSim"/>).</summary>
    public bool FakePmdg { get; init; }

    public string ProfileDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "profiles");
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
    private GroundServiceOrchestrator? _orchestrator;
    private volatile bool _resetOrchestrator;
    private volatile RampsideSnapshot _snapshot = RampsideSnapshot.Empty;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public RampsideHost(RampsideHostOptions options)
    {
        Profiles = ProfileRepository.LoadFromDirectory(options.ProfileDirectory, error => Write($"! {error}"));

        if (options.UseFakeSim || options.FakePmdg)
        {
            Fake = new FakeSimBridge(options.FakePmdg
                ? FakeSimBridge.CreateDefaultState() with { Title = "737-800 PAX SSW (fake PMDG)" }
                : null);
            Sim = Fake;
            Pmdg = options.FakePmdg ? new FakePmdgLink() : null;
            PmdgInfo = options.FakePmdg ? "fake PMDG 737" : "not used";
        }
        else
        {
#if RAMPSIDE_SIMCONNECT
            var bridge = new Rampside.MsfsBridge.SimConnectBridge();
            Sim = bridge;
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
    public ProfileRepository Profiles { get; }
    public RampsideSnapshot Snapshot => _snapshot;

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
                (Pmdg as FakePmdgLink)?.Advance(elapsed);
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

        if (_orchestrator is not null || !Sim.IsConnected || string.IsNullOrEmpty(Sim.State.Title))
        {
            return;
        }

        var profile = Profiles.Resolve(Sim.State.Title);
        var adapter = CreateAdapter(profile);
        _orchestrator = new GroundServiceOrchestrator(Sim, profile, adapter);
        _orchestrator.ServiceChanged += (_, service) => Write($"  [{service.DisplayName}] {service.Status}: {service.Detail}");
        Write($"Aircraft '{Sim.State.Title}' uses profile '{profile.Name}' ({adapter.Name} adapter).");
    }

    private IAircraftAdapter CreateAdapter(AircraftProfile profile)
    {
        if (string.Equals(profile.Adapter, AircraftProfile.Pmdg737Adapter, StringComparison.OrdinalIgnoreCase))
        {
            // Never fall back to writing sim variables on a PMDG aircraft: its EULA only allows the SDK.
            return new Pmdg737Adapter(Pmdg ?? new UnavailablePmdgLink($"PMDG 737 SDK not available: {PmdgInfo}"));
        }

        return new DefaultAircraftAdapter(Sim, profile);
    }

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
            Services = o?.Services
                .Select(s => new ServiceSnapshot(s.Id, s.DisplayName, s.Status, s.Detail, s.Progress, s.IsBusy))
                .ToArray() ?? Array.Empty<ServiceSnapshot>(),
            Doors = o?.Adapter.Doors
                .Select(d => new DoorSnapshot(d, GroundServiceOrchestrator.DoorName(d), o.Adapter.GetDoorState(d)))
                .ToArray() ?? Array.Empty<DoorSnapshot>(),
            PushbackSpeedMps = o?.Pushback.SpeedMps ?? 0,
            PushbackSteeringDeg = o?.Pushback.SteeringDeg ?? 0,
        };
    }

    private void Write(string line) => Log?.Invoke(this, line);

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
