using System.Text.Json;
using System.Text.Json.Serialization;
using FlyBrain.BrainClient;
using Topo.Core;

namespace Topo.Training;

public sealed record LogicalReadoutSnapshot(
    string SessionId,
    int SimulationStep,
    double SimulatedMilliseconds,
    IReadOnlyList<BrainReadoutNeuron> Neurons);

public sealed class LogicalReadoutSnapshotReader(IBrainServiceClient brainServiceClient)
{
    public async Task<LogicalReadoutSnapshot> ReadAsync(
        string sessionId,
        IReadOnlyList<long> rootIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(rootIds);
        if (rootIds.Count is < 1 or > 256 * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(rootIds), "Logical discovery snapshots require one to 1,024 roots.");
        }

        var roots = rootIds.ToArray();
        if (roots.Distinct().Count() != roots.Length)
        {
            throw new InvalidOperationException("Logical readout roots must be unique.");
        }

        var neurons = new List<BrainReadoutNeuron>(roots.Length);
        BrainReadoutSnapshot? firstBatch = null;
        for (var offset = 0; offset < roots.Length; offset += 256)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchRoots = roots.Skip(offset).Take(256).ToArray();
            var batch = await brainServiceClient.ReadoutAsync(sessionId, batchRoots, cancellationToken);
            if (batch.ReturnedCount != batchRoots.Length || !batch.Neurons.Select(neuron => neuron.RootId).SequenceEqual(batchRoots))
            {
                throw new InvalidOperationException("Brain Service batch readout did not preserve the requested root ordering.");
            }

            if (firstBatch is not null && batch.SimulationStep != firstBatch.SimulationStep)
            {
                throw new InvalidOperationException("Brain Service readout batches did not represent one simulation step.");
            }

            firstBatch ??= batch;
            neurons.AddRange(batch.Neurons);
        }

        return new LogicalReadoutSnapshot(
            firstBatch!.SessionId,
            firstBatch.SimulationStep,
            firstBatch.SimulatedMilliseconds,
            neurons);
    }
}

public sealed record ReadoutResponseMetrics(
    int NonInputSpikingNeurons,
    int MembraneResponsiveNeurons,
    double MaximumAbsoluteMembraneDeltaMillivolts,
    double SumAbsoluteMembraneDeltaMillivolts,
    double MeanAbsoluteMembraneDeltaMillivolts,
    int MaximumRefractorySteps)
{
    public string PropagationClass => NonInputSpikingNeurons > 0
        ? "DOWNSTREAM SPIKING"
        : MembraneResponsiveNeurons > 0
            ? "MEMBRANE RESPONSE ONLY"
            : "NO DOWNSTREAM RESPONSE";
}

public static class ReadoutResponseAnalysis
{
    public const double MembraneToleranceMillivolts = 1e-9;

    public static IReadOnlyList<(long RootId, double MembraneDeltaMillivolts, int SpikeCountDelta, int RefractorySteps)> Deltas(
        LogicalReadoutSnapshot baseline,
        LogicalReadoutSnapshot observation)
    {
        if (!baseline.Neurons.Select(neuron => neuron.RootId).SequenceEqual(observation.Neurons.Select(neuron => neuron.RootId)))
        {
            throw new InvalidOperationException("Baseline and observation readouts must have the same ordered roots.");
        }

        return baseline.Neurons.Zip(observation.Neurons, (before, after) =>
            (after.RootId, after.MembraneMillivolts - before.MembraneMillivolts, after.CumulativeSpikeCount - before.CumulativeSpikeCount, after.RefractorySteps)).ToArray();
    }

    public static ReadoutResponseMetrics Metrics(LogicalReadoutSnapshot baseline, LogicalReadoutSnapshot observation)
    {
        var deltas = Deltas(baseline, observation);
        var absoluteMembrane = deltas.Select(delta => Math.Abs(delta.MembraneDeltaMillivolts)).ToArray();
        return new ReadoutResponseMetrics(
            deltas.Count(delta => delta.SpikeCountDelta > 0),
            absoluteMembrane.Count(value => value > MembraneToleranceMillivolts),
            absoluteMembrane.DefaultIfEmpty(0).Max(),
            absoluteMembrane.Sum(),
            absoluteMembrane.DefaultIfEmpty(0).Average(),
            deltas.Select(delta => delta.RefractorySteps).DefaultIfEmpty(0).Max());
    }
}

public sealed record TokenReadoutTrial(
    GeoCoordinateToken Token,
    LogicalReadoutSnapshot Baseline,
    IReadOnlyDictionary<int, LogicalReadoutSnapshot> Observations);

