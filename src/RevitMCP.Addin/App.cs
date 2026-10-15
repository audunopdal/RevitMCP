using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin;

public sealed class App : IExternalApplication
{
    private ExternalEvent? _externalEvent;
    private RevitRequestHandler? _handler;
    private PipeBridge? _bridge;
    private TraceSource? _log;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "logs");
            Directory.CreateDirectory(logDirectory);
            _log = new TraceSource("RevitMCP", SourceLevels.All);
            _log.Listeners.Clear();
            _log.Listeners.Add(new TextWriterTraceListener(new StreamWriter(
                new FileStream(Path.Combine(logDirectory, $"revit-{Environment.ProcessId}.log"),
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true })
            {
                TraceOutputOptions = TraceOptions.DateTime
            });
            _handler = new RevitRequestHandler(_log);
            _externalEvent = ExternalEvent.Create(_handler);
            _bridge = new PipeBridge(_handler, _externalEvent, _log);
            _bridge.Start();
            _log.TraceEvent(TraceEventType.Information, 0, "RevitMCP read-only bridge started.");
            _log.Flush();
            application.ControlledApplication.WriteJournalComment("RevitMCP read-only bridge started.", false);
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            Autodesk.Revit.UI.TaskDialog.Show("RevitMCP startup failed", exception.ToString());
            Cleanup();
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Cleanup();
        return Result.Succeeded;
    }

    private void Cleanup()
    {
        _bridge?.Dispose();
        _handler?.CancelPending();
        _externalEvent?.Dispose();
        _log?.TraceEvent(TraceEventType.Information, 0, "RevitMCP read-only bridge stopped.");
        _log?.Close();
    }
}
