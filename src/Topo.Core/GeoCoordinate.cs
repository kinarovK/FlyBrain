using System.Globalization;
using System.Text.RegularExpressions;

namespace Topo.Core;

public readonly record struct GeoCoordinate
{
    private static readonly Regex CanonicalPattern = new(
        @"^\s*([NS])([0-9]+)\s+([EW])([0-9]+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public GeoCoordinate(int latitudeDegrees, int longitudeDegrees)
    {
        if (latitudeDegrees is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitudeDegrees),
                latitudeDegrees,
                "Latitude must be between -90 and 90 degrees.");
        }

        if (longitudeDegrees is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longitudeDegrees),
                longitudeDegrees,
                "Longitude must be between -180 and 180 degrees.");
        }

        LatitudeDegrees = latitudeDegrees;
        LongitudeDegrees = longitudeDegrees;
    }

    public int LatitudeDegrees { get; }

    public int LongitudeDegrees { get; }

    public int LatitudeMagnitude => Math.Abs(LatitudeDegrees);

    public int LongitudeMagnitude => Math.Abs(LongitudeDegrees);

    public LatitudeHemisphere LatitudeDirection =>
        LatitudeDegrees < 0 ? LatitudeHemisphere.South : LatitudeHemisphere.North;

    public LongitudeHemisphere LongitudeDirection =>
        LongitudeDegrees < 0 ? LongitudeHemisphere.West : LongitudeHemisphere.East;

    public static GeoCoordinate FromHemisphereMagnitudes(
        LatitudeHemisphere latitudeHemisphere,
        int latitudeMagnitude,
        LongitudeHemisphere longitudeHemisphere,
        int longitudeMagnitude)
    {
        if (!Enum.IsDefined(latitudeHemisphere))
        {
            throw new ArgumentOutOfRangeException(nameof(latitudeHemisphere));
        }

        if (!Enum.IsDefined(longitudeHemisphere))
        {
            throw new ArgumentOutOfRangeException(nameof(longitudeHemisphere));
        }

        if (latitudeMagnitude is < 0 or > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitudeMagnitude),
                latitudeMagnitude,
                "Latitude magnitude must be between 0 and 90 degrees.");
        }

        if (longitudeMagnitude is < 0 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longitudeMagnitude),
                longitudeMagnitude,
                "Longitude magnitude must be between 0 and 180 degrees.");
        }

        var latitude = latitudeHemisphere == LatitudeHemisphere.South
            ? -latitudeMagnitude
            : latitudeMagnitude;
        var longitude = longitudeHemisphere == LongitudeHemisphere.West
            ? -longitudeMagnitude
            : longitudeMagnitude;

        return new GeoCoordinate(latitude, longitude);
    }

    public static GeoCoordinate Parse(string text)
    {
        if (TryParse(text, out var coordinate))
        {
            return coordinate;
        }

        throw new FormatException(
            "Coordinate must use canonical latitude/longitude format such as 'N35 W54'.");
    }

    public static bool TryParse(string? text, out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = CanonicalPattern.Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var latitudeMagnitude) ||
            !int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var longitudeMagnitude) ||
            latitudeMagnitude > 90 ||
            longitudeMagnitude > 180)
        {
            return false;
        }

        var latitudeHemisphere = char.ToUpperInvariant(match.Groups[1].Value[0]) == 'N'
            ? LatitudeHemisphere.North
            : LatitudeHemisphere.South;
        var longitudeHemisphere = char.ToUpperInvariant(match.Groups[3].Value[0]) == 'E'
            ? LongitudeHemisphere.East
            : LongitudeHemisphere.West;

        coordinate = FromHemisphereMagnitudes(
            latitudeHemisphere,
            latitudeMagnitude,
            longitudeHemisphere,
            longitudeMagnitude);
        return true;
    }

    public IReadOnlyList<GeoCoordinateToken> ToTokens()
    {
        var tokens = new List<GeoCoordinateToken>();
        tokens.Add(LatitudeDirection == LatitudeHemisphere.North
            ? GeoCoordinateToken.North
            : GeoCoordinateToken.South);
        AppendDigits(tokens, LatitudeMagnitude);
        tokens.Add(GeoCoordinateToken.Separator);
        tokens.Add(LongitudeDirection == LongitudeHemisphere.East
            ? GeoCoordinateToken.East
            : GeoCoordinateToken.West);
        AppendDigits(tokens, LongitudeMagnitude);
        return tokens;
    }

    public double AngularDistanceDegreesTo(GeoCoordinate other) =>
        AngularDistanceDegrees(this, other);

    public static double AngularDistanceDegrees(GeoCoordinate first, GeoCoordinate second)
    {
        var latitudeOneRadians = DegreesToRadians(first.LatitudeDegrees);
        var latitudeTwoRadians = DegreesToRadians(second.LatitudeDegrees);
        var deltaLatitudeRadians = DegreesToRadians(second.LatitudeDegrees - first.LatitudeDegrees);
        var deltaLongitudeDegrees = WrapLongitudeDelta(second.LongitudeDegrees - first.LongitudeDegrees);
        var deltaLongitudeRadians = DegreesToRadians(deltaLongitudeDegrees);

        var haversine = Math.Pow(Math.Sin(deltaLatitudeRadians / 2), 2) +
            (Math.Cos(latitudeOneRadians) * Math.Cos(latitudeTwoRadians) *
             Math.Pow(Math.Sin(deltaLongitudeRadians / 2), 2));
        var clampedHaversine = Math.Clamp(haversine, 0d, 1d);
        var angularRadians = 2 * Math.Atan2(
            Math.Sqrt(clampedHaversine),
            Math.Sqrt(1 - clampedHaversine));

        return RadiansToDegrees(angularRadians);
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{(LatitudeDirection == LatitudeHemisphere.North ? 'N' : 'S')}{LatitudeMagnitude} {(LongitudeDirection == LongitudeHemisphere.East ? 'E' : 'W')}{LongitudeMagnitude}");

    private static void AppendDigits(List<GeoCoordinateToken> tokens, int magnitude)
    {
        foreach (var digit in magnitude.ToString(CultureInfo.InvariantCulture))
        {
            tokens.Add(digit switch
            {
                '0' => GeoCoordinateToken.Digit0,
                '1' => GeoCoordinateToken.Digit1,
                '2' => GeoCoordinateToken.Digit2,
                '3' => GeoCoordinateToken.Digit3,
                '4' => GeoCoordinateToken.Digit4,
                '5' => GeoCoordinateToken.Digit5,
                '6' => GeoCoordinateToken.Digit6,
                '7' => GeoCoordinateToken.Digit7,
                '8' => GeoCoordinateToken.Digit8,
                '9' => GeoCoordinateToken.Digit9,
                _ => throw new InvalidOperationException("Invariant numeric formatting produced a non-digit."),
            });
        }
    }

    private static int WrapLongitudeDelta(int deltaDegrees) =>
        ((deltaDegrees + 180) % 360 + 360) % 360 - 180;

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180d);

    private static double RadiansToDegrees(double radians) => radians * (180d / Math.PI);
}
