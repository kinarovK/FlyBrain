using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FlyBrain.BrainHost;

/// <summary>
/// Starts <c>python -m brain_service.server</c> from the repository virtual environment,
/// waits until <c>/health/ready</c> reports ready and stops the process again.
/// </summary>
public sealed class BrainProcessHost : IBrainHost
{
    private readonly BrainHostOptions _options;
    private readonly IBrainReadinessProbe _probe;
    private readonly IBrainProcessLauncher _launcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _log = new();
    private readonly object _logLock = new();
    private IRunningBrainProcess? _process;
    private volatile bool _stopRequested;
    private BrainHostStatus _status = BrainHostStatus.Stopped;

    public BrainProcessHost(BrainHostOptions options, IBrainReadinessProbe probe, IBrainProcessLauncher launcher)
    {
        _options = options;
        _probe = probe;
        _launcher = launcher;
    }

    public static BrainProcessHost Create(BrainHostOptions options)
    {
        var httpClient = new HttpClient { BaseAddress = options.BaseAddress };
        return new BrainProcessHost(options, new HttpBrainReadinessProbe(httpClient), new BrainProcessLauncher());
    }

    public event EventHandler<BrainHostStatus>? StatusChanged;

    public event EventHandler<string>? LogReceived;

    public BrainHostStatus Status => _status;

    public Uri BaseAddress => _options.BaseAddress;

