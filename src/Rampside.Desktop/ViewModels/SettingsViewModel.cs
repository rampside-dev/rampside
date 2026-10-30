using System.IO;
using Rampside.Core.FlightPlans;
using Rampside.Hosting;

namespace Rampside.Desktop.ViewModels;

/// <summary>Settings tab: the SimBrief user and the ground crew voice, saved to the settings file and used at once.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly RampsideHost _host;
    private readonly RampsideSettings _settings;
    private string _simBriefUser;
    private string? _simBriefResult;
    private bool _simBriefResultIsError;
    private string? _crewVoiceNote;

    public SettingsViewModel(RampsideHost host, RampsideSettings settings)
    {
        _host = host;
        _settings = settings;
        _simBriefUser = settings.SimBriefUser ?? string.Empty;
        SaveCommand = new AsyncCommand(SaveAsync, () => IsDirty);
        TestSimBriefCommand = new AsyncCommand(TestSimBriefAsync, () => RampsideSettings.Normalize(SimBriefUser) is not null);
        PlayCrewVoiceSampleCommand = new AsyncCommand(PlayCrewVoiceSampleAsync, () => CrewVoiceEnabled && HasCrewVoice);
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand TestSimBriefCommand { get; }
    public AsyncCommand PlayCrewVoiceSampleCommand { get; }

    /// <summary>False when no clips were found or the system cannot play them.</summary>
    public bool HasCrewVoice => _host.CrewVoice is not null;

    /// <summary>Ground crew voice on/off; saved at once.</summary>
    public bool CrewVoiceEnabled
    {
        get => _settings.CrewVoice;
        set
        {
            if (_settings.CrewVoice == value)
            {
                return;
            }

            _settings.CrewVoice = value;
            if (_host.CrewVoice is { } voice)
            {
                voice.Enabled = value;
            }

            try
            {
                _settings.Save();
                CrewVoiceNote = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrewVoiceNote = $"Could not save {SettingsPath}: {ex.Message}";
            }

            OnPropertyChanged();
        }
    }

    /// <summary>A save problem, shown under the switch.</summary>
    public string? CrewVoiceNote
    {
        get => _crewVoiceNote;
        private set
        {
            if (Set(ref _crewVoiceNote, value))
            {
                OnPropertyChanged(nameof(HasCrewVoiceNote));
            }
        }
    }

    public bool HasCrewVoiceNote => CrewVoiceNote is not null;

    public string CrewVoiceHint => HasCrewVoice
        ? "The pushback crew talks to you over the headset: tug connected, brakes, disconnect."
        : "No ground crew voice clips were found next to Rampside, so the crew stays silent.";

    public string SettingsPath => RampsideSettings.DefaultPath;

    /// <summary>SimBrief pilot ID or username as typed.</summary>
    public string SimBriefUser
    {
        get => _simBriefUser;
        set
        {
            if (Set(ref _simBriefUser, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                SimBriefResult = null;
            }
        }
    }

    public bool IsDirty => RampsideSettings.Normalize(SimBriefUser) != RampsideSettings.Normalize(_settings.SimBriefUser);

    /// <summary>Outcome of the last save or test, shown under the field.</summary>
    public string? SimBriefResult
    {
        get => _simBriefResult;
        private set
        {
            if (Set(ref _simBriefResult, value))
            {
                OnPropertyChanged(nameof(HasSimBriefResult));
            }
        }
    }

    public bool HasSimBriefResult => SimBriefResult is not null;

    public bool SimBriefResultIsError
    {
        get => _simBriefResultIsError;
        private set => Set(ref _simBriefResultIsError, value);
    }

    private Task SaveAsync()
    {
        var user = RampsideSettings.Normalize(SimBriefUser);
        var previous = _settings.SimBriefUser;
        _settings.SimBriefUser = user;
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.SimBriefUser = previous;
            ShowSimBriefResult($"Could not save {SettingsPath}: {ex.Message}", isError: true);
            return Task.CompletedTask;
        }

        _host.SimBriefUser = user;
        SimBriefUser = user ?? string.Empty;
        OnPropertyChanged(nameof(IsDirty));
        ShowSimBriefResult(user is null ? "SimBrief user removed." : $"Saved. Flight plans are loaded for {user}.", isError: false);
        return Task.CompletedTask;
    }

    /// <summary>Fetches the latest plan of the typed user without loading it, to check the ID.</summary>
    private async Task TestSimBriefAsync()
    {
        var user = RampsideSettings.Normalize(SimBriefUser);
        if (user is null)
        {
            return;
        }

        ShowSimBriefResult("Asking SimBrief…", isError: false);
        try
        {
            var plan = await _host.FetchSimBriefAsync(user);
            var generated = plan.GeneratedAt > DateTimeOffset.MinValue
                ? $", created {plan.GeneratedAt.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
                : string.Empty;
            ShowSimBriefResult($"Found: {FlightPlanFormat.Title(plan)}{generated}.", isError: false);
        }
        catch (SimBriefException ex)
        {
            ShowSimBriefResult(ex.Message, isError: true);
        }
    }

    private Task PlayCrewVoiceSampleAsync()
    {
        _host.CrewVoice?.PlaySample();
        return Task.CompletedTask;
    }

    private void ShowSimBriefResult(string text, bool isError)
    {
        // Set the flag first, so the text never shows with the previous colour.
        SimBriefResultIsError = isError;
        SimBriefResult = text;
    }
}
