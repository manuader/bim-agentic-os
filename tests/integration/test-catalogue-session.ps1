# C04 headless checks for scripts/owner-catalogue-steps.ps1 (plain assertions, no Pester).
# Always: envelopes, operation ids, reference picking and the 17-step sequence against an in-process fake executor,
# including fault cases. With -Dotnet and -CliDll: the same sequence through the real CLI "revit-command" verb and a
# fake add-in pipe, proving the envelopes pass the Core schema and guards and that the invalid arc never reaches
# the pipe. Revit itself is never involved; native behaviour stays unverified until the owner session runs.
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
function Invoke-FakeSequence([hashtable]$Faults = @{}) {
    $fake = New-FakeRevitState $documentUid $Faults
    $invoke = { param($index, $key, $json) $text = Invoke-FakeRevitCommand $fake $json; @{ output = $text; exit_code = $(if (($text | ConvertFrom-Json).status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false } }
    $result = Invoke-CatalogueSequence -Stamp $stamp -Run 1 -DocumentPath $documentPath -DocumentUid $documentUid -Invoke $invoke
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
$blocked = Invoke-CatalogueSequence -Stamp $stamp -Run 1 -DocumentPath $documentPath -DocumentUid $documentUid -Invoke $noLevels
Assert ($blocked.counts.passed -eq 0 -and ($blocked.steps | Where-Object { $_.key -eq 'wall-south' }).request -eq $null) 'steps without a level must not be sent'
# Timeout: completion unknown, nothing after it is sent.
$timeout = { param($index, $key, $json) if ($index -eq 6) { @{ output = ''; exit_code = $null; timed_out = $true } } else { $text = Invoke-FakeRevitCommand $fakeForTimeout $json; @{ output = $text; exit_code = 0; timed_out = $false } } }
$fakeForTimeout = New-FakeRevitState $documentUid
$timedOut = Invoke-CatalogueSequence -Stamp $stamp -Run 1 -DocumentPath $documentPath -DocumentUid $documentUid -Invoke $timeout
Assert ($timedOut.counts.passed -eq 5 -and $timedOut.counts.not_sent -eq 11) "timeout must stop the sequence: $($timedOut.counts | ConvertTo-Json -Compress)"
# Evidence must serialize.
$evidence = [ordered]@{ steps = $clean.Result.steps; counts = $clean.Result.counts } | ConvertTo-Json -Depth 20
Assert (($evidence | ConvertFrom-Json).steps.Count -eq 17) 'evidence JSON round trip'

if ($failures.Count) { throw ("catalogue session unit checks failed:`n" + ($failures -join "`n")) }
'PASS catalogue session: envelopes, picking, 17-step sequence and fault detection (in-process fake executor)'

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
    foreach ($argument in @('-NoProfile', '-File', "$PSScriptRoot/fake-revit-command-pipe.ps1", '-PipeName', $pipeName, '-DocumentUid', $documentUid, '-ReadyFile', $ready, '-LogFile', $log)) { $start.ArgumentList.Add($argument) }
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
    $real = Invoke-CatalogueSequence -Stamp $stamp -Run 7 -DocumentPath ([IO.Path]::GetFullPath($document)) -DocumentUid $documentUid -Invoke $invoke
    $notes = ($real.steps | Where-Object { !$_.passed } | ForEach-Object { "$($_.key): $($_.reason) $($_.raw_response)" }) -join '; '
    Assert ($real.counts.passed -eq 17) "real CLI run: $(Format-CatalogueResult $real.counts) $notes"
    $received = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue)
    $invalid = ($real.steps | Where-Object { $_.key -eq 'invalid-arc' }).operation_id
    Assert ($received.Count -eq 16) "pipe must receive 16 requests, got $($received.Count)"
    Assert ($received -notcontains $invalid) 'the collinear arc reached the pipe'
    Assert (@(Get-ChildItem -LiteralPath $requests -Filter *.json).Count -eq 17) 'one request file per step'
    if ($failures.Count -and $server.HasExited) { $failures.Add('fake pipe server stderr: ' + $serverErr.GetAwaiter().GetResult()) }
} finally {
    if ($server) { if (!$server.HasExited) { $server.Kill() }; $server.Dispose() }
    if (!$IsWindows) { $env:TMPDIR = $savedTmp }
    $boundary = [IO.Path]::GetFullPath((Join-Path $root '.bim/native-runs')) + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($runDirectory)
    if ($target.StartsWith($boundary, [StringComparison]::Ordinal) -and [IO.Path]::GetFileName($target).StartsWith('catalogue-test-', [StringComparison]::Ordinal)) { Remove-Item -LiteralPath $target -Recurse -Force }
}
if ($failures.Count) { throw ("catalogue session CLI checks failed:`n" + ($failures -join "`n")) }
'PASS catalogue session through the real CLI revit-command verb and a fake add-in pipe: 17/17, invalid arc not forwarded'
