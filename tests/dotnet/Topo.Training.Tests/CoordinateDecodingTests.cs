using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.Training.Tests;

public sealed class CoordinateDecodingTests
{
    [Theory]
    [InlineData("N35 W54")]
    [InlineData("S23 E117")]
    [InlineData("N0 E0")]
    [InlineData("N90 W180")]
    public void Decode_ValidGrammar_UsesExistingCoordinateSemantics(string text)
    {
        var source = GeoCoordinate.Parse(text);

        var result = CoordinateSequenceDecoder.Decode(source.ToTokens());

        Assert.True(result.IsSuccess);
        Assert.Equal(source, result.Coordinate);
    }

    [Fact]
    public void Decode_ZeroHemisphereVariants_NormalizesThroughGeoCoordinate()
    {
        var result = CoordinateSequenceDecoder.Decode([
            GeoCoordinateToken.South, GeoCoordinateToken.Digit0, GeoCoordinateToken.Separator,
            GeoCoordinateToken.West, GeoCoordinateToken.Digit0]);

        Assert.True(result.IsSuccess);
        Assert.Equal(new GeoCoordinate(0, 0), result.Coordinate);
        Assert.Equal("N0 E0", result.Coordinate!.Value.ToString());
    }

    [Theory]
    [MemberData(nameof(InvalidSequences))]
    public void Decode_InvalidGrammar_ReturnsStructuredFailure(IReadOnlyList<GeoCoordinateToken> tokens, CoordinateDecodeErrorCode error)
    {
        var result = CoordinateSequenceDecoder.Decode(tokens);

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.ErrorCode);
        Assert.Null(result.Coordinate);
    }

    [Fact]
    public void Decode_OutOfRangeMagnitudes_AreNotClamped()
    {
        Assert.Equal(CoordinateDecodeErrorCode.LatitudeOutOfRange, CoordinateSequenceDecoder.Decode([
            GeoCoordinateToken.North, GeoCoordinateToken.Digit9, GeoCoordinateToken.Digit1,
            GeoCoordinateToken.Separator, GeoCoordinateToken.East, GeoCoordinateToken.Digit1]).ErrorCode);
        Assert.Equal(CoordinateDecodeErrorCode.LongitudeOutOfRange, CoordinateSequenceDecoder.Decode([
            GeoCoordinateToken.North, GeoCoordinateToken.Digit1,
            GeoCoordinateToken.Separator, GeoCoordinateToken.East, GeoCoordinateToken.Digit1, GeoCoordinateToken.Digit8, GeoCoordinateToken.Digit1]).ErrorCode);
    }

    [Fact]
    public void BlindCorpus_UsesDifferentSeedAndDoesNotOverlapReconstructedPhaseSixTrainingCoordinates()
    {
        var phaseSixTrain = CoordinateDecodingEvaluationCorpus.PhaseSixTrainingCoordinates();
        var blind = CoordinateDecodingEvaluationCorpus.CreateBlind(60);

        Assert.Equal(60, blind.Count);
        Assert.Empty(blind.Select(coordinate => coordinate.ToString()).Intersect(phaseSixTrain));
        Assert.Contains(new GeoCoordinate(90, 180), blind);
        Assert.Contains(new GeoCoordinate(-90, -180), blind);
    }

    public static IEnumerable<object[]> InvalidSequences()
    {
        yield return [new[] { GeoCoordinateToken.Digit3, GeoCoordinateToken.Digit5, GeoCoordinateToken.Separator, GeoCoordinateToken.West, GeoCoordinateToken.Digit5 }, CoordinateDecodeErrorCode.MissingLatitudeHemisphere];
        yield return [new[] { GeoCoordinateToken.North, GeoCoordinateToken.Separator, GeoCoordinateToken.West, GeoCoordinateToken.Digit5 }, CoordinateDecodeErrorCode.MissingLatitudeDigits];
        yield return [new[] { GeoCoordinateToken.North, GeoCoordinateToken.Digit3, GeoCoordinateToken.East, GeoCoordinateToken.Digit5 }, CoordinateDecodeErrorCode.MissingSeparator];
        yield return [new[] { GeoCoordinateToken.North, GeoCoordinateToken.Digit3, GeoCoordinateToken.Digit5, GeoCoordinateToken.Separator, GeoCoordinateToken.Separator, GeoCoordinateToken.West, GeoCoordinateToken.Digit4 }, CoordinateDecodeErrorCode.MultipleSeparators];
    }
}
