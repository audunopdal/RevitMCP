using System.Collections.Concurrent;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Contracts;

namespace RevitMCP.Addin;

internal sealed record PendingRequest(BridgeRequest Request, TaskCompletionSource<BridgeResponse> Completion);

internal sealed class RevitRequestHandler(TraceSource log) : IExternalEventHandler
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
                    "list_elements" => BridgeResponse.Success(GetElements(document, pending.Request)),
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

    private static Page<WallInfo> GetWalls(Document document, BridgeRequest request)
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
            .Cast<Wall>().OrderBy(wall => wall.Id.Value).ToArray();
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
                height.HasValue ? ConvertLength(height.Value) : null, position);
        }).ToArray();
        return new Page<WallInfo>(items, page.Total, page.Offset, page.NextOffset);
    }

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

    private static Page<T> GetPage<T>(IReadOnlyList<T> items, BridgeRequest request)
    {
        T[] page = items.Skip(request.Offset).Take(request.Limit).ToArray();
        int next = request.Offset + page.Length;
        return new Page<T>(page, items.Count, request.Offset, next < items.Count ? next : null);
    }
}
