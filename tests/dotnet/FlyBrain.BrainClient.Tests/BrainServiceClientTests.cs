using System.Net;
using System.Text;
using System.Text.Json;
using FlyBrain.BrainClient;
using Xunit;

namespace FlyBrain.BrainClient.Tests;

public sealed class BrainServiceClientTests
{
    [Fact]
    public async Task GetReadinessAsync_DeserializesTheOpenApiResponse()
    {
        using var httpClient = CreateHttpClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/health/ready", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, """
                {"status":"ready","dataset":"FlyWire FAFB v783","graph_format":"flytrader-sparse-v1","canonical_neuron_count":139255,"startup_timings":{"startup_seconds":1.2},"service_version":"0.1.0"}
                """));
        });
        var client = new BrainServiceClient(httpClient);

        var readiness = await client.GetReadinessAsync();

        Assert.True(readiness.IsReady);
        Assert.Equal(139255, readiness.Result.CanonicalNeuronCount);
        Assert.Equal(1.2, readiness.Result.StartupTimings["startup_seconds"]);
    }

    [Fact]
    public async Task GetBrainInfoAsync_DeserializesRequiredNestedModels()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, """
            {"api_version":"v1","service_version":"0.1.0","source_biology":{"dataset":"FlyWire FAFB v783","canonical_neuron_count":139255,"source_edge_rows":1,"represented_synapses":2,"csr_pair_count":3,"graph_format":"flytrader-sparse-v1"},"model_configuration":{"model":"flytrader-neural-v1","dt_ms":0.1,"weight_transform":"normalized_saturating","available_transmitter_modes":["unsigned"],"supported_execution_modes":["reference"],"external_stimulus_note":"Experimental input."}}
            """)));
        var client = new BrainServiceClient(httpClient);

        var brainInfo = await client.GetBrainInfoAsync();

        Assert.Equal("v1", brainInfo.ApiVersion);
        Assert.Equal("flytrader-neural-v1", brainInfo.ModelConfiguration.Model);
        Assert.Equal(139255, brainInfo.SourceBiology.CanonicalNeuronCount);
    }

    [Fact]
    public async Task StepAsync_SerializesSnakeCaseAndPreservesInt64RootIds()
    {
        const long rootId = 720575940605686886;
        using var httpClient = CreateHttpClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/brain/sessions/session-1/step", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(1, document.RootElement.GetProperty("steps").GetInt32());
            var stimulus = document.RootElement.GetProperty("stimulus")[0];
            Assert.Equal(rootId, stimulus.GetProperty("root_id").GetInt64());
            Assert.Equal(2.0, stimulus.GetProperty("amplitude").GetDouble());
            return JsonResponse(HttpStatusCode.OK, $$"""
                {"api_version":"v1","session_id":"session-1","starting_step":0,"ending_step":1,"simulated_ms":0.1,"steps_executed":1,"total_spikes":1,"unique_spiking_neurons":1,"active_fraction":0.01,"peak_simultaneous_spike_count":1,"peak_simultaneous_spike_fraction":0.01,"safety_status":"completed","events":[{"step":1,"root_id":{{rootId}}}],"events_truncated":false,"returned_event_count":1,"engine_wall_seconds":0.001}
                """);
        });
        var client = new BrainServiceClient(httpClient);

        var result = await client.StepAsync(
            "session-1",
            new BrainStepRequest
            {
                Steps = 1,
                Stimulus = [new BrainStimulus(rootId, 2.0)],
            });

        Assert.Equal(rootId, result.Events.Single().RootId);
    }

    [Fact]
    public async Task GetSessionStateAsync_ExposesStructuredErrorsWithoutMessageParsing()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """
            {"error":{"code":"SESSION_NOT_FOUND","message":"Session was not found."}}
            """)));
        var client = new BrainServiceClient(httpClient);

        var exception = await Assert.ThrowsAsync<BrainServiceException>(
            () => client.GetSessionStateAsync("missing"));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("SESSION_NOT_FOUND", exception.Code);
    }

    [Fact]
    public async Task GetBrainInfoAsync_PropagatesCancellation()
    {
        using var httpClient = CreateHttpClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(HttpStatusCode.OK, "{}");
        });
        var client = new BrainServiceClient(httpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetBrainInfoAsync(cancellation.Token));
    }

    [Fact]
    public async Task GetSessionRootsAsync_DeserializesPagedInt64Roots()
    {
        const long rootId = 720575940602977248;
        using var httpClient = CreateHttpClient((request, _) =>
        {
            Assert.Equal("/api/v1/brain/sessions/test-session/roots", request.RequestUri!.AbsolutePath);
            Assert.Equal("?offset=0&limit=32", request.RequestUri.Query);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, $$"""
                {"session_id":"test-session","offset":0,"limit":32,"returned_count":1,"total_root_count":912,"root_ids":[{{rootId}}]}
                """));
        });
        var client = new BrainServiceClient(httpClient);

        var page = await client.GetSessionRootsAsync("test-session", limit: 32);

        Assert.Equal(0, page.Offset);
        Assert.Equal(32, page.Limit);
        Assert.Equal(912, page.TotalRootCount);
        Assert.Equal(rootId, Assert.Single(page.RootIds));
    }

    [Fact]
    public async Task GetSessionRootsAsync_DeserializesAnEmptyPage()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, """
            {"session_id":"test-session","offset":912,"limit":32,"returned_count":0,"total_root_count":912,"root_ids":[]}
            """)));
        var client = new BrainServiceClient(httpClient);

        var page = await client.GetSessionRootsAsync("test-session", offset: 912, limit: 32);

        Assert.Empty(page.RootIds);
        Assert.Equal(0, page.ReturnedCount);
    }

    [Fact]
    public async Task GetNeuronDiagnosticAsync_DeserializesLargeRootIds()
    {
        const long rootId = 720575940605686886;
        using var httpClient = CreateHttpClient((request, _) =>
        {
            Assert.Equal($"/api/v1/brain/sessions/test-session/neurons/{rootId}", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, $$"""
                {"session_id":"test-session","root_id":{{rootId}},"membrane_mV":-52.0,"refractory_steps":0,"cumulative_spike_count":3,"first_spike_step":1,"note":"Diagnostic model state."}
                """));
        });
        var client = new BrainServiceClient(httpClient);

        var diagnostic = await client.GetNeuronDiagnosticAsync("test-session", rootId);

        Assert.Equal(rootId, diagnostic.RootId);
        Assert.Equal(3, diagnostic.CumulativeSpikeCount);
        Assert.Equal(1, diagnostic.FirstSpikeStep);
    }

    [Fact]
    public async Task GetNeuronDiagnosticAsync_ExposesUnknownRootIdAsStructuredError()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """
            {"error":{"code":"UNKNOWN_ROOT_ID","message":"Diagnostic root is not in this session domain."}}
            """)));
        var client = new BrainServiceClient(httpClient);

        var exception = await Assert.ThrowsAsync<BrainServiceException>(
            () => client.GetNeuronDiagnosticAsync("test-session", 720575940605686886));

        Assert.Equal("UNKNOWN_ROOT_ID", exception.Code);
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task ReadoutAsync_SerializesOrderedInt64RootsAndDeserializesSnapshot()
    {
        const long firstRoot = 720575940605686886;
        const long secondRoot = 720575940602977248;
        using var httpClient = CreateHttpClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/brain/sessions/test-session/readout", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(firstRoot, body.RootElement.GetProperty("root_ids")[0].GetInt64());
            Assert.Equal(secondRoot, body.RootElement.GetProperty("root_ids")[1].GetInt64());
            return JsonResponse(HttpStatusCode.OK, $$"""
                {"session_id":"test-session","simulation_step":161,"simulated_ms":16.1,"returned_count":2,"neurons":[{"root_id":{{firstRoot}},"membrane_mV":-52.0,"refractory_steps":0,"cumulative_spike_count":4,"first_spike_step":0,"note":"Diagnostic model state."},{"root_id":{{secondRoot}},"membrane_mV":-65.0,"refractory_steps":2,"cumulative_spike_count":0,"first_spike_step":null,"note":"Diagnostic model state."}]}
                """);
        });
        var client = new BrainServiceClient(httpClient);

        var snapshot = await client.ReadoutAsync("test-session", [firstRoot, secondRoot]);

        Assert.Equal(161, snapshot.SimulationStep);
        Assert.Equal([firstRoot, secondRoot], snapshot.Neurons.Select(neuron => neuron.RootId));
        Assert.Equal(4, snapshot.Neurons[0].CumulativeSpikeCount);
    }

    [Fact]
    public async Task ReadoutAsync_ExposesStructuredErrorsAndCancellation()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """
            {"error":{"code":"UNKNOWN_ROOT_ID","message":"Readout root is not in this session domain."}}
            """)));
        var client = new BrainServiceClient(httpClient);

        var exception = await Assert.ThrowsAsync<BrainServiceException>(() => client.ReadoutAsync("test-session", [1]));

        Assert.Equal("UNKNOWN_ROOT_ID", exception.Code);
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task ReadoutAsync_PropagatesCancellation()
    {
        using var httpClient = CreateHttpClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(HttpStatusCode.OK, "{}");
        });
        var client = new BrainServiceClient(httpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadoutAsync("session", [1], cancellation.Token));
    }

    private static HttpClient CreateHttpClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        return new HttpClient(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("http://127.0.0.1:8765"),
        };
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
