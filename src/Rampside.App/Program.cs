using System.Diagnostics;
using System.Threading.Channels;
using Rampside.App;
using Rampside.Core.Orchestration;
using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

var useFake = args.Contains("--fake", StringComparer.OrdinalIgnoreCase);
var profileDirectory = Path.Combine(AppContext.BaseDirectory, "profiles");
var profiles = ProfileRepository.LoadFromDirectory(profileDirectory, error => Console.WriteLine($"! {error}"));

#if RAMPSIDE_SIMCONNECT
ISimBridge sim = useFake ? new FakeSimBridge() : new Rampside.MsfsBridge.SimConnectBridge();
#else
if (!useFake)
{
    Console.WriteLine("! Built without the MSFS SDK, starting with the fake simulator.");
}

ISimBridge sim = new FakeSimBridge();
#endif

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
await sim.StartAsync(cts.Token);

var shell = new ConsoleShell(sim, fake, () => orchestrator);
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

        if (orchestrator is null && sim.IsConnected && !string.IsNullOrEmpty(sim.State.Title))
        {
            var profile = profiles.Resolve(sim.State.Title);
            orchestrator = new GroundServiceOrchestrator(sim, profile);
            orchestrator.ServiceChanged += (_, service) =>
                Console.WriteLine($"  [{service.DisplayName}] {service.Status}: {service.Detail}");
            Console.WriteLine($"Aircraft '{sim.State.Title}' uses profile '{profile.Name}'.");
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
