$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$gameRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot '..\..')).Path
if (Get-Process -Name KingdomInc -ErrorAction SilentlyContinue) { throw 'A game is already running. Runtime HTTP check requires an owned main-menu process.' }
if (Get-NetTCPConnection -LocalPort 17840 -State Listen -ErrorAction SilentlyContinue) { throw 'API port is already in use.' }
$verification = Join-Path $projectRoot 'Verification'
New-Item -ItemType Directory -Path $verification -Force | Out-Null
$logPath = Join-Path $verification ('unity-http-' + [Guid]::NewGuid().ToString('N') + '.log')
$gameExe = Join-Path $gameRoot 'KingdomInc.exe'
$ownedProcess = Start-Process -FilePath $gameExe -WorkingDirectory $gameRoot -WindowStyle Hidden -ArgumentList @('-screen-width','960','-screen-height','640','-screen-fullscreen','0','-logFile',('"' + $logPath + '"')) -PassThru
try {
    $deadline = (Get-Date).AddSeconds(35)
    $health = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:17840/api/v1/health' -TimeoutSec 1
            if ($health.status -eq 'noKingdom' -and -not $health.stale) { break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $health -or $health.status -ne 'noKingdom' -or $health.stale) { throw 'Main-menu capture did not become ready.' }
    $catalog = Invoke-WebRequest -Uri 'http://127.0.0.1:17840/api/v1/catalog' -Headers @{Origin='http://localhost:5173'} -TimeoutSec 3
    $catalogData = $catalog.Content | ConvertFrom-Json
    if ($catalog.Headers['Access-Control-Allow-Origin'] -ne 'http://localhost:5173') { throw 'Local web client origin was not allowed.' }
    if (-not $catalogData.referenceMatchesRuntime -or $catalogData.items.Count -lt 200 -or $catalogData.recipes.Count -lt 200) { throw 'Runtime catalog incomplete or reference version mismatch.' }
    $snapshot = Invoke-RestMethod -Uri 'http://127.0.0.1:17840/api/v1/snapshot?sections=session,research,factory' -TimeoutSec 3
    if ($snapshot.sections.session.data.loaded -or $null -ne $snapshot.sections.research.data -or $null -ne $snapshot.sections.factory.data) { throw 'Main menu contains stale kingdom data.' }
    $image = Invoke-WebRequest -Uri 'http://127.0.0.1:17840/api/v1/icons/Icons_9.png' -TimeoutSec 3
    if ($image.Headers['Content-Type'] -ne 'image/png' -or $image.RawContentLength -le 8) { throw 'Game icon endpoint failed.' }
    $spec = Invoke-RestMethod -Uri 'http://127.0.0.1:17840/api/v1/openapi.json' -TimeoutSec 3
    if ($spec.openapi -ne '3.1.0') { throw 'API contract was not served.' }
    $write = Invoke-WebRequest -Uri 'http://127.0.0.1:17840/api/v1/snapshot' -Method Post -SkipHttpErrorCheck -TimeoutSec 3
    if ($write.StatusCode -ne 405) { throw 'HTTP write was not rejected.' }
    @{passed=$true;initialProcessId=$ownedProcess.Id;log=$logPath;unityHttp=$true;crossPortCors=$true;mainMenuClearsKingdom=$true;icons=$true;openApi=$true;readOnly=$true;items=$catalogData.items.Count;recipes=$catalogData.recipes.Count;research=$catalogData.research.Count;savedKingdomLoaded=$false;testedAt=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $verification 'runtime-http-tests.json')
    Get-Content -LiteralPath (Join-Path $verification 'runtime-http-tests.json')
} finally {
    $ownedProcess.Refresh()
    if (-not $ownedProcess.HasExited) { Stop-Process -Id $ownedProcess.Id }
    # Steam may replace the initial process. Only its exact unique log argument
    # and executable path authorize cleanup of the replacement process.
    foreach ($replacement in Get-CimInstance Win32_Process -Filter "Name='KingdomInc.exe'") {
        if ($replacement.ExecutablePath -eq $gameExe -and $replacement.CommandLine.Contains($logPath)) { Stop-Process -Id $replacement.ProcessId }
    }
}
