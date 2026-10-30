namespace Rampside.Core.Services;

/// <summary>Boards or deboards passengers at the profile's rate and keeps the payload stations in sync.</summary>
public sealed class PassengerService : GroundService
{
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(1);

    private bool _boarding;
    private double _startCount;
    private TimeSpan _phaseElapsed;
    private TimeSpan _sinceWrite;

    public PassengerService()
        : base("passengers", "Passengers")
    {
    }

    public double PassengersOnBoard { get; private set; }
    public int TargetPassengers { get; private set; }

    public void StartBoarding(ServiceContext context, int passengers)
    {
        _boarding = true;
        TargetPassengers = Math.Clamp(passengers, 0, context.Profile.Passengers.Capacity);
        Begin("Preparing cabin for boarding");
    }

    public void StartDeboarding(ServiceContext context)
    {
        _boarding = false;
        TargetPassengers = 0;
        Begin("Preparing for deboarding");
    }

    public override void Tick(ServiceContext context, TimeSpan elapsed)
    {
        switch (Status)
        {
            case ServiceStatus.Starting:
                _phaseElapsed += elapsed;
                if (_phaseElapsed.TotalSeconds >= context.Profile.Passengers.PreparationSeconds)
                {
                    SetStatus(ServiceStatus.Active, _boarding ? "Boarding passengers" : "Deboarding passengers");
                }

                break;

            case ServiceStatus.Active:
                var perMinute = _boarding
                    ? context.Profile.Passengers.BoardingPerMinute
                    : context.Profile.Passengers.DeboardingPerMinute;
                var step = perMinute / 60.0 * elapsed.TotalSeconds;
                PassengersOnBoard = _boarding
                    ? Math.Min(TargetPassengers, PassengersOnBoard + step)
                    : Math.Max(TargetPassengers, PassengersOnBoard - step);

                var total = Math.Abs(TargetPassengers - _startCount);
                var done = total <= 0 || Math.Abs(PassengersOnBoard - TargetPassengers) < 1e-9;
                Progress = total <= 0 ? 1.0 : Math.Abs(PassengersOnBoard - _startCount) / total;

                _sinceWrite += elapsed;
                if (_sinceWrite >= WriteInterval || done)
                {
                    WritePayload(context);
                    _sinceWrite = TimeSpan.Zero;
                }

                if (done)
                {
                    Progress = null;
                    SetStatus(
                        ServiceStatus.Completed,
                        _boarding ? $"Boarding complete: {TargetPassengers} passengers" : "Deboarding complete");
                }

                break;
        }
    }

    public override void Abort(ServiceContext context)
    {
        if (IsBusy)
        {
            WritePayload(context);
        }

        Progress = null;
        base.Abort(context);
    }

    private void Begin(string detail)
    {
        _startCount = PassengersOnBoard;
        _phaseElapsed = TimeSpan.Zero;
        _sinceWrite = TimeSpan.Zero;
        Progress = 0;
        SetStatus(ServiceStatus.Starting, detail);
    }

    private void WritePayload(ServiceContext context)
    {
        var passengers = (int)Math.Round(PassengersOnBoard);
        foreach (var station in PayloadCalculator.Compute(context.Profile, passengers))
        {
            context.Sim.SetPayloadStation(station.Key, station.Value);
        }
    }
}
