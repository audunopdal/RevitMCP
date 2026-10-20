# RevitMCP

A local, read-only MCP server for Autodesk Revit 2026, implemented in C#.

## Architecture

```text
VS Code -> MCP over stdio -> RevitMCP.Server
        -> current-user-only named pipe -> RevitMCP.Addin
        -> ExternalEvent -> Revit API
```

- [Server](src/RevitMCP.Server) exposes MCP tools using the official C# SDK.
- [Add-in](src/RevitMCP.Addin) runs inside Revit 2026. All document reads happen
  in `IExternalEventHandler.Execute`, never on the pipe listener thread.
- [Contracts](src/RevitMCP.Contracts) contains API-independent DTOs and bounded
  length-prefixed JSON framing.
- [Tests](tests/RevitMCP.Tests) exercise framing, validation, named pipes and MCP.

There are no model-editing tools or arbitrary code execution endpoints.

## Prerequisites

- Windows, Revit 2026, and a .NET SDK capable of targeting .NET 8.
- .NET 8 runtime for the console server.
- VS Code with an MCP-capable chat client.

The add-in references the installed Revit 2026 API assemblies without copying
them into its output. The default installation path is
`C:\Program Files\Autodesk\Revit 2026`. Override it with
`-p:RevitInstallDir="your installation directory"` when building.

## Build and test

```powershell
dotnet build .\RevitMCP.sln -c Release
dotnet test .\tests\RevitMCP.Tests\RevitMCP.Tests.csproj -c Release
```

Tests do not require Revit to be running. Loading the add-in and reading a real
document must also be verified manually in Revit.

## Install and connect

Close Revit and stop the `revit` MCP server, then run from the repository root:

```powershell
.\scripts\Install-Addin.ps1
```

This publishes both components under `artifacts` and creates
`%APPDATA%\Autodesk\Revit\Addins\2026\RevitMCP.addin` pointing to the local add-in.
It does not commit or push anything. Moving the repository requires rerunning
the script. Do not commit generated manifests containing local paths.

### Install a Debug build

The installer defaults to Release. For unoptimized Debug builds of both the
add-in and MCP server, with matching PDB symbols, run:

```powershell
.\scripts\Install-Addin.ps1 -Configuration Debug
```

From Command Prompt instead of PowerShell:

```cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\Repos\RevitMCP\scripts\Install-Addin.ps1" -Configuration Debug
```

Debug and Release installations use the same output folders and manifest.
No MCP configuration changes are needed. Restart Revit and the MCP server after
installation. Attach VS Code's C# debugger to `Revit.exe` to debug the add-in,
or to the `dotnet.exe` process running `RevitMCP.Server.dll` to debug the server.
Requests can time out when paused at a breakpoint for more than 30 seconds.
To switch back, close both processes and run the installer without
`-Configuration Debug` (or explicitly use `-Configuration Release`).

### Start and verify

1. Start Revit 2026 and accept the add-in loading prompt if shown.
2. Open a model.
3. Open this repository in VS Code.
4. Use **MCP: List Servers**, select `revit`, and start it. The workspace
   [MCP configuration](.vscode/mcp.json) uses the published console server.
5. Enable its tools in chat and invoke `get_active_document`.
6. Verify that the title matches the active Revit document.

The server resolves the single running Revit process when a tool is called.
For multiple Revit instances, append `"--revit-process-id", "12345"` to `args`
in the MCP configuration, using the desired Revit 2026 PID. Get PIDs with:

```powershell
Get-Process Revit | Select-Object Id, MainWindowTitle
```

Other Revit versions do not host this add-in.

## Tools

