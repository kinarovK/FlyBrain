namespace FlyBrain.TestSupport;

/// <summary>
/// Decides whether tests that need a running brain service should run.
/// <c>FLYBRAIN_REAL=1</c> enables all of them (set by scripts/gate.ps1);
/// the older per-area variables still work on their own.
/// Long experiments (Topo Phase 9, token-readout training, coordinate-decoding
/// corpus) keep their own variables and are never enabled by FLYBRAIN_REAL.
/// </summary>
public static class RealBrainGate
{
    public const string Variable = "FLYBRAIN_REAL";

    public static bool IsEnabled(params string[] legacyVariables) =>
        IsOn(Variable) || legacyVariables.Any(IsOn);

    private static bool IsOn(string variable) =>
        string.Equals(Environment.GetEnvironmentVariable(variable), "1", StringComparison.Ordinal);
}
