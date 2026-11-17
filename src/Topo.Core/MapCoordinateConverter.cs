namespace Topo.Core;

public static class MapCoordinateConverter
{
    public static GeoCoordinate ToTopoCoordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Map coordinates must be finite.");
        }

        return new GeoCoordinate(
            (int)Math.Round(Math.Clamp(latitude, -90d, 90d), MidpointRounding.AwayFromZero),
            (int)Math.Round(Math.Clamp(longitude, -180d, 180d), MidpointRounding.AwayFromZero));
    }
}