public sealed class TokenReadoutSurveyRunner(
    IBrainServiceClient brainServiceClient,
    LogicalReadoutSnapshotReader snapshotReader)
{
    public async Task<TokenReadoutTrial> ExecuteAsync(
        string sessionId,
        BrainChannelConfiguration channels,
        GeoCoordinateToken token,
        IReadOnlyList<long> candidateRoots,
        IEnumerable<int> observationOffsets,
        CancellationToken cancellationToken = default)
    {
        var offsets = observationOffsets.Distinct().OrderBy(offset => offset).ToArray();
        if (offsets.Length == 0 || offsets[0] < 0)
        {
            throw new ArgumentException("At least one non-negative observation offset is required.", nameof(observationOffsets));
        }

        await brainServiceClient.ResetSessionAsync(sessionId, cancellationToken);
        var baseline = await snapshotReader.ReadAsync(sessionId, candidateRoots, cancellationToken);
        var stimulus = await brainServiceClient.StepAsync(sessionId, new BrainStepRequest
        {
            Steps = channels.StimulusSteps,
            Stimulus = channels.GetChannel(token).Select(rootId => new BrainStimulus(rootId, channels.StimulusAmplitude)).ToArray(),
        }, cancellationToken);
        EnsureCompleted(stimulus.SafetyStatus);

        var observations = new Dictionary<int, LogicalReadoutSnapshot>();
        var advancedEmptySteps = 0;
        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var additionalSteps = offset - advancedEmptySteps;
            if (additionalSteps < 0)
            {
                throw new InvalidOperationException("Observation offsets must be ordered.");
            }

            if (additionalSteps > 0)
            {
                var advance = await brainServiceClient.StepAsync(sessionId, new BrainStepRequest
                {
                    Steps = additionalSteps,
                    Stimulus = Array.Empty<BrainStimulus>(),
                }, cancellationToken);
                EnsureCompleted(advance.SafetyStatus);
                advancedEmptySteps = offset;
            }

            observations.Add(offset, await snapshotReader.ReadAsync(sessionId, candidateRoots, cancellationToken));
        }

        return new TokenReadoutTrial(token, baseline, observations);
    }

    private static void EnsureCompleted(string safetyStatus)
    {
        if (!string.Equals(safetyStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Brain Service reported unsafe status '{safetyStatus}'.");
        }
    }
}

public sealed record NeuralFeatureVector(
    int SimulationStep,
    int ReadoutRootCount,
    int FeatureCount,
    int ObservationOffsetSteps,
    IReadOnlyList<double> Features);

public static class NeuralFeatureExtractor
{
    public static readonly IReadOnlyList<string> FeatureFields = ["membrane_delta_mV", "spike_count_delta", "refractory_steps"];

    public static NeuralFeatureVector Extract(
        LogicalReadoutSnapshot baseline,
        LogicalReadoutSnapshot observation,
        IReadOnlyList<long> orderedRootIds,
        int observationOffsetSteps)
    {
        if (!orderedRootIds.SequenceEqual(baseline.Neurons.Select(neuron => neuron.RootId)) ||
            !orderedRootIds.SequenceEqual(observation.Neurons.Select(neuron => neuron.RootId)))
        {
            throw new InvalidOperationException("Readout snapshots do not match the configured root ordering.");
        }

        var features = new double[orderedRootIds.Count * FeatureFields.Count];
        for (var index = 0; index < orderedRootIds.Count; index++)
        {
            var before = baseline.Neurons[index];
            var after = observation.Neurons[index];
            var featureOffset = index * FeatureFields.Count;
            features[featureOffset] = after.MembraneMillivolts - before.MembraneMillivolts;
            features[featureOffset + 1] = after.CumulativeSpikeCount - before.CumulativeSpikeCount;
            features[featureOffset + 2] = after.RefractorySteps;
        }

        return new NeuralFeatureVector(observation.SimulationStep, orderedRootIds.Count, features.Length, observationOffsetSteps, features);
    }
}

public sealed record TokenSeparabilityReport(
    int UniqueTokenVectors,
    double MaximumWithinTokenReplayDistance,
    double MinimumNonZeroBetweenTokenDistance,
    double MaximumBetweenTokenDistance,
    IReadOnlyList<IReadOnlyList<GeoCoordinateToken>> Collisions);

public static class TokenFeatureSurveyAnalysis
{
    public static TokenSeparabilityReport Analyze(IReadOnlyDictionary<GeoCoordinateToken, IReadOnlyList<NeuralFeatureVector>> vectorsByToken)
    {
        if (vectorsByToken.Count == 0 || vectorsByToken.Values.Any(vectors => vectors.Count == 0))
        {
            throw new ArgumentException("Every surveyed token requires at least one feature vector.", nameof(vectorsByToken));
        }

        var canonical = vectorsByToken.ToDictionary(pair => pair.Key, pair => pair.Value[0].Features);
        var within = vectorsByToken.Values.SelectMany(vectors => vectors.Skip(1).Select(vector => Distance(vectors[0].Features, vector.Features))).DefaultIfEmpty(0).Max();
        var between = canonical.OrderBy(pair => pair.Key).SelectMany((left, index) => canonical.OrderBy(pair => pair.Key).Skip(index + 1).Select(right => Distance(left.Value, right.Value))).ToArray();
        var groups = canonical.GroupBy(pair => Signature(pair.Value)).Select(group => group.Select(pair => pair.Key).OrderBy(token => token).ToArray()).ToArray();
        return new TokenSeparabilityReport(
            groups.Length,
            within,
            between.Where(distance => distance > 0).DefaultIfEmpty(0).Min(),
            between.DefaultIfEmpty(0).Max(),
            groups.Where(group => group.Length > 1).Cast<IReadOnlyList<GeoCoordinateToken>>().ToArray());
    }

