using System.IO;
using Rampside.Hosting;
using Rampside.Hosting.Updates;

namespace Rampside.Desktop.ViewModels;

/// <summary>
/// New versions: checked once at start (if enabled) or with "Check now". A newer one shows a banner with one
/// button, Update: it downloads and checks the MSI (progress fills the button), opens the installer and closes
/// Rampside; the installer starts Rampside again. Nothing is installed without that click.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    private static readonly string DownloadFolder = Path.Combine(Path.GetTempPath(), "Rampside", "updates");

    private readonly RampsideSettings _settings;
    private readonly Action<string> _log;
    private readonly Func<string, bool> _confirm;
    private readonly Func<bool> _servicesBusy;
    private readonly Action _close;
    private readonly bool _installedCopy = UpdateInstaller.IsInstalledCopy;

    private ReleaseInfo? _available;
    private string? _downloadText;
    private double _downloadProgress;
    private string? _checkResult;
    private bool _checkResultIsError;
    private string? _updateError;

    /// <param name="log">Adds a line to the window's log.</param>
    /// <param name="confirm">Asks a yes/no question.</param>
    /// <param name="servicesBusy">True while a ground service runs, which closing Rampside would stop.</param>
    /// <param name="close">Closes the window, which stops the host and exits.</param>
    public UpdateViewModel(RampsideSettings settings, Action<string> log, Func<string, bool> confirm, Func<bool> servicesBusy, Action close)
    {
        _settings = settings;
        _log = log;
        _confirm = confirm;
        _servicesBusy = servicesBusy;
        _close = close;
        CheckNowCommand = new AsyncCommand(() => CheckAsync(userAsked: true));
        InstallCommand = new AsyncCommand(InstallAsync, () => _available is not null);
    }

    public AsyncCommand CheckNowCommand { get; }
    public AsyncCommand InstallCommand { get; }

    public string CurrentVersionText => $"Rampside {ReleaseInfo.Format(ReleaseInfo.CurrentVersion)}";

    public bool ShowBanner => _available is not null;

    public string BannerText => _available is { } release ? $"Rampside {release.VersionText} is available." : string.Empty;

    /// <summary>"Update", or "Downloading…" from the click until Rampside closes.</summary>
    public string InstallButtonText => _downloadText ?? "Update";

    public string InstallHint => _installedCopy
        ? "Opens the installer and closes Rampside. Windows asks for permission; Rampside starts again afterwards and keeps its settings."
        : "This Rampside was not installed with the MSI: opens the full installer and closes Rampside. The installed Rampside starts afterwards.";

    private string? DownloadText
    {
        set
        {
            _downloadText = value;
            OnPropertyChanged(nameof(InstallButtonText));
        }
    }

    /// <summary>Download progress 0..1, drawn as the Update button's background.</summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => Set(ref _downloadProgress, value);
    }

    /// <summary>Check at start on/off; saved at once.</summary>
    public bool CheckAtStart
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (_settings.CheckForUpdates == value)
            {
                return;
            }

            _settings.CheckForUpdates = value;
            try
            {
                _settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowCheckResult($"Could not save {RampsideSettings.DefaultPath}: {ex.Message}", isError: true);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Outcome of the last check, shown on the Settings tab.</summary>
    public string? CheckResult
    {
        get => _checkResult;
        private set
        {
            if (Set(ref _checkResult, value))
            {
                OnPropertyChanged(nameof(HasCheckResult));
            }
        }
    }

    public bool HasCheckResult => CheckResult is not null;

    public bool CheckResultIsError
    {
        get => _checkResultIsError;
        private set => Set(ref _checkResultIsError, value);
    }

    /// <summary>Why the last update attempt failed, shown in the banner; pressing Update again retries.</summary>
    public string? UpdateError
    {
        get => _updateError;
        private set
        {
            if (Set(ref _updateError, value))
            {
                OnPropertyChanged(nameof(HasUpdateError));
            }
        }
    }

    public bool HasUpdateError => UpdateError is not null;

    /// <summary>The check at start: quiet unless a newer version is found or the check is switched off.</summary>
    public Task CheckOnStartAsync() => _settings.CheckForUpdates ? CheckAsync(userAsked: false) : Task.CompletedTask;

    private async Task CheckAsync(bool userAsked)
    {
        if (userAsked)
        {
            ShowCheckResult("Checking for updates…", isError: false);
        }

        ReleaseInfo latest;
        try
        {
            using var client = new UpdateClient();
            latest = await client.GetLatestAsync();
        }
        catch (UpdateException ex)
        {
            ShowCheckResult($"Could not check for updates. {ex.Message}", isError: true);
            if (!userAsked)
            {
                _log($"Update check: {ex.Message}");
            }

            return;
        }

        if (!latest.IsNewerThan(ReleaseInfo.CurrentVersion))
        {
            ShowCheckResult($"Rampside is up to date (latest release {latest.VersionText}).", isError: false);
            return;
        }

        var isNew = _available?.Version != latest.Version;
        _available = latest;
        ShowCheckResult($"Rampside {latest.VersionText} is available.", isError: false);
        if (isNew)
        {
            _log($"Rampside {latest.VersionText} is available (you have {ReleaseInfo.Format(ReleaseInfo.CurrentVersion)}).");
        }

        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(BannerText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task InstallAsync()
    {
        if (_available is not { } release)
        {
            return;
        }

        if (_servicesBusy() && !_confirm(
                $"A ground service is still running. Updating to Rampside {release.VersionText} closes Rampside and stops it.\n\nUpdate now?"))
        {
            return;
        }

        UpdateError = null;
        string msi;
        // Progress reports are queued on the UI thread; one arriving after the download must not overwrite the button.
        var downloading = false;
        try
        {
            using var client = new UpdateClient(new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) });
            downloading = true;
            DownloadText = "Downloading…";
            DownloadProgress = 0;
            msi = await client.DownloadAsync(release, DownloadFolder, new Progress<double>(p =>
            {
                if (downloading)
                {
                    DownloadProgress = p;
                }
            }));
            downloading = false;
            DownloadProgress = 1;
            UpdateInstaller.Start(msi, wizard: !_installedCopy);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            downloading = false;
            DownloadText = null;
            DownloadProgress = 0;
            UpdateError = ex is UpdateException ? ex.Message : $"The installer could not be started: {ex.Message}";
            _log($"Update: {UpdateError}");
            return;
        }

        _log($"Updating to Rampside {release.VersionText}: the installer opens and Rampside closes.");
        _close();
    }

    private void ShowCheckResult(string text, bool isError)
    {
        // Set the flag first, so the text never shows with the previous colour.
        CheckResultIsError = isError;
        CheckResult = text;
    }
}