| Tool | Inputs | Output |
| --- | --- | --- |
| `get_active_document` | None | Title, path, family-document flag, read-only flag |
| `get_selection` | `offset=0`, `limit=100` | Page of selected 64-bit element IDs |
| `get_element_info` | Positive 64-bit `elementId` | ID, unique ID, name, category, type ID |
| `list_levels` | `offset=0`, `limit=100` | Page of levels ordered by elevation and ID |
| `list_walls` | `offset=0`, `limit=100` | Page of walls ordered by element ID, with type, dimensions and shared-coordinate endpoints; mm for metric models, ft otherwise |
| `list_walls_with_comments` | `offset=0`, `limit=100` | Same wall data, restricted to walls with non-blank instance Comments |
| `list_doors` | `offset=0`, `limit=100` | Page of door instances ordered by element ID, with basic element metadata |
| `list_door_geometry` | `offset=0`, `limit=1` (max 5) | Door placement, host wall, rough opening data, component-local solid bounds/transforms and edge measurements |
| `list_elements` | `offset=0`, `limit=100`, `modelObjectsOnly=false` | Page of document elements ordered by ID, with the same metadata as `get_element_info` |
| `list_view_elements` | `viewName`, `offset=0`, `limit=100` | Page of non-type elements visible in the named view |
| `classify_view_elements` | `viewName`, `offset=0`, `limit=10` | Geometry-based mesh export status and reason for each view element |
| `get_element_mesh` | `elementId`, `offset=0`, `limit=100` | Page of triangles, material metadata and shared-coordinate placement |

Pages contain `items`, `total`, `offset`, and `nextOffset` (null at the end).
Limits must be 1 through 100; offsets must be non-negative.
Level elevations are in Revit internal units (feet). Element information is
basic metadata; parameter inspection is not implemented.

`list_doors` returns every door instance in the active document, including
hidden doors and excluding linked-document contents and door types. Results are
ordered by element ID and contain the same basic metadata as `list_elements`.
Follow `nextOffset` until it is null to collect all doors.

`list_door_geometry` returns geometry for all door instances in pages of up to
five doors (default one) to keep responses bounded. Each solid reports its
axis-aligned model-coordinate X, Y and Z extents, face and edge counts,
volume in cubic feet, surface area in square feet, and every boundary edge's
curve type and length. Circular edges include `radius`; elliptical edges
include `radiusX` and `radiusY`. Extents, edge lengths and radii use millimetres
for metric project length units and feet otherwise. Non-solid geometry types
are listed but are not dimensioned. Geometry unavailable from Revit is marked
with `geometryAvailable=false`. This reports generated family geometry, not
door instance parameters or manufacturer dimensions.

Each door also reports its insertion point and family transform origin/basis,
facing and hand direction vectors in active-project shared coordinates, flip
flags, and `hostWallId` when hosted by a wall. `opening` reports the Revit
rough-width/rough-height and sill parameters when available, plus the base level
and derived shared-coordinate bottom/top elevations when enough data is
available. These are Revit family opening parameters, not a geometric
intersection measurement of the cut in the host wall.

Each solid's `component` includes a nested geometry path, a local bounding box
(minimum, maximum and X/Y/Z extents), and a transform whose origin and basis
map those local bounds into shared coordinates. Thus local component dimensions
remain meaningful for rotated doors. Geometry paths distinguish nested
components but do not assign semantic roles such as frame, leaf or hardware;
solid boundaries and extents are the geometry Revit provides, not a recovered
manufacturer profile.

`list_elements` includes element instances and types, views, and non-physical
elements in the active document. Linked-model contents are excluded (the link
instance itself is included). Only the current page's element metadata is read.
Use `nextOffset` as the next request's `offset` until it is null. Avoid modifying
the model or switching documents while paging; each request reads the current
document and is not a frozen snapshot.

Set `modelObjectsOnly=true` to include only non-type elements whose category is
`CategoryType.Model`, across the entire document. This excludes element types,
annotations, views, and uncategorized/internal elements. This is a category-based
filter, not a geometry or visibility test: hidden model-category instances are
included, and some model categories can contain non-physical objects.
The filter is applied before paging, so `total` and `nextOffset` refer to the
filtered results. Keep the flag unchanged while retrieving subsequent pages.
Omitting it preserves the original all-elements behavior.

