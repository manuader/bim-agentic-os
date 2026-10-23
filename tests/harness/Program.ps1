$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../scripts/harness-common.ps1"
$root = Join-Path ([IO.Path]::GetTempPath()) ('bim-harness-' + [guid]::NewGuid())
New-Item -ItemType Directory $root | Out-Null
$pwsh = (Get-Process -Id $PID).Path
Invoke-EvidenceCommand -Name 'stdout' -Executable $pwsh -Arguments @('-NoProfile','-Command','[Console]::WriteLine("captured-native-stdout")') -EvidenceDirectory $root
if ((Get-Content "$root/stdout.log" -Raw) -notmatch 'captured-native-stdout') { throw 'Native stdout was not retained' }
$failed = $false
try { Invoke-EvidenceCommand -Name 'failure' -Executable $pwsh -Arguments @('-NoProfile','-Command','exit 7') -EvidenceDirectory $root } catch { $failed = $_.Exception.Message -match 'exit code 7' }
if (!$failed) { throw 'Nonzero exit did not propagate' }
if (!(Test-Path "$root/failure.log")) { throw 'Failed command lost evidence' }
'PASS harness captures native stdout and propagates nonzero exit'
$parentTemp = @{}
foreach ($name in 'TEMP','TMP','TMPDIR') { $parentTemp[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$isolatedEvidence = Join-Path $root 'isolated-evidence'
$expectedTemp = [IO.Path]::GetFullPath((Join-Path $isolatedEvidence 'tmp')).TrimEnd([IO.Path]::DirectorySeparatorChar)
foreach ($exitCode in 0,7) {
    $failed = $false
    try { Invoke-EvidenceCommand -Name "temp-$exitCode" -Executable $pwsh -Arguments @('-NoProfile','-Command',"[Console]::WriteLine([IO.Path]::GetTempPath()); exit $exitCode") -EvidenceDirectory $isolatedEvidence -Quiet } catch {
        if ($exitCode -ne 7 -or $_.Exception.Message -notmatch 'exit code 7') { throw }
        $failed = $true
    }
    if (($exitCode -eq 7) -ne $failed) { throw 'Temporary directory test lost nonzero exit behavior' }
    foreach ($name in 'TEMP','TMP','TMPDIR') {
        if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $parentTemp[$name]) { throw "Parent $name was not restored after exit $exitCode" }
    }
    $actualTemp = (Get-Content "$isolatedEvidence/temp-$exitCode.log" -Raw).Trim().TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($actualTemp -ne $expectedTemp) { throw "Child temporary directory was not isolated: expected $expectedTemp; got $actualTemp" }
}
'PASS child temporary directories are isolated and parent environment survives success and failure'
$absenceOriginal = @{}
foreach($name in 'TEMP','TMP','TMPDIR'){$absenceOriginal[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    foreach($missingName in 'TEMP','TMP','TMPDIR') {
        foreach($exitCode in 0,7) {
            Remove-Item -LiteralPath "Env:$missingName" -ErrorAction SilentlyContinue
            $failed=$false
            try { Invoke-EvidenceCommand -Name "absent-$missingName-$exitCode" -Executable $pwsh -Arguments @('-NoProfile','-Command',"exit $exitCode") -EvidenceDirectory $isolatedEvidence -Quiet } catch {
                if($exitCode -ne 7 -or $_.Exception.Message -notmatch 'exit code 7'){throw}
                $failed=$true
            }
            if(($exitCode -eq 7) -ne $failed){throw 'Absent variable case lost exit behavior'}
            if($null -ne [Environment]::GetEnvironmentVariable($missingName,'Process') -or (Test-Path -LiteralPath "Env:$missingName")){throw "Absent parent $missingName became defined after exit $exitCode"}
        }
        if($null -ne $absenceOriginal[$missingName]){[Environment]::SetEnvironmentVariable($missingName,$absenceOriginal[$missingName],'Process')}
    }
} finally {
    foreach($name in $absenceOriginal.Keys){if($null -eq $absenceOriginal[$name]){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}else{[Environment]::SetEnvironmentVariable($name,$absenceOriginal[$name],'Process')}}
}
'PASS originally absent temporary variables remain absent after success and failure'
$fixture = Join-Path $root 'fixture'
New-Item -ItemType Directory -Force "$fixture/core/Bim.Core", "$fixture/docs/contracts", "$fixture/.agents/skills/example" | Out-Null
'architecture' | Set-Content "$fixture/ARCHITECTURE.md"
'contracts' | Set-Content "$fixture/docs/contracts/module-contracts.md"
'[{"id":"fixture","status":"blocked","evidence":[],"blocker":"native acceptance not run"}]' | Set-Content "$fixture/docs/features.json"
"---`nname: example`ndescription: fixture`n---" | Set-Content "$fixture/.agents/skills/example/SKILL.md"
'namespace Bim.Core; public class Fixture {}' | Set-Content "$fixture/core/Bim.Core/Fixture.cs"
& git -C $fixture init --quiet
if ($LASTEXITCODE -ne 0) { throw 'fixture git init failed' }
& git -c core.autocrlf=false -C $fixture add .
if ($LASTEXITCODE -ne 0) { throw 'fixture git add failed' }
$checker = "$PSScriptRoot/../../scripts/harness-check.ps1"
Invoke-EvidenceCommand valid-fixture $pwsh @('-NoProfile','-File',$checker,'-Root',$fixture) $root
function Reject-Fixture([string]$name,[string]$path,[string]$content,[string]$expected) {
    $original = Get-Content $path -Raw
    try {
        $content | Set-Content $path
        $rejected = $false
        try { Invoke-EvidenceCommand $name $pwsh @('-NoProfile','-File',$checker,'-Root',$fixture) $root -Quiet } catch { $rejected = $true }
        if (!$rejected -or (Get-Content "$root/$name.log" -Raw) -notmatch $expected) { throw "Gate failed to reject $name" }
    } finally { $original | Set-Content $path }
}
foreach ($legacy in 'implemented','partial','pending') {
    Reject-Fixture "legacy-$legacy" "$fixture/docs/features.json" "[{`"id`":`"fixture`",`"status`":`"$legacy`",`"evidence`":[],`"blocker`":null}]" 'Invalid feature status'
}
foreach ($state in 'described','compiled','unit-tested','integration-tested (ifc)','verified-in-revit','unsupported','manual-only') {
    $ledger = "$fixture/docs/features.json"
    $original = Get-Content $ledger -Raw
    try {
        "[{`"id`":`"fixture`",`"status`":`"$state`",`"evidence`": [`"ARCHITECTURE.md`"],`"blocker`":null}]" | Set-Content $ledger
        Invoke-EvidenceCommand "allowed-$($state.Replace(' ','-'))" $pwsh @('-NoProfile','-File',$checker,'-Root',$fixture) $root -Quiet
    } finally { $original | Set-Content $ledger }
    Reject-Fixture "no-evidence-$($state.Replace(' ','-'))" "$fixture/docs/features.json" "[{`"id`":`"fixture`",`"status`":`"$state`",`"evidence`":[],`"blocker`":null}]" 'Claimed state without evidence'
}
Reject-Fixture blocked-no-reason "$fixture/docs/features.json" '[{"id":"fixture","status":"blocked","evidence":[],"blocker":null}]' 'Blocked without reason'
Reject-Fixture core-vendor "$fixture/core/Bim.Core/Fixture.cs" 'using Autodesk.Revit.DB;' 'Core dependency boundary'
Reject-Fixture false-status "$fixture/docs/features.json" '[{"id":"fixture","status":"verified","evidence":[],"blocker":null}]' 'Invalid feature status'
Reject-Fixture false-evidence "$fixture/docs/features.json" '[{"id":"fixture","status":"unit-tested","evidence":[],"blocker":null}]' 'Claimed state without evidence'
Reject-Fixture skill-metadata "$fixture/.agents/skills/example/SKILL.md" 'no frontmatter' 'Invalid skill frontmatter'
Reject-Fixture doc-links "$fixture/ARCHITECTURE.md" '[broken](missing.md)' 'Broken doc link'
'PASS static gates reject vendor coupling, false status/evidence, malformed skills and broken links'

function Reject-TrackedArtifact([string]$name,[string]$relativePath,[string]$expected) {
    $path = Join-Path $fixture $relativePath
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    'fixture artifact' | Set-Content $path
    & git -C $fixture add -f -- $relativePath
    if ($LASTEXITCODE -ne 0) { throw "Could not track artifact fixture $relativePath" }
    try {
        $rejected = $false
        try { Invoke-EvidenceCommand $name $pwsh @('-NoProfile','-File',$checker,'-Root',$fixture) $root -Quiet } catch { $rejected = $true }
        if (!$rejected -or (Get-Content "$root/$name.log" -Raw) -notmatch $expected) { throw "Gate failed to reject $name" }
    } finally {
        & git -C $fixture rm --cached --quiet -- $relativePath
        Remove-Item -LiteralPath $path -Force
    }
}
Reject-TrackedArtifact tracked-vendor-dll 'lib/AdWindows.dll' 'Prohibited tracked artifact'
Reject-TrackedArtifact tracked-model 'fixtures/BIM-Agentic-Test.rvt' 'Prohibited tracked artifact'
Reject-TrackedArtifact tracked-family 'fixtures/Generic.rfa' 'Prohibited tracked artifact'
Reject-TrackedArtifact tracked-sdk-tools '.tools/dotnet/dotnet.exe' 'Prohibited tracked artifact'
Reject-TrackedArtifact tracked-sdk-archive 'dotnet-sdk-9.0.100.zip' 'Prohibited tracked artifact'
'PASS static gate rejects tracked vendor binaries, BIM files and SDK payloads'

$originalGitCeiling = $env:GIT_CEILING_DIRECTORIES
$gitDirectoryMoved = $false
try {
    $env:GIT_CEILING_DIRECTORIES = $root
    Move-Item -LiteralPath "$fixture/.git" -Destination "$fixture/.git-disabled"
    $gitDirectoryMoved = $true
    $rejected = $false
    try { Invoke-EvidenceCommand no-git $pwsh @('-NoProfile','-File',$checker,'-Root',$fixture) $root -Quiet } catch { $rejected = $true }
    if (!$rejected -or (Get-Content "$root/no-git.log" -Raw) -notmatch 'Git inventory failed') { throw 'Static checks must fail closed when Git inventory fails' }
} finally {
    try {
        if ($gitDirectoryMoved) { Move-Item -LiteralPath "$fixture/.git-disabled" -Destination "$fixture/.git" }
    } finally { $env:GIT_CEILING_DIRECTORIES = $originalGitCeiling }
}
'PASS static checks fail closed without Git inventory'
'valid' | Set-Content "$fixture/bad.ps1"
Reject-Fixture syntax "$fixture/bad.ps1" 'if (' 'PowerShell syntax'
Reject-Fixture missing-evidence "$fixture/docs/features.json" '[{"id":"fixture","status":"unit-tested","evidence":["missing.log"],"blocker":null}]' 'Missing feature evidence'
Reject-Fixture escaping-evidence "$fixture/docs/features.json" '[{"id":"fixture","status":"unit-tested","evidence":["../outside.log"],"blocker":null}]' 'Evidence escapes repository'