    public static double Distance(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count)
        {
            throw new ArgumentException("Feature vectors must have the same length.");
        }

        return Math.Sqrt(left.Zip(right, (a, b) => (a - b) * (a - b)).Sum());
    }

    private static string Signature(IReadOnlyList<double> values) => string.Join(',', values.Select(BitConverter.DoubleToInt64Bits));
}

public sealed record ReadoutPopulationSelection(long RootId, int MaximumSpikeDelta, double MembraneRangeMillivolts, double MaximumAbsoluteMembraneDeltaMillivolts);

public static class ReadoutPopulationSelector
{
    public static IReadOnlyList<long> Select(
        IEnumerable<TokenReadoutTrial> trials,
        int observationOffsetSteps,
        IEnumerable<long> inputRoots,
        int maximumRoots = 128)
    {
        var inputSet = inputRoots.ToHashSet();
        var responses = trials.Select(trial =>
        {
            if (!trial.Observations.TryGetValue(observationOffsetSteps, out var observation))
            {
                throw new InvalidOperationException($"Token '{trial.Token}' has no observation at offset {observationOffsetSteps}.");
            }

            return ReadoutResponseAnalysis.Deltas(trial.Baseline, observation);
        }).ToArray();

        return responses.SelectMany(values => values)
            .GroupBy(value => value.RootId)
            .Where(group => !inputSet.Contains(group.Key))
            .Select(group => new ReadoutPopulationSelection(
                group.Key,
                group.Max(value => value.SpikeCountDelta),
                group.Max(value => value.MembraneDeltaMillivolts) - group.Min(value => value.MembraneDeltaMillivolts),
                group.Max(value => Math.Abs(value.MembraneDeltaMillivolts))))
            .Where(selection => selection.MaximumSpikeDelta > 0 || selection.MaximumAbsoluteMembraneDeltaMillivolts > ReadoutResponseAnalysis.MembraneToleranceMillivolts)
            .OrderByDescending(selection => selection.MaximumSpikeDelta > 0)
            .ThenByDescending(selection => selection.MaximumSpikeDelta)
            .ThenByDescending(selection => selection.MembraneRangeMillivolts)
            .ThenByDescending(selection => selection.MaximumAbsoluteMembraneDeltaMillivolts)
            .ThenBy(selection => selection.RootId)
            .Take(maximumRoots)
            .Select(selection => selection.RootId)
            .ToArray();
    }
}

public sealed class ReadoutPopulationConfiguration
{
    public ReadoutPopulationConfiguration(int version, string executionMode, int observationOffsetSteps, IReadOnlyList<long> rootIds, IReadOnlyList<string> features)
    {
        if (version != 1 || !string.Equals(executionMode, "reference", StringComparison.Ordinal) || observationOffsetSteps < 0 || rootIds.Count == 0 || rootIds.Count > 128 || rootIds.Distinct().Count() != rootIds.Count || rootIds.Any(rootId => rootId <= 0) || !features.SequenceEqual(NeuralFeatureExtractor.FeatureFields))
        {
            throw new InvalidOperationException("Invalid readout population configuration.");
        }

        Version = version; ExecutionMode = executionMode; ObservationOffsetSteps = observationOffsetSteps;
        RootIds = rootIds.ToArray(); Features = features.ToArray();
    }

    public int Version { get; }
    public string ExecutionMode { get; }
    public int ObservationOffsetSteps { get; }
    public IReadOnlyList<long> RootIds { get; }
    public IReadOnlyList<string> Features { get; }

    public static ReadoutPopulationConfiguration LoadFromFile(string path)
    {
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Readout population configuration is empty.");
        return new ReadoutPopulationConfiguration(document.Version, document.ExecutionMode, document.ObservationOffsetSteps, document.RootIds, document.Features);
    }

    private sealed class Document
    {
        [JsonPropertyName("version")] public required int Version { get; init; }
        [JsonPropertyName("execution_mode")] public required string ExecutionMode { get; init; }
        [JsonPropertyName("observation_offset_steps")] public required int ObservationOffsetSteps { get; init; }
        [JsonPropertyName("root_ids")] public required List<long> RootIds { get; init; }
        [JsonPropertyName("features")] public required List<string> Features { get; init; }
    }
}
