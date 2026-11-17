namespace FlyBrain.Common;

/// <summary>
/// Locates the FlyBrain repository root, which holds both the Python brain service
/// and the module artifacts (modules/&lt;name&gt;/...).
/// </summary>
public static class RepositoryLocator
{
    public const string RootEnvironmentVariable = "FLYBRAIN_ROOT";
    public const string LegacyTopoRootEnvironmentVariable = "TOPO_REPOSITORY_ROOT";

    public static bool IsRepositoryRoot(string directory) =>
        File.Exists(Path.Combine(directory, "brain_service", "server.py"));

    public static string? TryFindRoot(params string?[] extraStartDirectories)
    {
        foreach (var variable in new[] { RootEnvironmentVariable, LegacyTopoRootEnvironmentVariable })
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }
        }

        var starts = extraStartDirectories
            .Append(AppContext.BaseDirectory)
            .Append(Directory.GetCurrentDirectory())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!);

        foreach (var start in starts)
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
            {
                if (IsRepositoryRoot(directory.FullName))
                {
                    return directory.FullName;
                }
            }
        }

        return null;
    }

    public static string FindRoot(params string?[] extraStartDirectories) =>
        TryFindRoot(extraStartDirectories)
        ?? throw new InvalidOperationException(
            $"FlyBrain repository root was not found. Run the app from inside the repository or set {RootEnvironmentVariable}.");

    public static string ModuleDirectory(string repositoryRoot, string moduleName) =>
        Path.Combine(repositoryRoot, "modules", moduleName);
}