`list_view_elements` requires the exact view name in `viewName` (matching is
case-insensitive). If no non-template view matches, or multiple views match,
the request returns an error. It returns non-type element instances collected
by Revit for that view, including model and view-specific elements that Revit
considers visible under the view's current settings. It excludes linked-document
contents and view templates. Results are ordered by element ID and paged with
the same `items`, `total`, `offset`, and `nextOffset` shape as other listing
tools.

Example chat request: "List all model-category instances using list_elements
with modelObjectsOnly=true."

### Unknown physical elements: mesh fallback

Call `classify_view_elements(viewName="E3D_ARK_L201")` and follow `nextOffset`
until null. Every item includes basic element metadata, Revit class, stable
category ID, category type, group and parent family-instance IDs, plus a status:

- `mesh_fallback`: actual surface triangles exist; use `get_element_mesh`.
- `skipped`: a link/container/annotation or no usable triangle geometry, with a reason.
- `failed`: a geometry extraction error, with its message and an add-in log entry.

Unfamiliar or missing categories are not discarded. This is a generic mesh route
for any physical geometry, including walls/doors; it does not claim semantic
conversion support. Links require a separate export. Groups and MEP systems
are skipped as containers; their individual physical members can still export.

Call `get_element_mesh(elementId=1182660)` for each desired mesh. Offset/limit
refer to **triangles**, not elements; follow `triangles.nextOffset` until null.
Pages contain at most 100 triangles, each with A/B/C vertices and a material ID.
The material list describes only IDs used on that page (name, RGB, transparency).
The command triangulates solid faces and reads native meshes, recursively applying
nested instance transforms and reversing winding for reflected transforms.

Vertices are in shared-axis millimetres **relative to `originShared`**; adding the
origin yields shared coordinates. No additional door/family transform should be
applied. Origins are based on the first surface triangle and stay consistent
while geometry is unchanged. No UVs, texture assets or semantic frame/leaf roles
are inferred. This exports surface geometry, not collision or watertight solids.

Classification uses the view to select element IDs, but both commands extract
view-independent Fine-detail geometry with non-visible geometry excluded. Meshes
are not cropped to section boxes or view crop regions. Each request re-reads
geometry; do not edit/switch documents while paging. Large elements may be slow
because each triangle page re-traverses geometry to compute the total.

Do not export both a semantic representation and its fallback mesh. Parent and
nested-family geometry, stacked walls, curtain components, or overlapping
elements can also duplicate surfaces; use the hierarchy IDs and choose an export
level rather than automatically merging all results. No UE objects are created
and no host-wall cuts are inferred.

`list_walls` returns every `Autodesk.Revit.DB.Wall` instance in the active
document, regardless of active-view visibility. Linked document contents and
wall types are excluded. Basic, curtain and stacked walls are included; stacked
wall containers and their member walls are separate elements, so do not sum
their dimensions as if they were independent walls.

Each item contains `id` (64-bit element ID), `typeId`, `typeName`, `kind`
(`Basic`, `Curtain` or `Stacked`), `unit`, `length`, `width`, `height` and `position`.
The document's Length format determines the output unit: millimetres (`mm`) for
metric formats (mm, cm, dm, m or m/cm), feet (`ft`) for imperial formats.
All three dimensions and all position coordinates use that item's `unit`.
Length is the location-curve length (arc length for curved walls), width is
Revit's nominal wall width, and height is the model bounding box's vertical
extent, not the unconnected-height parameter. For slanted, attached or
profile-edited walls, this height is an overall extent, not a uniform face
height; width is not a measurement of every panel or layer. Length and height
are null when the location curve or bounding box is unavailable, not zero.
`position` contains `start` and `end` points (`x`, `y`, `z`) from the wall's
location curve, in the active project location's **shared coordinates**:
X is easting, Y is northing and Z is elevation. `position.coordinateSystem`
is `"shared"`. These agree with Revit's Report Shared Coordinates, including
the active project's shared-coordinate rotation and offset. Endpoints are not
physical wall corners; for curved walls they do not describe the complete arc.
Position is null when a bounded location curve is unavailable.

