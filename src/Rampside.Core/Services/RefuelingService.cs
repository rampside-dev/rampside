using Rampside.Core.Simulation;

namespace Rampside.Core.Services;

/// <summary>Fuel truck arrives, then fuel flows at the profile's rate until the target load is reached.</summary>
public sealed class RefuelingService : GroundService
{
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(1);

    private Dictionary<FuelTankId, double> _start = new();
    private Dictionary<FuelTankId, double> _target = new();
    private double _workKg;
    private double _transferredKg;
    private TimeSpan _phaseElapsed;
    private TimeSpan _sinceWrite;

    public RefuelingService()
        : base("refuel", "Refueling")
    {
    }

    public double TargetKg { get; private set; }

    public void Start(ServiceContext context, double targetKg)
    {
        var tanks = context.State.FuelTanks;
        TargetKg = Math.Clamp(targetKg, 0, context.State.FuelCapacityKg);
        _start = tanks.ToDictionary(t => t.Id, t => t.QuantityKg);
        _target = new Dictionary<FuelTankId, double>(FuelPlanner.Distribute(tanks, TargetKg));
        _workKg = _target.Sum(kv => Math.Abs(kv.Value - _start.GetValueOrDefault(kv.Key)));
        _transferredKg = 0;
        _phaseElapsed = TimeSpan.Zero;
        _sinceWrite = TimeSpan.Zero;
        Progress = 0;
        SetStatus(ServiceStatus.Starting, "Fuel truck on the way");
    }

    public override void Tick(ServiceContext context, TimeSpan elapsed)
    {
        switch (Status)
        {
            case ServiceStatus.Starting:
                _phaseElapsed += elapsed;
                if (_phaseElapsed.TotalSeconds >= context.Profile.Fuel.TruckArrivalSeconds)
                {
                    SetStatus(ServiceStatus.Active, TargetKg >= context.State.TotalFuelKg ? "Pumping fuel" : "Defueling");
                }

                break;

            case ServiceStatus.Active:
                var rateKgPerSecond = context.Profile.Fuel.RateKgPerMinute / 60.0;
                _transferredKg = Math.Min(_workKg, _transferredKg + rateKgPerSecond * elapsed.TotalSeconds);
                var fraction = _workKg <= 0 ? 1.0 : _transferredKg / _workKg;
                Progress = fraction;

                _sinceWrite += elapsed;
                if (_sinceWrite >= WriteInterval || fraction >= 1.0)
                {
                    context.Adapter.SetFuel(Interpolate(fraction));
                    _sinceWrite = TimeSpan.Zero;
                }

                if (fraction >= 1.0)
                {
                    Progress = null;
                    SetStatus(ServiceStatus.Completed, $"Fuel on board: {TargetKg:F0} kg");
                }

                break;
        }
    }

    public override void Abort(ServiceContext context)
    {
        Progress = null;
        base.Abort(context);
    }

    private Dictionary<FuelTankId, double> Interpolate(double fraction)
    {
        return _target.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var start = _start.GetValueOrDefault(kv.Key);
                return start + (kv.Value - start) * fraction;
            });
    }
}
