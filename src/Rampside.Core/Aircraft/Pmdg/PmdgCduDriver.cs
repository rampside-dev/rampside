using System.Text.RegularExpressions;

namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>
/// One item on the PMDG GROUND SERVICES pages. Either a request line (&lt;REQUEST / &lt;RELEASE) or, with
/// <paramref name="OnOption"/> and <paramref name="OffOption"/>, a two-way option where the selected one is shown
/// in the large font (WHEEL CHOCKS: SET / REMOVED).
/// </summary>
/// <param name="Page">Page number on GROUND SERVICES (1/4, 2/4, ...); 0 = shown on every page.</param>
/// <param name="Label">Small-font label above the item, e.g. "GROUND POWER" or "STAIRS 1L".</param>
/// <param name="Key">Line select key of the item, used when the screen cannot be read.</param>
public sealed record PmdgServiceItem(int Page, string Label, string Key, string? OnOption = null, string? OffOption = null);

/// <summary>
/// Works the PMDG 737 CDU like a pilot would: MENU → FS ACTIONS → GROUND SERVICES → page → line select key.
/// Uses the right CDU only, so the pilot's left CDU is left alone.
/// While the CDU broadcast shows the screen, every key press is decided from it (starting from whatever page is
/// open, and checking the line changed). When the screen cannot be read (cold and dark, or EnableCDUBroadcast.1
/// missing) the known key sequence is pressed blind; the caller must then only ask for real changes, because the
/// line select keys toggle.
/// </summary>
/// <remarks>
/// Screens read in MSFS 2024 on 30 Oct 2026 (PMDG 737-800):
/// MENU has "FS ACTIONS>" at R5; FS ACTIONS has "GROUND / SERVICES>" at R1;
/// GROUND SERVICES page 1 "AIRPLANE SERVICE 1/4" has GROUND POWER (&lt;REQUEST) at L2,
/// page 3 "PASSENGER SERVICE 3/4" has STAIRS 1L at L1 and STAIRS 2L at L2 (&lt;REQUEST / &lt;RELEASE).
/// Cold and dark (only DC HOT BATT powered) both CDUs show MENU but the broadcast reports them off with an empty
/// screen; the keys still work: MENU, R5, R1, L2 on the right CDU brought the PMDG GPU.
/// </remarks>
public sealed class PmdgCduDriver
{
    // Sent with CDU key events, as in the PMDG SDK sample (MOUSE_FLAG_LEFTSINGLE). Confirmed in the sim.
    public const uint KeyParameter = 0x2000_0000;

    // MOUSE_FLAG_LEFTRELEASE: ends a held key.
    public const uint KeyReleaseParameter = 0x0002_0000;

    private static readonly TimeSpan KeyInterval = TimeSpan.FromSeconds(0.8);