    public bool EagerWholeDomain
    {
        get => _options.EagerWholeDomain;
        set => _options.EagerWholeDomain = value;
    }

    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (_logLock)
            {
                return _log.ToArray();
            }
        }
    }

    public async Task<BrainHostStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_status.State == BrainHostState.Ready)
            {
                return _status;
            }

            _stopRequested = false;
            SetStatus(BrainHostState.Starting, "Checking for a running brain...", ownsProcess: false);
            var existing = await _probe.ProbeAsync(cancellationToken);
            switch (existing.Outcome)
            {
                case ProbeOutcome.Ready:
                    AppendLog($"Attached to a running brain service at {_options.BaseAddress}.");
                    return SetStatus(BrainHostState.Ready, "Connected to an already running brain.", ownsProcess: false);
                case ProbeOutcome.Starting:
                    AppendLog("A brain service is already starting; waiting for it.");
                    return await WaitForReadyAsync(ownsProcess: false, cancellationToken);
                case ProbeOutcome.Failed:
                    return SetStatus(BrainHostState.Failed, existing.Detail, ownsProcess: false);
            }

            var problem = ValidateEnvironment();
            if (problem is not null)
            {
                AppendLog(problem);
                return SetStatus(BrainHostState.Failed, problem, ownsProcess: false);
            }

            var process = _launcher.Launch(CreateStartInfo());
            process.OutputReceived += (_, line) => AppendLog(line);
            _process = process;
            _ = MonitorExitAsync(process);
            AppendLog($"Started brain service: {_options.PythonExecutable} -m brain_service.server");
            SetStatus(BrainHostState.Starting, "Loading the connectome...", ownsProcess: true);
            return await WaitForReadyAsync(ownsProcess: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await StopOwnedProcessAsync();
            SetStatus(BrainHostState.Stopped, "Start cancelled.", ownsProcess: false);
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await StopOwnedProcessAsync();
            AppendLog(exception.Message);
            return SetStatus(BrainHostState.Failed, $"The brain could not be started: {exception.Message}", ownsProcess: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is not null)
            {
                SetStatus(BrainHostState.Stopping, "Stopping the brain...", ownsProcess: true);
                await StopOwnedProcessAsync();
                AppendLog("Brain service stopped.");
            }
            else if (_status.State == BrainHostState.Ready)
            {
                AppendLog("Detached from the externally started brain service (it keeps running).");
            }

            SetStatus(BrainHostState.Stopped, "Brain is off.", ownsProcess: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            _gate.Dispose();
        }
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            WorkingDirectory = _options.RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("brain_service.server");
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["FLYTRADER_BRAIN_HOST"] = _options.BaseAddress.Host;
        startInfo.Environment["FLYTRADER_BRAIN_PORT"] = _options.BaseAddress.Port.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["FLYBRAIN_EAGER_WHOLE"] = _options.EagerWholeDomain ? "1" : "0";
        return startInfo;
    }

    internal string? ValidateEnvironment()
    {
        if (!File.Exists(_options.ServerScriptPath))
        {
            return $"Brain service sources were not found: {_options.ServerScriptPath}";
        }

        if (!File.Exists(_options.PythonExecutable))
        {
            return $"Python environment was not found: {_options.PythonExecutable}. Run the environment setup first.";
        }

        return null;
    }

    private async Task<BrainHostStatus> WaitForReadyAsync(bool ownsProcess, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastDetail = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownsProcess && _process is { HasExited: true } exited)
            {
                var code = exited.ExitCode;
                await StopOwnedProcessAsync();
                return SetStatus(
                    BrainHostState.Failed,
                    $"The brain process exited during startup (exit code {code?.ToString(CultureInfo.InvariantCulture) ?? "?"}). {LastLogLine()}".Trim(),
                    ownsProcess: false);
            }

            var probe = await _probe.ProbeAsync(cancellationToken);
            switch (probe.Outcome)
            {
                case ProbeOutcome.Ready:
                    AppendLog($"Brain ready after {stopwatch.Elapsed.TotalSeconds:F1} s.");
                    return SetStatus(BrainHostState.Ready, ownsProcess ? "Brain is ready." : "Connected to an already running brain.", ownsProcess);
                case ProbeOutcome.Failed:
                    if (ownsProcess)
                    {
                        await StopOwnedProcessAsync();
                    }

                    return SetStatus(BrainHostState.Failed, probe.Detail, ownsProcess: false);
                case ProbeOutcome.Unreachable when !ownsProcess:
                    return SetStatus(BrainHostState.Failed, "The running brain service stopped responding.", ownsProcess: false);
                case ProbeOutcome.Starting when probe.Detail != lastDetail:
                    lastDetail = probe.Detail;
                    SetStatus(BrainHostState.Starting, "Loading the connectome...", ownsProcess);
                    break;
            }

            if (stopwatch.Elapsed > _options.StartupTimeout)
            {
                if (ownsProcess)
                {
                    await StopOwnedProcessAsync();
                }

                return SetStatus(
                    BrainHostState.Failed,
                    $"The brain did not become ready within {_options.StartupTimeout.TotalMinutes:F0} minutes.",
                    ownsProcess: false);
            }

            await Task.Delay(_options.PollInterval, cancellationToken);
        }
    }

    private async Task MonitorExitAsync(IRunningBrainProcess process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            return;
        }

        if (_stopRequested || !ReferenceEquals(process, _process))
        {
            return;
        }

        if (_status.State == BrainHostState.Ready)
        {
            AppendLog($"Brain process exited unexpectedly (exit code {process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}).");
            SetStatus(BrainHostState.Failed, "The brain process exited unexpectedly.", ownsProcess: false);
        }
    }

    private async Task StopOwnedProcessAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        _stopRequested = true;
        _process = null;
        try
        {
            process.Kill();
            using var timeout = new CancellationTokenSource(_options.StopTimeout);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("The brain process did not exit in time.");
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private BrainHostStatus SetStatus(BrainHostState state, string message, bool ownsProcess)
    {
        var status = new BrainHostStatus(state, message, ownsProcess);
        _status = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            _log.Enqueue(line);
            while (_log.Count > _options.MaxLogLines)
            {
                _log.Dequeue();
            }
        }

        LogReceived?.Invoke(this, line);
    }

    private string LastLogLine()
    {
        lock (_logLock)
        {
            return _log.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
        }
    }
}
