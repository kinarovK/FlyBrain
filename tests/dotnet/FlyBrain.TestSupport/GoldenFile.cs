using System.Text.Json;
using System.Text.Json.Serialization;
using FlyBrain.Common;

namespace FlyBrain.TestSupport;

/// <summary>
/// Golden files under tests/golden/ pin frozen behaviour (for example the Topo
/// N35 W54 decode). Record them once with FLYBRAIN_RECORD_GOLDEN=1
/// (scripts/gate.ps1 -RecordGolden), commit them, and every later run must match.
/// </summary>
public static class GoldenFile
{
    public const string RecordVariable = "FLYBRAIN_RECORD_GOLDEN";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool IsRecording =>
        string.Equals(Environment.GetEnvironmentVariable(RecordVariable), "1", StringComparison.Ordinal);

    public static string PathFor(string name) =>
        Path.Combine(RepositoryLocator.FindRoot(AppContext.BaseDirectory), "tests", "golden", name);

    /// <summary>
    /// Records <paramref name="actual"/> when recording is on; otherwise compares it with
    /// the stored file. <paramref name="compare"/> returns null when equal, or a description.
    /// </summary>
    public static void AssertMatches<T>(string name, T actual, Func<T, T, string?> compare)
    {
        var path = PathFor(name);
        if (IsRecording)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(actual, JsonOptions) + Environment.NewLine);
            return;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Golden file {path} is missing. Record it once with scripts\\gate.ps1 -RecordGolden and commit it.");
        }

        var expected = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Golden file {path} is empty.");
        var problem = compare(expected, actual);
        if (problem is not null)
        {
            throw new InvalidOperationException(
                $"Frozen behaviour changed ({name}): {problem}. If the change is intended, re-record with -RecordGolden and explain it in the commit.");
        }
    }

    public static bool Close(double expected, double actual, double tolerance = 1e-9) =>
        Math.Abs(expected - actual) <= tolerance * Math.Max(1.0, Math.Abs(expected));
}
