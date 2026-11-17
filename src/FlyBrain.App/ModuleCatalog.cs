using FlyBrain.BrainClient;
using FlyBrain.Modules;
using Topo.Module;

namespace FlyBrain.App;

/// <summary>
/// The list of modules shown in the shell. To add a module, reference its project
/// and add one line here.
/// </summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<IBrainModule> Create(IBrainServiceClient client, string? repositoryRoot) =>
    [
        new TopoModule(client),
    ];
}
