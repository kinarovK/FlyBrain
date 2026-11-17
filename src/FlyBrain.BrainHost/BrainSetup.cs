using System.Diagnostics;
using System.Text;

namespace FlyBrain.BrainHost;

public sealed record SetupCheck(string Name, bool IsOk, string Detail, bool IsRequired = true);

public interface IBrainSetup
{
    IReadOnlyList<SetupCheck> Check();

    /// <summary>Creates <c>.venv</c> and installs <c>brain/requirements.txt</c>.</summary>
    Task<bool> CreatePythonEnvironmentAsync(IProgress<string> log, CancellationToken cancellationToken = default);

    /// <summary>Downloads the compact FlyWire v783 data and builds the sparse graph (several GB, long).</summary>
    Task<bool> PrepareBrainDataAsync(IProgress<string> log, CancellationToken cancellationToken = default);
}

/// <summary>First-run checks and the one-click environment/data preparation.</summary>
public sealed class BrainSetup(BrainHostOptions options) : IBrainSetup
{
    public const string FlyWirePrinciplesUrl = "https://edit.flywire.ai/principles.html";

    public string SparseGraphMetadataPath => Path.Combine(options.RepositoryRoot, "data", "flywire", "derived", "flytrader-sparse-v1", "metadata.json");

    public string MarketDataDirectory => Path.Combine(options.RepositoryRoot, "data", "market", "binance");

    public IReadOnlyList<SetupCheck> Check()
    {
        var repositoryOk = File.Exists(options.ServerScriptPath);
        var pythonOk = File.Exists(options.PythonExecutable);
        var dataOk = File.Exists(SparseGraphMetadataPath);
        var marketFiles = Directory.Exists(MarketDataDirectory)
            ? Directory.EnumerateFiles(MarketDataDirectory, "*.zip").Count()
            : 0;
        return
        [
            new("FlyBrain repository", repositoryOk, repositoryOk ? options.RepositoryRoot : $"brain_service not found under {options.RepositoryRoot}"),
            new("Python environment", pythonOk, pythonOk ? options.PythonExecutable : "Missing .venv - use 'Create Python environment'."),
            new("FlyWire brain data", dataOk, dataOk ? "Sparse graph flytrader-sparse-v1 present." : "Missing - use 'Download brain data' (several GB)."),
            new("Trader market data", marketFiles > 0, marketFiles > 0 ? $"{marketFiles} kline files." : $"Optional: put Binance BTCUSDT 1h zips into {MarketDataDirectory}.", IsRequired: false),
        ];
    }

    public async Task<bool> CreatePythonEnvironmentAsync(IProgress<string> log, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.PythonExecutable))
        {
            var basePython = await FindSystemPythonAsync(cancellationToken);
            if (basePython is null)
            {
                log.Report("No system Python found. Install Python 3.12+ from python.org (tick 'Add to PATH'), then retry.");
                return false;
            }

            log.Report($"Creating .venv with {basePython.Value.FileName} {string.Join(' ', basePython.Value.Arguments)}...");
            var venvArguments = basePython.Value.Arguments.Concat(new[] { "-m", "venv", ".venv" }).ToArray();
            if (await RunAsync(basePython.Value.FileName, venvArguments, log, cancellationToken) != 0)
            {
                return false;
            }
        }

        log.Report("Installing brain requirements (numpy, scipy, pyarrow, fastapi, uvicorn, ...)...");
        return await RunAsync(options.PythonExecutable, new[] { "-m", "pip", "install", "-r", Path.Combine("brain", "requirements.txt") }, log, cancellationToken) == 0;
    }

    public async Task<bool> PrepareBrainDataAsync(IProgress<string> log, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.PythonExecutable))
        {
            log.Report("Create the Python environment first.");
            return false;
        }

        string[][] steps =
        [
            [Path.Combine("scripts", "download_connectome.py"), "--profile", "compact", "--agree-flywire-principles"],
            [Path.Combine("scripts", "preprocess_connectome.py")],
            [Path.Combine("scripts", "validate_connectome.py")],
            [Path.Combine("scripts", "preprocess_sparse_graph.py")],
            [Path.Combine("scripts", "validate_sparse_graph.py")],
        ];
        foreach (var step in steps)
        {
            log.Report($"> python {string.Join(' ', step)}");
            if (await RunAsync(options.PythonExecutable, step, log, cancellationToken) != 0)
            {
                log.Report($"Step failed: {step[0]}. See the log above.");
                return false;
            }
        }

        return File.Exists(SparseGraphMetadataPath);
    }

    private async Task<(string FileName, string[] Arguments)?> FindSystemPythonAsync(CancellationToken cancellationToken)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new (string FileName, string[] Arguments)[] { ("py", new[] { "-3" }), ("python", Array.Empty<string>()), ("python3", Array.Empty<string>()) }
            : new (string FileName, string[] Arguments)[] { ("python3", Array.Empty<string>()), ("python", Array.Empty<string>()) };
        foreach (var (fileName, arguments) in candidates)
        {
            try
            {
                var exitCode = await RunAsync(fileName, arguments.Concat(new[] { "--version" }).ToArray(), new Progress<string>(_ => { }), cancellationToken);
                if (exitCode == 0)
                {
                    return (fileName, arguments);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed / not on PATH.
            }
        }

        return null;
    }

    private async Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, IProgress<string> log, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = options.RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.Report(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return process.ExitCode;
    }
}
