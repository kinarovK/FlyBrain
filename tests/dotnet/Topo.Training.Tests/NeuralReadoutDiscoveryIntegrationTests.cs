using System.Net;
using System.Text.Json;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;
using Xunit.Abstractions;

namespace Topo.Training.Tests;

public sealed class NeuralReadoutDiscoveryIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReferenceDomainDiscovery_ExcludesInputsAndSurveysEveryToken()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_BRAIN_SERVICE_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var configurationPath = Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH")
            ?? throw new InvalidOperationException("TOPO_BRAIN_CHANNELS_PATH is required for discovery.");
        var channels = BrainChannelConfiguration.LoadFromFile(configurationPath);
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
            var allRoots = await EnumerateAllRootsAsync(client, sessionId);
            var inputRoots = channels.Channels.Values.SelectMany(roots => roots).ToHashSet();
            var candidates = allRoots.Where(rootId => !inputRoots.Contains(rootId)).ToArray();
            Assert.Empty(candidates.Intersect(inputRoots));

            var reader = new LogicalReadoutSnapshotReader(client);
            var surveyRunner = new TokenReadoutSurveyRunner(client, reader);
            var offsets = new[] { 0, 1, 2, 5, 10, 22 };
            var trials = new List<TokenReadoutTrial>();
            foreach (var token in Enum.GetValues<GeoCoordinateToken>())
            {
                trials.Add(await surveyRunner.ExecuteAsync(sessionId, channels, token, candidates, offsets));
            }

            var summaries = offsets.Select(offset => new
            {
                offset,
                selected_count = ReadoutPopulationSelector.Select(trials, offset, inputRoots).Count,
                downstream_spiking_observations = trials.Count(trial => ReadoutResponseAnalysis.Metrics(trial.Baseline, trial.Observations[offset]).NonInputSpikingNeurons > 0),
                membrane_response_observations = trials.Count(trial => ReadoutResponseAnalysis.Metrics(trial.Baseline, trial.Observations[offset]).MembraneResponsiveNeurons > 0),
                maximum_absolute_membrane_delta = trials.Max(trial => ReadoutResponseAnalysis.Metrics(trial.Baseline, trial.Observations[offset]).MaximumAbsoluteMembraneDeltaMillivolts),
            }).ToArray();
            var selectedOffset = summaries
                .OrderByDescending(summary => summary.selected_count)
                .ThenByDescending(summary => summary.downstream_spiking_observations)
                .ThenByDescending(summary => summary.membrane_response_observations)
                .ThenByDescending(summary => summary.maximum_absolute_membrane_delta)
                .ThenBy(summary => summary.offset)
                .First().offset;
            var selectedRoots = ReadoutPopulationSelector.Select(trials, selectedOffset, inputRoots);

            output.WriteLine(JsonSerializer.Serialize(new
            {
                total_session_roots = allRoots.Count,
                excluded_input_roots = inputRoots.Count,
                candidate_roots = candidates.Length,
                summaries,
                selected_observation_offset = selectedOffset,
                selected_root_ids = selectedRoots,
                token_observations = trials.Select(trial => new
                {
                    token = trial.Token.ToString(),
                    observations = offsets.Select(offset => new
                    {
                        offset,
                        simulation_step = trial.Observations[offset].SimulationStep,
                        metrics = ReadoutResponseAnalysis.Metrics(trial.Baseline, trial.Observations[offset]),
                    }),
                }),
            }));

            if (selectedRoots.Count > 0)
            {
                var featureSurvey = new Dictionary<GeoCoordinateToken, IReadOnlyList<NeuralFeatureVector>>();
                foreach (var token in Enum.GetValues<GeoCoordinateToken>())
                {
                    var repetitions = new List<NeuralFeatureVector>();
                    for (var replay = 0; replay < 3; replay++)
                    {
                        var trial = await surveyRunner.ExecuteAsync(sessionId, channels, token, selectedRoots, [selectedOffset]);
                        repetitions.Add(NeuralFeatureExtractor.Extract(trial.Baseline, trial.Observations[selectedOffset], selectedRoots, selectedOffset));
                    }

                    featureSurvey[token] = repetitions;
                }

                output.WriteLine(JsonSerializer.Serialize(new
                {
                    feature_vector_length = selectedRoots.Count * NeuralFeatureExtractor.FeatureFields.Count,
                    separability = TokenFeatureSurveyAnalysis.Analyze(featureSurvey),
                }));
            }
        }
        finally
        {
            if (sessionId is not null)
            {
                await client.DeleteSessionAsync(sessionId);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task N35W54_ProducesAnOrderedReadoutFeatureSequenceWithoutExtraSettleSteps()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_BRAIN_SERVICE_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var channelsPath = Environment.GetEnvironmentVariable("TOPO_BRAIN_CHANNELS_PATH")
            ?? throw new InvalidOperationException("TOPO_BRAIN_CHANNELS_PATH is required for discovery.");
        var populationPath = Environment.GetEnvironmentVariable("TOPO_READOUT_POPULATION_PATH")
            ?? throw new InvalidOperationException("TOPO_READOUT_POPULATION_PATH is required for discovery.");
        var channels = BrainChannelConfiguration.LoadFromFile(channelsPath);
        var population = ReadoutPopulationConfiguration.LoadFromFile(populationPath);
        using var httpClient = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        var client = new BrainServiceClient(httpClient);
        string? sessionId = null;

        try
        {
            sessionId = (await client.CreateSessionAsync(new CreateBrainSessionRequest
            {
                ExecutionMode = BrainExecutionMode.Reference,
                TransmitterMode = BrainTransmitterMode.Unsigned,
            })).SessionId;
            await client.ResetSessionAsync(sessionId);
            var reader = new LogicalReadoutSnapshotReader(client);
            var plan = new CoordinateNeuralEncoder().Encode(GeoCoordinate.Parse("N35 W54"), channels);
            var before = await reader.ReadAsync(sessionId, population.RootIds);
            var vectors = new List<(GeoCoordinateToken Token, NeuralFeatureVector Vector)>();
            foreach (var entry in plan.Entries)
            {
                var stimulus = await client.StepAsync(sessionId, new BrainStepRequest
                {
                    Steps = entry.StimulusSteps,
                    Stimulus = entry.RootIds.Select(rootId => new BrainStimulus(rootId, entry.StimulusAmplitude)).ToArray(),
                });
                Assert.Equal("completed", stimulus.SafetyStatus);
                var settle = await client.StepAsync(sessionId, new BrainStepRequest
                {
                    Steps = entry.SettleSteps,
                    Stimulus = Array.Empty<BrainStimulus>(),
                });
                Assert.Equal("completed", settle.SafetyStatus);
                var after = await reader.ReadAsync(sessionId, population.RootIds);
                vectors.Add((entry.Token, NeuralFeatureExtractor.Extract(before, after, population.RootIds, population.ObservationOffsetSteps)));
                before = after;
            }

            Assert.Equal(7, vectors.Count);
            Assert.Equal(161, vectors[^1].Vector.SimulationStep);
            Assert.All(vectors, vector => Assert.Equal(384, vector.Vector.FeatureCount));
            output.WriteLine(JsonSerializer.Serialize(vectors.Select((vector, index) => new
            {
                index,
                token = vector.Token.ToString(),
                simulation_step = vector.Vector.SimulationStep,
                feature_l2_norm = Math.Sqrt(vector.Vector.Features.Sum(value => value * value)),
            })));
        }
        finally
        {
            if (sessionId is not null)
            {
                await client.DeleteSessionAsync(sessionId);
            }
        }
    }

    private static async Task<IReadOnlyList<long>> EnumerateAllRootsAsync(IBrainServiceClient client, string sessionId)
    {
        var roots = new List<long>();
        var offset = 0;
        while (true)
        {
            var page = await client.GetSessionRootsAsync(sessionId, offset, 256);
            roots.AddRange(page.RootIds);
            offset += page.ReturnedCount;
            if (offset >= page.TotalRootCount)
            {
                return roots;
            }
        }
    }
}
