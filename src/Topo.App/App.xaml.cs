using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using FlyBrain.BrainClient;
using Topo.App.ViewModels;
using Topo.App.Services;

namespace Topo.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddSingleton(BrainServiceClientOptions.FromEnvironment());
        services.AddHttpClient<IBrainServiceClient, BrainServiceClient>((provider, client) =>
        {
            client.BaseAddress = provider
                .GetRequiredService<BrainServiceClientOptions>()
                .BaseAddress;
        });
        services.AddSingleton<IInteractiveDecodeService, InteractiveDecodeService>();
        services.AddSingleton<IInteractiveSearchService, AutonomousSearchService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
        _ = _services.GetRequiredService<MainViewModel>().InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<IInteractiveDecodeService>()?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _services?.Dispose();
        base.OnExit(e);
    }
}
