using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using RevitMCP.Contracts;
using RevitMCP.Server;

namespace RevitMCP.Tests;

public sealed class RevitBridgeClientTests
{
    [Fact]
    public async Task ClientReturnsDataOverNamedPipe()
    {
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new DocumentInfo("Test model", "", false, false)));
        var client = new RevitBridgeClient(NullLogger<RevitBridgeClient>.Instance, fake.ProcessId);
        var data = await client.SendAsync(new BridgeRequest("get_active_document"), CancellationToken.None);
        Assert.Equal("Test model", data.GetProperty("title").GetString());
        Assert.Equal("get_active_document", (await fake.Request).Operation);
    }

    [Fact]
    public async Task RevitErrorsAreNotReturnedAsSuccess()
    {
        await using var fake = new FakeRevitBridge(BridgeResponse.Failure("No active document."));
        var client = new RevitBridgeClient(NullLogger<RevitBridgeClient>.Instance, fake.ProcessId);
        McpException exception = await Assert.ThrowsAsync<McpException>(() =>
            client.SendAsync(new BridgeRequest("get_active_document"), CancellationToken.None));
        Assert.Contains("No active document", exception.Message);
    }

    [Fact]
    public async Task InvalidInputsProduceToolErrorsBeforeConnecting()
    {
        var client = new RevitBridgeClient(NullLogger<RevitBridgeClient>.Instance, 100_000_999);
        McpException exception = await Assert.ThrowsAsync<McpException>(() =>
            client.SendAsync(new BridgeRequest("list_levels", Limit: 101), CancellationToken.None));
        Assert.Contains("between 1 and 100", exception.Message);
    }

    [Fact]
    public async Task EmptyResponseIsRejected()
    {
        await using var fake = new FakeRevitBridge(new BridgeResponse(null, null));
        var client = new RevitBridgeClient(NullLogger<RevitBridgeClient>.Instance, fake.ProcessId);
        McpException exception = await Assert.ThrowsAsync<McpException>(() =>
            client.SendAsync(new BridgeRequest("get_active_document"), CancellationToken.None));
        Assert.Contains("without data", exception.Message);
    }

    [Fact]
    public async Task MissingAddinProducesConnectionError()
    {
        var client = new RevitBridgeClient(NullLogger<RevitBridgeClient>.Instance, 100_000_998);
        McpException exception = await Assert.ThrowsAsync<McpException>(() =>
            client.SendAsync(new BridgeRequest("get_active_document"), CancellationToken.None));
        Assert.Contains("bridge failed", exception.Message);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        const int processId = 100_000_997;
        using var pipe = new NamedPipeServerStream(BridgeProtocol.PipeName(processId),
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var client = new RevitBridgeClient(NullLogger<RevitBridgeClient>.Instance, processId);
        Task connection = pipe.WaitForConnectionAsync(cancellation.Token);
        Task request = client.SendAsync(new BridgeRequest("get_active_document"), cancellation.Token);
        await connection;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }
}
