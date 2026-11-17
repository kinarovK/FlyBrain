namespace Trader.Core;

/// <summary>
/// A simulated BTC/USDT account. All-in buys and all-out sells, with a proportional fee.
/// No real orders, exchanges or wallets are involved.
/// </summary>
public sealed class PaperPortfolio
{
    public PaperPortfolio(decimal initialCash, decimal feeRate)
    {
        if (initialCash <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCash));
        }

        if (feeRate is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(feeRate));
        }

        InitialCash = initialCash;
        Cash = initialCash;
        FeeRate = feeRate;
    }

    public decimal InitialCash { get; }

    public decimal FeeRate { get; }

    public decimal Cash { get; private set; }

    public decimal Btc { get; private set; }

    public decimal FeesPaid { get; private set; }

    public int TradeCount { get; private set; }

    public bool HasPosition => Btc > 0;

    public decimal Equity(decimal price) => Cash + Btc * price;

    /// <summary>Applies the action at <paramref name="price"/>; returns true when a trade happened.</summary>
    public bool Apply(TradeAction action, decimal price)
    {
        if (price <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price));
        }

        switch (action)
        {
            case TradeAction.Buy when Cash > 0:
                var buyFee = Cash * FeeRate;
                Btc += (Cash - buyFee) / price;
                FeesPaid += buyFee;
                Cash = 0;
                TradeCount++;
                return true;
            case TradeAction.Sell when Btc > 0:
                var proceeds = Btc * price;
                var sellFee = proceeds * FeeRate;
                Cash += proceeds - sellFee;
                FeesPaid += sellFee;
                Btc = 0;
                TradeCount++;
                return true;
            default:
                return false;
        }
    }
}
