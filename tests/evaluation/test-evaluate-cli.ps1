param([Parameter(Mandatory)][string]$Dotnet)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$pwsh=(Get-Process -Id $PID).Path
$count=0
foreach($backend in @('ifc','revit')) {
    $raw= & $pwsh -NoProfile -File "$root/scripts/evaluate.ps1" -Backend $backend -Dotnet $Dotnet
    $code=$LASTEXITCODE
    if($code -ne 2){throw "expected blocked exit2 for $backend, got $code"}
    $report=($raw -join "`n") | ConvertFrom-Json
    if($report.verification_state -ne 'blocked' -or $report.product_acceptance -ne $false -or $report.outcome -ne 'fail'){throw 'false backend promotion'}
    if($report.counts.blocked -eq 0 -or $report.metrics.model_elements_written.value -ne $null){throw 'missing gaps or false metrics'}
    $count++
}
$raw= & $Dotnet "$root/interfaces/cli/bin/Release/net8.0/Bim.Cli.dll" evaluate offline "$root/does-not-exist-observations.json"
if($LASTEXITCODE -ne 1 -or (($raw -join "`n")|ConvertFrom-Json).outcome -ne 'fail'){throw 'malformed input did not fail closed'}
$count++
$beforeTemp=@{}
foreach($name in @('TEMP','TMP','TMPDIR')){$beforeTemp[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$failureOutput=Join-Path $root ('.bim/evaluation/startup-'+[Guid]::NewGuid().ToString('N'))
$raw= & "$root/scripts/evaluate.ps1" -Backend ifc -Dotnet (Join-Path $root 'missing-sdk-executable') -OutputDirectory $failureOutput
if($LASTEXITCODE -ne 2){throw 'missing SDK must block'}
foreach($name in $beforeTemp.Keys){if([Environment]::GetEnvironmentVariable($name,'Process') -cne $beforeTemp[$name]){throw "launcher leaked process $name on startup failure"}}
if(!(Test-Path (Join-Path $failureOutput 'report.json'))){throw 'startup failure report was not persisted'}
$count++
$raw= & "$root/scripts/evaluate.ps1" -Backend ifc -Dotnet $Dotnet
if($LASTEXITCODE -ne 2){throw 'IFC must block'}
foreach($name in $beforeTemp.Keys){if([Environment]::GetEnvironmentVariable($name,'Process') -cne $beforeTemp[$name]){throw "launcher leaked process $name after execution"}}
$count++
"PASS $count evaluation CLI assertions (blocked adapters; no model writes)"

