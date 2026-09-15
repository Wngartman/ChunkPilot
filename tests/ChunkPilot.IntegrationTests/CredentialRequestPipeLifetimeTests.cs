using System.IO.Pipes;
using System.Net;
using System.Text;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class CredentialRequestPipeLifetimeTests
{
    private const string Candidate = "synthetic-native-credential";

    [Fact(Timeout = 10_000)]
    public async Task Closing_native_pipe_during_validation_cancels_http_and_preserves_previous_key()
    {
        await using var connection = await PipePair.ConnectAsync();
        await using var lifetime = new CredentialRequestPipeLifetime(connection.Server, CancellationToken.None);
        var secrets = new MemorySecrets();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api = new CurseForgeApiClient(secrets, new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ValidResponse();
        }));
        var configure = new CurseForgeCredentialSetupService(secrets, api).ConfigureAsync(
            CurseForgeCredentialTransport.ProtectForCurrentUser(Candidate), lifetime.DemandConnected, lifetime.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await connection.Client.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configure.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("existing-synthetic-key", secrets.Value);
        Assert.Equal(0, secrets.Writes);
    }

    [Fact(Timeout = 10_000)]
    public async Task Disconnected_pipe_is_rejected_at_commit_even_before_read_monitor_observes_eof()
    {
        await using var connection = await PipePair.ConnectAsync();
        await using var lifetime = new CredentialRequestPipeLifetime(connection.Server, CancellationToken.None);
        var secrets = new MemorySecrets();
        using var api = new CurseForgeApiClient(secrets, new Handler((_, _) => Task.FromResult(ValidResponse())));
        var demands = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CurseForgeCredentialSetupService(secrets, api)
            .ConfigureAsync(CurseForgeCredentialTransport.ProtectForCurrentUser(Candidate), () =>
            {
                if (++demands == 2)
                    connection.Client.Dispose();
                lifetime.DemandConnected();
            }, lifetime.Token));
        Assert.Equal(2, demands);
        Assert.Equal("existing-synthetic-key", secrets.Value);
    }

    [Fact(Timeout = 10_000)]
    public async Task Successful_request_stops_monitor_without_closing_response_pipe_or_waiting_for_client()
    {
        await using var connection = await PipePair.ConnectAsync();
        var secrets = new MemorySecrets();
        using var api = new CurseForgeApiClient(secrets, new Handler((_, _) => Task.FromResult(ValidResponse())));
        var lifetime = new CredentialRequestPipeLifetime(connection.Server, CancellationToken.None);
        try
        {
            var result = await new CurseForgeCredentialSetupService(secrets, api).ConfigureAsync(
                CurseForgeCredentialTransport.ProtectForCurrentUser(Candidate), lifetime.DemandConnected, lifetime.Token);
            Assert.True(result.Success);
            Assert.Equal(Candidate, secrets.Value);
        }
        finally { await lifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
        // A zero-buffer pipe may not complete its write until the client drains it.
        var responseWrite = connection.Server.WriteAsync(Encoding.UTF8.GetBytes("ok"));
        var received = new byte[2];
        await connection.Client.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await responseWrite.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ok", Encoding.UTF8.GetString(received));
    }

    [Fact(Timeout = 10_000)]
    public async Task Closing_a_request_queued_for_removal_preserves_the_existing_key()
    {
        await using var connection = await PipePair.ConnectAsync();
        await using var lifetime = new CredentialRequestPipeLifetime(connection.Server, CancellationToken.None);
        using var gate = new SemaphoreSlim(0, 1);
        var secrets = new MemorySecrets();
        var pending = RemoveAsync();
        await connection.Client.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("existing-synthetic-key", secrets.Value);

        async Task RemoveAsync()
        {
            await gate.WaitAsync(lifetime.Token);
            try
            {
                lifetime.DemandConnected();
                secrets.Delete(CurseForgeUpdateProvider.ApiKeyName);
            }
            finally { gate.Release(); }
        }
    }

    private static HttpResponseMessage ValidResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"data\":{\"id\":432}}", Encoding.UTF8, "application/json")
    };

    private sealed class MemorySecrets : ISecretStore
    {
        public string? Value { get; private set; } = "existing-synthetic-key";
        public int Writes { get; private set; }
        public bool Contains(string key) => Value is not null;
        public string? GetSecret(string key) => Value;
        public void SetSecret(string key, string value) { Value = value; Writes++; }
        public void Delete(string key) => Value = null;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class PipePair(NamedPipeServerStream server, NamedPipeClientStream client) : IAsyncDisposable
    {
        public NamedPipeServerStream Server { get; } = server;
        public NamedPipeClientStream Client { get; } = client;

        public static async Task<PipePair> ConnectAsync()
        {
            var name = "ChunkPilot-credential-cancel-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var accept = server.WaitForConnectionAsync(timeout.Token);
                await client.ConnectAsync(timeout.Token);
                await accept;
                return new(server, client);
            }
            catch
            {
                await client.DisposeAsync();
                await server.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
