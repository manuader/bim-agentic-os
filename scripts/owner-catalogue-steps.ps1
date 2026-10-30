# C04 owner smoke of Revit command catalogue v1: the scripted step list, envelope building, picking references from
# query results and checking each response. Dot-sourced by scripts/owner-native-session.ps1 -Catalogue (which passes
# an invoker that runs the CLI "revit-command" verb against the child Revit's pipe) and by
# tests/integration/test-catalogue-session.ps1 (which passes fakes). Everything here except Invoke-CatalogueCli is
# pure; nothing here starts, stops or saves Revit.
Set-StrictMode -Version Latest

$script:CatalogueOperationIdPattern = '\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z'
# Plan geometry in millimetres (Revit internal origin). Walls run counter-clockwise around the 6000 x 4000 rectangle.
$script:CatalogueRectangle = @(@(0, 0), @(6000, 0), @(6000, 4000), @(0, 4000))
$script:CatalogueArcCenter = @(10000, 0)
$script:CatalogueWallHeightMm = 3000

# Member of a parsed JSON object, or $null when absent (safe under Set-StrictMode).
function Get-CatalogueMember($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# Array member without nulls; always an array (possibly empty).
function Get-CatalogueList($Object, [string]$Name) {
    $value = Get-CatalogueMember $Object $Name
    return , @(@($value) | Where-Object { $null -ne $_ })
}

function New-CatalogueOperationId([string]$Stamp, [int]$Run, [string]$Key) {
    $id = "c04-$Stamp-r$Run-$Key"
    if ($id -notmatch $script:CatalogueOperationIdPattern) { throw "operation id not valid for the protocol: $id" }
    return $id
}

# The ADR0009 request envelope as compact JSON. $Arguments is a dictionary (ordered for readable evidence).
function New-CatalogueEnvelope([string]$OperationId, [string]$DocumentPath, [string]$DocumentUid, [string]$Command, $Arguments) {
    if ($null -eq $Arguments) { $Arguments = [ordered]@{} }
    $envelope = [ordered]@{
        protocol_version = 1
        operation_id = $OperationId
        document = [ordered]@{ path = $DocumentPath; unique_id = $DocumentUid }
        command = $Command
        args = $Arguments
    }
    return ($envelope | ConvertTo-Json -Depth 12 -Compress)
}

function New-CatalogueLineWall([string]$Level, [string]$Type, $Start, $End) {
    [ordered]@{ level = $Level; type = $Type; height_mm = $script:CatalogueWallHeightMm; curve = [ordered]@{ kind = 'line'; start = @($Start[0], $Start[1]); end = @($End[0], $End[1]) } }
}

function New-CatalogueArcWall([string]$Level, [string]$Type, [double]$RadiusMm) {
    [ordered]@{ level = $Level; type = $Type; height_mm = $script:CatalogueWallHeightMm; curve = [ordered]@{ kind = 'arc'; center = @($script:CatalogueArcCenter[0], $script:CatalogueArcCenter[1]); radius_mm = $RadiusMm; start_angle_deg = 0; end_angle_deg = 90 } }
}

# --- Picking references from query responses (never hard-coded names) ---
function Select-CatalogueLevel($Response) {
    $levels = Get-CatalogueList (Get-CatalogueMember $Response 'readback') 'levels'
    if (!$levels.Count) { throw 'el documento no tiene niveles' }
    return [string](Get-CatalogueMember $levels[0] 'unique_id')
}

function Select-CatalogueWallType($Response) {
    $all = Get-CatalogueList (Get-CatalogueMember $Response 'readback') 'wall_types'
    $basic = @($all | Where-Object { (Get-CatalogueMember $_ 'kind') -eq 'Basic' })
    if (!$basic.Count) { throw 'no hay ningún tipo de muro básico (Basic) en el documento' }
    return [string](Get-CatalogueMember $basic[0] 'unique_id')
}

# query.floor_types already leaves out foundation slabs (executor rule); the name check is a second fence.
function Select-CatalogueFloorType($Response) {
    $all = Get-CatalogueList (Get-CatalogueMember $Response 'readback') 'floor_types'
    $types = @($all | Where-Object { [string](Get-CatalogueMember $_ 'name') -notmatch '(?i)foundation|cimentaci' })
    if (!$types.Count) { throw 'no hay ningún tipo de losa que no sea de fundación en el documento' }
    return [string](Get-CatalogueMember $types[0] 'unique_id')
}

function Select-CatalogueHostedSymbol($Response, [string]$Category) {
    $readback = Get-CatalogueMember $Response 'readback'
    if ((Get-CatalogueMember $readback 'category') -ne $Category) { throw "la consulta de familias respondió para otra categoría" }
    $all = Get-CatalogueList $readback 'family_symbols'
    $hosted = @($all | Where-Object { (Get-CatalogueMember $_ 'wall_hosted') -eq $true })
    if (!$hosted.Count) { $nombre = if ($Category -eq 'door') { 'puerta' } else { 'ventana' }; throw "no hay cargado ningún tipo de $nombre alojado en muro" }
    return [string](Get-CatalogueMember $hosted[0] 'unique_id')
}

function Test-CatalogueNear($Actual, [double]$Expected, [double]$Tolerance) {
    return ($null -ne $Actual) -and ($Actual -is [ValueType]) -and ([Math]::Abs([double]$Actual - $Expected) -le $Tolerance)
}

# $null when the response meets the expectation, otherwise the reason. $Expect: Status ('succeeded'|'failed'),
# Code (failures), Replayed (successes), Created (count of created elements on a first-run write).
function Test-CatalogueResponse($Response, $ExitCode, [string]$OperationId, [hashtable]$Expect) {
    if ($null -eq $Response) { return 'no JSON response' }
    $status = Get-CatalogueMember $Response 'status'
    $code = Get-CatalogueMember $Response 'code'
    $receipt = Get-CatalogueMember $Response 'receipt'
    $created = Get-CatalogueList $Response 'created'
    if ($Expect.Status -eq 'failed') {
        if ($status -ne 'failed') { return "expected failure $($Expect.Code), got status $status" }
        if ($code -ne $Expect.Code) { return "expected code $($Expect.Code), got $code" }
        if ($ExitCode -eq 0) { return 'failure reported with exit code 0' }
        if ($created.Count) { return 'a refused request reported created elements' }
        if ($null -ne $receipt) { return 'a refused request carried a receipt' }
        return $null
    }
    if ($status -ne 'succeeded' -or $code -ne 'ok') { return "expected success, got ${status}/${code}: $(Get-CatalogueMember $Response 'detail')" }
    if ($ExitCode -ne 0) { return "success reported with exit code $ExitCode" }
    if ($null -eq $receipt) { return 'success without receipt' }
    if ((Get-CatalogueMember $receipt 'operation_id') -ne $OperationId) { return 'receipt answers another operation_id' }
    $replayed = [bool]$Expect['Replayed']
    if ((Get-CatalogueMember $receipt 'replayed') -ne $replayed) { return "expected replayed=$replayed" }
    if ($Expect.ContainsKey('Created') -and $created.Count -ne $Expect.Created) { return "expected $($Expect.Created) created element(s), got $($created.Count)" }
    return $null
}

# The fixed C04 step list. Build returns the args dictionary (throws when a dependency is missing); Check returns
# $null or a reason and records picks. Both run inside Invoke-CatalogueSequence, which owns $picked.
function Get-CatalogueSteps {
    $rect = $script:CatalogueRectangle
    $sides = @(
        @{ Key = 'wall-south'; Es = 'Muro recto 1 de 4 (sur)'; From = $rect[0]; To = $rect[1] },
        @{ Key = 'wall-east'; Es = 'Muro recto 2 de 4 (este)'; From = $rect[1]; To = $rect[2] },
        @{ Key = 'wall-north'; Es = 'Muro recto 3 de 4 (norte)'; From = $rect[2]; To = $rect[3] },
        @{ Key = 'wall-west'; Es = 'Muro recto 4 de 4 (oeste)'; From = $rect[3]; To = $rect[0] })
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'query-levels'; Es = 'Consultar niveles'; Command = 'query.levels'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }; Check = { param($r) $picked['level'] = Select-CatalogueLevel $r; $null } })
    $steps.Add(@{ Key = 'query-wall-types'; Es = 'Consultar tipos de muro'; Command = 'query.wall_types'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }; Check = { param($r) $picked['wall_type'] = Select-CatalogueWallType $r; $null } })
    $steps.Add(@{ Key = 'query-floor-types'; Es = 'Consultar tipos de suelo'; Command = 'query.floor_types'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }; Check = { param($r) $picked['floor_type'] = Select-CatalogueFloorType $r; $null } })
    $steps.Add(@{ Key = 'query-doors'; Es = 'Consultar puertas cargadas'; Command = 'query.family_symbols'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ category = 'door' } }; Check = { param($r) $picked['door'] = Select-CatalogueHostedSymbol $r 'door'; $null } })
    $steps.Add(@{ Key = 'query-windows'; Es = 'Consultar ventanas cargadas'; Command = 'query.family_symbols'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ category = 'window' } }; Check = { param($r) $picked['window'] = Select-CatalogueHostedSymbol $r 'window'; $null } })
    foreach ($side in $sides) {
        $steps.Add(@{ Key = $side.Key; Es = $side.Es; Command = 'wall.create'; Expect = @{ Status = 'succeeded'; Created = 1 }; Side = $side
            Build = { Need 'level', 'wall_type'; New-CatalogueLineWall $picked['level'] $picked['wall_type'] $spec.Side.From $spec.Side.To }
            Check = {
                param($r)
                $wall = Get-CatalogueMember (Get-CatalogueMember $r 'readback') 'wall'
                if ((Get-CatalogueMember (Get-CatalogueMember $wall 'curve') 'kind') -ne 'line') { return 'readback is not a straight wall' }
                $uid = (Get-CatalogueList $r 'created')[0]
                if ((Get-CatalogueMember $wall 'unique_id') -ne $uid) { return 'readback wall is not the created element' }
                $picked[$spec.Key] = $uid; $null
            } })
    }
    $steps.Add(@{ Key = 'wall-arc'; Es = 'Muro curvo (radio 3000 mm, de 0 a 90 grados)'; Command = 'wall.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'level', 'wall_type'; New-CatalogueArcWall $picked['level'] $picked['wall_type'] 3000 }
        Check = {
            param($r)
            $curve = Get-CatalogueMember (Get-CatalogueMember (Get-CatalogueMember $r 'readback') 'wall') 'curve'
            if ((Get-CatalogueMember $curve 'kind') -ne 'arc') { return 'readback is not a curved wall' }
            if (!(Test-CatalogueNear (Get-CatalogueMember $curve 'radius_mm') 3000 0.01)) { return 'readback radius is not 3000 mm' }
            $picked['wall-arc'] = (Get-CatalogueList $r 'created')[0]; $null
        } })
    $steps.Add(@{ Key = 'door'; Es = 'Puerta en el muro sur'; Command = 'opening.place'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'door', 'wall-south'; [ordered]@{ category = 'door'; symbol = $picked['door']; host_wall_uid = $picked['wall-south']; position_along_mm = 1500 } }
        Check = {
            param($r)
            $opening = Get-CatalogueMember (Get-CatalogueMember $r 'readback') 'opening'
            if ((Get-CatalogueMember $opening 'host_wall_unique_id') -ne $picked['wall-south']) { return 'door is not hosted by the south wall' }
            $null
        } })
    $steps.Add(@{ Key = 'window'; Es = 'Ventana (antepecho 900 mm) en el muro este'; Command = 'opening.place'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'window', 'wall-east'; [ordered]@{ category = 'window'; symbol = $picked['window']; host_wall_uid = $picked['wall-east']; position_along_mm = 2000; sill_mm = 900 } }
        Check = {
            param($r)
            $opening = Get-CatalogueMember (Get-CatalogueMember $r 'readback') 'opening'
            if ((Get-CatalogueMember $opening 'host_wall_unique_id') -ne $picked['wall-east']) { return 'window is not hosted by the east wall' }
            if (!(Test-CatalogueNear (Get-CatalogueMember $opening 'sill_mm') 900 0.01)) { return 'readback sill is not 900 mm' }
            $null
        } })
    $steps.Add(@{ Key = 'floor'; Es = 'Suelo del rectángulo de 6000 x 4000 mm'; Command = 'floor.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'level', 'floor_type'; [ordered]@{ level = $picked['level']; type = $picked['floor_type']; boundary = @($script:CatalogueRectangle | ForEach-Object { , @($_[0], $_[1]) }) } }
        Check = {
            param($r)
            $floor = Get-CatalogueMember (Get-CatalogueMember $r 'readback') 'floor'
            if ((Get-CatalogueMember $floor 'unique_id') -ne (Get-CatalogueList $r 'created')[0]) { return 'readback floor is not the created element' }
            $area = Get-CatalogueMember $floor 'area_mm2'
            if ($null -ne $area -and !(Test-CatalogueNear $area 24000000 24000)) { return "floor area $area mm2 is not 24 m2" }
            $null
        } })
    $steps.Add(@{ Key = 'room'; Es = 'Habitación "Prueba" dentro del rectángulo'; Command = 'room.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'level'; [ordered]@{ level = $picked['level']; point = @(3000, 2000); name = 'Prueba' } }
        Check = {
            param($r)
            $room = Get-CatalogueMember (Get-CatalogueMember $r 'readback') 'room'
            if ((Get-CatalogueMember $room 'name') -ne 'Prueba') { return 'room name is not Prueba' }
            if ((Get-CatalogueMember $room 'unique_id') -ne (Get-CatalogueList $r 'created')[0]) { return 'readback room is not the created element' }
            $null
        } })
    # Same operation_id and same request: the stored answer comes back with replayed=true and the same element, so
    # no second wall exists. ReuseOf names the step whose operation_id is reused.
    $steps.Add(@{ Key = 'wall-arc-retry'; Es = 'Repetir el muro curvo con el mismo identificador (no debe duplicarse)'; Command = 'wall.create'; ReuseOf = 'wall-arc'; Expect = @{ Status = 'succeeded'; Replayed = $true; Created = 1 }
        Build = { Need 'wall-arc'; New-CatalogueArcWall $picked['level'] $picked['wall_type'] 3000 }
        Check = {
            param($r)
            if ((Get-CatalogueList $r 'created')[0] -ne $picked['wall-arc']) { return 'the retry reported a different element: a duplicate wall may exist' }
            $null
        } })
    $steps.Add(@{ Key = 'wall-arc-conflict'; Es = 'Mismo identificador con otro radio (debe rechazarse)'; Command = 'wall.create'; ReuseOf = 'wall-arc'; Expect = @{ Status = 'failed'; Code = 'operation_id_conflict' }
        Build = { Need 'wall-arc'; New-CatalogueArcWall $picked['level'] $picked['wall_type'] 3500 }; Check = { param($r) $null } })
    # arc_collinear is a client-side code (RevitCommandCodes.ClientSide): the CLI refuses before opening the pipe.
    $steps.Add(@{ Key = 'invalid-arc'; Es = 'Pedido inválido: arco con puntos alineados (debe rechazarse sin tocar Revit)'; Command = 'wall.create'; Expect = @{ Status = 'failed'; Code = 'arc_collinear' }
        Build = { Need 'level', 'wall_type'; [ordered]@{ level = $picked['level']; type = $picked['wall_type']; height_mm = $script:CatalogueWallHeightMm; curve = [ordered]@{ kind = 'arc'; start = @(0, -3000); end = @(2000, -3000); through = @(1000, -3000) } } }
        Check = { param($r) $null } })
    return , $steps.ToArray()
}

