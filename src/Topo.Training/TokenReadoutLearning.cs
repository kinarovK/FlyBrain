using System.Text.Json;
using System.Text.Json.Serialization;
using FlyBrain.BrainClient;
using Topo.Core;

namespace Topo.Training;

/// <summary>A labelled neural observation.  CoordinateId is the split boundary for contextual data.</summary>
public sealed record TokenReadoutExample(
    string CoordinateId,
    GeoCoordinateToken Token,
    NeuralFeatureVector Vector);

public sealed record PreparedTokenReadoutExample(
    string CoordinateId,
    GeoCoordinateToken Token,
    IReadOnlyList<double> Features);

public sealed record TokenReadoutDatasetSplit(
    IReadOnlyList<TokenReadoutExample> Training,
    IReadOnlyList<TokenReadoutExample> Validation,
    IReadOnlyList<TokenReadoutExample> Test,
    IReadOnlyList<string> TrainingCoordinateIds,
    IReadOnlyList<string> ValidationCoordinateIds,
    IReadOnlyList<string> TestCoordinateIds);

public static class TokenReadoutDatasetSplitter
{
    /// <summary>Splits whole coordinates reproducibly; a coordinate can never occur in two partitions.</summary>
    public static TokenReadoutDatasetSplit SplitByCoordinate(
        IEnumerable<TokenReadoutExample> examples,
        int seed,
        double trainingFraction = .70,
        double validationFraction = .15)
    {
        ArgumentNullException.ThrowIfNull(examples);
        if (trainingFraction <= 0 || validationFraction <= 0 || trainingFraction + validationFraction >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(trainingFraction));
        }

        var all = examples.ToArray();
        var ids = all.Select(example => example.CoordinateId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (ids.Length < 3)
        {
            throw new ArgumentException("At least three coordinate groups are required.", nameof(examples));
        }

        var random = new Random(seed);
        for (var index = ids.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (ids[index], ids[other]) = (ids[other], ids[index]);
        }

        var trainingCount = (int)Math.Floor(ids.Length * trainingFraction);
        var validationCount = (int)Math.Floor(ids.Length * validationFraction);
        trainingCount = Math.Clamp(trainingCount, 1, ids.Length - 2);
        validationCount = Math.Clamp(validationCount, 1, ids.Length - trainingCount - 1);
        var trainingIds = ids[..trainingCount];
        var validationIds = ids[trainingCount..(trainingCount + validationCount)];
        var testIds = ids[(trainingCount + validationCount)..];
        var trainingSet = trainingIds.ToHashSet(StringComparer.Ordinal);
        var validationSet = validationIds.ToHashSet(StringComparer.Ordinal);

        return new TokenReadoutDatasetSplit(
            all.Where(example => trainingSet.Contains(example.CoordinateId)).ToArray(),
            all.Where(example => validationSet.Contains(example.CoordinateId)).ToArray(),
            all.Where(example => !trainingSet.Contains(example.CoordinateId) && !validationSet.Contains(example.CoordinateId)).ToArray(),
            trainingIds,
            validationIds,
            testIds);
    }
}

public sealed class TokenReadoutPreprocessor
{
    private const double MinimumStandardDeviation = 1e-12;

    public TokenReadoutPreprocessor(int originalFeatureDimension, IReadOnlyList<int> retainedFeatureIndices, IReadOnlyList<double> means, IReadOnlyList<double> standardDeviations)
    {
        if (originalFeatureDimension < 1 || retainedFeatureIndices.Count == 0 || retainedFeatureIndices.Count != means.Count || means.Count != standardDeviations.Count || retainedFeatureIndices.Any(index => index < 0 || index >= originalFeatureDimension) || retainedFeatureIndices.Distinct().Count() != retainedFeatureIndices.Count || standardDeviations.Any(value => !double.IsFinite(value) || value <= MinimumStandardDeviation))
        {
            throw new ArgumentException("Invalid feature preprocessing metadata.");
        }

        OriginalFeatureDimension = originalFeatureDimension;
        RetainedFeatureIndices = retainedFeatureIndices.ToArray();
        Means = means.ToArray();
        StandardDeviations = standardDeviations.ToArray();
    }

    public int OriginalFeatureDimension { get; }
    public IReadOnlyList<int> RetainedFeatureIndices { get; }
    public IReadOnlyList<double> Means { get; }
    public IReadOnlyList<double> StandardDeviations { get; }
    public int FeatureDimension => RetainedFeatureIndices.Count;

