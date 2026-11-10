# C04 headless checks for scripts/owner-catalogue-steps.ps1 (plain assertions, no Pester).
# Always: envelopes, operation ids, reference picking and the 17-step v1 sequence (-V1Only) against an in-process fake
# executor, including fault cases; then the P1-06 one-run sequence (v1 first and unchanged, then every later group):
# all pass -> per-group counts and summary line; one failing group -> later groups still run and the outcome names it;
# a lost pipe -> nothing more is sent. P1-11 (formas, extrusion, transformar, copia, before the destructive group):
# every step checked against the Core contracts read from core/Bim.Core (catalogue command, refusal code, client-side
# code, save_copy reason, ReuseOf), exact arguments and outcomes, ordering and dependencies, the document path switch to
# the copy, no path outside .bim (no copy of a non-test document) and fault cases. P1-14 (copia, after save_copy):
# document.save_as of the open copy into .bim/native-runs with the hash and post-save path readback, the repeat refused
# target_exists, a target in an owner-enabled folder refused outside_copies_root (the test lists its own folder in
# .bim/revit-enabled-roots.txt when that file is absent, and removes it afterwards), the switch to the new file and
# fault cases. P4-11 (end of documentacion): the two-pass documentation update of a moved test wall (P4-09 on the
# test copy: dry-run probes, replan, confirm dry runs, real deletes with the planned confirm_dependents, re-created
# section, tag and dimension read back against the moved wall), its delete order and requests, and the P4-09 refusals
# (dependent_kept, dependent_unknown, dependent_planned, dependents_changed, a sha256 mismatch) sending no real delete.
# Runs in CI through
# scripts/verify.ps1 (catalogue-session, and catalogue-session-cli with the real CLI); never Revit.
# With -Dotnet and -CliDll: the whole one-run sequence through the real CLI
# "revit-command" verb (and Invoke-CatalogueWire for journal.read) against a fake add-in pipe, proving every envelope
# passes the Core schema and guards and that the invalid arc never reaches the pipe, then a dead pipe stopping the run.
# Revit itself is never involved; native behaviour stays unverified until the owner session runs.
param([string]$Dotnet, [string]$CliDll)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
. "$root/scripts/owner-catalogue-steps.ps1"
. "$PSScriptRoot/fake-revit-command-executor.ps1"
$failures = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { $failures.Add($Message) } }

