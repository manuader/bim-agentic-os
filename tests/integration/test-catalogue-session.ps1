# C04 headless checks for scripts/owner-catalogue-steps.ps1 (plain assertions, no Pester).
# Always: envelopes, operation ids, reference picking and the 17-step v1 sequence (-V1Only) against an in-process fake
# executor, including fault cases; then the P1-06 one-run sequence (v1 first and unchanged, then every later group):
# all pass -> per-group counts and summary line; one failing group -> later groups still run and the outcome names it;
# a lost pipe -> nothing more is sent. With -Dotnet and -CliDll: the whole one-run sequence through the real CLI
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
$documentPath = [IO.Path]::GetFullPath((Join-Path $root '.bim/native-runs/catalogue-test/BIM-Agentic-Test.rvt'))

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
# -Wrap (param index, key, json, inner) can stand in front of the fake CLI invoker.
function Invoke-FakeSequence([hashtable]$Faults = @{}, [switch]$All, [switch]$NoWire, [scriptblock]$Wrap) {
    $fake = New-FakeRevitState $documentUid $Faults
    $invoke = { param($index, $key, $json) $text = Invoke-FakeRevitCommand $fake $json; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    $wire = { param($index, $key, $line) $text = Invoke-FakeRevitWire $fake $line; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    if ($Wrap) { $inner = $invoke; $invoke = { param($index, $key, $json) & $Wrap $index $key $json $inner } }
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
$groupKeys = @('v1', 'v2', 'vistas', 'snapshot', 'consultas', 'estructura', 'documentacion', 'diario', 'modificar')
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
# Session totals over several runs.
$joined = Join-CatalogueResults @($full, $roofFails)
Assert ($joined.counts.passed -eq 34 -and (Get-Group $joined 'v2').failed -eq 1 -and $joined.overall.total -eq 2 * $full.overall.total -and !$joined.all_passed) 'session totals sum the runs by group'

if ($failures.Count) { throw ("catalogue session unit checks failed:`n" + ($failures -join "`n")) }
'PASS catalogue session: envelopes, picking, 17-step v1 sequence, one-run groups (all pass, failing group, lost pipe) and fault detection (in-process fake executor)'

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
    Assert ($received.Count -eq $real.overall.total - 1) "pipe must receive every step but the invalid arc ($($real.overall.total - 1)), got $($received.Count)"
    Assert ($received -notcontains $invalid) 'the collinear arc reached the pipe'
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
if ($failures.Count) { throw ("catalogue session CLI checks failed:`n" + ($failures -join "`n")) }
'PASS catalogue session through the real CLI revit-command verb and a fake add-in pipe: v1 17/17 and every one-run group passed, invalid arc not forwarded, dead pipe stops the run'
