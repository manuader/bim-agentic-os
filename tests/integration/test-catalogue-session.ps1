# C04 headless checks for scripts/owner-catalogue-steps.ps1 (plain assertions, no Pester).
# Always: envelopes, operation ids, reference picking and the 17-step v1 sequence (-V1Only) against an in-process fake
# executor, including fault cases; then the P1-06 one-run sequence (v1 first and unchanged, then every later group):
# all pass -> per-group counts and summary line; one failing group -> later groups still run and the outcome names it;
# a lost pipe -> nothing more is sent. P1-11 (formas, extrusion, transformar, copia, before the destructive group):
# every step checked against the Core contracts read from core/Bim.Core (catalogue command, refusal code, client-side
# code, save_copy reason, ReuseOf), exact arguments and outcomes, ordering and dependencies, the document path switch to
# the copy, no path outside .bim (no copy of a non-test document) and fault cases. Runs in CI through
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
# Its own folder under .bim/native-runs: the fake writes the document.save_copy copy next to the test document.
$inProcessDirectory = [IO.Path]::GetFullPath((Join-Path $root ('.bim/native-runs/catalogue-test-' + [guid]::NewGuid())))
New-Item -ItemType Directory -Force $inProcessDirectory | Out-Null
$documentPath = Join-Path $inProcessDirectory 'BIM-Agentic-Test.rvt'
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
Assert ($destructive.Count -ge 5 -and @($destructive | Where-Object { $_.group -ne 'modificar' }).Count -eq 0) 'modify/delete steps only in the last group'
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
Assert ((Get-Group $sectionFails 'vistas').failed -eq 1 -and $sectionFails.overall.failed -eq 1 -and (Format-CatalogueSummary $sectionFails) -like '*resultado: FALLÓ en vistas') "failing section: $(Get-FailedSteps $sectionFails)"
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
Assert ($codes.Contains('element_not_rotatable') -and $codes.Contains('document_conflict') -and $clientSide.Contains('arc_collinear') -and $clientSide.Contains('save_copy_refused') -and !$clientSide.Contains('reference_not_found')) "refusal codes parsed from core/Bim.Core: $($codes.Count) codes, $($clientSide.Count) client-side"
Assert ($copyReasons.Count -eq 9 -and $copyReasons.Contains('outside_copies_root') -and $copyReasons.Contains('target_exists')) "save_copy reasons parsed from core/Bim.Core: $(@($copyReasons) -join ',')"

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
        if ($spec.ContainsKey('Reason')) { Assert ($spec.Command -eq 'document.save_copy' -and $spec.Expect.Code -eq 'save_copy_refused' -and $copyReasons.Contains([string]$spec.Reason)) "[$($group.Key)] ${key}: save_copy reason $($spec.Reason) is not a RevitSaveCopyGuard reason" }
        if ($spec.ContainsKey('ReuseOf')) { Assert ($seen.ContainsKey($spec.ReuseOf) -and $seen[$spec.ReuseOf] -eq $spec.Command) "[$($group.Key)] ${key}: ReuseOf $($spec.ReuseOf) must be an earlier step of the same command" }
        Assert (!$seen.ContainsKey($key)) "duplicate step key $key"
        $seen[$key] = $spec.Command
    }
}
Assert (($clientKeys -join ',') -eq 'invalid-arc,f-bowtie,x-closed,t-move-zero,t-rotate-full,c-outside,c-save-again') "client-side refusal steps: $($clientKeys -join ',')"

