using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Trader.Market;

namespace Trader.Tests;

public sealed class MarketTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "flybrain-market-" + Guid.NewGuid().ToString("N"));

    public MarketTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Fact]
    public void Parses_binance_rows_with_millisecond_and_microsecond_times()
    {
        const string csv = """
            1704067200000,42283.58,42554.57,42261.02,42475.23,1271.68,1704070799999,53957248.97,47134,682.57,28957416.81,0
            1735689600000000,93576.00,94509.42,93489.03,94401.14,755.37,1735693199999999,71130780.06,96187,375.66,35377766.34,0
            """;

        var candles = BinanceKlineLoader.Parse(new StringReader(csv), "test");

        Assert.Equal(2, candles.Count);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), candles[0].OpenTime);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), candles[1].OpenTime);
        Assert.Equal(42475.23m, candles[0].Close);
    }

    [Fact]
    public void Skips_a_header_row()
    {
        const string csv = "open_time,open,high,low,close,volume\n1704067200000,1,2,0.5,1.5,3";

        Assert.Single(BinanceKlineLoader.Parse(new StringReader(csv), "test"));
    }

    [Fact]
    public void Rejects_inconsistent_prices()
    {
        const string csv = "1704067200000,10,9,8,11,1";

        Assert.Throws<InvalidDataException>(() => BinanceKlineLoader.Parse(new StringReader(csv), "test"));
    }

    [Fact]
    public void Loads_zips_in_time_order_verifies_checksums_and_counts_gaps()
    {
        WriteZip("BTCUSDT-1h-2024-02.zip", "1706745600000,3,3,3,3,1\n1706749200000,4,4,4,4,1", withChecksum: true);
        WriteZip("BTCUSDT-1h-2024-01.zip", "1704067200000,1,1,1,1,1\n1704070800000,2,2,2,2,1", withChecksum: false);

        var set = new BinanceKlineLoader().LoadDirectory(_directory);

        Assert.Equal(4, set.Candles.Count);
        Assert.Equal(1m, set.Candles[0].Close);
        Assert.Equal(4m, set.Candles[^1].Close);
        Assert.Equal(1, set.GapCount);
        Assert.Null(set.Files.Single(f => f.FileName.EndsWith("01.zip", StringComparison.Ordinal)).ChecksumValid);
        Assert.True(set.Files.Single(f => f.FileName.EndsWith("02.zip", StringComparison.Ordinal)).ChecksumValid);
    }

    [Fact]
    public void Missing_folder_gives_a_clear_error()
    {
        var error = Assert.Throws<DirectoryNotFoundException>(() => new BinanceKlineLoader().LoadDirectory(Path.Combine(_directory, "nope")));
        Assert.Contains("Market data folder", error.Message);
    }

    private void WriteZip(string name, string csv, bool withChecksum)
    {
        var path = Path.Combine(_directory, name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(Path.ChangeExtension(name, ".csv"));
            using var writer = new StreamWriter(entry.Open(), Encoding.ASCII);
            writer.Write(csv);
        }

        if (withChecksum)
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            File.WriteAllText(path + ".CHECKSUM", $"{hash}  {name}");
        }
    }
}
