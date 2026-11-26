using FlyBrain.BrainClient;
using FlyBrain.Modules;
using Topo.Module.Services;
using Topo.Module.ViewModels;

namespace Topo.Module;

/// <summary>Topo: decode integer coordinates through the connectome and search for hidden targets.</summary>
public sealed class TopoModule : IBrainModule
{
    private readonly InteractiveDecodeService _decoder;
    private readonly MainViewModel _viewModel;
    private TopoView? _view;

    public TopoModule(IBrainServiceClient client)
    {
        _decoder = new InteractiveDecodeService(client);
        _viewModel = new MainViewModel(_decoder);
    }

    public string Id => "topo";

    public string DisplayName => "Topo";

    public string Description => "Coordinates through the connectome, map search";

    public object View => _view ??= new TopoView(_viewModel);

    public MainViewModel ViewModel => _viewModel;

    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        _viewModel.SetBrainOffline("Module inactive; brain session released.");
        await _decoder.ReleaseSessionAsync();
    }

    public async Task OnBrainAvailabilityChangedAsync(bool isReady, CancellationToken cancellationToken = default)
    {
        if (isReady)
        {
            await _viewModel.InitializeAsync();
            return;
        }

        _viewModel.SetBrainOffline("Brain is off.");
        await _decoder.ReleaseSessionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _viewModel.CancelRunningOperation();
        await _decoder.DisposeAsync();
    }
}