# Runs every step in order through $Invoke (param index, key, envelopeJson; returns @{ output; exit_code; timed_out }).
# $Progress (optional) receives one Spanish line per step; $Alive (optional) returns $false when the child Revit is gone.
# A timeout means unknown completion: the remaining steps are not sent. Nothing is retried automatically.
function Invoke-CatalogueSequence {
    param(
        [Parameter(Mandatory)][string]$Stamp,
        [Parameter(Mandatory)][int]$Run,
        [Parameter(Mandatory)][string]$DocumentPath,
        [Parameter(Mandatory)][string]$DocumentUid,
        [Parameter(Mandatory)][scriptblock]$Invoke,
        [scriptblock]$Progress,
        [scriptblock]$Alive
    )
    $picked = @{}
    function Need([string[]]$Names) { foreach ($name in $Names) { if (!$picked.ContainsKey($name) -or !$picked[$name]) { throw "falta $name de un paso anterior" } } }
    $specs = Get-CatalogueSteps
    $records = [Collections.Generic.List[object]]::new()
    $aborted = $null
    $index = 0
    foreach ($spec in $specs) {
        $index++
        $operationId = New-CatalogueOperationId $Stamp $Run $(if ($spec.ContainsKey('ReuseOf')) { $spec.ReuseOf } else { $spec.Key })
        $record = [ordered]@{ index = $index; key = $spec.Key; description = $spec.Es; command = $spec.Command; operation_id = $operationId; expected = $spec.Expect; request = $null; response = $null; raw_response = $null; exit_code = $null; timed_out = $false; elapsed_ms = $null; passed = $false; reason = $null }
        $records.Add($record)
        if ($Progress) { & $Progress "Paso $index/$($specs.Count): $($spec.Es)" }
        if ($aborted) { $record.reason = "no enviado: $aborted"; continue }
        if ($Alive -and !(& $Alive)) { $aborted = 'el Revit de prueba se cerró'; $record.reason = "no enviado: $aborted"; continue }
        try { $stepArgs = & $spec.Build } catch { $record.reason = 'no enviado: ' + $_.Exception.Message; continue }
        $envelopeJson = New-CatalogueEnvelope $operationId $DocumentPath $DocumentUid $spec.Command $stepArgs
        $record.request = $envelopeJson | ConvertFrom-Json
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try { $result = & $Invoke $index $spec.Key $envelopeJson } catch { $watch.Stop(); $aborted = 'error al ejecutar la CLI: ' + $_.Exception.Message; $record.reason = $aborted; continue }
        $watch.Stop()
        $record.elapsed_ms = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
        $record.exit_code = $result.exit_code
        $record.timed_out = [bool]$result.timed_out
        if ($record.timed_out) { $aborted = "el paso $index superó el tiempo (no se sabe si terminó)"; $record.reason = $aborted; continue }
        $text = ([string]$result.output).Trim()
        $parsed = $null
        try { $parsed = $text | ConvertFrom-Json -ErrorAction Stop } catch { $parsed = $null }
        if ($null -eq $parsed) { $record.raw_response = $text; $record.reason = 'response is not JSON'; continue }
        $record.response = $parsed
        $reason = Test-CatalogueResponse $parsed $result.exit_code $operationId $spec.Expect
        if ($null -eq $reason) {
            try { $reason = & $spec.Check $parsed } catch { $reason = $_.Exception.Message }
        }
        $record.passed = $null -eq $reason
        $record.reason = $reason
    }
    $passed = @($records | Where-Object { $_.passed }).Count
    return [ordered]@{
        picked = $picked
        steps = $records.ToArray()
        counts = [ordered]@{ total = $records.Count; passed = $passed; failed = $records.Count - $passed; not_sent = @($records | Where-Object { $null -eq $_.request }).Count }
    }
}

