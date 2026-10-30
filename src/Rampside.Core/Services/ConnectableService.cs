using Rampside.Core.Profiles;

namespace Rampside.Core.Services;

/// <summary>Equipment that is simply connected and disconnected: jetway, stairs, ground power.</summary>
public sealed class ConnectableService : GroundService
{
    private readonly ConnectableConfig _config;
    private readonly string _simEvent;
    private TimeSpan _elapsed;

    public ConnectableService(string id, string displayName, ConnectableConfig config, string defaultSimEvent)
        : base(id, displayName)
    {
        _config = config;
        _simEvent = config.Event ?? defaultSimEvent;
        SetStatus(ServiceStatus.Idle, "Disconnected");
    }

    public bool IsConnected => Status == ServiceStatus.Active;

    public void Connect(ServiceContext context)
    {
        SendSimEvent(context);
        _elapsed = TimeSpan.Zero;
        SetStatus(ServiceStatus.Starting, "Connecting");
    }

    public void Disconnect(ServiceContext context)
    {
        SendSimEvent(context);
        _elapsed = TimeSpan.Zero;
        SetStatus(ServiceStatus.Finishing, "Disconnecting");
    }

    public override void Tick(ServiceContext context, TimeSpan elapsed)
    {
        if (Status is not (ServiceStatus.Starting or ServiceStatus.Finishing))
        {
            return;
        }

        _elapsed += elapsed;
        if (Status == ServiceStatus.Starting && _elapsed.TotalSeconds >= _config.ConnectSeconds)
        {
            SetStatus(ServiceStatus.Active, "Connected");
        }
        else if (Status == ServiceStatus.Finishing && _elapsed.TotalSeconds >= _config.DisconnectSeconds)
        {
            SetStatus(ServiceStatus.Idle, "Disconnected");
        }
    }

    /// <summary>Connectables are toggled, not aborted.</summary>
    public override void Abort(ServiceContext context)
    {
    }

    private void SendSimEvent(ServiceContext context)
    {
        if (!string.IsNullOrWhiteSpace(_simEvent))
        {
            context.Sim.TriggerEvent(_simEvent);
        }
    }
}
