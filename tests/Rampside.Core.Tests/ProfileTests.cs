using Rampside.Core.Profiles;

namespace Rampside.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void Shipped_profiles_load_without_errors()
    {
        var errors = new List<string>();
        var repository = ProfileRepository.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"), errors.Add);

        Assert.Empty(errors);
        Assert.Contains(repository.Profiles, p => p.Id == "generic");
    }

    [Fact]
    public void Resolve_matches_title_case_insensitively_and_falls_back_to_generic()
    {
        var repository = ProfileRepository.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"));

        Assert.Equal("example-narrowbody", repository.Resolve("RAMPSIDE TEST AIRCRAFT livery 2").Id);
        Assert.Equal("generic", repository.Resolve("Some Unknown Plane").Id);
    }

    [Fact]
    public void Missing_values_keep_their_defaults()
    {
        var profile = System.Text.Json.JsonSerializer.Deserialize<AircraftProfile>(
            """{ "id": "x", "pushback": { "speedMps": 2 } }""", ProfileRepository.JsonOptions)!;

        Assert.Equal(2, profile.Pushback.SpeedMps);
        Assert.Equal(new PushbackConfig().WheelbaseMeters, profile.Pushback.WheelbaseMeters);
        Assert.Equal(new PassengerConfig().Capacity, profile.Passengers.Capacity);
    }
}
