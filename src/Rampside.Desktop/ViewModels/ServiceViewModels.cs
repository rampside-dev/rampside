using Rampside.Core.Orchestration;
using Rampside.Core.Services;
using Rampside.Hosting;

namespace Rampside.Desktop.ViewModels;

/// <summary>One ground service row. Services without their own actions use this class directly.</summary>
public class ServiceViewModel : ObservableObject
{
    private ServiceStatus _status;
    private string _detail = string.Empty;
    private double? _progress;
    private bool _isBusy;

    public ServiceViewModel(MainViewModel owner, ServiceSnapshot service)
    {
        Owner = owner;
        Id = service.Id;
        DisplayName = service.DisplayName;
    }

    public string Id { get; }
    public string DisplayName { get; }

    public ServiceStatus Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public double? Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                OnPropertyChanged(nameof(HasProgress));
                OnPropertyChanged(nameof(ProgressValue));
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public bool HasProgress => Progress is not null;
    public double ProgressValue => Progress ?? 0;
    public string ProgressText => Progress is { } p ? $"{p * 100:F0} %" : string.Empty;

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    protected MainViewModel Owner { get; }

    public virtual void Update(ServiceSnapshot service, RampsideSnapshot snapshot)
    {
        Status = service.Status;
        Detail = service.Detail;
        Progress = service.Progress;
        IsBusy = service.IsBusy;
    }

    protected AsyncCommand Command(Func<GroundServiceOrchestrator, CommandResult> command, Func<bool>? canExecute = null) =>
        new(() => Owner.RunAsync(command), canExecute);
}

/// <summary>Jetway, stairs and ground power: one connect/disconnect button.</summary>
public sealed class ConnectableServiceViewModel : ServiceViewModel
{
    public ConnectableServiceViewModel(MainViewModel owner, ServiceSnapshot service, Func<GroundServiceOrchestrator, CommandResult> toggle)
        : base(owner, service)
    {
        // While connecting or disconnecting the orchestrator rejects a toggle anyway.
        ToggleCommand = Command(toggle, () => Status is not (ServiceStatus.Starting or ServiceStatus.Finishing));
    }

    public AsyncCommand ToggleCommand { get; }

    public string ToggleText => Status switch
    {
        ServiceStatus.Active => "Disconnect",
        ServiceStatus.Starting => "Connecting…",
        ServiceStatus.Finishing => "Disconnecting…",
        _ => "Connect",
    };

    public override void Update(ServiceSnapshot service, RampsideSnapshot snapshot)
    {
        base.Update(service, snapshot);
        OnPropertyChanged(nameof(ToggleText));
    }
}

public sealed class RefuelingViewModel : ServiceViewModel
{
    private string _targetKgText = string.Empty;
    private string _hint = "Target kg";

    public RefuelingViewModel(MainViewModel owner, ServiceSnapshot service)
        : base(owner, service)
    {
        RefuelCommand = new AsyncCommand(RefuelAsync, () => !IsBusy);
        AbortCommand = Command(o => o.Abort("refuel"), () => IsBusy);
    }

    public AsyncCommand RefuelCommand { get; }
    public AsyncCommand AbortCommand { get; }

    /// <summary>Total fuel load to refuel (or defuel) to, in kg.</summary>
    public string TargetKgText
    {
        get => _targetKgText;
        set => Set(ref _targetKgText, value);
    }

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    public override void Update(ServiceSnapshot service, RampsideSnapshot snapshot)
    {
        base.Update(service, snapshot);
        var capacity = snapshot.Aircraft.FuelCapacityKg;
        Hint = capacity > 0 ? $"Target kg (max {Format.Whole(capacity)})" : "Target kg";
    }

    private Task RefuelAsync()
    {
        if (!Format.TryParseWhole(TargetKgText, out var kg))
        {
            Owner.ShowResult(CommandResult.Rejected("Enter the total fuel load in whole kg, e.g. 8500."));
            return Task.CompletedTask;
        }

        return Owner.RunAsync(o => o.RequestRefuel(kg));
    }
}

public sealed class PassengersViewModel : ServiceViewModel
{
    private string _passengersText = string.Empty;
    private string _hint = "All";
    private bool _hasPassengersOnBoard;