$stamp = '20261030T120000Z'
$documentUid = '0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9-0000abcd'
# The in-process run uses its own temporary tree <temp>/bim-catalogue-session-<guid>/.bim (native-runs/<run> with the
# test document; its own revit-enabled-roots.txt listing an existing owner folder): it never reads or writes the
# repository's .bim/revit-enabled-roots.txt. The fake writes the save_copy and save_as files next to the test document.
$inProcessTree = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('bim-catalogue-session-' + [guid]::NewGuid())))
$inProcessBim = [IO.Path]::Combine($inProcessTree, '.bim')
$inProcessDirectory = [IO.Path]::Combine($inProcessBim, 'native-runs', 'catalogue-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Force $inProcessDirectory | Out-Null
$documentPath = Join-Path $inProcessDirectory 'BIM-Agentic-Test.rvt'
# The test document exists on disk like the owner's copy (c-as-save checks the previous file is still there).
[IO.File]::WriteAllBytes($documentPath, [byte[]]@())
$inProcessOwnerRoot = [IO.Path]::Combine($inProcessBim, 'owner-enabled')
New-Item -ItemType Directory -Force $inProcessOwnerRoot | Out-Null
[IO.File]::WriteAllText([IO.Path]::Combine($inProcessBim, 'revit-enabled-roots.txt'), "# in-process test owner file`n$inProcessOwnerRoot`n", [Text.UTF8Encoding]::new($false))

# The real-CLI pass reads the repository's owner file (the CLI does). When it is absent the test creates it with
# CreateNew (never overwriting one that appears meanwhile) listing <repo>/.bim/catalogue-owner-root-<guid> (never
# created: the refusal comes first), and removes it only if it still holds exactly what the test wrote. An existing
# owner file is never touched; if it lists no usable owner folder, c-as-outside is expected blocked, not failed.
$enabledRootsFile = Join-Path $root '.bim/revit-enabled-roots.txt'
$testOwnerRoot = [IO.Path]::GetFullPath((Join-Path $root ('.bim/catalogue-owner-root-' + [guid]::NewGuid())))
$testOwnerText = "# written by tests/integration/test-catalogue-session.ps1 (removed when it ends)`n$testOwnerRoot`n"
function Enter-TestOwnerRoot([string]$File, [string]$Text) {
    try { $stream = [IO.File]::Open($File, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) } catch { return $false }
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    return $true
}
function Exit-TestOwnerRoot([bool]$Created, [string]$File, [string]$Text) {
    if (!$Created) { return }
    try { if ([IO.File]::ReadAllText($File, [Text.UTF8Encoding]::new($false)) -ceq $Text) { Remove-Item -LiteralPath $File -Force } } catch { $null = $_ }
}
# CreateNew never overwrites and the cleanup leaves a file whose content changed.
$probeFile = [IO.Path]::Combine($inProcessBim, 'probe-owner.txt')
$probeFirst = Enter-TestOwnerRoot $probeFile 'one'
$probeSecond = Enter-TestOwnerRoot $probeFile 'two'
Assert ($probeFirst -and !$probeSecond -and [IO.File]::ReadAllText($probeFile) -eq 'one') 'Enter-TestOwnerRoot creates the file once and never overwrites it'
[IO.File]::WriteAllText($probeFile, 'changed by someone else')
Exit-TestOwnerRoot $true $probeFile 'one'
Assert (Test-Path -LiteralPath $probeFile) 'Exit-TestOwnerRoot keeps a file whose content changed'
Exit-TestOwnerRoot $true $probeFile 'changed by someone else'
Assert (!(Test-Path -LiteralPath $probeFile)) 'Exit-TestOwnerRoot removes the file it wrote'
# The in-process checks run inside try; finally removes that folder even when a check throws.
try {

# --- Envelope and operation id ---
$id = New-CatalogueOperationId $stamp 2 'wall-arc'
Assert ($id -eq 'c04-20261030T120000Z-r2-wall-arc') "operation id format: $id"
$rejected = $false; try { New-CatalogueOperationId $stamp 1 'bad key' | Out-Null } catch { $rejected = $true }
Assert $rejected 'operation id with a space accepted'
$envelope = New-CatalogueEnvelope $id $documentPath $documentUid 'query.levels' $null | ConvertFrom-Json
Assert ($envelope.protocol_version -eq 1 -and $envelope.command -eq 'query.levels' -and $envelope.document.unique_id -eq $documentUid) 'envelope members'
Assert (@($envelope.args.PSObject.Properties).Count -eq 0) 'query args must be {}'
$floor = New-CatalogueEnvelope 'x1' $documentPath $documentUid 'floor.create' ([ordered]@{ boundary = @($script:CatalogueRectangle | ForEach-Object { , @($_[0], $_[1]) }) }) | ConvertFrom-Json
Assert (@($floor.args.boundary).Count -eq 4 -and @($floor.args.boundary[2]).Count -eq 2 -and $floor.args.boundary[2][0] -eq 6000 -and $floor.args.boundary[2][1] -eq 4000) 'floor boundary must be four [x, y] pairs'

# --- Picking ---
$wallTypes = '{"readback":{"wall_types":[{"name":"Curtain","unique_id":"c","kind":"Curtain"},{"name":"Stack","unique_id":"s","kind":"Stacked"},{"name":"Generic","unique_id":"b","kind":"Basic"},{"name":"Generic 2","unique_id":"b2","kind":"Basic"}]}}' | ConvertFrom-Json
Assert ((Select-CatalogueWallType $wallTypes) -eq 'b') 'first Basic wall type'
$doors = '{"readback":{"category":"door","family_symbols":[{"unique_id":"f","wall_hosted":false},{"unique_id":"h","wall_hosted":true}]}}' | ConvertFrom-Json
Assert ((Select-CatalogueHostedSymbol $doors 'door') -eq 'h') 'first wall-hosted symbol'
$rejected = $false; try { Select-CatalogueHostedSymbol $doors 'window' | Out-Null } catch { $rejected = $true }
Assert $rejected 'symbol query for another category accepted'
$floors = '{"readback":{"floor_types":[{"name":"Foundation Slab 300","unique_id":"x"},{"name":"Generic 150mm","unique_id":"g"}]}}' | ConvertFrom-Json
Assert ((Select-CatalogueFloorType $floors) -eq 'g') 'foundation floor type picked'
$rejected = $false; try { Select-CatalogueLevel ('{"readback":{"levels":[]}}' | ConvertFrom-Json) | Out-Null } catch { $rejected = $true }
Assert $rejected 'empty level list accepted'

# --- Sequence against the in-process fake ---
# -All runs every group (the owner one-run sequence); default is v1 only. -NoWire leaves out the journal.read invoker.
# -Wrap / -WireWrap (param index, key, json, inner) can stand in front of the fake CLI / wire invoker.
function Invoke-FakeSequence([hashtable]$Faults = @{}, [switch]$All, [switch]$NoWire, [scriptblock]$Wrap, [scriptblock]$WireWrap) {
    $fake = New-FakeRevitState $documentUid $Faults
    $fake.DocumentPath = $documentPath
    $invoke = { param($index, $key, $json) $text = Invoke-FakeRevitCommand $fake $json; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    $wire = { param($index, $key, $line) $text = Invoke-FakeRevitWire $fake $line; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    if ($Wrap) { $inner = $invoke; $invoke = { param($index, $key, $json) & $Wrap $index $key $json $inner } }
    if ($WireWrap) { $innerWire = $wire; $wire = { param($index, $key, $line) & $WireWrap $index $key $line $innerWire } }
    $parameters = @{ Stamp = $stamp; Run = 1; DocumentPath = $documentPath; DocumentUid = $documentUid; Invoke = $invoke; V1Only = !$All }
    if (!$NoWire) { $parameters['InvokeWire'] = $wire }
    $result = Invoke-CatalogueSequence @parameters
    return @{ Result = $result; Fake = $fake }
}
$clean = Invoke-FakeSequence
$counts = $clean.Result.counts
Assert ($counts.total -eq 17 -and $counts.passed -eq 17) ("clean fake run: " + (($clean.Result.steps | Where-Object { !$_.passed } | ForEach-Object { "$($_.key): $($_.reason)" }) -join '; '))
Assert ((Format-CatalogueResult $counts) -eq 'Catálogo: 17/17 pasos OK') "result line: $(Format-CatalogueResult $counts)"
$steps = $clean.Result.steps
$operationIds = @($steps | ForEach-Object { $_.operation_id })
Assert (@($operationIds | Select-Object -Unique).Count -eq 15) 'operation ids: 15 distinct (retry and conflict reuse the curved wall id)'
Assert (@($operationIds | Where-Object { $_ -notlike "c04-$stamp-r1-*" }).Count -eq 0) 'operation ids must carry the session stamp and run'
$arc = $steps | Where-Object { $_.key -eq 'wall-arc' }
$retry = $steps | Where-Object { $_.key -eq 'wall-arc-retry' }
Assert ($retry.operation_id -eq $arc.operation_id -and $retry.response.receipt.replayed -eq $true -and $retry.response.created[0] -eq $arc.response.created[0]) 'retry must replay the same element'
Assert (($steps | Where-Object { $_.key -eq 'wall-arc-conflict' }).request.args.curve.radius_mm -eq 3500) 'conflict request must change the radius'
$walls = @($steps | Where-Object { $_.key -like 'wall-*' -and $_.key -notlike 'wall-arc*' })
Assert ($walls.Count -eq 4) 'four straight walls'
for ($i = 0; $i -lt 4; $i++) {
    $next = $walls[($i + 1) % 4].request.args.curve
    $current = $walls[$i].request.args.curve
    Assert ($current.end[0] -eq $next.start[0] -and $current.end[1] -eq $next.start[1]) "rectangle not closed at wall $($i + 1)"
}
$levelUid = $clean.Result.picked.level
Assert (@($steps | Where-Object { $_.command -ne 'query.levels' -and $_.request -and $_.request.args.PSObject.Properties['level'] -and $_.request.args.level -ne $levelUid }).Count -eq 0) 'every write must use the picked level'
$door = $steps | Where-Object { $_.key -eq 'door' }
$window = $steps | Where-Object { $_.key -eq 'window' }
Assert ($door.request.args.host_wall_uid -eq $clean.Result.picked['wall-south'] -and !$door.request.args.PSObject.Properties['sill_mm']) 'door host and no sill'
Assert ($window.request.args.sill_mm -eq 900 -and $window.request.args.host_wall_uid -eq $clean.Result.picked['wall-east']) 'window host and sill'
Assert (($steps | Where-Object { $_.key -eq 'room' }).request.args.name -eq 'Prueba') 'room name'
Assert (@($steps | Where-Object { $null -eq $_.elapsed_ms }).Count -eq 0) 'every sent step records elapsed ms'

# Fault cases: the checks must notice them.
$noFlag = (Invoke-FakeSequence @{ ReplayNotFlagged = $true }).Result
Assert (!($noFlag.steps | Where-Object { $_.key -eq 'wall-arc-retry' }).passed -and $noFlag.counts.passed -eq 16) 'replay without replayed=true must fail'
$duplicate = (Invoke-FakeSequence @{ ReplayNewElement = $true }).Result
Assert (!($duplicate.steps | Where-Object { $_.key -eq 'wall-arc-retry' }).passed) 'replay with a new element must fail'
$radius = (Invoke-FakeSequence @{ WrongRadius = $true }).Result
Assert (!($radius.steps | Where-Object { $_.key -eq 'wall-arc' }).passed -and $radius.counts.passed -lt 17) 'wrong readback radius must fail'
# Missing dependency: no levels means nothing that needs a level is sent.
$noLevels = { param($index, $key, $json) if ($key -eq 'query-levels') { @{ output = '{"status":"succeeded","code":"ok","field":null,"detail":null,"created":[],"modified":[],"deleted":[],"readback":{"levels":[]},"receipt":null,"warnings":[]}'; exit_code = 0; timed_out = $false } } else { @{ output = 'not json'; exit_code = 1; timed_out = $false } } }
$blocked = Invoke-CatalogueSequence -Stamp $stamp -Run 1 -DocumentPath $documentPath -DocumentUid $documentUid -Invoke $noLevels -V1Only
Assert ($blocked.counts.passed -eq 0 -and ($blocked.steps | Where-Object { $_.key -eq 'wall-south' }).request -eq $null) 'steps without a level must not be sent'
# Timeout: completion unknown, nothing after it is sent.
$timeout = { param($index, $key, $json) if ($index -eq 6) { @{ output = ''; exit_code = $null; timed_out = $true } } else { $text = Invoke-FakeRevitCommand $fakeForTimeout $json; @{ output = $text; exit_code = 0; timed_out = $false } } }
$fakeForTimeout = New-FakeRevitState $documentUid
$timedOut = Invoke-CatalogueSequence -Stamp $stamp -Run 1 -DocumentPath $documentPath -DocumentUid $documentUid -Invoke $timeout -V1Only
Assert ($timedOut.counts.passed -eq 5 -and $timedOut.counts.not_sent -eq 11) "timeout must stop the sequence: $($timedOut.counts | ConvertTo-Json -Compress)"
# Evidence must serialize.
$evidence = [ordered]@{ steps = $clean.Result.steps; counts = $clean.Result.counts } | ConvertTo-Json -Depth 20
Assert (($evidence | ConvertFrom-Json).steps.Count -eq 17) 'evidence JSON round trip'
Assert ($clean.Result.groups.Count -eq 1 -and (Format-CatalogueSummary $clean.Result) -eq 'Catálogo: 17/17 pasos OK') "v1-only summary line: $(Format-CatalogueSummary $clean.Result)"

# --- P1-06 one-run sequence: v1 first and unchanged, then every later group ---
$groupKeys = @('v1', 'v2', 'vistas', 'snapshot', 'consultas', 'estructura', 'documentacion', 'diario', 'formas', 'extrusion', 'transformar', 'copia', 'modificar')
# P4-11: the two-pass documentation update at the end of the documentation group; its element.delete steps (dry runs,
# confirm dry runs and the real deletes) touch only the stale dimension, tag and section that group made for it.
$docUpOrder = @('dimension', 'tag', 'section')
$docUpDeleteKeys = @(foreach ($pass in 'probe', 'confirm', 'delete') { foreach ($name in $docUpOrder) { "doc-up-$pass-$name" } })
function Get-FailedSteps($Result) { (@($Result.steps | Where-Object { !$_.passed } | ForEach-Object { "[$($_.group)] $($_.key): $($_.reason)" }) -join '; ') }
function Get-Group($Result, [string]$Key) { @($Result.groups | Where-Object { $_.key -eq $Key })[0] }
$full = (Invoke-FakeSequence -All).Result
Assert ((@($full.groups | ForEach-Object { $_.key }) -join ',') -eq ($groupKeys -join ',')) "group order: $(@($full.groups | ForEach-Object { $_.key }) -join ',')"
Assert ($full.all_passed -and $full.overall.failed -eq 0 -and $null -eq $full.aborted) "clean one-run fake: $(Get-FailedSteps $full)"
Assert ($full.counts.total -eq 17 -and $full.counts.passed -eq 17) "v1 counts keep their meaning in a one-run: $($full.counts | ConvertTo-Json -Compress)"
foreach ($group in @($full.groups)) { Assert ($group.total -gt 0 -and $group.passed -eq $group.total -and $group.not_sent -eq 0) "group $($group.key): $($group.passed)/$($group.total), not sent $($group.not_sent)" }
Assert ($full.overall.total -eq (@($full.groups | ForEach-Object { $_.total }) | Measure-Object -Sum).Sum -and $full.overall.total -eq @($full.steps).Count) 'overall total is the sum of the groups'
$v1Keys = (@($clean.Result.steps | ForEach-Object { $_.key }) -join ',')
Assert ((@($full.steps | Select-Object -First 17 | ForEach-Object { $_.key }) -join ',') -eq $v1Keys -and @($full.steps | Select-Object -First 17 | Where-Object { $_.group -ne 'v1' }).Count -eq 0) 'the 17 v1 steps come first, unchanged'
Assert ((@($full.steps | Select-Object -First 17 | ForEach-Object { $_.operation_id }) -join ',') -eq (@($clean.Result.steps | ForEach-Object { $_.operation_id }) -join ',')) 'v1 operation ids unchanged in a one-run'
$summary = Format-CatalogueSummary $full
$expectedGroups = (@($full.groups | Where-Object { $_.key -ne 'v1' } | ForEach-Object { "$($_.key) $($_.total)/$($_.total)" }) -join ', ')
Assert ($summary -eq "Catálogo: 17/17 pasos OK; grupos: $expectedGroups; total $($full.overall.total)/$($full.overall.total); resultado: TODO OK") "one-run summary line: $summary"
$destructive = @($full.steps | Where-Object { $_.command -eq 'element.delete' -or $_.command -eq 'element.set_parameter' })
Assert ($destructive.Count -ge 5 -and @($destructive | Where-Object { $_.group -ne 'modificar' -and !($_.group -eq 'documentacion' -and $_.key -in $docUpDeleteKeys) }).Count -eq 0) 'modify/delete steps only in the last group (and the P4-11 documentation update deletes of its own stale items)'
$lastWrite = @($full.steps | Where-Object { $_.group -ne 'modificar' } | Select-Object -Last 1)[0]
Assert ($lastWrite.index -lt @($full.steps | Where-Object { $_.group -eq 'modificar' } | Select-Object -First 1)[0].index) 'the destructive group runs last'
# P1-13: the fake add-in issues query.elements cursors exactly as core RevitElementQueryCursor (pinned vector shared
# with tests/revit-commands/ElementQueryCursor.cs), and q-walls-next follows the next_cursor of q-walls-limit.
$vector = 'eyJ2IjoxLCJkb2N1bWVudCI6IjYwZjkxZGFmLTNkZDctNDI4My1hODZkLTI0MTM3YjczZjNkYS0wMDAxZmQwYiIsImZpbHRlciI6IjBjYjIxNzZkNmNlODg4ZjA1OGMxNmY0MzNkNTNkNjIyMmM0M2JiNjU4OTA4NjljYTZlNWZiNzFiYTAwOWE0MzYiLCJhZnRlciI6NDA5NywiY2hlY2siOiI5MTcwMjNkMDlkYWZlYjEzMzBkMTRjMjM5OGMzYzg2OSJ9'
Assert ((New-FakeCursor '60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b' (Get-FakeFilterDigest 'OST_Walls' $null @()) 4097) -eq $vector -and (Get-FakeCursorAfter $vector) -eq 4097) 'the fake add-in cursor equals the core cursor vector'
$limitPage = @($full.steps | Where-Object { $_.key -eq 'q-walls-limit' })[0]
$nextPage = @($full.steps | Where-Object { $_.key -eq 'q-walls-next' })[0]
Assert ($null -ne $nextPage -and $nextPage.passed -and $null -ne $limitPage.response.readback.next_cursor -and [string]$nextPage.request.args.cursor -eq [string]$limitPage.response.readback.next_cursor -and $null -eq $nextPage.response.readback.next_cursor) "q-walls-next follows the next_cursor of q-walls-limit to the last page: $(if ($nextPage) { $nextPage.reason })"
$keys = @($full.steps | ForEach-Object { $_.key })
Assert (@($keys | Select-Object -Unique).Count -eq $keys.Count) 'step keys are unique'
foreach ($command in @('roof.create_footprint', 'stair.create', 'curtain_wall.create', 'floor.create_opening', 'view.create_section', 'view.create_3d', 'query.snapshot', 'query.elements', 'query.inspect', 'query.structural_types', 'grid.create', 'structural_column.create', 'structural_beam.create', 'foundation.create_isolated', 'foundation.create_wall', 'query.title_blocks', 'dimension.create_linear', 'tag.create', 'schedule.create', 'sheet.create', 'sheet.place_view', 'journal.read', 'element.set_parameter', 'element.delete')) {
    Assert (@($full.steps | Where-Object { $_.command -eq $command -and $_.passed }).Count -ge 1) "one-run covers $command"
}
$roof = @($full.steps | Where-Object { $_.key -eq 'v2-roof' })[0]
Assert ($roof.comparisons.Count -ge 5 -and @($roof.comparisons | Where-Object { !$_.ok }).Count -eq 0 -and @($roof.comparisons | Where-Object { $null -eq $_.requested }).Count -eq 0) 'steps record requested vs observed rows'
$journal = @($full.steps | Where-Object { $_.command -eq 'journal.read' })
Assert ($journal.Count -eq 5 -and @($journal | Where-Object { $_.request.Operation -ne 'journal.read' -or $null -ne $_.operation_id }).Count -eq 0) 'journal.read goes as a wire line, not a catalogue envelope'
$peeks = @($full.steps | Where-Object { $_.key -like 'j-peek-*' })
Assert ($peeks.Count -eq 2 -and $peeks[1].request.Journal.peek -eq $true) 'peek option on the wire line'
$deleteDry = @($full.steps | Where-Object { $_.key -eq 'm-delete-dry' })[0]
$deleteReal = @($full.steps | Where-Object { $_.key -eq 'm-delete' })[0]
Assert ((@($deleteReal.request.args.confirm_dependents) -join ',') -eq (@($deleteDry.response.readback.delete.dependents) -join ',') -and @($deleteDry.response.readback.delete.dependents).Count -ge 1) 'the real delete confirms exactly the dry-run dependents'
Assert (@($full.steps | Where-Object { $_.key -eq 'm-height-dry' })[0].request.args.dry_run -eq $true) 'dry run flag on the wire'
$fullEvidence = [ordered]@{ steps = $full.steps; counts = $full.counts; groups = $full.groups; overall = $full.overall } | ConvertTo-Json -Depth 24
Assert (@(($fullEvidence | ConvertFrom-Json).steps).Count -eq $full.overall.total) 'one-run evidence JSON round trip'

# One failing group: the later groups still run; the outcome names the failed group.
$roofFails = (Invoke-FakeSequence @{ FailCommands = @('roof.create_footprint') } -All).Result
Assert (!$roofFails.all_passed -and (Get-Group $roofFails 'v2').failed -eq 1 -and $roofFails.counts.passed -eq 17) "failing roof: $(Get-FailedSteps $roofFails)"
foreach ($key in @($groupKeys | Select-Object -Skip 2)) { Assert ((Get-Group $roofFails $key).failed -eq 0 -and (Get-Group $roofFails $key).not_sent -eq 0) "group $key must still run after a v2 failure: $(Get-FailedSteps $roofFails)" }
Assert ((Format-CatalogueSummary $roofFails) -like '*resultado: FALLÓ en v2') "failure summary: $(Format-CatalogueSummary $roofFails)"
Assert (@($roofFails.steps | Where-Object { $_.key -eq 'v2-roof' })[0].reason -like '*transaction_failed*') 'failure reason carries the code'
$sectionFails = (Invoke-FakeSequence @{ FailCommands = @('view.create_section') } -All).Result
# P4-11: the documentation update makes its own section, so its section steps (and what needs them) fail there too.
$sectionOthers = @($sectionFails.steps | Where-Object { !$_.passed -and $_.key -ne 'views-section' -and $_.key -notlike 'doc-up-*' })
Assert ((Get-Group $sectionFails 'vistas').failed -eq 1 -and $sectionOthers.Count -eq 0 -and !@($sectionFails.steps | Where-Object { $_.key -eq 'doc-up-section' })[0].passed -and (Format-CatalogueSummary $sectionFails) -like '*resultado: FALLÓ en vistas, documentacion') "failing section: $(Get-FailedSteps $sectionFails)"
$slope = (Invoke-FakeSequence @{ RoofSlopeOff = $true } -All).Result
$slopeReason = [string]@($slope.steps | Where-Object { $_.key -eq 'v2-roof' })[0].reason
Assert ($slope.overall.failed -eq 1 -and $slopeReason -like '*se pidió 4*se observó 0*') "readback mismatch reports requested vs observed: $slopeReason"
$unstable = (Invoke-FakeSequence @{ SnapshotUnstable = $true } -All).Result
Assert ((Get-Group $unstable 'snapshot').failed -eq 1 -and !@($unstable.steps | Where-Object { $_.key -eq 'snap-2' })[0].passed -and $unstable.overall.failed -eq 1) "snapshots that differ must fail snap-2 only: $(Get-FailedSteps $unstable)"
$noWire = (Invoke-FakeSequence -All -NoWire).Result
Assert ((Get-Group $noWire 'diario').failed -eq 5 -and (Get-Group $noWire 'modificar').failed -eq 0 -and $null -eq $noWire.aborted) "without a wire invoker only the journal reads fail: $(Get-FailedSteps $noWire)"
Assert ((Format-CatalogueSummary $noWire) -like '*resultado: FALLÓ en diario') "no-wire summary: $(Format-CatalogueSummary $noWire)"

# A lost pipe (the CLI answers pipe_connect_timeout) stops the run: nothing after that step is sent, in any group.
$deadLine = '{"status":"failed","code":"pipe_connect_timeout","field":"pipe_name","detail":"no add-in listening on this process pipe","created":[],"modified":[],"deleted":[],"readback":null,"receipt":null,"warnings":[]}'
$deadState = @{ On = $false }
$deadWrap = { param($index, $key, $json, $inner) if ($key -eq 'snap-1') { $deadState.On = $true }; if ($deadState.On) { return @{ output = $deadLine; exit_code = 1; timed_out = $false } }; & $inner $index $key $json }
$dead = (Invoke-FakeSequence -All -Wrap $deadWrap).Result
$deadStep = @($dead.steps | Where-Object { $_.key -eq 'snap-1' })[0]
$after = @($dead.steps | Where-Object { $_.index -gt $deadStep.index })
Assert ($null -ne $dead.aborted -and !$deadStep.passed -and $deadStep.reason -like '*pipe_connect_timeout*') "lost pipe must stop the run: $($dead.aborted)"
Assert ($after.Count -gt 0 -and @($after | Where-Object { $null -ne $_.request }).Count -eq 0) 'no step after a lost pipe is sent (journal wire steps included)'
Assert ((Get-Group $dead 'v1').passed -eq 17 -and (Get-Group $dead 'v2').failed -eq 0 -and (Get-Group $dead 'vistas').failed -eq 0) 'groups before the lost pipe keep their results'
Assert ((Format-CatalogueSummary $dead) -like '*DETENIDO*pipe_connect_timeout*') "lost-pipe summary: $(Format-CatalogueSummary $dead)"
# Review follow-ups: both wall faces, section crop/origin, locale-free parameters, refusal field and detail.
$faces = @($full.steps | Where-Object { $_.key -like 'doc-dim-walls-*' })
Assert ($faces.Count -eq 2 -and @($faces | Where-Object { $_.passed }).Count -eq 2 -and $faces[0].request.args.wall_face -eq 'interior' -and $faces[1].request.args.wall_face -eq 'exterior') 'wall dimensions on both faces'
Assert (@($faces[1].comparisons | Where-Object { $_.check -like '*2 x espesor*' -and $_.ok -and $_.requested -eq 400 }).Count -eq 1) 'exterior and interior differ by twice the wall width'
$section = @($full.steps | Where-Object { $_.key -eq 'views-section' })[0]
Assert (@($section.comparisons | Where-Object { $_.check -like 'recorte:*' -or $_.check -like 'origen:*' }).Count -eq 3) 'section checks crop x/z spans and origin y'
Assert (@($full.steps | Where-Object { $_.key -eq 'm-comments' })[0].request.args.parameter.built_in -eq 'ALL_MODEL_INSTANCE_COMMENTS') 'Comments set by built-in id'
Assert (@(@($full.steps | Where-Object { $_.key -eq 'q-params' })[0].request.args.parameters) -notcontains 'Comments') 'no localized parameter name in q-params'
foreach ($key in @('m-readonly', 'm-delete-unconfirmed')) { $refusal = @($full.steps | Where-Object { $_.key -eq $key })[0]; Assert ($refusal.passed -and @($refusal.comparisons | Where-Object { $_.ok }).Count -eq 2) "$key checks field and detail" }
$wrongField = (Invoke-FakeSequence -All -Wrap { param($index, $key, $json, $inner) $reply = & $inner $index $key $json; if ($key -eq 'm-readonly') { $reply.output = $reply.output.Replace('"field":"args.parameter"', '"field":"args.value"') }; $reply }).Result
Assert ((Get-Group $wrongField 'modificar').failed -eq 1 -and [string]@($wrongField.steps | Where-Object { $_.key -eq 'm-readonly' })[0].reason -like '*campo rechazado*args.value*') "a refusal on another field must fail: $(Get-FailedSteps $wrongField)"
$scheduleFails = (Invoke-FakeSequence @{ FailCommands = @('schedule.create') } -All).Result
Assert ([string]@($scheduleFails.steps | Where-Object { $_.key -eq 'doc-schedule' })[0].reason -like '*transaction_failed*interfaz en inglés*') 'schedule failure names the English UI precondition'
Assert (@($full.steps | Where-Object { $_.key -eq 'j-baseline' })[0].passed -and @(@($full.steps | Where-Object { $_.key -eq 'j-baseline' })[0].comparisons | Where-Object { $_.check -like '*baseline_existed*' }).Count -eq 1) 'first journal read has no baseline'
$conflictFake = New-FakeRevitState $documentUid
$conflictFake.DocumentPath = $documentPath
Assert ((Invoke-FakeRevitWire $conflictFake (New-CatalogueWireLine '/other/BIM-Agentic-Test.rvt' 'journal.read' $null)) -like '*document_conflict*') 'fake journal.read refuses another document path'

# Missing fixture preconditions are blocked, not failed.
$noColumns = (Invoke-FakeSequence @{ NoStructuralTypes = @('column') } -All).Result
$structure = Get-Group $noColumns 'estructura'
Assert ($structure.blocked -eq 2 -and $structure.failed -eq 0 -and $noColumns.overall.failed -eq 0 -and !$noColumns.all_passed) "missing column type blocks 2 steps: $(Get-FailedSteps $noColumns)"
Assert ((@($noColumns.steps | Where-Object { $_.blocked } | ForEach-Object { $_.key }) -join ',') -eq 's-types-column,s-column') "blocked steps: $(@($noColumns.steps | Where-Object { $_.blocked } | ForEach-Object { $_.key }) -join ',')"
Assert ([string]@($noColumns.steps | Where-Object { $_.key -eq 's-column' })[0].reason -like '*bloqueado*column*') 'a blocked dependent names the missing precondition'
$noColumnsLine = Format-CatalogueSummary $noColumns
Assert ($noColumnsLine -like '*estructura 10/12 (2 bloqueados)*' -and $noColumnsLine -like '*resultado: OK con bloqueados: estructura (2)' -and $noColumnsLine -notlike '*FALLÓ*' -and $noColumnsLine -notlike '*TODO OK*') "blocked-only summary: $noColumnsLine"
$oneLevel = (Invoke-FakeSequence @{ SingleLevel = $true } -All).Result
Assert ($oneLevel.overall.failed -eq 0 -and (Get-Group $oneLevel 'v2').blocked -eq 3 -and (Get-Group $oneLevel 'estructura').blocked -eq 4 -and $oneLevel.counts.passed -eq 17) "one level blocks the roof, stair, column, beam and footing: $(Get-FailedSteps $oneLevel)"
$mixed = (Invoke-FakeSequence @{ NoStructuralTypes = @('column'); FailCommands = @('roof.create_footprint') } -All).Result
Assert ((Format-CatalogueSummary $mixed) -like '*resultado: FALLÓ en v2; bloqueados en estructura (2)') "failed and blocked summary: $(Format-CatalogueSummary $mixed)"
$joinedBlocked = Join-CatalogueResults @($noColumns)
Assert ($joinedBlocked.overall.blocked -eq 2 -and !$joinedBlocked.all_passed -and (Format-CatalogueSummary $joinedBlocked) -like '*OK con bloqueados*') 'session totals keep blocked counts'

# A lost pipe on the journal.read wire line stops the run too.
$wireDead = @{ On = $false }
$wireDeadLine = '{"status":"failed","code":"pipe_io_error","field":"pipe_name","detail":"IOException: broken pipe","created":[],"modified":[],"deleted":[],"readback":null,"receipt":null,"warnings":[]}'
$deadJournal = (Invoke-FakeSequence -All -WireWrap { param($index, $key, $line, $inner) if ($key -eq 'j-after') { $wireDead.On = $true }; if ($wireDead.On) { return @{ output = $wireDeadLine; exit_code = 1; timed_out = $false } }; & $inner $index $key $line }).Result
$jAfter = @($deadJournal.steps | Where-Object { $_.key -eq 'j-after' })[0]
Assert ($null -ne $deadJournal.aborted -and $deadJournal.aborted -like '*pipe_io_error*' -and !$jAfter.passed -and @($deadJournal.steps | Where-Object { $_.index -gt $jAfter.index -and $null -ne $_.request }).Count -eq 0) "lost wire pipe must stop the run: $($deadJournal.aborted)"
Assert (@($deadJournal.steps | Where-Object { $_.key -in 'j-baseline', 'j-wall' -and $_.passed }).Count -eq 2 -and (Get-Group $deadJournal 'modificar').not_sent -eq (Get-Group $deadJournal 'modificar').total) 'journal steps before it kept, the destructive group never sent'

# v1 only: a lost pipe also stops the remaining v1 steps (new in P1-06; before, each later step was still sent).
$v1Dead = @{ On = $false }
$v1DeadRun = (Invoke-FakeSequence -Wrap { param($index, $key, $json, $inner) if ($key -eq 'door') { $v1Dead.On = $true }; if ($v1Dead.On) { return @{ output = $deadLine; exit_code = 1; timed_out = $false } }; & $inner $index $key $json }).Result
$doorStep = @($v1DeadRun.steps | Where-Object { $_.key -eq 'door' })[0]
Assert ($v1DeadRun.groups.Count -eq 1 -and $null -ne $v1DeadRun.aborted -and $v1DeadRun.counts.passed -eq $doorStep.index - 1 -and $v1DeadRun.counts.not_sent -eq 17 - $doorStep.index) "v1-only lost pipe: $($v1DeadRun.counts | ConvertTo-Json -Compress)"
Assert ((Format-CatalogueSummary $v1DeadRun) -eq "Catálogo: $($doorStep.index - 1)/17 pasos OK; DETENIDO: $($v1DeadRun.aborted)") "v1-only stopped line: $(Format-CatalogueSummary $v1DeadRun)"
# Session totals over several runs.
$joined = Join-CatalogueResults @($full, $roofFails)
Assert ($joined.counts.passed -eq 34 -and (Get-Group $joined 'v2').failed -eq 1 -and $joined.overall.total -eq 2 * $full.overall.total -and !$joined.all_passed) 'session totals sum the runs by group'

# --- P1-11: direct shapes, extrusion roofs, move/copy/rotate/mirror and document.save_copy (before the destructive group) ---
function Get-Step($Result, [string]$Key) { @($Result.steps | Where-Object { $_.key -eq $Key }) | Select-Object -First 1 }
function Get-StepField($Result, [string]$Key, [string]$Field) { Get-CatalogueMember (Get-Step $Result $Key) $Field }
function Get-RequestValue($Result, [string]$Key, [string]$Path) { Get-CataloguePath (Get-StepField $Result $Key 'request') $Path }
# A group summary member without indexing (no group yet answers $null).
function Get-GroupField($Result, [string]$Key, [string]$Field) { Get-CatalogueMember (@($Result.groups | Where-Object { $_.key -eq $Key }) | Select-Object -First 1) $Field }

# The Core contracts, read from the C# source (no build needed): catalogue command names, typed refusal codes, the
# client-side codes (refused by the CLI before the pipe) and the document.save_copy reason prefixes.
$coreSource = (Get-ChildItem -LiteralPath (Join-Path $root 'core/Bim.Core') -Filter '*.cs' | Sort-Object Name | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
function Get-CoreClassBody([string]$Class) {
    $match = [regex]::Match($coreSource, "(?s)\bclass $Class\b.*?\n\}")
    if ($match.Success) { return $match.Value }
    return ''
}
function Get-CoreConstants([string]$Class) {
    $map = [ordered]@{}
    foreach ($m in [regex]::Matches((Get-CoreClassBody $Class), 'const string (\w+)\s*=\s*"([^"]*)"')) { $map[$m.Groups[1].Value] = $m.Groups[2].Value }
    return $map
}
function Get-CoreList([string]$Class, [string]$Property) {
    $constants = Get-CoreConstants $Class
    $list = [regex]::Match((Get-CoreClassBody $Class), "(?s)\b$Property \{ get; \} = new\[\]\s*\{(.*?)\};")
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($id in [regex]::Matches($list.Groups[1].Value, '\w+')) { if ($constants.Contains($id.Value)) { $null = $set.Add($constants[$id.Value]) } }
    return , $set
}
$catalogue = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($m in [regex]::Matches($coreSource, 'new RevitCommandSpec\(\s*(?:"(?<name>[^"]+)"|(?<class>\w+)\.(?<member>\w+))')) {
    if ($m.Groups['name'].Success) { $null = $catalogue.Add($m.Groups['name'].Value) }
    else { $constants = Get-CoreConstants $m.Groups['class'].Value; if ($constants.Contains($m.Groups['member'].Value)) { $null = $catalogue.Add($constants[$m.Groups['member'].Value]) } }
}
Assert ($catalogue.Count -ge 60 -and $catalogue.Contains('wall.create') -and $catalogue.Contains('element.mirror') -and $catalogue.Contains('document.save_copy') -and $catalogue.Contains('roof.create_extrusion')) "catalogue command names parsed from core/Bim.Core: $($catalogue.Count)"
$codes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($class in 'RevitCommandCodes', 'RevitTransformCodes', 'RevitRotateMirrorCodes') { foreach ($value in (Get-CoreConstants $class).Values) { $null = $codes.Add($value) } }
$clientSide = Get-CoreList 'RevitCommandCodes' 'ClientSide'
$copyReasons = Get-CoreList 'RevitSaveCopyGuard' 'Reasons'
$saveAsReasons = Get-CoreList 'RevitSaveAsGuard' 'Reasons'
Assert ($codes.Contains('element_not_rotatable') -and $codes.Contains('document_conflict') -and $clientSide.Contains('arc_collinear') -and $clientSide.Contains('save_copy_refused') -and $clientSide.Contains('save_as_refused') -and !$clientSide.Contains('reference_not_found')) "refusal codes parsed from core/Bim.Core: $($codes.Count) codes, $($clientSide.Count) client-side"
Assert ($copyReasons.Count -eq 9 -and $copyReasons.Contains('outside_copies_root') -and $copyReasons.Contains('target_exists')) "save_copy reasons parsed from core/Bim.Core: $(@($copyReasons) -join ',')"
Assert ($saveAsReasons.Count -eq 13 -and $saveAsReasons.Contains('outside_copies_root') -and $saveAsReasons.Contains('target_exists') -and $catalogue.Contains('document.save_as')) "save_as reasons parsed from core/Bim.Core: $(@($saveAsReasons) -join ',')"

# Every step of the one-run list against those contracts: a catalogue command (journal.read is the wire line), a typed
# refusal code that exists, client-side refusals only with client-side codes, save_copy reasons that exist, ReuseOf
# naming an earlier step of the same command, unique keys.
$seen = @{}
$clientKeys = [Collections.Generic.List[string]]::new()
foreach ($group in (Get-CatalogueGroups)) {
    foreach ($spec in $group.Steps) {
        $key = [string]$spec.Key
        $isWire = $spec.ContainsKey('Wire') -and $spec.Wire
        if ($isWire) { Assert ($spec.Command -eq 'journal.read') "[$($group.Key)] ${key}: only journal.read goes as a wire line" }
        else { Assert ($catalogue.Contains([string]$spec.Command)) "[$($group.Key)] ${key}: $($spec.Command) is not a catalogue command" }
        if ($spec.Expect.ContainsKey('Code')) { Assert ($spec.Expect.Status -eq 'failed' -and $codes.Contains([string]$spec.Expect.Code)) "[$($group.Key)] ${key}: expected code $($spec.Expect.Code) is not a Core refusal code" }
        else { Assert ($spec.Expect.Status -eq 'succeeded') "[$($group.Key)] ${key}: a failure must name its code" }
        if ($spec.ContainsKey('ClientSide') -and $spec.ClientSide) {
            $clientKeys.Add($key)
            Assert (!$isWire -and $spec.Expect.ContainsKey('Code') -and $clientSide.Contains([string]$spec.Expect.Code)) "[$($group.Key)] ${key}: a client-side refusal must expect a RevitCommandCodes.ClientSide code"
        }
        if ($spec.ContainsKey('Reason')) {
            $reasonOk = ($spec.Command -eq 'document.save_copy' -and $spec.Expect.Code -eq 'save_copy_refused' -and $copyReasons.Contains([string]$spec.Reason)) -or ($spec.Command -eq 'document.save_as' -and $spec.Expect.Code -eq 'save_as_refused' -and $saveAsReasons.Contains([string]$spec.Reason))
            Assert $reasonOk "[$($group.Key)] ${key}: reason $($spec.Reason) is not a RevitSaveCopyGuard / RevitSaveAsGuard reason of $($spec.Command)"
        }
        if ($spec.ContainsKey('ReuseOf')) { Assert ($seen.ContainsKey($spec.ReuseOf) -and $seen[$spec.ReuseOf] -eq $spec.Command) "[$($group.Key)] ${key}: ReuseOf $($spec.ReuseOf) must be an earlier step of the same command" }
        Assert (!$seen.ContainsKey($key)) "duplicate step key $key"
        $seen[$key] = $spec.Command
    }
}
Assert (($clientKeys -join ',') -eq 'invalid-arc,f-bowtie,x-closed,t-move-zero,t-rotate-full,c-outside,c-save-again,c-as-outside,c-as-save-again') "client-side refusal steps: $($clientKeys -join ',')"

# The new groups: steps in order with their exact outcome (ok or the typed refusal code), each passing in the clean run.
$p111Groups = [ordered]@{
    formas = @('f-box', 'f-extrusion', 'f-bowtie')
    extrusion = @('x-roof-types', 'x-barrel', 'x-barrel-reversed', 'x-barrel-diagonal', 'x-closed')
    transformar = @('t-wall', 't-move', 't-move-retry', 't-move-zero', 't-copy', 't-rotate', 't-rotate-door', 't-rotate-full', 't-mirror')
    copia = @('c-outside', 'c-save', 'c-save-again', 'c-levels-copy', 'c-levels-source', 'c-as-outside', 'c-as-save', 'c-as-save-again', 'c-as-levels-new', 'c-as-levels-previous')
}
foreach ($entry in $p111Groups.GetEnumerator()) {
    $inGroup = (@($full.steps | Where-Object { $_.group -eq $entry.Key } | ForEach-Object { $_.key }) -join ',')
    Assert ($inGroup -eq ($entry.Value -join ',')) "group $($entry.Key) steps in order: $inGroup"
}
$p111Outcomes = [ordered]@{
    'f-box' = 'ok'; 'f-extrusion' = 'ok'; 'f-bowtie' = 'boundary_self_intersecting'
    'x-roof-types' = 'ok'; 'x-barrel' = 'ok'; 'x-barrel-reversed' = 'ok'; 'x-barrel-diagonal' = 'ok'; 'x-closed' = 'args_invalid'
    't-wall' = 'ok'; 't-move' = 'ok'; 't-move-retry' = 'ok'; 't-move-zero' = 'length_invalid'; 't-copy' = 'ok'; 't-rotate' = 'ok'; 't-rotate-door' = 'element_not_rotatable'; 't-rotate-full' = 'angle_invalid'; 't-mirror' = 'ok'
    'c-outside' = 'save_copy_refused'; 'c-save' = 'ok'; 'c-save-again' = 'save_copy_refused'; 'c-levels-copy' = 'ok'; 'c-levels-source' = 'document_conflict'
    'c-as-outside' = 'save_as_refused'; 'c-as-save' = 'ok'; 'c-as-save-again' = 'save_as_refused'; 'c-as-levels-new' = 'ok'; 'c-as-levels-previous' = 'document_conflict'
}
foreach ($entry in $p111Outcomes.GetEnumerator()) {
    $step = Get-Step $full $entry.Key
    $code = [string](Get-CatalogueMember (Get-CatalogueMember $step 'response') 'code')
    Assert ($null -ne $step -and $step.passed -and $code -eq $entry.Value) "P1-11 step $($entry.Key) must pass answering $($entry.Value): $(if ($step) { "$code / $($step.reason)" } else { 'missing' })"
}
foreach ($command in @('direct_shape.create', 'roof.create_extrusion', 'element.move', 'element.copy', 'element.rotate', 'element.mirror', 'document.save_copy', 'document.save_as')) {
    Assert (@($full.steps | Where-Object { $_.command -eq $command -and $_.passed -and [string](Get-CatalogueMember $_.response 'code') -eq 'ok' }).Count -ge 1) "one-run covers $command with a success"
}
$lastNew = $full.steps | Where-Object { $_.group -in @($p111Groups.Keys) } | Select-Object -Last 1
$firstModify = $full.steps | Where-Object { $_.group -eq 'modificar' } | Select-Object -First 1
$lastJournal = $full.steps | Where-Object { $_.group -eq 'diario' } | Select-Object -Last 1
Assert ($null -ne $lastNew -and $lastNew.index -lt $firstModify.index -and $lastNew.index -gt $lastJournal.index) 'the P1-11 groups run after the journal and before the destructive group'
Assert (@($full.steps | Where-Object { $_.group -in @($p111Groups.Keys) -and $_.command -in 'element.delete', 'element.set_parameter' }).Count -eq 0) 'no delete or parameter change in the P1-11 groups'

# Exact arguments: three barrel planes (axis-aligned, reversed, diagonal) with the same open arc profile and extents.
function Get-PlaneDirection($Result, [string]$Key) {
    $s = @(Get-RequestValue $Result $Key 'args.plane.start'); $e = @(Get-RequestValue $Result $Key 'args.plane.end')
    if ($s.Count -ne 2 -or $e.Count -ne 2) { return @(0, 0) }
    return @(([double]$e[0] - [double]$s[0]), ([double]$e[1] - [double]$s[1]))
}
$axis = Get-PlaneDirection $full 'x-barrel'
$reversed = Get-PlaneDirection $full 'x-barrel-reversed'
$diagonal = Get-PlaneDirection $full 'x-barrel-diagonal'
Assert ($axis[0] -gt 0 -and $axis[1] -eq 0 -and $reversed[0] -lt 0 -and $reversed[1] -eq 0 -and $diagonal[0] -ne 0 -and $diagonal[1] -ne 0) "barrel planes: axis-aligned $($axis -join ','), reversed $($reversed -join ','), diagonal $($diagonal -join ',')"
foreach ($key in 'x-barrel', 'x-barrel-reversed', 'x-barrel-diagonal') {
    $sentProfile = @(Get-RequestValue $full $key 'args.profile')
    Assert ($sentProfile.Count -eq 1 -and (Get-CatalogueMember $sentProfile[0] 'kind') -eq 'arc' -and $null -ne (Get-CatalogueMember $sentProfile[0] 'through') -and (Get-RequestValue $full $key 'args.extrusion_start_mm') -eq 0 -and (Get-RequestValue $full $key 'args.extrusion_end_mm') -eq 4000 -and (Get-RequestValue $full $key 'args.level') -eq $full.picked['level']) "$key sends one open arc, extents 0..4000 on the walls' level"
    Assert (@(Get-StepField $full $key 'comparisons' | Where-Object { $null -ne $_ -and $_.ok }).Count -ge 8) "$key records requested vs observed rows"
}
# Move, copy, rotate and mirror act on their own test wall (the rotation on its copy) and on the v1 door only for the refusal.
$testWall = Get-CatalogueFirstCreated (Get-StepField $full 't-wall' 'response')
$wallCopy = Get-CatalogueFirstCreated (Get-StepField $full 't-copy' 'response')
$doorUid = Get-CatalogueFirstCreated (Get-StepField $full 'door' 'response')
Assert ($testWall -and $wallCopy -and $testWall -ne $wallCopy) 'the copy is a new element'
foreach ($pair in @(@('t-move', $testWall), @('t-move-retry', $testWall), @('t-copy', $testWall), @('t-rotate', $wallCopy), @('t-mirror', $testWall), @('t-rotate-door', $doorUid))) {
    Assert ([string](Get-CatalogueFirst (Get-RequestValue $full $pair[0] 'args.element_uids')) -eq $pair[1]) "$($pair[0]) targets the expected element"
}
Assert ((Get-StepField $full 't-move-retry' 'operation_id') -eq (Get-StepField $full 't-move' 'operation_id') -and (Get-CataloguePath (Get-StepField $full 't-move-retry' 'response') 'receipt.replayed') -eq $true) 'the move retry replays (the wall moves once)'
Assert ((Get-RequestValue $full 't-mirror' 'args.keep_original') -eq $true -and (Get-RequestValue $full 't-rotate' 'args.angle_deg') -eq 90) 'mirror keeps the original, rotation is 90 degrees'

# document.save_copy: copy next to the test document, refusal outside .bim/native-runs, target_exists on the repeat; then
# every later request names the copy (Revit re-points the open document), except the two that name the source on purpose.
$copyPath = [IO.Path]::Combine([IO.Path]::GetDirectoryName($documentPath), "BIM-Agentic-Test-c04-$stamp-r1-copia.rvt")
$saveAsPath = [IO.Path]::Combine([IO.Path]::GetDirectoryName($documentPath), "BIM-Agentic-Test-c04-$stamp-r1-guardado.rvt")
$ownerRootUsed = if (Get-Command Get-CatalogueOwnerRoot -ErrorAction SilentlyContinue) { Get-CatalogueOwnerRoot $documentPath } else { $null }
# S1: the refused target lies in a folder that does not exist under the owner folder, so a guard regression would stop
# at folder_missing (and Revit's SaveAs would fail) instead of writing into the owner's folder.
$asOutsideFolder = if ($ownerRootUsed) { [IO.Path]::Combine($ownerRootUsed, "bim-agentic-no-existe-$stamp-r1") } else { $null }
$asOutsidePath = if ($ownerRootUsed) { [IO.Path]::Combine($asOutsideFolder, "BIM-Agentic-Test-c04-$stamp-r1-fuera-como.rvt") } else { $null }
$outsidePath = [IO.Path]::Combine([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($documentPath))), "BIM-Agentic-Test-c04-$stamp-r1-fuera.rvt")
Assert ((Get-RequestValue $full 'c-save' 'args.path') -eq $copyPath -and (Get-RequestValue $full 'c-save-again' 'args.path') -eq $copyPath -and (Get-RequestValue $full 'c-outside' 'args.path') -eq $outsidePath) "save_copy targets: $(Get-RequestValue $full 'c-save' 'args.path') / $(Get-RequestValue $full 'c-outside' 'args.path')"
Assert ((Get-StepField $full 'c-save-again' 'operation_id') -eq (Get-StepField $full 'c-save' 'operation_id')) 'the repeat reuses the save operation_id (refused target_exists, not replayed)'
foreach ($key in 'c-outside', 'c-save-again') { Assert (@(Get-StepField $full $key 'comparisons' | Where-Object { $null -ne $_ -and $_.ok }).Count -eq 2) "$key checks the refused field and reason" }
Assert ([string](Get-CataloguePath (Get-StepField $full 'c-outside' 'response') 'detail') -like 'outside_copies_root:*' -and [string](Get-CataloguePath (Get-StepField $full 'c-save-again' 'response') 'detail') -like 'target_exists:*') 'save_copy refusals carry their reason prefix'
$saveChecks = @(Get-StepField $full 'c-save' 'comparisons' | Where-Object { $null -ne $_ })
Assert (@($saveChecks | Where-Object { $_.check -like '*sha256*' -and $_.ok }).Count -ge 2 -and @($saveChecks | Where-Object { !$_.ok }).Count -eq 0) 'c-save compares the read sha256 with the file on disk'
Assert ((Test-Path -LiteralPath $copyPath -PathType Leaf) -and !(Test-Path -LiteralPath $outsidePath)) 'the copy exists next to the test document and nothing was written outside .bim/native-runs'
$saveIndex = [int](Get-StepField $full 'c-save' 'index')
$saveAsIndex = [int](Get-StepField $full 'c-as-save' 'index')
$wrongDocument = @($full.steps | Where-Object { $null -ne $_.request } | Where-Object {
    $named = if ($_.command -eq 'journal.read') { [string](Get-CatalogueMember $_.request 'DocumentPath') } else { [string](Get-CataloguePath $_.request 'document.path') }
    $wanted = if ($_.index -gt $saveAsIndex -and $_.key -notin 'c-as-save-again', 'c-as-levels-previous') { $saveAsPath }
    elseif ($_.index -gt $saveIndex -and $_.key -notin 'c-save-again', 'c-levels-source') { $copyPath } else { $documentPath }
    $named -ne $wanted })
Assert ($wrongDocument.Count -eq 0) "document.path per step (the source until c-save, then the copy, after c-as-save the new file): $(@($wrongDocument | ForEach-Object { $_.key }) -join ',')"

# P1-14 document.save_as: the open copy saved as a new file next to it (hash and post-save path readback), the repeat
# refused target_exists before the pipe, a target in an owner-enabled folder refused outside_copies_root.
Assert ($null -ne $asOutsidePath -and $ownerRootUsed -eq $inProcessOwnerRoot) "an owner-enabled folder outside .bim/native-runs is listed for the outside_copies_root refusal: $ownerRootUsed"
Assert ((Get-RequestValue $full 'c-as-save' 'args.path') -eq $saveAsPath -and (Get-RequestValue $full 'c-as-save-again' 'args.path') -eq $saveAsPath -and (Get-RequestValue $full 'c-as-outside' 'args.path') -eq $asOutsidePath) "save_as targets: $(Get-RequestValue $full 'c-as-save' 'args.path') / $(Get-RequestValue $full 'c-as-outside' 'args.path')"
Assert ((Get-RequestValue $full 'c-as-save' 'document.path') -eq $copyPath -and (Get-RequestValue $full 'c-as-outside' 'document.path') -eq $copyPath) 'save_as is sent naming the copy Revit has open'
Assert ((Get-StepField $full 'c-as-save-again' 'operation_id') -eq (Get-StepField $full 'c-as-save' 'operation_id') -and (Get-RequestValue $full 'c-as-save-again' 'document.path') -eq $copyPath) 'the save_as repeat is the same request (operation_id and previous document path), refused target_exists, not replayed'
foreach ($key in 'c-as-outside', 'c-as-save-again') { Assert (@(Get-StepField $full $key 'comparisons' | Where-Object { $null -ne $_ -and $_.ok }).Count -eq 2) "$key checks the refused field and reason" }
Assert ([string](Get-CataloguePath (Get-StepField $full 'c-as-outside' 'response') 'detail') -like 'outside_copies_root:*' -and [string](Get-CataloguePath (Get-StepField $full 'c-as-save-again' 'response') 'detail') -like 'target_exists:*') 'save_as refusals carry their reason prefix'
$asChecks = @(Get-StepField $full 'c-as-save' 'comparisons' | Where-Object { $null -ne $_ })
Assert (@($asChecks | Where-Object { $_.check -like '*sha256*' -and $_.ok }).Count -ge 2 -and @($asChecks | Where-Object { $_.check -like '*anterior*' -and $_.ok }).Count -ge 2 -and @($asChecks | Where-Object { !$_.ok }).Count -eq 0) "c-as-save compares the hash with the file on disk and reads back the previous path: $(@($asChecks | ForEach-Object { $_.check }) -join ' | ')"
Assert ((Get-RequestValue $full 'c-as-levels-new' 'document.path') -eq $saveAsPath -and (Get-RequestValue $full 'c-as-levels-previous' 'document.path') -eq $copyPath) 'the post-save probes name the new file (ok) and the previous one (document_conflict)'
Assert ((Test-Path -LiteralPath $saveAsPath -PathType Leaf) -and (Test-Path -LiteralPath $copyPath -PathType Leaf) -and @(Get-ChildItem -LiteralPath $inProcessOwnerRoot -Force).Count -eq 0) 'the new file exists next to the copy, the copy is kept and nothing was written in the owner folder'
Assert ($full.picked['document_switched_by'] -eq 'c-as-save' -and $full.picked['document_path'] -eq $saveAsPath -and $full.picked['save_as_path'] -eq $saveAsPath -and $full.picked['save_as_previous'] -eq $copyPath) "the evidence records the switch to the new file: $($full.picked['document_switched_by']) $($full.picked['document_path'])"
$bimRoot = $inProcessBim + [IO.Path]::DirectorySeparatorChar
$runsRoot = [IO.Path]::Combine($inProcessBim, 'native-runs') + [IO.Path]::DirectorySeparatorChar
$pathsNamed = @($full.steps | Where-Object { $null -ne $_.request -and $_.command -ne 'journal.read' } | ForEach-Object { [string](Get-CataloguePath $_.request 'document.path'); if ($_.command -in 'document.save_copy', 'document.save_as') { [string](Get-CataloguePath $_.request 'args.path') } })
$badPaths = @($pathsNamed | Where-Object { [IO.Path]::GetFileName($_) -cnotmatch '\ABIM-Agentic-Test[A-Za-z0-9._-]*\.rvt\z' -or (!([IO.Path]::GetFullPath($_)).StartsWith($runsRoot, [StringComparison]::Ordinal) -and $_ -ne $outsidePath -and $_ -ne $asOutsidePath) })
Assert ($pathsNamed.Count -gt 0 -and $badPaths.Count -eq 0) "every path a request names is a BIM-Agentic-Test*.rvt under .bim/native-runs (or one of the two refused outside targets): $($badPaths -join ', ')"
Assert ($outsidePath.StartsWith($bimRoot, [StringComparison]::Ordinal)) 'the refused save_copy target lies directly under .bim'

# A document that is not a test copy under .bim/native-runs is never saved as a copy: the copy steps are not sent.
foreach ($notCopy in @([IO.Path]::GetFullPath((Join-Path $root 'Proyectos/Cliente/Modelo.rvt')), [IO.Path]::GetFullPath((Join-Path $root 'otra-carpeta/BIM-Agentic-Test.rvt')))) {
    $otherFake = New-FakeRevitState $documentUid
    $otherInvoke = { param($index, $key, $json) $text = Invoke-FakeRevitCommand $otherFake $json; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    $other = Invoke-CatalogueSequence -Stamp $stamp -Run 1 -DocumentPath $notCopy -DocumentUid $documentUid -Invoke $otherInvoke
    $copySteps = @($other.steps | Where-Object { $_.group -eq 'copia' })
    Assert ($copySteps.Count -eq 10 -and @($copySteps | Where-Object { $null -ne $_.request }).Count -eq 0 -and [string](Get-StepField $other 'c-save' 'reason') -like '*no es una copia de prueba*' -and [string](Get-StepField $other 'c-as-outside' 'reason') -like '*no es una copia de prueba*' -and [string](Get-StepField $other 'c-as-save' 'reason') -like '*no es una copia de prueba*') "no save_copy or save_as for $notCopy : $(@($copySteps | ForEach-Object { "$($_.key)=$($_.reason)" }) -join '; ')"
}

# Faults the checks must notice.
$hashOff = (Invoke-FakeSequence @{ SaveCopyHashOff = $true } -All).Result
Assert (!(Get-StepField $hashOff 'c-save' 'passed') -and [string](Get-StepField $hashOff 'c-save' 'reason') -like '*sha256*' -and $null -eq (Get-StepField $hashOff 'c-save-again' 'request')) "a read sha256 that differs from the file must fail c-save: $(Get-StepField $hashOff 'c-save' 'reason')"
Assert ((Get-StepField $hashOff 'c-levels-copy' 'passed') -and (Get-StepField $hashOff 'c-levels-source' 'passed') -and (Get-GroupField $hashOff 'modificar' 'failed') -eq 0) "Revit re-pointed the document anyway: later steps name the copy: $(Get-FailedSteps $hashOff)"
Assert ((Get-RequestValue $hashOff 'c-as-save' 'document.path') -eq $copyPath -and $hashOff.picked['save_as_previous'] -eq $copyPath -and $full.picked['save_as_previous'] -eq $copyPath) 'after c-save the run names the copy until save_as'
# A save refused before Save As: the probe naming the copy is refused (document_conflict), nothing switches, the
# destructive group runs on the source.
$noCopy = (Invoke-FakeSequence @{ FailCommands = @('document.save_copy') } -All).Result
Assert (!(Get-StepField $noCopy 'c-save' 'passed') -and @('c-save-again', 'c-levels-source' | Where-Object { $null -ne (Get-StepField $noCopy $_ 'request') }).Count -eq 0) "a failed save sends no step that needs the copy: $(Get-FailedSteps $noCopy)"
Assert ((Get-RequestValue $noCopy 'c-levels-copy' 'document.path') -eq $copyPath -and !(Get-StepField $noCopy 'c-levels-copy' 'passed') -and [string](Get-CataloguePath (Get-StepField $noCopy 'c-levels-copy' 'response') 'code') -eq 'document_conflict' -and (Get-RequestValue $noCopy 'c-as-save' 'document.path') -eq $documentPath -and $noCopy.picked['save_as_previous'] -eq $documentPath) "the probe names the copy and is refused when Revit never saved it: $(Get-StepField $noCopy 'c-levels-copy' 'reason')"
Assert ((Get-GroupField $noCopy 'modificar' 'failed') -eq 0 -and (Get-StepField $noCopy 'c-as-save' 'passed') -and (Get-RequestValue $noCopy 'm-delete' 'document.path') -eq $saveAsPath) "without a copy save_as starts from the source and the destructive group runs on the new file: $(Get-FailedSteps $noCopy)"
# A save that fails after Save As already re-pointed the document (CheckWritten or readback failure): the probe
# naming the copy succeeds, the run switches to the copy and records it, the destructive group runs on the copy.
$afterSave = (Invoke-FakeSequence @{ SaveCopyFailsAfterSave = $true } -All).Result
Assert (!(Get-StepField $afterSave 'c-save' 'passed') -and [string](Get-CataloguePath (Get-StepField $afterSave 'c-save' 'response') 'code') -eq 'transaction_failed' -and $null -eq (Get-StepField $afterSave 'c-save-again' 'request')) "a save failing after Save As fails c-save: $(Get-StepField $afterSave 'c-save' 'reason')"
Assert ((Get-StepField $afterSave 'c-levels-copy' 'passed') -and (Get-RequestValue $afterSave 'c-as-save' 'document.path') -eq $copyPath -and $afterSave.picked['save_as_previous'] -eq $copyPath -and @(Get-StepField $afterSave 'c-levels-copy' 'comparisons' | Where-Object { $null -ne $_ -and $_.check -like '*cambia a la copia*' -and $_.ok }).Count -eq 1) "the probe switches the run to the copy and records it: $(Get-FailedSteps $afterSave)"
Assert ((Get-StepField $afterSave 'c-levels-source' 'passed') -and (Get-GroupField $afterSave 'modificar' 'failed') -eq 0 -and (Get-RequestValue $afterSave 'm-delete' 'document.path') -eq $saveAsPath -and $afterSave.overall.failed -eq 2) "after the switch save_as starts from the copy and the destructive group runs on the new file: $(Get-FailedSteps $afterSave)"
# P1-14 save_as faults. A read sha256 that differs from the file fails c-as-save; Revit re-pointed the open document
# anyway, so the probes and the destructive group go to the new file.
$asHashOff = (Invoke-FakeSequence @{ SaveAsHashOff = $true } -All).Result
Assert (!(Get-StepField $asHashOff 'c-as-save' 'passed') -and [string](Get-StepField $asHashOff 'c-as-save' 'reason') -like '*sha256*' -and $null -eq (Get-StepField $asHashOff 'c-as-save-again' 'request')) "a save_as sha256 that differs from the file must fail c-as-save: $(Get-StepField $asHashOff 'c-as-save' 'reason')"
Assert ((Get-StepField $asHashOff 'c-as-levels-new' 'passed') -and (Get-StepField $asHashOff 'c-as-levels-previous' 'passed') -and (Get-RequestValue $asHashOff 'm-delete' 'document.path') -eq $saveAsPath -and $asHashOff.overall.failed -eq 2) "after a re-pointed save_as later steps name the new file: $(Get-FailedSteps $asHashOff)"
# A save_as refused before Save As: the new-file probe is document_conflict, nothing switches, the previous-file probe
# is not sent and the destructive group runs on the copy.
$noSaveAs = (Invoke-FakeSequence @{ FailCommands = @('document.save_as') } -All).Result
Assert (!(Get-StepField $noSaveAs 'c-as-save' 'passed') -and @('c-as-save-again', 'c-as-levels-previous' | Where-Object { $null -ne (Get-StepField $noSaveAs $_ 'request') }).Count -eq 0) "a failed save_as sends no step that needs the new file: $(Get-FailedSteps $noSaveAs)"
Assert ((Get-RequestValue $noSaveAs 'c-as-levels-new' 'document.path') -eq $saveAsPath -and [string](Get-CataloguePath (Get-StepField $noSaveAs 'c-as-levels-new' 'response') 'code') -eq 'document_conflict' -and $noSaveAs.picked['document_path'] -eq $copyPath -and $noSaveAs.picked['document_switched_by'] -eq 'c-save') "the new-file probe is refused when Revit never saved it: $(Get-StepField $noSaveAs 'c-as-levels-new' 'reason')"
Assert ((Get-GroupField $noSaveAs 'modificar' 'failed') -eq 0 -and (Get-RequestValue $noSaveAs 'm-delete' 'document.path') -eq $copyPath -and $noSaveAs.overall.failed -eq 4) "without the new file the destructive group runs on the copy: $(Get-FailedSteps $noSaveAs)"
# A save_as failing after Save As re-pointed the document: the new-file probe switches the run and records it.
$asAfter = (Invoke-FakeSequence @{ SaveAsFailsAfterSave = $true } -All).Result
Assert (!(Get-StepField $asAfter 'c-as-save' 'passed') -and [string](Get-CataloguePath (Get-StepField $asAfter 'c-as-save' 'response') 'code') -eq 'transaction_failed' -and $null -eq (Get-StepField $asAfter 'c-as-save-again' 'request')) "a save_as failing after Save As fails c-as-save: $(Get-StepField $asAfter 'c-as-save' 'reason')"
Assert ((Get-StepField $asAfter 'c-as-levels-new' 'passed') -and $asAfter.picked['document_path'] -eq $saveAsPath -and $asAfter.picked['document_switched_by'] -eq 'c-as-levels-new' -and @(Get-StepField $asAfter 'c-as-levels-new' 'comparisons' | Where-Object { $null -ne $_ -and $_.check -like '*cambia al archivo nuevo*' -and $_.ok }).Count -eq 1) "the new-file probe switches the run and records it: $(Get-FailedSteps $asAfter)"
Assert ((Get-StepField $asAfter 'c-as-levels-previous' 'passed') -and (Get-RequestValue $asAfter 'm-delete' 'document.path') -eq $saveAsPath -and $asAfter.overall.failed -eq 2) "after that switch the destructive group runs on the new file: $(Get-FailedSteps $asAfter)"
# Without an owner-enabled folder the outside_copies_root step is blocked (not failed) and names the owner file.
$noOwnerFake = New-FakeRevitState $documentUid
$noOwnerFake.DocumentPath = $documentPath
$noOwnerInvoke = { param($index, $key, $json) $text = Invoke-FakeRevitCommand $noOwnerFake $json; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
$noOwnerWire = { param($index, $key, $line) $text = Invoke-FakeRevitWire $noOwnerFake $line; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
$savedOwnerRoot = ${function:Get-CatalogueOwnerRoot}
try {
    ${function:Get-CatalogueOwnerRoot} = { param([string]$Path) $null }
    $noOwner = Invoke-CatalogueSequence -Stamp $stamp -Run 2 -DocumentPath $documentPath -DocumentUid $documentUid -Invoke $noOwnerInvoke -InvokeWire $noOwnerWire
} finally { if ($savedOwnerRoot) { ${function:Get-CatalogueOwnerRoot} = $savedOwnerRoot } else { Remove-Item -LiteralPath function:Get-CatalogueOwnerRoot -ErrorAction SilentlyContinue } }
Assert ((Get-StepField $noOwner 'c-as-outside' 'blocked') -and $null -eq (Get-StepField $noOwner 'c-as-outside' 'request') -and [string](Get-StepField $noOwner 'c-as-outside' 'reason') -like '*revit-enabled-roots.txt*' -and $noOwner.overall.failed -eq 0 -and $noOwner.overall.blocked -eq 1) "no owner-enabled folder blocks only c-as-outside: $(Get-FailedSteps $noOwner)"
Assert ($null -ne $asOutsideFolder -and !(Test-Path -LiteralPath $asOutsideFolder)) "the c-as-outside target folder does not exist: $asOutsideFolder"
# S1 safety net (Test-CatalogueSaveTarget, applied by Invoke-CatalogueSequence before sending any document.save_as): a
# save_as that is not a client-side refusal goes only into the test copy's .bim/native-runs; a client-side refusal
# outside it only into a folder that does not exist.
$guardOk = Get-Command Test-CatalogueSaveTarget -ErrorAction SilentlyContinue
$runsFolder = [IO.Path]::GetDirectoryName($inProcessDirectory)
$guardCases = @(
    @{ What = 'a real save_as inside .bim/native-runs'; Spec = @{ Command = 'document.save_as' }; Path = [IO.Path]::Combine($inProcessDirectory, 'BIM-Agentic-Test-x.rvt'); Sent = $true },
    @{ What = 'a real save_as into the owner folder'; Spec = @{ Command = 'document.save_as' }; Path = [IO.Path]::Combine($inProcessOwnerRoot, 'BIM-Agentic-Test-x.rvt'); Sent = $false },
    @{ What = 'a real save_as into .bim next to native-runs'; Spec = @{ Command = 'document.save_as' }; Path = [IO.Path]::Combine($inProcessBim, 'BIM-Agentic-Test-x.rvt'); Sent = $false },
    @{ What = 'a real save_as into a sibling spelled native-runs-x'; Spec = @{ Command = 'document.save_as' }; Path = $runsFolder + '-x' + [IO.Path]::DirectorySeparatorChar + 'BIM-Agentic-Test-x.rvt'; Sent = $false },
    @{ What = 'a client-side refusal into an existing owner folder'; Spec = @{ Command = 'document.save_as'; ClientSide = $true }; Path = [IO.Path]::Combine($inProcessOwnerRoot, 'BIM-Agentic-Test-x.rvt'); Sent = $false },
    @{ What = 'a client-side refusal into a missing subfolder'; Spec = @{ Command = 'document.save_as'; ClientSide = $true }; Path = [IO.Path]::Combine($inProcessOwnerRoot, 'no-existe', 'BIM-Agentic-Test-x.rvt'); Sent = $true },
    @{ What = 'a client-side repeat inside .bim/native-runs'; Spec = @{ Command = 'document.save_as'; ClientSide = $true }; Path = [IO.Path]::Combine($inProcessDirectory, 'BIM-Agentic-Test-x.rvt'); Sent = $true })
foreach ($case in $guardCases) {
    $verdict = if ($guardOk) { Test-CatalogueSaveTarget $case.Spec ([ordered]@{ path = $case.Path }) $documentPath } else { 'missing Test-CatalogueSaveTarget' }
    Assert (($null -eq $verdict) -eq $case.Sent) "save target guard, $($case.What): $(if ($null -eq $verdict) { 'sent' } else { $verdict })"
}
# Through the sequence: when the would-be-missing folder exists, c-as-outside is not sent and says why.
$existingFolder = [IO.Path]::Combine($inProcessOwnerRoot, "bim-agentic-no-existe-$stamp-r3")
New-Item -ItemType Directory -Force $existingFolder | Out-Null
$existsFake = New-FakeRevitState $documentUid
$existsFake.DocumentPath = $documentPath
$existsInvoke = { param($index, $key, $json) $text = Invoke-FakeRevitCommand $existsFake $json; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
$existsWire = { param($index, $key, $line) $text = Invoke-FakeRevitWire $existsFake $line; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
$exists = Invoke-CatalogueSequence -Stamp $stamp -Run 3 -DocumentPath $documentPath -DocumentUid $documentUid -Invoke $existsInvoke -InvokeWire $existsWire
Remove-Item -LiteralPath $existingFolder -Force
Assert ($null -eq (Get-StepField $exists 'c-as-outside' 'request') -and [string](Get-StepField $exists 'c-as-outside' 'reason') -like '*no enviado*existe*' -and $exists.overall.failed -eq 1 -and (Get-StepField $exists 'c-as-save' 'passed')) "c-as-outside is not sent when its target folder exists: $(Get-FailedSteps $exists)"

# S4: owner entries as the CLI reads them, '/' normalised on drive paths, unusable entries skipped (Select-CatalogueOwnerRoot).
$selectOk = Get-Command Select-CatalogueOwnerRoot -ErrorAction SilentlyContinue
$ownerCases = @(
    @{ Lines = @('# owner folders', '', '  C:/repo/.bim/native-runs/  ', 'C:/Proyectos/Owner/'); Runs = 'C:\repo\.bim\native-runs'; Want = 'C:\Proyectos\Owner' },
    @{ Lines = @('c:/REPO/.bim/native-runs/sub', 'C:\repo\.BIM\Native-Runs', 'D:\Other'); Runs = 'C:\repo\.bim\native-runs'; Want = 'D:\Other' },
    @{ Lines = @('\\server\share\x', 'C:\a\..\b', 'relative\x', 'C:\ok.', 'C:\a\b.\c', 'C:', 'C:\x?y'); Runs = 'C:\repo\.bim\native-runs'; Want = $null },
    @{ Lines = @('/repo/.bim/native-runs/x', '/srv/owner/'); Runs = '/repo/.bim/native-runs'; Want = '/srv/owner' })
foreach ($case in $ownerCases) {
    $got = if ($selectOk) { Select-CatalogueOwnerRoot $case.Lines $case.Runs } else { 'missing Select-CatalogueOwnerRoot' }
    Assert ($got -ceq $case.Want) "owner root from [$($case.Lines -join ' | ')]: want '$($case.Want)', got '$got'"
}
$joinOk = Get-Command Join-CatalogueOwnerTarget -ErrorAction SilentlyContinue
Assert ($joinOk -and (Join-CatalogueOwnerTarget 'C:\Proyectos\Owner' 'sub' 'f.rvt') -ceq 'C:\Proyectos\Owner\sub\f.rvt' -and (Join-CatalogueOwnerTarget '/srv/owner' 'sub' 'f.rvt') -ceq '/srv/owner/sub/f.rvt') 'owner targets use the entry''s own separator'

# N1: a save_as Revit wrote outside the enabled roots or through a link (WrittenElsewhereDetail: "do not continue")
# stops the run: the new-file probe never switches to it and nothing more is sent.
Assert ($coreSource.Contains($script:CatalogueWrittenElsewhereMarker)) 'the stop marker is the closing sentence of core RevitSaveAsRules.WrittenElsewhereDetail'
$elsewhere = (Invoke-FakeSequence @{ SaveAsWrittenElsewhere = $true } -All).Result
Assert (!(Get-StepField $elsewhere 'c-as-save' 'passed') -and $null -eq (Get-StepField $elsewhere 'c-as-levels-new' 'request') -and @($elsewhere.steps | Where-Object { $_.index -gt [int](Get-StepField $elsewhere 'c-as-save' 'index') -and $null -ne $_.request }).Count -eq 0) "a save_as written elsewhere sends nothing more: $(Get-FailedSteps $elsewhere)"
Assert ($elsewhere.picked['document_path'] -eq $copyPath -and $elsewhere.picked['document_switched_by'] -eq 'c-save' -and $null -eq $elsewhere.picked['save_as_document'] -and [string]$elsewhere.aborted -like '*fuera de las carpetas habilitadas*' -and (Format-CatalogueSummary $elsewhere) -like '*DETENIDO*') "the run stops on the copy without switching to the new file: $($elsewhere.aborted)"
# Behind the real CLI a client-side case must never reach the pipe: the pipe fake answers it with its own code.
$leakFake = New-FakeRevitState $documentUid
$leakFake.BehindCli = $true
$leakLine = New-CatalogueEnvelope 'leak-1' $documentPath $documentUid 'element.move' ([ordered]@{ element_uids = @('0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0-00001001'); vector_mm = @(0, 0, 0) })
$leak = Invoke-FakeRevitCommand $leakFake $leakLine | ConvertFrom-Json
Assert ($leak.code -eq 'fake_client_side_leak' -and [string]$leak.detail -like '*length_invalid*') "a client-side case that reaches the pipe answers fake_client_side_leak: $($leak.code)"
$wrongSide = (Invoke-FakeSequence @{ RoofExtrusionMirrored = $true } -All).Result
Assert ((Get-GroupField $wrongSide 'extrusion' 'failed') -eq 3 -and $wrongSide.overall.failed -eq 3 -and @('x-barrel', 'x-barrel-reversed', 'x-barrel-diagonal' | Where-Object { [string](Get-StepField $wrongSide $_ 'reason') -notlike '*caja*' }).Count -eq 0) "a roof extruded to the wrong side must fail the three barrels on the box: $(Get-FailedSteps $wrongSide)"
$copyFails = (Invoke-FakeSequence @{ FailCommands = @('element.copy') } -All).Result
Assert (!(Get-StepField $copyFails 't-copy' 'passed') -and $null -eq (Get-StepField $copyFails 't-rotate' 'request') -and (Get-StepField $copyFails 't-mirror' 'passed') -and (Get-GroupField $copyFails 'transformar' 'failed') -eq 2) "a failed copy leaves its rotation unsent and the mirror running: $(Get-FailedSteps $copyFails)"
$shapeOff = (Invoke-FakeSequence @{ DirectShapeBoxOff = $true } -All).Result
Assert ((Get-GroupField $shapeOff 'formas' 'failed') -eq 2 -and $shapeOff.overall.failed -eq 2) "a DirectShape box off by 5 mm must fail both shapes: $(Get-FailedSteps $shapeOff)"
$rotateOff = (Invoke-FakeSequence @{ RotateWrongWay = $true } -All).Result
Assert (!(Get-StepField $rotateOff 't-rotate' 'passed') -and $rotateOff.overall.failed -eq 1) "a clockwise turn must fail t-rotate: $(Get-FailedSteps $rotateOff)"

# --- P4-11: a two-pass documentation update in the documentation group (P4-09 on the test copy) ---
# Its own wall, reference wall, section along the wall, tag and wall-to-wall dimension in doc-plan; the wall moved
# (0, 1000, 0); then P4-09's two passes over the stale dimension, tag and section in DocReplace delete order: a dry run
# of each stale target alone (collect), the replan (dependents classified: stale of an earlier delete dropped, kept /
# later-planned / untracked refused, untracked accepted only for the view), a dry run of each planned delete (confirm,
# same set), then the real deletes with exactly the planned confirm_dependents and the re-creations in create order
# (view, tag, dimension), each read back against the moved wall; the stale tag reads back reference_not_found.
$docUpExpected = @('doc-up-wall', 'doc-up-wall-ref', 'doc-up-section', 'doc-up-tag', 'doc-up-dim', 'doc-up-move') + $docUpDeleteKeys + @('doc-up-section-new', 'doc-up-tag-new', 'doc-up-dim-new', 'doc-up-stale-gone')
$docTail = @(@($full.steps | Where-Object { $_.group -eq 'documentacion' } | ForEach-Object { $_.key }) | Select-Object -Last $docUpExpected.Count)
Assert (($docTail -join ',') -eq ($docUpExpected -join ',')) "P4-11: the documentation group must end with the two-pass update steps in order; got $($docTail -join ',')"
foreach ($key in $docUpExpected) {
    $step = Get-Step $full $key
    $want = if ($key -eq 'doc-up-stale-gone') { 'reference_not_found' } else { 'ok' }
    $code = [string](Get-CatalogueMember (Get-CatalogueMember $step 'response') 'code')
    Assert ($null -ne $step -and $step.passed -and $code -eq $want) "P4-11 step $key must pass answering ${want}: $(if ($step) { "$code / $($step.reason)" } else { 'missing' })"
}
$upWall = Get-CatalogueFirstCreated (Get-StepField $full 'doc-up-wall' 'response')
$upStale = [ordered]@{ dimension = (Get-CatalogueFirstCreated (Get-StepField $full 'doc-up-dim' 'response')); tag = (Get-CatalogueFirstCreated (Get-StepField $full 'doc-up-tag' 'response')); section = (Get-CatalogueFirstCreated (Get-StepField $full 'doc-up-section' 'response')) }
Assert ($upWall -and @($upStale.Values | Where-Object { $_ }).Count -eq 3 -and [string](Get-CatalogueFirst (Get-RequestValue $full 'doc-up-move' 'args.element_uids')) -eq $upWall -and ((@(Get-RequestValue $full 'doc-up-move' 'args.vector_mm') | ForEach-Object { [double]$_ }) -join ',') -eq '0,1000,0') 'P4-11: the update moves its own wall (0, 1000, 0) and has its own stale dimension, tag and section'
foreach ($name in $docUpOrder) {
    foreach ($pass in 'probe', 'confirm', 'delete') {
        $key = "doc-up-$pass-$name"
        $uids = @(Get-RequestValue $full $key 'args.element_uids')
        $dry = Get-RequestValue $full $key 'args.dry_run'
        $dryOk = if ($pass -eq 'delete') { $null -eq $dry } else { $dry -eq $true }
        Assert ($uids.Count -eq 1 -and [string]$uids[0] -eq [string]$upStale[$name] -and $dryOk -and $null -eq (Get-RequestValue $full $key 'args.confirm_dependents')) "P4-11 $key must delete only the stale $name (dry run only before the real delete; nothing to confirm in the clean fake): $($uids -join ',') dry_run=$dry"
    }
}
$upIndex = @{}
foreach ($key in $docUpExpected) { $upIndex[$key] = [int](Get-StepField $full $key 'index') }
Assert ($upIndex['doc-up-move'] -lt $upIndex['doc-up-probe-dimension'] -and $upIndex['doc-up-confirm-section'] -lt $upIndex['doc-up-delete-dimension'] -and $upIndex['doc-up-delete-section'] -lt $upIndex['doc-up-section-new']) 'P4-11 order: move, every dry run, the real deletes (dimension, tag, view), then the re-creations'
$newSection = Get-StepField $full 'doc-up-section-new' 'response'
$newTag = Get-CataloguePath (Get-StepField $full 'doc-up-tag-new' 'response') 'readback.tag'
$oldValue = Get-CataloguePath (Get-StepField $full 'doc-up-dim' 'response') 'readback.dimension.value_mm'
$newValue = Get-CataloguePath (Get-StepField $full 'doc-up-dim-new' 'response') 'readback.dimension.value_mm'
$sectionY = Get-CatalogueFirst (Get-CatalogueArray $newSection 'readback.view.section.origin') 1
Assert ((Get-CatalogueFirstCreated $newSection) -and (Get-CatalogueFirstCreated $newSection) -ne $upStale['section'] -and $null -ne $sectionY -and [Math]::Abs([double]$sectionY + 5000) -lt 0.01) "P4-11: the regenerated section is a new view on the moved wall line (y = -5000): $sectionY"
Assert ((@(Get-CatalogueList $newTag 'tagged_unique_ids') -join ',') -eq $upWall -and ((@(Get-CatalogueMember $newTag 'head') | ForEach-Object { [double]$_ }) -join ',') -eq '-7500,-4500' -and [string](Get-CatalogueMember $newTag 'unique_id') -ne $upStale['tag']) "P4-11: the regenerated tag tags the moved wall at the moved head: $(ConvertTo-CatalogueText $newTag)"
Assert ($null -ne $oldValue -and $null -ne $newValue -and [Math]::Abs([double]$newValue - [double]$oldValue - 1000) -lt 0.01) "P4-11: the regenerated dimension measures 1000 mm more than the stale one: $oldValue -> $newValue"
Assert ([string](Get-RequestValue $full 'doc-up-stale-gone' 'args.unique_id') -eq $upStale['tag']) 'P4-11: the stale tag is read back gone'
foreach ($key in 'doc-up-move', 'doc-up-section-new', 'doc-up-tag-new', 'doc-up-dim-new', 'doc-up-probe-section', 'doc-up-confirm-section', 'doc-up-delete-section') {
    $rows = @(Get-StepField $full $key 'comparisons' | Where-Object { $null -ne $_ })
    Assert ($rows.Count -ge 3 -and @($rows | Where-Object { !$_.ok }).Count -eq 0) "P4-11 $key records requested vs observed rows: $(@($rows | ForEach-Object { $_.check }) -join ' | ')"
}

# Fault cases: dependents injected into the delete readbacks of the update (count and sha256 recomputed as the add-in does).
function Get-TestDependentsSha([string[]]$Uids) {
    $lower = [string[]]@($Uids | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() })
    [Array]::Sort($lower, [StringComparer]::Ordinal)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes(($lower -join "`n")))).ToLowerInvariant()
}
$docUpSeen = @{}
$docUpInject = @{}
$docUpBadSha = @{ On = $false }
# 'step:<key>' names the element that step created (in this run); anything else is a literal UniqueId.
$docUpWrap = {
    param($index, $key, $json, $inner)
    $reply = & $inner $index $key $json
    if ($key -notlike 'doc-up-*') { return $reply }
    $parsed = $reply.output | ConvertFrom-Json
    if ($parsed.status -eq 'succeeded' -and @($parsed.created).Count) { $docUpSeen[$key] = [string]@($parsed.created)[0] }
    if ($docUpInject.ContainsKey($key) -and $parsed.status -eq 'succeeded') {
        $delete = $parsed.readback.delete
        $extra = @($docUpInject[$key] | ForEach-Object { if ([string]$_ -like 'step:*') { $docUpSeen[([string]$_).Substring(5)] } else { [string]$_ } })
        $list = [string[]]@(@(@($delete.dependents) + $extra) | Where-Object { $_ })
        $delete.dependents = $list
        $delete.dependents_count = $list.Count
        $delete.dependents_sha256 = if ($docUpBadSha.On) { '0' * 64 } else { Get-TestDependentsSha $list }
        $reply.output = $parsed | ConvertTo-Json -Depth 20 -Compress
    }
    $reply
}
function Invoke-DocUpFault([hashtable]$Inject, [switch]$BadSha) {
    $docUpSeen.Clear()
    $docUpInject.Clear()
    foreach ($entry in $Inject.GetEnumerator()) { $docUpInject[$entry.Key] = $entry.Value }
    $docUpBadSha.On = [bool]$BadSha
    try { return (Invoke-FakeSequence -All -Wrap $docUpWrap).Result } finally { $docUpInject.Clear(); $docUpBadSha.On = $false }
}
$untracked = 'abcdef01-2345-6789-abcd-ef0123456789-0000beef'
$realDeletes = @($docUpOrder | ForEach-Object { "doc-up-delete-$_" })
function Get-SentAfter($Result, [string]$Key) { @($Result.steps | Where-Object { $_.key -like 'doc-up-*' -and $_.index -gt [int](Get-StepField $Result $Key 'index') -and $null -ne $_.request }) }
# An honest dry run of the view lists its tag and dimension: stale of earlier deletes, dropped from its confirm_dependents.
$honest = Invoke-DocUpFault @{ 'doc-up-probe-section' = @('step:doc-up-tag', 'step:doc-up-dim'); 'doc-up-confirm-section' = @('step:doc-up-tag', 'step:doc-up-dim') }
Assert ($honest.overall.failed -eq 0 -and $null -eq (Get-RequestValue $honest 'doc-up-confirm-section' 'args.confirm_dependents') -and $null -eq (Get-RequestValue $honest 'doc-up-delete-section' 'args.confirm_dependents') -and (Get-StepField $honest 'doc-up-section-new' 'passed')) "P4-11: stale dependents of an earlier delete are dropped from the view's confirm_dependents: $(Get-FailedSteps $honest)"
# Refusals of the first pass: nothing more of the update is sent (no confirm, no real delete), later groups still run.
foreach ($case in @(
        @{ What = 'a kept element (the reference wall)'; Key = 'doc-up-probe-tag'; Inject = @{ 'doc-up-probe-tag' = @('step:doc-up-wall-ref') }; Code = 'dependent_kept' },
        @{ What = 'an untracked element of a tag'; Key = 'doc-up-probe-dimension'; Inject = @{ 'doc-up-probe-dimension' = @($untracked) }; Code = 'dependent_unknown' },
        @{ What = 'the stale view deleted later'; Key = 'doc-up-probe-dimension'; Inject = @{ 'doc-up-probe-dimension' = @('step:doc-up-section') }; Code = 'dependent_planned' },
        @{ What = 'a second dry run with another set'; Key = 'doc-up-confirm-tag'; Inject = @{ 'doc-up-confirm-tag' = @($untracked) }; Code = 'dependents_changed' })) {
    $faulted = Invoke-DocUpFault $case.Inject
    $faultReason = [string](Get-StepField $faulted $case.Key 'reason')
    Assert ($null -ne (Get-StepField $faulted $case.Key 'request') -and !(Get-StepField $faulted $case.Key 'passed') -and $faultReason -like "*$($case.Code)*") "P4-11 $($case.What) must fail $($case.Key) with $($case.Code): $faultReason"
    Assert ((Get-SentAfter $faulted $case.Key).Count -eq 0 -and @($realDeletes | Where-Object { $null -ne (Get-StepField $faulted $_ 'request') }).Count -eq 0) "P4-11 after $($case.Code) no later update step and no real delete is sent: $(@(Get-SentAfter $faulted $case.Key | ForEach-Object { $_.key }) -join ',')"
    Assert ((Get-GroupField $faulted 'modificar' 'failed') -eq 0 -and (Get-GroupField $faulted 'copia' 'failed') -eq 0) "P4-11 later groups still run after $($case.Code): $(Get-FailedSteps $faulted)"
}
# A dry-run readback whose sha256 does not match its dependents is refused before the replan.
$badSha = Invoke-DocUpFault @{ 'doc-up-probe-dimension' = @() } -BadSha
Assert (!(Get-StepField $badSha 'doc-up-probe-dimension' 'passed') -and [string](Get-StepField $badSha 'doc-up-probe-dimension' 'reason') -like '*dependents_sha256*' -and @($realDeletes | Where-Object { $null -ne (Get-StepField $badSha $_ 'request') }).Count -eq 0) "P4-11 a dry-run sha256 mismatch fails the probe: $(Get-StepField $badSha 'doc-up-probe-dimension' 'reason')"
# An untracked dependent of the view (its plan marker) is accepted and confirmed by both the confirm dry run and the delete.
$viewer = Invoke-DocUpFault @{ 'doc-up-probe-section' = @($untracked); 'doc-up-confirm-section' = @($untracked) }
Assert ((Get-StepField $viewer 'doc-up-confirm-section' 'passed') -and (@(Get-RequestValue $viewer 'doc-up-confirm-section' 'args.confirm_dependents') -join ',') -eq $untracked -and (@(Get-RequestValue $viewer 'doc-up-delete-section' 'args.confirm_dependents') -join ',') -eq $untracked) "P4-11 an untracked dependent of the view is accepted and confirmed: $(Get-FailedSteps $viewer)"
# The fake has no such element, so its real delete refuses that confirmation: the re-creations are not sent.
Assert (!(Get-StepField $viewer 'doc-up-delete-section' 'passed') -and @('doc-up-section-new', 'doc-up-tag-new', 'doc-up-dim-new' | Where-Object { $null -ne (Get-StepField $viewer $_ 'request') }).Count -eq 0) "P4-11 a failed real delete stops the re-creations: $(Get-FailedSteps $viewer)"

} finally {
    if ([IO.Path]::GetFileName($inProcessTree).StartsWith('bim-catalogue-session-', [StringComparison]::Ordinal) -and $inProcessTree.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::Ordinal)) { Remove-Item -LiteralPath $inProcessTree -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($failures.Count -and $env:GITHUB_ACTIONS) { '::error title=catalogue session::' + ((@($failures | Select-Object -First 12) | ForEach-Object { $_ -replace '[\r\n]+', ' ' }) -join '%0A') }
if ($failures.Count) { throw ("catalogue session unit checks failed:`n" + ($failures -join "`n")) }
'PASS catalogue session: envelopes, picking, 17-step v1 sequence, one-run groups (all pass, failing group, lost pipe), P1-11 groups (direct shapes, extrusion roofs, move/copy/rotate/mirror, save_copy), P1-14 save_as and the P4-11 two-pass documentation update against the Core contracts and fault detection (in-process fake executor)'

# --- Real CLI and a fake add-in pipe ---
if (!$Dotnet -and !$CliDll) { 'NOTE real CLI pass skipped: run with -Dotnet and -CliDll'; return }
$runDirectory = Join-Path $root ('.bim/native-runs/catalogue-test-' + [guid]::NewGuid())
$work = Join-Path $runDirectory 'work'
New-Item -ItemType Directory -Force $work | Out-Null
$document = Join-Path $runDirectory 'BIM-Agentic-Test.rvt'
[IO.File]::WriteAllBytes($document, [byte[]]@())
$pipeName = 'bim-agentic-os-2025-' + (Get-Random -Minimum 100000 -Maximum 999999)
$ownerFileCreated = Enter-TestOwnerRoot $enabledRootsFile $testOwnerText
$ready = Join-Path $work 'ready.txt'
$log = Join-Path $work 'received.txt'
# .NET pipes on Unix are sockets under the temp directory; keep that path short (108-byte limit) for both processes.
$savedTmp = $env:TMPDIR
if (!$IsWindows) { $env:TMPDIR = '/tmp' }
$server = $null
try {
    $pwsh = (Get-Process -Id $PID).Path
    $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
    foreach ($argument in @('-NoProfile', '-File', "$PSScriptRoot/fake-revit-command-pipe.ps1", '-PipeName', $pipeName, '-DocumentUid', $documentUid, '-ReadyFile', $ready, '-LogFile', $log, '-MaxRequests', '500', '-TimeoutSeconds', '900')) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $server = [Diagnostics.Process]::Start($start)
    $serverOut = $server.StandardOutput.ReadToEndAsync()
    $serverErr = $server.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (!(Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline -and !$server.HasExited) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $ready)) { throw "fake pipe server did not start: $(if ($server.HasExited) { $serverErr.GetAwaiter().GetResult() })" }
    $requests = Join-Path $work 'requests'
    $invoke = { param($index, $key, $json) Invoke-CatalogueCli -Dotnet $Dotnet -CliDll $CliDll -PipeName $pipeName -RequestDirectory $requests -Index $index -Key $key -Json $json -TimeoutSeconds 60 }
    $invokeWire = { param($index, $key, $line) Invoke-CatalogueWire -PipeName $pipeName -Line $line -RequestDirectory $requests -Index $index -Key $key -TimeoutSeconds 60 }
    $real = Invoke-CatalogueSequence -Stamp $stamp -Run 7 -DocumentPath ([IO.Path]::GetFullPath($document)) -DocumentUid $documentUid -Invoke $invoke -InvokeWire $invokeWire
    $notes = ($real.steps | Where-Object { !$_.passed } | ForEach-Object { "[$($_.group)] $($_.key): $($_.reason) $($_.raw_response)" }) -join '; '
    # P1-14: document.save_as takes only a Windows drive path (C:\...\Name.rvt, RevitSaveAsGuard.PathProblem). On
    # Windows (core-windows) the whole one-run passes through the real CLI; elsewhere the CLI refuses both save_as
    # targets as args_invalid path_relative before the pipe, so exactly these five steps fail and nothing else.
    $asKeys = @('c-as-outside', 'c-as-save', 'c-as-save-again', 'c-as-levels-new', 'c-as-levels-previous')
    $realAsSave = [IO.Path]::Combine([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($document)), "BIM-Agentic-Test-c04-$stamp-r7-guardado.rvt")
    # S3: an existing owner file that lists no usable owner folder makes c-as-outside blocked (expected, reported), not failed.
    $ownerBlocked = $null -eq (Get-CatalogueOwnerRoot ([IO.Path]::GetFullPath($document)))
    if ($ownerBlocked) {
        $outsideStep = Get-Step $real 'c-as-outside'
        Assert ($outsideStep.blocked -and $null -eq $outsideStep.request -and [string]$outsideStep.reason -like '*revit-enabled-roots.txt*') "with no usable owner folder c-as-outside is blocked: $($outsideStep.reason)"
        'NOTE c-as-outside blocked as expected: the existing .bim/revit-enabled-roots.txt lists no owner folder outside .bim/native-runs'
    }
    $blockedKeys = @($real.steps | Where-Object { $_.blocked } | ForEach-Object { $_.key })
    Assert (($blockedKeys -join ',') -eq $(if ($ownerBlocked) { 'c-as-outside' } else { '' })) "real CLI blocked steps: $($blockedKeys -join ',')"
    if ($IsWindows) {
        Assert ($real.counts.passed -eq 17 -and $real.overall.failed -eq 0 -and $null -eq $real.aborted -and ($real.all_passed -or $ownerBlocked)) "real CLI one-run: $(Format-CatalogueSummary $real) $notes"
        Assert ((Test-Path -LiteralPath $realAsSave -PathType Leaf) -and [string](Get-CataloguePath (Get-StepField $real 'c-as-save-again' 'response') 'detail') -like 'target_exists:*' -and ($ownerBlocked -or [string](Get-CataloguePath (Get-StepField $real 'c-as-outside' 'response') 'detail') -like 'outside_copies_root:*') -and $real.picked['document_path'] -eq $realAsSave) "the real CLI saves as the new file, refuses the repeat (target_exists) and the owner-folder target (outside_copies_root): $notes"
    }
    else {
        $unexpected = @($real.steps | Where-Object { !$_.passed -and $_.key -notin $asKeys })
        Assert ($real.counts.passed -eq 17 -and $unexpected.Count -eq 0 -and $null -eq $real.aborted) "real CLI one-run outside the save_as steps: $(Format-CatalogueSummary $real) $notes"
        foreach ($key in @(@('c-as-outside', 'c-as-save') | Where-Object { !$ownerBlocked -or $_ -ne 'c-as-outside' })) {
            $refusedReply = Get-StepField $real $key 'response'
            Assert ([string](Get-CatalogueMember $refusedReply 'code') -eq 'args_invalid' -and [string](Get-CatalogueMember $refusedReply 'field') -eq 'args.path' -and [string](Get-CatalogueMember $refusedReply 'detail') -like 'path_relative:*') "off Windows the real CLI refuses the $key POSIX target as path_relative: $(Get-StepField $real $key 'reason')"
        }
        Assert (@($real.steps | Where-Object { $_.key -in $asKeys -and $_.passed }).Count -eq 0 -and $real.picked['document_switched_by'] -eq 'c-save' -and !(Test-Path -LiteralPath $realAsSave) -and (Format-CatalogueSummary $real) -like '*resultado: FALLÓ en copia*') "off Windows no save_as passes or switches the document: $(Format-CatalogueSummary $real)"
    }
    $received = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue)
    $invalid = ($real.steps | Where-Object { $_.key -eq 'invalid-arc' }).operation_id
    $realUp = @($real.steps | Where-Object { $_.key -like 'doc-up-*' })
    Assert ($realUp.Count -eq 19 -and @($realUp | Where-Object { !$_.passed }).Count -eq 0 -and @($realUp | Where-Object { $received -notcontains $_.operation_id }).Count -eq 0) "P4-11: the two-pass documentation update passes through the real CLI, every step reaching the pipe: $(@($realUp | Where-Object { !$_.passed } | ForEach-Object { "$($_.key): $($_.reason)" }) -join '; ')"
    # The client-side refusals (collinear arc, crossed outline, closed roof profile, zero move, full turn, save_copy
    # outside the copies root and over an existing file, save_as into an owner folder and over an existing file) are
    # refused by the real CLI before the pipe (off Windows also both save_as targets, args_invalid).
    $clientSteps = @($real.steps | Where-Object { $_.key -in $clientKeys })
    $notForwarded = @($clientKeys) + $(if ($IsWindows) { @() } else { @('c-as-outside', 'c-as-save') })
    $sentSteps = @($real.steps | Where-Object { $null -ne $_.request })
    $forwardedCount = @($sentSteps | Where-Object { $_.key -notin $notForwarded }).Count
    $expectedSent = $real.overall.total - $(if ($ownerBlocked) { 1 } else { 0 }) - $(if ($IsWindows) { 0 } else { 2 })
    Assert ($received.Count -eq $forwardedCount -and $sentSteps.Count -eq $expectedSent) "pipe must receive every sent step but the client-side refusals ($forwardedCount of $($sentSteps.Count) sent), got $($received.Count)"
    Assert ($received -notcontains $invalid) 'the collinear arc reached the pipe'
    $forwardedIds = @($real.steps | Where-Object { $_.key -notin $clientKeys } | ForEach-Object { $_.operation_id })
    foreach ($step in $clientSteps) { if ($forwardedIds -notcontains $step.operation_id) { Assert ($received -notcontains $step.operation_id) "client-side refusal $($step.key) reached the pipe" } }
    $realCopy = [IO.Path]::Combine([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($document)), "BIM-Agentic-Test-c04-$stamp-r7-copia.rvt")
    Assert ((Test-Path -LiteralPath $realCopy -PathType Leaf) -and [string](Get-CataloguePath (Get-StepField $real 'c-save-again' 'response') 'detail') -like 'target_exists:*' -and [string](Get-CataloguePath (Get-StepField $real 'c-outside' 'response') 'detail') -like 'outside_copies_root:*') 'the real CLI refuses the repeat (target_exists) and the outside target (outside_copies_root)'
    Assert (@($received | Where-Object { $_ -eq 'journal.read' }).Count -eq 5) 'the five journal reads reached the pipe as wire lines'
    Assert (@(Get-ChildItem -LiteralPath $requests -Filter *.json).Count -eq $sentSteps.Count) 'one request file per sent step'
    if ($IsWindows -and !$ownerBlocked) { Assert ((Format-CatalogueSummary $real) -like 'Catálogo: 17/17 pasos OK; grupos: *; resultado: TODO OK') "real CLI summary: $(Format-CatalogueSummary $real)" }
    # P1-15: revit-command --all pages query.elements through the real CLI (one pipe request per page); a non-query
    # envelope, a rejected pipe and an unknown option are refused before the pipe.
    $runAll = {
        param([string]$Name, $Envelope, [string]$Pipe, [string]$Option)
        $file = Join-Path $work ("all-$Name.json")
        [IO.File]::WriteAllText($file, ($Envelope | ConvertTo-Json -Depth 10 -Compress), [Text.UTF8Encoding]::new($false))
        $allStart = [Diagnostics.ProcessStartInfo]::new($Dotnet)
        foreach ($argument in @($CliDll, 'revit-command', $file, $Pipe, $Option)) { $allStart.ArgumentList.Add($argument) }
        $allStart.UseShellExecute = $false
        $allStart.RedirectStandardOutput = $true
        $allStart.RedirectStandardError = $true
        $allProcess = [Diagnostics.Process]::Start($allStart)
        try {
            $allOut = $allProcess.StandardOutput.ReadToEndAsync()
            $null = $allProcess.StandardError.ReadToEndAsync()
            if (!$allProcess.WaitForExit(120000)) { $allProcess.Kill(); return @{ Exit = -1; Json = $null; Raw = 'timeout' } }
            $allRaw = $allOut.GetAwaiter().GetResult()
            $allJson = $null
            try { $allJson = $allRaw | ConvertFrom-Json } catch { $allJson = $null }
            @{ Exit = $allProcess.ExitCode; Json = $allJson; Raw = $allRaw }
        } finally { $allProcess.Dispose() }
    }
    # c-save re-pointed the open document to its copy and, on Windows, c-as-save to the new file (Revit Save As), so
    # later requests name the document the run ended on.
    $allDocument = [ordered]@{ path = $(if ($IsWindows) { $realAsSave } else { $realCopy }); unique_id = $documentUid }
    $wallsAll = & $runAll 'walls' ([ordered]@{ protocol_version = 1; operation_id = 'p115-all-walls'; document = $allDocument; command = 'query.elements'; args = [ordered]@{ category = 'wall'; limit = 1 } }) $pipeName '--all'
    $allIds = @($wallsAll.Json.elements | ForEach-Object { [long]$_.id })
    $allReceived = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Where-Object { $_ -eq 'p115-all-walls' })
    Assert ($wallsAll.Exit -eq 0 -and $wallsAll.Json.status -eq 'succeeded' -and [int]$wallsAll.Json.matched -ge 2 -and [int]$wallsAll.Json.returned -eq [int]$wallsAll.Json.matched -and [int]$wallsAll.Json.pages -eq [int]$wallsAll.Json.matched -and $allReceived.Count -eq [int]$wallsAll.Json.pages -and (($allIds | Sort-Object -Unique) -join ',') -eq ($allIds -join ',')) "revit-command --all pages every wall with limit 1, one pipe request per page: $($wallsAll.Raw)"
    $levelsAll = & $runAll 'levels' ([ordered]@{ protocol_version = 1; operation_id = 'p115-all-levels'; document = $allDocument; command = 'query.levels'; args = [ordered]@{} }) $pipeName '--all'
    Assert ($levelsAll.Exit -eq 1 -and $levelsAll.Json.status -eq 'failed' -and $levelsAll.Json.code -eq 'paging_command_invalid' -and [int]$levelsAll.Json.pages -eq 0 -and @(Get-Content -LiteralPath $log | Where-Object { $_ -eq 'p115-all-levels' }).Count -eq 0) "revit-command --all refuses a non-query envelope before the pipe: $($levelsAll.Raw)"
    $badPipeAll = & $runAll 'bad-pipe' ([ordered]@{ protocol_version = 1; operation_id = 'p115-all-bad-pipe'; document = $allDocument; command = 'query.elements'; args = [ordered]@{ category = 'wall' } }) 'not-a-process-pipe' '--all'
    Assert ($badPipeAll.Exit -eq 1 -and $badPipeAll.Json.code -eq 'explicit_process_pipe_required' -and $null -ne $badPipeAll.Json.PSObject.Properties['pages'] -and [int]$badPipeAll.Json.pages -eq 0) "revit-command --all reports an early refusal in the listing shape: $($badPipeAll.Raw)"
    $unknownOption = & $runAll 'unknown-option' ([ordered]@{ protocol_version = 1; operation_id = 'p115-all-unknown'; document = $allDocument; command = 'query.elements'; args = [ordered]@{ category = 'wall' } }) $pipeName '--everything'
    Assert ($unknownOption.Exit -eq 1 -and $unknownOption.Json.code -eq 'explicit_process_pipe_required' -and $unknownOption.Json.field -eq 'args[3]' -and [string]$unknownOption.Json.detail -like '*--everything*' -and @(Get-Content -LiteralPath $log | Where-Object { $_ -eq 'p115-all-unknown' }).Count -eq 0) "revit-command refuses an unknown option by name: $($unknownOption.Raw)"
    if ($failures.Count -and $server.HasExited) { $failures.Add('fake pipe server stderr: ' + $serverErr.GetAwaiter().GetResult()) }
    # Dead pipe: the add-in is gone, the CLI cannot connect, the run stops after the first step.
    if (!$server.HasExited) { $server.Kill(); $null = $server.WaitForExit(10000) }
    $deadRequests = Join-Path $work 'requests-dead'
    $deadInvoke = { param($index, $key, $json) Invoke-CatalogueCli -Dotnet $Dotnet -CliDll $CliDll -PipeName $pipeName -RequestDirectory $deadRequests -Index $index -Key $key -Json $json -TimeoutSeconds 60 }
    $deadRun = Invoke-CatalogueSequence -Stamp $stamp -Run 8 -DocumentPath ([IO.Path]::GetFullPath($document)) -DocumentUid $documentUid -Invoke $deadInvoke -InvokeWire $invokeWire
    $firstDead = $deadRun.steps[0]
    Assert ($null -ne $deadRun.aborted -and !$firstDead.passed -and $deadRun.overall.not_sent -eq $deadRun.overall.total - 1) "dead pipe must stop the real CLI run after one step: $($deadRun.aborted) / $($firstDead.reason) / $($firstDead.raw_response)"
} finally {
    if ($server) { if (!$server.HasExited) { $server.Kill() }; $server.Dispose() }
    if (!$IsWindows) { $env:TMPDIR = $savedTmp }
    Exit-TestOwnerRoot $ownerFileCreated $enabledRootsFile $testOwnerText
    $boundary = [IO.Path]::GetFullPath((Join-Path $root '.bim/native-runs')) + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($runDirectory)
    if ($target.StartsWith($boundary, [StringComparison]::Ordinal) -and [IO.Path]::GetFileName($target).StartsWith('catalogue-test-', [StringComparison]::Ordinal)) { Remove-Item -LiteralPath $target -Recurse -Force }
}
if ($failures.Count -and $env:GITHUB_ACTIONS) { '::error title=catalogue session CLI::' + ((@($failures | Select-Object -First 12) | ForEach-Object { $_ -replace '[\r\n]+', ' ' }) -join '%0A') }
if ($failures.Count) { throw ("catalogue session CLI checks failed:`n" + ($failures -join "`n")) }
'PASS catalogue session through the real CLI revit-command verb and a fake add-in pipe: v1 17/17 and every one-run group passed (P1-11 included), client-side refusals not forwarded, dead pipe stops the run'
