using System.Text.Json;
using RevitMCP.Contracts;

namespace RevitMCP.Tests;

public sealed class ElementMeshTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MeshRequiresPositiveElementId(long? id) =>
        Assert.Throws<ArgumentException>(() => new BridgeRequest("get_element_mesh", ElementId: id).Validate());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void ClassificationRequiresViewName(string? name) =>
        Assert.Throws<ArgumentException>(() => new BridgeRequest("classify_view_elements", ViewName: name).Validate());

    [Fact]
    public async Task MaximumTrianglePageFitsBridgeFrameAndRoundTrips()
    {
        new BridgeRequest("get_element_mesh", ElementId: 5_000_000_000, Limit: 100).Validate();
        new BridgeRequest("classify_view_elements", ViewName: "View", Limit: 100).Validate();
        var triangles = Enumerable.Range(0, 100).Select(index =>
            new MeshTriangleInfo(new PointInfo(index, -1.23456789, 0),
                new PointInfo(index + 1, 10, 0), new PointInfo(index, 0, 1), 42)).ToArray();
        var info = new ElementExportInfo(new ElementInfo(5_000_000_000, "uid", "Unknown", null, -1),
            "DirectShape", null, null, null, null, "mesh_fallback", "Surface triangles");
        var payload = new ElementMeshInfo(info, "mm", new PointInfo(100, 200, 300),
            new Page<MeshTriangleInfo>(triangles, 101, 0, 100),
            new[] { new MeshMaterialInfo(42, "Concrete", 120, 121, 122, 0) });
        using var stream = new MemoryStream();
        await BridgeProtocol.WriteAsync(stream, BridgeResponse.Success(payload), CancellationToken.None);
        Assert.True(stream.Length <= BridgeProtocol.MaxMessageBytes + sizeof(int));
        stream.Position = 0;
        var response = await BridgeProtocol.ReadAsync<BridgeResponse>(stream, CancellationToken.None);
        var restored = response.Data!.Value.Deserialize<ElementMeshInfo>(BridgeProtocol.JsonOptions)!;
        Assert.Equal(triangles, restored.Triangles.Items);
        Assert.Equal(100, restored.Triangles.NextOffset);
        Assert.Equal("shared", restored.CoordinateSystem);
        Assert.Equal(payload.OriginShared, restored.OriginShared);
    }
}
