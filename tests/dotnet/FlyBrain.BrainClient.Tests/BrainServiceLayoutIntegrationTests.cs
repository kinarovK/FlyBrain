using System.Text.Json;
using FlyBrain.BrainClient;
using FlyBrain.Common;
using FlyBrain.TestSupport;
using Xunit;

namespace FlyBrain.BrainClient.Tests;

/// <summary>
/// Real-brain check of sprint V0 (task 0011): the layout of a reference session
/// equals the exported positions file. Needs a running brain and
/// <c>data/flywire/derived/positions-v1</c> (scripts/export_positions.py).
/// </summary>
public sealed class BrainServiceLayoutIntegrationTests
{
    private const int ReferenceRootCount = 912;

    private static string PositionsDirectory =>
        Path.Combine(RepositoryLocator.FindRoot(AppContext.BaseDirectory), "data", "flywire", "derived", "positions-v1");

    [Fact]
    [Trait("Category", "Integration")]
    public void ExportedPositions_HaveAPlausibleWholeBrainBoundingBox()
    {
        if (!RealBrainGate.IsEnabled())
        {
            return;
        }

        var file = PositionsFile.Load(PositionsDirectory);
        var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new[] { float.MinValue, float.MinValue, float.MinValue };
        for (var index = 0; index < file.RootIds.Length; index++)
        {
            if (!file.HasPosition[index])
            {
                continue;
            }

            for (var axis = 0; axis < 3; axis++)
            {
                min[axis] = Math.Min(min[axis], file.Xyz[(index * 3) + axis]);
                max[axis] = Math.Max(max[axis], file.Xyz[(index * 3) + axis]);
            }
        }

        Assert.Equal(139_255, file.RootIds.Length);
        Assert.Equal("um", file.Metadata.GetProperty("coordinate_unit").GetString());
        Assert.Equal(new[] { 4.0, 4.0, 40.0 }, file.Metadata.GetProperty("voxel_size_nm").EnumerateArray().Select(v => v.GetDouble()));
        var box = file.Metadata.GetProperty("bounding_box_um");
        var axes = new[] { "x", "y", "z" };
        for (var axis = 0; axis < 3; axis++)
        {
            Assert.Equal(box.GetProperty("min").GetProperty(axes[axis]).GetDouble(), min[axis], 3);
            Assert.Equal(box.GetProperty("max").GetProperty(axes[axis]).GetDouble(), max[axis], 3);
        }

        // Adult fly brain incl. optic lobes in FAFB space: ~0.8 mm wide, ~0.4 mm high, ~0.28 mm deep.
        Assert.InRange(max[0] - min[0], 500f, 1000f);
        Assert.InRange(max[1] - min[1], 200f, 600f);
        Assert.InRange(max[2] - min[2], 200f, 400f);
        Assert.Equal(file.UnpositionedRoots.Count, file.HasPosition.Count(flag => !flag));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReferenceSessionLayout_EqualsTheExportedFileInRootsOrder()
    {
        if (!RealBrainGate.IsEnabled())
        {
            return;
        }

        var file = PositionsFile.Load(PositionsDirectory);
        using var httpClient = new HttpClient { BaseAddress = BrainServiceClientOptions.FromEnvironment().BaseAddress };
        var client = new BrainServiceClient(httpClient);
        string? sessionId = null;
        try
        {
            sessionId = (await client.CreateSessionAsync(new CreateBrainSessionRequest())).SessionId;

            var roots = new List<long>();
            while (true)
            {
                var page = await client.GetSessionRootsAsync(sessionId, roots.Count, 256);
                roots.AddRange(page.RootIds);
                if (page.RootIds.Count == 0 || roots.Count >= page.TotalRootCount)
                {
                    break;
                }
            }

            var firstPage = await client.GetSessionLayoutAsync(sessionId, 0, 4096);
            var layout = await client.GetFullSessionLayoutAsync(sessionId);
            var smallPages = await client.GetFullSessionLayoutAsync(sessionId, pageSize: 100);

            Assert.Equal("um", firstPage.CoordinateUnit);
            Assert.Equal(ReferenceRootCount, firstPage.TotalRootCount);
            Assert.Equal(ReferenceRootCount, roots.Count);
            Assert.Equal(ReferenceRootCount, layout.Count);
            Assert.Equal(roots, layout.Select(entry => entry.RootId));
            Assert.Equal(layout.Select(Describe), smallPages.Select(Describe));
            Assert.Equal(layout.Select(Describe), firstPage.Entries.Select(Describe));

            foreach (var entry in layout)
            {
                var index = Array.BinarySearch(file.RootIds, entry.RootId);
                Assert.True(index >= 0, $"root {entry.RootId} missing from the positions file");
                Assert.Equal(file.Xyz[index * 3], entry.X);
                Assert.Equal(file.Xyz[(index * 3) + 1], entry.Y);
                Assert.Equal(file.Xyz[(index * 3) + 2], entry.Z);
                Assert.Equal(file.HasPosition[index], entry.HasPosition);
                Assert.True(entry.HasPosition || file.UnpositionedRoots.Contains(entry.RootId), $"root {entry.RootId} has no position and is not listed in metadata");
                if (entry.HasPosition)
                {
                    Assert.False(string.IsNullOrEmpty(entry.SuperClass));
                }
            }
        }
        finally
        {
            if (sessionId is not null)
            {
                await client.DeleteSessionAsync(sessionId);
            }
        }
    }

    private static string Describe(NeuronLayoutEntry entry) =>
        FormattableString.Invariant($"{entry.RootId}:{entry.X:R},{entry.Y:R},{entry.Z:R},{entry.HasPosition},{entry.SuperClass},{entry.CellClass},{entry.CellType},{entry.Side}");

    private sealed record PositionsFile(long[] RootIds, float[] Xyz, bool[] HasPosition, HashSet<long> UnpositionedRoots, JsonElement Metadata)
    {
        public static PositionsFile Load(string directory)
        {
            Assert.True(
                File.Exists(Path.Combine(directory, "metadata.json")),
                $"Positions are missing under {directory}; run scripts\\export_positions.py.");
            var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metadata.json"))).RootElement.Clone();
            var unpositioned = metadata.GetProperty("roots_without_annotation").EnumerateArray()
                .Select(entry => entry.GetProperty("root_id").GetInt64())
                .ToHashSet();
            var roots = NpyReader.ReadInt64(Path.Combine(directory, "root_ids.npy"));
            var xyz = NpyReader.ReadFloat32(Path.Combine(directory, "xyz_um.npy"));
            var hasPosition = NpyReader.ReadBool(Path.Combine(directory, "has_position.npy"));
            Assert.Equal(roots.Length * 3, xyz.Length);
            Assert.Equal(roots.Length, hasPosition.Length);
            return new PositionsFile(roots, xyz, hasPosition, unpositioned, metadata);
        }
    }
}