For backwards compatibility, `lengthFeet`, `widthFeet` and `heightFeet` remain
available and always contain internal-unit feet, independent of `unit`. New
clients should use `length`, `width`, `height` and `position` with `unit`.
For display, `lengthFormatted`, `widthFormatted` and `heightFormatted` contain
up to two decimal places, a decimal point and no thousands separators
(for example, `"7800"`). Unavailable length/height values remain null.
The numeric dimension fields retain their full precision.
This unit selection applies only to `list_walls`; `list_levels` is unchanged.

To retrieve all walls, call `list_walls` repeatedly with `offset` set to the
previous page's `nextOffset` until it is null. As with `list_elements`, avoid
model edits, unit/shared-coordinate changes or document switches while paging.
Example chat request: "List all walls with element ID, type and dimensions;
follow every page of list_walls."

`list_walls_with_comments` uses the built-in **instance Comments** parameter,
not Type Comments or a custom parameter with the same name. Missing, empty
and whitespace-only comments are excluded. Filtering happens before paging,
so `total` and `nextOffset` describe only matching walls. It otherwise uses
the same scope, units, positions and formatted dimensions as `list_walls`.
Both wall tools include `comments`, preserving the original text (null if
the parameter has no value). `list_walls` still includes walls without comments.

After adding or updating tools, stop the `revit` MCP server in VS Code, close
Revit, rerun `scripts\Install-Addin.ps1`, restart Revit and reopen the model,
then restart the MCP server and enable the new tools.

## Export walls to Excel CSV

With Revit running and a model open, run from Command Prompt:

```cmd
powershell.exe -NoProfile -File "D:\Repos\RevitMCP\scripts\Export-Walls.ps1" -OutputPath "D:\Repos\RevitMCP\walls.csv"
```

Live export requires Node.js/npm and uses MCP Inspector through `npx` (downloaded
automatically if needed). It retrieves all pages. Add `-CommentsOnly` for walls
with non-blank instance Comments. Avoid changing the document during export.

To convert an existing complete MCP Inspector response without Node.js:

```cmd
powershell.exe -NoProfile -File "D:\Repos\RevitMCP\scripts\Export-Walls.ps1" -InputPath "D:\Repos\RevitMCP\walls.json" -OutputPath "D:\Repos\RevitMCP\walls.csv"
```

Partial-page input files are rejected. The CSV includes IDs, type, dimensions,
units, shared-coordinate endpoints and Comments. Numbers use up to two decimal
places without thousands separators. Decimal symbols and column separators
follow the Windows user's culture for Excel compatibility; UTF-8 with BOM
preserves non-ASCII comments. Unavailable dimensions/positions are blank.
Formula-like text is prefixed with an apostrophe to prevent Excel execution.
An existing output file is overwritten only after retrieval and conversion succeed.

## Errors and troubleshooting

- No active document, missing elements, invalid inputs, missing add-in, and
  disconnected Revit produce tool errors, not success-shaped empty results.
- Pipe connection timeout is 5 seconds; the total request deadline is 30 seconds.
  Modal dialogs and active commands may prevent Revit from handling external
  events. Finish the command or close the dialog, then retry.
- Pipe access is restricted to the current Windows user. Revit and the server
  must run as the same user. No TCP port is opened.
- Server logs go to both stderr and
  `%LOCALAPPDATA%\RevitMCP\logs\server-<PID>.log`; stdout is reserved for MCP messages.
- Add-in logs are under `%LOCALAPPDATA%\RevitMCP\logs\revit-<PID>.log`.
  Both files include timestamps, request operation names, successful completions,
  elapsed milliseconds and errors. Response payloads and document contents are
  not logged by the request lifecycle messages. Files are appended and flushed
  as entries are written, so they can be read while debugging. There is no
  automatic log rotation; remove old logs when their processes have stopped.
  Restart Revit if a listener failure is reported.
- To uninstall, close Revit and remove only
  `%APPDATA%\Autodesk\Revit\Addins\2026\RevitMCP.addin`.

## References

- [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [Revit API Developer Guide](https://help.autodesk.com/view/RVT/2026/ENU/)
- [VS Code MCP configuration](https://code.visualstudio.com/docs/agent-customization/mcp-servers)
