using FlyBrain.Common;

namespace FlyBrain.BrainHost;

public sealed class BrainHostOptions
{
    public const string PythonEnvironmentVariable = "FLYBRAIN_PYTHON";

    public required string RepositoryRoot { get; init; }

    public required string PythonExecutable { get; init; }

    public Uri BaseAddress { get; init; } = new("http://127.0.0.1:8765");

    /// <summary>Prepare the full 139,255-neuron domain at startup (slower, ~1.4 GB).</summary>
    public bool EagerWholeDomain { get; set; }

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxLogLines { get; init; } = 500;

    public string ServerScriptPath => Path.Combine(RepositoryRoot, "brain_service", "server.py");

    public static string DefaultPythonExecutable(string repositoryRoot)
    {
        var configured = Environment.GetEnvironmentVariable(PythonEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return OperatingSystem.IsWindows()
            ? Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(repositoryRoot, ".venv", "bin", "python");
    }

    public static BrainHostOptions CreateDefault(Uri baseAddress, bool eagerWholeDomain = false)
    {
        var root = RepositoryLocator.FindRoot();
        return new BrainHostOptions
        {
            RepositoryRoot = root,
            PythonExecutable = DefaultPythonExecutable(root),
            BaseAddress = baseAddress,
            EagerWholeDomain = eagerWholeDomain,
        };
    }
}
