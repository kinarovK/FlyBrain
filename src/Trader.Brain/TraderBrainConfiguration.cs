using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trader.Brain;

/// <summary>Frozen, versioned settings for how market tokens reach the brain and how a decision is read out.</summary>
public sealed record TraderBrainConfiguration
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("execution_mode")]
    public string ExecutionMode { get; init; } = "reference";

    [JsonPropertyName("transmitter_mode")]
    public string TransmitterMode { get; init; } = "unsigned";

    [JsonPropertyName("stimulus_amplitude")]
    public double StimulusAmplitude { get; init; } = 100.0;

    [JsonPropertyName("stimulus_steps")]
    public int StimulusSteps { get; init; } = 1;

    [JsonPropertyName("settle_steps")]
    public int SettleSteps { get; init; } = 22;

    [JsonPropertyName("input_selection")]
    public InputSelection Inputs { get; init; } = new();

    [JsonPropertyName("decision")]
    public DecisionSettings Decision { get; init; } = new();

    public sealed record InputSelection
    {
        /// <summary>Only "ascending-rank" is supported: walk the domain roots in ascending ID order.</summary>
        [JsonPropertyName("strategy")]
        public string Strategy { get; init; } = "ascending-rank";

        /// <summary>First rank to consider (Topo uses ranks 0-14, so the trader starts at 15).</summary>
        [JsonPropertyName("first_rank")]
        public int FirstRank { get; init; } = 15;

        /// <summary>Accept a root only if one stimulus pulse makes it spike.</summary>
        [JsonPropertyName("require_spike")]
        public bool RequireSpike { get; init; } = true;

        [JsonPropertyName("max_candidates")]
        public int MaxCandidates { get; init; } = 120;
    }

    public sealed record DecisionSettings
    {
        /// <summary>|buy - sell| must exceed this fraction of the mean absolute response, otherwise HOLD.</summary>
        [JsonPropertyName("dead_zone_fraction")]
        public double DeadZoneFraction { get; init; } = 0.1;

        /// <summary>How much one extra spike counts, in mV-equivalents, in a pool score.</summary>
        [JsonPropertyName("spike_weight_mV")]
        public double SpikeWeightMillivolts { get; init; } = 1.0;
    }

    public static string DefaultPath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "modules", "trader", "config", "brain-trader.json");

    public static TraderBrainConfiguration LoadFromFile(string path) =>
        Load(File.ReadAllText(path));

    public static TraderBrainConfiguration Load(string json)
    {
        var configuration = JsonSerializer.Deserialize<TraderBrainConfiguration>(json)
            ?? throw new InvalidOperationException("Trader brain configuration is empty.");
        configuration.Validate();
        return configuration;
    }

    public void Validate()
    {
        if (Version != 1)
        {
            throw new InvalidOperationException("Only trader brain configuration version 1 is supported.");
        }

        if (ExecutionMode is not ("reference" or "whole"))
        {
            throw new InvalidOperationException("execution_mode must be 'reference' or 'whole'.");
        }

        if (TransmitterMode is not ("unsigned" or "transmitter"))
        {
            throw new InvalidOperationException("transmitter_mode must be 'unsigned' or 'transmitter'.");
        }

        if (!double.IsFinite(StimulusAmplitude) || StimulusAmplitude <= 0 || StimulusSteps < 1 || SettleSteps < 0)
        {
            throw new InvalidOperationException("Stimulus amplitude/steps are invalid.");
        }

        if (Inputs.Strategy != "ascending-rank" || Inputs.FirstRank < 0 || Inputs.MaxCandidates < Enum.GetValues<Trader.Core.MarketToken>().Length)
        {
            throw new InvalidOperationException("Input selection settings are invalid.");
        }

        if (Decision.DeadZoneFraction < 0 || !double.IsFinite(Decision.SpikeWeightMillivolts))
        {
            throw new InvalidOperationException("Decision settings are invalid.");
        }
    }
}
