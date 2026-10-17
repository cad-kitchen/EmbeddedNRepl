[CmdletBinding()]
param([ValidateSet('Target', 'BuildFailure', 'Coverage', 'CoverageMissing', 'CoverageEmpty', 'PublishFailure')][string]$Case = 'Target')
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureRoot = Join-Path $repositoryRoot ('artifacts/runner-contracts/' + [guid]::NewGuid().ToString('N'))
$fixtureBuild = Join-Path $fixtureRoot 'build'
New-Item -ItemType Directory -Path $fixtureBuild -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'build/Run-Tests.ps1') -Destination $fixtureBuild
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'build/coverage.runsettings') -Destination $fixtureBuild
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Fixtures/TestSdk.ps1') -Destination $fixtureBuild
@'
$ErrorActionPreference = 'Stop'
$nreplRoot = Split-Path -Parent $PSScriptRoot
$nreplSdk = Join-Path $PSScriptRoot 'TestSdk.ps1'
'@ | Set-Content -LiteralPath (Join-Path $fixtureBuild 'Use-WorkspaceSdk.ps1')
$fixtureMode = switch ($Case) {
    'BuildFailure' { 'build-failure' }
    'CoverageMissing' { 'coverage-missing' }
    'CoverageEmpty' { 'coverage-empty' }
    'PublishFailure' { 'publish-failure' }
    default { 'success' }
}
$fixtureMode |
    Set-Content -LiteralPath (Join-Path $fixtureRoot 'mode.txt')
$runnerLog = Join-Path $fixtureRoot 'runner.log'
$runnerArguments = @('-NoProfile', '-File', (Join-Path $fixtureBuild 'Run-Tests.ps1'), '-Layer', 'Unit', '-Framework', 'net10.0', '-RunName', 'contract')
if ($Case -in @('Coverage', 'CoverageMissing', 'CoverageEmpty', 'PublishFailure')) { $runnerArguments += '-Coverage' }
& pwsh @runnerArguments *> $runnerLog
$runnerExit = $LASTEXITCODE
Write-Output ('RUNNER_CONTRACT_DIRECTORY=' + $fixtureRoot)
$summaries = @(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'artifacts/tests') -Filter results.json -Recurse)
if ($summaries.Count -ne 1) { throw 'The runner must retain one structured outcome.' }
$result = @(Get-Content -LiteralPath $summaries[0].FullName -Raw | ConvertFrom-Json)[0]
if ($Case -in @('CoverageMissing', 'CoverageEmpty', 'PublishFailure')) {
    $expected = if ($Case -eq 'PublishFailure') { 'publish-failed' } else { 'coverage-failed' }
    if ($runnerExit -eq 0 -or $result.status -ne $expected -or $null -ne $result.coverage) {
        throw 'Coverage/publish failure must fail the run without inventing a coverage report.'
    }
    Write-Output "PASS: $Case fails with its structured diagnostic."
    exit 0
}
if ($Case -eq 'BuildFailure') {
    if ($runnerExit -eq 0) { throw 'A failed build must fail the runner.' }
    $summaries = @(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'artifacts/tests') -Filter results.json -Recurse)
    if ($summaries.Count -ne 1) { throw 'A build failure must still produce results.json.' }
    $result = @(Get-Content -LiteralPath $summaries[0].FullName -Raw | ConvertFrom-Json)[0]
    if ($result.status -ne 'build-failed' -or $result.exitCode -ne 37 -or $result.target -ne 'net10.0') {
        throw 'The summary must distinguish build failure from a behavioral test failure.'
    }
    if ($null -ne $result.total) { throw 'A failed build must not invent a discovered-test count.' }
    if (-not (Test-Path -LiteralPath $result.buildLog)) { throw 'The summary must link the retained build log.' }
    if ((Get-Content -LiteralPath $result.buildLog -Raw) -notmatch 'Controlled build failure') { throw 'The original build failure must be retained.' }
    Write-Output 'PASS: build failures retain a structured outcome and their original log.'
    exit 0
}
if ($runnerExit -ne 0) { throw ('Expected a successful fixture run. See ' + $runnerLog) }
if ($Case -eq 'Coverage') {
    $calls = @(Get-Content -LiteralPath (Join-Path $fixtureRoot 'calls.jsonl') | ForEach-Object { ,(ConvertFrom-Json -InputObject $_ -NoEnumerate) })
    $publish = @($calls | Where-Object { $_[0] -eq 'publish' })
    $test = @($calls | Where-Object { $_[0] -eq 'vstest' })
    if ($publish.Count -ne 1 -or $test.Count -ne 1 -or '/Collect:XPlat Code Coverage' -notin $test[0]) {
        throw 'Coverage must publish an isolated copy and enable the collector.'
    }
    if (-not $test[0][1].Contains('published') -or -not (Test-Path -LiteralPath $result.coverage)) {
        throw 'Coverage must use the isolated assembly and retain its report.'
    }
    Write-Output 'PASS: coverage runs against an isolated published copy and retains its report.'
    exit 0
}
$builds = @(Get-Content -LiteralPath (Join-Path $fixtureRoot 'calls.jsonl') | ForEach-Object {
    $call = ConvertFrom-Json -InputObject $_ -NoEnumerate
    if ($call[0] -eq 'build') { ,$call }
})
if ($builds.Count -ne 1) { throw 'Expected exactly one build for the selected target.' }
$frameworkOption = [Array]::IndexOf($builds[0], '--framework')
if ($frameworkOption -lt 0) { $frameworkOption = [Array]::IndexOf($builds[0], '-f') }
if ($frameworkOption -lt 0 -or $builds[0][$frameworkOption + 1] -ne 'net10.0') {
    throw 'The selected net10.0 target was not passed to the build command.'
}
Write-Output 'PASS: only the selected framework is built.'
exit 0
