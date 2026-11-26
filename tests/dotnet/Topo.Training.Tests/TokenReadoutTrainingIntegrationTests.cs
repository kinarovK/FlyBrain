using System.Diagnostics;
using System.Text.Json;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;
using Xunit.Abstractions;

namespace Topo.Training.Tests;

public sealed class TokenReadoutTrainingIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReferenceReadout_CollectsAndTrainsAnExternalSemanticTokenClassifier()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_TOKEN_READOUT_EXPERIMENT"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = FlyBrain.Common.RepositoryLocator.FindRoot();
        var channelsPath = Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH") ?? Path.Combine(repositoryRoot, "modules", "topo", "config", "brain-channels.json");
        var populationPath = Environment.GetEnvironmentVariable("TOPO_READOUT_POPULATION_PATH") ?? Path.Combine(repositoryRoot, "modules", "topo", "config", "readout-population.json");
        var modelPath = Environment.GetEnvironmentVariable("TOPO_TOKEN_READOUT_MODEL_PATH") ?? Path.Combine(repositoryRoot, "modules", "topo", "models", "token-readout-v1.json");
        var reportPath = Environment.GetEnvironmentVariable("TOPO_TOKEN_READOUT_REPORT_PATH") ?? Path.Combine(repositoryRoot, "modules", "topo", "models", "token-readout-v1-report.json");
        var corpusSize = int.TryParse(Environment.GetEnvironmentVariable("TOPO_TOKEN_READOUT_CORPUS_SIZE"), out var parsedSize) ? parsedSize : 200;
        var channels = BrainChannelConfiguration.LoadFromFile(channelsPath);
        var population = ReadoutPopulationConfiguration.LoadFromFile(populationPath);
        var stopwatch = Stopwatch.StartNew();
        using var httpClient = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        var client = new BrainServiceClient(httpClient);
        string? sessionId = null;

        try
        {
            Assert.True((await client.GetReadinessAsync()).IsReady);
            sessionId = (await client.CreateSessionAsync(new CreateBrainSessionRequest
            {
                ExecutionMode = BrainExecutionMode.Reference,
                TransmitterMode = BrainTransmitterMode.Unsigned,
            })).SessionId;
            var runner = new TokenReadoutDatasetRunner(client, new LogicalReadoutSnapshotReader(client));

            var isolated = await runner.CollectIsolatedAsync(sessionId, channels, population, repetitions: 12);
            var contextualCoordinates = TokenReadoutCoordinateCorpus.Create(corpusSize);
            var contextual = await runner.CollectContextualAsync(sessionId, channels, population, contextualCoordinates);
            stopwatch.Stop();

            var parameters = TokenReadoutTrainingParameters.Default;
            var isolatedResult = TrainAndEvaluate(isolated, population, parameters);
            var contextualResult = TrainAndEvaluate(contextual, population, parameters);
            TokenReadoutModelStore.Save(modelPath, contextualResult.Artifact);
            var loaded = TokenReadoutModelStore.Load(modelPath);
            Assert.All(contextual, example => Assert.Equal(
                contextualResult.Model.Predict(contextualResult.Preprocessor.Transform(example.Vector)),
                loaded.Classifier.Predict(loaded.Preprocessor.Transform(example.Vector))));

            var report = new
            {
                phase = "6",
                collected_utc = DateTimeOffset.UtcNow,
                collection_elapsed_seconds = stopwatch.Elapsed.TotalSeconds,
                configuration = new
                {
                    population_roots = population.RootIds.Count,
                    population_observation_offset_steps = population.ObservationOffsetSteps,
                    raw_feature_dimension = contextualResult.Preprocessor.OriginalFeatureDimension,
                    retained_feature_dimension = contextualResult.Preprocessor.FeatureDimension,
                    removed_feature_indices = Enumerable.Range(0, contextualResult.Preprocessor.OriginalFeatureDimension).Except(contextualResult.Preprocessor.RetainedFeatureIndices).ToArray(),
                    training_parameters = parameters,
                },
                isolated = DescribeDataset(isolated, isolatedResult),
                contextual = DescribeDataset(contextual, contextualResult),
                repeated_digit_contexts = new[] { "N11 E11", "N33 E33", "S55 W55", "S33 E111" }.ToDictionary(
                    coordinate => coordinate,
                    coordinate => DescribeSubset(contextualResult, contextual.Where(example => string.Equals(example.CoordinateId, coordinate, StringComparison.Ordinal)))),
                persistence = new { model_path = modelPath, reload_prediction_equivalent = true },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
            output.WriteLine(JsonSerializer.Serialize(report));

            Assert.Equal(384, contextualResult.Preprocessor.OriginalFeatureDimension);
            Assert.True(contextualResult.Preprocessor.FeatureDimension > 0);
            Assert.Equal(contextualCoordinates.Count, contextual.Select(example => example.CoordinateId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(contextualCoordinates.Count, contextualResult.Split.TrainingCoordinateIds.Count + contextualResult.Split.ValidationCoordinateIds.Count + contextualResult.Split.TestCoordinateIds.Count);
        }
        finally
        {
            if (sessionId is not null)
            {
                await client.DeleteSessionAsync(sessionId);
            }
        }
    }

    private static ExperimentResult TrainAndEvaluate(IReadOnlyList<TokenReadoutExample> examples, ReadoutPopulationConfiguration population, TokenReadoutTrainingParameters parameters)
    {
        var split = TokenReadoutDatasetSplitter.SplitByCoordinate(examples, parameters.Seed);
        var preprocessor = TokenReadoutPreprocessor.Fit(split.Training);
        var model = new TokenSoftmaxClassifier(Enum.GetValues<GeoCoordinateToken>(), preprocessor.FeatureDimension, parameters.Seed);
        var training = split.Training.Select(preprocessor.Transform).ToArray();
        model.Train(training, parameters);
        return new ExperimentResult(
            split,
            preprocessor,
            model,
            TokenReadoutEvaluator.Evaluate(model, training),
            TokenReadoutEvaluator.Evaluate(model, split.Validation.Select(preprocessor.Transform)),
            TokenReadoutEvaluator.Evaluate(model, split.Test.Select(preprocessor.Transform)),
            TokenReadoutModelStore.CreateArtifact(model, preprocessor, parameters, population));
    }

    private static object DescribeDataset(IReadOnlyList<TokenReadoutExample> examples, ExperimentResult result) => new
    {
        observations = examples.Count,
        class_distribution = examples.GroupBy(example => example.Token).OrderBy(group => group.Key).ToDictionary(group => group.Key.ToString(), group => group.Count()),
        coordinate_split = new { training = result.Split.TrainingCoordinateIds.Count, validation = result.Split.ValidationCoordinateIds.Count, test = result.Split.TestCoordinateIds.Count },
        train = DescribeEvaluation(result.Training),
        validation = DescribeEvaluation(result.Validation),
        test = DescribeEvaluation(result.Test),
        majority_baseline_accuracy = examples.GroupBy(example => example.Token).Max(group => group.Count()) / (double)examples.Count,
        random_baseline_accuracy = 1d / Enum.GetValues<GeoCoordinateToken>().Length,
    };

    private static object DescribeSubset(ExperimentResult result, IEnumerable<TokenReadoutExample> examples)
    {
        var subset = examples.ToArray();
        return subset.Length == 0
            ? new { observations = 0, accuracy = 0d }
            : new
            {
                observations = subset.Length,
                accuracy = TokenReadoutEvaluator.Evaluate(result.Model, subset.Select(result.Preprocessor.Transform)).Accuracy,
                repeated_tokens = subset.GroupBy(example => example.Token)
                    .Where(group => group.Count() > 1)
                    .OrderBy(group => group.Key)
                    .ToDictionary(
                        group => group.Key.ToString(),
                        group => new
                        {
                            occurrences = group.Count(),
                            accuracy = TokenReadoutEvaluator.Evaluate(result.Model, group.Select(result.Preprocessor.Transform)).Accuracy,
                        }),
            };
    }

    private static object DescribeEvaluation(TokenReadoutEvaluation evaluation) => new
    {
        accuracy = evaluation.Accuracy,
        macro_recall = evaluation.MacroRecall,
        support = evaluation.SupportByClass.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
        recall = evaluation.RecallByClass.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
        confusion_matrix = evaluation.ConfusionMatrix,
    };

    private sealed record ExperimentResult(
        TokenReadoutDatasetSplit Split,
        TokenReadoutPreprocessor Preprocessor,
        TokenSoftmaxClassifier Model,
        TokenReadoutEvaluation Training,
        TokenReadoutEvaluation Validation,
        TokenReadoutEvaluation Test,
        TokenReadoutModelArtifact Artifact);
}
