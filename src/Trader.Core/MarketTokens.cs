namespace Trader.Core;

/// <summary>
/// Discrete market symbols fed to the brain. Each decision uses exactly one token
/// from each group, in this order: return, trend, volatility.
/// The meaning is application-defined; the connectome knows nothing about markets.
/// </summary>
public enum MarketToken
{
    ReturnStrongDown,
    ReturnDown,
    ReturnFlat,
    ReturnUp,
    ReturnStrongUp,
    TrendDown,
    TrendFlat,
    TrendUp,
    VolatilityLow,
    VolatilityHigh,
}

public enum TradeAction
{
    Hold,
    Buy,
    Sell,
}

public static class MarketTokenText
{
    public static string Short(MarketToken token) => token switch
    {
        MarketToken.ReturnStrongDown => "R--",
        MarketToken.ReturnDown => "R-",
        MarketToken.ReturnFlat => "R0",
        MarketToken.ReturnUp => "R+",
        MarketToken.ReturnStrongUp => "R++",
        MarketToken.TrendDown => "T-",
        MarketToken.TrendFlat => "T0",
        MarketToken.TrendUp => "T+",
        MarketToken.VolatilityLow => "Vlo",
        MarketToken.VolatilityHigh => "Vhi",
        _ => token.ToString(),
    };

    public static string Sequence(IEnumerable<MarketToken> tokens) => string.Join(" ", tokens.Select(Short));
}
