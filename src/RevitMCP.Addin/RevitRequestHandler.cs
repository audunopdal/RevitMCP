using System.Collections.Concurrent;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Contracts;

namespace RevitMCP.Addin;

internal sealed record PendingRequest(BridgeRequest Request, TaskCompletionSource<BridgeResponse> Completion);

internal sealed partial class RevitRequestHandler(TraceSource log) : IExternalEventHandler
{
    private readonly ConcurrentQueue<PendingRequest> _requests = new();

    public void Enqueue(PendingRequest request) => _requests.Enqueue(request);
    public string GetName() => "RevitMCP read-only requests";

    public void Execute(UIApplication app)
    {
        while (_requests.TryDequeue(out PendingRequest? pending))
        {
            if (pending.Completion.Task.IsCompleted)
                continue;
            try
            {
                pending.Request.Validate();
                UIDocument uiDocument = app.ActiveUIDocument
                    ?? throw new InvalidOperationException("No active document. Open a model in Revit and retry.");
                Document document = uiDocument.Document;
                BridgeResponse response = pending.Request.Operation switch
                {
                    "get_active_document" => BridgeResponse.Success(new DocumentInfo(
                        document.Title, document.PathName, document.IsFamilyDocument, document.IsReadOnly)),
                    "get_selection" => BridgeResponse.Success(GetPage(
                        uiDocument.Selection.GetElementIds().Select(id => id.Value).Order().ToArray(), pending.Request)),
                    "get_element_info" => BridgeResponse.Success(GetElement(document, pending.Request.ElementId!.Value)),
                    "list_levels" => BridgeResponse.Success(GetLevels(document, pending.Request)),
                    "list_walls" => BridgeResponse.Success(GetWalls(document, pending.Request)),
                    "list_walls_with_comments" => BridgeResponse.Success(GetWalls(document, pending.Request, commentsOnly: true)),
                    "list_doors" => BridgeResponse.Success(GetDoors(document, pending.Request)),
                    "list_door_geometry" => BridgeResponse.Success(GetDoorGeometry(document, pending.Request)),
                    "list_elements" => BridgeResponse.Success(GetElements(document, pending.Request)),
                    "list_view_elements" => BridgeResponse.Success(GetViewElements(document, pending.Request)),
                    "classify_view_elements" => BridgeResponse.Success(ClassifyViewElements(document, pending.Request)),
                    "get_element_mesh" => BridgeResponse.Success(GetElementMesh(document, pending.Request)),
                    _ => throw new ArgumentException($"Unknown operation: {pending.Request.Operation}")
                };
                pending.Completion.TrySetResult(response);
            }
            catch (Exception exception)
            {
                log.TraceEvent(TraceEventType.Error, 0, $"Revit request {pending.Request.Operation} failed: {exception}");
                log.Flush();
                pending.Completion.TrySetResult(BridgeResponse.Failure(exception.Message));
            }
        }
    }

    public void CancelPending()
    {
        while (_requests.TryDequeue(out PendingRequest? pending))
            pending.Completion.TrySetCanceled();
    }

    private static ElementInfo GetElement(Document document, long id)
    {
        Element element = document.GetElement(new ElementId(id))
            ?? throw new ArgumentException($"Element {id} does not exist in the active document.");
        return new ElementInfo(element.Id.Value, element.UniqueId, element.Name,
            element.Category?.Name, element.GetTypeId().Value);
    }

    private static Page<LevelInfo> GetLevels(Document document, BridgeRequest request)
    {
        using var collector = new FilteredElementCollector(document);
        LevelInfo[] levels = collector.OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(level => level.Elevation).ThenBy(level => level.Id.Value)
            .Select(level => new LevelInfo(level.Id.Value, level.Name, level.Elevation)).ToArray();
        return GetPage(levels, request);
    }

