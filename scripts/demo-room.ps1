param([string]$Dotnet = "$PSScriptRoot/../.tools/dotnet/dotnet.exe")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$cli = Join-Path $root 'interfaces/cli/bin/Release/net8.0/Bim.Cli.dll'
if (!(Test-Path $cli)) { throw 'Build Release CLI with scripts/verify.ps1 first' }
$case = Join-Path $root ('.bim/room-demo/' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $case -Force | Out-Null
function Invoke-Cli([string[]]$Arguments, [string]$Output) {
    $json = & $Dotnet $cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "CLI failed: $json" }
    $json | Set-Content -LiteralPath (Join-Path $case $Output)
    return ($json | ConvertFrom-Json)
}
$project = '00000000-0000-0000-0000-000000000001'
$before = Invoke-Cli @('room',$project,'0',"$root/examples/room-intent.json") 'before.json'
$intent = Get-Content "$root/examples/room-intent.json" -Raw | ConvertFrom-Json
$intent.WidthMetres = 5.4
$intent | ConvertTo-Json -Depth 20 | Set-Content "$case/revised-intent.json"
$after = Invoke-Cli @('room',$project,'0',"$case/revised-intent.json") 'after.json'
$null = Invoke-Cli @('init',"$case/state","$case/before.json") 'init.json'
$plan = Invoke-Cli @('plan',"$case/state","$case/after.json") 'plan.json'
if ($plan.Operations.Count -ne 4 -or @($plan.Operations | Where-Object Action -ne 'update').Count) { throw 'Non-minimal room patch' }
$first = Invoke-Cli @('commit-intent',"$case/state","$case/after.json","$case/plan.json",'width-400mm') 'commit.json'
$retry = Invoke-Cli @('commit-intent',"$case/state","$case/after.json","$case/plan.json",'width-400mm') 'retry.json'
if ($first.project.Revision -ne 1 -or $retry.project.Revision -ne 1) { throw 'Non-idempotent intent revision' }
@{status='succeeded';scope='intention_semantic_diff_persistence_only';operations=$plan.Operations.Count;dirty_nodes=$plan.DirtyIds.Count;revision=$retry.project.Revision;native_verified=$false;documentation_regenerated=$false;artifacts=$case} | ConvertTo-Json
