using System.Buffers.Binary;
using System.Text.Json;
using RevitMCP.Contracts;

namespace RevitMCP.Tests;

public sealed class BridgeProtocolTests
{
    [Fact]
    public async Task RequestRoundTripPreserves64BitIds()
    {
        var request = new BridgeRequest("get_element_info", ElementId: 5_000_000_000);
        using var stream = new MemoryStream();
        await BridgeProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(request, await BridgeProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelObjectFilterRoundTrips(bool modelObjectsOnly)
    {
        var request = new BridgeRequest("list_elements", Offset: 100, ModelObjectsOnly: modelObjectsOnly);
        using var stream = new MemoryStream();
        await BridgeProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(request, await BridgeProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public void LegacyRequestDefaultsToAllElements()
    {
        BridgeRequest? request = JsonSerializer.Deserialize<BridgeRequest>(
            """{"operation":"list_elements","offset":0,"limit":100}""", BridgeProtocol.JsonOptions);
        Assert.NotNull(request);
        Assert.False(request.ModelObjectsOnly);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(BridgeProtocol.MaxMessageBytes + 1)]
    public async Task InvalidFrameLengthIsRejected(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BridgeProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedFrameIsRejected()
    {
        using var stream = new MemoryStream(new byte[] { 10, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            BridgeProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedWriteIsRejected()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BridgeProtocol.WriteAsync(stream, new string('x', BridgeProtocol.MaxMessageBytes), CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData("unknown", null, 0, 10)]
    [InlineData("get_selection", null, -1, 10)]
    [InlineData("get_selection", null, 0, 0)]
    [InlineData("list_levels", null, 0, 101)]
    [InlineData("list_walls", null, 0, 101)]
    [InlineData("list_walls", null, -1, 100)]
    [InlineData("list_walls", null, 0, 0)]
    [InlineData("list_walls_with_comments", null, 0, 101)]
    [InlineData("list_walls_with_comments", null, -1, 100)]
    [InlineData("list_walls_with_comments", null, 0, 0)]
    [InlineData("list_doors", null, 0, 101)]
    [InlineData("list_doors", null, -1, 100)]
    [InlineData("list_doors", null, 0, 0)]
    [InlineData("list_door_geometry", null, 0, 6)]
    [InlineData("list_door_geometry", null, -1, 1)]
    [InlineData("list_door_geometry", null, 0, 0)]
    [InlineData("list_view_elements", null, 0, 101)]
    [InlineData("list_view_elements", null, -1, 100)]
    [InlineData("list_view_elements", null, 0, 0)]
    [InlineData("get_element_info", null, 0, 10)]
    [InlineData("get_element_info", 0L, 0, 10)]
    [InlineData("get_element_info", -1L, 0, 10)]
    public void InvalidRequestIsRejected(string operation, long? id, int offset, int limit) =>
        Assert.Throws<ArgumentException>(() => new BridgeRequest(operation, id, offset, limit).Validate());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ViewElementRequestRequiresViewName(string? viewName) =>
        Assert.Throws<ArgumentException>(() =>
            new BridgeRequest("list_view_elements", ViewName: viewName).Validate());

    [Fact]
    public async Task ViewElementRequestPreservesViewName()
    {
        var request = new BridgeRequest("list_view_elements", Offset: 20, Limit: 25, ViewName: "Level 1 - Floor Plan");
        request.Validate();
        using var stream = new MemoryStream();
        await BridgeProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(request, await BridgeProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void ValidPageLimitsAreAccepted(int limit) =>
        new BridgeRequest("list_levels", Offset: int.MaxValue, Limit: limit).Validate();

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void ValidDoorGeometryPageLimitsAreAccepted(int limit) =>
        new BridgeRequest("list_door_geometry", Offset: int.MaxValue, Limit: limit).Validate();

    [Fact]
    public void DoorGeometryPayloadPreservesMeasurements()
    {
        var edge = new DoorEdgeInfo(0, "Arc", 120, 60, null, null);
        var solid = new DoorSolidInfo(0, 6, 12, 4.5, 20,
            new DoorBounds(900, 2100, 150), new[] { edge },
            new DoorComponentInfo("root/instance[0]/solid[1]",
                new DoorLocalBounds(new PointInfo(0, 0, 0), new PointInfo(3, 7, 1), 3, 7, 1),
                new DoorTransformInfo(new PointInfo(100, 200, 0),
                    new VectorInfo(0, 1, 0), new VectorInfo(-1, 0, 0), new VectorInfo(0, 0, 1))));
        var door = new DoorGeometryInfo(123, "Door", true, "mm", new[] { solid }, Array.Empty<string>(),
            new DoorTransformInfo(new PointInfo(100, 200, 0),
                new VectorInfo(0, 1, 0), new VectorInfo(-1, 0, 0), new VectorInfo(0, 0, 1)),
            new PointInfo(100, 200, 0), new VectorInfo(0, 1, 0), new VectorInfo(-1, 0, 0),
            false, true, 456, new DoorOpeningInfo(950, 2100, 0, 789, 1000, 1000, 3100));
        JsonElement data = BridgeResponse.Success(new Page<DoorGeometryInfo>(
            new[] { door }, 1, 0, null)).Data!.Value;
        JsonElement item = data.GetProperty("items")[0];
        Assert.Equal(123, item.GetProperty("id").GetInt64());
        Assert.True(item.GetProperty("geometryAvailable").GetBoolean());
        Assert.Equal(900, item.GetProperty("solids")[0].GetProperty("bounds").GetProperty("xExtent").GetDouble());
        Assert.Equal(456, item.GetProperty("hostWallId").GetInt64());
        Assert.Equal(100, item.GetProperty("insertionPointShared").GetProperty("x").GetDouble());
        Assert.Equal(950, item.GetProperty("opening").GetProperty("roughWidth").GetDouble());
        JsonElement component = item.GetProperty("solids")[0].GetProperty("component");
        Assert.Equal("root/instance[0]/solid[1]", component.GetProperty("path").GetString());
        Assert.Equal(3, component.GetProperty("bounds").GetProperty("xExtent").GetDouble());
        Assert.Equal("shared", component.GetProperty("transformShared").GetProperty("coordinateSystem").GetString());
        JsonElement serializedEdge = item.GetProperty("solids")[0].GetProperty("edges")[0];
        Assert.Equal(120, serializedEdge.GetProperty("length").GetDouble());
        Assert.Equal(60, serializedEdge.GetProperty("radius").GetDouble());
        Assert.Equal(JsonValueKind.Null, serializedEdge.GetProperty("radiusX").ValueKind);
    }

    [Fact]
    public void SuccessPayloadUsesDocumentedPropertyNames()
    {
        BridgeResponse response = BridgeResponse.Success(new Page<LevelInfo>(
            new[] { new LevelInfo(5_000_000_000, "Level 1", 12.5) }, 2, 0, 1));
        JsonElement data = response.Data!.Value;
        Assert.Equal(1, data.GetProperty("nextOffset").GetInt32());
        Assert.Equal(5_000_000_000, data.GetProperty("items")[0].GetProperty("id").GetInt64());
        Assert.Equal(12.5, data.GetProperty("items")[0].GetProperty("elevationFeet").GetDouble());
        Assert.Null(response.Error);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("nb-NO")]
    public void WallDimensionsAreFormattedWithoutGrouping(string culture)
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            var wall = new WallInfo(1, 2, "Wall", "Basic", null, 0, null,
                "mm", 7800.125, 200, 9600, null);
            JsonElement data = BridgeResponse.Success(wall).Data!.Value;
            Assert.Equal("7800.13", data.GetProperty("lengthFormatted").GetString());
            Assert.Equal("200", data.GetProperty("widthFormatted").GetString());
            Assert.Equal("9600", data.GetProperty("heightFormatted").GetString());
            Assert.Equal(7800.125, data.GetProperty("length").GetDouble());
            var unavailable = wall with { Length = null, Height = null };
            Assert.Null(unavailable.LengthFormatted);
            Assert.Null(unavailable.HeightFormatted);
            Assert.Equal("0", (wall with { Width = 0 }).WidthFormatted);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(24.5, 10.25, "ft", 1.0)]
    [InlineData(24.5, 10.25, "mm", 304.8)]
    [InlineData(null, null, "mm", 304.8)]
    [InlineData(0.0, 0.0, "ft", 1.0)]
    public async Task WallPayloadPreservesDimensionsAndPropertyNames(double? length, double? height,
        string unit, double scale)
    {
        new BridgeRequest("list_walls").Validate();
        var info = new WallInfo(5_000_000_000, 6_000_000_000, "Wall type", "Basic", length, 0.5, height,
            unit, length * scale, 0.5 * scale, height * scale,
            length.HasValue ? new WallPosition(new PointInfo(-100 * scale, 200 * scale, 3 * scale),
                new PointInfo(-75.5 * scale, 200 * scale, 3 * scale)) : null);
        BridgeResponse response = BridgeResponse.Success(new Page<WallInfo>(
            new[] { info },
            1, 0, null));
        using var stream = new MemoryStream();
        await BridgeProtocol.WriteAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        BridgeResponse result = await BridgeProtocol.ReadAsync<BridgeResponse>(stream, CancellationToken.None);
        JsonElement wall = result.Data!.Value.GetProperty("items")[0];
        Assert.Equal(5_000_000_000, wall.GetProperty("id").GetInt64());
        Assert.Equal(6_000_000_000, wall.GetProperty("typeId").GetInt64());
        Assert.Equal("Wall type", wall.GetProperty("typeName").GetString());
        Assert.Equal("Basic", wall.GetProperty("kind").GetString());
        Assert.Equal(length, wall.GetProperty("lengthFeet").Deserialize<double?>());
        Assert.Equal(0.5, wall.GetProperty("widthFeet").GetDouble());
        Assert.Equal(height, wall.GetProperty("heightFeet").Deserialize<double?>());
        Assert.Equal(unit, wall.GetProperty("unit").GetString());
        Assert.Equal(length * scale, wall.GetProperty("length").Deserialize<double?>());
        Assert.Equal(0.5 * scale, wall.GetProperty("width").GetDouble());
        Assert.Equal(height * scale, wall.GetProperty("height").Deserialize<double?>());
        Assert.Equal(info, wall.Deserialize<WallInfo>(BridgeProtocol.JsonOptions));
        if (length.HasValue)
        {
            JsonElement position = wall.GetProperty("position");
            Assert.Equal("shared", position.GetProperty("coordinateSystem").GetString());
            Assert.Equal(-100 * scale, position.GetProperty("start").GetProperty("x").GetDouble());
            Assert.Equal(200 * scale, position.GetProperty("start").GetProperty("y").GetDouble());
            Assert.Equal(3 * scale, position.GetProperty("start").GetProperty("z").GetDouble());
            Assert.Equal(-75.5 * scale, position.GetProperty("end").GetProperty("x").GetDouble());
        }
        else
            Assert.Equal(JsonValueKind.Null, wall.GetProperty("position").ValueKind);
    }
}
