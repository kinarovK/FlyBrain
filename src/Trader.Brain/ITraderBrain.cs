using Trader.Core;

namespace Trader.Brain;

public interface ITraderBrain
{
    bool IsConnected { get; }

    int CacheSize { get; }

    TraderBrainLayout? Layout { get; }

    Task<TraderBrainStatus> ConnectAsync(IProgress<string>? log = null, CancellationToken cancellationToken = default);

    Task<BrainMarketEvaluation> EvaluateAsync(IReadOnlyList<MarketToken> tokens, CancellationToken cancellationToken = default);

    Task ReleaseAsync();
}
