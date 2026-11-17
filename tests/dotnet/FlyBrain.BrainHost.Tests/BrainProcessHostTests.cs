using System.Diagnostics;
using FlyBrain.BrainHost;

namespace FlyBrain.BrainHost.Tests;

public sealed class BrainProcessHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flybrain-host-" + Guid.NewGuid().ToString("N"));

    public BrainProcessHostTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "brain_service"));
        File.WriteAllText(Path.Combine(_root, "brain_service", "server.py"), "# test");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Attaches_to_running_service_without_launching()
    {
        var launcher = new FakeLauncher();
        await using var host = CreateHost(new FakeProbe(ProbeOutcome.Ready), launcher, CreatePython());

        var status = await host.StartAsync();

        Assert.Equal(BrainHostState.Ready, status.State);
        Assert.False(status.OwnsProcess);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task Fails_with_clear_message_when_python_environment_is_missing()
    {
        var launcher = new FakeLauncher();
        await using var host = CreateHost(new FakeProbe(ProbeOutcome.Unreachable), launcher, Path.Combine(_root, "missing", "python.exe"));

        var status = await host.StartAsync();

        Assert.Equal(BrainHostState.Failed, status.State);
        Assert.Contains("Python environment", status.Message);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task Launches_process_and_waits_until_ready()
    {
        var launcher = new FakeLauncher();
        var probe = new FakeProbe(ProbeOutcome.Unreachable, ProbeOutcome.Unreachable, ProbeOutcome.Starting, ProbeOutcome.Ready);
        await using var host = CreateHost(probe, launcher, CreatePython());
        var states = new List<BrainHostState>();
        host.StatusChanged += (_, s) => states.Add(s.State);

        var status = await host.StartAsync();

        Assert.Equal(BrainHostState.Ready, status.State);
        Assert.True(status.OwnsProcess);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Contains(BrainHostState.Starting, states);
    }

    [Fact]
    public async Task Reports_failure_when_process_exits_during_startup()
    {
        var launcher = new FakeLauncher { ExitImmediatelyWithCode = 3 };
        await using var host = CreateHost(new FakeProbe(ProbeOutcome.Unreachable), launcher, CreatePython());

        var status = await host.StartAsync();

        Assert.Equal(BrainHostState.Failed, status.State);
        Assert.Contains("exit code 3", status.Message);
    }

    [Fact]
    public async Task Stop_kills_owned_process()
    {
        var launcher = new FakeLauncher();
        await using var host = CreateHost(new FakeProbe(ProbeOutcome.Unreachable, ProbeOutcome.Ready), launcher, CreatePython());
        await host.StartAsync();

        await host.StopAsync();

        Assert.True(launcher.Last!.Killed);
        Assert.Equal(BrainHostState.Stopped, host.Status.State);
    }

    [Fact]
    public async Task Times_out_and_stops_process_when_never_ready()
    {
        var launcher = new FakeLauncher();
        await using var host = new BrainProcessHost(
            new BrainHostOptions
            {
                RepositoryRoot = _root,
                PythonExecutable = CreatePython(),
                PollInterval = TimeSpan.FromMilliseconds(1),
                StartupTimeout = TimeSpan.FromMilliseconds(30),
            },
            new FakeProbe(ProbeOutcome.Unreachable, ProbeOutcome.Starting),
            launcher);

        var status = await host.StartAsync();

        Assert.Equal(BrainHostState.Failed, status.State);
        Assert.True(launcher.Last!.Killed);
    }

    [Fact]
    public void Start_info_runs_brain_service_module_with_port_and_domain_settings()
    {
        var host = new BrainProcessHost(
            new BrainHostOptions
            {
                RepositoryRoot = _root,
                PythonExecutable = "python",
                BaseAddress = new Uri("http://127.0.0.1:9123"),
                EagerWholeDomain = true,
            },
            new FakeProbe(ProbeOutcome.Ready),
            new FakeLauncher());

        var info = host.CreateStartInfo();

        Assert.Equal(new[] { "-u", "-m", "brain_service.server" }, info.ArgumentList);
        Assert.Equal(_root, info.WorkingDirectory);
        Assert.Equal("9123", info.Environment["FLYTRADER_BRAIN_PORT"]);
        Assert.Equal("1", info.Environment["FLYBRAIN_EAGER_WHOLE"]);
    }

    [Fact]
    public void Setup_check_reports_missing_python_and_brain_data()
    {
        var setup = new BrainSetup(Options(Path.Combine(_root, "missing", "python.exe")));

        var checks = setup.Check();

        Assert.True(checks.Single(c => c.Name == "FlyBrain repository").IsOk);
        Assert.False(checks.Single(c => c.Name == "Python environment").IsOk);
        Assert.False(checks.Single(c => c.Name == "FlyWire brain data").IsOk);
        Assert.False(checks.Single(c => c.Name == "Trader market data").IsRequired);
    }

    [Theory]
    [InlineData("{\"status\":\"ready\"}", "ready")]
    [InlineData("{\"status\":\"failed\",\"dataset\":null}", "failed")]
    [InlineData("not json", null)]
    public void Readiness_status_is_parsed(string body, string? expected)
    {
        Assert.Equal(expected, HttpBrainReadinessProbe.ReadStatus(body));
    }

    private BrainProcessHost CreateHost(IBrainReadinessProbe probe, FakeLauncher launcher, string python) =>
        new(Options(python), probe, launcher);

    private BrainHostOptions Options(string python) => new()
    {
        RepositoryRoot = _root,
        PythonExecutable = python,
        PollInterval = TimeSpan.FromMilliseconds(1),
        StartupTimeout = TimeSpan.FromSeconds(5),
    };

    private string CreatePython()
    {
        var path = Path.Combine(_root, "python.exe");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private sealed class FakeProbe(params ProbeOutcome[] outcomes) : IBrainReadinessProbe
    {
        private int _index;

        public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            var outcome = outcomes[Math.Min(_index++, outcomes.Length - 1)];
            return Task.FromResult(new ProbeResult(outcome, outcome.ToString()));
        }
    }

    private sealed class FakeLauncher : IBrainProcessLauncher
    {
        public int LaunchCount { get; private set; }

        public int? ExitImmediatelyWithCode { get; init; }

        public FakeProcess? Last { get; private set; }

        public IRunningBrainProcess Launch(ProcessStartInfo startInfo)
        {
            LaunchCount++;
            Last = new FakeProcess();
            if (ExitImmediatelyWithCode is { } code)
            {
                Last.Exit(code);
            }

            return Last;
        }
    }

    private sealed class FakeProcess : IRunningBrainProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => _exited.Task.IsCompleted;

        public int? ExitCode { get; private set; }

        public bool Killed { get; private set; }

#pragma warning disable CS0067
        public event EventHandler<string>? OutputReceived;
#pragma warning restore CS0067

        public void Exit(int code)
        {
            ExitCode = code;
            _exited.TrySetResult();
        }

        public void Kill()
        {
            Killed = true;
            Exit(-1);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
        }
    }
}
