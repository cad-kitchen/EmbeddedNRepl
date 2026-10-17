#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReportDirectory,
    [Parameter(Mandatory)][ValidateSet('net472', 'net8.0', 'net10.0')][string]$Framework,
    [Parameter(Mandatory)][string[]]$TestResults,
    [ValidateRange(0, 100)][double]$MinimumLinePercent = 0,
    [ValidateRange(0, 100)][double]$MinimumBranchPercent = 0,
    [ValidateRange(0, 100)][double]$MinimumModuleLinePercent = 0,
    [ValidateRange(0, 100)][double]$MinimumModuleBranchPercent = 0
)
$ErrorActionPreference = 'Stop'
$nreplReport = (Resolve-Path -LiteralPath $ReportDirectory).Path
$nreplScope = Get-Content (Join-Path $PSScriptRoot 'coverage-scope.json') -Raw | ConvertFrom-Json
$nreplExpected = @($nreplScope.assemblies)
$nreplRequiredProjects = @($nreplScope.projects)
$nreplSource = Get-Content -LiteralPath (Join-Path $nreplReport 'Summary.json') -Raw | ConvertFrom-Json
$nreplAssemblies = @($nreplSource.coverage.assemblies)
if (Compare-Object $nreplExpected @($nreplAssemblies.name)) {
    throw 'Coverage must contain exactly the declared production libraries.'
}
function Get-Metrics($Data) {
    foreach ($nreplField in @('coveredlines', 'coverablelines', 'coveredbranches', 'totalbranches')) {
        if ($null -eq $Data.$nreplField -or [long]$Data.$nreplField -lt 0) { throw "Invalid coverage counter: $nreplField" }
    }
    if ($Data.coveredlines -gt $Data.coverablelines -or $Data.coveredbranches -gt $Data.totalbranches) {
        throw 'Covered counters cannot exceed their denominators.'
    }
    return [ordered]@{
        coveredLines = [long]$Data.coveredlines
        coverableLines = [long]$Data.coverablelines
        linePercent = $(if ($Data.coverablelines -gt 0) { [Math]::Round(100.0 * $Data.coveredlines / $Data.coverablelines, 2) } else { $null })
        coveredBranches = [long]$Data.coveredbranches
        totalBranches = [long]$Data.totalbranches
        branchPercent = $(if ($Data.totalbranches -gt 0) { [Math]::Round(100.0 * $Data.coveredbranches / $Data.totalbranches, 2) } else { $null })
    }
}
$nreplTests = @($TestResults | ForEach-Object { Get-Content -LiteralPath $_ -Raw | ConvertFrom-Json })
if ($nreplTests.Count -ne $nreplRequiredProjects.Count -or @($nreplTests | Where-Object { $_.status -ne 'passed' -or $_.target -ne $Framework -or -not $_.coverage }).Count -gt 0) {
    throw 'A complete measurement requires all declared instrumented test suites on one framework.'
}
if (Compare-Object $nreplRequiredProjects @($nreplTests.project)) { throw 'Unexpected or duplicate coverage test suites.' }
foreach ($nreplTest in $nreplTests) {
    if (-not (Test-Path -LiteralPath $nreplTest.coverage) -or [int]$nreplTest.total -le 0 -or
        [int]$nreplTest.passed -ne [int]$nreplTest.total -or [int]$nreplTest.failed -ne 0 -or [int]$nreplTest.notExecuted -ne 0) {
        throw 'Cannot publish coverage from missing, failed, empty or skipped test results.'
    }
}
$nreplTotals = Get-Metrics $nreplSource.summary
if ($nreplTotals.coverableLines -le 0 -or $nreplTotals.totalBranches -le 0) { throw 'The report must measure production lines and branches.' }
$nreplModules = @($nreplAssemblies | Sort-Object name | ForEach-Object {
    $nreplMetrics = Get-Metrics $_
    if ($nreplMetrics.coverableLines -le 0) { throw "No instrumented source lines in $($_.name)." }
    $nreplMetrics.name = $_.name
    [pscustomobject]$nreplMetrics
})
foreach ($nreplCounter in @('coveredLines', 'coverableLines', 'coveredBranches', 'totalBranches')) {
    $nreplModuleSum = ($nreplModules | Measure-Object -Property $nreplCounter -Sum).Sum
    if ($nreplModuleSum -ne $nreplTotals.$nreplCounter) { throw "Inconsistent merged coverage counter: $nreplCounter" }
}
$nreplUnmeasured = @(
    'Dynamically compiled ClojureCLR sources (.cljr).',
    'Console samples, build scripts and external client processes.',
    'Application-specific host integrations.'
)
$nreplBelowThreshold = 100.0 * $nreplTotals.coveredLines / $nreplTotals.coverableLines -lt $MinimumLinePercent -or
    100.0 * $nreplTotals.coveredBranches / $nreplTotals.totalBranches -lt $MinimumBranchPercent
