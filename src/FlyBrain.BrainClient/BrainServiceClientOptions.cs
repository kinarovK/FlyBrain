namespace FlyBrain.BrainClient;

public sealed class BrainServiceClientOptions
{
    public const string BaseAddressEnvironmentVariable = "TOPO_BRAIN_SERVICE_URL";

    public static Uri DefaultBaseAddress { get; } = new("http://127.0.0.1:8765");

    public Uri BaseAddress { get; init; } = DefaultBaseAddress;

    public static BrainServiceClientOptions FromEnvironment()
    {
        var configuredValue = Environment.GetEnvironmentVariable(BaseAddressEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return new BrainServiceClientOptions();
        }

        if (!Uri.TryCreate(configuredValue, UriKind.Absolute, out var configuredAddress) ||
            (configuredAddress.Scheme != Uri.UriSchemeHttp && configuredAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{BaseAddressEnvironmentVariable} must be an absolute HTTP or HTTPS URL.");
        }

        return new BrainServiceClientOptions { BaseAddress = configuredAddress };
    }
}
