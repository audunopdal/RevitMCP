using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RevitMCP.Contracts;
using RevitMCP.Server;

namespace RevitMCP.Tests;

public sealed class FileLoggerTests
{
    [Fact]
    public void FileLoggerAppendsFlushesAndPreservesExceptions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"RevitMCP-log-{Guid.NewGuid():N}.log");
        try
        {
            for (int i = 0; i < 2; i++)
            {
                using var provider = new FileLoggerProvider(path);
                ILogger logger = provider.CreateLogger("TestCategory");
                logger.LogError(new InvalidOperationException("test failure"), "Entry {Index}", i);
                logger.Log(LogLevel.None, "Must not be written");
                string text = ReadActiveLog(path);
                Assert.Contains($"Entry {i}", text);
                Assert.Contains("[Error] TestCategory", text);
                Assert.Contains("System.InvalidOperationException: test failure", text);
                Assert.DoesNotContain("Must not be written", text);
                Assert.True(DateTimeOffset.TryParse(text.Split(' ')[0], out _));
            }
            Assert.Contains("Entry 0", File.ReadAllText(path));
            Assert.Contains("Entry 1", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ConcurrentEntriesAreNotLost()
    {
        string path = Path.Combine(Path.GetTempPath(), $"RevitMCP-log-{Guid.NewGuid():N}.log");
        try
        {
            using (var provider = new FileLoggerProvider(path))
            {
                ILogger logger = provider.CreateLogger("Concurrent");
                Parallel.For(0, 100, index => logger.LogInformation("Entry {Index}", index));
            }
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(100, lines.Length);
            Assert.Equal(100, lines.Distinct().Count());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BridgeRequestLifecycleIsWrittenToFile(bool fail)
    {
        string path = Path.Combine(Path.GetTempPath(), $"RevitMCP-log-{Guid.NewGuid():N}.log");
        try
        {
            await using var fake = new FakeRevitBridge(fail
                ? BridgeResponse.Failure("Test Revit error")
                : BridgeResponse.Success(new DocumentInfo("Payload must not be logged", "", false, false)));
            using var provider = new FileLoggerProvider(path);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            var client = new RevitBridgeClient(factory.CreateLogger<RevitBridgeClient>(), fake.ProcessId);
            if (fail)
                await Assert.ThrowsAsync<McpException>(() =>
                    client.SendAsync(new BridgeRequest("get_active_document"), CancellationToken.None));
            else
                await client.SendAsync(new BridgeRequest("get_active_document"), CancellationToken.None);

            string text = ReadActiveLog(path);
            Assert.Contains("Revit request get_active_document started.", text);
            Assert.Contains($"connecting to Revit PID {fake.ProcessId}", text);
            Assert.Contains(fail ? "failed: Test Revit error" : "succeeded in", text);
            Assert.DoesNotContain("Payload must not be logged", text);
        }
        finally
        {
            File.Delete(path);
        }

    }

    private static string ReadActiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
