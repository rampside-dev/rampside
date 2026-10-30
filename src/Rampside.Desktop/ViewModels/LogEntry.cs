using System.Globalization;

namespace Rampside.Desktop.ViewModels;

public enum LogKind
{
    Info,
    Warning,
    Accepted,
    Rejected,
}

public sealed record LogEntry(DateTime Time, string Text, LogKind Kind)
{
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public override string ToString() => $"{TimeText}  {Text}";
}
