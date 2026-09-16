using System.Net;
using System.Text.Json;

namespace ChunkPilot.Infrastructure;

public sealed partial class CurseForgeApiClient
{
    private const int MaximumServiceErrorBytes = 4_096;

    private static async Task<CurseForgeApiException> MapServiceFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = MapStatus(response.StatusCode, applicationService: true);
        if (response.Content.Headers.ContentLength is > MaximumServiceErrorBytes ||
            !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json",
                StringComparison.OrdinalIgnoreCase))
            return fallback;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaximumServiceErrorBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumServiceErrorBytes) return fallback;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.Number ||
                !protocol.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
                return fallback;
            // Only known protocol codes select local copy. Never display a remote message, URL,
            // body, or unknown code, even when a service response is malformed or compromised.
            var message = (response.StatusCode, error.GetString()) switch
            {
                (HttpStatusCode.TooManyRequests, "client_rate_limited") =>
                    "Too many CurseForge requests were made from this network. Try again in a minute.",
                (HttpStatusCode.TooManyRequests, "provider_rate_limited") =>
                    "CurseForge is temporarily limiting requests. Try again shortly.",
                (HttpStatusCode.TooManyRequests, "service_capacity_reached") =>
                    "ChunkPilot's CurseForge service has reached its daily capacity. Try again later.",
                (HttpStatusCode.TooManyRequests, "service_concurrency_reached") =>
                    "All CurseForge service download slots are busy. Try again shortly.",
                (HttpStatusCode.Forbidden, "author_distribution_unavailable") =>
                    "The author has not enabled this file for third-party downloads.",
                (HttpStatusCode.ServiceUnavailable, "provider_unavailable") =>
                    "The ChunkPilot service cannot currently authenticate with CurseForge. No personal key is required; try again later.",
                (HttpStatusCode.ServiceUnavailable, "service_not_configured") =>
                    "ChunkPilot's CurseForge service is temporarily unavailable. Try again later.",
                _ => null
            };
            return message is null ? fallback : new CurseForgeApiException(fallback.Kind, message, response.StatusCode);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return fallback;
        }
    }
}
