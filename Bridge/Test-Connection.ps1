param([int]$SolidWorksPid = 0)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BridgeClient.ps1')
$client = $null
$report = [ordered]@{ attempted_at=[DateTimeOffset]::Now.ToString('o'); mode='read_only'; success=$false; cad_writes_attempted=$false }
try {
    $client = New-SwBridgeClient -PackageRoot $PSScriptRoot -SolidWorksPid $SolidWorksPid
    Initialize-SwBridgeClient $client
    $tools = Invoke-SwBridgeRpc $client 'tools/list'
    $status = Invoke-SwBridgeTool $client 'sw_status'
    $report.tools = @($tools.tools | ForEach-Object name)
    $report.status = $status
    if ($status.data.connection -ne 'connected') { throw ('Connection status: ' + $status.data.connection) }
    $document = $status.data.documents | Where-Object { $_.document_id -eq $status.data.active_document_id } | Select-Object -First 1
    if ($null -eq $document) { $document = $status.data.documents | Select-Object -First 1 }
    if ($null -ne $document) {
        $report.inspection = Invoke-SwBridgeTool $client 'sw_inspect' @{session_id=$status.session_id;document_id=$document.document_id;scopes=@('summary','geometry','parameters','references','checks')}
    }
    $report.success = $true
} catch {
    $report.error = $_.Exception.Message
} finally {
    if ($null -ne $client) { $report.transcript = $client.Transcript.ToArray() }
    Close-SwBridgeClient $client
    $folder = Join-Path $PSScriptRoot 'reports'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $path = Join-Path $folder ('connection-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '.json')
    $report | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Output $path
    if (-not $report.success) { Write-Error $report.error }
}
