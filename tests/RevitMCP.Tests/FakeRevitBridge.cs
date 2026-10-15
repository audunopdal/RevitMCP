using System.IO.Pipes;
using RevitMCP.Contracts;

namespace RevitMCP.Tests;

internal sealed class FakeRevitBridge : IAsyncDisposable
{
    private static int _nextProcessId = 100_000_000;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(20));
    private readonly Task<BridgeRequest> _request;

    public int ProcessId { get; } = Interlocked.Increment(ref _nextProcessId);

    public FakeRevitBridge(BridgeResponse response)
    {
        _pipe = new NamedPipeServerStream(BridgeProtocol.PipeName(ProcessId), PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _request = RespondAsync(response);
    }

    public Task<BridgeRequest> Request => _request;

    private async Task<BridgeRequest> RespondAsync(BridgeResponse response)
    {
        await _pipe.WaitForConnectionAsync(_timeout.Token);
        BridgeRequest request = await BridgeProtocol.ReadAsync<BridgeRequest>(_pipe, _timeout.Token);
        await BridgeProtocol.WriteAsync(_pipe, response, _timeout.Token);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        await _timeout.CancelAsync();
        try
        {
            await _request;
        }
        catch (OperationCanceledException) when (_timeout.IsCancellationRequested)
        {
        }
        finally
        {
            _pipe.Dispose();
            _timeout.Dispose();
        }
    }
}
