using FlyBrain.BrainClient;
using Topo.Core;

namespace Topo.Training;

public enum CoordinateDecodeErrorCode
{
    MissingLatitudeHemisphere,
    MissingLatitudeDigits,
    MissingSeparator,
    MissingLongitudeHemisphere,
    MissingLongitudeDigits,
    UnexpectedToken,
    MultipleSeparators,
    LatitudeOutOfRange,
    LongitudeOutOfRange,
}

public sealed record CoordinateSequenceDecodeResult(
    GeoCoordinate? Coordinate,
    CoordinateDecodeErrorCode? ErrorCode,
    string? ErrorMessage)
{
    public bool IsSuccess => Coordinate is not null;

    public static CoordinateSequenceDecodeResult Failure(CoordinateDecodeErrorCode code, string message) => new(null, code, message);
    public static CoordinateSequenceDecodeResult Success(GeoCoordinate coordinate) => new(coordinate, null, null);
}

/// <summary>Pure semantic-token grammar decoder. It delegates zero normalization to GeoCoordinate.</summary>
public static class CoordinateSequenceDecoder
{
    public static CoordinateSequenceDecodeResult Decode(IReadOnlyList<GeoCoordinateToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0 || tokens[0] is not (GeoCoordinateToken.North or GeoCoordinateToken.South))
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.MissingLatitudeHemisphere, "First token must be North or South.");
        }

        var separators = tokens.Select((token, index) => (token, index)).Where(pair => pair.token == GeoCoordinateToken.Separator).Select(pair => pair.index).ToArray();
        if (separators.Length == 0)
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.MissingSeparator, "Sequence requires one separator.");
        }

        if (separators.Length != 1)
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.MultipleSeparators, "Sequence must contain exactly one separator.");
        }

        var separator = separators[0];
        if (separator == 1)
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.MissingLatitudeDigits, "Latitude requires one or more decimal digits.");
        }

        if (separator + 1 >= tokens.Count || tokens[separator + 1] is not (GeoCoordinateToken.East or GeoCoordinateToken.West))
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.MissingLongitudeHemisphere, "Separator must be followed by East or West.");
        }

        if (separator + 2 >= tokens.Count)
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.MissingLongitudeDigits, "Longitude requires one or more decimal digits.");
        }

        if (!TryMagnitude(tokens.Skip(1).Take(separator - 1), out var latitude))
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.UnexpectedToken, "Latitude magnitude may contain decimal digit tokens only.");
        }

        if (!TryMagnitude(tokens.Skip(separator + 2), out var longitude))
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.UnexpectedToken, "Longitude magnitude may contain decimal digit tokens only.");
        }

        if (latitude > 90)
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.LatitudeOutOfRange, "Latitude magnitude must not exceed 90.");
        }

        if (longitude > 180)
        {
            return CoordinateSequenceDecodeResult.Failure(CoordinateDecodeErrorCode.LongitudeOutOfRange, "Longitude magnitude must not exceed 180.");
        }

        return CoordinateSequenceDecodeResult.Success(GeoCoordinate.FromHemisphereMagnitudes(
            tokens[0] == GeoCoordinateToken.North ? LatitudeHemisphere.North : LatitudeHemisphere.South,
            latitude,
            tokens[separator + 1] == GeoCoordinateToken.East ? LongitudeHemisphere.East : LongitudeHemisphere.West,
            longitude));
    }

    private static bool TryMagnitude(IEnumerable<GeoCoordinateToken> tokens, out int magnitude)
    {
        magnitude = 0;
        foreach (var token in tokens)
        {
            if (token is < GeoCoordinateToken.Digit0 or > GeoCoordinateToken.Digit9)
            {
                return false;
            }

            magnitude = checked(magnitude * 10 + (token - GeoCoordinateToken.Digit0));
        }

        return true;
    }
}

public static class TokenReadoutCompatibility
{
    public static void Validate(
        TokenReadoutModelArtifact model,
        TokenSoftmaxClassifier classifier,
        TokenReadoutPreprocessor preprocessor,
        ReadoutPopulationConfiguration population,
        BrainChannelConfiguration channels)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.ReadoutPopulationVersion != population.Version || model.ObservationOffsetSteps != population.ObservationOffsetSteps || !model.ReadoutRootIds.SequenceEqual(population.RootIds) || !model.FeatureFields.SequenceEqual(population.Features) || !string.Equals(population.ExecutionMode, channels.ExecutionMode, StringComparison.Ordinal) || !string.Equals(channels.ExecutionMode, "reference", StringComparison.Ordinal) || !string.Equals(channels.TransmitterMode, "unsigned", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Frozen token-readout model is incompatible with current readout/channel configuration.");
        }

