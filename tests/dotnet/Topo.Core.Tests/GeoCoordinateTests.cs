using Topo.Core;
using Xunit;

namespace Topo.Core.Tests;

public sealed class GeoCoordinateTests
{
    [Theory]
    [InlineData("N35 W54", 35, -54)]
    [InlineData("N47 E22", 47, 22)]
    [InlineData("S23 E117", -23, 117)]
    [InlineData("N0 E0", 0, 0)]
    [InlineData("N90 E180", 90, 180)]
    [InlineData("S90 W180", -90, -180)]
    public void TryParse_AcceptsValidCanonicalCoordinates(string input, int latitude, int longitude)
    {
        var parsed = GeoCoordinate.TryParse(input, out var coordinate);

        Assert.True(parsed);
        Assert.Equal(latitude, coordinate.LatitudeDegrees);
        Assert.Equal(longitude, coordinate.LongitudeDegrees);
    }

    [Theory]
    [InlineData(" N35 W54")]
    [InlineData("N35   W54")]
    [InlineData("N35 W54 ")]
    [InlineData("n35 w54")]
    public void TryParse_AcceptsSensibleWhitespaceAndCase(string input)
    {
        var parsed = GeoCoordinate.TryParse(input, out var coordinate);

        Assert.True(parsed);
        Assert.Equal(new GeoCoordinate(35, -54), coordinate);
    }

    [Theory]
    [InlineData("N91 E0")]
    [InlineData("S91 W20")]
    public void TryParse_RejectsInvalidLatitude(string input) =>
        Assert.False(GeoCoordinate.TryParse(input, out _));

    [Theory]
    [InlineData("N0 E181")]
    [InlineData("N20 W181")]
    public void TryParse_RejectsInvalidLongitude(string input) =>
        Assert.False(GeoCoordinate.TryParse(input, out _));

    [Theory]
    [InlineData("X35 W54")]
    [InlineData("N35 Q54")]
    [InlineData("35N 54W")]
    [InlineData("N-35 W54")]
    [InlineData("")]
    public void TryParse_RejectsInvalidSyntax(string input) =>
        Assert.False(GeoCoordinate.TryParse(input, out _));

    [Theory]
    [InlineData(35, -54, "N35 W54")]
    [InlineData(-23, 117, "S23 E117")]
    public void ToString_UsesCanonicalInvariantFormatting(int latitude, int longitude, string expected)
    {
        Assert.Equal(expected, new GeoCoordinate(latitude, longitude).ToString());
    }

    [Fact]
    public void ZeroCoordinates_NormalizeToNorthAndEast()
    {
        var coordinate = GeoCoordinate.Parse("S0 W0");

        Assert.Equal(new GeoCoordinate(0, 0), coordinate);
        Assert.Equal("N0 E0", coordinate.ToString());
        Assert.Equal(LatitudeHemisphere.North, coordinate.LatitudeDirection);
        Assert.Equal(LongitudeHemisphere.East, coordinate.LongitudeDirection);
    }

    [Theory]
    [InlineData("N35 W54", new[] { GeoCoordinateToken.North, GeoCoordinateToken.Digit3, GeoCoordinateToken.Digit5, GeoCoordinateToken.Separator, GeoCoordinateToken.West, GeoCoordinateToken.Digit5, GeoCoordinateToken.Digit4 })]
    [InlineData("N5 W7", new[] { GeoCoordinateToken.North, GeoCoordinateToken.Digit5, GeoCoordinateToken.Separator, GeoCoordinateToken.West, GeoCoordinateToken.Digit7 })]
    [InlineData("S23 E117", new[] { GeoCoordinateToken.South, GeoCoordinateToken.Digit2, GeoCoordinateToken.Digit3, GeoCoordinateToken.Separator, GeoCoordinateToken.East, GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit7 })]
    public void ToTokens_PreservesCanonicalOrderWithoutZeroPadding(string input, GeoCoordinateToken[] expected)
    {
        Assert.Equal(expected, GeoCoordinate.Parse(input).ToTokens());
    }

    [Fact]
    public void EquivalentNumericCoordinates_CompareEqual()
    {
        Assert.Equal(GeoCoordinate.Parse("N0 E0"), GeoCoordinate.Parse("S0 W0"));
    }

    [Theory]
    [InlineData("N0 E0", "N0 E0", 0d)]
    [InlineData("N0 E0", "N0 E90", 90d)]
    [InlineData("N0 E0", "N90 E0", 90d)]
    [InlineData("N0 E0", "N0 E180", 180d)]
    [InlineData("N0 E180", "N0 W180", 0d)]
    [InlineData("N45 E0", "S45 E0", 90d)]
    public void AngularDistanceDegrees_UsesGreatCircleDistance(string first, string second, double expectedDegrees)
    {
        var actualDegrees = GeoCoordinate.AngularDistanceDegrees(
            GeoCoordinate.Parse(first),
            GeoCoordinate.Parse(second));

        Assert.InRange(actualDegrees, expectedDegrees - 0.000001d, expectedDegrees + 0.000001d);
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    public void Constructor_RejectsInvalidSignedCoordinates(int latitude, int longitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(latitude, longitude));
    }
}
