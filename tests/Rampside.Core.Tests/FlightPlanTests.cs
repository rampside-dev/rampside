using System.Globalization;
using Rampside.Core.FlightPlans;
using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

public class FlightPlanTests
{
    private static readonly DateTimeOffset Generated = DateTimeOffset.FromUnixTimeSeconds(1_793_352_191);

    /// <summary>Made-up OFP with the same structure as SimBrief's xml.fetcher.php output.</summary>
    private static string Ofp(
        string units = "kgs",
        string type = "A320",
        string origin = "EDDM",
        double paxWeight = 80,
        double cargo = 2000,
        double freight = 500,
        double blockFuel = 5000,
        double oew = 42_000,
        string status = "Success") => string.Create(CultureInfo.InvariantCulture, $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <OFP>
          <fetch><userid>1</userid><status>{status}</status></fetch>
          <params><request_id>4711</request_id><time_generated>{Generated.ToUnixTimeSeconds()}</time_generated><units>{units}</units></params>
          <general><icao_airline>RSD</icao_airline><flight_number>123</flight_number></general>
          <origin><icao_code>{origin}</icao_code></origin>
          <destination><icao_code>LIRF</icao_code></destination>
          <aircraft><icaocode>{type}</icaocode><reg>D-ARSD</reg></aircraft>
          <fuel><plan_ramp>{blockFuel}</plan_ramp></fuel>
          <times><sched_out>1793354100</sched_out></times>
          <weights>
            <oew>{oew}</oew>
            <pax_count>100</pax_count><pax_count_actual>98</pax_count_actual>
            <bag_count>100</bag_count><bag_count_actual>95</bag_count_actual>
            <pax_weight>{paxWeight}</pax_weight><bag_weight>15</bag_weight>
            <freight_added>{freight}</freight_added><cargo>{cargo}</cargo>
            <est_zfw>{oew + 98 * paxWeight + cargo}</est_zfw>
          </weights>
        </OFP>
        """);

    private static FlightPlan Plan(string type = "A320", string origin = "EDDM", double blockFuel = 5000, double oew = 42_000) =>
        SimBriefOfpParser.Parse(Ofp(type: type, origin: origin, blockFuel: blockFuel, oew: oew));

    private static AircraftProfile Profile(params string[] types) => new()
    {
        IcaoTypes = types.ToList(),
        Passengers = new PassengerConfig { Capacity = 180 },
    };

    private static readonly AircraftState AtEddm = FakeSimBridge.CreateDefaultState() with { AirportIcao = "EDDM" };

    [Fact]
    public void Parser_reads_the_load_sheet_in_kg()
    {
        var plan = SimBriefOfpParser.Parse(Ofp());

        Assert.Equal("4711", plan.Id);
        Assert.Equal(Generated, plan.GeneratedAt);
        Assert.Equal("RSD123", plan.FlightNumber);
        Assert.Equal("EDDM-LIRF", plan.Route);
        Assert.Equal("A320", plan.AircraftIcao);
        Assert.Equal(98, plan.Passengers); // the "actual" load sheet values win
        Assert.Equal(95, plan.Bags);
        Assert.Equal(80, plan.PassengerMassKg, 3);
        Assert.Equal(2000, plan.CargoKg, 3);
        Assert.Equal(500, plan.FreightKg, 3);
        Assert.Equal(1500, plan.BagsKg, 3);
        Assert.Equal(5000, plan.BlockFuelKg, 3);
        Assert.Equal(plan.EmptyWeightKg + plan.PayloadKg, plan.ZeroFuelWeightKg, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_793_354_100), plan.ScheduledOffBlock);
    }

    [Fact]
    public void Parser_converts_pounds()
    {
        var plan = SimBriefOfpParser.Parse(Ofp(units: "lbs", paxWeight: 175, cargo: 4409.25, blockFuel: 11023.11));

        Assert.Equal(79.379, plan.PassengerMassKg, 3);
        Assert.Equal(2000, plan.CargoKg, 1);
        Assert.Equal(5000, plan.BlockFuelKg, 1);
    }

    [Fact]
    public void Parser_reports_SimBrief_errors()
    {
        var error = Assert.Throws<SimBriefException>(() => SimBriefOfpParser.Parse(Ofp(status: "Error: Unknown UserID")));
        Assert.Contains("Unknown UserID", error.Message);

        Assert.Throws<SimBriefException>(() => SimBriefOfpParser.Parse("<html>maintenance</html>"));
        Assert.Throws<SimBriefException>(() => SimBriefOfpParser.Parse("not xml"));
    }

    [Fact]
    public void Matching_plan_has_no_blockers()
    {
        var review = FlightPlanCheck.Review(Plan(), AtEddm, Profile("A320", "A20N"), Generated.AddHours(1));

        Assert.False(review.IsBlocked);
        Assert.Empty(review.Warnings);
        Assert.Empty(review.NotChecked);
    }

    [Fact]
    public void Other_aircraft_type_blocks_the_plan()
    {
        var review = FlightPlanCheck.Review(Plan(type: "B77W"), AtEddm, Profile("A320"), Generated);

        Assert.True(review.IsBlocked);
        Assert.Contains(review.Blockers, b => b.Contains("B77W") && b.Contains("A320"));
    }

    [Fact]
    public void Other_departure_airport_blocks_the_plan()
    {
        var review = FlightPlanCheck.Review(Plan(origin: "WSSS"), AtEddm, Profile("A320"), Generated);

        Assert.True(review.IsBlocked);
        Assert.Contains(review.Blockers, b => b.Contains("WSSS") && b.Contains("EDDM"));
    }

    [Fact]
    public void Stand_airport_is_used_before_the_nearest_airport()
    {
        var state = AtEddm with { AirportIcao = "EDMO", Stand = FakeSimBridge.DemoStand };

        var review = FlightPlanCheck.Review(Plan(), state, Profile("A320"), Generated);

        Assert.False(review.IsBlocked);
    }

    [Fact]
    public void Unknown_type_and_airport_are_reported_but_do_not_block()
    {
        var review = FlightPlanCheck.Review(Plan(), FakeSimBridge.CreateDefaultState(), Profile(), Generated);

        Assert.False(review.IsBlocked);
        Assert.Equal(2, review.NotChecked.Count);
    }

    [Fact]
    public void Type_comes_from_ATC_MODEL_without_profile_types()
    {
        var state = AtEddm with { AtcModel = "TT:ATCCOM.AC_MODEL_B738.0.text" };

        var review = FlightPlanCheck.Review(Plan(type: "A320"), state, Profile(), Generated);

        Assert.Contains(review.Blockers, b => b.Contains("B738"));
    }

    [Fact]
    public void Old_plan_too_much_fuel_and_other_empty_weight_give_warnings()
    {
        var state = AtEddm with { EmptyWeightKg = 43_000 };

        var review = FlightPlanCheck.Review(Plan(blockFuel: 25_000), state, Profile("A320"), Generated.AddHours(30));

        Assert.False(review.IsBlocked);
        Assert.Equal(3, review.Warnings.Count);
    }

    [Theory]
    [InlineData("B738", "B738")]
    [InlineData("b738", "B738")]
    [InlineData("TT:ATCCOM.AC_MODEL_A320.0.text", "A320")]
    [InlineData("TT:ATCCOM.AC_MODEL B77W.0.text", "B77W")]
    [InlineData("$$:C172", "C172")]
    [InlineData("Boeing 737-800", null)]
    [InlineData("", null)]
    public void ICAO_type_from_ATC_MODEL(string atcModel, string? expected)
    {
        Assert.Equal(expected, IcaoTypeDesignator.FromAtcModel(atcModel));
    }

    [Fact]
    public void Load_plan_keeps_freight_when_fewer_passengers_board()
    {
        var load = LoadPlan.FromFlightPlan(Plan(), 49); // half of the 98 planned

        Assert.Equal(80, load.PassengerMassKg, 3);
        Assert.Equal(500 + 750, load.CargoKg, 3);
        Assert.Equal(625, load.At(24.5).CargoKg, 3);
    }
}

public class FlightPlanOrchestratorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_793_352_191);

    private static FlightPlan Plan(string type = "A320", string origin = "EDDM") => new(
        Source: "SimBrief", Id: "1", GeneratedAt: Now, FlightNumber: "RSD123", Origin: origin, Destination: "LIRF",
        AircraftIcao: type, Registration: null, Passengers: 100, PassengerMassKg: 80, Bags: 100, CargoKg: 2000,
        FreightKg: 500, BlockFuelKg: 5000, EmptyWeightKg: 0, ZeroFuelWeightKg: 0, ScheduledOffBlock: null);

    private static TestRig Rig()
    {
        var profile = TestRig.FastProfile();
        profile.IcaoTypes.Add("A320");
        return new TestRig(profile, FakeSimBridge.CreateDefaultState() with { AirportIcao = "EDDM" });
    }

    [Fact]
    public void Mismatching_plan_is_refused_unless_forced()
    {
        var rig = Rig();

        var refused = rig.Orchestrator.LoadFlightPlan(Plan(type: "B77W", origin: "WSSS"), now: Now);
        Assert.False(refused.Accepted);
        Assert.Contains("B77W", refused.Message);
        Assert.Contains("WSSS", refused.Message);
        Assert.Null(rig.Orchestrator.FlightPlan);

        var forced = rig.Orchestrator.LoadFlightPlan(Plan(type: "B77W", origin: "WSSS"), force: true, now: Now);
        Assert.True(forced.Accepted);
        Assert.Contains("Forced despite", forced.Message);
        Assert.NotNull(rig.Orchestrator.FlightPlan);
    }

    [Fact]
    public void Boarding_uses_the_plan_masses_and_cargo()
    {
        var rig = Rig();
        Assert.True(rig.Orchestrator.LoadFlightPlan(Plan(), now: Now).Accepted);
        rig.Orchestrator.ToggleStairs();
        rig.Run(1.5);

        Assert.True(rig.Orchestrator.RequestBoarding().Accepted);
        rig.Run(110); // 100 pax at 60/min

        Assert.Equal(ServiceStatus.Completed, rig.Orchestrator.Passengers.Status);
        var stations = rig.Sim.PayloadStations;
        Assert.Equal(50 * 80, stations[1], 3);
        Assert.Equal(50 * 80, stations[2], 3);
        Assert.Equal(2000, stations[3], 3);
    }

    [Fact]
    public void Deboarding_unloads_the_plan_cargo()
    {
        var rig = Rig();
        rig.Orchestrator.LoadFlightPlan(Plan(), now: Now);
        rig.Orchestrator.ToggleStairs();
        rig.Run(1.5);
        rig.Orchestrator.RequestBoarding();
        rig.Run(110);

        Assert.True(rig.Orchestrator.RequestDeboarding().Accepted);
        rig.Run(60);

        Assert.All(rig.Sim.PayloadStations.Values, w => Assert.Equal(0, w, 3));
    }

    [Fact]
    public void Refuel_without_amount_uses_block_fuel()
    {
        var rig = Rig();
        Assert.False(rig.Orchestrator.RequestRefuel().Accepted); // no plan yet

        rig.Orchestrator.LoadFlightPlan(Plan(), now: Now);
        Assert.True(rig.Orchestrator.RequestRefuel().Accepted);

        Assert.Equal(5000, rig.Orchestrator.Refueling.TargetKg, 3);
    }

    [Fact]
    public void Plan_cannot_change_while_boarding_or_with_its_passengers_on_board()
    {
        var rig = Rig();
        rig.Orchestrator.LoadFlightPlan(Plan(), now: Now);
        rig.Orchestrator.ToggleStairs();
        rig.Run(1.5);
        rig.Orchestrator.RequestBoarding();
        rig.Run(5);

        Assert.False(rig.Orchestrator.LoadFlightPlan(Plan() with { Id = "2" }, now: Now).Accepted);

        rig.Run(110);
        Assert.False(rig.Orchestrator.LoadFlightPlan(Plan() with { Id = "2" }, now: Now).Accepted);
        Assert.True(rig.Orchestrator.LoadFlightPlan(Plan() with { Id = "2" }, force: true, now: Now).Accepted);
    }
}
