using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

namespace Rampside.Core.Services;

/// <summary>
/// Common lifecycle of all services:
/// Idle -> Starting (vehicle arriving / connecting) -> Active -> Finishing -> Completed (or back to Idle).
/// </summary>
public enum ServiceStatus
{
    Idle,
    Starting,
    Active,
    Finishing,
    Completed,
    Aborted,
}

public sealed class ServiceContext
{
    public ServiceContext(ISimBridge sim, AircraftProfile profile)
    {
        Sim = sim;
        Profile = profile;
    }

    public ISimBridge Sim { get; }
    public AircraftProfile Profile { get; }
    public AircraftState State => Sim.State;
}

public abstract class GroundService
{
    protected GroundService(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public ServiceStatus Status { get; private set; } = ServiceStatus.Idle;
    public string Detail { get; private set; } = string.Empty;

    /// <summary>0..1 while a measurable task runs, otherwise null.</summary>
    public double? Progress { get; protected set; }

    public bool IsBusy => Status is ServiceStatus.Starting or ServiceStatus.Active or ServiceStatus.Finishing;

    /// <summary>Raised when <see cref="Status"/> or <see cref="Detail"/> changes (not on progress).</summary>
    public event EventHandler? Changed;

    public abstract void Tick(ServiceContext context, TimeSpan elapsed);

    public virtual void Abort(ServiceContext context)
    {
        if (IsBusy)
        {
            SetStatus(ServiceStatus.Aborted, "Aborted");
        }
    }

    protected void SetStatus(ServiceStatus status, string detail)
    {
        if (Status == status && Detail == detail)
        {
            return;
        }

        Status = status;
        Detail = detail;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
