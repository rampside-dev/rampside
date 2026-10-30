using Rampside.App;
using Rampside.Hosting;

var options = new RampsideHostOptions
{
    UseFakeSim = args.Contains("--fake", StringComparer.OrdinalIgnoreCase),
    FakePmdg = args.Contains("--fake-pmdg", StringComparer.OrdinalIgnoreCase),
};

await using var host = new RampsideHost(options);
host.Log += (_, line) => Console.WriteLine(line);
await host.StartAsync(CancellationToken.None);
Console.WriteLine("Type 'help' for commands.");

var shell = new ConsoleShell(host.Sim, host.Fake, () => host.Orchestrator, host.Pmdg);
while (Console.ReadLine() is { } line)
{
    // The shell touches the orchestrator, so it runs on the host loop.
    if (!await host.InvokeAsync(() => shell.Execute(line)))
    {
        break;
    }
}

Console.WriteLine("Bye.");
