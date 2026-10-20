using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCP.Addin;

internal sealed partial class RevitRequestHandler
{
    private sealed record ModelTriangle(XYZ A, XYZ B, XYZ C, long? MaterialId);

    private static string? MeshSkipReason(Element element) => element switch
    {
        RevitLinkInstance => "Linked document contents require a separate link export.",
        Group => "Container: export its physical members once, not the group.",
        MEPSystem => "System container: export its physical members once.",
        Autodesk.Revit.DB.View => "View/camera helper, not physical geometry.",
        ElementType => "Element type, not a placed instance.",
        _ when element.Category?.CategoryType == Autodesk.Revit.DB.CategoryType.Annotation
            => "Annotation, not physical model geometry.",
        _ => null
    };

    private static ElementExportInfo ExportInfo(Element element, string status, string reason) =>
        new(new ElementInfo(element.Id.Value, element.UniqueId, element.Name,
                element.Category?.Name, element.GetTypeId().Value),
            element.GetType().Name, element.Category?.Id.Value, element.Category?.CategoryType.ToString(),
            element.GroupId.Value > 0 ? element.GroupId.Value : null,
            element is FamilyInstance { SuperComponent: { } parent } ? parent.Id.Value : null,
            status, reason);

    private static Options MeshOptions() => new()
    {
        DetailLevel = ViewDetailLevel.Fine,
        IncludeNonVisibleObjects = false,
        ComputeReferences = false
    };

    private Page<ElementExportInfo> ClassifyViewElements(Document document, BridgeRequest request)
    {
        Page<long> page = GetPage(GetViewElementIds(document, request.ViewName!), request);
        using Options options = MeshOptions();
        ElementExportInfo[] items = page.Items.Select(id =>
        {
            Element element = document.GetElement(new ElementId(id));
            string? skip = MeshSkipReason(element);
            if (skip is not null)
                return ExportInfo(element, "skipped", skip);
            try
            {
                using GeometryElement? geometry = element.get_Geometry(options);
                bool hasTriangles = geometry is not null && EnumerateTriangles(geometry, Transform.Identity).Any();
                return ExportInfo(element, hasTriangles ? "mesh_fallback" : "skipped",
                    hasTriangles ? "Triangle geometry available; use get_element_mesh."
                        : "No non-degenerate surface triangles in visible Fine-detail geometry.");
            }
            catch (Exception exception) when (exception is Autodesk.Revit.Exceptions.InvalidOperationException
                or Autodesk.Revit.Exceptions.ArgumentException or InvalidOperationException)
            {
                log.TraceEvent(TraceEventType.Error, 0, $"Mesh classification for element {id} failed: {exception}");
                log.Flush();
                return ExportInfo(element, "failed", exception.Message);
            }
        }).ToArray();
        return new Page<ElementExportInfo>(items, page.Total, page.Offset, page.NextOffset);
    }

    private static ElementMeshInfo GetElementMesh(Document document, BridgeRequest request)
    {
        Element element = document.GetElement(new ElementId(request.ElementId!.Value))
            ?? throw new ArgumentException($"Element {request.ElementId} does not exist in the active document.");
        string? skip = MeshSkipReason(element);
        if (skip is not null)
            return new ElementMeshInfo(ExportInfo(element, "skipped", skip), "mm", new PointInfo(0, 0, 0),
                new Page<MeshTriangleInfo>([], 0, request.Offset, null), []);

        using Options options = MeshOptions();
        using GeometryElement? geometry = element.get_Geometry(options);
        var triangles = new List<MeshTriangleInfo>();
        var materialIds = new HashSet<long>();
        PointInfo? originShared = null;
        int total = 0;
        double Millimeters(double value) => UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters);
        PointInfo Vertex(XYZ point)
        {
            PointInfo shared = ToSharedPoint(document.ActiveProjectLocation, point, Millimeters);
            return new PointInfo(shared.X - originShared!.X, shared.Y - originShared.Y, shared.Z - originShared.Z);
        }
        if (geometry is not null)
        {
            foreach (ModelTriangle triangle in EnumerateTriangles(geometry, Transform.Identity))
            {
                originShared ??= ToSharedPoint(document.ActiveProjectLocation, triangle.A, Millimeters);
                if (total >= request.Offset && triangles.Count < request.Limit)
                {
                    triangles.Add(new MeshTriangleInfo(Vertex(triangle.A), Vertex(triangle.B), Vertex(triangle.C),
                        triangle.MaterialId));
                    if (triangle.MaterialId.HasValue)
                        materialIds.Add(triangle.MaterialId.Value);
                }
                total = checked(total + 1);
            }
        }
        MeshMaterialInfo[] materials = materialIds.Order().Select(id =>
        {
            Material material = document.GetElement(new ElementId(id)) as Material
                ?? throw new InvalidOperationException($"Triangle material {id} is not a valid material.");
            Autodesk.Revit.DB.Color color = material.Color;
            return new MeshMaterialInfo(id, material.Name, color.Red, color.Green, color.Blue, material.Transparency);
        }).ToArray();
        int? next = (long)request.Offset + triangles.Count < total ? request.Offset + triangles.Count : null;
        return new ElementMeshInfo(ExportInfo(element, total > 0 ? "mesh_fallback" : "skipped",
                total > 0 ? "Surface triangles; no semantic reconstruction."
                    : "No non-degenerate surface triangles in visible Fine-detail geometry."),
            "mm", originShared ?? new PointInfo(0, 0, 0),
            new Page<MeshTriangleInfo>(triangles, total, request.Offset, next), materials);
    }

    private static IEnumerable<ModelTriangle> EnumerateTriangles(GeometryElement geometry, Transform toModel)
    {
        foreach (GeometryObject part in geometry)
        {
            if (part is GeometryInstance instance)
            {
                using GeometryElement symbol = instance.GetSymbolGeometry();
                using Transform nested = toModel.Multiply(instance.Transform);
                foreach (ModelTriangle triangle in EnumerateTriangles(symbol, nested))
                    yield return triangle;
            }
            else if (part is Solid solid)
            {
                foreach (Face face in solid.Faces)
                {
                    using Mesh mesh = face.Triangulate();
                    foreach (ModelTriangle triangle in MeshTriangles(mesh, toModel, face.MaterialElementId))
                        yield return triangle;
                }
            }
            else if (part is Mesh mesh)
            {
                foreach (ModelTriangle triangle in MeshTriangles(mesh, toModel, mesh.MaterialElementId))
                    yield return triangle;
            }
        }
    }

    private static IEnumerable<ModelTriangle> MeshTriangles(Mesh mesh, Transform toModel, ElementId materialId)
    {
        for (int index = 0; index < mesh.NumTriangles; index++)
        {
            MeshTriangle triangle = mesh.get_Triangle(index);
            XYZ a = toModel.OfPoint(triangle.get_Vertex(0));
            XYZ b = toModel.OfPoint(triangle.get_Vertex(1));
            XYZ c = toModel.OfPoint(triangle.get_Vertex(2));
            if ((b - a).CrossProduct(c - a).GetLength() == 0)
                continue;
            // A reflection reverses winding after vertices are transformed.
            if (toModel.HasReflection)
                (b, c) = (c, b);
            yield return new ModelTriangle(a, b, c, materialId.Value > 0 ? materialId.Value : null);
        }
    }
}
