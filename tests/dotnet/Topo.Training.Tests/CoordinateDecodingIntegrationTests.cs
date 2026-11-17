using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;
using Xunit.Abstractions;

namespace Topo.Training.Tests;

public sealed class CoordinateDecodingIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FrozenTokenReadout_DecodesBlindCompleteCoordinates()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_COORDINATE_DECODING_EXPERIMENT"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = FlyBrain.Common.RepositoryLocator.FindRoot();
        var channelsPath = Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH") ?? Path.Combine(root, "modules", "topo", "config", "brain-channels.json");
        var populationPath = Environment.GetEnvironmentVariable("TOPO_READOUT_POPULATION_PATH") ?? Path.Combine(root, "modules", "topo", "config", "readout-population.json");
        var modelPath = Environment.GetEnvironmentVariable("TOPO_TOKEN_READOUT_MODEL_PATH") ?? Path.Combine(root, "modules", "topo", "models", "token-readout-v1.json");
        var experimentDirectory = Environment.GetEnvironmentVariable("TOPO_COORDINATE_DECODING_OUTPUT") ?? Path.Combine(root, "modules", "topo", "experiments", "coordinate-decoding-v1");
        var count = int.TryParse(Environment.GetEnvironmentVariable("TOPO_COORDINATE_DECODING_CORPUS_SIZE"), out var configuredCount) ? configuredCount : 300;
        var channels = BrainChannelConfiguration.LoadFromFile(channelsPath);
        var population = ReadoutPopulationConfiguration.LoadFromFile(populationPath);
        var loaded = TokenReadoutModelStore.Load(modelPath);
        TokenReadoutCompatibility.Validate(loaded.Artifact, loaded.Classifier, loaded.Preprocessor, population, channels);
        var blind = CoordinateDecodingEvaluationCorpus.CreateBlind(count);
        var phaseSixTrain = CoordinateDecodingEvaluationCorpus.PhaseSixTrainingCoordinates();
        var overlap = blind.Count(coordinate => phaseSixTrain.Contains(coordinate.ToString()));
        Assert.Equal(0, overlap);

        using var httpClient = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        var client = new BrainServiceClient(httpClient);
        string? sessionId = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var readiness = await client.GetReadinessAsync();
            Assert.True(readiness.IsReady);
            sessionId = (await client.CreateSessionAsync(new CreateBrainSessionRequest
            {
                ExecutionMode = BrainExecutionMode.Reference,
                TransmitterMode = BrainTransmitterMode.Unsigned,
            })).SessionId;
            var pipeline = new CoordinateNeuralDecodePipeline(client, new LogicalReadoutSnapshotReader(client));
            var results = new List<CoordinateNeuralDecode>(blind.Count);
            foreach (var coordinate in blind)
            {
                results.Add(await pipeline.DecodeAsync(sessionId, coordinate, channels, population, loaded.Classifier, loaded.Preprocessor));
            }

            var edgeSources = new[] { "N0 E0", "S0 W0", "N90 E180", "N90 W180", "S90 E180", "S90 W180", "N1 E1", "S1 W1", "N11 E11", "N55 W55", "S33 E111", "N88 W177" }.Select(GeoCoordinate.Parse).ToArray();
            var edges = new List<CoordinateNeuralDecode>();
            foreach (var coordinate in edgeSources)
            {
                edges.Add(await pipeline.DecodeAsync(sessionId, coordinate, channels, population, loaded.Classifier, loaded.Preprocessor));
            }

            var manualSources = new[] { "N35 W54", "N47 E22", "S23 E117" }.Select(GeoCoordinate.Parse).ToArray();
            var manual = new List<CoordinateNeuralDecode>();
            foreach (var coordinate in manualSources)
            {
                manual.Add(await pipeline.DecodeAsync(sessionId, coordinate, channels, population, loaded.Classifier, loaded.Preprocessor));
            }

            var repeatedSources = new[] { "N11 E11", "N22 E22", "N33 E33", "N44 E44", "N55 W55", "N66 E66", "N77 W77", "N88 E88", "N9 E99", "S33 E111" }.Select(GeoCoordinate.Parse).ToArray();
            var repeated = new List<CoordinateNeuralDecode>();
            foreach (var coordinate in repeatedSources)
            {
                repeated.Add(await pipeline.DecodeAsync(sessionId, coordinate, channels, population, loaded.Classifier, loaded.Preprocessor));
            }

            stopwatch.Stop();
            Directory.CreateDirectory(experimentDirectory);
            var report = BuildReport(results, edges, manual, repeated, overlap, readiness.Result.ServiceVersion, loaded.Artifact, channelsPath, populationPath, modelPath, stopwatch.Elapsed);
            File.WriteAllText(Path.Combine(experimentDirectory, "blind-evaluation.json"), JsonSerializer.Serialize(report, JsonOptions));
            File.WriteAllText(Path.Combine(experimentDirectory, "representative-traces.json"), JsonSerializer.Serialize(manual.Concat(results.Take(2)).Select(Trace), JsonOptions));
            File.WriteAllText(Path.Combine(experimentDirectory, "failures.json"), JsonSerializer.Serialize(results.Where(result => !IsExact(result)).Select(Trace), JsonOptions));
            output.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            Assert.All(results, result => Assert.True(IsExact(result), $"Failed to decode {result.SourceCoordinate}."));
        }
        finally
        {
            if (sessionId is not null)
            {
                await client.DeleteSessionAsync(sessionId);
            }
        }
    }

    private static object BuildReport(IReadOnlyList<CoordinateNeuralDecode> results, IReadOnlyList<CoordinateNeuralDecode> edges, IReadOnlyList<CoordinateNeuralDecode> manual, IReadOnlyList<CoordinateNeuralDecode> repeated, int overlap, string serviceVersion, TokenReadoutModelArtifact model, string channelsPath, string populationPath, string modelPath, TimeSpan elapsed)
    {
        var allTokens = results.SelectMany(result => result.ExpectedTokens.Zip(result.PredictedTokens)).ToArray();
        var valid = results.Where(result => result.Decoding.IsSuccess).ToArray();
        var exact = results.Where(IsExact).ToArray();
        var angular = valid.Select(result => GeoCoordinate.AngularDistanceDegrees(result.SourceCoordinate, result.Decoding.Coordinate!.Value)).OrderBy(value => value).ToArray();
        var correctConfidence = exact.Select(MeanConfidence).ToArray();
        var incorrectConfidence = results.Where(result => !IsExact(result)).Select(MeanConfidence).ToArray();
        var latitudeHemisphere = results.Count(result => result.Predictions[0].PredictedToken == result.ExpectedTokens[0]);
        var longitudeHemisphere = results.Count(result =>
        {
            var index = Array.IndexOf(result.ExpectedTokens.ToArray(), GeoCoordinateToken.Separator) + 1;
            return result.Predictions[index].PredictedToken == result.ExpectedTokens[index];
        });
        return new
        {
            phase = "7",
            model = new { path = modelPath, schema_version = model.SchemaVersion, model_type = model.ModelType, raw_dimension = model.OriginalFeatureDimension, retained_dimension = model.RetainedFeatureIndices.Count },
            configuration = new { execution_mode = "reference", transmitter_mode = "unsigned", brain_service_version = serviceVersion, channels_sha256 = Hash(channelsPath), readout_population_sha256 = Hash(populationPath), timing = new { amplitude = 100, stimulus_steps = 1, settle_steps = 22, observation_offset_steps = 22 } },
            blind_corpus = new { coordinates = results.Count, token_observations = allTokens.Length, seed = CoordinateDecodingEvaluationCorpus.Seed, phase_six_train_overlap = overlap, phase_six_identity_note = "Phase 6 artifact did not persist coordinate IDs; overlap was reconstructed from its documented deterministic 200-coordinate corpus and split seed." },
            exact_coordinate = new { accuracy = exact.Count() / (double)results.Count, correct = exact.Count(), incorrect = results.Count - exact.Count(), invalid = results.Count - valid.Length },
            token_accuracy = new { overall = allTokens.Count(pair => pair.First == pair.Second) / (double)allTokens.Length, by_class = allTokens.GroupBy(pair => pair.First).OrderBy(group => group.Key).ToDictionary(group => group.Key.ToString(), group => group.Count(pair => pair.First == pair.Second) / (double)group.Count()) },
            components = new { latitude_hemisphere_accuracy = latitudeHemisphere / (double)results.Count, longitude_hemisphere_accuracy = longitudeHemisphere / (double)results.Count, latitude_magnitude_accuracy = valid.Count(result => result.SourceCoordinate.LatitudeMagnitude == result.Decoding.Coordinate!.Value.LatitudeMagnitude) / (double)results.Count, longitude_magnitude_accuracy = valid.Count(result => result.SourceCoordinate.LongitudeMagnitude == result.Decoding.Coordinate!.Value.LongitudeMagnitude) / (double)results.Count },
            angular_error_degrees = new { mean = angular.DefaultIfEmpty(0).Average(), median = Median(angular), maximum = angular.DefaultIfEmpty(0).Max() },
            confidence_score = new { correct = Statistics(correctConfidence), incorrect = Statistics(incorrectConfidence) },
            edge_cases = edges.Select(Summary),
            manual_examples = manual.Select(Summary),
            repeated_token_suite = repeated.Select(Summary),
            performance = new { total_wall_seconds = elapsed.TotalSeconds, average_coordinate_seconds = elapsed.TotalSeconds / results.Count },
        };
    }

    private static object Summary(CoordinateNeuralDecode result) => new { source = result.SourceCoordinate.ToString(), expected_tokens = result.ExpectedTokens.Select(token => token.ToString()), predicted_tokens = result.PredictedTokens.Select(token => token.ToString()), decoded = result.Decoding.Coordinate?.ToString(), valid = result.Decoding.IsSuccess, exact = IsExact(result), minimum_confidence_score = result.Predictions.Min(prediction => prediction.ConfidenceScore), angular_error_degrees = result.Decoding.IsSuccess ? GeoCoordinate.AngularDistanceDegrees(result.SourceCoordinate, result.Decoding.Coordinate!.Value) : (double?)null };
    private static object Trace(CoordinateNeuralDecode result) => new { source = result.SourceCoordinate.ToString(), expected_tokens = result.ExpectedTokens.Select(token => token.ToString()), predicted_tokens = result.PredictedTokens.Select(token => token.ToString()), decoded = result.Decoding.Coordinate?.ToString(), steps = result.Predictions.Select(prediction => new { prediction.SequenceIndex, prediction.SimulationStep, predicted = prediction.PredictedToken.ToString(), confidence_score = prediction.ConfidenceScore, prediction.FeatureCount, prediction.ObservationOffsetSteps }) };
    private static bool IsExact(CoordinateNeuralDecode result) => result.Decoding.IsSuccess && result.Decoding.Coordinate == result.SourceCoordinate;
    private static double MeanConfidence(CoordinateNeuralDecode result) => result.Predictions.Average(prediction => prediction.ConfidenceScore);
    private static object Statistics(IReadOnlyList<double> values) => new { count = values.Count, minimum = values.DefaultIfEmpty(0).Min(), mean = values.DefaultIfEmpty(0).Average(), maximum = values.DefaultIfEmpty(0).Max() };
    private static double Median(IReadOnlyList<double> sorted) => sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