    public static TokenReadoutPreprocessor Fit(IEnumerable<TokenReadoutExample> trainingExamples)
    {
        ArgumentNullException.ThrowIfNull(trainingExamples);
        var examples = trainingExamples.ToArray();
        if (examples.Length == 0)
        {
            throw new ArgumentException("At least one training example is required.", nameof(trainingExamples));
        }

        var dimension = examples[0].Vector.FeatureCount;
        if (dimension < 1 || examples.Any(example => example.Vector.FeatureCount != dimension || example.Vector.Features.Count != dimension))
        {
            throw new ArgumentException("Training feature vectors must have one non-zero dimension.", nameof(trainingExamples));
        }

        var retained = new List<int>();
        var means = new List<double>();
        var deviations = new List<double>();
        for (var column = 0; column < dimension; column++)
        {
            var mean = examples.Average(example => example.Vector.Features[column]);
            var variance = examples.Average(example => Math.Pow(example.Vector.Features[column] - mean, 2));
            var deviation = Math.Sqrt(variance);
            if (deviation > MinimumStandardDeviation && double.IsFinite(deviation))
            {
                retained.Add(column);
                means.Add(mean);
                deviations.Add(deviation);
            }
        }

        if (retained.Count == 0)
        {
            throw new InvalidOperationException("Training data has no non-constant features.");
        }

        return new TokenReadoutPreprocessor(dimension, retained, means, deviations);
    }

    public IReadOnlyList<double> Transform(NeuralFeatureVector vector)
    {
        if (vector.FeatureCount != OriginalFeatureDimension || vector.Features.Count != OriginalFeatureDimension)
        {
            throw new ArgumentException("Feature vector does not match the fitted raw dimension.", nameof(vector));
        }

        var transformed = new double[FeatureDimension];
        for (var index = 0; index < transformed.Length; index++)
        {
            transformed[index] = (vector.Features[RetainedFeatureIndices[index]] - Means[index]) / StandardDeviations[index];
        }

        return transformed;
    }

    public PreparedTokenReadoutExample Transform(TokenReadoutExample example) =>
        new(example.CoordinateId, example.Token, Transform(example.Vector));
}

public sealed record TokenReadoutTrainingParameters(int Seed, double LearningRate, int Epochs, double L2Regularization, int BatchSize = 64)
{
    public static TokenReadoutTrainingParameters Default { get; } = new(20260922, .08, 180, 1e-4, 64);
}

/// <summary>Small transparent multiclass softmax classifier; no external ML dependency is used.</summary>
public sealed class TokenSoftmaxClassifier
{
    public TokenSoftmaxClassifier(IReadOnlyList<GeoCoordinateToken> classOrdering, int featureDimension, int seed)
        : this(classOrdering, CreateWeights(classOrdering, featureDimension, seed), new double[classOrdering.Count])
    {
    }

    public TokenSoftmaxClassifier(IReadOnlyList<GeoCoordinateToken> classOrdering, IReadOnlyList<IReadOnlyList<double>> weights, IReadOnlyList<double> biases)
    {
        if (classOrdering.Count < 2 || classOrdering.Distinct().Count() != classOrdering.Count || weights.Count != classOrdering.Count || biases.Count != classOrdering.Count || weights.Count == 0 || weights.Select(row => row.Count).Distinct().Count() != 1 || weights[0].Count == 0)
        {
            throw new ArgumentException("Invalid softmax model shape.");
        }

        ClassOrdering = classOrdering.ToArray();
        Weights = weights.Select(row => row.ToArray()).ToArray();
        Biases = biases.ToArray();
    }

    public IReadOnlyList<GeoCoordinateToken> ClassOrdering { get; }
    public IReadOnlyList<IReadOnlyList<double>> Weights { get; }
    public IReadOnlyList<double> Biases { get; }
    public int FeatureDimension => Weights[0].Count;