        var expectedRawDimension = population.RootIds.Count * NeuralFeatureExtractor.FeatureFields.Count;
        if (model.OriginalFeatureDimension != expectedRawDimension || preprocessor.OriginalFeatureDimension != expectedRawDimension || model.RetainedFeatureIndices.Count != preprocessor.FeatureDimension || !model.RetainedFeatureIndices.SequenceEqual(preprocessor.RetainedFeatureIndices) || classifier.FeatureDimension != preprocessor.FeatureDimension || !classifier.ClassOrdering.SequenceEqual(Enum.GetValues<GeoCoordinateToken>()) || !model.ClassOrdering.SequenceEqual(classifier.ClassOrdering.Select(token => token.ToString())))
        {
            throw new InvalidOperationException("Frozen token-readout model has incompatible semantic ordering or feature layout.");
        }

        if (channels.StimulusAmplitude != 100d || channels.StimulusSteps != 1 || channels.SettleSteps != population.ObservationOffsetSteps)
        {
            throw new InvalidOperationException("Phase 7 requires the accepted 100/1/22 stimulation and observation timing.");
        }
    }
}

public sealed record CoordinateTokenPrediction(
    int SequenceIndex,
    GeoCoordinateToken PredictedToken,
    double ConfidenceScore,
    int SimulationStep,
    int FeatureCount,
    int ObservationOffsetSteps);

public enum CoordinateDecodeProgressStage { TokenStarted, NeuralObservationCaptured, TokenPredicted }

public sealed record CoordinateDecodeProgress(
    CoordinateDecodeProgressStage Stage,
    int SequenceIndex,
    GeoCoordinateToken ExpectedToken,
    GeoCoordinateToken? PredictedToken,
    double? ConfidenceScore,
    int SimulationStep,
    double MaximumMembraneDeltaMillivolts,
    int TotalSpikes,
    double ActiveFraction,
    string SafetyStatus);

public sealed record CoordinateNeuralDecode(
    GeoCoordinate SourceCoordinate,
    IReadOnlyList<GeoCoordinateToken> ExpectedTokens,
    IReadOnlyList<CoordinateTokenPrediction> Predictions,
    CoordinateSequenceDecodeResult Decoding)
{
    public IReadOnlyList<GeoCoordinateToken> PredictedTokens => Predictions.Select(prediction => prediction.PredictedToken).ToArray();
}

/// <summary>Serial session orchestration. Classifier input is only the extracted neural vector.</summary>
public sealed class CoordinateNeuralDecodePipeline(IBrainServiceClient client, LogicalReadoutSnapshotReader reader)
{
    public async Task<CoordinateNeuralDecode> DecodeAsync(
        string sessionId,
        GeoCoordinate source,
        BrainChannelConfiguration channels,
        ReadoutPopulationConfiguration population,
        TokenSoftmaxClassifier classifier,
        TokenReadoutPreprocessor preprocessor,
        IProgress<CoordinateDecodeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        TokenReadoutCompatibility.Validate(
            new TokenReadoutModelArtifact(1, "runtime", 0, 0, 0, 0, 0, classifier.ClassOrdering.Select(token => token.ToString()).ToArray(), preprocessor.OriginalFeatureDimension, preprocessor.RetainedFeatureIndices, preprocessor.Means, preprocessor.StandardDeviations, classifier.Weights, classifier.Biases, population.Version, population.ObservationOffsetSteps, population.RootIds, population.Features),
            classifier, preprocessor, population, channels);

        await client.ResetSessionAsync(sessionId, cancellationToken);
        var expected = source.ToTokens();
        var before = await reader.ReadAsync(sessionId, population.RootIds, cancellationToken);
        var predictions = new List<CoordinateTokenPrediction>(expected.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = expected[index];
            progress?.Report(new(CoordinateDecodeProgressStage.TokenStarted, index, token, null, null, before.SimulationStep, 0, 0, 0, "running"));
            await StepCompletedAsync(sessionId, new BrainStepRequest
            {
                Steps = channels.StimulusSteps,
                Stimulus = channels.GetChannel(token).Select(rootId => new BrainStimulus(rootId, channels.StimulusAmplitude)).ToArray(),
            }, cancellationToken);
            var settled = await StepCompletedAsync(sessionId, new BrainStepRequest { Steps = channels.SettleSteps, Stimulus = Array.Empty<BrainStimulus>() }, cancellationToken);
            var after = await reader.ReadAsync(sessionId, population.RootIds, cancellationToken);
            var vector = NeuralFeatureExtractor.Extract(before, after, population.RootIds, population.ObservationOffsetSteps);
            var metrics = ReadoutResponseAnalysis.Metrics(before, after);
            progress?.Report(new(CoordinateDecodeProgressStage.NeuralObservationCaptured, index, token, null, null, vector.SimulationStep, metrics.MaximumAbsoluteMembraneDeltaMillivolts, settled.TotalSpikes, settled.ActiveFraction, settled.SafetyStatus));
            var probabilities = classifier.PredictProbabilities(preprocessor.Transform(vector));
            var predicted = classifier.Predict(preprocessor.Transform(vector));
            var classIndex = Array.IndexOf(classifier.ClassOrdering.ToArray(), predicted);
            predictions.Add(new CoordinateTokenPrediction(index, predicted, probabilities[classIndex], vector.SimulationStep, vector.FeatureCount, vector.ObservationOffsetSteps));
            progress?.Report(new(CoordinateDecodeProgressStage.TokenPredicted, index, token, predicted, probabilities[classIndex], vector.SimulationStep, metrics.MaximumAbsoluteMembraneDeltaMillivolts, settled.TotalSpikes, settled.ActiveFraction, settled.SafetyStatus));
            before = after;
        }

        return new CoordinateNeuralDecode(source, expected, predictions, CoordinateSequenceDecoder.Decode(predictions.Select(prediction => prediction.PredictedToken).ToArray()));
    }

