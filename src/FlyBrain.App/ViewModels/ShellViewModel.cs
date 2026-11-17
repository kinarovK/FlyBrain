using System.Collections.ObjectModel;
using FlyBrain.BrainHost;
using FlyBrain.Modules;

namespace FlyBrain.App.ViewModels;

/// <summary>
/// The FlyBrain window: one brain on/off button, the module list and the active module.
/// Only the active module talks to the brain; switching modules releases its sessions.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogLines = 300;

    private readonly IBrainHost _host;
    private readonly IBrainSetup? _setup;
    private readonly Func<string, bool> _confirm;
    private CancellationTokenSource? _setupCancellation;
    private bool _isSetupRunning;
    private bool _isSetupPanelVisible;
    private readonly Action<Action> _dispatch;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private CancellationTokenSource? _startCancellation;
    private IBrainModule? _selectedModule;
    private BrainHostStatus _brainStatus;
    private bool _moduleKnowsBrainReady;
    private bool _isSwitching;
    private bool _isLogVisible;
    private bool _disposed;

    public ShellViewModel(
        IBrainHost host,
        IReadOnlyList<IBrainModule> modules,
        Action<Action>? dispatch = null,
        IBrainSetup? setup = null,
        Func<string, bool>? confirm = null)
    {
        _host = host;
        _setup = setup;
        _confirm = confirm ?? (_ => true);
        _dispatch = dispatch ?? (action => action());
        RecheckCommand = new AsyncCommand(() => { RefreshSetupChecks(); return Task.CompletedTask; }, () => !_isSetupRunning);
        CreatePythonCommand = new AsyncCommand(CreatePythonEnvironmentAsync, CanRunSetup);
        PrepareDataCommand = new AsyncCommand(PrepareBrainDataAsync, CanRunSetup);
        CancelSetupCommand = new AsyncCommand(() => { _setupCancellation?.Cancel(); return Task.CompletedTask; }, () => _isSetupRunning);
        Modules = new ObservableCollection<IBrainModule>(modules);
        _brainStatus = host.Status;
        ToggleBrainCommand = new AsyncCommand(ToggleBrainAsync, () => _brainStatus.State != BrainHostState.Stopping && !_disposed && !_isSetupRunning);
        foreach (var line in host.RecentLog)
        {
            LogLines.Add(line);
        }

        _host.StatusChanged += OnHostStatusChanged;
        _host.LogReceived += OnHostLogReceived;
    }

    public ObservableCollection<IBrainModule> Modules { get; }

    public ObservableCollection<string> LogLines { get; } = [];

    public AsyncCommand ToggleBrainCommand { get; }

    public AsyncCommand RecheckCommand { get; }

    public AsyncCommand CreatePythonCommand { get; }

    public AsyncCommand PrepareDataCommand { get; }

    public AsyncCommand CancelSetupCommand { get; }

    public ObservableCollection<SetupCheck> SetupChecks { get; } = [];

    public bool IsSetupNeeded => SetupChecks.Any(check => check.IsRequired && !check.IsOk);

    public string SetupSummary => IsSetupNeeded
        ? "Setup needed: " + string.Join(", ", SetupChecks.Where(c => c.IsRequired && !c.IsOk).Select(c => c.Name)) + "."
        : "Setup complete.";

    public bool IsSetupRunning
    {
        get => _isSetupRunning;
        private set
        {
            if (Set(ref _isSetupRunning, value))
            {
                RecheckCommand.RaiseCanExecuteChanged();
                CreatePythonCommand.RaiseCanExecuteChanged();
                PrepareDataCommand.RaiseCanExecuteChanged();
                CancelSetupCommand.RaiseCanExecuteChanged();
                ToggleBrainCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSetupPanelVisible
    {
        get => _isSetupPanelVisible;
        set => Set(ref _isSetupPanelVisible, value);
    }

    public IBrainModule? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (value is not null && !ReferenceEquals(value, _selectedModule))
            {
                _ = SelectModuleAsync(value);
            }
        }
    }

    public object? CurrentView => _selectedModule?.View;

    public string ActiveModuleTitle => _selectedModule is null ? string.Empty : $"/ {_selectedModule.DisplayName}";

    public BrainHostStatus BrainStatus => _brainStatus;

    public string BrainStateText => _brainStatus.State switch
    {
        BrainHostState.Stopped => "BRAIN OFF",
        BrainHostState.Starting => "STARTING",
        BrainHostState.Ready => _brainStatus.OwnsProcess ? "BRAIN ON" : "BRAIN ON (external)",
        BrainHostState.Stopping => "STOPPING",
        BrainHostState.Failed => "BRAIN ERROR",
        _ => _brainStatus.State.ToString().ToUpperInvariant(),
    };

    public string BrainMessage => _brainStatus.Message;

    public string BrainIndicatorColor => _brainStatus.State switch
    {
        BrainHostState.Ready => "#5CDB95",
        BrainHostState.Starting or BrainHostState.Stopping => "#F2C14E",
        BrainHostState.Failed => "#FF6B6B",
        _ => "#5B6B7C",
    };

    public string BrainButtonText => _brainStatus.State switch
    {
        BrainHostState.Ready => "STOP BRAIN",
        BrainHostState.Starting => "CANCEL START",
        BrainHostState.Stopping => "STOPPING...",
        _ => "START BRAIN",
    };

    public string BrainButtonColor => _brainStatus.State switch
    {
        BrainHostState.Ready => "#B5523B",
        BrainHostState.Starting => "#7A6A2B",
        _ => "#2B9CCB",
    };

    public bool IsBrainReady => _brainStatus.State == BrainHostState.Ready;

    public bool CanChangeBrainSettings => _brainStatus.State is BrainHostState.Stopped or BrainHostState.Failed;

    public bool CanSwitchModules => !_isSwitching;

    public bool EagerWholeDomain
    {
        get => _host.EagerWholeDomain;
        set
        {
            if (_host.EagerWholeDomain != value)
            {
                _host.EagerWholeDomain = value;
                Raise();
            }
        }
    }

    public bool IsLogVisible
    {
        get => _isLogVisible;
        set => Set(ref _isLogVisible, value);
    }

    public async Task InitializeAsync()
    {
        RefreshSetupChecks();
        IsSetupPanelVisible = IsSetupNeeded;
        if (_selectedModule is null && Modules.Count > 0)
        {
            await SelectModuleAsync(Modules[0]);
        }
    }

    public async Task SelectModuleAsync(IBrainModule module)
    {
        await _moduleGate.WaitAsync();
        try
        {
            if (ReferenceEquals(module, _selectedModule) || _disposed)
            {
                return;
            }

            SetSwitching(true);
            var previous = _selectedModule;
            if (previous is not null)
            {
                await RunModuleCallbackAsync(previous, "deactivate", () => previous.DeactivateAsync());
            }

            _selectedModule = module;
            _moduleKnowsBrainReady = false;
            Raise(nameof(SelectedModule));
            Raise(nameof(CurrentView));
            Raise(nameof(ActiveModuleTitle));
            await RunModuleCallbackAsync(module, "activate", () => module.ActivateAsync());
            if (IsBrainReady)
            {
                _moduleKnowsBrainReady = true;
                await RunModuleCallbackAsync(module, "brain ready", () => module.OnBrainAvailabilityChangedAsync(true));
            }
        }
        finally
        {
            SetSwitching(false);
            _moduleGate.Release();
        }
    }

    public async Task ToggleBrainAsync()
    {
        switch (_brainStatus.State)
        {
            case BrainHostState.Ready:
                await NotifyActiveModuleAsync(false);
                await _host.StopAsync();
                break;
            case BrainHostState.Starting:
                _startCancellation?.Cancel();
                break;
            case BrainHostState.Stopping:
                break;
            default:
                await StartBrainAsync();
                break;
        }
    }

    public void ReportProblem(string message) => AddLog(message);

    public void RefreshSetupChecks()
    {
        if (_setup is null)
        {
            return;
        }

        SetupChecks.Clear();
        foreach (var check in _setup.Check())
        {
            SetupChecks.Add(check);
        }

        Raise(nameof(IsSetupNeeded));
        Raise(nameof(SetupSummary));
    }

    public Task CreatePythonEnvironmentAsync() =>
        RunSetupAsync("Creating the Python environment", (setup, log, token) => setup.CreatePythonEnvironmentAsync(log, token));

    public Task PrepareBrainDataAsync()
    {
        const string question =
            "This downloads the public FlyWire FAFB v783 connectome (compact profile, several GB) and builds the brain graph. " +
            "It can take a long time.\n\nBy continuing you confirm that you have read and agree to the FlyWire data principles: " +
            BrainSetup.FlyWirePrinciplesUrl + "\n\nContinue?";
        return _confirm(question)
            ? RunSetupAsync("Preparing FlyWire brain data", (setup, log, token) => setup.PrepareBrainDataAsync(log, token))
            : Task.CompletedTask;
    }

    private bool CanRunSetup() =>
        _setup is not null && !_isSetupRunning && _brainStatus.State is BrainHostState.Stopped or BrainHostState.Failed;

    private async Task RunSetupAsync(string title, Func<IBrainSetup, IProgress<string>, CancellationToken, Task<bool>> action)
    {
        if (_setup is null || IsSetupRunning)
        {
            return;
        }

        _setupCancellation?.Dispose();
        _setupCancellation = new CancellationTokenSource();
        IsSetupRunning = true;
        IsLogVisible = true;
        AddLog($"{title}...");
        try
        {
            var log = new Progress<string>(line => _dispatch(() => AddLog(line)));
            var ok = await action(_setup, log, _setupCancellation.Token);
            AddLog(ok ? $"{title}: done." : $"{title}: failed - see the log.");
        }
        catch (OperationCanceledException)
        {
            AddLog($"{title}: cancelled.");
        }
        catch (Exception exception)
        {
            AddLog($"{title}: {exception.Message}");
        }
        finally
        {
            IsSetupRunning = false;
            RefreshSetupChecks();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _startCancellation?.Cancel();
        await _moduleGate.WaitAsync();
        try
        {
            _disposed = true;
            if (_selectedModule is not null)
            {
                var module = _selectedModule;
                await RunModuleCallbackAsync(module, "deactivate", () => module.DeactivateAsync());
            }

            foreach (var module in Modules)
            {
                try
                {
                    await module.DisposeAsync();
                }
                catch (Exception exception)
                {
                    AddLog($"{module.DisplayName} dispose failed: {exception.Message}");
                }
            }
        }
        finally
        {
            _moduleGate.Release();
        }

        _host.StatusChanged -= OnHostStatusChanged;
        _host.LogReceived -= OnHostLogReceived;
        await _host.DisposeAsync();
    }

    private async Task StartBrainAsync()
    {
        _startCancellation?.Dispose();
        _startCancellation = new CancellationTokenSource();
        try
        {
            var status = await _host.StartAsync(_startCancellation.Token);
            ApplyStatus(status);
            await NotifyActiveModuleAsync(status.IsReady);
        }
        catch (OperationCanceledException)
        {
            AddLog("Brain start cancelled.");
            ApplyStatus(_host.Status);
        }
    }

    private void OnHostStatusChanged(object? sender, BrainHostStatus status) =>
        _dispatch(() =>
        {
            if (_disposed)
            {
                return;
            }

            ApplyStatus(status);
            if (!status.IsReady && _moduleKnowsBrainReady)
            {
                // The brain went away (stopped, crashed, failed): let the module release its sessions.
                _ = NotifyActiveModuleAsync(false);
            }
        });

    private void OnHostLogReceived(object? sender, string line) => _dispatch(() => AddLog(line));

    private void ApplyStatus(BrainHostStatus status)
    {
        _brainStatus = status;
        Raise(nameof(BrainStatus));
        Raise(nameof(BrainStateText));
        Raise(nameof(BrainMessage));
        Raise(nameof(BrainIndicatorColor));
        Raise(nameof(BrainButtonText));
        Raise(nameof(BrainButtonColor));
        Raise(nameof(IsBrainReady));
        Raise(nameof(CanChangeBrainSettings));
        ToggleBrainCommand.RaiseCanExecuteChanged();
        CreatePythonCommand.RaiseCanExecuteChanged();
        PrepareDataCommand.RaiseCanExecuteChanged();
    }

    private async Task NotifyActiveModuleAsync(bool isReady)
    {
        await _moduleGate.WaitAsync();
        try
        {
            var module = _selectedModule;
            if (module is null || _disposed || _moduleKnowsBrainReady == isReady)
            {
                return;
            }

            _moduleKnowsBrainReady = isReady;
            await RunModuleCallbackAsync(module, isReady ? "brain ready" : "brain stopped", () => module.OnBrainAvailabilityChangedAsync(isReady));
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    private async Task RunModuleCallbackAsync(IBrainModule module, string step, Func<Task> callback)
    {
        try
        {
            await callback();
        }
        catch (Exception exception)
        {
            AddLog($"{module.DisplayName}: {step} failed: {exception.Message}");
        }
    }

    private void SetSwitching(bool value)
    {
        if (_isSwitching != value)
        {
            _isSwitching = value;
            Raise(nameof(CanSwitchModules));
        }
    }

    private void AddLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
    }
}