$nreplModuleFailures = @($nreplModules | ForEach-Object {
    if (100.0 * $_.coveredLines / $_.coverableLines -lt $MinimumModuleLinePercent) { "$($_.name): line coverage below $MinimumModuleLinePercent%." }
    if ($_.totalBranches -gt 0 -and 100.0 * $_.coveredBranches / $_.totalBranches -lt $MinimumModuleBranchPercent) { "$($_.name): branch coverage below $MinimumModuleBranchPercent%." }
})
$nreplBelowThreshold = $nreplBelowThreshold -or $nreplModuleFailures.Count -gt 0
$nreplSummary = [ordered]@{
    schemaVersion = 1
    status = $(if ($nreplBelowThreshold) { 'below-threshold' } else { 'measured' })
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    framework = $Framework
    scope = [ordered]@{
        description = 'C# production library exercised by independent tests; dynamically compiled language sources are unmeasured.'
        assemblies = $nreplExpected
        unmeasured = $nreplUnmeasured
    }
    tests = [ordered]@{ total = [int](($nreplTests | ForEach-Object { [int]$_.total } | Measure-Object -Sum).Sum); suites = $nreplTests.Count; results = $TestResults }
    totals = $nreplTotals
    modules = $nreplModules
    thresholds = [ordered]@{
        minimumLinePercent = $MinimumLinePercent; minimumBranchPercent = $MinimumBranchPercent
        minimumModuleLinePercent = $MinimumModuleLinePercent; minimumModuleBranchPercent = $MinimumModuleBranchPercent
        moduleFailures = $nreplModuleFailures
    }
}
$nreplSummary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $nreplReport 'coverage-summary.json') -Encoding utf8
$nreplMarkdown = [Collections.Generic.List[string]]::new()
$nreplMarkdown.Add('# EmbeddedNRepl C# library coverage')
$nreplMarkdown.Add('')
$nreplMarkdown.Add("Framework: $Framework. $($nreplSummary.tests.total) passed tests across $($nreplTests.Count) suites. Status: $($nreplSummary.status).")
$nreplMarkdown.Add('')
$nreplMarkdown.Add('| Module | Lines | Branches |')
$nreplMarkdown.Add('| --- | ---: | ---: |')
foreach ($nreplModule in $nreplModules) {
    $nreplLinePercent = if ($null -eq $nreplModule.linePercent) { 'n/a' } else { "$($nreplModule.linePercent)%" }
    $nreplBranchPercent = if ($null -eq $nreplModule.branchPercent) { 'n/a' } else { "$($nreplModule.branchPercent)%" }
    $nreplMarkdown.Add("| $($nreplModule.name) | $nreplLinePercent ($($nreplModule.coveredLines)/$($nreplModule.coverableLines)) | $nreplBranchPercent ($($nreplModule.coveredBranches)/$($nreplModule.totalBranches)) |")
}
$nreplMarkdown.Add("| **Combined** | **$($nreplTotals.linePercent)%** ($($nreplTotals.coveredLines)/$($nreplTotals.coverableLines)) | **$($nreplTotals.branchPercent)%** ($($nreplTotals.coveredBranches)/$($nreplTotals.totalBranches)) |")
$nreplMarkdown.Add('')
$nreplMarkdown.Add('Combined percentages use merged hits and weighted line/branch counts, not averages of test runs or module percentages. Unexecuted code in the included assemblies remains in the denominator.')
$nreplMarkdown.Add('')
$nreplMarkdown.Add('## Scope limits')
$nreplMarkdown.Add('')
$nreplMarkdown.Add('This is not a percentage for all embedded language behavior. ClojureCLR tests execute real language code, but the dynamically generated code has no instrumented source coverage in this report. The following areas are unmeasured, not 0% or 100%:')
$nreplMarkdown.Add('')
foreach ($nreplLimit in $nreplSummary.scope.unmeasured) { $nreplMarkdown.Add('- ' + $nreplLimit) }
$nreplMarkdown.Add('')
$nreplMarkdown.Add('[Open the detailed HTML report](index.html). Raw merged data: [Cobertura.xml](Cobertura.xml).')
$nreplMarkdown.Add('')
$nreplMarkdown.Add("Required minimums: combined lines $MinimumLinePercent%, combined branches $MinimumBranchPercent%; each module lines $MinimumModuleLinePercent%, branches $MinimumModuleBranchPercent%. Coverage does not measure application-specific integrations.")
foreach ($nreplFailure in $nreplModuleFailures) { $nreplMarkdown.Add('- ' + $nreplFailure) }
[IO.File]::WriteAllLines((Join-Path $nreplReport 'summary.md'), $nreplMarkdown)
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($nreplMarkdown -join [Environment]::NewLine) }
Write-Output "LINE_COVERAGE=$($nreplTotals.linePercent); BRANCH_COVERAGE=$($nreplTotals.branchPercent); REPORT=$nreplReport"
if ($nreplBelowThreshold) { throw 'Coverage is below the requested minimums; the report has been retained.' }
