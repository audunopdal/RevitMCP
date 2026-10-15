using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RevitMCP.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "RevitMCP", "logs", $"server-{Environment.ProcessId}.log");
builder.Logging.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(logPath));

int? revitProcessId = null;
string? configuredId = builder.Configuration["revit-process-id"];
if (configuredId is not null)
{
    if (!int.TryParse(configuredId, out int id) || id <= 0)
        throw new ArgumentException("--revit-process-id must be a positive integer.");
    revitProcessId = id;
}

builder.Services.AddSingleton(provider =>
    new RevitBridgeClient(provider.GetRequiredService<ILogger<RevitBridgeClient>>(), revitProcessId));
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RevitTools>();

using var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RevitMCP.Server")
    .LogInformation("Server starting. File log: {LogPath}", logPath);
await host.RunAsync();
