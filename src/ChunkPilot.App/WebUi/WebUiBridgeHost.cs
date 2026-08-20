using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunkPilot.App.Navigation;
using ChunkPilot.Core;
using Microsoft.Web.WebView2.Core;

namespace ChunkPilot.App.WebUi;

internal sealed class WebUiBridgeHost : IDisposable
{
    private readonly CoreWebView2 webView;
    private readonly MainViewModel viewModel;
    private readonly WebUiSnapshotMapper snapshots;
    private readonly WebUiSnapshotChangeDetector snapshotChanges = new();
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonNode?>> dispatch;
    private readonly Dictionary<string, CancellationTokenSource> activeRequests = new(StringComparer.Ordinal);
    private long eventRevision;
    private bool disposed;

    public WebUiBridgeHost(
        CoreWebView2 webView,
        MainViewModel viewModel,
        WebUiSnapshotMapper snapshots,
        Func<string, JsonObject, CancellationToken, Task<JsonNode?>> dispatch)
    {
        this.webView = webView;
        this.viewModel = viewModel;
        this.snapshots = snapshots;
        this.dispatch = dispatch;
        webView.WebMessageReceived += OnWebMessageReceived;
    }

    public Task PublishSnapshotAsync()
    {
        if (disposed)
            return Task.CompletedTask;
        var snapshot = snapshots.Capture(viewModel);
        if (!snapshotChanges.HasMeaningfulChanges(snapshot))
            return Task.CompletedTask;
        var revision = snapshot["revision"]?.GetValue<long>() ?? 0;
        var envelope = new WebUiEvent(WebUiProtocol.Version, "snapshot.changed", revision, snapshot);
        webView.PostWebMessageAsJson(JsonSerializer.Serialize(envelope, WebUiProtocol.Json));
        return Task.CompletedTask;
    }

    public void PublishOperationCompleted(Guid operationId, string method, Guid serverId, bool success, string? error)
    {
        if (disposed)
            return;
        var payload = JsonSerializer.SerializeToNode(new
        {
            operationId,
            method,
            serverId,
            success,
            error
        }, WebUiProtocol.Json) ?? new JsonObject();
        var envelope = new WebUiEvent(WebUiProtocol.Version, "operation.completed",
            Interlocked.Increment(ref eventRevision), payload);
        webView.PostWebMessageAsJson(JsonSerializer.Serialize(envelope, WebUiProtocol.Json));
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var requestId = "unknown";
        try
        {
            if (!WebUiProtocol.IsTrustedSource(args.Source))
                return;
            var json = args.WebMessageAsJson;
            if (Encoding.UTF8.GetByteCount(json) > WebUiProtocol.MaximumInboundBytes)
            {
                Post(new(WebUiProtocol.Version, requestId, false, Error: new(
                    "message_too_large", "The request exceeded the bridge message limit.")));
                return;
            }

            var request = JsonSerializer.Deserialize<WebUiRequest>(json, WebUiProtocol.Json)
                ?? throw new JsonException("The request envelope was empty.");
            requestId = request.Id;
            if (request.ProtocolVersion != WebUiProtocol.Version)
            {
                Post(new(WebUiProtocol.Version, request.Id, false, Error: new(
                    "protocol_mismatch", "The native host and WebUI use different bridge versions.")));
                return;
            }
            if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128)
                throw new ArgumentException("A bounded request correlation ID is required.");
            if (!WebUiMethodPolicy.IsAllowed(request.Method))
                throw new ArgumentException($"The bridge method '{request.Method}' is not allowed.");

            if (request.Method == "bridge.cancel")
            {
                var targetId = request.Params["requestId"]?.GetValue<string>()?.Trim() ?? "";
                if (targetId.Length is < 1 or > 128)
                    throw new ArgumentException("A bounded request correlation ID is required for cancellation.");
                if (activeRequests.TryGetValue(targetId, out var target))
                    target.Cancel();
                Post(new(WebUiProtocol.Version, request.Id, true,
                    JsonSerializer.SerializeToNode(new { cancelled = true }, WebUiProtocol.Json)));
                return;
            }

            JsonNode? result;
            if (request.Method is "renderer.ready" or "snapshot.get")
                result = request.Method == "renderer.ready"
                    ? JsonSerializer.SerializeToNode(new { ready = true, protocolVersion = WebUiProtocol.Version }, WebUiProtocol.Json)
                    : snapshots.Capture(viewModel);
            else
            {
                using var cancellation = new CancellationTokenSource();
                if (!activeRequests.TryAdd(request.Id, cancellation))
                    throw new ArgumentException("The request correlation ID is already active.");
                try
                {
                    result = await dispatch(request.Method, request.Params, cancellation.Token).ConfigureAwait(true);
                }
                finally
                {
                    activeRequests.Remove(request.Id);
                }
            }
            Post(new(WebUiProtocol.Version, request.Id, true, result));
        }
        catch (JsonException exception)
        {
            Post(new(WebUiProtocol.Version, requestId, false, Error: new(
                "invalid_json", "The native bridge could not read that request.", exception.Message)));
        }
        catch (ArgumentException exception)
        {
            Post(new(WebUiProtocol.Version, requestId, false, Error: new(
                "validation", exception.Message)));
        }
        catch (OperationCanceledException)
        {
            Post(new(WebUiProtocol.Version, requestId, false, Error: new(
                "cancelled", "The operation was cancelled.")));
        }
        catch (TimeoutException exception)
        {
            Post(new(WebUiProtocol.Version, requestId, false, Error: new(
                "timeout", "ChunkPilot did not complete the request in time.", exception.Message)));
        }
        catch (InvalidOperationException exception)
        {
            Post(new(WebUiProtocol.Version, requestId, false, Error: new(
                "conflict", exception.Message)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Post(new(WebUiProtocol.Version, requestId, false, Error: new(
                "unavailable", exception.Message)));
        }
        catch (Exception exception)
        {
            Post(new(WebUiProtocol.Version, requestId, false, Error: new(
                "internal", "ChunkPilot could not complete that request.", SecretRedactor.Redact(exception.Message))));
        }
    }

    private void Post(WebUiResponse response)
    {
        if (!disposed)
            webView.PostWebMessageAsJson(JsonSerializer.Serialize(response, WebUiProtocol.Json));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var cancellation in activeRequests.Values)
            cancellation.Cancel();
        activeRequests.Clear();
        webView.WebMessageReceived -= OnWebMessageReceived;
    }
}

internal sealed class WebUiSnapshotChangeDetector
{
    private JsonNode? lastMeaningfulSnapshot;

    public bool HasMeaningfulChanges(JsonNode snapshot)
    {
        var meaningful = snapshot.DeepClone();
        if (meaningful is JsonObject values)
        {
            values.Remove("revision");
            values.Remove("capturedAt");
        }

        if (lastMeaningfulSnapshot is not null && JsonNode.DeepEquals(lastMeaningfulSnapshot, meaningful))
            return false;

        lastMeaningfulSnapshot = meaningful;
        return true;
    }
}