    private static Page<WallInfo> GetWalls(Document document, BridgeRequest request, bool commentsOnly = false)
    {
        using Units units = document.GetUnits();
        using FormatOptions lengthFormat = units.GetFormatOptions(SpecTypeId.Length);
        ForgeTypeId projectLengthUnit = lengthFormat.GetUnitTypeId();
        bool metric = projectLengthUnit == UnitTypeId.Millimeters
            || projectLengthUnit == UnitTypeId.Centimeters
            || projectLengthUnit == UnitTypeId.Decimeters
            || projectLengthUnit == UnitTypeId.Meters
            || projectLengthUnit == UnitTypeId.MetersCentimeters;
        ForgeTypeId outputUnit = metric ? UnitTypeId.Millimeters : UnitTypeId.Feet;
        double ConvertLength(double value) => UnitUtils.ConvertFromInternalUnits(value, outputUnit);
        PointInfo SharedPoint(XYZ point)
        {
            using ProjectPosition position = document.ActiveProjectLocation.GetProjectPosition(point);
            return new PointInfo(ConvertLength(position.EastWest), ConvertLength(position.NorthSouth),
                ConvertLength(position.Elevation));
        }

        using var collector = new FilteredElementCollector(document);
        Wall[] walls = collector.OfClass(typeof(Wall)).WhereElementIsNotElementType()
            .Cast<Wall>()
            .Where(wall => !commentsOnly || !string.IsNullOrWhiteSpace(GetWallComments(wall)))
            .OrderBy(wall => wall.Id.Value).ToArray();
        Page<Wall> page = GetPage(walls, request);
        WallInfo[] items = page.Items.Select(wall =>
        {
            WallType type = wall.WallType;
            using BoundingBoxXYZ? bounds = wall.get_BoundingBox(null);
            Curve? curve = wall.Location is LocationCurve location ? location.Curve : null;
            double? length = curve?.Length;
            double? height = bounds is null ? null : bounds.Max.Z - bounds.Min.Z;
            WallPosition? position = curve is { IsBound: true }
                ? new WallPosition(SharedPoint(curve.GetEndPoint(0)), SharedPoint(curve.GetEndPoint(1)))
                : null;
            return new WallInfo(wall.Id.Value, type.Id.Value, type.Name, type.Kind.ToString(),
                length, wall.Width, height, metric ? "mm" : "ft",
                length.HasValue ? ConvertLength(length.Value) : null, ConvertLength(wall.Width),
                height.HasValue ? ConvertLength(height.Value) : null, position, GetWallComments(wall));
        }).ToArray();
        return new Page<WallInfo>(items, page.Total, page.Offset, page.NextOffset);
    }

    private static string? GetWallComments(Wall wall) =>
        wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();

    private static Page<ElementInfo> GetElements(Document document, BridgeRequest request)
    {
        using var collector = new FilteredElementCollector(document);
        using var types = new ElementIsElementTypeFilter();
        using var instances = new ElementIsElementTypeFilter(true);
        using var allElements = new LogicalOrFilter(types, instances);
        long[] ids = request.ModelObjectsOnly
            ? collector.WhereElementIsNotElementType()
                .Where(element => element.Category?.CategoryType == CategoryType.Model)
                .Select(element => element.Id.Value).Order().ToArray()
            : collector.WherePasses(allElements).ToElementIds()
                .Select(id => id.Value).Order().ToArray();
        Page<long> page = GetPage(ids, request);
        ElementInfo[] items = page.Items.Select(id => GetElement(document, id)).ToArray();
        return new Page<ElementInfo>(items, page.Total, page.Offset, page.NextOffset);
    }

    private static Page<ElementInfo> GetDoors(Document document, BridgeRequest request)
    {
        using var collector = new FilteredElementCollector(document);
        long[] ids = collector.OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
            .Select(element => element.Id.Value).Order().ToArray();
        Page<long> page = GetPage(ids, request);
        ElementInfo[] items = page.Items.Select(id => GetElement(document, id)).ToArray();
        return new Page<ElementInfo>(items, page.Total, page.Offset, page.NextOffset);
    }

    private static Page<ElementInfo> GetViewElements(Document document, BridgeRequest request)
    {
        long[] ids = GetViewElementIds(document, request.ViewName!);
        Page<long> page = GetPage(ids, request);
        ElementInfo[] items = page.Items.Select(id => GetElement(document, id)).ToArray();
        return new Page<ElementInfo>(items, page.Total, page.Offset, page.NextOffset);
    }

