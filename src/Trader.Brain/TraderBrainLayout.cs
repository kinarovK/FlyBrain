using Trader.Core;

namespace Trader.Brain;

/// <summary>
/// Which neurons the trader stimulates (one input root per market token) and which
/// neurons it reads (two fixed pools). The assignment is arbitrary and
/// application-defined: no neuron has a biological "buy" or "sell" meaning.
/// </summary>
public sealed record TraderBrainLayout(
    IReadOnlyDictionary<MarketToken, long> InputRoots,
    IReadOnlyList<long> BuyPool,
    IReadOnlyList<long> SellPool)
{
    public IReadOnlyList<long> ReadoutRoots { get; } = BuyPool.Concat(SellPool).ToArray();

    /// <summary>
    /// Builds a layout from the session's roots. Input roots are taken in ascending ID
    /// order from <paramref name="firstRank"/>, keeping only accepted candidates. All
    /// remaining roots form the readout; even ranks go to BUY, odd ranks to SELL.
    /// </summary>
    public static async Task<TraderBrainLayout> BuildAsync(
        IEnumerable<long> domainRoots,
        int firstRank,
        int maxCandidates,
        Func<long, CancellationToken, Task<bool>> acceptInput,
        CancellationToken cancellationToken = default)
    {
        var sorted = domainRoots.Distinct().Order().ToArray();
        var tokens = Enum.GetValues<MarketToken>();
        var inputs = new Dictionary<MarketToken, long>();
        var examined = 0;
        for (var rank = firstRank; rank < sorted.Length && inputs.Count < tokens.Length && examined < maxCandidates; rank++)
        {
            examined++;
            if (await acceptInput(sorted[rank], cancellationToken))
            {
                inputs[tokens[inputs.Count]] = sorted[rank];
            }
        }

        if (inputs.Count < tokens.Length)
        {
            throw new InvalidOperationException(
                $"Only {inputs.Count} of {tokens.Length} trader input roots responded to the stimulus after {examined} candidates.");
        }

        var inputSet = inputs.Values.ToHashSet();
        var readout = sorted.Where(root => !inputSet.Contains(root)).ToArray();
        var buy = readout.Where((_, index) => index % 2 == 0).ToArray();
        var sell = readout.Where((_, index) => index % 2 == 1).ToArray();
        return new TraderBrainLayout(inputs, buy, sell);
    }
}
