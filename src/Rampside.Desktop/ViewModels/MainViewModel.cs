using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Orchestration;
using Rampside.Core.Simulation;
using Rampside.Hosting;

namespace Rampside.Desktop.ViewModels;

public enum ConnectionKind
{
    Disconnected,
    FakeSim,
    Msfs,
}

/// <summary>
/// State of the main window. Host events arrive on background threads and are marshalled to the UI
/// thread; commands go to the orchestrator through <see cref="RampsideHost.ExecuteAsync"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogEntries = 500;

    private readonly RampsideHost _host;
    private readonly Dispatcher _dispatcher;
    private RampsideSnapshot? _pendingSnapshot;
    private bool _disposed;

    private ConnectionKind _connection;
    private string _connectionText = string.Empty;
    private string _aircraftTitle = string.Empty;
    private string _profileText = string.Empty;
    private string? _adapterProblem;
    private string _standText = Format.None;
    private string _jetwayText = Format.None;
    private string _onGroundText = Format.None;
    private string _parkingBrakeText = Format.None;
    private string _enginesText = Format.None;
    private string _groundSpeedText = Format.None;
    private string _headingText = Format.None;
    private string _fuelText = Format.None;
    private string _passengersText = Format.None;
    private bool _usingFakeSim;
    private bool _parkingBrakeSet;
    private bool _enginesRunning;
    private bool _dcPowerOn = true;
    private string? _statusMessage;
    private bool _statusIsError;
    private string _statusTime = string.Empty;

    public MainViewModel(RampsideHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;
        HasFakePmdg = host.Pmdg is FakePmdgLink;

        CloseAllDoorsCommand = new AsyncCommand(() => RunAsync(o => o.CloseAllDoors()));
        ToggleParkingBrakeCommand = new AsyncCommand(
            () => ChangeFakeSimAsync(s => s with { ParkingBrakeSet = !s.ParkingBrakeSet }, s => $"Parking brake {(s.ParkingBrakeSet ? "set" : "released")}."),
            () => _host.Fake is not null);
        ToggleEnginesCommand = new AsyncCommand(
            () => ChangeFakeSimAsync(s => s with { EnginesRunning = !s.EnginesRunning }, s => $"Engines {(s.EnginesRunning ? "running" : "off")}."),
            () => _host.Fake is not null);
        ToggleDcPowerCommand = new AsyncCommand(ToggleDcPowerAsync, () => HasFakePmdg);

        host.Log += OnHostLog;
        host.SnapshotUpdated += OnSnapshotUpdated;
        Apply(host.Snapshot);
    }

    public ObservableCollection<ServiceViewModel> Services { get; } = new();
    public ObservableCollection<DoorViewModel> Doors { get; } = new();
    public ObservableCollection<LogEntry> Log { get; } = new();

    public AsyncCommand CloseAllDoorsCommand { get; }
    public AsyncCommand ToggleParkingBrakeCommand { get; }
    public AsyncCommand ToggleEnginesCommand { get; }
    public AsyncCommand ToggleDcPowerCommand { get; }

    public ConnectionKind Connection
    {
        get => _connection;
        private set => Set(ref _connection, value);
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => Set(ref _connectionText, value);
    }

    public string AircraftTitle
    {
        get => _aircraftTitle;
        private set => Set(ref _aircraftTitle, value);
    }

    public string ProfileText
    {
        get => _profileText;
        private set => Set(ref _profileText, value);
    }

    public string? AdapterProblem
    {
        get => _adapterProblem;
        private set
        {
            if (Set(ref _adapterProblem, value))
            {
                OnPropertyChanged(nameof(HasAdapterProblem));
            }
        }
    }

    public bool HasAdapterProblem => !string.IsNullOrEmpty(AdapterProblem);

    public string StandText { get => _standText; private set => Set(ref _standText, value); }
    public string JetwayText { get => _jetwayText; private set => Set(ref _jetwayText, value); }
    public string OnGroundText { get => _onGroundText; private set => Set(ref _onGroundText, value); }
    public string ParkingBrakeText { get => _parkingBrakeText; private set => Set(ref _parkingBrakeText, value); }
    public string EnginesText { get => _enginesText; private set => Set(ref _enginesText, value); }
    public string GroundSpeedText { get => _groundSpeedText; private set => Set(ref _groundSpeedText, value); }
    public string HeadingText { get => _headingText; private set => Set(ref _headingText, value); }
    public string FuelText { get => _fuelText; private set => Set(ref _fuelText, value); }
    public string PassengersText { get => _passengersText; private set => Set(ref _passengersText, value); }

    public bool HasServices => Services.Count > 0;
    public bool HasDoors => Doors.Count > 0;

    public bool UsingFakeSim
    {
        get => _usingFakeSim;
        private set => Set(ref _usingFakeSim, value);
    }

    /// <summary>True in --fake-pmdg mode, where the fake PMDG's DC power can be switched.</summary>
    public bool HasFakePmdg { get; }

    public string ParkingBrakeButtonText => _parkingBrakeSet ? "Release parking brake" : "Set parking brake";
    public string EnginesButtonText => _enginesRunning ? "Stop engines" : "Start engines";
    public string DcPowerButtonText => _dcPowerOn ? "DC power off" : "DC power on";

    /// <summary>Result of the last command, shown under the header until the next one.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => StatusMessage is not null;

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    public string StatusTime
    {
        get => _statusTime;
        private set => Set(ref _statusTime, value);
    }

    public async Task StartAsync()
    {
        try
        {
            await _host.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowResult(CommandResult.Rejected($"Rampside could not start: {ex.Message}"));
        }
    }

    /// <summary>Runs an orchestrator command on the host loop and shows its result. Returns whether it was accepted.</summary>
    public async Task<bool> RunAsync(Func<GroundServiceOrchestrator, CommandResult> command)
    {
        CommandResult result;
        try
        {
            result = await _host.ExecuteAsync(command);
        }
        catch (Exception ex)
        {
            result = CommandResult.Rejected($"Command failed: {ex.Message}");
        }

        ShowResult(result);
        return result.Accepted;
    }

    public void ShowResult(CommandResult result)
    {
        var now = DateTime.Now;
        StatusMessage = result.Message;
        StatusIsError = !result.Accepted;
        StatusTime = now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        AddLog(new LogEntry(now, result.Message, result.Accepted ? LogKind.Accepted : LogKind.Rejected));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Log -= OnHostLog;
        _host.SnapshotUpdated -= OnSnapshotUpdated;
        await _host.DisposeAsync();
    }

    private void OnHostLog(object? sender, string line)
    {
        var trimmed = line.Trim();
        var entry = trimmed.StartsWith('!')
            ? new LogEntry(DateTime.Now, trimmed.TrimStart('!', ' '), LogKind.Warning)
            : new LogEntry(DateTime.Now, trimmed, LogKind.Info);
        _dispatcher.InvokeAsync(() => AddLog(entry));
    }

    private void OnSnapshotUpdated(object? sender, RampsideSnapshot snapshot)
    {
        // Latest wins: if the UI thread is behind, skip snapshots instead of queueing them.
        if (Interlocked.Exchange(ref _pendingSnapshot, snapshot) is null)
        {
            _dispatcher.InvokeAsync(() =>
            {
                if (Interlocked.Exchange(ref _pendingSnapshot, null) is { } latest && !_disposed)
                {
                    Apply(latest);
                }
            });
        }
    }

    private void AddLog(LogEntry entry)
    {
        Log.Add(entry);
        while (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(0);
        }
    }

    private void Apply(RampsideSnapshot snapshot)
    {
        UsingFakeSim = snapshot.UsingFakeSim;
        Connection = !snapshot.SimConnected ? ConnectionKind.Disconnected
            : snapshot.UsingFakeSim ? ConnectionKind.FakeSim
            : ConnectionKind.Msfs;
        ConnectionText = Connection switch
        {
            ConnectionKind.FakeSim => HasFakePmdg ? "Fake sim · fake PMDG 737" : "Fake sim",
            ConnectionKind.Msfs => "MSFS connected",
            _ => snapshot.UsingFakeSim ? "Fake sim not running" : "Waiting for MSFS…",
        };

        var aircraft = snapshot.Aircraft;
        var hasAircraft = snapshot.SimConnected && !string.IsNullOrEmpty(aircraft.Title);
        AircraftTitle = hasAircraft ? aircraft.Title : "No aircraft";
        ProfileText = snapshot.ProfileName is null
            ? "Profile and adapter are chosen once the user aircraft is known."
            : $"Profile {snapshot.ProfileName} · {snapshot.AdapterName} adapter";
        AdapterProblem = snapshot.AdapterProblem;

        StandText = hasAircraft && aircraft.Stand is { } stand ? $"{stand.Name} · {stand.AirportIcao}" : "unknown";
        JetwayText = hasAircraft ? DescribeJetway(aircraft.Stand) : Format.None;
        OnGroundText = hasAircraft ? (aircraft.OnGround ? "yes" : "no") : Format.None;
        ParkingBrakeText = hasAircraft ? (aircraft.ParkingBrakeSet ? "set" : "released") : Format.None;
        EnginesText = hasAircraft ? (aircraft.EnginesRunning ? "running" : "off") : Format.None;
        GroundSpeedText = hasAircraft ? $"{Format.Decimal(aircraft.GroundSpeedKnots)} kt" : Format.None;
        HeadingText = hasAircraft ? $"{Format.Whole(aircraft.HeadingTrueDeg)}° true" : Format.None;
        FuelText = hasAircraft && aircraft.FuelCapacityKg > 0
            ? $"{Format.Whole(aircraft.TotalFuelKg)} / {Format.Whole(aircraft.FuelCapacityKg)} kg"
            : Format.None;
        PassengersText = snapshot.PassengerCapacity > 0
            ? $"{Format.Whole(snapshot.PassengersOnBoard)} / {Format.Whole(snapshot.PassengerCapacity)}"
            : Format.None;

        _parkingBrakeSet = aircraft.ParkingBrakeSet;
        _enginesRunning = aircraft.EnginesRunning;
        OnPropertyChanged(nameof(ParkingBrakeButtonText));
        OnPropertyChanged(nameof(EnginesButtonText));

        SyncServices(snapshot);
        SyncDoors(snapshot);
        CommandManager.InvalidateRequerySuggested();
    }

    private static string DescribeJetway(StandInfo? stand)
    {
        if (stand is null)
        {
            return "unknown";
        }

        return stand.Jetway switch
        {
            null => "none at this stand",
            { Status: JetwayStatus.Attached, AttachedToUser: true } => "attached to us",
            { Status: JetwayStatus.Attached } => "attached to another aircraft",
            { Status: JetwayStatus.Moving } => "moving",
            _ => "at rest",
        };
    }

    // Rows are updated in place so bindings, focus and typed input survive the 5 Hz snapshots.
    private void SyncServices(RampsideSnapshot snapshot)
    {
        var services = snapshot.Services;
        if (!Services.Select(s => s.Id).SequenceEqual(services.Select(s => s.Id)))
        {
            Services.Clear();
            foreach (var service in services)
            {
                Services.Add(CreateService(service));
            }

            OnPropertyChanged(nameof(HasServices));
        }

        for (var i = 0; i < services.Count; i++)
        {
            Services[i].Update(services[i], snapshot);
        }
    }

    private ServiceViewModel CreateService(ServiceSnapshot service) => service.Id switch
    {
        "jetway" => new ConnectableServiceViewModel(this, service, o => o.ToggleJetway()),
        "stairs" => new ConnectableServiceViewModel(this, service, o => o.ToggleStairs()),
        "gpu" => new ConnectableServiceViewModel(this, service, o => o.ToggleGpu()),
        "refuel" => new RefuelingViewModel(this, service),
        "passengers" => new PassengersViewModel(this, service),
        "pushback" => new PushbackViewModel(this, service),
        _ => new ServiceViewModel(this, service),
    };

    private void SyncDoors(RampsideSnapshot snapshot)
    {
        var doors = snapshot.Doors;
        if (!Doors.Select(d => d.Door).SequenceEqual(doors.Select(d => d.Door)))
        {
            Doors.Clear();
            foreach (var door in doors)
            {
                Doors.Add(new DoorViewModel(this, door.Door, door.Name));
            }

            OnPropertyChanged(nameof(HasDoors));
        }

        for (var i = 0; i < doors.Count; i++)
        {
            Doors[i].State = doors[i].State;
        }
    }

    private async Task ChangeFakeSimAsync(Func<AircraftState, AircraftState> change, Func<AircraftState, string> describe)
    {
        var fake = _host.Fake;
        if (fake is null)
        {
            return;
        }

        try
        {
            var state = await _host.InvokeAsync(() =>
            {
                fake.Update(change);
                return fake.State;
            });
            AddLog(new LogEntry(DateTime.Now, $"(fake) {describe(state)}", LogKind.Info));
        }
        catch (ObjectDisposedException)
        {
            // Window is closing.
        }
    }

    private async Task ToggleDcPowerAsync()
    {
        if (_host.Pmdg is not FakePmdgLink pmdg)
        {
            return;
        }

        var on = !_dcPowerOn;
        try
        {
            await _host.InvokeAsync(() =>
            {
                pmdg.SetDcPower(on);
                return true;
            });
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _dcPowerOn = on;
        OnPropertyChanged(nameof(DcPowerButtonText));
        AddLog(new LogEntry(DateTime.Now, $"(fake) DC power {(on ? "on" : "off")}.", LogKind.Info));
    }
}
