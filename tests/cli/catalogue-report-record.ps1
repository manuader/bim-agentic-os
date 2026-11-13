# P1-12 end-to-end input for catalogue-report: run records written by the REAL owner code, not by hand. For each
# scenario it builds <Root>/<scenario>/.bim/native-runs/<guid> (the fixture document; the copy the fake writes on
# document.save_copy and document.save_as), <Root>/<scenario>/.bim/revit-enabled-roots.txt (an owner folder for the
# save_as outside_copies_root refusal) and <Root>/<scenario>/.bim/native-sessions/<stamp>/{summary.json,catalogue-run1.json}:
#   - the sequence is scripts/owner-catalogue-steps.ps1 Invoke-CatalogueSequence against the in-process fake executor of
#     tests/integration/fake-revit-command-executor.ps1 and the doc-update stand-in of tests/integration/fake-doc-update.ps1
#     (the same harness as tests/integration/test-catalogue-session.ps1);
#   - the record is written by the record writer block of scripts/owner-native-session.ps1 itself (extracted from that
#     file and run with the variables the session sets), so a change of the record format reaches this test;
#   - summary.json has the session's shape (mode, catalogue_scope, runs[] with index, run_directory and catalogue).
#   - P4-18: the documentation update's doc-up-elements step runs the REAL doc-realized CLI verb (-Dotnet, -CliDll)
#     through the owner invoker Invoke-CatalogueDocUpdate (-> Invoke-CatalogueDocRealized, offline: no pipe) into the
#     run folder's catalogue-requests/NN-doc-up-elements.doc-realized/ (record, fixture, assignments, realized, report,
#     stderr), as the owner session lays it out; the doc-update calls stay on the stand-in, which reads that realized.json.
# Scenarios: clean (every group passes), mixed (a failing v2 command, a missing column type blocking two steps, a wrong
# copy sha256 read back), nocopy (document.save_copy fails, no copy), v1only (-CatalogueV1Only), realized (the request
# doc-realized gets assigns the door to the wall's step: the real verb refuses realized_unique_id_duplicate and the run
# stops). Writes only under -Root; never Revit. Called by tests/cli/CatalogueReportTests.cs.
param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Dotnet, [Parameter(Mandatory)][string]$CliDll)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path (Split-Path $PSScriptRoot)
. "$repo/scripts/owner-catalogue-steps.ps1"
. "$repo/tests/integration/fake-revit-command-executor.ps1"
. "$repo/tests/integration/fake-doc-update.ps1"
$sessionScript = Get-Content -LiteralPath "$repo/scripts/owner-native-session.ps1" -Raw
$found = [regex]::Match($sessionScript, '(?s)\$cataloguePath = Join-Path \$session "catalogue-run\$i\.json"\r?\n(?<writer>\s*\[ordered\]@\{.*?\| ConvertTo-Json -Depth 24 \| Set-Content -LiteralPath \$cataloguePath)')
if (!$found.Success) { throw 'the catalogue record writer of scripts/owner-native-session.ps1 was not found' }
$writer = [scriptblock]::Create($found.Groups['writer'].Value)

