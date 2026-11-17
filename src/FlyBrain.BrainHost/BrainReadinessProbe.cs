using System.Net;
using System.Text.Json;

namespace FlyBrain.BrainHost;

public enum ProbeOutcome
{
    Unreachable,
    Starting,
    Ready,
    Failed,
}

public sealed record ProbeResult(ProbeOutcome Outcome, string Detail);

public interface IBrainReadinessProbe
{
    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Reads <c>/health/ready</c> of the brain service.</summary>
public sealed class HttpBrainReadinessProbe(HttpClient httpClient) : IBrainReadinessProbe
{
    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await httpClient.GetAsync("health/ready", timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
            {
                return new ProbeResult(ProbeOutcome.Unreachable, $"Unexpected HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var status = ReadStatus(body);
            return status switch
            {
                "ready" => new ProbeResult(ProbeOutcome.Ready, "Brain is ready."),
                "failed" => new ProbeResult(ProbeOutcome.Failed, "Brain service reported that the real graph could not be validated."),
                _ => new ProbeResult(ProbeOutcome.Starting, $"Brain service status: {status ?? "unknown"}."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(ProbeOutcome.Unreachable, "Health check timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new ProbeResult(ProbeOutcome.Unreachable, exception.Message);
        }
    }

    internal static string? ReadStatus(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
