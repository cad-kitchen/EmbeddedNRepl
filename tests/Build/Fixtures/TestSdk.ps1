# A process-boundary fixture for Run-Tests.ps1, never a product test runner.
$ErrorActionPreference = 'Stop'
$fixtureRoot = Split-Path -Parent $PSScriptRoot
Add-Content -LiteralPath (Join-Path $fixtureRoot 'calls.jsonl') -Value (ConvertTo-Json -InputObject @($args) -Compress)
if ($args[0] -eq 'build') {
    if ((Get-Content -LiteralPath (Join-Path $fixtureRoot 'mode.txt')) -eq 'build-failure') {
        Write-Output 'Controlled build failure.'
        exit 37
    }
    $output = Join-Path $fixtureRoot 'tests/EmbeddedNRepl.UnitTests/bin/Release/net10.0'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $output 'EmbeddedNRepl.UnitTests.dll') -Value 'fixture only'
    exit 0
}
if ($args[0] -eq 'publish') {
    if ((Get-Content -LiteralPath (Join-Path $fixtureRoot 'mode.txt')) -eq 'publish-failure') { exit 38 }
    $output = $args[[Array]::IndexOf($args, '--output') + 1]
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    exit 0
}
if ($args[0] -ne 'vstest') { throw 'Unexpected fixture command.' }
$fixtureResults = @($args | Where-Object { $_ -like '/ResultsDirectory:*' })[0].Substring('/ResultsDirectory:'.Length)
$fixtureName = @($args | Where-Object { $_ -like '/Logger:trx;LogFileName=*' })[0].Substring('/Logger:trx;LogFileName='.Length)
New-Item -ItemType Directory -Path $fixtureResults -Force | Out-Null
'<TestRun><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>' |
    Set-Content -LiteralPath (Join-Path $fixtureResults $fixtureName)
Write-Output 'ACTUAL_RUNTIME=Runner fixture (not product evidence)'
Write-Output 'PROCESS_ARCHITECTURE=X64'
if ('/Collect:XPlat Code Coverage' -in $args) {
    $mode = Get-Content -LiteralPath (Join-Path $fixtureRoot 'mode.txt')
    if ($mode -ne 'coverage-missing') {
        $lines = if ($mode -eq 'coverage-empty') { 0 } else { 1 }
        "<coverage lines-valid=`"$lines`" lines-covered=`"$lines`" />" |
            Set-Content -LiteralPath (Join-Path $fixtureResults 'coverage.cobertura.xml')
        # The real TRX logger creates a second, deeper copy of the attachment.
        $attachmentCopy = Join-Path $fixtureResults 'trx-copy/In/fixture'
        New-Item -ItemType Directory -Path $attachmentCopy -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $fixtureResults 'coverage.cobertura.xml') -Destination $attachmentCopy
    }
}
exit 0
