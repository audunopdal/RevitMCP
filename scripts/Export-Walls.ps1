[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Get-Location) 'walls.csv'),
    [string]$InputPath,
    [switch]$CommentsOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$serverPath = Join-Path $root 'artifacts\server\RevitMCP.Server.dll'
$operation = if ($CommentsOnly) { 'list_walls_with_comments' } else { 'list_walls' }

function Read-WallPage([string]$Json) {
    $response = $Json | ConvertFrom-Json
    if ($response.isError) {
        throw "MCP tool failed: $($response.content | ConvertTo-Json -Depth 10 -Compress)"
    }
    $blocks = @($response.content | Where-Object { $_.type -eq 'text' })
    if ($blocks.Count -ne 1) {
        throw 'Expected exactly one MCP text response containing a wall page.'
    }
    $page = $blocks[0].text | ConvertFrom-Json
    foreach ($property in @('items', 'total', 'offset', 'nextOffset')) {
        if ($null -eq $page.PSObject.Properties[$property]) {
            throw "Wall response is missing '$property'."
        }
    }
    return $page
}

function Format-Number($Value) {
    if ($null -ne $Value) {
        ([double]$Value).ToString('0.##', [Globalization.CultureInfo]::CurrentCulture)
    }
}

function Protect-ExcelText([string]$Value) {
    if ($Value -match '^\s*[=+@\-\t\r\n]') {
        return "'" + $Value
    }
    return $Value
}

$walls = [Collections.Generic.List[object]]::new()
$offset = 0
do {
    if ($InputPath) {
        $page = Read-WallPage (Get-Content -LiteralPath $InputPath -Raw)
        if ($page.offset -ne 0 -or $null -ne $page.nextOffset) {
            throw 'The input file contains only a partial page. Omit -InputPath to fetch all pages.'
        }
    }
    else {
        if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
            throw "Published server not found: $serverPath. Run Install-Addin.ps1 first."
        }
        $output = & npx.cmd --yes '@modelcontextprotocol/inspector' --cli dotnet $serverPath `
            --method tools/call --tool-name $operation --tool-arg "offset=$offset" --tool-arg 'limit=100'
        if ($LASTEXITCODE -ne 0) {
            throw "MCP Inspector failed with exit code $LASTEXITCODE."
        }
        $page = Read-WallPage ($output -join [Environment]::NewLine)
    }
    if ($page.offset -ne $offset -or ($null -ne $page.nextOffset -and $page.nextOffset -le $offset)) {
        throw 'The server returned invalid pagination.'
    }
    foreach ($wall in $page.items) {
        if (-not $CommentsOnly -or -not [string]::IsNullOrWhiteSpace($wall.comments)) {
            $walls.Add($wall)
        }
    }
    $offset = $page.nextOffset
} while ($null -ne $offset)

$columns = @('ElementId', 'TypeId', 'Type', 'Kind', 'Unit', 'Length', 'Width', 'Height',
    'CoordinateSystem', 'StartX', 'StartY', 'StartZ', 'EndX', 'EndY', 'EndZ', 'Comments')
$rows = @(
    foreach ($wall in $walls) {
        [pscustomobject][ordered]@{
            ElementId = $wall.id
            TypeId = $wall.typeId
            Type = Protect-ExcelText $wall.typeName
            Kind = Protect-ExcelText $wall.kind
            Unit = Protect-ExcelText $wall.unit
            Length = Format-Number $wall.length
            Width = Format-Number $wall.width
            Height = Format-Number $wall.height
            CoordinateSystem = Protect-ExcelText $wall.position.coordinateSystem
            StartX = Format-Number $wall.position.start.x
            StartY = Format-Number $wall.position.start.y
            StartZ = Format-Number $wall.position.start.z
            EndX = Format-Number $wall.position.end.x
            EndY = Format-Number $wall.position.end.y
            EndZ = Format-Number $wall.position.end.z
            Comments = Protect-ExcelText $wall.comments
        }
    }
)
if ($rows.Count -gt 0) {
    $csv = @($rows | ConvertTo-Csv -NoTypeInformation -UseCulture)
}
else {
    $empty = [ordered]@{}
    foreach ($column in $columns) { $empty[$column] = '' }
    $csv = @(([pscustomobject]$empty | ConvertTo-Csv -NoTypeInformation -UseCulture)[0])
}
$fullPath = [IO.Path]::GetFullPath($OutputPath)
[IO.File]::WriteAllLines($fullPath, [string[]]$csv, [Text.UTF8Encoding]::new($true))
Write-Output "Exported $($rows.Count) walls to $fullPath"
