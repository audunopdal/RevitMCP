using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RevitMCP.Contracts;
using RevitMCP.Server;

namespace RevitMCP.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task ServerDiscoversToolsAndCallsBridgeOverStdio()
    {
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new DocumentInfo("MCP integration model", "", false, false)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(new[] { "get_active_document", "get_element_info", "get_selection", "list_door_geometry", "list_doors", "list_elements", "list_levels", "list_view_elements", "list_walls", "list_walls_with_comments" },
            tools.Select(tool => tool.Name).Order().ToArray());
        var elementTool = Assert.Single(tools.Where(tool => tool.Name == "list_elements"));
        Assert.Equal("boolean", elementTool.JsonSchema.GetProperty("properties")
            .GetProperty("modelObjectsOnly").GetProperty("type").GetString());

        var result = await client.CallToolAsync("get_active_document",
            new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var data = JsonDocument.Parse(text);
        Assert.Equal("MCP integration model", data.RootElement.GetProperty("title").GetString());
        Assert.Equal("get_active_document", (await fake.Request).Operation);
    }

    [Fact]
    public async Task RevitErrorBecomesMcpToolError()
    {
        await using var fake = new FakeRevitBridge(BridgeResponse.Failure("No active document."));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync("get_active_document",
            new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.True(result.IsError);
        Assert.Contains("No active document", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }

    [Theory]
    [InlineData("list_levels", 0, 101)]
    [InlineData("list_elements", 0, 101)]
    [InlineData("list_elements", -1, 1)]
    [InlineData("list_elements", 0, 0)]
    [InlineData("list_walls", 0, 101)]
    [InlineData("list_walls", -1, 1)]
    [InlineData("list_walls", 0, 0)]
    [InlineData("list_walls_with_comments", 0, 101)]
    [InlineData("list_walls_with_comments", -1, 1)]
    [InlineData("list_walls_with_comments", 0, 0)]
    [InlineData("list_doors", 0, 101)]
    [InlineData("list_doors", -1, 1)]
    [InlineData("list_doors", 0, 0)]
    [InlineData("list_door_geometry", 0, 6)]
    [InlineData("list_door_geometry", -1, 1)]
    [InlineData("list_door_geometry", 0, 0)]
    [InlineData("list_view_elements", 0, 101)]
    [InlineData("list_view_elements", -1, 1)]
    [InlineData("list_view_elements", 0, 0)]
    public async Task InvalidPaginationBecomesMcpToolError(string operation, int offset, int limit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(100_000_996, timeout.Token);
        var arguments = new Dictionary<string, object?> { ["offset"] = offset, ["limit"] = limit };
        if (operation == "list_view_elements")
            arguments["viewName"] = "Floor Plan";
        var result = await client.CallToolAsync(operation,
            arguments, cancellationToken: timeout.Token);
        Assert.True(result.IsError);
        string expectedMessage = operation == "list_door_geometry" && limit > 5
            ? "between 1 and 5"
            : "between 1 and 100";
        Assert.Contains(expectedMessage, Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }

    [Theory]
    [InlineData("get_selection")]
    [InlineData("list_levels")]
    [InlineData("list_walls")]
    [InlineData("list_walls_with_comments")]
    [InlineData("list_doors")]
    [InlineData("list_door_geometry")]
    [InlineData("list_view_elements")]
    public async Task PaginationArgumentsReachRevit(string operation)
    {
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new Page<long>(new[] { 5_000_000_000L }, 3, 1, 2)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var arguments = new Dictionary<string, object?> { ["offset"] = 1, ["limit"] = 1 };
        if (operation == "list_view_elements")
            arguments["viewName"] = "Floor Plan";
        var result = await client.CallToolAsync(operation,
            arguments, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        BridgeRequest request = await fake.Request;
        Assert.Equal(operation, request.Operation);
        Assert.Equal(1, request.Offset);
        Assert.Equal(1, request.Limit);
        if (operation == "list_view_elements")
            Assert.Equal("Floor Plan", request.ViewName);
    }

    [Fact]
    public async Task ElementToolPreserves64BitIds()
    {
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new ElementInfo(5_000_000_000, "unique-id", "Wall", "Walls", 42)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync("get_element_info",
            new Dictionary<string, object?> { ["elementId"] = 5_000_000_000L }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(5_000_000_000, (await fake.Request).ElementId);
    }

    [Theory]
    [InlineData(0, 1, 1, false)]
    [InlineData(1, 100, null, false)]
    [InlineData(0, 1, 1, true)]
    [InlineData(1, 100, null, true)]
    public async Task ElementPagesPreserveMetadataAndPagination(int offset, int limit, int? nextOffset, bool modelObjectsOnly)
    {
        var element = new ElementInfo(5_000_000_000, "unique-id", "Wall", "Walls", 42);
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new Page<ElementInfo>(new[] { element }, 2, offset, nextOffset)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync("list_elements",
            new Dictionary<string, object?> { ["offset"] = offset, ["limit"] = limit, ["modelObjectsOnly"] = modelObjectsOnly }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        using var data = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Page<ElementInfo>? page = data.RootElement.Deserialize<Page<ElementInfo>>(BridgeProtocol.JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(element, Assert.Single(page.Items));
        Assert.Equal(2, page.Total);
        Assert.Equal(offset, page.Offset);
        Assert.Equal(nextOffset, page.NextOffset);
        Assert.Equal(new BridgeRequest("list_elements", Offset: offset, Limit: limit, ModelObjectsOnly: modelObjectsOnly), await fake.Request);
    }

    [Fact]
    public async Task ElementListingDefaultsAndEmptyPageArePreserved()
    {
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new Page<ElementInfo>(Array.Empty<ElementInfo>(), 0, 0, null)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync("list_elements",
            new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        using var data = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal(0, data.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("nextOffset").ValueKind);
        Assert.Equal(new BridgeRequest("list_elements"), await fake.Request);
    }

    [Fact]
    public async Task ViewElementListingForwardsViewNameAndPagination()
    {
        var element = new ElementInfo(123, "view-element", "Wall", "Walls", 42);
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new Page<ElementInfo>(new[] { element }, 2, 10, 11)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync("list_view_elements",
            new Dictionary<string, object?> { ["viewName"] = "Floor Plan", ["offset"] = 10, ["limit"] = 1 },
            cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        using var data = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Page<ElementInfo>? page = data.RootElement.Deserialize<Page<ElementInfo>>(BridgeProtocol.JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(element, Assert.Single(page.Items));
        Assert.Equal(2, page.Total);
        Assert.Equal(11, page.NextOffset);
        Assert.Equal(new BridgeRequest("list_view_elements", Offset: 10, Limit: 1, ViewName: "Floor Plan"),
            await fake.Request);
    }

    [Theory]
    [InlineData(0, 1, 1, "ft", 1.0, "list_walls")]
    [InlineData(1, 100, null, "ft", 1.0, "list_walls")]
    [InlineData(0, 1, 1, "mm", 304.8, "list_walls")]
    [InlineData(1, 100, null, "mm", 304.8, "list_walls")]
    [InlineData(0, 1, 1, "ft", 1.0, "list_walls_with_comments")]
    [InlineData(1, 100, null, "mm", 304.8, "list_walls_with_comments")]
    public async Task WallPagesPreserveTypeDimensionsAndPagination(int offset, int limit, int? nextOffset,
        string unit, double scale, string operation)
    {
        var wall = new WallInfo(5_000_000_000, 6_000_000_000, "Generic - 200mm", "Basic", 24.5, 0.656, 10.25,
            unit, 24.5 * scale, 0.656 * scale, 10.25 * scale,
            new WallPosition(new PointInfo(-100 * scale, 200 * scale, 3 * scale),
                new PointInfo(-75.5 * scale, 200 * scale, 3 * scale)), "  Exterior wall  ");
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new Page<WallInfo>(new[] { wall }, 2, offset, nextOffset)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync(operation,
            new Dictionary<string, object?> { ["offset"] = offset, ["limit"] = limit }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        using var data = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Page<WallInfo>? page = data.RootElement.Deserialize<Page<WallInfo>>(BridgeProtocol.JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(wall, Assert.Single(page.Items));
        Assert.Equal("  Exterior wall  ", data.RootElement.GetProperty("items")[0].GetProperty("comments").GetString());
        Assert.Equal(2, page.Total);
        Assert.Equal(offset, page.Offset);
        Assert.Equal(nextOffset, page.NextOffset);
        Assert.Equal("shared", data.RootElement.GetProperty("items")[0]
            .GetProperty("position").GetProperty("coordinateSystem").GetString());
        Assert.Equal(new BridgeRequest(operation, Offset: offset, Limit: limit), await fake.Request);
    }

    [Theory]
    [InlineData("list_walls")]
    [InlineData("list_walls_with_comments")]
    public async Task WallListingDefaultsAndEmptyPageArePreserved(string operation)
    {
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new Page<WallInfo>(Array.Empty<WallInfo>(), 0, 0, null)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync(operation,
            new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        using var data = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal(0, data.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, data.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("nextOffset").ValueKind);
        Assert.Equal(new BridgeRequest(operation), await fake.Request);
    }

    [Fact]
    public async Task DoorListingDefaultsAndPaginationArePreserved()
    {
        var door = new ElementInfo(5_000_000_000, "door-unique-id", "Door 900 x 2100", "Doors", 42);
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new Page<ElementInfo>(new[] { door }, 3, 0, 1)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync("list_doors",
            new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        using var data = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Page<ElementInfo>? page = data.RootElement.Deserialize<Page<ElementInfo>>(BridgeProtocol.JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(door, Assert.Single(page.Items));
        Assert.Equal(3, page.Total);
        Assert.Equal(1, page.NextOffset);
        Assert.Equal(new BridgeRequest("list_doors"), await fake.Request);
    }

    [Fact]
    public async Task DoorGeometryListingPreservesMeasurementsAndPagination()
    {
        var edge = new DoorEdgeInfo(0, "Arc", 120, 60, null, null);
        var solid = new DoorSolidInfo(0, 6, 12, 4.5, 20, new DoorBounds(900, 2100, 150), new[] { edge });
        var door = new DoorGeometryInfo(123, "Door", true, "mm", new[] { solid }, Array.Empty<string>());
        await using var fake = new FakeRevitBridge(BridgeResponse.Success(
            new Page<DoorGeometryInfo>(new[] { door }, 2, 0, 1)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await CreateClientAsync(fake.ProcessId, timeout.Token);
        var result = await client.CallToolAsync("list_door_geometry",
            new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        using var data = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Page<DoorGeometryInfo>? page = data.RootElement.Deserialize<Page<DoorGeometryInfo>>(BridgeProtocol.JsonOptions);
        Assert.NotNull(page);
        DoorGeometryInfo actualDoor = Assert.Single(page.Items);
        Assert.Equal(door.Id, actualDoor.Id);
        Assert.Equal(door.Unit, actualDoor.Unit);
        DoorSolidInfo actualSolid = Assert.Single(actualDoor.Solids);
        Assert.Equal(solid.Bounds, actualSolid.Bounds);
        Assert.Equal(edge, Assert.Single(actualSolid.Edges));
        Assert.Equal(2, page.Total);
        Assert.Equal(1, page.NextOffset);
        Assert.Equal(new BridgeRequest("list_door_geometry", Limit: 1), await fake.Request);
    }

    private static Task<McpClient> CreateClientAsync(int processId, CancellationToken cancellationToken)
    {
        string serverPath = typeof(RevitTools).Assembly.Location;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "RevitMCP test",
            Command = "dotnet",
            Arguments = [serverPath, "--revit-process-id", processId.ToString()]
        });
        return McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }
}
