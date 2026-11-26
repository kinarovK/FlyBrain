using System.IO;
using FlyBrain.BrainClient;
using FlyBrain.Modules;
using Trader.Brain;
using Trader.Market;
using Trader.Module.ViewModels;

namespace Trader.Module;

/// <summary>Trader: the fly paper-trades an offline BTC/USDT replay.</summary>
public sealed class TraderModule : IBrainModule
{
    private readonly TraderBrainSession _brain;
    private readonly TraderViewModel _viewModel;
    private readonly string? _configurationProblem;
    private TraderView? _view;
    private bool _dataLoadAttempted;

    public TraderModule(IBrainServiceClient client, string? repositoryRoot)
    {
        var configuration = new TraderBrainConfiguration();
        if (repositoryRoot is not null)
        {
            var path = TraderBrainConfiguration.DefaultPath(repositoryRoot);
            try
            {
                if (File.Exists(path))
                {
                    configuration = TraderBrainConfiguration.LoadFromFile(path);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                _configurationProblem = $"Invalid {path}: {exception.Message}. Using built-in defaults.";
            }
        }

        var dataDirectory = repositoryRoot is not null
            ? BinanceKlineLoader.DefaultDirectory(repositoryRoot)
            : Path.Combine(AppContext.BaseDirectory, "data", "market", "binance");
        _brain = new TraderBrainSession(client, configuration);
        _viewModel = new TraderViewModel(_brain, () => new BinanceKlineLoader().LoadDirectory(dataDirectory), dataDirectory);
    }

    public string Id => "trader";

    public string DisplayName => "Trader";

    public string Description => "The fly paper-trades BTC/USDT (2024 replay)";

    public object View => _view ??= new TraderView(_viewModel);

    public TraderViewModel ViewModel => _viewModel;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_dataLoadAttempted)
        {
            return;
        }

        _dataLoadAttempted = true;
        if (_configurationProblem is not null)
        {
            _viewModel.Log.Insert(0, _configurationProblem);
        }

        await _viewModel.LoadDataAsync();
    }

    public Task DeactivateAsync(CancellationToken cancellationToken = default) =>
        _viewModel.ReleaseBrainAsync("Module inactive; brain session released.");

    public Task OnBrainAvailabilityChangedAsync(bool isReady, CancellationToken cancellationToken = default) =>
        isReady ? _viewModel.ConnectBrainAsync() : _viewModel.ReleaseBrainAsync("Brain is off.");

    public async ValueTask DisposeAsync()
    {
        await _viewModel.DisposeAsync();
        await _brain.DisposeAsync();
    }
}
