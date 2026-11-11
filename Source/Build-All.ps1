[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination, [string]$InteropDirectory)
$ErrorActionPreference = 'Stop'
$target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
if (Test-Path -LiteralPath $target) { throw 'Build destination exists; choose a fresh directory.' }
$bridgeArgs = @{Destination=(Join-Path $target 'Bridge')}
if ($InteropDirectory) { $bridgeArgs.InteropDirectory = $InteropDirectory }
& (Join-Path $PSScriptRoot 'bridge\build.ps1') @bridgeArgs
& (Join-Path $PSScriptRoot 'discovery\build.ps1') -Destination (Join-Path $target 'Discovery')
Write-Output $target
