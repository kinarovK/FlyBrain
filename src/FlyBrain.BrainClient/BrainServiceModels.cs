using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyBrain.BrainClient;

public enum BrainExecutionMode
{
    Reference,
    Whole,
}

public enum BrainTransmitterMode
{
    Unsigned,
    Transmitter,
}

public sealed class BrainReadyResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("dataset")]
    public string? Dataset { get; init; }

    [JsonPropertyName("graph_format")]
    public string? GraphFormat { get; init; }

    [JsonPropertyName("canonical_neuron_count")]
    public int? CanonicalNeuronCount { get; init; }

    [JsonPropertyName("startup_timings")]
    public required Dictionary<string, double> StartupTimings { get; init; }

    [JsonPropertyName("service_version")]
    public required string ServiceVersion { get; init; }
}

public sealed record BrainReadiness(HttpStatusCode StatusCode, BrainReadyResult Result)
{
    public bool IsReady => StatusCode == HttpStatusCode.OK &&
        string.Equals(Result.Status, "ready", StringComparison.OrdinalIgnoreCase);
}

public sealed class BrainInfo
{
    [JsonPropertyName("api_version")]
    public required string ApiVersion { get; init; }

    [JsonPropertyName("service_version")]
    public required string ServiceVersion { get; init; }

    [JsonPropertyName("source_biology")]
    public required BrainSourceBiology SourceBiology { get; init; }

    [JsonPropertyName("model_configuration")]
    public required BrainModelConfiguration ModelConfiguration { get; init; }
}

public sealed class BrainSourceBiology
{
    [JsonPropertyName("dataset")]
    public required string Dataset { get; init; }

    [JsonPropertyName("canonical_neuron_count")]
    public required int CanonicalNeuronCount { get; init; }

    [JsonPropertyName("source_edge_rows")]
    public required int SourceEdgeRows { get; init; }

    [JsonPropertyName("represented_synapses")]
    public required int RepresentedSynapses { get; init; }

    [JsonPropertyName("csr_pair_count")]
    public required int CsrPairCount { get; init; }

    [JsonPropertyName("graph_format")]
    public required string GraphFormat { get; init; }
}

public sealed class BrainModelConfiguration
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("dt_ms")]
    public required double DtMilliseconds { get; init; }

    [JsonPropertyName("weight_transform")]
    public required string WeightTransform { get; init; }

    [JsonPropertyName("available_transmitter_modes")]
    public required List<string> AvailableTransmitterModes { get; init; }

    [JsonPropertyName("supported_execution_modes")]
    public required List<string> SupportedExecutionModes { get; init; }

    [JsonPropertyName("external_stimulus_note")]
    public required string ExternalStimulusNote { get; init; }
}

public sealed class CreateBrainSessionRequest
{
    [JsonPropertyName("execution_mode")]
    public BrainExecutionMode ExecutionMode { get; init; } = BrainExecutionMode.Reference;

    [JsonPropertyName("transmitter_mode")]
    public BrainTransmitterMode TransmitterMode { get; init; } = BrainTransmitterMode.Unsigned;
}

public sealed class BrainSession
{
    [JsonPropertyName("api_version")]
    public required string ApiVersion { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("execution_mode")]
    public required BrainExecutionMode ExecutionMode { get; init; }

    [JsonPropertyName("neuron_count")]
    public required int NeuronCount { get; init; }

    [JsonPropertyName("transmitter_mode")]
    public required string TransmitterMode { get; init; }

    [JsonPropertyName("resolved_configuration")]
    public required BrainResolvedConfiguration ResolvedConfiguration { get; init; }
}

public sealed class BrainResolvedConfiguration
{
    [JsonPropertyName("neural_format")]
    public required string NeuralFormat { get; init; }

    [JsonPropertyName("neural_version")]
    public required int NeuralVersion { get; init; }

    [JsonPropertyName("transmitter_mode")]
    public required BrainTransmitterMode TransmitterMode { get; init; }

    [JsonPropertyName("execution_mode")]
    public required BrainExecutionMode ExecutionMode { get; init; }
}

public sealed class BrainStepRequest
{
    [JsonPropertyName("steps")]
    public required int Steps { get; init; }

    [JsonPropertyName("stimulus")]
    public IReadOnlyList<BrainStimulus>? Stimulus { get; init; }

    [JsonPropertyName("max_returned_events")]
    public int? MaxReturnedEvents { get; init; }
}

public sealed record BrainStimulus(
    [property: JsonPropertyName("root_id")] long RootId,
    [property: JsonPropertyName("amplitude")] double Amplitude);

public sealed class BrainStepResult
{
    [JsonPropertyName("api_version")]
    public required string ApiVersion { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("starting_step")]
    public required int StartingStep { get; init; }

    [JsonPropertyName("ending_step")]
    public required int EndingStep { get; init; }

    [JsonPropertyName("simulated_ms")]
    public required double SimulatedMilliseconds { get; init; }

    [JsonPropertyName("steps_executed")]
    public required int StepsExecuted { get; init; }

    [JsonPropertyName("total_spikes")]
    public required int TotalSpikes { get; init; }

    [JsonPropertyName("unique_spiking_neurons")]
    public required int UniqueSpikingNeurons { get; init; }

    [JsonPropertyName("active_fraction")]
    public required double ActiveFraction { get; init; }

    [JsonPropertyName("peak_simultaneous_spike_count")]
    public required int PeakSimultaneousSpikeCount { get; init; }

    [JsonPropertyName("peak_simultaneous_spike_fraction")]
    public required double PeakSimultaneousSpikeFraction { get; init; }

