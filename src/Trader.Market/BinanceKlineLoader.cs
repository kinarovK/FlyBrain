using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Trader.Core;

namespace Trader.Market;

public sealed record MarketDataFile(string FileName, int CandleCount, bool? ChecksumValid);

public sealed record MarketDataSet(
    string Symbol,
    string Interval,
    IReadOnlyList<Candle> Candles,
    IReadOnlyList<MarketDataFile> Files,
    int GapCount)
{
    public DateTimeOffset? Start => Candles.Count > 0 ? Candles[0].OpenTime : null;

    public DateTimeOffset? End => Candles.Count > 0 ? Candles[^1].OpenTime : null;
}

/// <summary>
/// Loads Binance public kline files (data.binance.vision), for example
/// <c>BTCUSDT-1h-2024-01.zip</c>. Each zip holds one headerless CSV. Optional
/// <c>.CHECKSUM</c> files (SHA-256) are verified when present.
/// </summary>
public sealed class BinanceKlineLoader
{
    public static string DefaultDirectory(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "data", "market", "binance");

    public MarketDataSet LoadDirectory(string directory, string symbol = "BTCUSDT", string interval = "1h")
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Market data folder not found: {directory}");
        }

        var pattern = $"{symbol}-{interval}-*";
        var files = Directory.EnumerateFiles(directory, pattern + ".zip")
            .Concat(Directory.EnumerateFiles(directory, pattern + ".csv"))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new FileNotFoundException($"No {symbol} {interval} kline files ({pattern}.zip) found in {directory}.");
        }

        var byTime = new SortedDictionary<DateTimeOffset, Candle>();
        var reports = new List<MarketDataFile>();
        foreach (var path in files)
        {
            var candles = LoadFile(path);
            foreach (var candle in candles)
            {
                byTime[candle.OpenTime] = candle;
            }

            reports.Add(new MarketDataFile(Path.GetFileName(path), candles.Count, VerifyChecksum(path)));
        }

        var ordered = byTime.Values.ToList();
        var step = IntervalLength(interval);
        var gaps = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].OpenTime - ordered[i - 1].OpenTime != step)
            {
                gaps++;
            }
        }

        return new MarketDataSet(symbol, interval, ordered, reports, gaps);
    }

    public IReadOnlyList<Candle> LoadFile(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{Path.GetFileName(path)} does not contain a CSV file.");
            using var reader = new StreamReader(entry.Open());
            return Parse(reader, Path.GetFileName(path));
        }

        using var csv = new StreamReader(path);
        return Parse(csv, Path.GetFileName(path));
    }

    internal static IReadOnlyList<Candle> Parse(TextReader reader, string sourceName)
    {
        var candles = new List<Candle>();
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length < 6 || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var openTime))
            {
                if (lineNumber == 1)
                {
                    continue; // header row
                }

                throw new InvalidDataException($"{sourceName}: line {lineNumber} is not a kline row.");
            }

            var candle = new Candle(
                ToTime(openTime),
                ParseDecimal(fields[1], sourceName, lineNumber),
                ParseDecimal(fields[2], sourceName, lineNumber),
                ParseDecimal(fields[3], sourceName, lineNumber),
                ParseDecimal(fields[4], sourceName, lineNumber),
                ParseDecimal(fields[5], sourceName, lineNumber));
            candle.Validate();
            candles.Add(candle);
        }

        return candles;
    }

    /// <summary>Binance switched spot timestamps from milliseconds to microseconds in 2025.</summary>
    internal static DateTimeOffset ToTime(long value) =>
        value > 100_000_000_000_000L
            ? DateTimeOffset.UnixEpoch.AddTicks(value * 10)
            : DateTimeOffset.FromUnixTimeMilliseconds(value);

    internal static bool? VerifyChecksum(string path)
    {
        var checksumPath = path + ".CHECKSUM";
        if (!File.Exists(checksumPath))
        {
            return null;
        }

        var expected = File.ReadAllText(checksumPath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan IntervalLength(string interval) => interval switch
    {
        "1m" => TimeSpan.FromMinutes(1),
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "1h" => TimeSpan.FromHours(1),
        "4h" => TimeSpan.FromHours(4),
        "1d" => TimeSpan.FromDays(1),
        _ => throw new ArgumentException($"Unsupported interval '{interval}'.", nameof(interval)),
    };

    private static decimal ParseDecimal(string value, string sourceName, int lineNumber) =>
        decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{sourceName}: line {lineNumber} has an invalid number '{value}'.");
}
