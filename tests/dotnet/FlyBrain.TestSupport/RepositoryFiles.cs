using FlyBrain.Common;

namespace FlyBrain.TestSupport;

/// <summary>
/// Resolves frozen input files for real-brain tests. An environment variable
/// (for example <c>TOPO_BRAIN_CHANNELS_PATH</c>) still overrides the location;
/// otherwise the versioned file in the repository is used, so
/// <c>FLYBRAIN_REAL=1</c> alone is enough to run the tests.
/// </summary>
public static class RepositoryFiles
{
    public const string TopoChannelsVariable = "TOPO_BRAIN_CHANNELS_PATH";
    public const string TopoReadoutPopulationVariable = "TOPO_READOUT_POPULATION_PATH";
    public const string TopoTokenReadoutModelVariable = "TOPO_TOKEN_READOUT_MODEL_PATH";

    public static string TopoChannels =>
        Resolve(TopoChannelsVariable, "modules", "topo", "config", "brain-channels.json");

    public static string TopoReadoutPopulation =>
        Resolve(TopoReadoutPopulationVariable, "modules", "topo", "config", "readout-population.json");

    public static string TopoTokenReadoutModel =>
        Resolve(TopoTokenReadoutModelVariable, "modules", "topo", "models", "token-readout-v1.json");

    /// <summary>
    /// Returns the path named by <paramref name="overrideVariable"/> when it is set,
    /// otherwise the repository file at <paramref name="relativeSegments"/>.
    /// </summary>
    public static string Resolve(string overrideVariable, params string[] relativeSegments)
    {
        var configured = Environment.GetEnvironmentVariable(overrideVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var root = RepositoryLocator.FindRoot(AppContext.BaseDirectory);
        return Path.Combine([root, .. relativeSegments]);
    }
}