    public void Train(IReadOnlyList<PreparedTokenReadoutExample> trainingExamples, TokenReadoutTrainingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(trainingExamples);
        if (trainingExamples.Count == 0 || trainingExamples.Any(example => example.Features.Count != FeatureDimension) || parameters.LearningRate <= 0 || parameters.Epochs < 1 || parameters.L2Regularization < 0 || parameters.BatchSize < 1)
        {
            throw new ArgumentException("Invalid softmax training input or parameters.");
        }

        var classIndex = ClassOrdering.Select((token, index) => (token, index)).ToDictionary(pair => pair.token, pair => pair.index);
        if (trainingExamples.Any(example => !classIndex.ContainsKey(example.Token)))
        {
            throw new ArgumentException("Training contains a token absent from the class ordering.", nameof(trainingExamples));
        }

        for (var epoch = 0; epoch < parameters.Epochs; epoch++)
        {
            for (var batchStart = 0; batchStart < trainingExamples.Count; batchStart += parameters.BatchSize)
            {
                var batchEnd = Math.Min(batchStart + parameters.BatchSize, trainingExamples.Count);
                var gradientWeights = CreateZeroWeights(ClassOrdering.Count, FeatureDimension);
                var gradientBiases = new double[ClassOrdering.Count];
                for (var sample = batchStart; sample < batchEnd; sample++)
                {
                    var example = trainingExamples[sample];
                    var probabilities = PredictProbabilities(example.Features);
                    var expected = classIndex[example.Token];
                    for (var output = 0; output < ClassOrdering.Count; output++)
                    {
                        var error = probabilities[output] - (output == expected ? 1d : 0d);
                        gradientBiases[output] += error;
                        for (var input = 0; input < FeatureDimension; input++)
                        {
                            gradientWeights[output][input] += error * example.Features[input];
                        }
                    }
                }

                var batchSize = batchEnd - batchStart;
                for (var output = 0; output < ClassOrdering.Count; output++)
                {
                    Biases.AsArray()[output] -= parameters.LearningRate * gradientBiases[output] / batchSize;
                    for (var input = 0; input < FeatureDimension; input++)
                    {
                        var gradient = gradientWeights[output][input] / batchSize + (parameters.L2Regularization * Weights[output][input]);
                        Weights[output].AsArray()[input] -= parameters.LearningRate * gradient;
                    }
                }
            }
        }
    }

    public GeoCoordinateToken Predict(IReadOnlyList<double> features)
    {
        var probabilities = PredictProbabilities(features);
        var prediction = 0;
        for (var index = 1; index < probabilities.Count; index++)
        {
            if (probabilities[index] > probabilities[prediction])
            {
                prediction = index;
            }
        }

        return ClassOrdering[prediction];
    }

    public IReadOnlyList<double> PredictProbabilities(IReadOnlyList<double> features)
    {
        if (features.Count != FeatureDimension)
        {
            throw new ArgumentException("Feature vector does not match model input dimension.", nameof(features));
        }

        var logits = new double[ClassOrdering.Count];
        for (var output = 0; output < logits.Length; output++)
        {
            var logit = Biases[output];
            for (var input = 0; input < features.Count; input++)
            {
                logit += features[input] * Weights[output][input];
            }

            logits[output] = logit;
        }

        var maximum = logits.Max();
        var exponentials = logits.Select(value => Math.Exp(value - maximum)).ToArray();
        var total = exponentials.Sum();
        return exponentials.Select(value => value / total).ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<double>> CreateWeights(IReadOnlyList<GeoCoordinateToken> classes, int featureDimension, int seed)
    {
        if (featureDimension < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(featureDimension));
        }

        var random = new Random(seed);
        return Enumerable.Range(0, classes.Count)
            .Select(_ => Enumerable.Range(0, featureDimension).Select(__ => (random.NextDouble() - .5) * .02).ToArray() as IReadOnlyList<double>)
            .ToArray();
    }

    private static double[][] CreateZeroWeights(int outputs, int inputs) => Enumerable.Range(0, outputs).Select(_ => new double[inputs]).ToArray();
}

public sealed record TokenReadoutEvaluation(
    double Accuracy,
    double MacroRecall,
    IReadOnlyDictionary<GeoCoordinateToken, int> SupportByClass,
    IReadOnlyDictionary<GeoCoordinateToken, double> RecallByClass,
    IReadOnlyList<IReadOnlyList<int>> ConfusionMatrix);