# The new groups: steps in order with their exact outcome (ok or the typed refusal code), each passing in the clean run.
$p111Groups = [ordered]@{
    formas = @('f-box', 'f-extrusion', 'f-bowtie')
    extrusion = @('x-roof-types', 'x-barrel', 'x-barrel-reversed', 'x-barrel-diagonal', 'x-closed')
    transformar = @('t-wall', 't-move', 't-move-retry', 't-move-zero', 't-copy', 't-rotate', 't-rotate-door', 't-rotate-full', 't-mirror')
    copia = @('c-outside', 'c-save', 'c-save-again', 'c-levels-copy', 'c-levels-source')
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
}
foreach ($entry in $p111Outcomes.GetEnumerator()) {
    $step = Get-Step $full $entry.Key
    $code = [string](Get-CatalogueMember (Get-CatalogueMember $step 'response') 'code')
    Assert ($null -ne $step -and $step.passed -and $code -eq $entry.Value) "P1-11 step $($entry.Key) must pass answering $($entry.Value): $(if ($step) { "$code / $($step.reason)" } else { 'missing' })"
}
foreach ($command in @('direct_shape.create', 'roof.create_extrusion', 'element.move', 'element.copy', 'element.rotate', 'element.mirror', 'document.save_copy')) {
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
$outsidePath = [IO.Path]::Combine([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($documentPath))), "BIM-Agentic-Test-c04-$stamp-r1-fuera.rvt")
Assert ((Get-RequestValue $full 'c-save' 'args.path') -eq $copyPath -and (Get-RequestValue $full 'c-save-again' 'args.path') -eq $copyPath -and (Get-RequestValue $full 'c-outside' 'args.path') -eq $outsidePath) "save_copy targets: $(Get-RequestValue $full 'c-save' 'args.path') / $(Get-RequestValue $full 'c-outside' 'args.path')"
Assert ((Get-StepField $full 'c-save-again' 'operation_id') -eq (Get-StepField $full 'c-save' 'operation_id')) 'the repeat reuses the save operation_id (refused target_exists, not replayed)'
foreach ($key in 'c-outside', 'c-save-again') { Assert (@(Get-StepField $full $key 'comparisons' | Where-Object { $null -ne $_ -and $_.ok }).Count -eq 2) "$key checks the refused field and reason" }
Assert ([string](Get-CataloguePath (Get-StepField $full 'c-outside' 'response') 'detail') -like 'outside_copies_root:*' -and [string](Get-CataloguePath (Get-StepField $full 'c-save-again' 'response') 'detail') -like 'target_exists:*') 'save_copy refusals carry their reason prefix'
$saveChecks = @(Get-StepField $full 'c-save' 'comparisons' | Where-Object { $null -ne $_ })
Assert (@($saveChecks | Where-Object { $_.check -like '*sha256*' -and $_.ok }).Count -ge 2 -and @($saveChecks | Where-Object { !$_.ok }).Count -eq 0) 'c-save compares the read sha256 with the file on disk'
Assert ((Test-Path -LiteralPath $copyPath -PathType Leaf) -and !(Test-Path -LiteralPath $outsidePath)) 'the copy exists next to the test document and nothing was written outside .bim/native-runs'
$saveIndex = [int](Get-StepField $full 'c-save' 'index')
$wrongDocument = @($full.steps | Where-Object { $null -ne $_.request } | Where-Object {
    $named = if ($_.command -eq 'journal.read') { [string](Get-CatalogueMember $_.request 'DocumentPath') } else { [string](Get-CataloguePath $_.request 'document.path') }
    $wanted = if ($_.index -gt $saveIndex -and $_.key -notin 'c-save-again', 'c-levels-source') { $copyPath } else { $documentPath }
    $named -ne $wanted })
Assert ($wrongDocument.Count -eq 0) "document.path per step (the source until c-save, then the copy): $(@($wrongDocument | ForEach-Object { $_.key }) -join ',')"
$bimRoot = [IO.Path]::GetFullPath((Join-Path $root '.bim')) + [IO.Path]::DirectorySeparatorChar
$runsRoot = [IO.Path]::GetFullPath((Join-Path $root '.bim/native-runs')) + [IO.Path]::DirectorySeparatorChar
$pathsNamed = @($full.steps | Where-Object { $null -ne $_.request -and $_.command -ne 'journal.read' } | ForEach-Object { [string](Get-CataloguePath $_.request 'document.path'); if ($_.command -eq 'document.save_copy') { [string](Get-CataloguePath $_.request 'args.path') } })
$badPaths = @($pathsNamed | Where-Object { !([IO.Path]::GetFullPath($_)).StartsWith($bimRoot, [StringComparison]::Ordinal) -or [IO.Path]::GetFileName($_) -cnotmatch '\ABIM-Agentic-Test[A-Za-z0-9._-]*\.rvt\z' -or (!$_.StartsWith($runsRoot, [StringComparison]::Ordinal) -and $_ -ne $outsidePath) })
Assert ($pathsNamed.Count -gt 0 -and $badPaths.Count -eq 0) "every path a request names is a BIM-Agentic-Test*.rvt under .bim/native-runs (the refused outside target under .bim): $($badPaths -join ', ')"

