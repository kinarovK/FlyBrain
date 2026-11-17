using System.Diagnostics;

namespace FlyBrain.BrainHost;

public interface IRunningBrainProcess : IDisposable
{
    bool HasExited { get; }

    int? ExitCode { get; }

    event EventHandler<string>? OutputReceived;

    void Kill();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IBrainProcessLauncher
{
    IRunningBrainProcess Launch(ProcessStartInfo startInfo);
}

/// <summary>
/// Starts the real process. On Windows the child is placed in a job object that
/// kills it when the app process ends, even after a crash.
/// </summary>
public sealed class BrainProcessLauncher : IBrainProcessLauncher
{
    public IRunningBrainProcess Launch(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var running = new RunningBrainProcess(process);
        if (!process.Start())
        {
            running.Dispose();
            throw new InvalidOperationException("The brain process could not be started.");
        }

        if (OperatingSystem.IsWindows())
        {
            running.AttachJob(WindowsJobObject.TryCreateKillOnClose(process));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return running;
    }

    private sealed class RunningBrainProcess : IRunningBrainProcess
    {
        private readonly Process _process;
        private IDisposable? _job;

        public RunningBrainProcess(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(this, e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(this, e.Data); };
        }

        public event EventHandler<string>? OutputReceived;

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int? ExitCode => HasExited ? SafeExitCode() : null;

        public void AttachJob(IDisposable? job) => _job = job;

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

        public void Dispose()
        {
            _job?.Dispose();
            _process.Dispose();
        }

        private int? SafeExitCode()
        {
            try { return _process.ExitCode; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
