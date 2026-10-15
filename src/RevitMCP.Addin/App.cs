using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin;

public sealed class App : IExternalApplication
{
	private ExternalEvent? _externalEvent;
	private RevitRequestHandler? _handler;

	// Correct initialization of _bridge and _log
	private PipeBridge? _bridge = null; // Initialize _bridge to null to ensure it exists in the current context.
	private TraceSource? _log = null; // Initialize _log to null to ensure it exists in the current context.

	public Result OnStartup(UIControlledApplication application)
	{
		try
		{
			var settings = LoadSettings();

			string logDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"RevitMCP", "logs");
			Directory.CreateDirectory(logDirectory);

			SourceLevels logLevel = Enum.TryParse(settings.LogLevel, out SourceLevels level) ? level : SourceLevels.Information;
			_log = new TraceSource("RevitMCP", logLevel);

			// Initialize other components (e.g., _handler, _externalEvent, _bridge) here if needed
			_handler = new RevitRequestHandler(_log); // Pass the TraceSource instance to the constructor
			_externalEvent = ExternalEvent.Create(_handler);

			// Initialize the PipeBridge instance
			_bridge = new PipeBridge(_handler, _externalEvent, _log); // Correctly initialize _bridge with required arguments
			_bridge.Start();

			return Result.Succeeded; // Ensure a return value is provided
		}
		catch (Exception ex)
		{
			_log?.TraceEvent(TraceEventType.Error, 0, $"Startup failed: {ex.Message}");
			return Result.Failed; // Return a failure result in case of an exception
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

	private MCPSettings LoadSettings()
	{
		return new MCPSettings
		{
			LogLevel = Environment.GetEnvironmentVariable("REVITMCP_LOGLEVEL") ?? "Information",
			PipeName = Environment.GetEnvironmentVariable("REVITMCP_PIPENAME") ?? "RevitMCP"
			// Add more as needed
		};
	}
}