    [JsonPropertyName("safety_status")]
    public required string SafetyStatus { get; init; }

    [JsonPropertyName("events")]
    public required List<BrainStepEvent> Events { get; init; }

    [JsonPropertyName("events_truncated")]
    public required bool EventsTruncated { get; init; }

    [JsonPropertyName("returned_event_count")]
    public required int ReturnedEventCount { get; init; }

    [JsonPropertyName("engine_wall_seconds")]
    public required double EngineWallSeconds { get; init; }
}

public sealed record BrainStepEvent(
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("root_id")] long RootId);

public sealed class BrainSessionState
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("simulation_step")]
    public required int SimulationStep { get; init; }

    [JsonPropertyName("simulated_ms")]
    public required double SimulatedMilliseconds { get; init; }

    [JsonPropertyName("total_accumulated_spikes")]
    public required int TotalAccumulatedSpikes { get; init; }

    [JsonPropertyName("active_neuron_count")]
    public required int ActiveNeuronCount { get; init; }

    [JsonPropertyName("safety_status")]
    public required string SafetyStatus { get; init; }

    [JsonPropertyName("membrane_mV")]
    public required BrainMembraneSummary MembraneMillivolts { get; init; }
}

public sealed class SessionRootsPage
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("offset")]
    public required int Offset { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    [JsonPropertyName("returned_count")]
    public required int ReturnedCount { get; init; }

    [JsonPropertyName("total_root_count")]
    public required int TotalRootCount { get; init; }

    [JsonPropertyName("root_ids")]
    public required List<long> RootIds { get; init; }
}

public sealed class SessionLayoutPage
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("offset")]
    public required int Offset { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    [JsonPropertyName("returned_count")]
    public required int ReturnedCount { get; init; }

    [JsonPropertyName("total_root_count")]
    public required int TotalRootCount { get; init; }

    /// <summary>Always "um" (micrometres, FAFB space).</summary>
    [JsonPropertyName("coordinate_unit")]
    public required string CoordinateUnit { get; init; }

    /// <summary>Same order as <see cref="SessionRootsPage.RootIds"/>: ascending numeric root ID.</summary>
    [JsonPropertyName("entries")]
    public required List<NeuronLayoutEntry> Entries { get; init; }
}

public sealed class NeuronLayoutEntry
{
    [JsonPropertyName("root_id")]
    public required long RootId { get; init; }

    [JsonPropertyName("x")]
    public required float X { get; init; }

    [JsonPropertyName("y")]
    public required float Y { get; init; }

    [JsonPropertyName("z")]
    public required float Z { get; init; }

    /// <summary>
    /// False for a root without FlyWire annotation: X/Y/Z are then a placeholder
    /// (mean of annotated graph neighbours) and must not be shown as a real position.
    /// </summary>
    [JsonPropertyName("has_position")]
    public required bool HasPosition { get; init; }

    [JsonPropertyName("super_class")]
    public string? SuperClass { get; init; }

    [JsonPropertyName("cell_class")]
    public string? CellClass { get; init; }

    [JsonPropertyName("cell_type")]
    public string? CellType { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }
}

public sealed class NeuronDiagnostic
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("root_id")]
    public required long RootId { get; init; }

    [JsonPropertyName("membrane_mV")]
    public required double MembraneMillivolts { get; init; }

    [JsonPropertyName("refractory_steps")]
    public required int RefractorySteps { get; init; }

    [JsonPropertyName("cumulative_spike_count")]
    public required int CumulativeSpikeCount { get; init; }

    [JsonPropertyName("first_spike_step")]
    public int? FirstSpikeStep { get; init; }

    [JsonPropertyName("note")]
    public required string Note { get; init; }
}

public sealed class BrainReadoutRequest
{
    [JsonPropertyName("root_ids")]
    public required IReadOnlyList<long> RootIds { get; init; }
}

public sealed class BrainReadoutSnapshot
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("simulation_step")]
    public required int SimulationStep { get; init; }

    [JsonPropertyName("simulated_ms")]
    public required double SimulatedMilliseconds { get; init; }

    [JsonPropertyName("returned_count")]
    public required int ReturnedCount { get; init; }

    [JsonPropertyName("neurons")]
    public required List<BrainReadoutNeuron> Neurons { get; init; }
}

public sealed class BrainReadoutNeuron
{
    [JsonPropertyName("root_id")]
    public required long RootId { get; init; }

    [JsonPropertyName("membrane_mV")]
    public required double MembraneMillivolts { get; init; }

    [JsonPropertyName("refractory_steps")]
    public required int RefractorySteps { get; init; }

    [JsonPropertyName("cumulative_spike_count")]
    public required int CumulativeSpikeCount { get; init; }

    [JsonPropertyName("first_spike_step")]
    public int? FirstSpikeStep { get; init; }

    [JsonPropertyName("note")]
    public required string Note { get; init; }
}

public sealed class BrainMembraneSummary
{
    [JsonPropertyName("min")]
    public required double Minimum { get; init; }

    [JsonPropertyName("mean")]
    public required double Mean { get; init; }

    [JsonPropertyName("max")]
    public required double Maximum { get; init; }
}

public sealed class BrainResetResult
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("simulation_step")]
    public required int SimulationStep { get; init; }

    [JsonPropertyName("reset_wall_seconds")]
    public required double ResetWallSeconds { get; init; }
}

public sealed class BrainServiceErrorEnvelope
{
    [JsonPropertyName("error")]
    public required BrainServiceError Error { get; init; }
}

public sealed class BrainServiceError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }
}
