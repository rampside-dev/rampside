using Rampside.App;
using Rampside.Hosting;

var settings = RampsideSettings.Load(null, out var settingsError);
if (settingsError is not null)
{
    Console.WriteLine($"! {settingsError}");
}

var options = new RampsideHostOptions
{
    UseFakeSim = args.Contains("--fake", StringComparer.OrdinalIgnoreCase),
    FakePmdg = args.Contains("--fake-pmdg", StringComparer.OrdinalIgnoreCase),
    Passive = args.Contains("--passive", StringComparer.OrdinalIgnoreCase),
    SimBriefUser = RampsideSettings.Normalize(
        OptionValue(args, "--simbrief") ?? Environment.GetEnvironmentVariable("RAMPSIDE_SIMBRIEF") ?? settings.SimBriefUser),
    CrewVoiceEnabled = settings.CrewVoice && !args.Contains("--no-voice", StringComparer.OrdinalIgnoreCase),
    CrewVoiceVolume = settings.CrewVoiceVolume,
};

await using var host = new RampsideHost(options);
host.Log += (_, line) => Console.WriteLine(line);
await host.StartAsync(CancellationToken.None);
Console.WriteLine(host.SimBriefUser is { } user
    ? $"SimBrief user: {user} (type 'ofp' to load the latest flight plan)."
    : "SimBrief: not set (Settings tab of the desktop window, or start with --simbrief <pilot id or username>).");
Console.WriteLine("Type 'help' for commands.");

var shell = new ConsoleShell(host.Sim, host.Fake, () => host.Orchestrator, host.Pmdg);
while (Console.ReadLine() is { } line)
{
    // Fetching from SimBrief is network I/O, so it must not run on the host loop.
    if (ConsoleShell.ParseOfpLoad(line) is { } force)
    {
        Console.WriteLine("  Fetching the latest SimBrief flight plan...");
        var result = await host.LoadSimBriefAsync(force);
        ConsoleShell.PrintResult(result);
        continue;
    }

    // The shell touches the orchestrator, so it runs on the host loop.
    if (!await host.InvokeAsync(() => shell.Execute(line)))
    {
        break;
    }
}

Console.WriteLine("Bye.");

static string? OptionValue(string[] args, string name)
{
    var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