public static class TokenReadoutEvaluator
{
    public static TokenReadoutEvaluation Evaluate(TokenSoftmaxClassifier classifier, IEnumerable<PreparedTokenReadoutExample> examples)
    {
        var items = examples.ToArray();
        if (items.Length == 0)
        {
            throw new ArgumentException("Evaluation needs at least one example.", nameof(examples));
        }

        var labels = classifier.ClassOrdering;
        var indices = labels.Select((token, index) => (token, index)).ToDictionary(pair => pair.token, pair => pair.index);
        var matrix = Enumerable.Range(0, labels.Count).Select(_ => new int[labels.Count]).ToArray();
        foreach (var item in items)
        {
            matrix[indices[item.Token]][indices[classifier.Predict(item.Features)]]++;
        }

        var support = labels.ToDictionary(token => token, token => matrix[indices[token]].Sum());
        var recall = labels.ToDictionary(token => token, token => support[token] == 0 ? 0d : (double)matrix[indices[token]][indices[token]] / support[token]);
        return new TokenReadoutEvaluation(
            (double)matrix.Select((row, index) => row[index]).Sum() / items.Length,
            recall.Where(pair => support[pair.Key] > 0).Select(pair => pair.Value).Average(),
            support,
            recall,
            matrix.Select(row => (IReadOnlyList<int>)row).ToArray());
    }
}

public sealed record TokenReadoutModelArtifact(
    int SchemaVersion,
    string ModelType,
    int Seed,
    double LearningRate,
    int Epochs,
    double L2Regularization,
    int BatchSize,
    IReadOnlyList<string> ClassOrdering,
    int OriginalFeatureDimension,
    IReadOnlyList<int> RetainedFeatureIndices,
    IReadOnlyList<double> Means,
    IReadOnlyList<double> StandardDeviations,
    IReadOnlyList<IReadOnlyList<double>> Weights,
    IReadOnlyList<double> Biases,
    int ReadoutPopulationVersion,
    int ObservationOffsetSteps,
    IReadOnlyList<long> ReadoutRootIds,
    IReadOnlyList<string> FeatureFields);

public static class TokenReadoutModelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static TokenReadoutModelArtifact CreateArtifact(TokenSoftmaxClassifier classifier, TokenReadoutPreprocessor preprocessor, TokenReadoutTrainingParameters parameters, ReadoutPopulationConfiguration population) =>
        new(1, "multiclass_softmax_linear", parameters.Seed, parameters.LearningRate, parameters.Epochs, parameters.L2Regularization, parameters.BatchSize,
            classifier.ClassOrdering.Select(token => token.ToString()).ToArray(), preprocessor.OriginalFeatureDimension,
            preprocessor.RetainedFeatureIndices, preprocessor.Means, preprocessor.StandardDeviations,
            classifier.Weights, classifier.Biases, population.Version, population.ObservationOffsetSteps, population.RootIds, population.Features);

    public static void Save(string path, TokenReadoutModelArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(artifact, JsonOptions));
    }

    public static (TokenSoftmaxClassifier Classifier, TokenReadoutPreprocessor Preprocessor, TokenReadoutModelArtifact Artifact) Load(string path)
    {
        var artifact = JsonSerializer.Deserialize<TokenReadoutModelArtifact>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidOperationException("Model artifact is empty.");
        if (artifact.SchemaVersion != 1 || !string.Equals(artifact.ModelType, "multiclass_softmax_linear", StringComparison.Ordinal) || !artifact.FeatureFields.SequenceEqual(NeuralFeatureExtractor.FeatureFields))
        {
            throw new InvalidOperationException("Unsupported token-readout model artifact.");
        }

        var ordering = artifact.ClassOrdering.Select(name => Enum.Parse<GeoCoordinateToken>(name, ignoreCase: false)).ToArray();
        return (
            new TokenSoftmaxClassifier(ordering, artifact.Weights, artifact.Biases),
            new TokenReadoutPreprocessor(artifact.OriginalFeatureDimension, artifact.RetainedFeatureIndices, artifact.Means, artifact.StandardDeviations),
            artifact);
    }
}