    // Blind keys get no feedback, so they are spaced more widely. A dark CDU only wakes up on the first key and
    // drops keys while it starts (seen in the sim: MENU, R5, R1, L2 on a dark right CDU ended on the DLNK page,
    // i.e. only MENU and L2 arrived), so the first blind MENU is followed by a long pause and a second MENU.
    private static readonly TimeSpan BlindKeyInterval = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan WakeUpTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);
    private const int MaxKeys = 20;

    private static readonly Regex ServicePage = new(@"SERVICE\s+(\d)/(\d)\s*$", RegexOptions.Compiled);
    private static readonly string[] OnWords = { "RELEASE", "REMOVE", "DISCONNECT", "CANCEL" };

    private readonly IPmdgLink _link;
    private readonly Queue<Job> _jobs = new();
    private Job? _current;
    private TimeSpan _sinceKey = KeyInterval;
    private TimeSpan _pause = KeyInterval;

    public PmdgCduDriver(IPmdgLink link) => _link = link;

    /// <summary>Raised with a short text when a request finished or failed.</summary>
    public event EventHandler<string>? Message;

    /// <summary>Raised when an item is done: true when it is (or already was) in the wanted state.</summary>
    public event Action<PmdgServiceItem, bool>? Finished;

    public bool IsBusy => _current is not null || _jobs.Count > 0;

    /// <summary>
    /// Rampside only uses the first officer's (right) CDU, so the pilot can keep preparing the flight on the left one.
    /// </summary>
    public const int CduIndex = 1;

    /// <summary>
    /// True while the right CDU's screen arrives through the broadcast. Without the broadcast option the PMDG still has
    /// the data area but leaves it empty and "off" (seen in the sim), the same as a CDU without power.
    /// </summary>
    public bool CanRead => _link.GetCdu(CduIndex) is { Powered: true };

    /// <summary>
    /// Asks for an item to be switched on or off. <paramref name="isOn"/> is what the aircraft shows from outside
    /// (vehicle in place, GRD POWER AVAILABLE); it decides whether a blind key press is needed.
    /// </summary>
    public void Enqueue(PmdgServiceItem item, bool on, bool? isOn)
    {
        // A newer request for the same item replaces a waiting one.
        var waiting = _jobs.Where(j => j.Item != item).ToList();
        _jobs.Clear();
        foreach (var job in waiting)
        {
            _jobs.Enqueue(job);
        }

        _jobs.Enqueue(new Job(item, on, isOn));
    }

    /// <summary>
    /// Opens the item's page without pressing anything, so its state can be read (<see cref="ReadState"/>).
    /// Only while the right CDU can be read and nothing else is queued.
    /// </summary>
    public void Peek(PmdgServiceItem item)
    {
        if (CanRead && _current is null && _jobs.Count == 0)
        {
            _jobs.Enqueue(new Job(item, on: false, isOn: null) { Peek = true });
        }
    }

    /// <summary>The item's state if the right CDU shows it right now (true = on/requested/set), else null.</summary>
    public bool? ReadState(PmdgServiceItem item)
    {
        if (_link.GetCdu(CduIndex) is not { Powered: true } screen)
        {
            return null;
        }

        var page = ServicePage.Match(screen.Lines.Count > 0 ? screen.Lines[0].Trim() : string.Empty);
        if (!page.Success || (item.Page != 0 && int.Parse(page.Groups[1].Value) != item.Page))
        {
            return null;
        }

        return Locate(screen.Lines, item.Label) is { } found ? Evaluate(screen, item, found) : null;
    }

    public void Tick(TimeSpan elapsed)
    {
        _sinceKey += elapsed;
        if (_current is null && !_jobs.TryDequeue(out _current))
        {
            return;
        }

        var job = _current;
        job.Elapsed += elapsed;
        if (job.Elapsed > JobTimeout || job.Keys > MaxKeys)
        {
            Finish($"CDU: gave up on {Describe(job)} (page not reached).", ok: false);
            return;
        }

        if (_sinceKey < _pause)
        {
            return; // give the CDU time to show the result of the last key
        }

        // Once a job has started blind it stays blind, so a sequence is never mixed with screen-based keys.
        var blind = job.BlindStep > 0 || !CanRead;
        var key = blind ? DecideBlind(job) : Decide(job, _link.GetCdu(CduIndex)!);
        if (key is not null)
        {
            if (_link.SendEvent($"EVT_CDU_R_{key}", KeyParameter))
            {
                job.Keys++;
                _sinceKey = TimeSpan.Zero;
                _pause = !blind ? KeyInterval : job.BlindStep == 1 ? WakeUpTime : BlindKeyInterval;
            }
            else
            {
                Finish($"CDU: key {key} is not in the PMDG SDK header.", ok: false);
            }
        }
    }

    /// <summary>Next key of the fixed sequence MENU (wake), MENU, R5, R1, NEXT_PAGE..., item key; null when done or not needed.</summary>
    private string? DecideBlind(Job job)
    {
        if (job.Peek)
        {
            Finish(null, ok: false); // nothing to read on a dark CDU
            return null;
        }

        if (job.BlindStep == 0)
        {
            if (job.IsOn is null)
            {
                Finish($"CDU: cannot {Describe(job)}: the right CDU cannot be read and the current state is unknown.", ok: false);
                return null;
            }

            if (job.IsOn == job.On)
            {
                Finish(null, ok: true); // already there; pressing would toggle it back
                return null;
            }
        }

        // The first MENU may only wake the CDU; the second one is the one that counts.
        var keys = new List<string> { "MENU", "MENU", "R5", "R1" };
        keys.AddRange(Enumerable.Repeat("NEXT_PAGE", Math.Max(0, job.Item.Page - 1)));
        keys.Add(job.Item.Key);

        if (job.BlindStep >= keys.Count)
        {
            Finish($"CDU: {Describe(job)} sent (right CDU not readable, keys pressed blind).", ok: true);
            return null;
        }

        return keys[job.BlindStep++];
    }

    /// <summary>The key to press next for <paramref name="job"/>, or null when it is finished (see <see cref="Finish"/>).</summary>
    private string? Decide(Job job, PmdgCduScreen screen)
    {
        var lines = screen.Lines;
        var title = lines.Count > 0 ? lines[0].Trim() : string.Empty;

        var page = ServicePage.Match(title);
        if (page.Success)
        {
            var number = int.Parse(page.Groups[1].Value);
            if (job.Item.Page != 0 && number != job.Item.Page)
            {
                return number < job.Item.Page ? "NEXT_PAGE" : "PREV_PAGE";
            }

            return DecideOnServicePage(job, screen);
        }

        if (title.Contains("FS ACTIONS", StringComparison.Ordinal))
        {
            // "GROUND" (small font) above "SERVICES>"; "GROUND MAINTENANCE>" is a different page.
            for (var row = 2; row <= 12; row += 2)
            {
                if (lines[row].TrimEnd().EndsWith("SERVICES>", StringComparison.Ordinal) && lines[row - 1].Contains("GROUND", StringComparison.Ordinal))
                {
                    return $"R{row / 2}";
                }
            }

            Finish("CDU: GROUND SERVICES not found on the FS ACTIONS page.", ok: false);
            return null;
        }

        for (var row = 2; row <= 12 && title == "MENU"; row += 2)
        {
            if (lines[row].Contains("FS ACTIONS>", StringComparison.Ordinal))
            {
                return $"R{row / 2}";
            }
        }

        return "MENU";
    }

    private string? DecideOnServicePage(Job job, PmdgCduScreen screen)
    {
        if (Locate(screen.Lines, job.Item.Label) is not { } item)
        {
            Finish($"CDU: {job.Item.Label} not found on GROUND SERVICES{(job.Item.Page != 0 ? $" page {job.Item.Page}" : string.Empty)}.", ok: false);
            return null;
        }

        var state = Evaluate(screen, job.Item, item);
        if (job.Peek)
        {
            Finish(null, ok: state is not null);
            return null;
        }

        if (job.Pressed)
        {
            // After our key the aircraft has taken the request: the option moved, or the request line changed.
            if (state == job.On || (job.Item.OnOption is null && item.Value != job.ValueBefore))
            {
                Finish($"CDU: {Describe(job)} done ({Clean(job.ValueBefore)} → {Clean(item.Value)}).", ok: true);
                return null;
            }

            if (_sinceKey < KeyInterval * 4)
            {
                return null; // still waiting for the screen to update; Tick waits for the interval again
            }

            Finish($"CDU: {job.Item.Label} did not react ({Clean(item.Value)}).", ok: false);
            return null;
        }

        if (state == job.On)
        {
            Finish(null, ok: true); // already there
            return null;
        }

        if (state is null)
        {
            Finish($"CDU: cannot {Describe(job)} right now ({Clean(item.Value)}).", ok: false);
            return null;
        }

        job.Pressed = true;
        job.ValueBefore = item.Value;
        return item.Key;
    }

    /// <summary>
    /// True when the item is on (requested, or <see cref="PmdgServiceItem.OnOption"/> in the large font), false when off,
    /// null when the line shows neither (e.g. INHBT, or no font information).
    /// </summary>
    private static bool? Evaluate(PmdgCduScreen screen, PmdgServiceItem item, (string Key, string Value, int Row, int Start) found)
    {
        if (item.OnOption is { } onOption && item.OffOption is { } offOption)
        {
            if (screen.Flags is null)
            {
                return null;
            }

            var line = screen.Lines[found.Row];
            var on = line.IndexOf(onOption, found.Start, StringComparison.Ordinal);
            var off = line.IndexOf(offOption, found.Start, StringComparison.Ordinal);
            var onLarge = on >= 0 && !screen.IsSmall(found.Row, on);
            var offLarge = off >= 0 && !screen.IsSmall(found.Row, off);
            return onLarge == offLarge ? null : onLarge;
        }

        if (OnWords.Any(w => found.Value.Contains(w, StringComparison.Ordinal)))
        {
            return true;
        }

        return found.Value.Contains("REQUEST", StringComparison.Ordinal) ? false : null;
    }

    private void Finish(string? message, bool ok)
    {
        var item = _current!.Item;
        _current = null;
        if (message is not null)
        {
            Message?.Invoke(this, message);
        }

        Finished?.Invoke(item, ok);
    }

    /// <summary>
    /// Finds a label (small font, odd row) and returns the text of its data line below together with the line select key.
    /// Fields are separated by two or more spaces; left fields start in the left half of the screen.
    /// </summary>
    public static (string Key, string Value)? FindItem(IReadOnlyList<string> lines, string label) =>
        Locate(lines, label) is { } found ? (found.Key, found.Value) : null;

    /// <summary><see cref="FindItem"/> plus the row and column where the data field starts.</summary>
    private static (string Key, string Value, int Row, int Start)? Locate(IReadOnlyList<string> lines, string label)
    {
        for (var row = 1; row + 1 < lines.Count && row <= 11; row += 2)
        {
            foreach (var (start, text) in Fields(lines[row]))
            {
                if (text != label)
                {
                    continue;
                }

                // Side by the label's middle: right-aligned labels such as "WHEEL CHOCKS" start just left of centre.
                var left = start + text.Length / 2 < lines[row].Length / 2;
                var data = Fields(lines[row + 1]).Where(f => left ? f.Start < 12 : f.Start + f.Text.Length > 12).ToList();
                var field = data.Count == 0 ? (Start: 0, Text: string.Empty) : left ? data[0] : data[^1];
                return ($"{(left ? "L" : "R")}{(row + 1) / 2}", field.Text, row + 1, field.Start);
            }
        }

        return null;
    }

    private static IEnumerable<(int Start, string Text)> Fields(string line)
    {
        foreach (Match m in Regex.Matches(line, @"\S+(?: \S+)*"))
        {
            yield return (m.Index, m.Value);
        }
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim('<', '>', ' ');

    private static string Describe(Job job) => job.Item.OnOption is { } onOption
        ? $"{job.Item.Label} {(job.On ? onOption : job.Item.OffOption)}"
        : $"{(job.On ? "request" : "release")} {job.Item.Label}";

    private sealed class Job
    {
        public Job(PmdgServiceItem item, bool on, bool? isOn)
        {
            Item = item;
            On = on;
            IsOn = isOn;
        }

        public PmdgServiceItem Item { get; }
        public bool On { get; }
        public bool? IsOn { get; }

        /// <summary>Only open the page, press nothing.</summary>
        public bool Peek { get; init; }
        public int BlindStep { get; set; }
        public TimeSpan Elapsed { get; set; }
        public int Keys { get; set; }
        public bool Pressed { get; set; }
        public string? ValueBefore { get; set; }
    }
}