$stamp = '20261105T120000Z'
$documentUid = '0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9-0000abcd'
$scenarios = [ordered]@{
    clean = @{ Faults = @{}; V1Only = $false }
    mixed = @{ Faults = @{ NoStructuralTypes = @('column'); FailCommands = @('roof.create_footprint'); SaveCopyHashOff = $true }; V1Only = $false }
    nocopy = @{ Faults = @{ FailCommands = @('document.save_copy') }; V1Only = $false }
    v1only = @{ Faults = @{}; V1Only = $true }
    realized = @{ Faults = @{}; V1Only = $false; TamperRealized = $true }
}
foreach ($name in @($scenarios.Keys)) {
    $scenario = $scenarios[$name]
    $runDir = [IO.Path]::GetFullPath((Join-Path $Root "$name/.bim/native-runs/$([guid]::NewGuid())"))
    $session = [IO.Path]::GetFullPath((Join-Path $Root "$name/.bim/native-sessions/$stamp"))
    New-Item -ItemType Directory -Force $runDir, $session | Out-Null
    $documentPath = Join-Path $runDir 'BIM-Agentic-Test.rvt'
    # P1-14: the save_as outside_copies_root step needs an owner-enabled folder outside .bim/native-runs, listed in this
    # scenario's own .bim/revit-enabled-roots.txt (the folder itself is never created: the refusal comes first).
    [IO.File]::WriteAllText((Join-Path $Root "$name/.bim/revit-enabled-roots.txt"), [IO.Path]::GetFullPath((Join-Path $Root "$name/.bim/owner-enabled")) + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($documentPath, 'fixture')
    $fake = New-FakeRevitState $documentUid $scenario.Faults
    $fake.DocumentPath = $documentPath
    $invoke = { param($index, $key, $json) $text = Invoke-FakeRevitCommand $fake $json; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    $wire = { param($index, $key, $line) $text = Invoke-FakeRevitWire $fake $line; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    # P4-14: the documentation update's doc-update calls through the stand-in (plan files outside the run folder).
    $docFake = New-FakeDocUpdate $fake ([IO.Path]::GetFullPath((Join-Path $Root "$name/doc-update"))) $scenario.Faults
    # P4-18: the doc-realized call (a request with a record member) goes to the real CLI through the owner invoker, into
    # this run folder's catalogue-requests (unique variable names: the sequence calls this block from its own scope).
    $p418Requests = Join-Path $runDir 'catalogue-requests'
    $p418Tamper = [bool]$scenario['TamperRealized']
    $docUpdate = {
        param($index, $key, $json)
        if ($null -eq (Get-CatalogueMember ($json | ConvertFrom-Json) 'record')) { return (Invoke-FakeDocUpdate $docFake $index $key $json) }
        if ($p418Tamper) {
            $tampered = $json | ConvertFrom-Json
            $tampered.assignments.('element:' + $script:CatalogueDocUpdateDoorId) = 'doc-up-wall'
            $json = $tampered | ConvertTo-Json -Depth 64 -Compress
        }
        Invoke-CatalogueDocUpdate -Dotnet $Dotnet -CliDll $CliDll -PipeName 'bim-agentic-os-unused' -RequestDirectory $p418Requests -Index $index -Key $key -Json $json -TimeoutSeconds 120
    }
    $sequence = Invoke-CatalogueSequence -Stamp $stamp -Run 1 -DocumentPath $documentPath -DocumentUid $documentUid -Invoke $invoke -InvokeWire $wire -InvokeDocUpdate $docUpdate -V1Only:$scenario.V1Only
    # The variables the session's writer reads, set as scripts/owner-native-session.ps1 sets them for run 1.
    $i = 1
    $summary = [ordered]@{ schema_version = 1; mode = 'catalogue'; catalogue_scope = $(if ($scenario.V1Only) { 'v1_only' } else { 'one_run_all_groups' }); started_utc = '2026-11-05T12:00:00.0000000Z'; session = $session; head = 'e2e0001'; protected_revit_pids = @(); source_check = 'skipped'; builds = [ordered]@{}; runs = @(); finished_utc = $null; outcome = 'incomplete' }
    $child = [pscustomobject]@{ Id = 4242 }
    $boot = [pscustomobject]@{ pipeName = 'bim-agentic-os-2025-4242'; documentPath = $documentPath; documentUniqueId = $documentUid; revitBuild = '25.4.0.32' }
    $cataloguePath = Join-Path $session "catalogue-run$i.json"
    . $writer
    $summary.runs += [ordered]@{ index = $i; run_directory = $runDir; child_pid = $child.Id; bootstrap = 'succeeded'; outcome = (Format-CatalogueSummary $sequence)
        catalogue = [ordered]@{ file = $cataloguePath; counts = $sequence.counts; groups = $sequence.groups; overall = $sequence.overall; all_passed = $sequence.all_passed; aborted = $sequence.aborted } }
    $summary.finished_utc = '2026-11-05T12:30:00.0000000Z'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $session 'summary.json')
    Write-Output "$name $runDir"
}