    private async Task<BrainStepResult> StepCompletedAsync(string sessionId, BrainStepRequest request, CancellationToken cancellationToken)
    {
        var response = await client.StepAsync(sessionId, request, cancellationToken);
        if (!string.Equals(response.SafetyStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Brain Service reported unsafe status '{response.SafetyStatus}'.");
        }

        return response;
    }
}

/// <summary>Convenience owner for a short-lived decode session; deletion is guaranteed after failures or cancellation.</summary>
public sealed class CoordinateNeuralDecodeSessionRunner(IBrainServiceClient client, CoordinateNeuralDecodePipeline pipeline)
{
    public async Task<CoordinateNeuralDecode> RunAsync(
        GeoCoordinate source,
        BrainChannelConfiguration channels,
        ReadoutPopulationConfiguration population,
        TokenSoftmaxClassifier classifier,
        TokenReadoutPreprocessor preprocessor,
        CancellationToken cancellationToken = default)
    {
        var session = await client.CreateSessionAsync(new CreateBrainSessionRequest
        {
            ExecutionMode = BrainExecutionMode.Reference,
            TransmitterMode = BrainTransmitterMode.Unsigned,
        }, cancellationToken);
        try
        {
            return await pipeline.DecodeAsync(session.SessionId, source, channels, population, classifier, preprocessor, cancellationToken: cancellationToken);
        }
        finally
        {
            await client.DeleteSessionAsync(session.SessionId, CancellationToken.None);
        }
    }
}

public static class CoordinateDecodingEvaluationCorpus
{
    public const int Seed = 20260923;

    public static IReadOnlyList<GeoCoordinate> CreateBlind(int count = 300)
    {
        if (count is < 20 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var phaseSixTrain = PhaseSixTrainingCoordinates();
        var required = new[] { "N0 E0", "S0 W0", "N90 E180", "S90 W180", "N10 W101", "S88 E177", "N22 E22", "S44 W44", "N66 E66", "S77 W77", "N88 E88", "S9 W99", "N11 W111" };
        var coordinates = required.Select(GeoCoordinate.Parse).Where(coordinate => !phaseSixTrain.Contains(coordinate.ToString())).ToList();
        var known = coordinates.Select(coordinate => coordinate.ToString()).ToHashSet(StringComparer.Ordinal);
        var random = new Random(Seed);
        while (coordinates.Count < count)
        {
            var coordinate = new GeoCoordinate(random.Next(-90, 91), random.Next(-180, 181));
            if (!phaseSixTrain.Contains(coordinate.ToString()) && known.Add(coordinate.ToString()))
            {
                coordinates.Add(coordinate);
            }
        }

        return coordinates;
    }

    public static IReadOnlySet<string> PhaseSixTrainingCoordinates()
    {
        var examples = TokenReadoutCoordinateCorpus.Create(200, 20260922)
            .Select(coordinate => new TokenReadoutExample(coordinate.ToString(), GeoCoordinateToken.North, new NeuralFeatureVector(0, 1, 1, 0, [0d])));
        return TokenReadoutDatasetSplitter.SplitByCoordinate(examples, 20260922).TrainingCoordinateIds.ToHashSet(StringComparer.Ordinal);
    }
}
