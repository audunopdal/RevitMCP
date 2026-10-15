[CmdletBinding()]
param(
    [string]$RevitInstallDir = 'C:\Program Files\Autodesk\Revit 2026',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$addinOutput = Join-Path $root 'artifacts\addin'
$serverOutput = Join-Path $root 'artifacts\server'

if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    throw 'Close Revit before installing or updating the add-in.'
}

& dotnet publish (Join-Path $root 'src\RevitMCP.Addin\RevitMCP.Addin.csproj') -c $Configuration -o $addinOutput "-p:RevitInstallDir=$RevitInstallDir"
if ($LASTEXITCODE -ne 0) { throw 'Add-in publish failed.' }
& dotnet publish (Join-Path $root 'src\RevitMCP.Server\RevitMCP.Server.csproj') -c $Configuration -o $serverOutput
if ($LASTEXITCODE -ne 0) { throw 'MCP server publish failed.' }

if ($Configuration -eq 'Debug') {
    foreach ($symbolPath in @(
        (Join-Path $addinOutput 'RevitMCP.Addin.pdb'),
        (Join-Path $serverOutput 'RevitMCP.Server.pdb')
    )) {
        if (-not (Test-Path -LiteralPath $symbolPath -PathType Leaf)) {
            throw "Debug symbols missing: $symbolPath"
        }
    }
}

$manifestDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
$assemblyPath = [System.Security.SecurityElement]::Escape((Join-Path $addinOutput 'RevitMCP.Addin.dll'))
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RevitMCP</Name>
    <Assembly>$assemblyPath</Assembly>
    <AddInId>623E8A1D-9444-4C87-B1D3-B22AC63022B8</AddInId>
    <FullClassName>RevitMCP.Addin.App</FullClassName>
    <VendorId>RMCP</VendorId>
    <VendorDescription>Local read-only Revit MCP bridge</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
$manifestPath = Join-Path $manifestDirectory 'RevitMCP.addin'
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding utf8
Write-Output "Installed manifest: $manifestPath"
Write-Output "Installed build configuration: $Configuration"
Write-Output 'Start Revit 2026 and open a model. Then start the revit MCP server in VS Code.'
