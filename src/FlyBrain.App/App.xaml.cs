using System.Net.Http;
using System.Windows;
using FlyBrain.App.ViewModels;
using FlyBrain.BrainClient;
using FlyBrain.BrainHost;
using FlyBrain.Common;

namespace FlyBrain.App;

public partial class App : Application
{
    public const string AutoStartEnvironmentVariable = "FLYBRAIN_AUTOSTART";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var clientOptions = BrainServiceClientOptions.FromEnvironment();
        var repositoryRoot = RepositoryLocator.TryFindRoot();
        var hostRoot = repositoryRoot ?? AppContext.BaseDirectory;
        var hostOptions = new BrainHostOptions
        {
            RepositoryRoot = hostRoot,
            PythonExecutable = BrainHostOptions.DefaultPythonExecutable(hostRoot),
            BaseAddress = clientOptions.BaseAddress,
            EagerWholeDomain = IsEnabled(Environment.GetEnvironmentVariable("FLYBRAIN_EAGER_WHOLE")),
        };
        var host = BrainProcessHost.Create(hostOptions);

        var httpClient = new HttpClient { BaseAddress = clientOptions.BaseAddress, Timeout = TimeSpan.FromMinutes(5) };
        var client = new BrainServiceClient(httpClient);
        var modules = ModuleCatalog.Create(client, repositoryRoot);

        var shell = new ShellViewModel(host, modules, action => Dispatcher.BeginInvoke(action));
        var window = new MainWindow(shell);
        MainWindow = window;
        window.Show();

        await shell.InitializeAsync();
        if (repositoryRoot is null)
        {
            shell.ReportProblem("FlyBrain repository root was not found. Run the app from the repository or set FLYBRAIN_ROOT.");
        }

        if (e.Args.Contains("--start-brain", StringComparer.OrdinalIgnoreCase) ||
            IsEnabled(Environment.GetEnvironmentVariable(AutoStartEnvironmentVariable)))
        {
            await shell.ToggleBrainAsync();
        }
    }

    private static bool IsEnabled(string? value) =>
        value is not null && (value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
