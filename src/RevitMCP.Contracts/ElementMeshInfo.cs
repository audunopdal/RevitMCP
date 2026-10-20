namespace RevitMCP.Contracts;

public sealed record ElementExportInfo(ElementInfo Element, string RevitClass, long? CategoryId,
    string? CategoryType, long? GroupId, long? SuperComponentId, string Status, string Reason);

public sealed record ElementMeshInfo(ElementExportInfo Element, string Unit, PointInfo OriginShared,
    Page<MeshTriangleInfo> Triangles, IReadOnlyList<MeshMaterialInfo> Materials)
{
    public string CoordinateSystem => "shared";
    public string VertexSpace => "relativeToOriginShared";
}

public sealed record MeshTriangleInfo(PointInfo A, PointInfo B, PointInfo C, long? MaterialId);
public sealed record MeshMaterialInfo(long Id, string Name, byte Red, byte Green, byte Blue, int Transparency);
