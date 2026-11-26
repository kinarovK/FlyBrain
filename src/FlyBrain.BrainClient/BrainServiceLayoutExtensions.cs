namespace FlyBrain.BrainClient;

public static class BrainServiceLayoutExtensions
{
    /// <summary>Largest page the service accepts by default (<c>max_layout_page</c>).</summary>
    public const int DefaultLayoutPageSize = 4096;

    /// <summary>
    /// Reads the layout of every root of a session, page by page, in the order of
    /// <see cref="IBrainServiceClient.GetSessionRootsAsync"/>.
    /// </summary>
    public static async Task<IReadOnlyList<NeuronLayoutEntry>> GetFullSessionLayoutAsync(
        this IBrainServiceClient client,
        string sessionId,
        int pageSize = DefaultLayoutPageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var entries = new List<NeuronLayoutEntry>();
        int? total = null;
        while (total is null || entries.Count < total)
        {
            var page = await client.GetSessionLayoutAsync(sessionId, entries.Count, pageSize, cancellationToken);
            if (total is not null && page.TotalRootCount != total)
            {
                throw new InvalidOperationException($"Session root count changed while paging ({total} -> {page.TotalRootCount}).");
            }

            total = page.TotalRootCount;
            if (page.Offset != entries.Count || page.Entries.Count != page.ReturnedCount)
            {
                throw new InvalidOperationException("Brain Service returned an inconsistent layout page.");
            }

            if (page.Entries.Count == 0 && entries.Count < total)
            {
                throw new InvalidOperationException($"Brain Service returned an empty layout page at offset {entries.Count} of {total}.");
            }

            entries.AddRange(page.Entries);
        }

        if (entries.Count != total)
        {
            throw new InvalidOperationException($"Brain Service returned {entries.Count} layout entries for {total} roots.");
        }

        return entries;
    }
}
