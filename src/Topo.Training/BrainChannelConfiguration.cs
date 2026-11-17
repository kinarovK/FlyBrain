using System.Text.Json;
using System.Text.Json.Serialization;
using Topo.Core;

namespace Topo.Training;

public sealed class BrainChannelConfiguration
{
    public BrainChannelConfiguration(
        int version,
        string executionMode,
        string transmitterMode,
        double stimulusAmplitude,
        int stimulusSteps,
        int settleSteps,
        IReadOnlyDictionary<GeoCoordinateToken, IReadOnlyList<long>> channels)
    {
        Version = version;
        ExecutionMode = executionMode;
        TransmitterMode = transmitterMode;
        StimulusAmplitude = stimulusAmplitude;
        StimulusSteps = stimulusSteps;
        SettleSteps = settleSteps;
        Channels = channels.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<long>)pair.Value.ToArray());
        Validate();
    }

    public int Version { get; }
    public string ExecutionMode { get; }
    public string TransmitterMode { get; }
    public double StimulusAmplitude { get; }
    public int StimulusSteps { get; }
    public int SettleSteps { get; }
    public IReadOnlyDictionary<GeoCoordinateToken, IReadOnlyList<long>> Channels { get; }

    public IReadOnlyList<long> GetChannel(GeoCoordinateToken token) => Channels[token];

    public static BrainChannelConfiguration LoadFromFile(string path) => Load(File.ReadAllText(path));

    public static BrainChannelConfiguration Load(string json)
    {
        var document = JsonSerializer.Deserialize<BrainChannelConfigurationDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("Brain channel configuration is empty.");
        var channels = new Dictionary<GeoCoordinateToken, IReadOnlyList<long>>();
        foreach (var (tokenName, rootIds) in document.Channels)
        {
            if (!Enum.TryParse<GeoCoordinateToken>(tokenName, ignoreCase: false, out var token))
            {
                throw new InvalidOperationException($"Unknown coordinate token '{tokenName}'.");
            }

            if (!channels.TryAdd(token, rootIds))
            {
                throw new InvalidOperationException($"Duplicate coordinate token '{tokenName}'.");
            }
        }

        return new BrainChannelConfiguration(
            document.Version,
            document.ExecutionMode,
            document.TransmitterMode,
            document.StimulusAmplitude,
            document.StimulusSteps,
            document.SettleSteps,
            channels);
    }

    private void Validate()
    {
        if (Version != 1)
        {
            throw new InvalidOperationException("Only brain channel configuration version 1 is supported.");
        }

        if (!string.Equals(ExecutionMode, "reference", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Phase 4 channel configuration requires reference execution mode.");
        }

        if (!string.Equals(TransmitterMode, "unsigned", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Phase 4 channel configuration requires unsigned transmitter mode.");
        }

        if (!double.IsFinite(StimulusAmplitude))
        {
            throw new InvalidOperationException("Stimulus amplitude must be finite.");
        }

        if (StimulusSteps <= 0)
        {
            throw new InvalidOperationException("Stimulus steps must be positive.");
        }

        if (SettleSteps < 0)
        {
            throw new InvalidOperationException("Settle steps cannot be negative.");
        }

        var expectedTokens = Enum.GetValues<GeoCoordinateToken>();
        if (Channels.Count != expectedTokens.Length || expectedTokens.Any(token => !Channels.ContainsKey(token)))
        {
            throw new InvalidOperationException("Channel configuration must contain exactly every coordinate token.");
        }

        var assignedRoots = new HashSet<long>();
        foreach (var token in expectedTokens)
        {
            var channel = Channels[token];
            if (channel.Count == 0)
            {
                throw new InvalidOperationException($"Channel '{token}' cannot be empty.");
            }

            var channelRoots = new HashSet<long>();
            foreach (var rootId in channel)
            {
                if (rootId <= 0)
                {
                    throw new InvalidOperationException($"Channel '{token}' contains a non-positive root ID.");
                }

                if (!channelRoots.Add(rootId))
                {
                    throw new InvalidOperationException($"Channel '{token}' contains a duplicate root ID.");
                }

                if (!assignedRoots.Add(rootId))
                {
                    throw new InvalidOperationException($"Root ID '{rootId}' is shared between channels.");
                }
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private sealed class BrainChannelConfigurationDocument
    {
        [JsonPropertyName("version")]
        public required int Version { get; init; }
        [JsonPropertyName("execution_mode")]
        public required string ExecutionMode { get; init; }
        [JsonPropertyName("transmitter_mode")]
        public required string TransmitterMode { get; init; }
        [JsonPropertyName("stimulus_amplitude")]
        public required double StimulusAmplitude { get; init; }
        [JsonPropertyName("stimulus_steps")]
        public required int StimulusSteps { get; init; }
        [JsonPropertyName("settle_steps")]
        public required int SettleSteps { get; init; }
        [JsonPropertyName("channels")]
        public required Dictionary<string, List<long>> Channels { get; init; }
    }
}
