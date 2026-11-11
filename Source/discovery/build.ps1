param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $Destination)) { $null = New-Item -ItemType Directory -Path $Destination }
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll', ('/out:' + (Join-Path $Destination 'sw-api-probe.exe')), (Join-Path $PSScriptRoot 'SwApiProbe.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Discovery compilation failed.' }
