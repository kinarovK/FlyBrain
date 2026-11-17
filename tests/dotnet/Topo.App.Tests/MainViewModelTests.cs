using Topo.App.Services;
using Topo.App.ViewModels;
using FlyBrain.BrainClient;
using Topo.Core;
using Topo.Training;
using System.Net;
using Xunit;

namespace Topo.App.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task InvalidInput_DoesNotCallDecode()
    {
        var fake = new FakeDecoder(); var viewModel = new MainViewModel(fake) { CoordinateText = "bad" };
        await viewModel.DecodeForTestAsync();
        Assert.Equal(0, fake.DecodeCalls); Assert.NotEmpty(viewModel.Error);
    }

    [Fact]
    public async Task SuccessfulDecode_UpdatesResultAndOrderedProgress()
    {
        var fake = new FakeDecoder(); var viewModel = new MainViewModel(fake) { CoordinateText = "N0 E0" };
        await viewModel.InitializeAsync(); await viewModel.DecodeForTestAsync();
        Assert.Equal("N0 E0", viewModel.Decoded); Assert.Equal(1d, viewModel.Reward); Assert.All(viewModel.Tokens, token => Assert.Equal("COMPLETE", token.State)); Assert.Equal(new[] { "N", "0", "|", "E", "0" }, viewModel.Tokens.Select(token => token.Expected));
    }

    [Fact]
    public async Task ServiceError_IsPresentedAsUiState()
    {
        var fake = new FakeDecoder { ThrowOnConnect = true }; var viewModel = new MainViewModel(fake);
        await viewModel.InitializeAsync();
        Assert.Equal("OFFLINE", viewModel.BrainState); Assert.NotEmpty(viewModel.Error);
    }

    [Fact]
    public async Task RepeatedDecode_ClearsPriorRunState()
    {
        var fake = new FakeDecoder(); var viewModel = new MainViewModel(fake) { CoordinateText = "N0 E0" };
        await viewModel.InitializeAsync(); await viewModel.DecodeForTestAsync(); viewModel.CoordinateText = "N1 E1"; await viewModel.DecodeForTestAsync();
        Assert.Equal(2, fake.DecodeCalls); Assert.Equal("N1 E1", viewModel.Target); Assert.Equal(5, viewModel.Tokens.Count);
    }

    [Fact]
    public async Task Cancellation_RestoresIdleState()
    {
        var fake = new FakeDecoder { WaitForCancellation = true }; var viewModel = new MainViewModel(fake) { CoordinateText = "N0 E0" };
        await viewModel.InitializeAsync(); var run = viewModel.DecodeForTestAsync();
        await Task.Delay(10); viewModel.CancelCommand.Execute(null); await run;
        Assert.False(viewModel.IsRunning); Assert.Contains("cancelled", viewModel.Log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionBusy_IsPresentedAsConciseUiState()
    {
        var fake = new FakeDecoder { DecodeException = new BrainServiceException(HttpStatusCode.Conflict, new BrainServiceError { Code = "SESSION_BUSY", Message = "session lock held" }) };
        var viewModel = new MainViewModel(fake) { CoordinateText = "N0 E0" };
        await viewModel.InitializeAsync(); await viewModel.DecodeForTestAsync();
        Assert.Equal("Brain session is busy. Wait for the current operation or retry.", viewModel.Error);
        Assert.Equal("ERROR", viewModel.BrainState);
        Assert.Contains("session lock held", viewModel.Log);
    }

    private sealed class FakeDecoder : IInteractiveDecodeService
    {
        public bool ThrowOnConnect { get; init; } public bool WaitForCancellation { get; init; } public Exception? DecodeException { get; init; } public int DecodeCalls { get; private set; }
        public Task<InteractiveBrainStatus> ConnectAsync(CancellationToken cancellationToken = default) => ThrowOnConnect ? Task.FromException<InteractiveBrainStatus>(new InvalidOperationException("model mismatch")) : Task.FromResult(new InteractiveBrainStatus("READY", "test", 912, "0.1.0"));
        public Task<CoordinateNeuralDecode> DecodeAsync(GeoCoordinate source, IProgress<CoordinateDecodeProgress> progress, CancellationToken cancellationToken = default)
        {
            DecodeCalls++; if (DecodeException is not null) return Task.FromException<CoordinateNeuralDecode>(DecodeException); var tokens = source.ToTokens(); var predictions = new List<CoordinateTokenPrediction>();
            if (WaitForCancellation) return WaitAsync(cancellationToken);
            for (var index = 0; index < tokens.Count; index++) { progress.Report(new(CoordinateDecodeProgressStage.TokenStarted, index, tokens[index], null, null, index * 23, 0, 0, 0, "running")); progress.Report(new(CoordinateDecodeProgressStage.TokenPredicted, index, tokens[index], tokens[index], .99, (index + 1) * 23, .1, 0, 0, "completed")); predictions.Add(new(index, tokens[index], .99, (index + 1) * 23, 384, 22)); }
            return Task.FromResult(new CoordinateNeuralDecode(source, tokens, predictions, CoordinateSequenceDecoder.Decode(tokens)));
        }
        private static async Task<CoordinateNeuralDecode> WaitAsync(CancellationToken cancellationToken) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