function Format-CatalogueResult($Counts) { "Catálogo: $($Counts.passed)/$($Counts.total) pasos OK" }

# One CLI call: the envelope goes to a new numbered file, then "dotnet Bim.Cli.dll revit-command <file> <pipe>" with a
# bounded wait. On timeout only that CLI process is stopped (never Revit) and the step counts as unknown completion.
function Invoke-CatalogueCli {
    param(
        [Parameter(Mandatory)][string]$Dotnet,
        [Parameter(Mandatory)][string]$CliDll,
        [Parameter(Mandatory)][string]$PipeName,
        [Parameter(Mandatory)][string]$RequestDirectory,
        [Parameter(Mandatory)][int]$Index,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Json,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 120
    )
    New-Item -ItemType Directory -Force $RequestDirectory | Out-Null
    $file = Join-Path $RequestDirectory ('{0:D2}-{1}.json' -f $Index, $Key)
    $stream = [IO.File]::Open($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Json); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    $start = [Diagnostics.ProcessStartInfo]::new($Dotnet)
    foreach ($argument in @($CliDll, 'revit-command', $file, $PipeName)) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        $exited = $process.WaitForExit($TimeoutSeconds * 1000)
        if (!$exited) { $process.Kill($true); $null = $process.WaitForExit(10000) }
        else { $process.WaitForExit() }
        return @{ output = $out.GetAwaiter().GetResult(); stderr = $err.GetAwaiter().GetResult(); exit_code = $(if ($exited) { $process.ExitCode } else { $null }); timed_out = !$exited; request_file = $file }
    } finally { $process.Dispose() }
}
