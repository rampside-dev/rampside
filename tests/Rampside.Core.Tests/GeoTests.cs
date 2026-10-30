namespace Rampside.Core.Tests;

public class GeoTests
{
    [Fact]
    public void Forward_on_north_heading_moves_north()
    {
        var (lat, lon) = Geo.Offset(50, 8, 0, Units.MetersPerDegreeLatitude, 0);

        Assert.Equal(51, lat, 6);
        Assert.Equal(8, lon, 6);
    }

    [Fact]
    public void Right_on_north_heading_moves_east()
    {
        var (lat, lon) = Geo.Offset(0, 8, 0, 0, Units.MetersPerDegreeLatitude);

        Assert.Equal(0, lat, 6);
        Assert.Equal(9, lon, 6);
    }

    [Fact]
    public void Right_on_east_heading_moves_south()
    {
        var (lat, lon) = Geo.Offset(0, 8, 90, 0, Units.MetersPerDegreeLatitude);

        Assert.Equal(-1, lat, 6);
        Assert.Equal(8, lon, 6);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(360, 0)]
    public void Headings_wrap_into_0_to_360(double input, double expected)
    {
        Assert.Equal(expected, Geo.NormalizeHeading(input), 6);
    }
}
