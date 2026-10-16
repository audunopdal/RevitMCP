using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCP.Server;

[McpServerToolType]
public sealed class RevitTools(RevitBridgeClient bridge)
{
    [McpServerTool(Name = "get_active_document", ReadOnly = true, OpenWorld = false)]
    [Description("Get the title, path, family-document flag and read-only flag of the active Revit document.")]
    public Task<JsonElement> GetActiveDocument(CancellationToken cancellationToken) =>
        bridge.SendAsync(new BridgeRequest("get_active_document"), cancellationToken);

    [McpServerTool(Name = "get_selection", ReadOnly = true, OpenWorld = false)]
    [Description("Get a page of selected Revit element IDs. Limit must be 1 to 100; offset must be non-negative.")]
    public Task<JsonElement> GetSelection(CancellationToken cancellationToken, int offset = 0, int limit = 100) =>
        bridge.SendAsync(new BridgeRequest("get_selection", Offset: offset, Limit: limit), cancellationToken);

    [McpServerTool(Name = "get_element_info", ReadOnly = true, OpenWorld = false)]
    [Description("Get basic element metadata using a positive 64-bit Revit element ID. No model changes are made.")]
    public Task<JsonElement> GetElementInfo(long elementId, CancellationToken cancellationToken) =>
        bridge.SendAsync(new BridgeRequest("get_element_info", ElementId: elementId), cancellationToken);

    [McpServerTool(Name = "list_levels", ReadOnly = true, OpenWorld = false)]
    [Description("Get a page of levels ordered by elevation then ID. Elevations are Revit internal units (feet). Limit must be 1 to 100.")]
    public Task<JsonElement> ListLevels(CancellationToken cancellationToken, int offset = 0, int limit = 100) =>
        bridge.SendAsync(new BridgeRequest("list_levels", Offset: offset, Limit: limit), cancellationToken);

    [McpServerTool(Name = "list_walls", ReadOnly = true, OpenWorld = false)]
    [Description("Get all Wall instances in the active document, paged and ordered by element ID, including hidden walls but excluding linked document contents. Returns element ID, type ID, type name, wall kind, length, width, height and position. Unit is mm for metric project length units, ft otherwise; applies to dimensions and position.start/end (x=easting, y=northing, z=elevation in the active project location's shared coordinates). Position uses location-curve endpoints, not physical wall corners; null when no bounded curve is available. Length is location-curve length, width is nominal width, height is model bounding-box vertical extent (not unconnected height). Length or height is null when unavailable. Legacy lengthFeet/widthFeet/heightFeet remain in feet. Follow nextOffset until null. Limit must be 1 to 100; offset must be non-negative.")]
    public Task<JsonElement> ListWalls(CancellationToken cancellationToken, int offset = 0, int limit = 100) =>
        bridge.SendAsync(new BridgeRequest("list_walls", Offset: offset, Limit: limit), cancellationToken);

    [McpServerTool(Name = "list_walls_with_comments", ReadOnly = true, OpenWorld = false)]
    [Description("List only Wall instances whose built-in instance Comments parameter contains non-whitespace text (not Type Comments). Returns the same metadata, dimensions and shared-coordinate endpoints as list_walls, plus comments. Dimensions and positions use unit mm for metric project length units, ft otherwise; legacy fields ending Feet remain feet. Includes hidden walls, excludes linked documents. Filtering occurs before pagination; total and nextOffset refer only to matching walls, ordered by ID. Follow nextOffset until null. Limit must be 1 to 100; offset must be non-negative.")]
    public Task<JsonElement> ListWallsWithComments(CancellationToken cancellationToken, int offset = 0, int limit = 100) =>
        bridge.SendAsync(new BridgeRequest("list_walls_with_comments", Offset: offset, Limit: limit), cancellationToken);

    [McpServerTool(Name = "list_doors", ReadOnly = true, OpenWorld = false)]
    [Description("Get a page of door instances in the active document ordered by element ID, including hidden doors but excluding linked document contents. Returns element ID, unique ID, name, category and type ID. Follow nextOffset until null to collect all doors. Limit must be 1 to 100; offset must be non-negative.")]
    public Task<JsonElement> ListDoors(CancellationToken cancellationToken, int offset = 0, int limit = 100) =>
        bridge.SendAsync(new BridgeRequest("list_doors", Offset: offset, Limit: limit), cancellationToken);

    [McpServerTool(Name = "list_door_geometry", ReadOnly = true, OpenWorld = false)]
    [Description("Get door placement, host, rough opening dimensions and geometry for a page of door instances in the active document. Placement origins and basis/facing/hand directions are in shared coordinates. Reports host wall ID, rough width/height, sill and shared elevations when Revit exposes them, plus each solid's component-local bounds and transform, face/edge counts, volume, surface area and edge lengths/radii. Lengths use mm for metric projects and ft otherwise; volumes and areas are always cubic feet and square feet. Component paths identify geometry nesting, not semantic frame/leaf roles. Limit must be 1 to 5; follow nextOffset until null.")]
    public Task<JsonElement> ListDoorGeometry(CancellationToken cancellationToken, int offset = 0, int limit = 1) =>
        bridge.SendAsync(new BridgeRequest("list_door_geometry", Offset: offset, Limit: limit), cancellationToken);

    [McpServerTool(Name = "list_elements", ReadOnly = true, OpenWorld = false)]
    [Description("Get a page of element metadata ordered by ID. By default includes all document elements. Set modelObjectsOnly=true for only non-type elements with a Model category across the entire document, regardless of visibility in the active view. Linked document contents are excluded. Follow nextOffset until null. Limit must be 1 to 100; offset must be non-negative.")]
    public Task<JsonElement> ListElements(CancellationToken cancellationToken, int offset = 0, int limit = 100,
        [Description("Only model-category instances across the whole document; not an active-view visibility filter.")] bool modelObjectsOnly = false) =>
        bridge.SendAsync(new BridgeRequest("list_elements", Offset: offset, Limit: limit, ModelObjectsOnly: modelObjectsOnly), cancellationToken);

    [McpServerTool(Name = "list_view_elements", ReadOnly = true, OpenWorld = false)]
    [Description("Get a page of non-type element metadata visible in a specified Revit view. Supply the exact view name as viewName (matched case-insensitively). If multiple views match, the tool returns an ambiguity error. Includes model and view-specific instances visible under that view's settings, excludes linked-document contents and element types. Results are ordered by element ID. Follow nextOffset until null. Limit must be 1 to 100; offset must be non-negative.")]
    public Task<JsonElement> ListViewElements(string viewName, CancellationToken cancellationToken, int offset = 0, int limit = 100) =>
        bridge.SendAsync(new BridgeRequest("list_view_elements", Offset: offset, Limit: limit, ViewName: viewName), cancellationToken);
}
