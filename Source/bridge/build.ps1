[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$InteropDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'Bridge')
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sldworks = Join-Path $InteropDirectory 'SolidWorks.Interop.sldworks.dll'
$swconst = Join-Path $InteropDirectory 'SolidWorks.Interop.swconst.dll'
foreach ($item in @($compiler, $sldworks, $swconst)) { if (-not (Test-Path -LiteralPath $item -PathType Leaf)) { throw "Required component missing: $item" } }
if (Test-Path -LiteralPath (Join-Path $Destination 'SwBridge.exe')) { throw 'Destination already contains SwBridge.exe; use a fresh directory.' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$schemaDirectory = Join-Path $Destination 'schemas'
New-Item -ItemType Directory -Path $schemaDirectory -Force | Out-Null
Copy-Item -LiteralPath $sldworks,$swconst -Destination $Destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'schema\bridge.schema.json') -Destination $schemaDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SwBridge.exe.config') -Destination $Destination
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$arguments = @('/nologo','/target:exe','/platform:x64','/optimize+','/warn:4',('/out:'+(Join-Path $Destination 'SwBridge.exe')),
    ('/reference:'+$sldworks),('/reference:'+$swconst),'/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Core.dll') + $sources
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Write-Output (Join-Path $Destination 'SwBridge.exe')