/// <summary>Collects isolated and contextual observations while keeping all mutable Brain Service actions serial.</summary>
public sealed class TokenReadoutDatasetRunner(IBrainServiceClient client, LogicalReadoutSnapshotReader reader)
{
    public async Task<IReadOnlyList<TokenReadoutExample>> CollectIsolatedAsync(
        string sessionId,
        BrainChannelConfiguration channels,
        ReadoutPopulationConfiguration population,
        int repetitions,
        CancellationToken cancellationToken = default)
    {
        if (repetitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repetitions));
        }

        var examples = new List<TokenReadoutExample>();
        foreach (var token in Enum.GetValues<GeoCoordinateToken>())
        {
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.ResetSessionAsync(sessionId, cancellationToken);
                var before = await reader.ReadAsync(sessionId, population.RootIds, cancellationToken);
                await StimulateAndAdvanceAsync(sessionId, channels, token, population.ObservationOffsetSteps, cancellationToken);
                var after = await reader.ReadAsync(sessionId, population.RootIds, cancellationToken);
                examples.Add(new TokenReadoutExample($"isolated:{token}:{repetition}", token, NeuralFeatureExtractor.Extract(before, after, population.RootIds, population.ObservationOffsetSteps)));
            }
        }

        return examples;
    }

    public async Task<IReadOnlyList<TokenReadoutExample>> CollectContextualAsync(
        string sessionId,
        BrainChannelConfiguration channels,
        ReadoutPopulationConfiguration population,
        IEnumerable<GeoCoordinate> coordinates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        if (population.ObservationOffsetSteps != channels.SettleSteps)
        {
            throw new InvalidOperationException("Contextual collection requires the accepted observation offset to equal configured settle steps; do not add an extra settle period.");
        }

        var examples = new List<TokenReadoutExample>();
        var encoder = new CoordinateNeuralEncoder();
        foreach (var coordinate in coordinates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.ResetSessionAsync(sessionId, cancellationToken);
            var before = await reader.ReadAsync(sessionId, population.RootIds, cancellationToken);
            foreach (var stimulus in encoder.Encode(coordinate, channels).Entries)
            {
                await StepCompletedAsync(sessionId, new BrainStepRequest
                {
                    Steps = stimulus.StimulusSteps,
                    Stimulus = stimulus.RootIds.Select(rootId => new BrainStimulus(rootId, stimulus.StimulusAmplitude)).ToArray(),
                }, cancellationToken);
                await StepCompletedAsync(sessionId, new BrainStepRequest { Steps = stimulus.SettleSteps, Stimulus = Array.Empty<BrainStimulus>() }, cancellationToken);
                var after = await reader.ReadAsync(sessionId, population.RootIds, cancellationToken);
                examples.Add(new TokenReadoutExample(coordinate.ToString(), stimulus.Token, NeuralFeatureExtractor.Extract(before, after, population.RootIds, population.ObservationOffsetSteps)));
                before = after;
            }
        }

        return examples;
    }

    private async Task StimulateAndAdvanceAsync(string sessionId, BrainChannelConfiguration channels, GeoCoordinateToken token, int observationOffsetSteps, CancellationToken cancellationToken)
    {
        await StepCompletedAsync(sessionId, new BrainStepRequest
        {
            Steps = channels.StimulusSteps,
            Stimulus = channels.GetChannel(token).Select(rootId => new BrainStimulus(rootId, channels.StimulusAmplitude)).ToArray(),
        }, cancellationToken);
        if (observationOffsetSteps > 0)
        {
            await StepCompletedAsync(sessionId, new BrainStepRequest { Steps = observationOffsetSteps, Stimulus = Array.Empty<BrainStimulus>() }, cancellationToken);
        }
    }

    private async Task StepCompletedAsync(string sessionId, BrainStepRequest request, CancellationToken cancellationToken)
    {
        var response = await client.StepAsync(sessionId, request, cancellationToken);
        if (!string.Equals(response.SafetyStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Brain Service reported unsafe status '{response.SafetyStatus}'.");
        }
    }
}

public static class TokenReadoutCoordinateCorpus
{
    public static IReadOnlyList<GeoCoordinate> Create(int count = 210, int seed = 20260922)
    {
        if (count is < 12 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The Phase 6 corpus must contain 12 to 500 complete coordinates.");
        }

        var required = new[] { "N5 E7", "N11 E11", "N55 W11", "S33 E111", "N90 W180", "S0 E0", "N33 E33", "S55 W55", "N11 W111", "S33 E55", "N55 E111", "S11 W33" };
        var coordinates = required.Select(GeoCoordinate.Parse).ToList();
        var known = coordinates.Select(coordinate => coordinate.ToString()).ToHashSet(StringComparer.Ordinal);
        var random = new Random(seed);
        while (coordinates.Count < count)
        {
            var latitude = random.Next(-90, 91);
            var longitude = random.Next(-180, 181);
            var coordinate = new GeoCoordinate(latitude, longitude);
            if (known.Add(coordinate.ToString()))
            {
                coordinates.Add(coordinate);
            }
        }

        return coordinates;
    }
}

internal static class ReadOnlyListExtensions
{
    public static T[] AsArray<T>(this IReadOnlyList<T> values) => values as T[] ?? throw new InvalidOperationException("Mutable model storage was unexpectedly replaced.");
}
