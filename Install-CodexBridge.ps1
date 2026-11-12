[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigPath,
    [switch]$AllowWrite,
    [ValidateRange(0,2147483647)][int]$SolidWorksPid = 0,
    [string[]]$ApiLibrary = @()
)
$ErrorActionPreference = 'Stop'
if (-not $ConfigPath) {
    $codexDirectory = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    $ConfigPath = Join-Path $codexDirectory 'config.toml'
}
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigPath)
$verification = & (Join-Path $PSScriptRoot 'Verify-Package.ps1') | ConvertFrom-Json
if ($verification.status -ne 'passed') { throw 'Package verification failed.' }
$begin = '# BEGIN solidworks-local-bridge'
$end = '# END solidworks-local-bridge'
$pattern = '(?ms)^' + [regex]::Escape($begin) + '\r?\n.*?^' + [regex]::Escape($end) + '(?:\r?\n|$)'
$existing = if (Test-Path -LiteralPath $destination) { [IO.File]::ReadAllText($destination) } else { '' }
$matches = [regex]::Matches($existing, $pattern)
if ($matches.Count -gt 1) { throw 'Multiple managed bridge blocks found. Resolve them before installing.' }
$unmanaged = [regex]::Replace($existing, $pattern, '')
if ($unmanaged -match '(?m)^\s*\[mcp_servers\.(?:solidworks_local_01a1cb2e|"solidworks_local_01a1cb2e"|''solidworks_local_01a1cb2e'')(?:\.|\])') {
    throw 'An existing SolidWorks MCP table is outside the managed block. Update it using New-CodexConfig.ps1 instead.'
}
if ([regex]::Matches($existing, '(?m)^' + [regex]::Escape($begin) + '\r?$').Count -ne $matches.Count -or
    [regex]::Matches($existing, '(?m)^' + [regex]::Escape($end) + '\r?$').Count -ne $matches.Count) {
    throw 'Incomplete managed bridge block. Resolve it before installing.'
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('solidworks-' + [guid]::NewGuid().ToString('N') + '.toml')
try {
    & (Join-Path $PSScriptRoot 'New-CodexConfig.ps1') -OutputPath $temporary -AllowWrite:$AllowWrite -SolidWorksPid $SolidWorksPid -ApiLibrary $ApiLibrary | Out-Null
    $block = $begin + "`r`n" + [IO.File]::ReadAllText($temporary).TrimEnd() + "`r`n" + $end + "`r`n"
    $updated = if ($matches.Count) {
        $existing.Substring(0, $matches[0].Index) + $block + $existing.Substring($matches[0].Index + $matches[0].Length)
    } else {
        $existing + $(if ($existing.Length -and -not $existing.EndsWith("`n")) { "`r`n" }) + "`r`n" + $block
    }
    if ($PSCmdlet.ShouldProcess($destination, 'Install SolidWorks MCP configuration')) {
        $directory = Split-Path -Parent $destination
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $pending = Join-Path $directory ('.solidworks-' + [guid]::NewGuid().ToString('N') + '.tmp')
        try {
            [IO.File]::WriteAllText($pending, $updated, [Text.UTF8Encoding]::new($false))
            if (Test-Path -LiteralPath $destination) {
                [IO.File]::Replace($pending, $destination, $destination + '.before-solidworks-' + [guid]::NewGuid().ToString('N') + '.bak')
            } else { [IO.File]::Move($pending, $destination) }
        } finally { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending } }
        Write-Output ('Installed: ' + $destination)
        Write-Output ('Reload Codex MCP, then ask the agent to read: ' + (Join-Path $PSScriptRoot 'AGENTS.md'))
    }
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
