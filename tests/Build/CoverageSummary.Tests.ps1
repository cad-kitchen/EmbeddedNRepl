#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$nreplRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scope = Get-Content (Join-Path $nreplRoot 'build/coverage-scope.json') -Raw | ConvertFrom-Json
$nreplRun = Join-Path $nreplRoot ('artifacts/runner-contracts/coverage-summary-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $nreplRun | Out-Null
$nreplRaw = Join-Path $nreplRun 'coverage.cobertura.xml'
Set-Content -LiteralPath $nreplRaw -Value '<coverage lines-valid="1" lines-covered="1" />'
$nreplResults = @($scope.projects | ForEach-Object {
    [pscustomobject]@{ project = $_; target = 'net10.0'; status = 'passed'; total = 1; passed = 1; failed = 0; notExecuted = 0; coverage = $nreplRaw }
})
$nreplResultsFile = Join-Path $nreplRun 'results.json'
ConvertTo-Json -InputObject $nreplResults | Set-Content -LiteralPath $nreplResultsFile
$modules = @()
foreach ($name in $scope.assemblies) {
    if ($modules.Count -eq 0) { $modules += @{ name=$name; coveredlines=5; coverablelines=10; coveredbranches=1; totalbranches=4 } }
    else { $modules += @{ name=$name; coveredlines=18; coverablelines=20; coveredbranches=6; totalbranches=8 } }
}
$totals = @{}
foreach ($counter in @('coveredlines', 'coverablelines', 'coveredbranches', 'totalbranches')) {
    $totals[$counter] = ($modules | ForEach-Object { $_[$counter] } | Measure-Object -Sum).Sum
}
$nreplGeneratorSummary = @{ summary=$totals; coverage=@{ assemblies=$modules } }
$nreplGeneratorSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $nreplRun 'Summary.json')
$nreplWriter = Join-Path $nreplRoot 'build/Write-CoverageSummary.ps1'
function Run-Summary([string]$Case, [string[]]$Options = @()) {
    & pwsh -NoProfile -File $nreplWriter -ReportDirectory $nreplRun -Framework net10.0 -TestResults $nreplResultsFile @Options *> (Join-Path $nreplRun "$Case.log")
    return $LASTEXITCODE
}
if ((Run-Summary 'weighted') -ne 0) { throw 'A complete synthetic report must produce a summary.' }
$nreplSummary = Get-Content (Join-Path $nreplRun 'coverage-summary.json') -Raw | ConvertFrom-Json
$expectedLines = [Math]::Round(100 * $totals.coveredlines / $totals.coverablelines, 2)
$expectedBranches = [Math]::Round(100 * $totals.coveredbranches / $totals.totalbranches, 2)
if ($nreplSummary.totals.linePercent -ne $expectedLines -or $nreplSummary.totals.branchPercent -ne $expectedBranches) {
    throw 'Coverage percentages must use counters, not averages of module percentages.'
}
if ($nreplSummary.scope.unmeasured.Count -eq 0 -or $nreplSummary.tests.total -ne $scope.projects.Count) { throw 'The summary must retain scope and test evidence.' }
$threshold = ([Math]::Ceiling($expectedLines) + 1).ToString([Globalization.CultureInfo]::InvariantCulture)
if ((Run-Summary 'threshold' @('-MinimumLinePercent', $threshold)) -eq 0) { throw 'Coverage below a requested minimum must fail.' }
$nreplSummary = Get-Content (Join-Path $nreplRun 'coverage-summary.json') -Raw | ConvertFrom-Json
if ($nreplSummary.status -ne 'below-threshold') { throw 'A threshold failure must still retain a measured report.' }
if ((Run-Summary 'module-lines' @('-MinimumModuleLinePercent', '60')) -eq 0) { throw 'A weak module must fail even when combined coverage passes.' }
$nreplSummary = Get-Content (Join-Path $nreplRun 'coverage-summary.json') -Raw | ConvertFrom-Json
if ($nreplSummary.thresholds.moduleFailures.Count -ne 1) { throw 'The failing module must be identified in the report.' }
if ((Run-Summary 'module-branches' @('-MinimumModuleBranchPercent', '30')) -eq 0) { throw 'Module branch coverage must be enforced independently.' }
$nreplResults[0].status = 'failed'
ConvertTo-Json -InputObject $nreplResults | Set-Content -LiteralPath $nreplResultsFile
if ((Run-Summary 'failed-tests') -eq 0) { throw 'Failed tests cannot produce successful coverage.' }
$nreplResults[0].status = 'passed'
$nreplResults[0].notExecuted = 1
ConvertTo-Json -InputObject $nreplResults | Set-Content -LiteralPath $nreplResultsFile
if ((Run-Summary 'skipped-tests') -eq 0) { throw 'Skipped tests cannot produce complete coverage.' }
$nreplResults[0].notExecuted = 0
ConvertTo-Json -InputObject $nreplResults | Set-Content -LiteralPath $nreplResultsFile
$totals.coveredlines++
$nreplGeneratorSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $nreplRun 'Summary.json')
if ((Run-Summary 'inconsistent-counters') -eq 0) { throw 'Module counters must match the declared combined denominator and hits.' }
$totals.coveredlines--
$nreplGeneratorSummary.coverage.assemblies = @($modules | Select-Object -Skip 1)
$nreplGeneratorSummary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $nreplRun 'Summary.json')
if ((Run-Summary 'missing-module') -eq 0) { throw 'Missing modules cannot inflate the declared coverage scope.' }
Write-Output "COVERAGE_SUMMARY_CONTRACTS=passed; cases=8; evidence=$nreplRun"
exit 0
