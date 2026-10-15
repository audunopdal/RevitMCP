using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Autodesk.Revit.UI;
using RevitMCP.Contracts;

namespace RevitMCP.Addin;

internal sealed class PipeBridge(RevitRequestHandler handler, ExternalEvent externalEvent, TraceSource log) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;

    public void Start() => _listener = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(BridgeProtocol.PipeName(Environment.ProcessId),
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                await HandleConnectionAsync(pipe);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            log.TraceEvent(TraceEventType.Critical, 0, $"Bridge listener stopped: {exception}");
            log.Flush();
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(BridgeProtocol.RequestTimeout);
        TaskCompletionSource<BridgeResponse>? completion = null;
        string operation = "(unread)";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            BridgeRequest request = await BridgeProtocol.ReadAsync<BridgeRequest>(pipe, timeout.Token);
            operation = request.Operation;
            log.TraceEvent(TraceEventType.Information, 0, $"Revit request {operation} received.");
            log.Flush();
            request.Validate();
            completion = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            handler.Enqueue(new PendingRequest(request, completion));
            ExternalEventRequest result = externalEvent.Raise();
            if (result is not (ExternalEventRequest.Accepted or ExternalEventRequest.Pending))
                completion.TrySetResult(BridgeResponse.Failure($"Revit rejected the external event: {result}. Retry when Revit is idle."));
            BridgeResponse response = await completion.Task;
            await BridgeProtocol.WriteAsync(pipe, response, timeout.Token);
            log.TraceEvent(response.Error is null ? TraceEventType.Information : TraceEventType.Warning, 0,
                $"Revit request {operation} {(response.Error is null ? "succeeded" : "failed")}; response sent in {stopwatch.ElapsedMilliseconds} ms.");
            log.Flush();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                log.TraceEvent(TraceEventType.Warning, 0, $"Revit request {operation} timed out after {stopwatch.ElapsedMilliseconds} ms.");
                log.Flush();
            }
        }
        catch (Exception exception)
        {
            log.TraceEvent(TraceEventType.Error, 0, $"Revit request {operation} failed: {exception}");
            log.Flush();
            if (pipe.IsConnected && !timeout.IsCancellationRequested)
            {
                try
                {
                    await BridgeProtocol.WriteAsync(pipe, BridgeResponse.Failure(exception.Message), timeout.Token);
                }
                catch (Exception writeException) when (writeException is IOException or OperationCanceledException)
                {
                    log.TraceEvent(TraceEventType.Error, 0, $"Cannot send bridge error: {writeException}");
                    log.Flush();
                }
            }
        }
        finally
        {
            completion?.TrySetCanceled();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        handler.CancelPending();
        _listener?.GetAwaiter().GetResult();
        _shutdown.Dispose();
    }
}
