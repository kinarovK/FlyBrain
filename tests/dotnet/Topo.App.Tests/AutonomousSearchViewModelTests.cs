using Topo.App.Services;
using Topo.App.ViewModels;
using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.App.Tests;

public sealed class AutonomousSearchViewModelTests
{
    [Fact]
    public async Task SearchMode_StartsTargetOnlySearchAndUpdatesCurrentBestAndTrail()
    {
        var search = new FakeSearchService(); var viewModel = new MainViewModel(new FakeDecoder(), search) { CoordinateText = "N35 W54" };
        await viewModel.SetSearchModeForTestAsync(); await viewModel.StartSearchForTestAsync(); await Task.Delay(25);
        Assert.Equal(ApplicationMode.AutonomousSearch, viewModel.ApplicationMode); Assert.Equal(1, search.Calls); Assert.Equal("N35 W54", search.Target.ToString());
        Assert.Equal("N35 W54", viewModel.CurrentDecoded); Assert.Equal("N35 W54", viewModel.BestDecoded); Assert.Equal("EXACTSUCCESS", viewModel.SearchStatus); Assert.Single(viewModel.SearchTrail);
    }

    [Fact]
    public async Task SearchReset_ClearsBoundedPresentationAndDecodeModeRemainsAvailable()
    {
        var viewModel = new MainViewModel(new FakeDecoder(), new FakeSearchService(many: true)) { CoordinateText = "N35 W54" };
        await viewModel.SetSearchModeForTestAsync(); await viewModel.StartSearchForTestAsync(); await Task.Delay(25);
        Assert.Equal(100, viewModel.SearchHistory.Count); Assert.Equal(100, viewModel.SearchTrail.Count);
        await viewModel.ResetSearchForTestAsync(); Assert.Empty(viewModel.SearchHistory); Assert.Empty(viewModel.SearchTrail); Assert.Equal("IDLE", viewModel.SearchStatus);
        await viewModel.SetDecodeModeForTestAsync(); Assert.Equal(ApplicationMode.NeuralDecode, viewModel.ApplicationMode);
    }

    private sealed class FakeDecoder : IInteractiveDecodeService
    {
        public Task<InteractiveBrainStatus> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(new InteractiveBrainStatus("READY", "test", 912, "0.1"));
        public Task<CoordinateNeuralDecode> DecodeAsync(GeoCoordinate source, IProgress<CoordinateDecodeProgress> progress, CancellationToken cancellationToken = default) => Task.FromResult(new CoordinateNeuralDecode(source, source.ToTokens(), [], CoordinateSequenceDecoder.Decode(source.ToTokens())));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSearchService(bool many = false) : IInteractiveSearchService
    {
        public int Calls { get; private set; } public GeoCoordinate Target { get; private set; }
        public Task<SearchResult> RunAsync(GeoCoordinate target, SearchSettings settings, bool useRandomBaseline, IProgress<SearchIteration>? progress = null, IProgress<CoordinateDecodeProgress>? neuralProgress = null, CancellationToken cancellationToken = default)
        {
            Calls++; Target = target; var trace = new List<SearchIteration>(); var count = many ? 101 : 1;
            for (var index = 1; index <= count; index++) { var record = new SearchIteration(index, target, target, 0, 1, true, target, 1, 0, 1, false, TimeSpan.Zero); trace.Add(record); progress?.Report(record); }
            return Task.FromResult(new SearchResult(target, SearchTerminationReason.ExactSuccess, trace, trace.TakeLast(100).ToArray(), target, 1, 0, TimeSpan.Zero));
        }
    }
}