# A document that is not a test copy under .bim/native-runs is never saved as a copy: the copy steps are not sent.
foreach ($notCopy in @([IO.Path]::GetFullPath((Join-Path $root 'Proyectos/Cliente/Modelo.rvt')), [IO.Path]::GetFullPath((Join-Path $root 'otra-carpeta/BIM-Agentic-Test.rvt')))) {
    $otherFake = New-FakeRevitState $documentUid
    $otherInvoke = { param($index, $key, $json) $text = Invoke-FakeRevitCommand $otherFake $json; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    $other = Invoke-CatalogueSequence -Stamp $stamp -Run 1 -DocumentPath $notCopy -DocumentUid $documentUid -Invoke $otherInvoke
    $copySteps = @($other.steps | Where-Object { $_.group -eq 'copia' })
    Assert ($copySteps.Count -eq 5 -and @($copySteps | Where-Object { $null -ne $_.request }).Count -eq 0 -and [string](Get-StepField $other 'c-save' 'reason') -like '*no es una copia de prueba*') "no save_copy for $notCopy : $(@($copySteps | ForEach-Object { "$($_.key)=$($_.reason)" }) -join '; ')"
}

# Faults the checks must notice.
$hashOff = (Invoke-FakeSequence @{ SaveCopyHashOff = $true } -All).Result
Assert (!(Get-StepField $hashOff 'c-save' 'passed') -and [string](Get-StepField $hashOff 'c-save' 'reason') -like '*sha256*' -and $null -eq (Get-StepField $hashOff 'c-save-again' 'request')) "a read sha256 that differs from the file must fail c-save: $(Get-StepField $hashOff 'c-save' 'reason')"
Assert ((Get-StepField $hashOff 'c-levels-copy' 'passed') -and (Get-StepField $hashOff 'c-levels-source' 'passed') -and (Get-GroupField $hashOff 'modificar' 'failed') -eq 0) "Revit re-pointed the document anyway: later steps name the copy: $(Get-FailedSteps $hashOff)"
Assert ($hashOff.picked['document_switched_by'] -eq 'c-save' -and $full.picked['document_switched_by'] -eq 'c-save') 'the evidence records which step switched to the copy'
# A save refused before Save As: the probe naming the copy is refused (document_conflict), nothing switches, the
# destructive group runs on the source.
$noCopy = (Invoke-FakeSequence @{ FailCommands = @('document.save_copy') } -All).Result
Assert (!(Get-StepField $noCopy 'c-save' 'passed') -and @('c-save-again', 'c-levels-source' | Where-Object { $null -ne (Get-StepField $noCopy $_ 'request') }).Count -eq 0) "a failed save sends no step that needs the copy: $(Get-FailedSteps $noCopy)"
Assert ((Get-RequestValue $noCopy 'c-levels-copy' 'document.path') -eq $copyPath -and !(Get-StepField $noCopy 'c-levels-copy' 'passed') -and [string](Get-CataloguePath (Get-StepField $noCopy 'c-levels-copy' 'response') 'code') -eq 'document_conflict' -and $null -eq $noCopy.picked['document_path']) "the probe names the copy and is refused when Revit never saved it: $(Get-StepField $noCopy 'c-levels-copy' 'reason')"
Assert ((Get-GroupField $noCopy 'modificar' 'failed') -eq 0 -and (Get-RequestValue $noCopy 'm-delete' 'document.path') -eq $documentPath) 'without a copy the destructive group still runs on the source'
# A save that fails after Save As already re-pointed the document (CheckWritten or readback failure): the probe
# naming the copy succeeds, the run switches to the copy and records it, the destructive group runs on the copy.
$afterSave = (Invoke-FakeSequence @{ SaveCopyFailsAfterSave = $true } -All).Result
Assert (!(Get-StepField $afterSave 'c-save' 'passed') -and [string](Get-CataloguePath (Get-StepField $afterSave 'c-save' 'response') 'code') -eq 'transaction_failed' -and $null -eq (Get-StepField $afterSave 'c-save-again' 'request')) "a save failing after Save As fails c-save: $(Get-StepField $afterSave 'c-save' 'reason')"
Assert ((Get-StepField $afterSave 'c-levels-copy' 'passed') -and $afterSave.picked['document_path'] -eq $copyPath -and $afterSave.picked['document_switched_by'] -eq 'c-levels-copy' -and @(Get-StepField $afterSave 'c-levels-copy' 'comparisons' | Where-Object { $null -ne $_ -and $_.check -like '*cambia a la copia*' -and $_.ok }).Count -eq 1) "the probe switches the run to the copy and records it: $(Get-FailedSteps $afterSave)"
Assert ((Get-StepField $afterSave 'c-levels-source' 'passed') -and (Get-GroupField $afterSave 'modificar' 'failed') -eq 0 -and (Get-RequestValue $afterSave 'm-delete' 'document.path') -eq $copyPath -and $afterSave.overall.failed -eq 2) "after the switch the destructive group runs on the copy: $(Get-FailedSteps $afterSave)"
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

} finally {
    $boundary0 = [IO.Path]::GetFullPath((Join-Path $root '.bim/native-runs')) + [IO.Path]::DirectorySeparatorChar
    if ($inProcessDirectory.StartsWith($boundary0, [StringComparison]::Ordinal) -and [IO.Path]::GetFileName($inProcessDirectory).StartsWith('catalogue-test-', [StringComparison]::Ordinal)) { Remove-Item -LiteralPath $inProcessDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($failures.Count -and $env:GITHUB_ACTIONS) { '::error title=catalogue session::' + ((@($failures | Select-Object -First 12) | ForEach-Object { $_ -replace '[\r\n]+', ' ' }) -join '%0A') }
if ($failures.Count) { throw ("catalogue session unit checks failed:`n" + ($failures -join "`n")) }
'PASS catalogue session: envelopes, picking, 17-step v1 sequence, one-run groups (all pass, failing group, lost pipe), P1-11 groups (direct shapes, extrusion roofs, move/copy/rotate/mirror, save_copy) against the Core contracts and fault detection (in-process fake executor)'

# --- Real CLI and a fake add-in pipe ---
if (!$Dotnet -and !$CliDll) { 'NOTE real CLI pass skipped: run with -Dotnet and -CliDll'; return }
$runDirectory = Join-Path $root ('.bim/native-runs/catalogue-test-' + [guid]::NewGuid())
$work = Join-Path $runDirectory 'work'
New-Item -ItemType Directory -Force $work | Out-Null
$document = Join-Path $runDirectory 'BIM-Agentic-Test.rvt'
[IO.File]::WriteAllBytes($document, [byte[]]@())
$pipeName = 'bim-agentic-os-2025-' + (Get-Random -Minimum 100000 -Maximum 999999)
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
    Assert ($real.counts.passed -eq 17 -and $real.all_passed) "real CLI one-run: $(Format-CatalogueSummary $real) $notes"
    $received = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue)
    $invalid = ($real.steps | Where-Object { $_.key -eq 'invalid-arc' }).operation_id
    # The client-side refusals (collinear arc, crossed outline, closed roof profile, zero move, full turn, save_copy
    # outside the copies root and over an existing file) are refused by the real CLI before the pipe.
    $clientSteps = @($real.steps | Where-Object { $_.key -in $clientKeys })
    Assert ($clientSteps.Count -eq $clientKeys.Count -and $received.Count -eq $real.overall.total - $clientSteps.Count) "pipe must receive every step but the $($clientSteps.Count) client-side refusals ($($real.overall.total - $clientSteps.Count)), got $($received.Count)"
    Assert ($received -notcontains $invalid) 'the collinear arc reached the pipe'
    $forwardedIds = @($real.steps | Where-Object { $_.key -notin $clientKeys } | ForEach-Object { $_.operation_id })
    foreach ($step in $clientSteps) { if ($forwardedIds -notcontains $step.operation_id) { Assert ($received -notcontains $step.operation_id) "client-side refusal $($step.key) reached the pipe" } }
    $realCopy = [IO.Path]::Combine([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($document)), "BIM-Agentic-Test-c04-$stamp-r7-copia.rvt")
    Assert ((Test-Path -LiteralPath $realCopy -PathType Leaf) -and [string](Get-CataloguePath (Get-StepField $real 'c-save-again' 'response') 'detail') -like 'target_exists:*' -and [string](Get-CataloguePath (Get-StepField $real 'c-outside' 'response') 'detail') -like 'outside_copies_root:*') 'the real CLI refuses the repeat (target_exists) and the outside target (outside_copies_root)'
    Assert (@($received | Where-Object { $_ -eq 'journal.read' }).Count -eq 5) 'the five journal reads reached the pipe as wire lines'
    Assert (@(Get-ChildItem -LiteralPath $requests -Filter *.json).Count -eq $real.overall.total) 'one request file per step'
    Assert ((Format-CatalogueSummary $real) -like 'Catálogo: 17/17 pasos OK; grupos: *; resultado: TODO OK') "real CLI summary: $(Format-CatalogueSummary $real)"
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
    $boundary = [IO.Path]::GetFullPath((Join-Path $root '.bim/native-runs')) + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($runDirectory)
    if ($target.StartsWith($boundary, [StringComparison]::Ordinal) -and [IO.Path]::GetFileName($target).StartsWith('catalogue-test-', [StringComparison]::Ordinal)) { Remove-Item -LiteralPath $target -Recurse -Force }
}
if ($failures.Count -and $env:GITHUB_ACTIONS) { '::error title=catalogue session CLI::' + ((@($failures | Select-Object -First 12) | ForEach-Object { $_ -replace '[\r\n]+', ' ' }) -join '%0A') }
if ($failures.Count) { throw ("catalogue session CLI checks failed:`n" + ($failures -join "`n")) }
'PASS catalogue session through the real CLI revit-command verb and a fake add-in pipe: v1 17/17 and every one-run group passed (P1-11 included), client-side refusals not forwarded, dead pipe stops the run'
