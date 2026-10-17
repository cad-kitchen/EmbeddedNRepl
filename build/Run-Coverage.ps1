#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('net472', 'net8.0', 'net10.0')][string]$Framework = 'net10.0',
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$RunName = 'coverage',
    [ValidateRange(0, 100)][double]$MinimumLinePercent = 90,
    [ValidateRange(0, 100)][double]$MinimumBranchPercent = 90,
    [ValidateRange(0, 100)][double]$MinimumModuleLinePercent = 90,
    [ValidateRange(0, 100)][double]$MinimumModuleBranchPercent = 90,
    [string]$Net8SdkRoot,
    [switch]$NoBuild
)
. "$PSScriptRoot/Use-WorkspaceSdk.ps1"
$nreplDestination = Join-Path $nreplRoot ("artifacts/coverage/$RunName-$Framework-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $nreplDestination | Out-Null
Write-Output "COVERAGE_DIRECTORY=$nreplDestination"
$nreplRunState = [ordered]@{ framework = $Framework; status = 'running'; stage = 'tools'; testResults = @(); error = $null }
Push-Location $nreplRoot
try {
    & $nreplSdk tool restore --tool-manifest "$nreplRoot/.config/dotnet-tools.json" --configfile "$nreplRoot/build/ci/NuGet.Config" *> (Join-Path $nreplDestination 'tools.log')
    if ($LASTEXITCODE -ne 0) { throw 'Pinned ReportGenerator restore failed; see tools.log.' }
    $nreplResultFiles = @()
    $nreplCoverageFiles = @()
    foreach ($nreplStage in @(
        @{ Layer = 'Independent'; Filter = $null },
        @{ Layer = 'ClojureClr'; Filter = 'FullyQualifiedName!~AcceptanceAvailabilityTests' }
    )) {
        $nreplRunState.stage = $nreplStage.Layer
        Write-Output "COVERAGE_STAGE=$($nreplStage.Layer); framework=$Framework"
        $nreplParameters = @{ Layer = $nreplStage.Layer; Framework = @($Framework); Coverage = $true; RunName = "$RunName-$($nreplStage.Layer)"; NoBuild = $NoBuild }
        if ($nreplStage.Filter) { $nreplParameters.Filter = $nreplStage.Filter }
        if ($Net8SdkRoot) { $nreplParameters.Net8SdkRoot = $Net8SdkRoot }
        $nreplOutput = @(& "$PSScriptRoot/Run-Tests.ps1" @nreplParameters 2>&1 | Tee-Object -FilePath (Join-Path $nreplDestination "$($nreplStage.Layer).log"))
        $nreplExit = $LASTEXITCODE
        $nreplEvidence = @($nreplOutput | ForEach-Object { if ("$_" -match '^EVIDENCE_DIRECTORY=(.+)$') { $Matches[1] } })
        if ($nreplEvidence.Count -ne 1) { throw "Missing test evidence for $($nreplStage.Layer)." }
        $nreplResultFile = Join-Path $nreplEvidence[0] 'results.json'
        $nreplResultFiles += $nreplResultFile
        $nreplRunState.testResults = $nreplResultFiles
        if ($nreplExit -ne 0) { throw "Instrumented $($nreplStage.Layer) tests failed; see $($nreplStage.Layer).log and results.json." }
        $nreplRows = @(Get-Content -LiteralPath $nreplResultFile -Raw | ConvertFrom-Json)
        if (@($nreplRows | Where-Object { $_.status -ne 'passed' -or -not $_.coverage }).Count -gt 0) { throw 'Missing or unsuccessful coverage result.' }
        $nreplCoverageFiles += $nreplRows.coverage
    }
    $nreplRunState.stage = 'report'
    if ($nreplCoverageFiles.Count -ne @((Get-Content (Join-Path $PSScriptRoot 'coverage-scope.json') -Raw | ConvertFrom-Json).projects).Count) { throw 'Expected one coverage report for each declared suite.' }
    & $nreplSdk tool run reportgenerator -- "-reports:$($nreplCoverageFiles -join ';')" "-targetdir:$nreplDestination" '-reporttypes:Html;Cobertura;JsonSummary;MarkdownSummaryGithub' "-sourcedirs:$nreplRoot" "-title:EmbeddedNRepl C# library coverage ($Framework)" '-verbosity:Warning' *> (Join-Path $nreplDestination 'reportgenerator.log')
    if ($LASTEXITCODE -ne 0) { throw 'Coverage report generation failed; see reportgenerator.log.' }
    & "$PSScriptRoot/Write-CoverageSummary.ps1" -ReportDirectory $nreplDestination -Framework $Framework -TestResults $nreplResultFiles -MinimumLinePercent $MinimumLinePercent -MinimumBranchPercent $MinimumBranchPercent -MinimumModuleLinePercent $MinimumModuleLinePercent -MinimumModuleBranchPercent $MinimumModuleBranchPercent
    $nreplRunState.status = 'measured'
} catch {
    $nreplRunState.status = 'failed'
    $nreplRunState.error = $_.Exception.Message
    throw
} finally {
    $nreplRunState | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $nreplDestination 'run.json') -Encoding utf8
    Pop-Location
}
