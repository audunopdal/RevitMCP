using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RevitMCP.Contracts;

namespace RevitMCP.Server;

public sealed class RevitBridgeClient(ILogger<RevitBridgeClient> logger, int? processId = null)
{
    public async Task<JsonElement> SendAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Revit request {Operation} started.", request.Operation);
        try
        {
            request.Validate();
            int id = processId ?? FindRevitProcess();
            logger.LogInformation("Revit request {Operation} connecting to Revit PID {ProcessId}.", request.Operation, id);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BridgeProtocol.RequestTimeout);
            using var pipe = new NamedPipeClientStream(".", BridgeProtocol.PipeName(id),
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await pipe.ConnectAsync(5000, timeout.Token);
            await BridgeProtocol.WriteAsync(pipe, request, timeout.Token);
            BridgeResponse response = await BridgeProtocol.ReadAsync<BridgeResponse>(pipe, timeout.Token);
            if (response.Error is not null)
                throw new McpException(response.Error);
            JsonElement data = response.Data ?? throw new InvalidDataException("Revit returned a response without data.");
            logger.LogInformation("Revit request {Operation} succeeded in {ElapsedMilliseconds} ms.",
                request.Operation, stopwatch.ElapsedMilliseconds);
            return data;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Revit request {Operation} timed out.", request.Operation);
            throw new McpException("Revit did not respond within 30 seconds. Close modal dialogs or finish the active command and retry.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Revit request {Operation} canceled after {ElapsedMilliseconds} ms.",
                request.Operation, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or ArgumentException or InvalidOperationException or JsonException)
        {
            logger.LogError(exception, "Revit bridge request {Operation} failed.", request.Operation);
            throw new McpException($"Revit bridge failed: {exception.Message}. Ensure Revit 2026 is running and the RevitMCP add-in is installed.");
        }
        catch (McpException exception)
        {
            logger.LogWarning("Revit request {Operation} failed: {Error}", request.Operation, exception.Message);
            throw;
        }
    }

    private static int FindRevitProcess()
    {
        Process[] processes = Process.GetProcessesByName("Revit");
        try
        {
            return processes.Length switch
            {
                0 => throw new InvalidOperationException("No Revit process is running"),
                1 => processes[0].Id,
                _ => throw new InvalidOperationException("Multiple Revit instances are running; configure --revit-process-id")
            };
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }
}
