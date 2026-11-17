namespace Trader.Core;

/// <summary>One OHLCV bar. Times are UTC.</summary>
public readonly record struct Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume)
{
    public void Validate()
    {
        if (Open <= 0 || High <= 0 || Low <= 0 || Close <= 0)
        {
            throw new InvalidDataException($"Candle {OpenTime:u} has a non-positive price.");
        }

        if (High < Low || High < Math.Max(Open, Close) || Low > Math.Min(Open, Close))
        {
            throw new InvalidDataException($"Candle {OpenTime:u} has inconsistent high/low values.");
        }

        if (Volume < 0)
        {
            throw new InvalidDataException($"Candle {OpenTime:u} has a negative volume.");
        }
    }
}
