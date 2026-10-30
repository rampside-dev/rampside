using Rampside.Core.Aircraft;
using Rampside.Core.Profiles;

namespace Rampside.Core.Services;

/// <summary>What the simulator reports about a piece of equipment, when it reports anything (jetways do).</summary>
public enum ObservedEquipment
{
    Away,
    Moving,
    Connected,
}

/// <summary>Status texts of a connectable, e.g. "Set" / "Removed" for wheel chocks.</summary>
public sealed record ConnectableWords(string Connecting, string Connected, string Disconnecting, string Disconnected, string DidNotConnect)
{
    public static ConnectableWords Default { get; } = new("Connecting", "Connected", "Disconnecting", "Disconnected", "Did not connect");
    public static ConnectableWords Chocks { get; } = new("Setting", "Set", "Removing", "Removed", "Not set");
}

/// <summary>
/// Equipment that is simply connected and disconnected: jetway, stairs, ground power, wheel chocks.
/// Without feedback from the sim it follows fixed connect/disconnect times; when the sim reports the real
/// state (<see cref="ApplyObserved"/>), that state wins, including changes made by someone else (GSX, the sim, the user).
/// </summary>
public sealed class ConnectableService : GroundService
{
    // After our own command the sim needs a moment before it reports the new movement.
    private static readonly TimeSpan CommandGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(3);

    private readonly ConnectableConfig _config;
    private readonly string _simEvent;
    private readonly ConnectableWords _words;
    private TimeSpan _elapsed;
    private TimeSpan? _sinceObservation;

    /// <param name="startsUnknown">Show "Unknown" until the aircraft reports the state (chocks may already be set).</param>
    public ConnectableService(
        string id, string displayName, GroundEquipment equipment, ConnectableConfig config, string defaultSimEvent,
        ConnectableWords? words = null, bool startsUnknown = false)
        : base(id, displayName)
    {
        Equipment = equipment;
        _config = config;
        _simEvent = config.Event ?? defaultSimEvent;
        _words = words ?? ConnectableWords.Default;
        SetStatus(ServiceStatus.Idle, startsUnknown ? UnknownDetail : _words.Disconnected);
    }

    public const string UnknownDetail = "Unknown";

    public GroundEquipment Equipment { get; }

    public bool IsConnected => Status == ServiceStatus.Active;

    public void Connect(ServiceContext context)
    {
        SendSimEvent(context);
        _elapsed = TimeSpan.Zero;
        SetStatus(ServiceStatus.Starting, _words.Connecting);
    }

    public void Disconnect(ServiceContext context)
    {
        SendSimEvent(context);
        _elapsed = TimeSpan.Zero;
        SetStatus(ServiceStatus.Finishing, _words.Disconnecting);
    }

    /// <summary>True while the sim reports this equipment's real state.</summary>
    public bool IsObserved => _sinceObservation < ObservationTimeout;

    /// <summary>Applies the state reported by the simulator.</summary>
    public void ApplyObserved(ObservedEquipment observed)
    {
        _sinceObservation = TimeSpan.Zero;
        var inGrace = _elapsed < CommandGrace;

        switch (observed)
        {
            case ObservedEquipment.Connected:
                if (Status == ServiceStatus.Finishing && inGrace)
                {
                    return; // our disconnect has not started moving yet
                }

                if (Status != ServiceStatus.Active)
                {
                    SetStatus(ServiceStatus.Active, _words.Connected);
                }

                break;

            case ObservedEquipment.Moving:
                if (Status == ServiceStatus.Idle || Status is ServiceStatus.Completed or ServiceStatus.Aborted)
                {
                    StartObservedMovement(ServiceStatus.Starting, _words.Connecting);
                }
                else if (Status == ServiceStatus.Active)
                {
                    StartObservedMovement(ServiceStatus.Finishing, _words.Disconnecting);
                }

                break;

            case ObservedEquipment.Away:
                if (Status == ServiceStatus.Starting && inGrace)
                {
                    return; // our connect has not started moving yet
                }

                if (Status != ServiceStatus.Idle)
                {
                    SetStatus(ServiceStatus.Idle, Status == ServiceStatus.Starting ? _words.DidNotConnect : _words.Disconnected);
                }
                else if (Detail == UnknownDetail)
                {
                    SetStatus(ServiceStatus.Idle, _words.Disconnected); // first report of an unknown start state
                }

                break;
        }
    }

    public override void Tick(ServiceContext context, TimeSpan elapsed)
    {
        if (_sinceObservation is not null)
        {
            _sinceObservation += elapsed;
        }

        if (Status is not (ServiceStatus.Starting or ServiceStatus.Finishing))
        {
            return;
        }

        _elapsed += elapsed;
        if (IsObserved)
        {
            return; // the sim's report decides, not the timer
        }

        if (Status == ServiceStatus.Starting && _elapsed.TotalSeconds >= _config.ConnectSeconds)
        {
            SetStatus(ServiceStatus.Active, _words.Connected);
        }
        else if (Status == ServiceStatus.Finishing && _elapsed.TotalSeconds >= _config.DisconnectSeconds)
        {
            SetStatus(ServiceStatus.Idle, _words.Disconnected);
        }
    }

    /// <summary>Shows why an idle service is not requested yet, e.g. "Waiting for the stairs to leave".</summary>
    public void SetWaiting(string? detail) => SetStatus(ServiceStatus.Idle, detail ?? _words.Disconnected);

    /// <summary>Connectables are toggled, not aborted.</summary>
    public override void Abort(ServiceContext context)
    {
    }

    private void StartObservedMovement(ServiceStatus status, string detail)
    {
        _elapsed = CommandGrace; // not our command, no grace needed
        SetStatus(status, detail);
    }

    private void SendSimEvent(ServiceContext context)
    {
        // Aircraft with their own equipment (PMDG) are asked by the orchestrator instead.
        if (!context.Adapter.HandlesEquipment(Equipment) && !string.IsNullOrWhiteSpace(_simEvent))
        {
            context.Sim.TriggerEvent(_simEvent);
        }
    }
}
