using Topo.Core;
using Xunit;

namespace Topo.Core.Tests;

public sealed class RewardAndMapTests
{
    [Fact]
    public void SameCoordinate_HasUnitReward()
    {
        var result = RewardCalculator.Calculate(new GeoCoordinate(35, -54), new GeoCoordinate(35, -54));
        Assert.Equal(0d, result.AngularDistanceDegrees);
        Assert.Equal(1d, result.Reward);
    }

    [Fact]
    public void GreaterDistance_HasLowerBoundedPositiveReward()
    {
        var near = RewardCalculator.Calculate(new GeoCoordinate(0, 0), new GeoCoordinate(1, 0));
        var far = RewardCalculator.Calculate(new GeoCoordinate(0, 0), new GeoCoordinate(0, 180));
        Assert.InRange(far.Reward, 0d, 1d);
        Assert.True(far.Reward > 0d);
        Assert.True(far.Reward < near.Reward);
    }

    [Theory]
    [InlineData(48.213, 22.645, 48, 23)]
    [InlineData(-89.6, -179.6, -90, -180)]
    [InlineData(90.9, 181.2, 90, 180)]
    public void MapCoordinates_RoundAndClampToTopoIntegers(double latitude, double longitude, int expectedLatitude, int expectedLongitude)
    {
        Assert.Equal(new GeoCoordinate(expectedLatitude, expectedLongitude), MapCoordinateConverter.ToTopoCoordinate(latitude, longitude));
    }
}