    private static long[] GetViewElementIds(Document document, string viewName)
    {
        using var views = new FilteredElementCollector(document);
        Autodesk.Revit.DB.View[] matches = views.OfClass(typeof(Autodesk.Revit.DB.View))
            .Cast<Autodesk.Revit.DB.View>()
            .Where(view => !view.IsTemplate && string.Equals(view.Name, viewName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
            throw new ArgumentException($"No non-template view named '{viewName}' exists in the active document.");
        if (matches.Length > 1)
            throw new ArgumentException($"Multiple views match '{viewName}' (case-insensitive). Use a name that uniquely identifies a view.");
        Autodesk.Revit.DB.View view = matches[0];

        using var collector = new FilteredElementCollector(document, view.Id);
        return collector.WhereElementIsNotElementType()
            .ToElementIds().Select(id => id.Value).Order().ToArray();
    }

    private static Page<DoorGeometryInfo> GetDoorGeometry(Document document, BridgeRequest request)
    {
        using Units units = document.GetUnits();
        using FormatOptions lengthFormat = units.GetFormatOptions(SpecTypeId.Length);
        ForgeTypeId projectLengthUnit = lengthFormat.GetUnitTypeId();
        bool metric = projectLengthUnit == UnitTypeId.Millimeters
            || projectLengthUnit == UnitTypeId.Centimeters
            || projectLengthUnit == UnitTypeId.Decimeters
            || projectLengthUnit == UnitTypeId.Meters
            || projectLengthUnit == UnitTypeId.MetersCentimeters;
        ForgeTypeId outputUnit = metric ? UnitTypeId.Millimeters : UnitTypeId.Feet;
        double ConvertLength(double value) => UnitUtils.ConvertFromInternalUnits(value, outputUnit);

        using var collector = new FilteredElementCollector(document);
        FamilyInstance[] doors = collector.OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
            .Cast<FamilyInstance>().OrderBy(door => door.Id.Value).ToArray();
        Page<FamilyInstance> page = GetPage(doors, request);
        using var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };

        DoorGeometryInfo[] items = page.Items.Select(door =>
        {
            ProjectLocation projectLocation = document.ActiveProjectLocation;
            Transform instanceTransform = door.GetTransform();
            XYZ? insertionPoint = (door.Location as LocationPoint)?.Point;
            PointInfo? insertionPointShared = insertionPoint is null
                ? null
                : ToSharedPoint(projectLocation, insertionPoint, ConvertLength);
            DoorTransformInfo transformShared = ToSharedTransform(projectLocation,
                instanceTransform.OfPoint, ConvertLength);
            VectorInfo facingDirectionShared = ToSharedVector(projectLocation, instanceTransform.Origin,
                door.FacingOrientation);
            VectorInfo handDirectionShared = ToSharedVector(projectLocation, instanceTransform.Origin,
                door.HandOrientation);
            DoorOpeningInfo opening = GetDoorOpening(document, door, projectLocation, ConvertLength);
            long? hostWallId = (door.Host as Wall)?.Id.Value;
            using GeometryElement? geometry = door.get_Geometry(options);
            if (geometry is null)
                return new DoorGeometryInfo(door.Id.Value, door.Name, false, metric ? "mm" : "ft",
                    Array.Empty<DoorSolidInfo>(), Array.Empty<string>(), transformShared, insertionPointShared,
                    facingDirectionShared, handDirectionShared, door.FacingFlipped, door.HandFlipped,
                    hostWallId, opening);

            var solids = new List<DoorSolidInfo>();
            var otherGeometryTypes = new List<string>();
            CollectGeometry(geometry, [], "root", solids, otherGeometryTypes, projectLocation, ConvertLength);
            return new DoorGeometryInfo(door.Id.Value, door.Name, true, metric ? "mm" : "ft",
                solids, otherGeometryTypes, transformShared, insertionPointShared, facingDirectionShared,
                handDirectionShared, door.FacingFlipped, door.HandFlipped, hostWallId, opening);
        }).ToArray();
        return new Page<DoorGeometryInfo>(items, page.Total, page.Offset, page.NextOffset);
    }

    private static DoorOpeningInfo GetDoorOpening(Document document, FamilyInstance door,
        ProjectLocation projectLocation, Func<double, double> convertLength)
    {
        double? roughWidth = GetDoubleParameter(door, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM)
            ?? GetDoubleParameter(door.Symbol, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM);
        double? roughHeight = GetDoubleParameter(door, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM)
            ?? GetDoubleParameter(door.Symbol, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM);
        double? sillHeight = GetDoubleParameter(door, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
        Level? baseLevel = door.LevelId.Value > 0 ? document.GetElement(door.LevelId) as Level : null;
        double? baseLevelElevation = baseLevel is null
            ? null
            : ToSharedPoint(projectLocation, new XYZ(0, 0, baseLevel.Elevation), convertLength).Z;
        double? bottomElevation = baseLevel is not null && sillHeight.HasValue
            ? ToSharedPoint(projectLocation, new XYZ(0, 0, baseLevel.Elevation + sillHeight.Value), convertLength).Z
            : null;
        double? topElevation = bottomElevation.HasValue && roughHeight.HasValue
            ? bottomElevation.Value + convertLength(roughHeight.Value)
            : null;
        return new DoorOpeningInfo(
            roughWidth.HasValue ? convertLength(roughWidth.Value) : null,
            roughHeight.HasValue ? convertLength(roughHeight.Value) : null,
            sillHeight.HasValue ? convertLength(sillHeight.Value) : null,
            baseLevel?.Id.Value,
            baseLevelElevation,
            bottomElevation,
            topElevation);
    }

    private static double? GetDoubleParameter(Element element, BuiltInParameter parameterId)
    {
        Parameter? parameter = element.get_Parameter(parameterId);
        return parameter?.StorageType == StorageType.Double && parameter.HasValue
            ? parameter.AsDouble()
            : null;
    }

    private static PointInfo ToSharedPoint(ProjectLocation projectLocation, XYZ point,
        Func<double, double> convertLength)
    {
        using ProjectPosition position = projectLocation.GetProjectPosition(point);
        return new PointInfo(convertLength(position.EastWest), convertLength(position.NorthSouth),
            convertLength(position.Elevation));
    }

    private static VectorInfo ToSharedVector(ProjectLocation projectLocation, XYZ modelOrigin, XYZ modelVector)
    {
        using ProjectPosition origin = projectLocation.GetProjectPosition(modelOrigin);
        using ProjectPosition endpoint = projectLocation.GetProjectPosition(modelOrigin + modelVector);
        return new VectorInfo(endpoint.EastWest - origin.EastWest,
            endpoint.NorthSouth - origin.NorthSouth, endpoint.Elevation - origin.Elevation);
    }

    private static DoorTransformInfo ToSharedTransform(ProjectLocation projectLocation,
        Func<XYZ, XYZ> toModel, Func<double, double> convertLength)
    {
        XYZ origin = toModel(XYZ.Zero);
        return new DoorTransformInfo(
            ToSharedPoint(projectLocation, origin, convertLength),
            ToSharedVector(projectLocation, origin, toModel(XYZ.BasisX) - origin),
            ToSharedVector(projectLocation, origin, toModel(XYZ.BasisY) - origin),
            ToSharedVector(projectLocation, origin, toModel(XYZ.BasisZ) - origin));
    }

    private static XYZ ApplyTransforms(XYZ point, IReadOnlyList<Transform> transforms)
    {
        for (int index = transforms.Count - 1; index >= 0; index--)
            point = transforms[index].OfPoint(point);
        return point;
    }

    private static void CollectGeometry(GeometryElement geometry, IReadOnlyList<Transform> transforms,
        string path, List<DoorSolidInfo> solids, List<string> otherGeometryTypes,
        ProjectLocation projectLocation, Func<double, double> convertLength)
    {
        int objectIndex = 0;
        foreach (GeometryObject geometryObject in geometry)
        {
            if (geometryObject is Solid solid)
            {
                if (solid.Faces.Size == 0 && solid.Edges.Size == 0)
                {
                    objectIndex++;
                    continue;
                }
                solids.Add(GetSolidGeometry(solid, solids.Count, $"{path}/solid[{objectIndex}]",
                    transforms, projectLocation, convertLength));
            }
            else if (geometryObject is GeometryInstance instance)
            {
                using GeometryElement symbolGeometry = instance.GetSymbolGeometry();
                Transform[] childTransforms = [.. transforms, instance.Transform];
                CollectGeometry(symbolGeometry, childTransforms, $"{path}/instance[{objectIndex}]",
                    solids, otherGeometryTypes, projectLocation, convertLength);
            }
            else
            {
                otherGeometryTypes.Add(geometryObject.GetType().Name);
            }
            objectIndex++;
        }
    }

    private static DoorSolidInfo GetSolidGeometry(Solid solid, int index, string path,
        IReadOnlyList<Transform> transforms, ProjectLocation projectLocation,
        Func<double, double> convertLength)
    {
        using BoundingBoxXYZ bounds = solid.GetBoundingBox();
        XYZ minimum = bounds.Min;
        XYZ maximum = bounds.Max;
        Transform[] componentTransforms = [.. transforms, bounds.Transform];
        XYZ firstCorner = ApplyTransforms(new XYZ(minimum.X, minimum.Y, minimum.Z), componentTransforms);
        double minimumX = firstCorner.X;
        double minimumY = firstCorner.Y;
        double minimumZ = firstCorner.Z;
        double maximumX = firstCorner.X;
        double maximumY = firstCorner.Y;
        double maximumZ = firstCorner.Z;
        for (int x = 0; x < 2; x++)
        for (int y = 0; y < 2; y++)
        for (int z = 0; z < 2; z++)
        {
            XYZ corner = ApplyTransforms(new XYZ(
                x == 0 ? minimum.X : maximum.X,
                y == 0 ? minimum.Y : maximum.Y,
                z == 0 ? minimum.Z : maximum.Z), componentTransforms);
            minimumX = Math.Min(minimumX, corner.X);
            minimumY = Math.Min(minimumY, corner.Y);
            minimumZ = Math.Min(minimumZ, corner.Z);
            maximumX = Math.Max(maximumX, corner.X);
            maximumY = Math.Max(maximumY, corner.Y);
            maximumZ = Math.Max(maximumZ, corner.Z);
        }
        var extents = new DoorBounds(
            convertLength(maximumX - minimumX),
            convertLength(maximumY - minimumY),
            convertLength(maximumZ - minimumZ));
        DoorEdgeInfo[] edges = solid.Edges.Cast<Edge>().Select((edge, edgeIndex) =>
        {
            using Curve curve = edge.AsCurve();
            double? radius = curve is Arc arc ? convertLength(arc.Radius) : null;
            double? radiusX = curve is Ellipse ellipse ? convertLength(ellipse.RadiusX) : null;
            double? radiusY = curve is Ellipse ellipseY ? convertLength(ellipseY.RadiusY) : null;
            return new DoorEdgeInfo(edgeIndex, curve.GetType().Name, convertLength(curve.Length),
                radius, radiusX, radiusY);
        }).ToArray();
        var localBounds = new DoorLocalBounds(
            new PointInfo(convertLength(minimum.X), convertLength(minimum.Y), convertLength(minimum.Z)),
            new PointInfo(convertLength(maximum.X), convertLength(maximum.Y), convertLength(maximum.Z)),
            convertLength(maximum.X - minimum.X),
            convertLength(maximum.Y - minimum.Y),
            convertLength(maximum.Z - minimum.Z));
        var component = new DoorComponentInfo(path, localBounds,
            ToSharedTransform(projectLocation, point => ApplyTransforms(point, componentTransforms), convertLength));
        return new DoorSolidInfo(index, solid.Faces.Size, solid.Edges.Size, solid.Volume,
            solid.SurfaceArea, extents, edges, component);
    }

    private static Page<T> GetPage<T>(IReadOnlyList<T> items, BridgeRequest request)
    {
        T[] page = items.Skip(request.Offset).Take(request.Limit).ToArray();
        int next = request.Offset + page.Length;
        return new Page<T>(page, items.Count, request.Offset, next < items.Count ? next : null);
    }
}