    public PassengersViewModel(MainViewModel owner, ServiceSnapshot service)
        : base(owner, service)
    {
        BoardCommand = new AsyncCommand(BoardAsync, () => !IsBusy);
        DeboardCommand = Command(o => o.RequestDeboarding(), () => !IsBusy && _hasPassengersOnBoard);
        AbortCommand = Command(o => o.Abort("passengers"), () => IsBusy);
    }

    public AsyncCommand BoardCommand { get; }
    public AsyncCommand DeboardCommand { get; }
    public AsyncCommand AbortCommand { get; }

    /// <summary>Passengers to board; empty boards a full cabin.</summary>
    public string PassengersText
    {
        get => _passengersText;
        set => Set(ref _passengersText, value);
    }

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    public override void Update(ServiceSnapshot service, RampsideSnapshot snapshot)
    {
        base.Update(service, snapshot);
        _hasPassengersOnBoard = snapshot.PassengersOnBoard > 0;
        Hint = snapshot.PassengerCapacity > 0 ? $"Pax (all {snapshot.PassengerCapacity})" : "Pax (all)";
    }

    private Task BoardAsync()
    {
        if (string.IsNullOrWhiteSpace(PassengersText))
        {
            return Owner.RunAsync(o => o.RequestBoarding());
        }

        if (!Format.TryParseWhole(PassengersText, out var passengers))
        {
            Owner.ShowResult(CommandResult.Rejected("Enter a whole number of passengers, or leave the field empty for a full cabin."));
            return Task.CompletedTask;
        }

        return Owner.RunAsync(o => o.RequestBoarding(passengers));
    }
}

public sealed class PushbackViewModel : ServiceViewModel
{
    private string _motionText = string.Empty;
    private PushbackSteering? _requestedSteering;

    public PushbackViewModel(MainViewModel owner, ServiceSnapshot service)
        : base(owner, service)
    {
        CallCommand = Command(o => o.RequestPushback(), () => !IsBusy);
        TailLeftCommand = SteerCommand(PushbackSteering.TailLeft);
        StraightCommand = SteerCommand(PushbackSteering.Straight);
        TailRightCommand = SteerCommand(PushbackSteering.TailRight);
        StopCommand = Command(o => o.StopPushback(), () => IsBusy);
        AbortCommand = Command(o => o.Abort("pushback"), () => IsBusy);
    }

    public AsyncCommand CallCommand { get; }
    public AsyncCommand TailLeftCommand { get; }
    public AsyncCommand StraightCommand { get; }
    public AsyncCommand TailRightCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand AbortCommand { get; }

    /// <summary>Requested steering, tug speed and nose wheel angle while the pushback runs.</summary>
    public string MotionText
    {
        get => _motionText;
        private set => Set(ref _motionText, value);
    }

    public override void Update(ServiceSnapshot service, RampsideSnapshot snapshot)
    {
        base.Update(service, snapshot);
        if (!IsBusy)
        {
            _requestedSteering = null;
            MotionText = string.Empty;
            return;
        }

        var speed = snapshot.PushbackSpeedMps;
        var motion = Math.Abs(speed) < 0.05
            ? "tug stopped"
            : $"tug {Format.Decimal(Math.Abs(speed))} m/s {(speed < 0 ? "backwards" : "forwards")}";
        var angle = snapshot.PushbackSteeringDeg;
        var wheel = Math.Abs(angle) < 0.5
            ? "nose wheel centred"
            : $"nose wheel {Format.Whole(Math.Abs(angle))}° {(angle > 0 ? "right" : "left")}";
        var steering = _requestedSteering switch
        {
            PushbackSteering.TailLeft => "Tail left",
            PushbackSteering.TailRight => "Tail right",
            _ => "Straight",
        };
        MotionText = $"{steering} · {motion} · {wheel}";
    }

    private AsyncCommand SteerCommand(PushbackSteering steering) => new(async () =>
    {
        if (await Owner.RunAsync(o => o.SteerPushback(steering)))
        {
            _requestedSteering = steering;
        }
    }, () => IsBusy);
}
