[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$AllowWrite,
    [ValidateRange(0,2147483647)][int]$SolidWorksPid = 0,
    [string[]]$ApiLibrary = @()
)
$ErrorActionPreference = 'Stop'
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if (Test-Path -LiteralPath $destination) { throw 'Output exists; choose a new file. Existing Codex configuration is not modified.' }
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$exe = Join-Path $root 'Bridge\SwBridge.exe'
$state = Join-Path $root ('Bridge\state\codex-' + [Guid]::NewGuid().ToString('N'))
$models = Join-Path $root 'Bridge\models'
$serverArgs = @('--attach-only', '--state-root', $state, '--output-root', $models)
if ($AllowWrite) { $serverArgs += '--allow-write' }
if ($SolidWorksPid -gt 0) { $serverArgs += @('--pid', [string]$SolidWorksPid) }
foreach ($library in $ApiLibrary) {
    $libraryPath = [IO.Path]::GetFullPath($library)
    if (-not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) { throw "API Interop library not found: $libraryPath" }
    $serverArgs += @('--api-library', $libraryPath)
}
# JSON encoding is valid for these TOML basic strings and string arrays.
$exeLiteral = ConvertTo-Json -InputObject $exe -Compress
$argsLiteral = ConvertTo-Json -InputObject $serverArgs -Compress
$text = @"
# Generated for this extracted directory. Merge this block; preserve other settings.
[mcp_servers.solidworks_local_01a1cb2e]
command = $exeLiteral
args = $argsLiteral
"@
$stream = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    try { $writer.WriteLine($text) } finally { $writer.Dispose() }
} finally { $stream.Dispose() }
Write-Output $destination
