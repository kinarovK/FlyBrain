using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyBrain.BrainClient;

public sealed class BrainServiceClient : IBrainServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HttpClient _httpClient;

    public BrainServiceClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<BrainReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("health/ready", cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
        {
            throw await CreateExceptionAsync(response, cancellationToken);
        }

        var result = await ReadJsonAsync<BrainReadyResult>(response.Content, cancellationToken);
        return new BrainReadiness(response.StatusCode, result);
    }

    public Task<BrainInfo> GetBrainInfoAsync(CancellationToken cancellationToken = default) =>
        SendForJsonAsync<BrainInfo>(
            new HttpRequestMessage(HttpMethod.Get, "api/v1/brain/info"),
            cancellationToken);

    public Task<BrainSession> CreateSessionAsync(
        CreateBrainSessionRequest request,
        CancellationToken cancellationToken = default) =>
        SendForJsonAsync<BrainSession>(
            CreateJsonRequest(HttpMethod.Post, "api/v1/brain/sessions", request),
            cancellationToken);

    public Task<BrainStepResult> StepAsync(
        string sessionId,
        BrainStepRequest request,
        CancellationToken cancellationToken = default) =>
        SendForJsonAsync<BrainStepResult>(
            CreateJsonRequest(HttpMethod.Post, $"api/v1/brain/sessions/{EscapeSessionId(sessionId)}/step", request),
            cancellationToken);

    public Task<BrainSessionState> GetSessionStateAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        SendForJsonAsync<BrainSessionState>(
            new HttpRequestMessage(HttpMethod.Get, $"api/v1/brain/sessions/{EscapeSessionId(sessionId)}/state"),
            cancellationToken);

    public Task<SessionRootsPage> GetSessionRootsAsync(
        string sessionId,
        int offset = 0,
        int limit = 64,
        CancellationToken cancellationToken = default) =>
        SendForJsonAsync<SessionRootsPage>(
            new HttpRequestMessage(
                HttpMethod.Get,
                $"api/v1/brain/sessions/{EscapeSessionId(sessionId)}/roots?offset={offset.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}"),
            cancellationToken);

    public Task<NeuronDiagnostic> GetNeuronDiagnosticAsync(
        string sessionId,
        long rootId,
        CancellationToken cancellationToken = default) =>
        SendForJsonAsync<NeuronDiagnostic>(
            new HttpRequestMessage(
                HttpMethod.Get,
                $"api/v1/brain/sessions/{EscapeSessionId(sessionId)}/neurons/{rootId.ToString(CultureInfo.InvariantCulture)}"),
            cancellationToken);

    public Task<BrainReadoutSnapshot> ReadoutAsync(
        string sessionId,
        IReadOnlyList<long> rootIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootIds);
        return SendForJsonAsync<BrainReadoutSnapshot>(
            CreateJsonRequest(
                HttpMethod.Post,
                $"api/v1/brain/sessions/{EscapeSessionId(sessionId)}/readout",
                new BrainReadoutRequest { RootIds = rootIds }),
            cancellationToken);
    }

    public Task<BrainResetResult> ResetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        SendForJsonAsync<BrainResetResult>(
            new HttpRequestMessage(HttpMethod.Post, $"api/v1/brain/sessions/{EscapeSessionId(sessionId)}/reset"),
            cancellationToken);

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api/v1/brain/sessions/{EscapeSessionId(sessionId)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken);
        }
    }

    private async Task<T> SendForJsonAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        using (var response = await _httpClient.SendAsync(request, cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateExceptionAsync(response, cancellationToken);
            }

            return await ReadJsonAsync<T>(response.Content, cancellationToken);
        }
    }

    private static HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string requestUri, T payload) =>
        new(method, requestUri)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

    private static async Task<T> ReadJsonAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Brain Service returned an empty JSON response.");
    }

    private static async Task<BrainServiceException> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        BrainServiceError? error = null;
        if (response.Content.Headers.ContentLength is not 0)
        {
            try
            {
                var envelope = await ReadJsonAsync<BrainServiceErrorEnvelope>(response.Content, cancellationToken);
                error = envelope.Error;
            }
            catch (JsonException)
            {
                // The HTTP status still remains available when a nonconforming error body is returned.
            }
        }

        error ??= new BrainServiceError
        {
            Code = "HTTP_ERROR",
            Message = $"Brain Service returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
        };

        return new BrainServiceException(response.StatusCode, error);
    }

    private static string EscapeSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Uri.EscapeDataString(sessionId);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
