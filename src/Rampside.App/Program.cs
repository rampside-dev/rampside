using System.Diagnostics;
using System.Threading.Channels;
using Rampside.App;
using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Orchestration;
using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

var fakePmdg = args.Contains("--fake-pmdg", StringComparer.OrdinalIgnoreCase);
var useFake = fakePmdg || args.Contains("--fake", StringComparer.OrdinalIgnoreCase);
var profileDirectory = Path.Combine(AppContext.BaseDirectory, "profiles");
var profiles = ProfileRepository.LoadFromDirectory(profileDirectory, error => Console.WriteLine($"! {error}"));

ISimBridge sim;
IPmdgLink? pmdgLink = null;
string pmdgInfo;

if (useFake)
{
    sim = new FakeSimBridge(fakePmdg
        ? FakeSimBridge.CreateDefaultState() with { Title = "737-800 PAX SSW (fake PMDG)" }
        : null);
    pmdgLink = fakePmdg ? new FakePmdgLink() : null;
    pmdgInfo = fakePmdg ? "fake PMDG 737" : "not used";
}
else
{
#if RAMPSIDE_SIMCONNECT
    var bridge = new Rampside.MsfsBridge.SimConnectBridge();
    sim = bridge;
    pmdgInfo = SetUpPmdg(bridge, out pmdgLink);
#else
    Console.WriteLine("! Built without the MSFS SDK, starting with the fake simulator.");
    sim = new FakeSimBridge();
    pmdgInfo = "not available without the MSFS SDK";
#endif
}

var fake = sim as FakeSimBridge;
GroundServiceOrchestrator? orchestrator = null;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

sim.Message += (_, message) => Console.WriteLine($"  [sim] {message}");
sim.ConnectionChanged += (_, connected) =>
{
    if (connected)
    {
        Console.WriteLine("Connected to simulator.");
    }
    else if (!cts.IsCancellationRequested)
    {
        Console.WriteLine("Simulator disconnected, waiting for reconnect...");
    }

    if (!connected)
    {
        orchestrator = null;
    }
};

Console.WriteLine($"Rampside 0.1 ({(fake is null ? "MSFS" : "fake sim")}), {profiles.Profiles.Count} profile(s) loaded. Type 'help'.");
Console.WriteLine($"PMDG 737 SDK: {pmdgInfo}");
await sim.StartAsync(cts.Token);

var shell = new ConsoleShell(sim, fake, () => orchestrator, pmdgLink);
var input = Channel.CreateUnbounded<string>();
var inputThread = new Thread(() =>
{
    while (Console.ReadLine() is { } line)
    {
        input.Writer.TryWrite(line);
    }

    input.Writer.TryComplete();
})
{
    IsBackground = true,
};
inputThread.Start();

using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
var clock = Stopwatch.StartNew();
var last = clock.Elapsed;

try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        var now = clock.Elapsed;
        var elapsed = now - last;
        last = now;

        fake?.Advance(elapsed);
        (pmdgLink as FakePmdgLink)?.Advance(elapsed);

        if (orchestrator is null && sim.IsConnected && !string.IsNullOrEmpty(sim.State.Title))
        {
            var profile = profiles.Resolve(sim.State.Title);
            var adapter = CreateAdapter(profile);
            orchestrator = new GroundServiceOrchestrator(sim, profile, adapter);
            orchestrator.ServiceChanged += (_, service) =>
                Console.WriteLine($"  [{service.DisplayName}] {service.Status}: {service.Detail}");
            Console.WriteLine($"Aircraft '{sim.State.Title}' uses profile '{profile.Name}' ({adapter.Name} adapter).");
        }

        orchestrator?.Tick(elapsed);

        while (input.Reader.TryRead(out var line))
        {
            if (!shell.Execute(line))
            {
                cts.Cancel();
            }
        }

        if (input.Reader.Completion.IsCompleted)
        {
            cts.Cancel();
        }
    }
}
catch (OperationCanceledException)
{
}

await sim.DisposeAsync();
Console.WriteLine("Bye.");

IAircraftAdapter CreateAdapter(AircraftProfile profile)
{
    if (string.Equals(profile.Adapter, AircraftProfile.Pmdg737Adapter, StringComparison.OrdinalIgnoreCase))
    {
        // Never fall back to writing sim variables on a PMDG aircraft: its EULA only allows the SDK.
        return new Pmdg737Adapter(pmdgLink ?? new UnavailablePmdgLink($"PMDG 737 SDK not available: {pmdgInfo}"));
    }

    return new DefaultAircraftAdapter(sim, profile);
}

#if RAMPSIDE_SIMCONNECT
static string SetUpPmdg(Rampside.MsfsBridge.SimConnectBridge bridge, out IPmdgLink? link)
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
