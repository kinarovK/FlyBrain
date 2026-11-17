using FlyBrain.App.ViewModels;
using FlyBrain.BrainHost;
using FlyBrain.Modules;

namespace FlyBrain.App.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public async Task Initialize_activates_first_module()
    {
        var first = new FakeModule("a");
        var shell = new ShellViewModel(new FakeHost(), [first, new FakeModule("b")]);

        await shell.InitializeAsync();

        Assert.Same(first, shell.SelectedModule);
        Assert.Equal(new[] { "activate" }, first.Calls);
        Assert.Same(first.View, shell.CurrentView);
    }

    [Fact]
    public async Task Start_brain_notifies_active_module_when_ready()
    {
        var module = new FakeModule("a");
        var shell = new ShellViewModel(new FakeHost(), [module]);
        await shell.InitializeAsync();

        await shell.ToggleBrainAsync();

        Assert.True(shell.IsBrainReady);
        Assert.Equal("STOP BRAIN", shell.BrainButtonText);
        Assert.Equal(new[] { "activate", "ready:True" }, module.Calls);
    }

    [Fact]
    public async Task Stop_brain_releases_module_before_stopping_host()
    {
        var host = new FakeHost();
        var module = new FakeModule("a") { OnCall = call => { if (call == "ready:False") Assert.Equal(BrainHostState.Ready, host.Status.State); } };
        var shell = new ShellViewModel(host, [module]);
        await shell.InitializeAsync();
        await shell.ToggleBrainAsync();

        await shell.ToggleBrainAsync();

        Assert.Equal(BrainHostState.Stopped, host.Status.State);
        Assert.Equal(new[] { "activate", "ready:True", "ready:False" }, module.Calls);
        Assert.Equal("START BRAIN", shell.BrainButtonText);
    }

    [Fact]
    public async Task Switching_module_deactivates_previous_and_tells_new_one_brain_is_ready()
    {
        var first = new FakeModule("a");
        var second = new FakeModule("b");
        var shell = new ShellViewModel(new FakeHost(), [first, second]);
        await shell.InitializeAsync();
        await shell.ToggleBrainAsync();

        await shell.SelectModuleAsync(second);

        Assert.Equal(new[] { "activate", "ready:True", "deactivate" }, first.Calls);
        Assert.Equal(new[] { "activate", "ready:True" }, second.Calls);
        Assert.Same(second.View, shell.CurrentView);
    }

    [Fact]
    public async Task Brain_failure_is_shown_and_module_is_told_brain_is_gone()
    {
        var host = new FakeHost();
        var module = new FakeModule("a");
        var shell = new ShellViewModel(host, [module]);
        await shell.InitializeAsync();
        await shell.ToggleBrainAsync();

        host.Raise(new BrainHostStatus(BrainHostState.Failed, "The brain process exited unexpectedly.", false));
        await Task.Yield();

        Assert.Equal("BRAIN ERROR", shell.BrainStateText);
        Assert.Equal("START BRAIN", shell.BrainButtonText);
        Assert.Contains("ready:False", module.Calls);
    }

    [Fact]
    public async Task Failed_start_keeps_module_offline()
    {
        var module = new FakeModule("a");
        var shell = new ShellViewModel(new FakeHost { FailStart = true }, [module]);
        await shell.InitializeAsync();

        await shell.ToggleBrainAsync();

        Assert.False(shell.IsBrainReady);
        Assert.Equal(new[] { "activate" }, module.Calls);
        Assert.True(shell.CanChangeBrainSettings);
    }

    [Fact]
    public async Task Dispose_deactivates_disposes_modules_and_host()
    {
        var host = new FakeHost();
        var module = new FakeModule("a");
        var shell = new ShellViewModel(host, [module]);
        await shell.InitializeAsync();
        await shell.ToggleBrainAsync();

        await shell.DisposeAsync();

        Assert.Contains("deactivate", module.Calls);
        Assert.True(module.Disposed);
        Assert.True(host.Disposed);
    }

    [Fact]
    public void Eager_whole_domain_setting_is_forwarded_to_host()
    {
        var host = new FakeHost();
        var shell = new ShellViewModel(host, []);

        shell.EagerWholeDomain = true;

        Assert.True(host.EagerWholeDomain);
    }

    [Fact]
    public async Task Missing_setup_opens_the_setup_panel_and_setup_rechecks_afterwards()
    {
        var setup = new FakeSetup();
        var shell = new ShellViewModel(new FakeHost(), [new FakeModule("a")], setup: setup);

        await shell.InitializeAsync();

        Assert.True(shell.IsSetupNeeded);
        Assert.True(shell.IsSetupPanelVisible);
        Assert.Contains("Python environment", shell.SetupSummary);

        await shell.CreatePythonEnvironmentAsync();

        Assert.Equal(1, setup.CreateCalls);
        Assert.False(shell.IsSetupNeeded);
        Assert.False(shell.IsSetupRunning);
    }

    [Fact]
    public async Task Brain_data_download_requires_confirmation()
    {
        var setup = new FakeSetup();
        var shell = new ShellViewModel(new FakeHost(), [], setup: setup, confirm: _ => false);

        await shell.PrepareBrainDataAsync();

        Assert.Equal(0, setup.PrepareCalls);
    }

    private sealed class FakeSetup : IBrainSetup
    {
        private bool _pythonReady;

        public int CreateCalls { get; private set; }

        public int PrepareCalls { get; private set; }

        public IReadOnlyList<SetupCheck> Check() =>
        [
            new SetupCheck("Python environment", _pythonReady, "test"),
            new SetupCheck("Trader market data", false, "optional", IsRequired: false),
        ];

        public Task<bool> CreatePythonEnvironmentAsync(IProgress<string> log, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            _pythonReady = true;
            return Task.FromResult(true);
        }

        public Task<bool> PrepareBrainDataAsync(IProgress<string> log, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeHost : IBrainHost
    {
        public bool FailStart { get; init; }

        public bool Disposed { get; private set; }

        public BrainHostStatus Status { get; private set; } = BrainHostStatus.Stopped;

        public Uri BaseAddress { get; } = new("http://127.0.0.1:8765");

        public bool EagerWholeDomain { get; set; }

        public IReadOnlyList<string> RecentLog => [];

        public event EventHandler<BrainHostStatus>? StatusChanged;

#pragma warning disable CS0067
        public event EventHandler<string>? LogReceived;
#pragma warning restore CS0067

        public Task<BrainHostStatus> StartAsync(CancellationToken cancellationToken = default)
        {
            Raise(new BrainHostStatus(BrainHostState.Starting, "Loading", true));
            Raise(FailStart
                ? new BrainHostStatus(BrainHostState.Failed, "Python environment was not found.", false)
                : new BrainHostStatus(BrainHostState.Ready, "Brain is ready.", true));
            return Task.FromResult(Status);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Raise(BrainHostStatus.Stopped);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public void Raise(BrainHostStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    private sealed class FakeModule(string id) : IBrainModule
    {
        public List<string> Calls { get; } = [];

        public Action<string>? OnCall { get; init; }

        public bool Disposed { get; private set; }

        public string Id => id;

        public string DisplayName => id;

        public string Description => id;

        public object View { get; } = new object();

        public Task ActivateAsync(CancellationToken cancellationToken = default) => Record("activate");

        public Task DeactivateAsync(CancellationToken cancellationToken = default) => Record("deactivate");

        public Task OnBrainAvailabilityChangedAsync(bool isReady, CancellationToken cancellationToken = default) => Record($"ready:{isReady}");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private Task Record(string call)
        {
            OnCall?.Invoke(call);
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }
}
