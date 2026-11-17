using Topo.App.Services;
using System.IO;
using System.Net.Http;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.App.Tests;

public sealed class InteractiveDecodeServiceIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FrozenInteractiveService_DecodesRequiredSmokeCoordinates()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_UI_SMOKE"), "1", StringComparison.Ordinal)) return;
        using var http = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        await using var service = new InteractiveDecodeService(new BrainServiceClient(http));
        var status = await service.ConnectAsync();
        Assert.Equal("READY", status.State);

        foreach (var text in new[] { "N35 W54", "N47 E22", "S23 E117" })
        {
            var progress = new List<CoordinateDecodeProgress>();
            var source = GeoCoordinate.Parse(text);
            var result = await service.DecodeAsync(source, new Progress<CoordinateDecodeProgress>(progress.Add));
            Assert.True(result.Decoding.IsSuccess);
            Assert.Equal(source, result.Decoding.Coordinate);
            Assert.Equal(source.ToTokens().Count * 3, progress.Count);
            Assert.Equal(23 * source.ToTokens().Count, result.Predictions[^1].SimulationStep);
        }
    }

    [Fact]
    public async Task FrozenInteractiveService_ProducesRequiredAutonomousSearchTraces()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_UI_SMOKE"), "1", StringComparison.Ordinal)) return;
        using var httpClient = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        var client = new BrainServiceClient(httpClient);
        await using var decoder = new InteractiveDecodeService(client);
        var status = await decoder.ConnectAsync(); Assert.Equal("READY", status.State);
        var search = new AutonomousSearchService(decoder);
        foreach (var text in new[] { "N35 W54", "N47 E22", "S23 E117" })
        {
            var result = await search.RunAsync(GeoCoordinate.Parse(text), new SearchSettings(20260924, MaximumIterations: 2), false);
            Assert.InRange(result.Trace.Count, 1, 2);
            Assert.All(result.Trace, trace => Assert.NotNull(trace.DecodedCoordinate));
        }
    }

    [Fact]
    [Trait("Category", "LongRunningIntegration")]
    public async Task FrozenInteractiveService_PersistsFiftyTargetPolicyAndBaselineExperiment()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOPO_RUN_PHASE9_EXPERIMENT"), "1", StringComparison.Ordinal)) return;
        var repository = FlyBrain.Common.RepositoryLocator.FindRoot();
        using var http = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        await using var decoder = new InteractiveDecodeService(new BrainServiceClient(http));
        Assert.Equal("READY", (await decoder.ConnectAsync()).State);
        var settings = new SearchSettings(); var targets = AutonomousSearchEvaluationCorpus.Create(50, settings.Seed);
        var comparison = await new AutonomousSearchExperimentRunner().RunAsync(targets, decoder, settings);
        var directory = await new AutonomousSearchExperimentRunner().PersistAsync(comparison, targets, settings, Path.Combine(repository, "modules", "topo", "experiments", "autonomous-search-v1"));
        Assert.Equal(50, comparison.HillClimbing.TargetCount); Assert.Equal(50, comparison.RandomBaseline.TargetCount); Assert.True(Directory.Exists(directory));
    }
}
