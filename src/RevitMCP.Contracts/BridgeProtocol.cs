using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace RevitMCP.Contracts;

public sealed record BridgeRequest(string Operation, long? ElementId = null, int Offset = 0, int Limit = 100,
    bool ModelObjectsOnly = false, string? ViewName = null)
{
    public void Validate()
    {
        if (Operation is not ("get_active_document" or "get_selection" or "get_element_info" or "list_levels" or "list_elements" or "list_walls" or "list_walls_with_comments" or "list_doors" or "list_door_geometry" or "list_view_elements"))
            throw new ArgumentException($"Unknown operation: {Operation}");
        if (Offset < 0 || Limit is < 1 or > 100)
            throw new ArgumentException("Offset must be non-negative and limit must be between 1 and 100.");
        if (Operation == "list_door_geometry" && Limit > 5)
            throw new ArgumentException("Door geometry limit must be between 1 and 5.");
        if (Operation == "get_element_info" && ElementId is not > 0)
            throw new ArgumentException("Element ID must be a positive 64-bit integer.");
        if (Operation == "list_view_elements" && string.IsNullOrWhiteSpace(ViewName))
            throw new ArgumentException("View name must not be empty.");
    }
}

public sealed record BridgeResponse(JsonElement? Data, string? Error)
{
    public static BridgeResponse Success<T>(T data) =>
        new(JsonSerializer.SerializeToElement(data, BridgeProtocol.JsonOptions), null);

    public static BridgeResponse Failure(string error) => new(null, error);
}

public sealed record DocumentInfo(string Title, string Path, bool IsFamilyDocument, bool IsReadOnly);
public sealed record ElementInfo(long Id, string UniqueId, string Name, string? Category, long TypeId);
public sealed record LevelInfo(long Id, string Name, double ElevationFeet);
public sealed record WallInfo(long Id, long TypeId, string TypeName, string Kind,
    double? LengthFeet, double WidthFeet, double? HeightFeet,
    string Unit, double? Length, double Width, double? Height, WallPosition? Position, string? Comments = null)
{
    public string? LengthFormatted => Length?.ToString("0.##", CultureInfo.InvariantCulture);
    public string WidthFormatted => Width.ToString("0.##", CultureInfo.InvariantCulture);
    public string? HeightFormatted => Height?.ToString("0.##", CultureInfo.InvariantCulture);
}
public sealed record PointInfo(double X, double Y, double Z);
public sealed record WallPosition(PointInfo Start, PointInfo End)
{
    public string CoordinateSystem => "shared";
}
public sealed record DoorGeometryInfo(long Id, string Name, bool GeometryAvailable, string Unit,
    IReadOnlyList<DoorSolidInfo> Solids, IReadOnlyList<string> OtherGeometryTypes,
    DoorTransformInfo? TransformShared = null, PointInfo? InsertionPointShared = null,
    VectorInfo? FacingDirectionShared = null, VectorInfo? HandDirectionShared = null,
    bool? FacingFlipped = null, bool? HandFlipped = null, long? HostWallId = null,
    DoorOpeningInfo? Opening = null);
public sealed record DoorSolidInfo(int Index, int FaceCount, int EdgeCount, double VolumeCubicFeet,
    double SurfaceAreaSquareFeet, DoorBounds Bounds, IReadOnlyList<DoorEdgeInfo> Edges,
    DoorComponentInfo? Component = null);
public sealed record DoorBounds(double XExtent, double YExtent, double ZExtent);
public sealed record DoorEdgeInfo(int Index, string CurveType, double Length,
    double? Radius, double? RadiusX, double? RadiusY);
public sealed record DoorComponentInfo(string Path, DoorLocalBounds Bounds, DoorTransformInfo TransformShared);
public sealed record DoorLocalBounds(PointInfo Minimum, PointInfo Maximum, double XExtent, double YExtent, double ZExtent);
public sealed record DoorTransformInfo(PointInfo Origin, VectorInfo BasisX, VectorInfo BasisY, VectorInfo BasisZ)
{
    public string CoordinateSystem => "shared";
}
public sealed record VectorInfo(double X, double Y, double Z);
public sealed record DoorOpeningInfo(double? RoughWidth, double? RoughHeight, double? SillHeight,
    long? BaseLevelId, double? BaseLevelElevationShared, double? BottomElevationShared, double? TopElevationShared);
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Offset, int? NextOffset);

public static class BridgeProtocol
{
    public const int MaxMessageBytes = 1024 * 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static string PipeName(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        return $"RevitMCP-2026-{processId}";
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length > MaxMessageBytes)
            throw new InvalidDataException("Bridge message exceeds the 1 MiB limit.");
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
            throw new InvalidDataException("Invalid bridge message length.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
            ?? throw new InvalidDataException("Bridge message cannot be null.");
    }
}
