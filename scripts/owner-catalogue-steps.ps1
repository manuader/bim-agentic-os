# C04 owner smoke of Revit command catalogue v1 and, after it, the P1-06 one-run verification groups (catalogue v2,
# views, snapshot, element queries, structure, documentation (ending with the P4-14 documentation update of a moved wall
# through the CLI doc-update verb: a dry run, then --execute of that plan's sha256, twice), journal, then the
# P1-11 groups: DirectShapes, extrusion roofs, move/copy/rotate/mirror, document.save_copy and document.save_as, and
# last modify/delete): the scripted step lists, envelope
# building, picking references from query results and checking each response with requested vs observed rows.
# Dot-sourced by scripts/owner-native-session.ps1 -Catalogue (which passes an invoker that runs the CLI
# "revit-command" verb against the child Revit's pipe, a wire invoker for journal.read and a doc-update invoker) and by
# tests/integration/test-catalogue-session.ps1 (which passes fakes). Everything here except Invoke-CatalogueCli,
# Invoke-CatalogueDocUpdate and Invoke-CatalogueWire is pure (the save_copy and save_as checks read the saved file to hash it, and the save_as steps
# read .bim/revit-enabled-roots.txt); nothing here starts or stops Revit, and the only saves are the document.save_copy
# step, a new BIM-Agentic-Test-c04-*-copia.rvt next to the test copy, and the document.save_as step, a new
# BIM-Agentic-Test-c04-*-guardado.rvt next to it (the save_as into an owner folder is refused by the CLI before the pipe).
# See docs/owner-verification-session.md.
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
    $steps.Add(@{ Key = 'invalid-arc'; Es = 'Pedido inválido: arco con puntos alineados (debe rechazarse sin tocar Revit)'; Command = 'wall.create'; ClientSide = $true; Expect = @{ Status = 'failed'; Code = 'arc_collinear' }
        Build = { Need 'level', 'wall_type'; [ordered]@{ level = $picked['level']; type = $picked['wall_type']; height_mm = $script:CatalogueWallHeightMm; curve = [ordered]@{ kind = 'arc'; start = @(0, -3000); end = @(2000, -3000); through = @(1000, -3000) } } }
        Check = { param($r) $null } })
    return , $steps.ToArray()
}

# --- P1-06 one-run verification: everything else that waits for Revit, after the 17 v1 steps ---
# Each later group is a list of steps in the v1 shape (Key, Es, Command, Expect, Build, Check, optional ReuseOf), plus
# Wire = $true for journal.read, a pipe Operation outside the revit.command catalogue that goes straight to the add-in
# pipe through the wire invoker (Invoke-CatalogueWire). Checks record requested vs observed rows with the
# Test-Catalogue* helpers below. Inputs shared across groups: only the v1 picks and v1 query responses; every later
# group makes its own levels pair, views and grids, so one failing group leaves the next ones runnable. The modify and
# delete group (destructive) is last; before it only the P4-11 documentation update deletes, and only its own stale items.
# pipe_read_timeout (P1-17): the add-in got the request but sent no line in time, so the step's completion is unknown.
$script:CatalogueTransportCodes = @('pipe_connect_timeout', 'pipe_io_error', 'pipe_read_timeout', 'no_response')
# A well-formed UniqueId that no element has (query.inspect must answer reference_not_found).
$script:CatalogueMissingUid = '00000000-0000-0000-0000-000000000000-00000000'
# The requested-vs-observed list of the step whose Check is running (set by Invoke-CatalogueSequence), else $null.
$script:CatalogueComparisons = $null
# A fixture precondition that is missing (no second level, no loaded type of a kind): the step is reported blocked,
# not failed, and every step that needs what it should have picked is blocked too.
$script:CatalogueBlockedMarker = '[bloqueado] '
# The closing sentence of RevitSaveAsRules.WrittenElsewhereDetail: the run stops when a reply carries it.
$script:CatalogueWrittenElsewhereMarker = 'do not continue until the owner has checked that file'

function New-CatalogueBlocked([string]$Precondition) { [InvalidOperationException]::new($script:CatalogueBlockedMarker + 'falta en el modelo de prueba: ' + $Precondition) }

function Test-CatalogueBlockedMessage([string]$Message) { $Message.StartsWith($script:CatalogueBlockedMarker, [StringComparison]::Ordinal) }

# Nested member by dotted path ('readback.view.section'), or $null.
function Get-CataloguePath($Object, [string]$Path) {
    $value = $Object
    foreach ($name in $Path.Split('.')) { $value = Get-CatalogueMember $value $name }
    return $value
}

# Array member by dotted path without nulls; always an array (possibly empty).
function Get-CatalogueArray($Object, [string]$Path) {
    $value = $Object
    foreach ($name in $Path.Split('.')) { $value = Get-CatalogueMember $value $name }
    return , @(@($value) | Where-Object { $null -ne $_ })
}

# Item $Index of a list, or $null.
function Get-CatalogueFirst($List, [int]$Index = 0) {
    $items = @($List)
    if ($items.Count -gt $Index) { return $items[$Index] }
    return $null
}

# [low, high] of coordinate $Axis over a section's crop_min and crop_max, or $null.
function Get-CatalogueSpan($Section, [int]$Axis) {
    $a = Get-CatalogueFirst (Get-CatalogueArray $Section 'crop_min') $Axis
    $b = Get-CatalogueFirst (Get-CatalogueArray $Section 'crop_max') $Axis
    if (!(Test-CatalogueNear $a 0 1e12) -or !(Test-CatalogueNear $b 0 1e12)) { return $null }
    return @([Math]::Min([double]$a, [double]$b), [Math]::Max([double]$a, [double]$b))
}

function Get-CatalogueFirstCreated($Response) {
    $created = Get-CatalogueList $Response 'created'
    if ($created.Count) { return [string]$created[0] }
    return $null
}

function ConvertTo-CatalogueText($Value) {
    if ($null -eq $Value) { return 'null' }
    return (ConvertTo-Json -InputObject $Value -Compress -Depth 8)
}

# Records one requested-vs-observed row for the running step; returns $null when it holds, else the reason.
function Add-CatalogueComparison([string]$What, $Requested, $Observed, [bool]$Ok) {
    if ($null -ne $script:CatalogueComparisons) { $script:CatalogueComparisons.Add([ordered]@{ check = $What; requested = $Requested; observed = $Observed; ok = $Ok }) }
    if ($Ok) { return $null }
    return "${What}: se pidió $(ConvertTo-CatalogueText $Requested), se observó $(ConvertTo-CatalogueText $Observed)"
}

function Test-CatalogueSame([string]$What, $Requested, $Observed) {
    $ok = ($null -ne $Observed) -and ([string]$Requested -eq [string]$Observed)
    return Add-CatalogueComparison $What $Requested $Observed $ok
}

function Test-CatalogueNumber([string]$What, [double]$Requested, $Observed, [double]$Tolerance) {
    return Add-CatalogueComparison $What $Requested $Observed (Test-CatalogueNear $Observed $Requested $Tolerance)
}

function Test-CataloguePoint([string]$What, [double[]]$Requested, $Observed, [double]$Tolerance) {
    $values = @($Observed)
    $ok = $values.Count -eq $Requested.Count
    for ($i = 0; $ok -and $i -lt $Requested.Count; $i++) { $ok = Test-CatalogueNear $values[$i] $Requested[$i] $Tolerance }
    return Add-CatalogueComparison $What $Requested $Observed $ok
}

function Test-CatalogueContains([string]$What, $Needle, $List) {
    $items = @(@($List) | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    $ok = ($null -ne $Needle) -and ($items -contains [string]$Needle)
    return Add-CatalogueComparison $What $Needle $items $ok
}

function Test-CatalogueCondition([string]$What, $Requested, $Observed, [bool]$Ok) {
    return Add-CatalogueComparison $What $Requested $Observed $Ok
}

# All failed checks of a step joined, or $null when every check held.
function Join-CatalogueReasons([object[]]$Reasons) {
    $bad = @($Reasons | Where-Object { $_ })
    if ($bad.Count) { return ($bad -join '; ') }
    return $null
}

# The two lowest levels by elevation (base and top of the stair and the column).
function Select-CatalogueLevelPair($Response) {
    $levels = Get-CatalogueList (Get-CatalogueMember $Response 'readback') 'levels'
    $sorted = @($levels | Sort-Object { [double](Get-CatalogueMember $_ 'elevation_mm') })
    if ($sorted.Count -lt 2) { throw (New-CatalogueBlocked 'dos niveles (base y superior) para la escalera, el techo y la columna') }
    return @{ low = [string](Get-CatalogueMember $sorted[0] 'unique_id'); high = [string](Get-CatalogueMember $sorted[1] 'unique_id') }
}

function Select-CatalogueRoofType($Response) {
    $all = Get-CatalogueList (Get-CatalogueMember $Response 'readback') 'roof_types'
    $types = @($all | Where-Object { [string](Get-CatalogueMember $_ 'name') -notmatch '(?i)glaz|glass|vidri' })
    if (!$types.Count) { throw (New-CatalogueBlocked 'un tipo de techo no vidriado') }
    return [string](Get-CatalogueMember $types[0] 'unique_id')
}

function Select-CatalogueCurtainType($Response) {
    $all = Get-CatalogueList (Get-CatalogueMember $Response 'readback') 'wall_types'
    $curtain = @($all | Where-Object { (Get-CatalogueMember $_ 'kind') -eq 'Curtain' })
    if (!$curtain.Count) { throw (New-CatalogueBlocked 'un tipo de muro cortina (Curtain)') }
    return [string](Get-CatalogueMember $curtain[0] 'unique_id')
}

function Select-CatalogueStructuralType($Response, [string]$Category) {
    $readback = Get-CatalogueMember $Response 'readback'
    if ((Get-CatalogueMember $readback 'category') -ne $Category) { throw 'la consulta de tipos estructurales respondió para otra categoría' }
    $types = Get-CatalogueList $readback 'structural_types'
    if (!$types.Count) { throw (New-CatalogueBlocked "un tipo estructural cargado de categoría $Category") }
    return [string](Get-CatalogueMember $types[0] 'unique_id')
}

# A refusal names the offending field and explains itself (detail matches the wildcard pattern).
function Test-CatalogueRefusal($Response, [string]$Field, [string]$DetailPattern) {
    $detail = [string](Get-CatalogueMember $Response 'detail')
    return Join-CatalogueReasons @(
        (Test-CatalogueSame 'campo rechazado' $Field (Get-CatalogueMember $Response 'field')),
        (Test-CatalogueCondition 'motivo del rechazo' $DetailPattern $detail ($detail -like $DetailPattern)))
}

# width_mm of a wall type in a query.wall_types reply, or $null.
function Get-CatalogueWallTypeWidth($Response, [string]$TypeUid) {
    foreach ($type in (Get-CatalogueList (Get-CatalogueMember $Response 'readback') 'wall_types')) { if ((Get-CatalogueMember $type 'unique_id') -eq $TypeUid) { return Get-CatalogueMember $type 'width_mm' } }
    return $null
}

# One parameter of a query.inspect reply by its BuiltInParameter name, or $null.
function Find-CatalogueParameter($Response, [string]$BuiltIn) {
    foreach ($parameter in (Get-CatalogueArray $Response 'readback.parameters')) {
        if ((Get-CatalogueMember $parameter 'built_in') -eq $BuiltIn) { return $parameter }
    }
    return $null
}

# The wall height read back by query.inspect, compared with the expected millimetres.
function Test-CatalogueInspectHeight($Response, [string]$WallUid, [double]$HeightMm) {
    $height = Find-CatalogueParameter $Response 'WALL_USER_HEIGHT_PARAM'
    return Join-CatalogueReasons @(
        (Test-CatalogueSame 'elemento inspeccionado' $WallUid (Get-CataloguePath $Response 'readback.element.unique_id')),
        (Test-CatalogueNumber 'altura sin restricciones (WALL_USER_HEIGHT_PARAM, mm)' $HeightMm (Get-CatalogueMember $height 'value') 0.01),
        (Test-CatalogueSame 'unidad de la altura' 'mm' (Get-CatalogueMember $height 'unit')))
}

# A query.snapshot reply: the BIM-IR id of a source UniqueId, and the element with that id.
function Find-CatalogueSnapshotId($Readback, [string]$UniqueId) {
    foreach ($source in (Get-CatalogueList $Readback 'sources')) {
        if ([string](Get-CatalogueMember $source 'unique_id') -eq $UniqueId) { return [string](Get-CatalogueMember $source 'id') }
    }
    return $null
}

function Find-CatalogueSnapshotElement($Readback, [string]$Id) {
    foreach ($element in (Get-CatalogueList $Readback 'elements')) {
        if ([string](Get-CatalogueMember $element 'Id') -eq $Id) { return $element }
    }
    return $null
}

# A door or window of the snapshot lies on its host wall axis, AlongMm from the wall start, with the given sill.
function Test-CatalogueSnapshotOpening($Readback, [string]$What, [string]$OpeningUid, [string]$WallUid, [double]$AlongMm, [double]$SillMm) {
    $openingId = Find-CatalogueSnapshotId $Readback $OpeningUid
    $wallId = Find-CatalogueSnapshotId $Readback $WallUid
    if (!$openingId -or !$wallId) { return Test-CatalogueCondition "$What y su muro mapeados en BIM-IR" 'mapeados' "puerta/ventana: $openingId, muro: $wallId" $false }
    $opening = Find-CatalogueSnapshotElement $Readback $openingId
    $wall = Find-CatalogueSnapshotElement $Readback $wallId
    $dependencies = @((Get-CatalogueList $opening 'Dependencies') | ForEach-Object { [string]$_ })
    $g = Get-CatalogueList $opening 'GeometryMetres'
    $w = Get-CatalogueList $wall 'GeometryMetres'
    if ($g.Count -lt 6 -or $w.Count -lt 4) { return Test-CatalogueCondition "geometría de $What y del muro" '6 y 6 números' "$($g.Count) y $($w.Count)" $false }
    $dx = [double]$w[2] - [double]$w[0]
    $dy = [double]$w[3] - [double]$w[1]
    $length = [Math]::Sqrt($dx * $dx + $dy * $dy)
    if (!($length -gt 0)) { return Test-CatalogueCondition "eje del muro de $What" 'longitud > 0' $length $false }
    $mx = ([double]$g[0] + [double]$g[2]) / 2 - [double]$w[0]
    $my = ([double]$g[1] + [double]$g[3]) / 2 - [double]$w[1]
    $along = ($mx * $dx + $my * $dy) / $length * 1000
    $off = [Math]::Abs($mx * $dy - $my * $dx) / $length * 1000
    return Join-CatalogueReasons @(
        (Test-CatalogueContains "$What depende de su muro anfitrión" $wallId $dependencies),
        (Test-CatalogueNumber "$What sobre el eje del muro: distancia desde el inicio (mm)" $AlongMm $along 1),
        (Test-CatalogueNumber "${What}: distancia al eje del muro (mm)" 0 $off 1),
        (Test-CatalogueNumber "${What}: antepecho (mm)" $SillMm ([double]$g[4] * 1000) 1))
}

function Test-CatalogueSnapshotUnmapped($Readback, [string]$What, [string]$UniqueId, [string]$Reason) {
    $entry = $null
    foreach ($row in (Get-CatalogueList $Readback 'unmapped')) { if ([string](Get-CatalogueMember $row 'unique_id') -eq $UniqueId) { $entry = $row } }
    return Test-CatalogueSame "$What en unmapped con motivo" $Reason (Get-CatalogueMember $entry 'reason')
}

# The raw readback text of a response line (between "readback": and the top-level ,"receipt":), for byte comparison.
function Get-CatalogueRawReadback([string]$Text) {
    if (!$Text) { return $null }
    $start = $Text.IndexOf('"readback":', [StringComparison]::Ordinal)
    $end = $Text.LastIndexOf(',"receipt":', [StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -le $start) { return $null }
    return $Text.Substring($start + 11, $end - $start - 11)
}

function Test-CatalogueSameText([string]$What, [string]$First, [string]$Second) {
    $ok = ($First.Length -gt 0) -and [string]::Equals($First, $Second, [StringComparison]::Ordinal)
    return Test-CatalogueCondition $What "$($First.Length) bytes iguales" "$($Second.Length) bytes$(if (!$ok) { ', distintos' })" $ok
}

function Get-CatalogueViewName([string]$What) { "C04 $Stamp r$Run $What" }

# P9-01 catalogue v2. The roof sits on the upper level over the v1 rectangle; the stair and the curtain wall stay
# clear of the v1 elements; the opening is cut through the v1 floor.
function Get-CatalogueV2Steps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'v2-levels'; Es = 'Consultar niveles (base y superior)'; Command = 'query.levels'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }
        Check = { param($r) $pair = Guard @('level_low', 'level_high') { Select-CatalogueLevelPair $r }; $picked['level_low'] = $pair.low; $picked['level_high'] = $pair.high; $null } })
    $steps.Add(@{ Key = 'v2-roof-types'; Es = 'Consultar tipos de techo'; Command = 'query.roof_types'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }; Check = { param($r) $picked['roof_type'] = Guard @('roof_type') { Select-CatalogueRoofType $r }; $null } })
    $steps.Add(@{ Key = 'v2-roof'; Es = 'Techo a cuatro aguas (30 grados) sobre el rectángulo, en el nivel superior'; Command = 'roof.create_footprint'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'level_high', 'roof_type'; [ordered]@{ level = $picked['level_high']; type = $picked['roof_type']; boundary = @($script:CatalogueRectangle | ForEach-Object { , @($_[0], $_[1]) }); slope_deg = 30 } }
        Check = {
            param($r)
            $roof = Get-CataloguePath $r 'readback.roof'
            $edges = Get-CatalogueList $roof 'edges'
            $sloped = @($edges | Where-Object { (Get-CatalogueMember $_ 'defines_slope') -eq $true -and (Test-CatalogueNear (Get-CatalogueMember $_ 'slope_deg') 30 0.001) })
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'techo = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $roof 'unique_id')),
                (Test-CatalogueSame 'nivel del techo' $picked['level_high'] (Get-CatalogueMember $roof 'level_unique_id')),
                (Test-CatalogueSame 'tipo del techo' $picked['roof_type'] (Get-CatalogueMember $roof 'type_unique_id')),
                (Test-CatalogueCondition 'bordes que definen pendiente de 30 grados' 4 $sloped.Count ($edges.Count -eq 4 -and $sloped.Count -eq 4)),
                (Test-CatalogueNumber 'área de la huella (mm2)' 24000000 (Get-CatalogueMember $roof 'area_mm2') 24))
        } })
    $steps.Add(@{ Key = 'v2-stair'; Es = 'Escalera recta de un tramo (ancho 1000 mm) del nivel base al superior'; Command = 'stair.create'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'level_low', 'level_high'; [ordered]@{ base_level = $picked['level_low']; top_level = $picked['level_high']; run = [ordered]@{ start = @(-8000, 0); end = @(-8000, 6500) }; width_mm = 1000 } }
        Check = {
            param($r)
            $stairs = Get-CataloguePath $r 'readback.stairs'
            $widths = Get-CatalogueArray $stairs 'run_widths_mm'
            $width = if ($widths.Count) { $widths[0] } else { $null }
            Join-CatalogueReasons @(
                (Test-CatalogueContains 'escalera entre los elementos creados' (Get-CatalogueMember $stairs 'unique_id') (Get-CatalogueList $r 'created')),
                (Test-CatalogueSame 'nivel base' $picked['level_low'] (Get-CatalogueMember $stairs 'base_level_unique_id')),
                (Test-CatalogueSame 'nivel superior' $picked['level_high'] (Get-CatalogueMember $stairs 'top_level_unique_id')),
                (Test-CatalogueNumber 'ancho del tramo (mm)' 1000 $width 0.01),
                (Test-CataloguePoint 'inicio del tramo (mm)' @(-8000, 0) (Get-CataloguePath $stairs 'run.start') 0.01),
                (Test-CataloguePoint 'fin del tramo (mm)' @(-8000, 6500) (Get-CataloguePath $stairs 'run.end') 0.01),
                (Test-CatalogueSame 'contrahuellas reales = deseadas (llega al nivel superior)' (Get-CatalogueMember $stairs 'desired_risers') (Get-CatalogueMember $stairs 'risers')))
        } })
    $steps.Add(@{ Key = 'v2-curtain-wall'; Es = 'Muro cortina recto de 6000 mm con 3 líneas de rejilla verticales y 2 horizontales'; Command = 'curtain_wall.create'; Expect = @{ Status = 'succeeded' }
        Build = {
            Need 'level'
            $picked['curtain_type'] = Guard @('curtain_type') { Select-CatalogueCurtainType (Response 'query-wall-types') }
            [ordered]@{ level = $picked['level']; type = $picked['curtain_type']; height_mm = 3000; curve = [ordered]@{ kind = 'line'; start = @(0, -3000); end = @(6000, -3000) }; grid = [ordered]@{ vertical_spacing_mm = 1500; horizontal_spacing_mm = 1000 } }
        }
        Check = {
            param($r)
            $wall = Get-CataloguePath $r 'readback.wall'
            $added = Get-CatalogueArray $r 'readback.curtain_grid.added_grid_line_unique_ids'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'muro cortina = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $wall 'unique_id')),
                (Test-CatalogueSame 'tipo de muro cortina' $picked['curtain_type'] (Get-CatalogueMember $wall 'type_unique_id')),
                (Test-CatalogueNumber 'altura (mm)' 3000 (Get-CatalogueMember $wall 'height_mm') 0.01),
                (Test-CatalogueCondition 'líneas de rejilla agregadas (3 verticales + 2 horizontales)' 5 $added.Count ($added.Count -eq 5)))
        } })
    $steps.Add(@{ Key = 'v2-floor-opening'; Es = 'Hueco de 1000 x 1000 mm en el suelo del rectángulo'; Command = 'floor.create_opening'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ host_floor_uid = (Created 'floor'); profile = @(@(1000, 1000), @(2000, 1000), @(2000, 2000), @(1000, 2000)) } }
        Check = {
            param($r)
            $opening = Get-CataloguePath $r 'readback.floor_opening'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'hueco = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $opening 'unique_id')),
                (Test-CatalogueSame 'suelo anfitrión' (Created 'floor') (Get-CatalogueMember $opening 'host_floor_unique_id')),
                (Test-CatalogueSame 'contorno pedido (no rectangular de Revit)' $false (Get-CatalogueMember $opening 'rect_boundary')),
                (Test-CatalogueNumber 'área del hueco (mm2)' 1000000 (Get-CatalogueMember $opening 'area_mm2') 1))
        } })
    return , $steps.ToArray()
}

# P4-01 views: the same section and boxed 3D requests and expected values as tests/integration/native-views-probe.ps1.
function Get-CatalogueViewSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'views-family-types'; Es = 'Consultar tipos de vista de corte'; Command = 'query.view_family_types'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ family = 'section' } }
        Check = { param($r) $rows = Get-CatalogueArray $r 'readback.view_family_types'; Test-CatalogueCondition 'tipos de vista de corte' '>= 1' $rows.Count ($rows.Count -ge 1) } })
    $steps.Add(@{ Key = 'views-templates'; Es = 'Consultar plantillas de vista'; Command = 'query.view_templates'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }
        Check = { param($r) $readback = Get-CatalogueMember $r 'readback'; Test-CatalogueCondition 'lista view_templates presente' 'presente' $(if ($null -ne $readback -and $null -ne $readback.PSObject.Properties['view_templates']) { 'presente' } else { 'ausente' }) ($null -ne $readback -and $null -ne $readback.PSObject.Properties['view_templates']) } })
    $steps.Add(@{ Key = 'views-plan'; Es = 'Planta del nivel de los muros'; Command = 'view.create_plan'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'level'; [ordered]@{ level = $picked['level']; kind = 'floor_plan'; name = (Get-CatalogueViewName 'planta') } }
        Check = {
            param($r)
            $view = Get-CataloguePath $r 'readback.view'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'vista = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $view 'unique_id')),
                (Test-CatalogueSame 'nombre' (Get-CatalogueViewName 'planta') (Get-CatalogueMember $view 'name')),
                (Test-CatalogueSame 'tipo de vista' 'FloorPlan' (Get-CatalogueMember $view 'view_type')),
                (Test-CatalogueSame 'nivel de la planta' $picked['level'] (Get-CatalogueMember $view 'level_unique_id')))
        } })
    $steps.Add(@{ Key = 'views-section'; Es = 'Corte a lo largo del muro sur (0,0)-(6000,0), profundidad 3000 mm, de -500 a 4000 mm'; Command = 'view.create_section'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ line = [ordered]@{ start = @(0, 0); end = @(6000, 0) }; depth_mm = 3000; bottom_elevation_mm = -500; top_elevation_mm = 4000; name = (Get-CatalogueViewName 'corte') } }
        Check = {
            param($r)
            $view = Get-CataloguePath $r 'readback.view'
            $section = Get-CatalogueMember $view 'section'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'vista = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $view 'unique_id')),
                (Test-CatalogueSame 'nombre' (Get-CatalogueViewName 'corte') (Get-CatalogueMember $view 'name')),
                (Test-CataloguePoint 'dirección derecha' @(1, 0, 0) (Get-CatalogueMember $section 'right_direction') 0.000001),
                (Test-CataloguePoint 'dirección arriba' @(0, 0, 1) (Get-CatalogueMember $section 'up_direction') 0.000001),
                (Test-CataloguePoint 'dirección de vista (hacia el observador)' @(0, -1, 0) (Get-CatalogueMember $section 'view_direction') 0.000001),
                (Test-CatalogueNumber 'profundidad far clip (mm)' 3000 (Get-CatalogueMember $section 'far_clip_mm') 0.01),
                (Test-CatalogueNumber 'origen: y (plano de corte sobre la línea, mm)' 0 (Get-CatalogueFirst (Get-CatalogueArray $section 'origin') 1) 0.01),
                (Test-CataloguePoint 'recorte: x mínimo y máximo a lo largo de la línea (mm)' @(0, 6000) (Get-CatalogueSpan $section 0) 0.01),
                (Test-CataloguePoint 'recorte: z mínimo y máximo (elevación, mm)' @(-500, 4000) (Get-CatalogueSpan $section 2) 0.01),
                (Test-CatalogueSame 'recorte activo' $true (Get-CatalogueMember $section 'crop_box_active')))
        } })
    $steps.Add(@{ Key = 'views-3d'; Es = 'Vista 3D isométrica con caja de sección (0,0,0)-(8000,6000,3500)'; Command = 'view.create_3d'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ section_box = [ordered]@{ min = @(0, 0, 0); max = @(8000, 6000, 3500) }; name = (Get-CatalogueViewName '3D') } }
        Check = {
            param($r)
            $view = Get-CataloguePath $r 'readback.view'
            $threeD = Get-CatalogueMember $view 'three_d'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'vista = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $view 'unique_id')),
                (Test-CatalogueSame 'perspectiva' $false (Get-CatalogueMember $threeD 'perspective')),
                (Test-CatalogueSame 'caja de sección activa' $true (Get-CatalogueMember $threeD 'section_box_active')),
                (Test-CataloguePoint 'caja: mínimo (mm)' @(0, 0, 0) (Get-CataloguePath $threeD 'section_box.min') 0.01),
                (Test-CataloguePoint 'caja: máximo (mm)' @(8000, 6000, 3500) (Get-CataloguePath $threeD 'section_box.max') 0.01))
        } })
    return , $steps.ToArray()
}

# P2-01 query.snapshot: twice byte-identical; the v1 door and window on their wall axes; the floor and the curved wall
# listed under unmapped.
function Get-CatalogueSnapshotSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'snap-1'; Es = 'Instantánea BIM-IR del modelo (query.snapshot)'; Command = 'query.snapshot'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }
        Check = {
            param($r)
            $readback = Get-CatalogueMember $r 'readback'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'snapshot_version' 1 (Get-CatalogueMember $readback 'snapshot_version')),
                (Test-CatalogueSame 'documento' $DocumentUid (Get-CatalogueMember $readback 'document_unique_id')),
                (Test-CatalogueSnapshotOpening $readback 'puerta' (Created 'door') $picked['wall-south'] 1500 0),
                (Test-CatalogueSnapshotOpening $readback 'ventana' (Created 'window') $picked['wall-east'] 2000 900),
                (Test-CatalogueSnapshotUnmapped $readback 'suelo' (Created 'floor') 'no_bim_ir_kind'),
                (Test-CatalogueSnapshotUnmapped $readback 'muro curvo' $picked['wall-arc'] 'curved_wall_unsupported'))
        } })
    $steps.Add(@{ Key = 'snap-2'; Es = 'Segunda instantánea sin cambios: debe ser idéntica byte a byte'; Command = 'query.snapshot'; Expect = @{ Status = 'succeeded' }
        Build = { if (!$rawText.ContainsKey('snap-1')) { throw 'falta la primera instantánea' }; [ordered]@{} }
        Check = { param($r) Test-CatalogueSameText 'readback idéntico a la primera instantánea' (Get-CatalogueRawReadback $rawText['snap-1']) (Get-CatalogueRawReadback $rawText['snap-2']) } })
    return , $steps.ToArray()
}

# P1-02 query.elements and query.inspect, including truncation by limit, missing parameters and reference_not_found.
function Get-CatalogueQuerySteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'q-walls'; Es = 'Listar muros (query.elements por categoría)'; Command = 'query.elements'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-south'; [ordered]@{ category = 'wall' } }
        Check = {
            param($r)
            $readback = Get-CatalogueMember $r 'readback'
            $rows = Get-CatalogueList $readback 'elements'
            $uids = @($rows | ForEach-Object { Get-CatalogueMember $_ 'unique_id' })
            $matched = Get-CatalogueMember $readback 'matched'
            $south = $null
            foreach ($row in $rows) { if ((Get-CatalogueMember $row 'unique_id') -eq $picked['wall-south']) { $south = $row } }
            $builtIns = @((Get-CatalogueList $south 'parameters') | ForEach-Object { Get-CatalogueMember $_ 'built_in' })
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'categoría Revit' 'OST_Walls' (Get-CatalogueMember $readback 'built_in_category')),
                (Test-CatalogueContains 'parámetros por defecto del muro sur (Comments por id interno, sin depender del idioma)' 'ALL_MODEL_INSTANCE_COMMENTS' $builtIns),
                (Test-CatalogueCondition 'coincidencias (al menos los 5 muros de v1)' '>= 5' $matched (($null -ne $matched) -and ($matched -ge 5))),
                (Test-CatalogueContains 'incluye el muro sur' $picked['wall-south'] $uids),
                (Test-CatalogueSame 'returned = filas' $rows.Count (Get-CatalogueMember $readback 'returned')),
                (Test-CatalogueSame 'truncado' $false (Get-CatalogueMember $readback 'truncated')))
        } })
    $steps.Add(@{ Key = 'q-walls-limit'; Es = 'Listar muros con límite 1 (debe truncar)'; Command = 'query.elements'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ category = 'wall'; limit = 1 } }
        Check = {
            param($r)
            $readback = Get-CatalogueMember $r 'readback'
            $rows = Get-CatalogueList $readback 'elements'
            Join-CatalogueReasons @(
                (Test-CatalogueCondition 'filas devueltas' 1 $rows.Count ($rows.Count -eq 1)),
                (Test-CatalogueSame 'returned' 1 (Get-CatalogueMember $readback 'returned')),
                (Test-CatalogueSame 'truncado' $true (Get-CatalogueMember $readback 'truncated')),
                (Test-CatalogueSame 'motivo del truncado' 'limit' (Get-CatalogueMember $readback 'truncated_reason')),
                (Test-CatalogueCondition 'next_cursor para seguir' 'presente' $(if (Get-CatalogueMember $readback 'next_cursor') { 'presente' } else { 'ausente' }) ([bool](Get-CatalogueMember $readback 'next_cursor'))))
        } })
    # P1-13: follow the next_cursor of the limit-1 page to the end (limit 5000); read-only. The pages are disjoint and,
    # in order, equal the unlimited q-walls listing (no write between q-walls and here).
    $steps.Add(@{ Key = 'q-walls-next'; Es = 'Seguir next_cursor de la lista con límite 1 (páginas disjuntas que juntas dan la lista completa)'; Command = 'query.elements'; Expect = @{ Status = 'succeeded' }
        Build = {
            $cursor = Get-CataloguePath (Response 'q-walls-limit') 'readback.next_cursor'
            if (!$cursor) { throw 'falta next_cursor del paso q-walls-limit' }
            [ordered]@{ category = 'wall'; limit = 5000; cursor = [string]$cursor } }
        Check = {
            param($r)
            $firstRows = Get-CatalogueArray (Response 'q-walls-limit') 'readback.elements'
            $first = @($firstRows | ForEach-Object { [string](Get-CatalogueMember $_ 'unique_id') })
            $second = @((Get-CatalogueArray $r 'readback.elements') | ForEach-Object { [string](Get-CatalogueMember $_ 'unique_id') })
            $all = @((Get-CatalogueArray (Response 'q-walls') 'readback.elements') | ForEach-Object { [string](Get-CatalogueMember $_ 'unique_id') })
            $overlap = @($second | Where-Object { $first -contains $_ })
            $union = @($first) + @($second)
            $lastId = if ($firstRows.Count) { Get-CatalogueMember $firstRows[-1] 'id' } else { $null }
            $next = Get-CataloguePath $r 'readback.next_cursor'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'after = último id de la primera página' $lastId (Get-CataloguePath $r 'readback.after')),
                (Test-CatalogueSame 'truncado' $false (Get-CataloguePath $r 'readback.truncated')),
                (Test-CatalogueCondition 'next_cursor de la última página' 'ausente' $(if ($next) { 'presente' } else { 'ausente' }) (!$next)),
                (Test-CatalogueCondition 'páginas disjuntas' 0 $overlap.Count ($overlap.Count -eq 0)),
                (Test-CatalogueCondition 'unión de las páginas = lista completa de q-walls, en orden' $all.Count $union.Count (($union -join ',') -eq ($all -join ',') -and $all.Count -ge 2)))
        } })
    $steps.Add(@{ Key = 'q-doors-level'; Es = 'Listar puertas del nivel de los muros'; Command = 'query.elements'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'level'; [ordered]@{ category = 'door'; level = $picked['level'] } }
        Check = {
            param($r)
            $readback = Get-CatalogueMember $r 'readback'
            $uids = @((Get-CatalogueList $readback 'elements') | ForEach-Object { Get-CatalogueMember $_ 'unique_id' })
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'nivel del filtro' $picked['level'] (Get-CataloguePath $readback 'level.unique_id')),
                (Test-CatalogueContains 'incluye la puerta de v1' (Created 'door') $uids))
        } })
    # Named parameters are matched by their name as shown in Revit (localized); only an absent name is asked for here,
    # so the step does not depend on the Revit UI language. Comments is read by built-in id in q-walls.
    $steps.Add(@{ Key = 'q-params'; Es = 'Listar muros pidiendo un parámetro inexistente'; Command = 'query.elements'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-south'; [ordered]@{ category = 'wall'; parameters = @('C04 Missing Parameter'); limit = 100 } }
        Check = {
            param($r)
            $row = $null
            foreach ($candidate in (Get-CatalogueArray $r 'readback.elements')) { if ((Get-CatalogueMember $candidate 'unique_id') -eq $picked['wall-south']) { $row = $candidate } }
            Join-CatalogueReasons @(
                (Test-CatalogueCondition 'fila del muro sur' 'presente' $(if ($null -ne $row) { 'presente' } else { 'ausente' }) ($null -ne $row)),
                (Test-CatalogueContains 'parámetro inexistente en missing_parameters' 'C04 Missing Parameter' (Get-CatalogueList $row 'missing_parameters')))
        } })
    $steps.Add(@{ Key = 'q-inspect'; Es = 'Inspeccionar el muro sur (query.inspect)'; Command = 'query.inspect'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-south'; [ordered]@{ unique_id = $picked['wall-south'] } }
        Check = {
            param($r)
            $count = Get-CataloguePath $r 'readback.parameter_count'
            Join-CatalogueReasons @(
                (Test-CatalogueInspectHeight $r $picked['wall-south'] $script:CatalogueWallHeightMm),
                (Test-CatalogueCondition 'parámetros leídos' '>= 1' $count (($null -ne $count) -and ($count -ge 1))))
        } })
    $steps.Add(@{ Key = 'q-inspect-missing'; Es = 'Inspeccionar un UniqueId inexistente (debe responder reference_not_found)'; Command = 'query.inspect'; Expect = @{ Status = 'failed'; Code = 'reference_not_found' }
        Build = { [ordered]@{ unique_id = $script:CatalogueMissingUid } }; Check = { param($r) $null } })
    return , $steps.ToArray()
}

# P9-02 structure, away from the v1 rectangle (x = 20000 mm); the wall foundation goes under the v1 north wall.
function Get-CatalogueStructureSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 's-levels'; Es = 'Consultar niveles (base y superior)'; Command = 'query.levels'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }
        Check = { param($r) $pair = Guard @('level_low', 'level_high') { Select-CatalogueLevelPair $r }; $picked['level_low'] = $pair.low; $picked['level_high'] = $pair.high; $null } })
    foreach ($category in @('column', 'framing', 'foundation', 'wall_foundation')) {
        $steps.Add(@{ Key = "s-types-$($category -replace '_', '-')"; Es = "Consultar tipos estructurales: $category"; Command = 'query.structural_types'; Expect = @{ Status = 'succeeded' }; Category = $category
            Build = { [ordered]@{ category = $spec.Category } }
            Check = { param($r) $picked["structural_$($spec.Category)"] = Guard @("structural_$($spec.Category)") { Select-CatalogueStructuralType $r $spec.Category }; $null } })
    }
    $steps.Add(@{ Key = 's-grids'; Es = 'Consultar ejes (query.grids)'; Command = 'query.grids'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }
        Check = { param($r) $readback = Get-CatalogueMember $r 'readback'; $present = ($null -ne $readback) -and ($null -ne $readback.PSObject.Properties['grids']); Test-CatalogueCondition 'lista grids presente' 'presente' $(if ($present) { 'presente' } else { 'ausente' }) $present } })
    $steps.Add(@{ Key = 's-grid'; Es = 'Eje recto de (20000,-2000) a (20000,6000)'; Command = 'grid.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { [ordered]@{ line = [ordered]@{ start = @(20000, -2000); end = @(20000, 6000) } } }
        Check = {
            param($r)
            $grid = Get-CataloguePath $r 'readback.grid'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'eje = elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $grid 'unique_id')),
                (Test-CatalogueSame 'eje recto' $false (Get-CatalogueMember $grid 'curved')),
                (Test-CataloguePoint 'inicio (mm)' @(20000, -2000) (Get-CatalogueMember $grid 'start') 0.01),
                (Test-CataloguePoint 'fin (mm)' @(20000, 6000) (Get-CatalogueMember $grid 'end') 0.01))
        } })
    $steps.Add(@{ Key = 's-grid-retry'; Es = 'Repetir el eje con el mismo identificador (no debe duplicarse)'; Command = 'grid.create'; ReuseOf = 's-grid'; Expect = @{ Status = 'succeeded'; Replayed = $true; Created = 1 }
        Build = { [ordered]@{ line = [ordered]@{ start = @(20000, -2000); end = @(20000, 6000) } } }
        Check = { param($r) Test-CatalogueSame 'el reintento devuelve el mismo eje' (Created 's-grid') (Get-CatalogueFirstCreated $r) } })
    $steps.Add(@{ Key = 's-column'; Es = 'Columna estructural en (20000,0) del nivel base al superior'; Command = 'structural_column.create'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'structural_column', 'level_low', 'level_high'; [ordered]@{ type = $picked['structural_column']; point = @(20000, 0); base_level = $picked['level_low']; top_level = $picked['level_high'] } }
        Check = {
            param($r)
            $column = Get-CataloguePath $r 'readback.structural_column'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'columna = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $column 'unique_id')),
                (Test-CatalogueSame 'tipo' $picked['structural_column'] (Get-CatalogueMember $column 'type_unique_id')),
                (Test-CataloguePoint 'punto (mm)' @(20000, 0) (Get-CatalogueMember $column 'point') 0.01),
                (Test-CatalogueSame 'nivel base' $picked['level_low'] (Get-CatalogueMember $column 'base_level_unique_id')),
                (Test-CatalogueSame 'nivel superior' $picked['level_high'] (Get-CatalogueMember $column 'top_level_unique_id')),
                (Test-CatalogueSame 'vertical (no inclinada)' $false (Get-CatalogueMember $column 'slanted')))
        } })
    $steps.Add(@{ Key = 's-beam'; Es = 'Viga de (20000,0) a (26000,0) en el nivel superior'; Command = 'structural_beam.create'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'structural_framing', 'level_high'; [ordered]@{ level = $picked['level_high']; type = $picked['structural_framing']; line = [ordered]@{ start = @(20000, 0); end = @(26000, 0) } } }
        Check = {
            param($r)
            $beam = Get-CataloguePath $r 'readback.structural_beam'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'viga = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $beam 'unique_id')),
                (Test-CatalogueSame 'tipo' $picked['structural_framing'] (Get-CatalogueMember $beam 'type_unique_id')),
                (Test-CatalogueSame 'nivel de referencia' $picked['level_high'] (Get-CatalogueMember $beam 'level_unique_id')),
                (Test-CataloguePoint 'inicio (mm)' @(20000, 0) (Get-CatalogueMember $beam 'start') 0.01),
                (Test-CataloguePoint 'fin (mm)' @(26000, 0) (Get-CatalogueMember $beam 'end') 0.01))
        } })
    $steps.Add(@{ Key = 's-footing'; Es = 'Zapata aislada bajo la columna (20000,0) en el nivel base'; Command = 'foundation.create_isolated'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'structural_foundation', 'level_low'; [ordered]@{ level = $picked['level_low']; type = $picked['structural_foundation']; point = @(20000, 0) } }
        Check = {
            param($r)
            $footing = Get-CataloguePath $r 'readback.foundation'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'zapata = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $footing 'unique_id')),
                (Test-CatalogueSame 'tipo' $picked['structural_foundation'] (Get-CatalogueMember $footing 'type_unique_id')),
                (Test-CatalogueSame 'nivel' $picked['level_low'] (Get-CatalogueMember $footing 'level_unique_id')),
                (Test-CataloguePoint 'punto (mm)' @(20000, 0) (Get-CatalogueMember $footing 'point') 0.01))
        } })
    $steps.Add(@{ Key = 's-wall-foundation'; Es = 'Cimiento corrido bajo el muro norte'; Command = 'foundation.create_wall'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'structural_wall_foundation', 'wall-north'; [ordered]@{ host_wall_uid = $picked['wall-north']; type = $picked['structural_wall_foundation'] } }
        Check = {
            param($r)
            $foundation = Get-CataloguePath $r 'readback.wall_foundation'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'cimiento = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $foundation 'unique_id')),
                (Test-CatalogueSame 'tipo' $picked['structural_wall_foundation'] (Get-CatalogueMember $foundation 'type_unique_id')),
                (Test-CatalogueSame 'muro anfitrión' $picked['wall-north'] (Get-CatalogueMember $foundation 'host_wall_unique_id')))
        } })
    return , $steps.ToArray()
}

# P4-02 documentation, in its own floor plan with its own two grids 3000 mm apart.
function Get-CatalogueDocumentationSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'doc-title-blocks'; Es = 'Consultar carátulas (query.title_blocks)'; Command = 'query.title_blocks'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }
        Check = { param($r) $readback = Get-CatalogueMember $r 'readback'; $present = ($null -ne $readback) -and ($null -ne $readback.PSObject.Properties['title_blocks']); Test-CatalogueCondition 'lista title_blocks presente' 'presente' $(if ($present) { 'presente' } else { 'ausente' }) $present } })
    $steps.Add(@{ Key = 'doc-plan'; Es = 'Planta para documentar'; Command = 'view.create_plan'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'level'; [ordered]@{ level = $picked['level']; kind = 'floor_plan'; name = (Get-CatalogueViewName 'documentacion') } }
        Check = { param($r) Test-CatalogueSame 'nivel de la planta' $picked['level'] (Get-CataloguePath $r 'readback.view.level_unique_id') } })
    $steps.Add(@{ Key = 'doc-grid-1'; Es = 'Eje vertical en x = -2000'; Command = 'grid.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { [ordered]@{ line = [ordered]@{ start = @(-2000, -1000); end = @(-2000, 5000) } } }; Check = { param($r) $null } })
    $steps.Add(@{ Key = 'doc-grid-2'; Es = 'Eje vertical en x = -5000'; Command = 'grid.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { [ordered]@{ line = [ordered]@{ start = @(-5000, -1000); end = @(-5000, 5000) } } }; Check = { param($r) $null } })
    $steps.Add(@{ Key = 'doc-dim-grids'; Es = 'Cota entre los dos ejes (3000 mm)'; Command = 'dimension.create_linear'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ view_uid = (Created 'doc-plan'); first_uid = (Created 'doc-grid-1'); second_uid = (Created 'doc-grid-2') } }
        Check = {
            param($r)
            $dimension = Get-CataloguePath $r 'readback.dimension'
            $references = Get-CatalogueList $dimension 'reference_unique_ids'
            Join-CatalogueReasons @(
                (Test-CatalogueNumber 'valor de la cota (mm)' 3000 (Get-CatalogueMember $dimension 'value_mm') 0.01),
                (Test-CatalogueSame 'vista de la cota' (Created 'doc-plan') (Get-CatalogueMember $dimension 'view_unique_id')),
                (Test-CatalogueContains 'referencia: primer eje' (Created 'doc-grid-1') $references),
                (Test-CatalogueContains 'referencia: segundo eje' (Created 'doc-grid-2') $references))
        } })
    # Both side faces of the v1 south and north walls (centre lines 4000 mm apart). One face pair measures 4000 - width
    # and the other 4000 + width; which one Revit calls exterior depends on the drawing direction of the walls (the v1
    # rectangle is counter-clockwise), which the executor does not decide, so only |value - 4000| = width per face and
    # |exterior - interior| = 2 x width are asserted.
    foreach ($face in @('interior', 'exterior')) {
        $steps.Add(@{ Key = "doc-dim-walls-$face"; Es = "Cota entre las caras $face (wall_face $face) de los muros sur y norte"; Command = 'dimension.create_linear'; Expect = @{ Status = 'succeeded' }; Face = $face
            Build = { Need 'wall-south', 'wall-north'; [ordered]@{ view_uid = (Created 'doc-plan'); first_uid = $picked['wall-south']; second_uid = $picked['wall-north']; wall_face = $spec.Face } }
            Check = {
                param($r)
                $width = Get-CatalogueWallTypeWidth (Response 'query-wall-types') $picked['wall_type']
                $value = Get-CataloguePath $r 'readback.dimension.value_mm'
                $gap = if (Test-CatalogueNear $value 0 1e12) { [Math]::Abs([double]$value - 4000) } else { $null }
                $references = Get-CatalogueArray $r 'readback.dimension.reference_unique_ids'
                $checks = @(
                    (Test-CatalogueNumber "|valor - 4000 mm entre ejes| = espesor del tipo de muro (caras $($spec.Face), mm)" ([double]$width) $gap 0.5),
                    (Test-CatalogueContains 'referencia: muro sur' $picked['wall-south'] $references),
                    (Test-CatalogueContains 'referencia: muro norte' $picked['wall-north'] $references))
                if ($spec.Face -eq 'exterior') {
                    $interior = Get-CataloguePath (Response 'doc-dim-walls-interior') 'readback.dimension.value_mm'
                    $difference = if ((Test-CatalogueNear $value 0 1e12) -and (Test-CatalogueNear $interior 0 1e12)) { [Math]::Abs([double]$value - [double]$interior) } else { $null }
                    $checks += Test-CatalogueNumber '|cota exterior - cota interior| = 2 x espesor (mm)' (2 * [double]$width) $difference 1
                }
                Join-CatalogueReasons $checks
            } })
    }
    $steps.Add(@{ Key = 'doc-tag-wall'; Es = 'Etiqueta del muro sur (sin directriz)'; Command = 'tag.create'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-south'; [ordered]@{ view_uid = (Created 'doc-plan'); element_uid = $picked['wall-south'] } }
        Check = {
            param($r)
            $tag = Get-CataloguePath $r 'readback.tag'
            Join-CatalogueReasons @(
                (Test-CatalogueContains 'etiqueta del muro sur' $picked['wall-south'] (Get-CatalogueList $tag 'tagged_unique_ids')),
                (Test-CatalogueSame 'sin directriz' $false (Get-CatalogueMember $tag 'has_leader')),
                (Test-CatalogueSame 'no huérfana' $false (Get-CatalogueMember $tag 'orphaned')))
        } })
    $steps.Add(@{ Key = 'doc-tag-door'; Es = 'Etiqueta de la puerta con directriz y cabeza en (1500,-1500)'; Command = 'tag.create'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ view_uid = (Created 'doc-plan'); element_uid = (Created 'door'); head = @(1500, -1500); leader = $true } }
        Check = {
            param($r)
            $tag = Get-CataloguePath $r 'readback.tag'
            Join-CatalogueReasons @(
                (Test-CatalogueContains 'etiqueta de la puerta' (Created 'door') (Get-CatalogueList $tag 'tagged_unique_ids')),
                (Test-CatalogueSame 'con directriz' $true (Get-CatalogueMember $tag 'has_leader')),
                (Test-CataloguePoint 'cabeza (mm)' @(1500, -1500) (Get-CatalogueMember $tag 'head') 1))
        } })
    # Schedule fields are matched by SchedulableField.GetName, Revit's localized name; there is no built-in id form.
    $steps.Add(@{ Key = 'doc-schedule'; Es = 'Tabla de puertas con Mark y Type, ordenada por Mark'; Command = 'schedule.create'; Expect = @{ Status = 'succeeded' }
        Note = 'precondición: Revit con la interfaz en inglés (los campos de tabla Mark y Type se buscan por su nombre visible)'
        Build = { [ordered]@{ category = 'door'; fields = @('Mark', 'Type'); sort_by = 'Mark'; name = (Get-CatalogueViewName 'puertas') } }
        Check = {
            param($r)
            $schedule = Get-CataloguePath $r 'readback.schedule'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'categoría' 'OST_Doors' (Get-CatalogueMember $schedule 'built_in_category')),
                (Test-CatalogueSame 'campos' 'Mark,Type' ((Get-CatalogueList $schedule 'fields') -join ',')),
                (Test-CatalogueSame 'orden' 'Mark' ((Get-CatalogueList $schedule 'sort_fields') -join ',')))
        } })
    $steps.Add(@{ Key = 'doc-sheet'; Es = 'Lámina sin carátula'; Command = 'sheet.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { [ordered]@{ number = "C04-r$Run-A101"; name = 'Prueba' } }
        Check = {
            param($r)
            $sheet = Get-CataloguePath $r 'readback.sheet'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'número' "C04-r$Run-A101" (Get-CatalogueMember $sheet 'number')),
                (Test-CatalogueSame 'nombre' 'Prueba' (Get-CatalogueMember $sheet 'name')))
        } })
    $steps.Add(@{ Key = 'doc-sheet-retry'; Es = 'Repetir la lámina con el mismo identificador (no debe duplicarse)'; Command = 'sheet.create'; ReuseOf = 'doc-sheet'; Expect = @{ Status = 'succeeded'; Replayed = $true; Created = 1 }
        Build = { [ordered]@{ number = "C04-r$Run-A101"; name = 'Prueba' } }
        Check = { param($r) Test-CatalogueSame 'el reintento devuelve la misma lámina' (Created 'doc-sheet') (Get-CatalogueFirstCreated $r) } })
    $steps.Add(@{ Key = 'doc-place-plan'; Es = 'Colocar la planta en la lámina en (420,297)'; Command = 'sheet.place_view'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ sheet_uid = (Created 'doc-sheet'); view_uid = (Created 'doc-plan'); center = @(420, 297) } }
        Check = {
            param($r)
            $placement = Get-CataloguePath $r 'readback.placement'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'lámina' (Created 'doc-sheet') (Get-CatalogueMember $placement 'sheet_unique_id')),
                (Test-CatalogueSame 'vista' (Created 'doc-plan') (Get-CatalogueMember $placement 'view_unique_id')),
                (Test-CataloguePoint 'centro (mm de lámina)' @(420, 297) (Get-CatalogueMember $placement 'center') 1))
        } })
    $steps.Add(@{ Key = 'doc-place-schedule'; Es = 'Colocar la tabla de puertas en la lámina en (100,100)'; Command = 'sheet.place_view'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ sheet_uid = (Created 'doc-sheet'); view_uid = (Created 'doc-schedule'); center = @(100, 100) } }
        Check = {
            param($r)
            $placement = Get-CataloguePath $r 'readback.placement'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'lámina' (Created 'doc-sheet') (Get-CatalogueMember $placement 'sheet_unique_id')),
                (Test-CatalogueSame 'tabla' (Created 'doc-schedule') (Get-CatalogueMember $placement 'view_unique_id')))
        } })
    $steps.Add(@{ Key = 'doc-place-plan-again'; Es = 'Colocar otra vez la misma planta (debe rechazarse nombrando la lámina)'; Command = 'sheet.place_view'; Expect = @{ Status = 'failed'; Code = 'args_invalid' }
        Build = { [ordered]@{ sheet_uid = (Created 'doc-sheet'); view_uid = (Created 'doc-plan'); center = @(200, 200) } }
        Check = { param($r) $detail = [string](Get-CatalogueMember $r 'detail'); Test-CatalogueCondition 'el rechazo nombra la lámina' "C04-r$Run-A101" $detail ($detail -like "*C04-r$Run-A101*") } })
    # P4-14: the documentation update of a moved wall through doc-update --execute, last in this group (see Get-CatalogueDocUpdateSteps).
    foreach ($step in (Get-CatalogueDocUpdateSteps)) { $steps.Add($step) }
    return , $steps.ToArray()
}

# --- P4-14: the documentation update of a moved wall through `doc-update --execute` (P4-12), end of documentation ---
# The update is no longer a hand-built plan sent step by step (P4-11): the CLI verb plans it from a BIM-IR before and
# after and runs it over the same pipe. The session makes its own wall (-9000,-6000)-(-6000,-6000) and a door hosted on
# it (centre 1450 mm along it), describes both in a small BIM-IR fixture (one level, the wall, the door [offset 1.0 m,
# width 0.9 m]: New-CatalogueDocUpdateProject) and runs four doc-update calls, each a step of the sequence sent with
# the -InvokeDocUpdate invoker (owner: Invoke-CatalogueDocUpdate; the CLI writes the plan file and prints one line):
#   0. doc-up-elements (P4-16): the CLI doc-realized verb (P4-15, offline) on this run's own record (schema_version 1,
#      document_unique_id, every earlier step as the owner session records it), the fixture, the assignments
#      element:<wall id> -> doc-up-wall and element:<door id> -> doc-up-door and the test document's UniqueId, through
#      the same -InvokeDocUpdate channel (a request with a record member). Its output file (the two model elements:
#      "element:<id>" -> the first UniqueId each step created) is checked against the printed sha256 and the created
#      UniqueIds and is what 1 and 2 pass to doc-update --realized (realized_file, realized_sha256; no realized member;
#      the step keeps a stub of its request: record_steps, record_document_unique_id, fixture, assignments, document,
#      record_file = the record the invoker kept, never the record itself). A realized_*
#      refusal (the record does not say what the session believes it made) stops the whole run, its envelope kept in
#      the run folder; any other doc-realized failure fails only the update;
#   1. doc-up-realize-plan: a dry run (no --execute) with before = after = the fixture and the realized map of 0 (the
#      two model elements only). Every default documentation
#      item of the fixture (DocDefaults: level plan, its sheet and placement, the door tag, door and room schedules, the
#      schedules sheet and both placements: 9 keys) has no realization, so all 9 are forced (created). The plan file must
#      hash to the printed sha256; its keys, command counts, plan level and tag (door, head at the door centre) checked;
#   2. doc-up-realize: the same request with --execute --expected-document <sha256 of 1> (the plan sha from its own dry
#      run): every step done, sent once, not replayed, one new UniqueId each; the printed realized map is the element
#      entries plus doc:<key> = the created UniqueId of that key's step;
#   3. doc-up-move: element.move of the wall (0, 1000, 0) in Revit (the door goes with it);
#   4. doc-up-update-plan: a dry run with after = the fixture with the wall 1000 mm north and the realized map of 2. Only
#      the door tag reads the moved wall (its head follows the host axis), so exactly tag-door is regenerated: a dry-run
#      probe of its stale tag (two passes inside the CLI, no dependents), then one element.delete of the stale tag and
#      one tag.create on the realized plan for the door at the moved head;
#   5. doc-up-update: --execute --expected-document <sha256 of 4>: delete then create done, the realized map of 2 with
#      only doc:tag-door changed to the new tag;
#   6. doc-up-stale-gone / doc-up-tag-new: query.inspect of the stale tag (reference_not_found) and of the new one.
# A doc-update refusal (any typed envelope, e.g. dependent_unknown, doc_update_plan_changed) fails its step; what needs
# it is not sent (Need). A per-step report entry with reply_lost (no_response, pipe_io_error, pipe_read_timeout,
# response_invalid: the write may have run, its reply is missing) stops the whole run like a lost pipe: nothing more
# is sent. The CLI never re-sends such a write (P4-12) and this session never retries it.
# Writes: only the documentation set of this fixture and the session's own wall and door, on the test copy. Names:
# "Level 1 Plan", sheets A101 and A900, "Door Schedule", "Room Schedule" (DocDefaults' defaults, unchanged).
$script:CatalogueDocUpdateProjectId = '5f1c0e2a-4b3d-4c5e-8f60-7a8b9c0d1e20'
$script:CatalogueDocUpdateLevelId = '5f1c0e2a-4b3d-4c5e-8f60-7a8b9c0d1e21'
$script:CatalogueDocUpdateWallId = '5f1c0e2a-4b3d-4c5e-8f60-7a8b9c0d1e22'
$script:CatalogueDocUpdateDoorId = '5f1c0e2a-4b3d-4c5e-8f60-7a8b9c0d1e23'
$script:CatalogueDocUpdateWall = @(@(-9000, -6000), @(-6000, -6000))
$script:CatalogueDocUpdateShiftMm = 1000
$script:CatalogueDocUpdateDoorOffsetMm = 1000
$script:CatalogueDocUpdateDoorWidthMm = 900
$script:CatalogueDocUpdateWallThicknessMm = 200
# The catalogue commands of the first (forced) plan, by count.
$script:CatalogueDocUpdateCommands = [ordered]@{ 'schedule.create' = 2; 'sheet.create' = 2; 'sheet.place_view' = 3; 'tag.create' = 1; 'view.create_plan' = 1 }
$script:CatalogueUniqueIdPattern = '\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-[0-9a-fA-F]{8}\z'

# A DocDefaults key: prefix + the BIM-IR id in "N" form.
function Get-CatalogueDocUpdateKey([string]$Prefix, [string]$Id) { return $Prefix + $Id.Replace('-', '').ToLowerInvariant() }

# The 9 default documentation keys of the fixture, sorted ordinally.
function Get-CatalogueDocUpdateKeys {
    $level = $script:CatalogueDocUpdateLevelId
    $keys = [string[]]@((Get-CatalogueDocUpdateKey 'place-plan-' $level), 'place-schedule-doors', 'place-schedule-rooms', (Get-CatalogueDocUpdateKey 'plan-' $level),
        'schedule-doors', 'schedule-rooms', (Get-CatalogueDocUpdateKey 'sheet-' $level), 'sheet-schedules', (Get-CatalogueDocUpdateKey 'tag-door-' $script:CatalogueDocUpdateDoorId))
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    return , $keys
}

# The BIM-IR fixture (Project JSON members as System.Text.Json reads them) with the wall $ShiftMm north.
function New-CatalogueDocUpdateProject([double]$ShiftMm) {
    $provenance = [ordered]@{ Source = 'catalogue-c04-p4-14'; Approval = 'owner-test-copy' }
    $y = ([double]$script:CatalogueDocUpdateWall[0][1] + $ShiftMm) / 1000
    $wall = @(([double]$script:CatalogueDocUpdateWall[0][0] / 1000), $y, ([double]$script:CatalogueDocUpdateWall[1][0] / 1000), $y, ($script:CatalogueWallHeightMm / 1000), ($script:CatalogueDocUpdateWallThicknessMm / 1000))
    return [ordered]@{
        Id = $script:CatalogueDocUpdateProjectId
        Revision = 0
        Elements = @(
            [ordered]@{ Id = $script:CatalogueDocUpdateLevelId; Kind = 'level'; GeometryMetres = @([double]0); Dependencies = @(); Provenance = $provenance },
            [ordered]@{ Id = $script:CatalogueDocUpdateWallId; Kind = 'wall'; GeometryMetres = $wall; Dependencies = @($script:CatalogueDocUpdateLevelId); Provenance = $provenance },
            [ordered]@{ Id = $script:CatalogueDocUpdateDoorId; Kind = 'door'; GeometryMetres = @(($script:CatalogueDocUpdateDoorOffsetMm / 1000), ($script:CatalogueDocUpdateDoorWidthMm / 1000)); Dependencies = @($script:CatalogueDocUpdateWallId); Provenance = $provenance })
    }
}

# The door tag head DocRegeneration computes: the door centre on the wall axis, $ShiftMm north (mm).
function Get-CatalogueDocUpdateHead([double]$ShiftMm) {
    return [double[]]@(([double]$script:CatalogueDocUpdateWall[0][0] + $script:CatalogueDocUpdateDoorOffsetMm + $script:CatalogueDocUpdateDoorWidthMm / 2), ([double]$script:CatalogueDocUpdateWall[0][1] + $ShiftMm))
}

# The realized model elements: "element:<BIM-IR id>" -> the UniqueId its catalogue step created (P4-16: only the
# oracle of doc-up-elements; the map doc-update reads comes from doc-realized).
function Get-CatalogueDocUpdateElements {
    return [ordered]@{ ('element:' + $script:CatalogueDocUpdateWallId) = (Created 'doc-up-wall'); ('element:' + $script:CatalogueDocUpdateDoorId) = (Created 'doc-up-door') }
}

# One doc-update request: before/after fixtures, realized map, bindings (the sequence fills bindings.document with the
# step's document), --accept values (none) and expected_document ($null: dry run; a sha256: --execute). P4-16:
# $RealizedFile (with its $RealizedSha) names the doc-realized output that doc-update --realized reads; the request
# then has no realized member (one source of truth) and $Realized is ignored.
function New-CatalogueDocUpdateRequest([double]$BeforeShiftMm, [double]$AfterShiftMm, $Realized, $Expected, [string]$RealizedFile, [string]$RealizedSha) {
    $request = [ordered]@{ before = (New-CatalogueDocUpdateProject $BeforeShiftMm); after = (New-CatalogueDocUpdateProject $AfterShiftMm) }
    if ($RealizedFile) { $request['realized_file'] = $RealizedFile; $request['realized_sha256'] = $RealizedSha }
    else { $request['realized'] = $Realized }
    $request['bindings'] = [ordered]@{ document = $null; levels = [ordered]@{ $script:CatalogueDocUpdateLevelId = [string]$picked['level'] } }
    $request['accept'] = @()
    $request['expected_document'] = $Expected
    return $request
}

# P4-16: the doc-realized request of doc-up-elements: the fixture of the first plan and the element sources assigned to
# the steps that created them; the sequence fills record (this run's own record so far) and document.
function New-CatalogueDocRealizedRequest {
    return [ordered]@{
        record = $null
        fixture = (New-CatalogueDocUpdateProject 0)
        assignments = [ordered]@{ ('element:' + $script:CatalogueDocUpdateWallId) = 'doc-up-wall'; ('element:' + $script:CatalogueDocUpdateDoorId) = 'doc-up-door' }
        document = $null
    }
}

# Status and exit code of one doc-update (or doc-realized) line against $Expect (Status, Code).
function Test-CatalogueDocUpdateLine($Response, $ExitCode, [hashtable]$Expect, [string]$Verb = 'doc-update') {
    if ($null -eq $Response) { return 'no JSON response' }
    $status = Get-CatalogueMember $Response 'status'
    $code = Get-CatalogueMember $Response 'code'
    if ($Expect.Status -eq 'failed') {
        if ($status -ne 'failed') { return "${Verb}: expected failure $($Expect.Code), got status $status" }
        if ($code -ne $Expect.Code) { return "${Verb}: expected code $($Expect.Code), got $code" }
        if ($ExitCode -eq 0) { return "${Verb}: failure reported with exit code 0" }
        return $null
    }
    if ($status -ne 'succeeded') { return "${Verb}: expected success, got ${status}/${code}: $(Get-CatalogueMember $Response 'field') $(Get-CatalogueMember $Response 'detail')" }
    if ($ExitCode -ne 0) { return "${Verb}: success reported with exit code $ExitCode" }
    return $null
}

# P4-16, step 0: the doc-realized output. The file it names must hash to the printed sha256 and bytes and hold exactly
# the two element sources -> the first UniqueId the wall and door steps created; records doc_up_elements = @{ File;
# Sha } (the file doc-update --realized reads, and its sha256).
function Test-CatalogueDocRealizedElements($Response) {
    $path = [string](Get-CatalogueMember $Response 'output')
    if (!$path -or !(Test-Path -LiteralPath $path -PathType Leaf)) { return (Test-CatalogueCondition 'archivo del mapa realizado escrito' 'presente' $path $false) }
    $bytes = [IO.File]::ReadAllBytes($path)
    $fileSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $printed = [string](Get-CatalogueMember $Response 'sha256')
    $parsed = $null
    try { $parsed = [Text.UTF8Encoding]::new($false).GetString($bytes) | ConvertFrom-Json -ErrorAction Stop } catch { $parsed = $null }
    $map = [ordered]@{}
    if ($null -ne $parsed -and $parsed -is [Management.Automation.PSCustomObject]) { foreach ($property in $parsed.PSObject.Properties) { $map[$property.Name] = [string]$property.Value } }
    $reason = Join-CatalogueReasons @(
        (Test-CatalogueCondition 'sha256 del mapa realizado = sha256 informado' $printed $fileSha ($printed -match '\A[0-9a-fA-F]{64}\z' -and [string]::Equals($printed, $fileSha, [StringComparison]::OrdinalIgnoreCase))),
        (Test-CatalogueSame 'bytes del mapa realizado' $bytes.Length (Get-CatalogueMember $Response 'bytes')),
        (Test-CatalogueSame 'entradas del mapa realizado' 2 (Get-CatalogueMember $Response 'entries')),
        (Test-CatalogueSame 'mapa realizado (doc-realized): el muro y la puerta = lo que crearon sus pasos' (Get-CatalogueMapText (Get-CatalogueDocUpdateElements)) (Get-CatalogueMapText $map)))
    if ($null -eq $reason) { $picked['doc_up_elements'] = @{ File = $path; Sha = $printed } }
    return $reason
}

# The plan file a doc-update line names: it must exist and hash to the printed sha256 and bytes. Returns @{ Reason; Plan }.
function Read-CatalogueDocUpdatePlanFile($Response) {
    $path = [string](Get-CatalogueMember $Response 'output')
    if (!$path -or !(Test-Path -LiteralPath $path -PathType Leaf)) { return @{ Reason = (Test-CatalogueCondition 'archivo del plan escrito' 'presente' $path $false); Plan = $null } }
    $bytes = [IO.File]::ReadAllBytes($path)
    $fileSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $printed = [string](Get-CatalogueMember $Response 'sha256')
    $plan = $null
    try { $plan = [Text.UTF8Encoding]::new($false).GetString($bytes) | ConvertFrom-Json -ErrorAction Stop } catch { $plan = $null }
    $reason = Join-CatalogueReasons @(
        (Test-CatalogueCondition 'sha256 del archivo del plan = sha256 informado' $printed $fileSha ($printed -match '\A[0-9a-fA-F]{64}\z' -and [string]::Equals($printed, $fileSha, [StringComparison]::OrdinalIgnoreCase))),
        (Test-CatalogueSame 'bytes del archivo del plan' $bytes.Length (Get-CatalogueMember $Response 'bytes')),
        (Test-CatalogueCondition 'archivo del plan: JSON' 'JSON' $(if ($null -ne $plan) { 'JSON' } else { 'no JSON' }) ($null -ne $plan)))
    return @{ Reason = $reason; Plan = $plan }
}

# The dry summary counts of a doc-update line; applied false.
function Test-CatalogueDocUpdateSummary($Response, [hashtable]$Expected) {
    $rows = @(foreach ($name in 'regenerated', 'steps', 'forced', 'retired', 'probed', 'dependents') { Test-CatalogueSame "resumen: $name" $Expected[$name] (Get-CatalogueMember $Response $name) })
    return Join-CatalogueReasons (@($rows) + @(Test-CatalogueSame 'applied (simulacro, nada escrito)' $false (Get-CatalogueMember $Response 'applied')))
}

# The plan's steps (Replacement.Commands.Steps) with their args parsed: @{ Command; Args; OperationId }.
function Get-CatalogueDocUpdatePlanSteps($Plan) {
    # Get-CatalogueArray writes its list as one object: enumerate it from a variable.
    $raw = Get-CatalogueArray $Plan 'Replacement.Commands.Steps'
    $steps = [Collections.Generic.List[hashtable]]::new()
    foreach ($step in $raw) {
        $text = [string](Get-CatalogueMember $step 'ArgsJson')
        $parsed = $null
        try { $parsed = $text | ConvertFrom-Json -ErrorAction Stop } catch { $parsed = $null }
        $steps.Add(@{ Command = [string](Get-CatalogueMember $step 'Command'); Args = $parsed; OperationId = [string](Get-CatalogueMember $step 'OperationId') })
    }
    return , $steps.ToArray()
}

# "command=count" of a list of commands, sorted ordinally.
function Get-CatalogueCommandCounts([string[]]$Commands) {
    $names = [string[]]@($Commands | Select-Object -Unique)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    return (@($names | ForEach-Object { $name = $_; "$name=$(@($Commands | Where-Object { $_ -ceq $name }).Count)" }) -join ',')
}

# A realized map (dictionary or parsed JSON object) as "source=UniqueId" lines sorted by source.
function Get-CatalogueMapText($Map) {
    $pairs = if ($Map -is [Collections.IDictionary]) { @($Map.Keys | ForEach-Object { "$_=$($Map[$_])" }) } elseif ($null -ne $Map) { @($Map.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) } else { @() }
    $sorted = [string[]]$pairs
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return ($sorted -join "`n")
}

# Step 1: the forced realization plan of the whole default documentation; records doc_up_realize_plan = @{ Sha }.
function Test-CatalogueDocUpdateRealizePlan($Response) {
    $file = Read-CatalogueDocUpdatePlanFile $Response
    if ($file.Reason) { return $file.Reason }
    $keys = Get-CatalogueDocUpdateKeys
    $steps = Get-CatalogueDocUpdatePlanSteps $file.Plan
    $expected = Get-CatalogueCommandCounts ([string[]]@(foreach ($entry in $script:CatalogueDocUpdateCommands.GetEnumerator()) { for ($i = 0; $i -lt $entry.Value; $i++) { $entry.Key } }))
    $tag = @($steps | Where-Object { $_.Command -eq 'tag.create' }) | Select-Object -First 1
    $view = @($steps | Where-Object { $_.Command -eq 'view.create_plan' }) | Select-Object -First 1
    $reason = Join-CatalogueReasons @(
        (Test-CatalogueDocUpdateSummary $Response @{ regenerated = $keys.Count; steps = $keys.Count; forced = $keys.Count; retired = 0; probed = 0; dependents = 0 }),
        (Test-CatalogueSame 'claves regeneradas: la documentación por defecto del modelo BIM-IR' ($keys -join ',') ((Get-CatalogueArray $file.Plan 'Regeneration.Regenerated') -join ',')),
        (Test-CatalogueSame 'claves forzadas: toda la documentación sin realizar' ($keys -join ',') ((Get-CatalogueArray $file.Plan 'Forced') -join ',')),
        (Test-CatalogueSame 'sin claves retiradas' '' ((Get-CatalogueArray $file.Plan 'Retired') -join ',')),
        (Test-CatalogueSame 'órdenes del plan (solo creaciones)' $expected (Get-CatalogueCommandCounts ([string[]]@($steps | ForEach-Object { $_.Command })))),
        (Test-CatalogueSame 'planta: el nivel elegido' $picked['level'] (Get-CatalogueMember $(if ($view) { $view.Args }) 'level')),
        (Test-CatalogueSame 'etiqueta: la puerta de la actualización' (Created 'doc-up-door') (Get-CatalogueMember $(if ($tag) { $tag.Args }) 'element_uid')),
        (Test-CataloguePoint 'etiqueta: cabeza en el centro de la puerta (mm)' (Get-CatalogueDocUpdateHead 0) (Get-CatalogueMember $(if ($tag) { $tag.Args }) 'head') 0.5))
    if ($null -eq $reason) { $picked['doc_up_realize_plan'] = @{ Sha = [string](Get-CatalogueMember $Response 'sha256') } }
    return $reason
}

# What every --execute line must say: applied, completed, the reviewed plan (same sha256), nothing gone, $Count report
# entries, each done, sent once and executed in this invocation (not replayed). Returns @{ Reason; Report }.
function Test-CatalogueDocUpdateRun($Response, [string]$PlanPick, [int]$Count) {
    # The plan file of the --execute call still hashes to what it printed (the reviewed sha256, checked below).
    $file = Read-CatalogueDocUpdatePlanFile $Response
    if ($file.Reason) { return @{ Reason = $file.Reason; Report = @() } }
    $report = Get-CatalogueList $Response 'report'
    $bad = @($report | Where-Object { [string](Get-CatalogueMember $_ 'status') -ne 'done' -or [string](Get-CatalogueMember $_ 'sent') -ne '1' -or (Get-CatalogueMember $_ 'replayed') -ne $false })
    $reason = Join-CatalogueReasons @(
        (Test-CatalogueSame 'applied' $true (Get-CatalogueMember $Response 'applied')),
        (Test-CatalogueSame 'run' 'completed' (Get-CatalogueMember $Response 'run')),
        (Test-CatalogueCondition 'sha256 = el del simulacro revisado' $picked[$PlanPick].Sha (Get-CatalogueMember $Response 'sha256') ([string]::Equals([string]$picked[$PlanPick].Sha, [string](Get-CatalogueMember $Response 'sha256'), [StringComparison]::OrdinalIgnoreCase))),
        (Test-CatalogueSame 'gone (nada ya borrado)' '' ((Get-CatalogueList $Response 'gone') -join ',')),
        (Test-CatalogueSame 'pasos en el informe' $Count $report.Count),
        (Test-CatalogueCondition 'cada paso done, enviado una vez, ejecutado ahora (no replayed)' 'todos' @($bad | ForEach-Object { "$(Get-CatalogueMember $_ 'key') $(Get-CatalogueMember $_ 'command') $(Get-CatalogueMember $_ 'status')/$(Get-CatalogueMember $_ 'code')" }) ($bad.Count -eq 0)))
    return @{ Reason = $reason; Report = $report }
}

# Step 2: the realization run; records doc_up_realized = the printed realized map (ordered by source).
function Test-CatalogueDocUpdateRealize($Response) {
    $keys = Get-CatalogueDocUpdateKeys
    $run = Test-CatalogueDocUpdateRun $Response 'doc_up_realize_plan' $keys.Count
    if ($run.Reason) { return $run.Reason }
    $want = Get-CatalogueDocUpdateElements
    $created = [Collections.Generic.List[string]]::new()
    foreach ($entry in $run.Report) {
        $uids = Get-CatalogueList $entry 'created'
        $created.Add([string](Get-CatalogueFirst $uids))
        if ($uids.Count -eq 1) { $want['doc:' + [string](Get-CatalogueMember $entry 'key')] = [string]$uids[0] }
    }
    $entryKeys = [string[]]@($run.Report | ForEach-Object { [string](Get-CatalogueMember $_ 'key') })
    [Array]::Sort($entryKeys, [StringComparer]::Ordinal)
    $malformed = @($created | Where-Object { $_ -notmatch $script:CatalogueUniqueIdPattern })
    $distinct = @($created | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique).Count
    $realized = Get-CatalogueMember $Response 'realized'
    $reason = Join-CatalogueReasons @(
        (Test-CatalogueSame 'claves del informe' ($keys -join ',') ($entryKeys -join ',')),
        (Test-CatalogueCondition 'un UniqueId nuevo y distinto por paso' $keys.Count @($created) ($malformed.Count -eq 0 -and $distinct -eq $keys.Count -and @(@($run.Report) | Where-Object { (Get-CatalogueList $_ 'created').Count -ne 1 }).Count -eq 0)),
        (Test-CatalogueSame 'mapa realizado: los elementos del modelo más doc:<clave> = lo creado' (Get-CatalogueMapText $want) (Get-CatalogueMapText $realized)))
    if ($null -eq $reason) {
        $map = [ordered]@{}
        foreach ($property in $realized.PSObject.Properties) { $map[$property.Name] = [string]$property.Value }
        $picked['doc_up_realized'] = $map
    }
    return $reason
}

# Step 4: the update plan after the move; records doc_up_update_plan = @{ Sha }.
function Test-CatalogueDocUpdateUpdatePlan($Response) {
    $file = Read-CatalogueDocUpdatePlanFile $Response
    if ($file.Reason) { return $file.Reason }
    $tagKey = Get-CatalogueDocUpdateKey 'tag-door-' $script:CatalogueDocUpdateDoorId
    $realized = $picked['doc_up_realized']
    $steps = Get-CatalogueDocUpdatePlanSteps $file.Plan
    $delete = Get-CatalogueFirst $steps 0
    $create = Get-CatalogueFirst $steps 1
    $deleteArgs = if ($delete) { $delete.Args }
    $createArgs = if ($create) { $create.Args }
    $forced = Get-CatalogueArray $file.Plan 'Forced'
    $retired = Get-CatalogueArray $file.Plan 'Retired'
    $reason = Join-CatalogueReasons @(
        (Test-CatalogueDocUpdateSummary $Response @{ regenerated = 1; steps = 2; forced = 0; retired = 0; probed = 1; dependents = 0 }),
        (Test-CatalogueSame 'regenerada solo la etiqueta de la puerta (lee el muro movido)' $tagKey ((Get-CatalogueArray $file.Plan 'Regeneration.Regenerated') -join ',')),
        (Test-CatalogueSame 'sin claves forzadas ni retiradas' '' (($forced + $retired) -join ',')),
        (Test-CatalogueSame 'órdenes: borrar la etiqueta obsoleta y crear la nueva' 'element.delete,tag.create' (@($steps | ForEach-Object { $_.Command }) -join ',')),
        (Test-CatalogueSame 'borrado: solo la etiqueta obsoleta' $realized["doc:$tagKey"] ((Get-CatalogueList $deleteArgs 'element_uids') -join ',')),
        (Test-CatalogueSame 'borrado: sin dependientes que confirmar' '' ((Get-CatalogueList $deleteArgs 'confirm_dependents') -join ',')),
        (Test-CatalogueSame 'etiqueta nueva: en la planta realizada' $realized['doc:' + (Get-CatalogueDocUpdateKey 'plan-' $script:CatalogueDocUpdateLevelId)] (Get-CatalogueMember $createArgs 'view_uid')),
        (Test-CatalogueSame 'etiqueta nueva: de la puerta' (Created 'doc-up-door') (Get-CatalogueMember $createArgs 'element_uid')),
        (Test-CataloguePoint "etiqueta nueva: cabeza en el centro de la puerta movida $($script:CatalogueDocUpdateShiftMm) mm (mm)" (Get-CatalogueDocUpdateHead $script:CatalogueDocUpdateShiftMm) (Get-CatalogueMember $createArgs 'head') 0.5))
    if ($null -eq $reason) { $picked['doc_up_update_plan'] = @{ Sha = [string](Get-CatalogueMember $Response 'sha256') } }
    return $reason
}

# Step 5: the update run; records doc_up_updated = @{ Stale; New } (the door tag's UniqueIds).
function Test-CatalogueDocUpdateUpdate($Response) {
    $run = Test-CatalogueDocUpdateRun $Response 'doc_up_update_plan' 2
    if ($run.Reason) { return $run.Reason }
    $tagKey = Get-CatalogueDocUpdateKey 'tag-door-' $script:CatalogueDocUpdateDoorId
    $stale = [string]$picked['doc_up_realized']["doc:$tagKey"]
    $delete = Get-CatalogueFirst $run.Report 0
    $create = Get-CatalogueFirst $run.Report 1
    $new = [string](Get-CatalogueFirst (Get-CatalogueList $create 'created'))
    $want = [ordered]@{}
    foreach ($entry in $picked['doc_up_realized'].GetEnumerator()) { $want[$entry.Key] = $entry.Value }
    $want["doc:$tagKey"] = $new
    $reason = Join-CatalogueReasons @(
        (Test-CatalogueSame 'informe: borrar y crear la etiqueta de la puerta' "$tagKey element.delete,$tagKey tag.create" (@($run.Report | ForEach-Object { "$(Get-CatalogueMember $_ 'key') $(Get-CatalogueMember $_ 'command')" }) -join ',')),
        (Test-CatalogueSame 'el borrado no crea nada' 0 (Get-CatalogueList $delete 'created').Count),
        (Test-CatalogueCondition 'una etiqueta nueva, no la obsoleta' 'otra' $new ($new -match $script:CatalogueUniqueIdPattern -and (Get-CatalogueList $create 'created').Count -eq 1 -and ![string]::Equals($new, $stale, [StringComparison]::OrdinalIgnoreCase))),
        (Test-CatalogueSame 'mapa realizado: igual salvo doc:tag-door = la etiqueta nueva' (Get-CatalogueMapText $want) (Get-CatalogueMapText (Get-CatalogueMember $Response 'realized'))))
    if ($null -eq $reason) { $picked['doc_up_updated'] = @{ Stale = $stale; New = $new } }
    return $reason
}

function Get-CatalogueDocUpdateSteps {
    $shift = $script:CatalogueDocUpdateShiftMm
    $along = $script:CatalogueDocUpdateDoorOffsetMm + $script:CatalogueDocUpdateDoorWidthMm / 2
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'doc-up-wall'; Es = 'Actualización de documentación (P4-14, doc-update --execute): muro propio de (-9000,-6000) a (-6000,-6000)'; Command = 'wall.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'level', 'wall_type'; New-CatalogueLineWall $picked['level'] $picked['wall_type'] $script:CatalogueDocUpdateWall[0] $script:CatalogueDocUpdateWall[1] }
        Check = { param($r) Test-CatalogueSame 'muro = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CataloguePath $r 'readback.wall.unique_id') } })
    $steps.Add(@{ Key = 'doc-up-door'; Es = "Actualización: puerta en ese muro, centro a $along mm de su inicio"; Command = 'opening.place'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'door'; [ordered]@{ category = 'door'; symbol = $picked['door']; host_wall_uid = (Created 'doc-up-wall'); position_along_mm = ($script:CatalogueDocUpdateDoorOffsetMm + $script:CatalogueDocUpdateDoorWidthMm / 2) } }
        Check = {
            param($r)
            $opening = Get-CataloguePath $r 'readback.opening'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'puerta = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $opening 'unique_id')),
                (Test-CatalogueSame 'anfitrión: el muro de la actualización' (Created 'doc-up-wall') (Get-CatalogueMember $opening 'host_wall_unique_id')))
        } })
    # P4-16: the wall and door must exist (else nothing of the update is sent); doc-realized then reads them from the record.
    $steps.Add(@{ Key = 'doc-up-elements'; Es = 'Actualización: doc-realized sobre el registro de esta ejecución (mapa realizado del muro y la puerta)'; Command = 'doc-realized'; DocRealized = $true; Expect = @{ Status = 'succeeded' }
        Build = { $null = Created 'doc-up-wall'; $null = Created 'doc-up-door'; New-CatalogueDocRealizedRequest }
        Check = { param($r) Test-CatalogueDocRealizedElements $r } })
    $steps.Add(@{ Key = 'doc-up-realize-plan'; Es = 'Actualización: doc-update en simulacro de la documentación por defecto del modelo BIM-IR (9 elementos sin realizar)'; Command = 'doc-update'; DocUpdate = $true; Expect = @{ Status = 'succeeded' }
        Build = { Need 'level', 'doc_up_elements'; $elements = $picked['doc_up_elements']; New-CatalogueDocUpdateRequest 0 0 $null $null $elements.File $elements.Sha }
        Check = { param($r) Test-CatalogueDocUpdateRealizePlan $r } })
    $steps.Add(@{ Key = 'doc-up-realize'; Es = 'Actualización: doc-update --execute del plan revisado (crea planta, láminas, tablas, colocaciones y la etiqueta de la puerta)'; Command = 'doc-update'; DocUpdate = $true; Expect = @{ Status = 'succeeded' }
        Build = { Need 'doc_up_elements', 'doc_up_realize_plan'; $elements = $picked['doc_up_elements']; New-CatalogueDocUpdateRequest 0 0 $null $picked['doc_up_realize_plan'].Sha $elements.File $elements.Sha }
        Check = { param($r) Test-CatalogueDocUpdateRealize $r } })
    $steps.Add(@{ Key = 'doc-up-move'; Es = "Actualización: mover el muro (0, $shift, 0) mm; la etiqueta de la puerta queda obsoleta"; Command = 'element.move'; Expect = @{ Status = 'succeeded'; Created = 0 }
        Build = { Need 'doc_up_realized'; [ordered]@{ element_uids = @(Created 'doc-up-wall'); vector_mm = @(0, $script:CatalogueDocUpdateShiftMm, 0) } }
        Check = {
            param($r)
            $moved = Get-CatalogueFirst (Get-CatalogueArray $r 'readback.moved')
            $a = $script:CatalogueDocUpdateWall[0]; $b = $script:CatalogueDocUpdateWall[1]; $d = $script:CatalogueDocUpdateShiftMm
            $reason = Join-CatalogueReasons @(
                (Test-CataloguePoint 'vector leído (mm)' @(0, $d, 0) (Get-CataloguePath $r 'readback.vector_mm') 0.01),
                (Test-CatalogueSame 'elemento movido' (Created 'doc-up-wall') (Get-CatalogueMember $moved 'unique_id')),
                (Test-CatalogueSegment 'antes (mm)' $a $b (Get-CatalogueMember $moved 'before') 1),
                (Test-CatalogueSegment 'después (mm)' @($a[0], ($a[1] + $d)) @($b[0], ($b[1] + $d)) (Get-CatalogueMember $moved 'after') 1))
            if ($null -eq $reason) { $picked['doc_up_moved'] = $true }
            $reason
        } })
    $steps.Add(@{ Key = 'doc-up-update-plan'; Es = "Actualización: doc-update en simulacro del muro movido $shift mm (dos pasadas de dependientes dentro de la CLI)"; Command = 'doc-update'; DocUpdate = $true; Expect = @{ Status = 'succeeded' }
        Build = { Need 'doc_up_moved'; New-CatalogueDocUpdateRequest 0 $script:CatalogueDocUpdateShiftMm $picked['doc_up_realized'] $null }
        Check = { param($r) Test-CatalogueDocUpdateUpdatePlan $r } })
    $steps.Add(@{ Key = 'doc-up-update'; Es = 'Actualización: doc-update --execute del plan revisado (borra la etiqueta obsoleta y crea la nueva)'; Command = 'doc-update'; DocUpdate = $true; Expect = @{ Status = 'succeeded' }
        Build = { Need 'doc_up_update_plan'; New-CatalogueDocUpdateRequest 0 $script:CatalogueDocUpdateShiftMm $picked['doc_up_realized'] $picked['doc_up_update_plan'].Sha }
        Check = { param($r) Test-CatalogueDocUpdateUpdate $r } })
    $steps.Add(@{ Key = 'doc-up-stale-gone'; Es = 'Actualización: inspeccionar la etiqueta obsoleta (debe responder reference_not_found)'; Command = 'query.inspect'; Expect = @{ Status = 'failed'; Code = 'reference_not_found' }
        Build = { Need 'doc_up_updated'; [ordered]@{ unique_id = $picked['doc_up_updated'].Stale } }
        Check = { param($r) $null } })
    $steps.Add(@{ Key = 'doc-up-tag-new'; Es = 'Actualización: inspeccionar la etiqueta nueva'; Command = 'query.inspect'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'doc_up_updated'; [ordered]@{ unique_id = $picked['doc_up_updated'].New } }
        Check = { param($r) Test-CatalogueSame 'elemento inspeccionado: la etiqueta nueva' $picked['doc_up_updated'].New (Get-CataloguePath $r 'readback.element.unique_id') } })
    return , $steps.ToArray()
}

# P2-03 journal.read: baseline, one CLI wall (skipped as add-in, seen in the diff), an empty read, two equal peeks.
function Get-CatalogueJournalSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'j-baseline'; Es = 'Leer el diario (línea base)'; Command = 'journal.read'; Wire = $true; Expect = @{ Status = 'succeeded' }
        Build = { $null }
        Check = {
            param($r)
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'grabador suscrito (attached)' $true (Get-CatalogueMember $r 'attached')),
                (Test-CatalogueSame 'primera lectura de este Revit (baseline_existed)' $false (Get-CataloguePath $r 'report.baseline_existed')),
                (Test-CatalogueSame 'documento' $DocumentUid (Get-CataloguePath $r 'report.document_unique_id')))
        } })
    $steps.Add(@{ Key = 'j-wall'; Es = 'Muro recto nuevo por la CLI, de (0,9000) a (3000,9000)'; Command = 'wall.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'level', 'wall_type'; New-CatalogueLineWall $picked['level'] $picked['wall_type'] @(0, 9000) @(3000, 9000) }
        Check = { param($r) $null } })
    $steps.Add(@{ Key = 'j-after'; Es = 'Leer el diario: el muro aparece en el diff y la escritura de la CLI no cuenta como externa'; Command = 'journal.read'; Wire = $true; Expect = @{ Status = 'succeeded' }
        Build = { $null = Response 'j-baseline'; $null = Created 'j-wall'; $null }
        Check = {
            param($r)
            $wallUid = Created 'j-wall'
            $before = Get-CatalogueMember (Response 'j-baseline') 'skipped_add_in'
            $skipped = Get-CatalogueMember $r 'skipped_add_in'
            $report = Get-CatalogueMember $r 'report'
            $added = @((Get-CatalogueArray $report 'diff.added') | ForEach-Object { Get-CatalogueMember $_ 'unique_id' })
            $external = @((Get-CatalogueArray $report 'entries') | Where-Object { (Get-CatalogueMember $_ 'origin') -eq 'external' -and ((Get-CatalogueList $_ 'added') -contains $wallUid) })
            Join-CatalogueReasons @(
                (Test-CatalogueCondition 'skipped_add_in creció (escritura propia del complemento)' "> $before" $skipped (($null -ne $skipped) -and ($null -ne $before) -and ([long]$skipped -gt [long]$before))),
                (Test-CatalogueSame 'línea base previa (baseline_existed)' $true (Get-CatalogueMember $report 'baseline_existed')),
                (Test-CatalogueContains 'diff.added incluye el muro nuevo' $wallUid $added),
                (Test-CatalogueCondition 'ninguna entrada externa con el muro nuevo' 0 $external.Count ($external.Count -eq 0)))
        } })
    $steps.Add(@{ Key = 'j-empty'; Es = 'Leer el diario otra vez sin cambios: vacío'; Command = 'journal.read'; Wire = $true; Expect = @{ Status = 'succeeded' }
        Build = { $null }
        Check = {
            param($r)
            $report = Get-CatalogueMember $r 'report'
            $sizes = @((Get-CatalogueArray $report 'entries').Count, (Get-CatalogueArray $report 'diff.added').Count, (Get-CatalogueArray $report 'diff.removed').Count, (Get-CatalogueArray $report 'diff.changed').Count)
            Test-CatalogueCondition 'entradas, agregados, quitados y cambiados' '0,0,0,0' ($sizes -join ',') (($sizes -join ',') -eq '0,0,0,0')
        } })
    $steps.Add(@{ Key = 'j-peek-1'; Es = 'Leer el diario con peek'; Command = 'journal.read'; Wire = $true; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{ peek = $true } }
        Check = { param($r) Test-CatalogueSame 'peek' $true (Get-CataloguePath $r 'report.peek') } })
    $steps.Add(@{ Key = 'j-peek-2'; Es = 'Repetir peek: misma respuesta byte a byte'; Command = 'journal.read'; Wire = $true; Expect = @{ Status = 'succeeded' }
        Build = { if (!$rawText.ContainsKey('j-peek-1')) { throw 'falta el primer peek' }; [ordered]@{ peek = $true } }
        Check = { param($r) Test-CatalogueSameText 'respuesta idéntica al primer peek' $rawText['j-peek-1'] $rawText['j-peek-2'] } })
    return , $steps.ToArray()
}

# --- P1-11: the commands added after P1-06, between the journal group and the destructive one ---
# Each group uses only the v1 picks (level, wall type, the v1 door) and its own elements, placed clear of everything
# earlier (x >= 29700 or y >= 15000): DirectShapes at x 50000-53200, barrel roofs at x 29700-43780, the transformed
# walls at y 15000-20500. ClientSide = $true marks a refusal the CLI decides before the pipe (RevitCommandCodes.ClientSide;
# the headless test proves it never reaches the pipe); Reason names the document.save_copy reason prefix.

# The open barrel profile of the extrusion roofs ([u, z] mm in the plane frame: u along the plane line from its start,
# z above the level) and its exact extent: the arc is symmetric, so its top is the through point.
$script:CatalogueBarrelProfile = @([ordered]@{ kind = 'arc'; start = @(-300, 2700); end = @(6300, 2700); through = @(3000, 3800) })
$script:CatalogueBarrelU = @(-300, 6300)
$script:CatalogueBarrelZ = @(2700, 3800)
$script:CatalogueBarrelExtrusion = @(0, 4000)

# The project elevation (mm) of a level in a query.levels reply (elevation_mm when the reply has no project elevation).
function Get-CatalogueLevelElevation($Response, [string]$LevelUid) {
    foreach ($level in (Get-CatalogueList (Get-CatalogueMember $Response 'readback') 'levels')) {
        if ([string](Get-CatalogueMember $level 'unique_id') -eq $LevelUid) {
            $project = Get-CatalogueMember $level 'project_elevation_mm'
            if ($null -ne $project) { return [double]$project }
            return [double](Get-CatalogueMember $level 'elevation_mm')
        }
    }
    throw "falta la elevación del nivel $LevelUid en la consulta de niveles"
}

# [x, y] of a location member ([x, y, z] in the move/rotate/mirror readback), or $null.
function Get-CataloguePlan($Location, [string]$Name) {
    $point = @(Get-CatalogueMember $Location $Name)
    if ($point.Count -lt 2 -or $null -eq $point[0] -or $null -eq $point[1]) { return $null }
    return @($point[0], $point[1])
}

# A curve location whose plan ends are A and B, in either order, within the tolerance.
function Test-CatalogueSegment([string]$What, [double[]]$A, [double[]]$B, $Location, [double]$Tolerance) {
    $s = Get-CataloguePlan $Location 'start'
    $e = Get-CataloguePlan $Location 'end'
    $ok = $false
    if ($null -ne $s -and $null -ne $e) {
        $forward = (Test-CatalogueNear $s[0] $A[0] $Tolerance) -and (Test-CatalogueNear $s[1] $A[1] $Tolerance) -and (Test-CatalogueNear $e[0] $B[0] $Tolerance) -and (Test-CatalogueNear $e[1] $B[1] $Tolerance)
        $backward = (Test-CatalogueNear $s[0] $B[0] $Tolerance) -and (Test-CatalogueNear $s[1] $B[1] $Tolerance) -and (Test-CatalogueNear $e[0] $A[0] $Tolerance) -and (Test-CatalogueNear $e[1] $A[1] $Tolerance)
        $ok = $forward -or $backward
    }
    return Test-CatalogueCondition $What @($A, $B) @($s, $e) $ok
}

# The model box (min/max [x, y, z] mm) of a profile u/z range swept along the plane's left normal from WStart to WEnd
# (or, Mirrored, along the right normal: a roof extruded to the wrong side), at the level elevation.
function Get-CatalogueExtrusionBox($Plane, [double[]]$URange, [double[]]$ZRange, [double]$WStart, [double]$WEnd, [double]$Elevation, [switch]$Mirrored) {
    $x0 = [double]$Plane['start'][0]; $y0 = [double]$Plane['start'][1]
    $dx = [double]$Plane['end'][0] - $x0; $dy = [double]$Plane['end'][1] - $y0
    $length = [Math]::Sqrt($dx * $dx + $dy * $dy)
    $ux = $dx / $length; $uy = $dy / $length; $nx = -$uy; $ny = $ux
    $ws = if ($Mirrored) { @(-$WEnd, -$WStart) } else { @($WStart, $WEnd) }
    $xs = [Collections.Generic.List[double]]::new(); $ys = [Collections.Generic.List[double]]::new()
    foreach ($u in $URange) { foreach ($w in $ws) { $xs.Add($x0 + $u * $ux + $w * $nx); $ys.Add($y0 + $u * $uy + $w * $ny) } }
    return @{ Min = @(($xs | Measure-Object -Minimum).Minimum, ($ys | Measure-Object -Minimum).Minimum, ($Elevation + $ZRange[0])); Max = @(($xs | Measure-Object -Maximum).Maximum, ($ys | Measure-Object -Maximum).Maximum, ($Elevation + $ZRange[1])) }
}

# The observed bounding_box_mm holds the expected box within the tolerance on every axis.
function Test-CatalogueBoxHolds($Box, $Expected, [double]$Tolerance) {
    $min = Get-CatalogueArray $Box 'min'
    $max = Get-CatalogueArray $Box 'max'
    if ($min.Count -ne 3 -or $max.Count -ne 3) { return $false }
    for ($k = 0; $k -lt 3; $k++) {
        if (!(Test-CatalogueNear $min[$k] 0 1e12) -or !(Test-CatalogueNear $max[$k] 0 1e12)) { return $false }
        if (!([double]$min[$k] -le $Expected.Min[$k] + $Tolerance -and [double]$max[$k] -ge $Expected.Max[$k] - $Tolerance)) { return $false }
    }
    return $true
}

# One direct_shape.create readback: the created element, category, kind, name, volume (within 1 mm on the side faces,
# as the executor) and the bounding box within 1 mm.
function Test-CatalogueDirectShape($Response, [string]$Category, [string]$BuiltIn, [string]$Kind, [string]$Name, [double[]]$Min, [double[]]$Max, [double]$VolumeMm3, [double]$PerimeterMm) {
    $shape = Get-CataloguePath $Response 'readback.direct_shape'
    $box = Get-CatalogueMember $shape 'bounding_box_mm'
    $tolerance = $PerimeterMm * ($Max[2] - $Min[2]) + 1e-6 * $VolumeMm3
    return Join-CatalogueReasons @(
        (Test-CatalogueSame 'forma = primer elemento creado' (Get-CatalogueFirstCreated $Response) (Get-CatalogueMember $shape 'unique_id')),
        (Test-CatalogueSame 'categoría' $Category (Get-CatalogueMember $shape 'category')),
        (Test-CatalogueSame 'categoría de Revit' $BuiltIn (Get-CatalogueMember $shape 'built_in_category')),
        (Test-CatalogueSame 'tipo de forma' $Kind (Get-CatalogueMember $shape 'shape_kind')),
        (Test-CatalogueSame 'nombre' $Name (Get-CatalogueMember $shape 'name')),
        (Test-CatalogueNumber 'volumen (mm3)' $VolumeMm3 (Get-CatalogueMember $shape 'volume_mm3') $tolerance),
        (Test-CataloguePoint 'caja: esquina mínima (mm)' $Min (Get-CatalogueMember $box 'min') 1),
        (Test-CataloguePoint 'caja: esquina máxima (mm)' $Max (Get-CatalogueMember $box 'max') 1))
}

# P9-09 direct_shape.create: a generic model box, a site extrusion on the walls' level, and a crossed outline the CLI refuses.
function Get-CatalogueDirectShapeSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'f-box'; Es = 'Caja DirectShape (modelo genérico) de (50000,0,0) a (51000,1000,1000)'; Command = 'direct_shape.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { [ordered]@{ category = 'generic_model'; shape = [ordered]@{ kind = 'box'; min = @(50000, 0, 0); max = @(51000, 1000, 1000) }; name = "C04 r$Run caja" } }
        Check = { param($r) Test-CatalogueDirectShape $r 'generic_model' 'OST_GenericModel' 'box' "C04 r$Run caja" @(50000, 0, 0) @(51000, 1000, 1000) 1000000000 4000 } })
    $steps.Add(@{ Key = 'f-extrusion'; Es = 'Roca DirectShape (sitio): planta 1200 x 800 mm en (52000,0), 900 mm de alto sobre el nivel de los muros'; Command = 'direct_shape.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'level'; [ordered]@{ category = 'site'; shape = [ordered]@{ kind = 'extrusion'; level = $picked['level']; outline = @(@(52000, 0), @(53200, 0), @(53200, 800), @(52000, 800)); height_mm = 900 }; name = "C04 r$Run roca" } }
        Check = {
            param($r)
            $z = Get-CatalogueLevelElevation (Response 'query-levels') $picked['level']
            Test-CatalogueDirectShape $r 'site' 'OST_Site' 'extrusion' "C04 r$Run roca" @(52000, 0, $z) @(53200, 800, ($z + 900)) 864000000 4000
        } })
    # Edges 0 and 2 cross at (54500, 500): boundary_self_intersecting at args.shape.outline[0], before the pipe.
    $steps.Add(@{ Key = 'f-bowtie'; Es = 'Pedido inválido: contorno cruzado (moño); debe rechazarse sin tocar Revit'; Command = 'direct_shape.create'; ClientSide = $true; Expect = @{ Status = 'failed'; Code = 'boundary_self_intersecting' }
        Build = { Need 'level'; [ordered]@{ category = 'generic_model'; shape = [ordered]@{ kind = 'extrusion'; level = $picked['level']; outline = @(@(54000, 0), @(55000, 1000), @(55000, 0), @(54000, 1000)); height_mm = 500 } } }
        Check = { param($r) Test-CatalogueSame 'campo rechazado' 'args.shape.outline[0]' (Get-CatalogueMember $r 'field') } })
    return , $steps.ToArray()
}

# One roof.create_extrusion readback for the barrel profile on $Plane: the roof and its new reference plane among the
# created elements, level, type, plane echo, extents, one arc through the crown, and the bounding box holding the
# profile swept to the plane's left (within 25 mm, as the executor) and not the profile swept to its right.
function Test-CatalogueRoofExtrusion($Response, $Plane) {
    $roof = Get-CataloguePath $Response 'readback.roof_extrusion'
    $z = Get-CatalogueLevelElevation (Response 'query-levels') $picked['level']
    $expected = Get-CatalogueExtrusionBox $Plane $script:CatalogueBarrelU $script:CatalogueBarrelZ $script:CatalogueBarrelExtrusion[0] $script:CatalogueBarrelExtrusion[1] $z
    $other = Get-CatalogueExtrusionBox $Plane $script:CatalogueBarrelU $script:CatalogueBarrelZ $script:CatalogueBarrelExtrusion[0] $script:CatalogueBarrelExtrusion[1] $z -Mirrored
    $box = Get-CatalogueMember $roof 'bounding_box_mm'
    $readProfile = Get-CatalogueList $roof 'profile_mm'
    $arc = Get-CatalogueFirst $readProfile
    $expectedText = "min $(ConvertTo-CatalogueText $expected.Min) max $(ConvertTo-CatalogueText $expected.Max)"
    $otherText = "no: min $(ConvertTo-CatalogueText $other.Min) max $(ConvertTo-CatalogueText $other.Max)"
    return Join-CatalogueReasons @(
        (Test-CatalogueSame 'techo = primer elemento creado' (Get-CatalogueFirstCreated $Response) (Get-CatalogueMember $roof 'unique_id')),
        (Test-CatalogueContains 'plano de referencia entre los elementos creados' (Get-CatalogueMember $roof 'reference_plane_unique_id') (Get-CatalogueList $Response 'created')),
        (Test-CatalogueSame 'nivel de referencia' $picked['level'] (Get-CatalogueMember $roof 'level_unique_id')),
        (Test-CatalogueSame 'tipo de techo' $picked['extrusion_roof_type'] (Get-CatalogueMember $roof 'type_unique_id')),
        (Test-CataloguePoint 'inicio del plano (mm)' @($Plane['start']) (Get-CatalogueMember $roof 'plane_start_mm') 0.01),
        (Test-CataloguePoint 'fin del plano (mm)' @($Plane['end']) (Get-CatalogueMember $roof 'plane_end_mm') 0.01),
        (Test-CatalogueNumber 'inicio de la extrusión (mm)' $script:CatalogueBarrelExtrusion[0] (Get-CatalogueMember $roof 'extrusion_start_mm') 1),
        (Test-CatalogueNumber 'fin de la extrusión (mm)' $script:CatalogueBarrelExtrusion[1] (Get-CatalogueMember $roof 'extrusion_end_mm') 1),
        (Test-CatalogueCondition 'perfil leído: un arco' 'arc' (@($readProfile | ForEach-Object { Get-CatalogueMember $_ 'kind' }) -join ',') ($readProfile.Count -eq 1 -and (Get-CatalogueMember $arc 'kind') -eq 'arc')),
        (Test-CataloguePoint 'arco: punto medio (cumbrera, mm)' @(3000, 3800) (Get-CatalogueMember $arc 'mid') 1),
        (Test-CatalogueSegment 'arco: extremos (aleros, mm)' @(-300, 2700) @(6300, 2700) $arc 1),
        (Test-CatalogueCondition 'caja: contiene el perfil extruido a la izquierda del plano (25 mm)' $expectedText (ConvertTo-CatalogueText $box) (Test-CatalogueBoxHolds $box $expected 25)),
        (Test-CatalogueCondition 'caja: no contiene el perfil extruido al lado opuesto' $otherText (ConvertTo-CatalogueText $box) (!(Test-CatalogueBoxHolds $box $other 25))))
}

# P9-07 roof.create_extrusion: the same barrel (eaves 2700, crown 3800, 300 mm overhangs, 4000 mm deep) on an
# x-aligned plane (extruded toward +y), the reversed plane (toward -y) and a diagonal 3-4-5 plane, then a closed
# profile the CLI refuses.
function Get-CatalogueRoofExtrusionSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'x-roof-types'; Es = 'Consultar tipos de techo (para la extrusión)'; Command = 'query.roof_types'; Expect = @{ Status = 'succeeded' }
        Build = { [ordered]@{} }; Check = { param($r) $picked['extrusion_roof_type'] = Guard @('extrusion_roof_type') { Select-CatalogueRoofType $r }; $null } })
    $cases = @(
        @{ Key = 'x-barrel'; Es = 'Techo bóveda por extrusión, plano alineado (30000,0)->(36000,0), extruido de 0 a 4000 mm hacia +y'; Plane = [ordered]@{ start = @(30000, 0); end = @(36000, 0) } },
        @{ Key = 'x-barrel-reversed'; Es = 'Techo bóveda, plano invertido (36000,10000)->(30000,10000), extruido de 0 a 4000 mm hacia -y'; Plane = [ordered]@{ start = @(36000, 10000); end = @(30000, 10000) } },
        @{ Key = 'x-barrel-diagonal'; Es = 'Techo bóveda, plano diagonal (40000,0)->(43600,4800) (dirección 3-4-5), extruido de 0 a 4000 mm a su izquierda'; Plane = [ordered]@{ start = @(40000, 0); end = @(43600, 4800) } })
    foreach ($case in $cases) {
        $steps.Add(@{ Key = $case.Key; Es = $case.Es; Command = 'roof.create_extrusion'; Expect = @{ Status = 'succeeded'; Created = 2 }; Plane = $case.Plane
            Build = { Need 'level', 'extrusion_roof_type'; [ordered]@{ level = $picked['level']; type = $picked['extrusion_roof_type']; plane = $spec.Plane; profile = $script:CatalogueBarrelProfile; extrusion_start_mm = $script:CatalogueBarrelExtrusion[0]; extrusion_end_mm = $script:CatalogueBarrelExtrusion[1] } }
            Check = { param($r) Test-CatalogueRoofExtrusion $r $spec.Plane } })
    }
    $steps.Add(@{ Key = 'x-closed'; Es = 'Pedido inválido: perfil cerrado (triángulo); debe rechazarse sin tocar Revit'; Command = 'roof.create_extrusion'; ClientSide = $true; Expect = @{ Status = 'failed'; Code = 'args_invalid' }
        Build = {
            Need 'level', 'extrusion_roof_type'
            [ordered]@{ level = $picked['level']; type = $picked['extrusion_roof_type']; plane = [ordered]@{ start = @(30000, 20000); end = @(36000, 20000) }
                profile = @([ordered]@{ kind = 'line'; start = @(0, 2700); end = @(3000, 3800) }, [ordered]@{ kind = 'line'; start = @(3000, 3800); end = @(6000, 2700) }, [ordered]@{ kind = 'line'; start = @(6000, 2700); end = @(0, 2700) })
                extrusion_start_mm = 0; extrusion_end_mm = 4000 }
        }
        Check = { param($r) Test-CatalogueRefusal $r 'args.profile' '*closed*' } })
    return , $steps.ToArray()
}

# P1-09 element.move / element.copy and P1-10 element.rotate / element.mirror on a wall of this group: (0,15000)-(3000,15000)
# moved by (1000, 500, 0) to (1000,15500)-(4000,15500); the move replayed by operation_id; a copy 2000 mm north at
# (1000,17500)-(4000,17500) turned 90 degrees about its start to (1000,17500)-(1000,20500); the moved wall mirrored
# (kept) across x = 6000 to (11000,15500)-(8000,15500). Refusals: a zero move and a full turn (CLI), and the v1 door
# turned without its wall (element_not_rotatable, executor).
function Get-CatalogueTransformSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 't-wall'; Es = 'Muro de prueba para transformar, de (0,15000) a (3000,15000)'; Command = 'wall.create'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { Need 'level', 'wall_type'; New-CatalogueLineWall $picked['level'] $picked['wall_type'] @(0, 15000) @(3000, 15000) }
        Check = { param($r) Test-CatalogueSame 'muro = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CataloguePath $r 'readback.wall.unique_id') } })
    $move = {
        param($r)
        $moved = Get-CatalogueFirst (Get-CatalogueArray $r 'readback.moved')
        Join-CatalogueReasons @(
            (Test-CataloguePoint 'vector leído (mm)' @(1000, 500, 0) (Get-CataloguePath $r 'readback.vector_mm') 0.01),
            (Test-CatalogueSame 'elemento movido' (Created 't-wall') (Get-CatalogueMember $moved 'unique_id')),
            (Test-CatalogueContains 'modified incluye el muro' (Created 't-wall') (Get-CatalogueList $r 'modified')),
            (Test-CatalogueSegment 'antes (mm)' @(0, 15000) @(3000, 15000) (Get-CatalogueMember $moved 'before') 1),
            (Test-CataloguePoint 'después: inicio (mm)' @(1000, 15500) (Get-CataloguePlan (Get-CatalogueMember $moved 'after') 'start') 1),
            (Test-CataloguePoint 'después: fin (mm)' @(4000, 15500) (Get-CataloguePlan (Get-CatalogueMember $moved 'after') 'end') 1))
    }
    $steps.Add(@{ Key = 't-move'; Es = 'Mover el muro de prueba (1000, 500, 0) mm'; Command = 'element.move'; Expect = @{ Status = 'succeeded'; Created = 0 }
        Build = { [ordered]@{ element_uids = @(Created 't-wall'); vector_mm = @(1000, 500, 0) } }; Check = $move })
    $steps.Add(@{ Key = 't-move-retry'; Es = 'Repetir el movimiento con el mismo identificador (respuesta guardada; no se mueve dos veces)'; Command = 'element.move'; ReuseOf = 't-move'; Expect = @{ Status = 'succeeded'; Replayed = $true; Created = 0 }
        Build = { [ordered]@{ element_uids = @(Created 't-wall'); vector_mm = @(1000, 500, 0) } }; Check = $move })
    $steps.Add(@{ Key = 't-move-zero'; Es = 'Pedido inválido: mover un vector nulo; debe rechazarse sin tocar Revit'; Command = 'element.move'; ClientSide = $true; Expect = @{ Status = 'failed'; Code = 'length_invalid' }
        Build = { [ordered]@{ element_uids = @(Created 't-wall'); vector_mm = @(0, 0, 0) } }
        Check = { param($r) Test-CatalogueRefusal $r 'args.vector_mm' '*zero vector*' } })
    $steps.Add(@{ Key = 't-copy'; Es = 'Copiar el muro movido 2000 mm al norte (0, 2000, 0)'; Command = 'element.copy'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { [ordered]@{ element_uids = @(Created 't-wall'); vector_mm = @(0, 2000, 0) } }
        Check = {
            param($r)
            $copies = Get-CatalogueArray $r 'readback.copies'
            $also = Get-CatalogueArray $r 'readback.also_created'
            $copy = Get-CatalogueFirst $copies
            Join-CatalogueReasons @(
                (Test-CatalogueCondition 'una copia' 1 $copies.Count ($copies.Count -eq 1)),
                (Test-CatalogueSame 'origen de la copia' (Created 't-wall') (Get-CatalogueMember $copy 'source_unique_id')),
                (Test-CatalogueSame 'copia = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $copy 'copy_unique_id')),
                (Test-CataloguePoint 'copia: inicio (mm)' @(1000, 17500) (Get-CataloguePlan (Get-CatalogueMember $copy 'location') 'start') 1),
                (Test-CataloguePoint 'copia: fin (mm)' @(4000, 17500) (Get-CataloguePlan (Get-CatalogueMember $copy 'location') 'end') 1),
                (Test-CatalogueCondition 'nada más copiado (also_created vacío)' 0 $also.Count ($also.Count -eq 0)))
        } })
    $steps.Add(@{ Key = 't-rotate'; Es = 'Rotar la copia 90 grados (antihorario) alrededor de su inicio (1000,17500)'; Command = 'element.rotate'; Expect = @{ Status = 'succeeded'; Created = 0 }
        Build = { [ordered]@{ element_uids = @(Created 't-copy'); center = @(1000, 17500); angle_deg = 90 } }
        Check = {
            param($r)
            $rotated = Get-CatalogueFirst (Get-CatalogueArray $r 'readback.rotated')
            $after = Get-CatalogueMember $rotated 'after'
            Join-CatalogueReasons @(
                (Test-CatalogueNumber 'ángulo leído (grados)' 90 (Get-CataloguePath $r 'readback.angle_deg') 1e-9),
                (Test-CataloguePoint 'centro leído (mm)' @(1000, 17500) (Get-CataloguePath $r 'readback.center_mm') 0.01),
                (Test-CatalogueSame 'elemento rotado' (Created 't-copy') (Get-CatalogueMember $rotated 'unique_id')),
                (Test-CatalogueContains 'modified incluye la copia' (Created 't-copy') (Get-CatalogueList $r 'modified')),
                (Test-CataloguePoint 'después: inicio (mm)' @(1000, 17500) (Get-CataloguePlan $after 'start') 1),
                (Test-CataloguePoint 'después: fin (mm)' @(1000, 20500) (Get-CataloguePlan $after 'end') 1))
        } })
    $steps.Add(@{ Key = 't-rotate-door'; Es = 'Rotar la puerta v1 sin su muro (debe rechazarse: gira solo con su anfitrión)'; Command = 'element.rotate'; Expect = @{ Status = 'failed'; Code = 'element_not_rotatable' }
        Build = { [ordered]@{ element_uids = @(Created 'door'); center = @(1500, 0); angle_deg = 90 } }
        Check = { param($r) Test-CatalogueRefusal $r 'args.element_uids[0]' '*include the host*' } })
    $steps.Add(@{ Key = 't-rotate-full'; Es = 'Pedido inválido: rotar 360 grados; debe rechazarse sin tocar Revit'; Command = 'element.rotate'; ClientSide = $true; Expect = @{ Status = 'failed'; Code = 'angle_invalid' }
        Build = { [ordered]@{ element_uids = @(Created 't-wall'); center = @(0, 15000); angle_deg = 360 } }
        Check = { param($r) Test-CatalogueRefusal $r 'args.angle_deg' '*full turn*' } })
    $steps.Add(@{ Key = 't-mirror'; Es = 'Reflejar el muro movido en la recta x = 6000, conservando el original'; Command = 'element.mirror'; Expect = @{ Status = 'succeeded'; Created = 1 }
        Build = { [ordered]@{ element_uids = @(Created 't-wall'); axis = [ordered]@{ start = @(6000, 15000); end = @(6000, 16000) }; keep_original = $true } }
        Check = {
            param($r)
            $mirrored = Get-CatalogueFirst (Get-CatalogueArray $r 'readback.mirrored')
            $deleted = Get-CatalogueList $r 'deleted'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'conserva el original' $true (Get-CataloguePath $r 'readback.keep_original')),
                (Test-CatalogueSame 'origen del reflejo' (Created 't-wall') (Get-CatalogueMember $mirrored 'source_unique_id')),
                (Test-CatalogueSame 'reflejo nuevo' $true (Get-CatalogueMember $mirrored 'new')),
                (Test-CatalogueSame 'reflejo = primer elemento creado' (Get-CatalogueFirstCreated $r) (Get-CatalogueMember $mirrored 'unique_id')),
                (Test-CatalogueSegment 'original antes (mm)' @(1000, 15500) @(4000, 15500) (Get-CatalogueMember $mirrored 'before') 1),
                (Test-CatalogueSegment 'reflejo (mm)' @(11000, 15500) @(8000, 15500) (Get-CatalogueMember $mirrored 'after') 1),
                (Test-CatalogueCondition 'nada borrado' 0 $deleted.Count ($deleted.Count -eq 0)))
        } })
    return , $steps.ToArray()
}

# The document.save_copy paths for the open test copy <repo>/.bim/native-runs/<run>/BIM-Agentic-Test*.rvt: the copy
# next to it and a refused target directly in <repo>/.bim (outside the copies root). Any other document throws, so
# no copy step is sent: a real project is never copied and nothing is written outside .bim/native-runs.
function Get-CatalogueCopyPaths([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $folder = [IO.Path]::GetDirectoryName($full)
    $runs = if ($folder) { [IO.Path]::GetDirectoryName($folder) } else { $null }
    $bim = if ($runs) { [IO.Path]::GetDirectoryName($runs) } else { $null }
    if ([IO.Path]::GetFileName($full) -cnotmatch '\ABIM-Agentic-Test[A-Za-z0-9._-]*\.[rR][vV][tT]\z' -or !$runs -or [IO.Path]::GetFileName($runs) -cne 'native-runs' -or !$bim -or [IO.Path]::GetFileName($bim) -cne '.bim') {
        throw "el documento abierto no es una copia de prueba BIM-Agentic-Test*.rvt dentro de .bim/native-runs: $Path"
    }
    $name = "BIM-Agentic-Test-c04-$Stamp-r$Run"
    return @{ Source = $full; Copy = [IO.Path]::Combine($folder, "$name-copia.rvt"); Outside = [IO.Path]::Combine($bim, "$name-fuera.rvt"); SaveAs = [IO.Path]::Combine($folder, "$name-guardado.rvt"); Name = $name }
}

# The first usable owner-enabled folder of <repo>/.bim/revit-enabled-roots.txt (the file the CLI reads, found from the
# test copy <repo>/.bim/native-runs/<run>/BIM-Agentic-Test*.rvt), or $null (Select-CatalogueOwnerRoot).
function Get-CatalogueOwnerRoot([string]$Path) {
    $runs = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path)))
    if (!$runs -or [IO.Path]::GetFileName($runs) -cne 'native-runs') { return $null }
    $file = [IO.Path]::Combine([IO.Path]::GetDirectoryName($runs), 'revit-enabled-roots.txt')
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    return Select-CatalogueOwnerRoot ([IO.File]::ReadAllLines($file)) $runs
}

# Pure: the first entry that is a usable owner folder, or $null. Lines are read as the CLI reads them (trimmed, blank
# lines and '#' comments skipped). A drive path (C:\... or C:/...) gets '\' separators; a '/'-rooted path is kept.
# Skipped: UNC, relative or drive-relative entries, '.'/'..' or empty segments, segments ending with a dot or space,
# wildcard or ':' characters (save_as would refuse any target under them), any entry spelled .bim/native-runs, and
# Runs itself or anything inside it (compared after the same normalisation, case-insensitively for drive paths).
function Select-CatalogueOwnerRoot([string[]]$Lines, [string]$Runs) {
    function Normalize([string]$Entry) {
        if ($Entry -match '\A[A-Za-z]:[\\/]') { return ($Entry.Replace('/', '\')).TrimEnd('\') }
        if ($Entry.StartsWith('/') -and !$Entry.StartsWith('//')) { return $Entry.TrimEnd('/') }
        return $null
    }
    $runsNormal = Normalize $Runs
    foreach ($raw in @($Lines)) {
        $line = ([string]$raw).Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
        $entry = Normalize $line
        if (!$entry) { continue }
        $drive = $entry -match '\A[A-Za-z]:'
        $separator = if ($drive) { '\' } else { '/' }
        $rest = if ($drive) { $entry.Substring(2) } else { $entry }
        $segments = @($rest.Split($separator) | Select-Object -Skip 1)
        if ($segments.Count -eq 0 -or @($segments | Where-Object { $_.Length -eq 0 -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_.IndexOfAny([char[]]'<>"|?*:') -ge 0 -or ($drive -and $_.Contains('/')) }).Count) { continue }
        if ($segments.Count -ge 2 -and $segments[-2] -ieq '.bim' -and $segments[-1] -ieq 'native-runs') { continue }
        if ($runsNormal) {
            $comparison = if ($drive) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
            if ([string]::Equals($entry, $runsNormal, $comparison) -or $entry.StartsWith($runsNormal + $separator, $comparison)) { continue }
        }
        return $entry
    }
    return $null
}

# Root\Folder\File with the root's own separator ('\' for a drive path, '/' for a '/'-rooted one).
function Join-CatalogueOwnerTarget([string]$Root, [string]$Folder, [string]$File) {
    $separator = if ($Root -match '\A[A-Za-z]:') { '\' } else { '/' }
    return $Root + $separator + $Folder + $separator + $File
}

# S1 safety net, checked by Invoke-CatalogueSequence before any document.save_as is sent: $null when the target is
# acceptable, otherwise why it is not sent. A save_as that is not a client-side refusal goes only inside the test copy's
# .bim/native-runs; a client-side refusal step outside it only into a folder that does not exist, so a guard
# regression would stop at folder_missing (and Revit's SaveAs would fail) instead of writing into an owner folder.
function Test-CatalogueSaveTarget([hashtable]$Spec, $StepArgs, [string]$DocumentPath) {
    $target = [string](Get-CatalogueMember $StepArgs 'path')
    if (!$target) { return 'save_as sin ruta de destino' }
    $runs = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($DocumentPath)))
    $inside = $false
    if ($runs -and [IO.Path]::GetFileName($runs) -ceq 'native-runs') {
        $full = [IO.Path]::GetFullPath($target)
        $inside = $full.StartsWith($runs.TrimEnd('/', '\') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($inside) { return $null }
    if (!($Spec.ContainsKey('ClientSide') -and $Spec.ClientSide)) { return "un guardar como real solo va dentro de .bim/native-runs del modelo de prueba: $target" }
    $cut = $target.LastIndexOfAny([char[]]@('/', '\'))
    $folder = if ($cut -gt 0) { $target.Substring(0, $cut) } else { $null }
    if (!$folder -or (Test-Path -LiteralPath $folder)) { return "la carpeta del destino rechazado existe ($folder); el rechazo debe apuntar a una carpeta que no existe" }
    return $null
}

function Get-CatalogueFullPath($Path) { if ($Path) { return [IO.Path]::GetFullPath([string]$Path) }; return $null }

# Lowercase hex SHA-256 of a file; Revit keeps the saved copy open, so it is opened shared for write and delete.
function Get-CatalogueFileSha256([string]$Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
}

# P1-08 document.save_copy, last before the destructive group: a target outside .bim/native-runs is refused by the CLI
# (outside_copies_root); the copy is saved next to the test document and its sha256 and size are compared with the
# file on disk; the same request again is refused by the CLI (target_exists, not replayed). Revit then has the copy
# open in place of the source: a query naming the copy succeeds, one naming the source is document_conflict, and
# every later step (the destructive group) works on the copy, so the saved copy keeps the model as it was here.
function Get-CatalogueSaveCopySteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'c-outside'; Es = 'Guardar una copia directamente en .bim (fuera de .bim/native-runs): debe rechazarse sin tocar Revit'; Command = 'document.save_copy'; ClientSide = $true; Reason = 'outside_copies_root'; Expect = @{ Status = 'failed'; Code = 'save_copy_refused' }
        Build = { [ordered]@{ path = (Get-CatalogueCopyPaths $DocumentPath).Outside } }
        Check = { param($r) Test-CatalogueRefusal $r 'args.path' 'outside_copies_root:*' } })
    $steps.Add(@{ Key = 'c-save'; Es = 'Guardar una copia BIM-Agentic-Test-c04-*-copia.rvt junto al modelo de prueba (hash leído = hash del archivo)'; Command = 'document.save_copy'; Expect = @{ Status = 'succeeded'; Created = 0 }
        Build = { [ordered]@{ path = (Get-CatalogueCopyPaths $DocumentPath).Copy } }
        Check = {
            param($r)
            $paths = Get-CatalogueCopyPaths $DocumentPath
            $copy = Get-CataloguePath $r 'readback.copy'
            $opened = Get-CatalogueMember $copy 'document_path'
            # Revit re-pointed the open document to the copy: every later request names it, whatever else differs.
            if ($opened -and (Get-CatalogueFullPath $opened) -eq $paths.Copy) { $picked['document_path'] = [string]$opened; $picked['document_switched_by'] = 'c-save' }
            $sha = [string](Get-CatalogueMember $copy 'sha256')
            $exists = Test-Path -LiteralPath $paths.Copy -PathType Leaf
            $diskSha = if ($exists) { Get-CatalogueFileSha256 $paths.Copy } else { $null }
            $diskBytes = if ($exists) { (Get-Item -LiteralPath $paths.Copy).Length } else { $null }
            $warnings = Get-CatalogueList $r 'warnings'
            $warning = $warnings -join ' '
            $reason = Join-CatalogueReasons @(
                (Test-CatalogueSame 'ruta de la copia' $paths.Copy (Get-CatalogueFullPath (Get-CatalogueMember $copy 'path'))),
                (Test-CatalogueSame 'documento abierto = la copia' $paths.Copy (Get-CatalogueFullPath $opened)),
                (Test-CatalogueSame 'origen' $paths.Source (Get-CatalogueFullPath (Get-CatalogueMember $copy 'source_path'))),
                (Test-CatalogueSame 'origen sin reescribir' $true (Get-CatalogueMember $copy 'source_unchanged')),
                (Test-CatalogueSame 'sin cambios pendientes tras guardar' $false (Get-CatalogueMember $copy 'is_modified')),
                (Test-CatalogueCondition 'sha256 leído (64 hex)' '64 hex' $sha ($sha -cmatch '\A[0-9a-f]{64}\z')),
                (Test-CatalogueCondition 'archivo de la copia en disco' $paths.Copy $exists $exists),
                (Test-CatalogueSame 'bytes leídos = tamaño en disco' (Get-CatalogueMember $copy 'bytes') $diskBytes),
                (Test-CatalogueSame 'sha256 leído = sha256 del archivo en disco' $sha $diskSha),
                (Test-CatalogueCondition 'aviso: el documento abierto ahora es la copia' '*now the copy*' $warning ($warning -like '*now the copy*')))
            if ($null -eq $reason) { $picked['copy_path'] = $paths.Copy }
            $reason
        } })
    $steps.Add(@{ Key = 'c-save-again'; Es = 'Repetir la misma copia (mismo identificador): debe rechazarse sin tocar Revit porque el archivo ya existe'; Command = 'document.save_copy'; ReuseOf = 'c-save'; UseSourceDocument = $true; ClientSide = $true; Reason = 'target_exists'; Expect = @{ Status = 'failed'; Code = 'save_copy_refused' }
        Build = { Need 'copy_path'; [ordered]@{ path = $picked['copy_path'] } }
        Check = { param($r) Test-CatalogueRefusal $r 'args.path' 'target_exists:*' } })
    # Always sent naming the copy, also when c-save failed: Revit can fail the save after Save As already re-pointed the
    # open document (location check or readback after the save). If this probe succeeds while nothing switched yet,
    # the run switches to the copy here and records it (picked.document_switched_by = c-levels-copy); a save refused
    # before Save As makes it document_conflict and the later groups keep the source.
    $steps.Add(@{ Key = 'c-levels-copy'; Es = 'Consultar niveles nombrando la copia (Revit la tiene abierta; si c-save falló, comprueba si Revit igual quedó en la copia)'; Command = 'query.levels'; Expect = @{ Status = 'succeeded' }
        Build = { $null = Get-CatalogueCopyPaths $DocumentPath; [ordered]@{} }
        Document = { (Get-CatalogueCopyPaths $DocumentPath).Copy }
        Check = {
            param($r)
            $levels = Get-CatalogueList (Get-CatalogueMember $r 'readback') 'levels'
            $reason = Test-CatalogueContains 'el nivel de los muros está en la copia' $picked['level'] @($levels | ForEach-Object { Get-CatalogueMember $_ 'unique_id' })
            if (!$picked.ContainsKey('document_path') -or !$picked['document_path']) {
                $copyPath = (Get-CatalogueCopyPaths $DocumentPath).Copy
                $picked['document_path'] = $copyPath
                $picked['document_switched_by'] = 'c-levels-copy'
                $null = Test-CatalogueCondition 'c-save falló pero Revit tiene la copia abierta: la sesión cambia a la copia' $copyPath $copyPath $true
            }
            $reason
        } })
    $steps.Add(@{ Key = 'c-levels-source'; Es = 'Consultar niveles nombrando el modelo de origen (debe responder document_conflict)'; Command = 'query.levels'; UseSourceDocument = $true; Expect = @{ Status = 'failed'; Code = 'document_conflict' }
        Build = { Need 'document_path'; [ordered]@{} }
        Check = { param($r) Test-CatalogueRefusal $r 'document.path' '*not the saved document*' } })
    foreach ($step in (Get-CatalogueSaveAsSteps)) { $steps.Add($step) }
    return , $steps.ToArray()
}

# P1-14 document.save_as (P9-08), after the save_copy steps, on the document Revit has open then (the copy, or the test
# copy itself when no save_copy switched it): a target in an owner-enabled folder (the first entry of
# .bim/revit-enabled-roots.txt outside .bim/native-runs) is refused by the CLI (outside_copies_root: a test copy is saved
# as a new file only inside .bim/native-runs; blocked, never sent, when the owner lists no such folder); the open
# document is saved as BIM-Agentic-Test-c04-*-guardado.rvt next to the test copy and the readback (new path, previous
# path untouched, sha256 and size against the file on disk, no unsaved changes, the moved-document warning) is checked;
# the same request again is refused by the CLI (target_exists, not replayed). Revit then has the new file open: a query
# naming it succeeds (the post-save path readback) and one naming the previous file is document_conflict; the
# destructive group then works on the new file.
function Get-CatalogueSaveAsSteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $steps.Add(@{ Key = 'c-as-outside'; Es = 'Guardar como en una carpeta del propietario (fuera de .bim/native-runs): debe rechazarse sin tocar Revit'; Command = 'document.save_as'; ClientSide = $true; Reason = 'outside_copies_root'; Expect = @{ Status = 'failed'; Code = 'save_as_refused' }
        Build = {
            $paths = Get-CatalogueCopyPaths $DocumentPath
            $owner = Get-CatalogueOwnerRoot $DocumentPath
            if (!$owner) { throw [InvalidOperationException]::new($script:CatalogueBlockedMarker + 'falta una carpeta del propietario en .bim/revit-enabled-roots.txt (fuera de .bim/native-runs) para probar el rechazo outside_copies_root') }
            # Into a subfolder that does not exist: a guard regression stops at folder_missing, never in the owner folder.
            [ordered]@{ path = (Join-CatalogueOwnerTarget $owner "bim-agentic-no-existe-$Stamp-r$Run" "$($paths.Name)-fuera-como.rvt") }
        }
        Check = { param($r) Test-CatalogueRefusal $r 'args.path' 'outside_copies_root:*' } })
    $steps.Add(@{ Key = 'c-as-save'; Es = 'Guardar como BIM-Agentic-Test-c04-*-guardado.rvt junto al modelo de prueba (ruta nueva, anterior intacto, hash leído = hash del archivo)'; Command = 'document.save_as'; Expect = @{ Status = 'succeeded'; Created = 0 }
        Build = {
            $paths = Get-CatalogueCopyPaths $DocumentPath
            $picked['save_as_previous'] = $(if ($picked.ContainsKey('document_path') -and $picked['document_path']) { [string]$picked['document_path'] } else { $DocumentPath })
            [ordered]@{ path = $paths.SaveAs }
        }
        Check = {
            param($r)
            $paths = Get-CatalogueCopyPaths $DocumentPath
            $previous = [string]$picked['save_as_previous']
            $saved = Get-CataloguePath $r 'readback.save_as'
            $opened = Get-CatalogueMember $saved 'document_path'
            # Revit re-pointed the open document to the new file: every later request names it, whatever else differs.
            if ($opened -and (Get-CatalogueFullPath $opened) -eq $paths.SaveAs) { $picked['document_path'] = [string]$opened; $picked['document_switched_by'] = 'c-as-save'; $picked['save_as_document'] = [string]$opened }
            $sha = [string](Get-CatalogueMember $saved 'sha256')
            $exists = Test-Path -LiteralPath $paths.SaveAs -PathType Leaf
            $diskSha = if ($exists) { Get-CatalogueFileSha256 $paths.SaveAs } else { $null }
            $diskBytes = if ($exists) { (Get-Item -LiteralPath $paths.SaveAs).Length } else { $null }
            $previousKept = Test-Path -LiteralPath $previous -PathType Leaf
            $warning = (Get-CatalogueList $r 'warnings') -join ' '
            $reason = Join-CatalogueReasons @(
                (Test-CatalogueSame 'ruta nueva' $paths.SaveAs (Get-CatalogueFullPath (Get-CatalogueMember $saved 'path'))),
                (Test-CatalogueSame 'documento abierto = archivo nuevo' $paths.SaveAs (Get-CatalogueFullPath $opened)),
                (Test-CatalogueSame 'ruta anterior' (Get-CatalogueFullPath $previous) (Get-CatalogueFullPath (Get-CatalogueMember $saved 'previous_path'))),
                (Test-CatalogueSame 'archivo anterior sin reescribir' $true (Get-CatalogueMember $saved 'previous_unchanged')),
                (Test-CatalogueCondition 'archivo anterior sigue en disco' $previous $previousKept $previousKept),
                (Test-CatalogueSame 'sin cambios pendientes tras guardar' $false (Get-CatalogueMember $saved 'is_modified')),
                (Test-CatalogueCondition 'sha256 leído (64 hex)' '64 hex' $sha ($sha -cmatch '\A[0-9a-f]{64}\z')),
                (Test-CatalogueCondition 'archivo nuevo en disco' $paths.SaveAs $exists $exists),
                (Test-CatalogueSame 'bytes leídos = tamaño en disco' (Get-CatalogueMember $saved 'bytes') $diskBytes),
                (Test-CatalogueSame 'sha256 leído = sha256 del archivo en disco' $sha $diskSha),
                (Test-CatalogueCondition 'aviso: el documento abierto ahora es el archivo nuevo' "*now $($paths.SaveAs)*" $warning ($warning.Contains("the open document is now $($paths.SaveAs) ", [StringComparison]::OrdinalIgnoreCase))))
            if ($null -eq $reason) { $picked['save_as_path'] = $paths.SaveAs }
            $reason
        } })
    $steps.Add(@{ Key = 'c-as-save-again'; Es = 'Repetir el mismo guardar como (mismo identificador y documento anterior): debe rechazarse sin tocar Revit porque el archivo ya existe'; Command = 'document.save_as'; ReuseOf = 'c-as-save'; ClientSide = $true; Reason = 'target_exists'; Expect = @{ Status = 'failed'; Code = 'save_as_refused' }
        Build = { Need 'save_as_path', 'save_as_previous'; [ordered]@{ path = $picked['save_as_path'] } }
        Document = { [string]$picked['save_as_previous'] }
        Check = { param($r) Test-CatalogueRefusal $r 'args.path' 'target_exists:*' } })
    # Always sent naming the new file, also when c-as-save failed (Revit can fail the readback after Save As already
    # re-pointed the document): this probe is the post-save path readback; if it succeeds while nothing switched yet,
    # the run switches to the new file here and records it (picked.document_switched_by = c-as-levels-new).
    $steps.Add(@{ Key = 'c-as-levels-new'; Es = 'Consultar niveles nombrando el archivo nuevo (Revit lo tiene abierto tras guardar como)'; Command = 'query.levels'; Expect = @{ Status = 'succeeded' }
        Build = { $null = Get-CatalogueCopyPaths $DocumentPath; [ordered]@{} }
        Document = { (Get-CatalogueCopyPaths $DocumentPath).SaveAs }
        Check = {
            param($r)
            $levels = Get-CatalogueList (Get-CatalogueMember $r 'readback') 'levels'
            $reason = Test-CatalogueContains 'el nivel de los muros está en el archivo nuevo' $picked['level'] @($levels | ForEach-Object { Get-CatalogueMember $_ 'unique_id' })
            if (!$picked.ContainsKey('save_as_document') -or !$picked['save_as_document']) {
                $newPath = (Get-CatalogueCopyPaths $DocumentPath).SaveAs
                $picked['document_path'] = $newPath
                $picked['save_as_document'] = $newPath
                $picked['document_switched_by'] = 'c-as-levels-new'
                $null = Test-CatalogueCondition 'c-as-save falló pero Revit tiene el archivo nuevo abierto: la sesión cambia al archivo nuevo' $newPath $newPath $true
            }
            $reason
        } })
    $steps.Add(@{ Key = 'c-as-levels-previous'; Es = 'Consultar niveles nombrando el archivo anterior (debe responder document_conflict)'; Command = 'query.levels'; Expect = @{ Status = 'failed'; Code = 'document_conflict' }
        Build = { Need 'save_as_document', 'save_as_previous'; [ordered]@{} }
        Document = { [string]$picked['save_as_previous'] }
        Check = { param($r) Test-CatalogueRefusal $r 'document.path' '*not the saved document*' } })
    return , $steps.ToArray()
}

# P1-05 modify and delete, destructive, always last: the v1 west wall height and comments, then the v1 south wall
# (which hosts the v1 door) is deleted after a dry run and a refused unconfirmed delete. After a successful
# document.save_copy (and document.save_as) these steps name the file Revit has open (the saved files keep the state before them).
function Get-CatalogueModifySteps {
    $steps = [Collections.Generic.List[hashtable]]::new()
    $height = [ordered]@{ built_in = 'WALL_USER_HEIGHT_PARAM' }
    $steps.Add(@{ Key = 'm-inspect-before'; Es = 'Inspeccionar la altura del muro oeste (3000 mm)'; Command = 'query.inspect'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-west'; [ordered]@{ unique_id = $picked['wall-west'] } }
        Check = { param($r) Test-CatalogueInspectHeight $r $picked['wall-west'] 3000 } })
    $steps.Add(@{ Key = 'm-height-dry'; Es = 'Altura del muro oeste a 3200 mm en simulacro (dry_run)'; Command = 'element.set_parameter'; Expect = @{ Status = 'succeeded' }; Parameter = $height
        Build = { Need 'wall-west'; [ordered]@{ element_uid = $picked['wall-west']; parameter = $spec.Parameter; value = [ordered]@{ kind = 'length'; mm = 3200 }; dry_run = $true } }
        Check = {
            param($r)
            $set = Get-CataloguePath $r 'readback.set_parameter'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'simulacro' $true (Get-CatalogueMember $set 'dry_run')),
                (Test-CatalogueSame 'elemento' $picked['wall-west'] (Get-CatalogueMember $set 'element_unique_id')),
                (Test-CatalogueNumber 'antes (mm)' 3000 (Get-CataloguePath $set 'before.mm') 0.01),
                (Test-CatalogueNumber 'después (mm)' 3200 (Get-CataloguePath $set 'after.mm') 0.01),
                (Test-CatalogueSame 'cambiaría' $true (Get-CatalogueMember $set 'changed')),
                (Test-CatalogueCondition 'modified vacío en simulacro' 0 (Get-CatalogueList $r 'modified').Count ((Get-CatalogueList $r 'modified').Count -eq 0)))
        } })
    $steps.Add(@{ Key = 'm-inspect-dry'; Es = 'Inspeccionar: la altura sigue en 3000 mm tras el simulacro'; Command = 'query.inspect'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-west'; [ordered]@{ unique_id = $picked['wall-west'] } }
        Check = { param($r) Test-CatalogueInspectHeight $r $picked['wall-west'] 3000 } })
    $steps.Add(@{ Key = 'm-height'; Es = 'Altura del muro oeste a 3200 mm (real)'; Command = 'element.set_parameter'; Expect = @{ Status = 'succeeded' }; Parameter = $height
        Build = { Need 'wall-west'; [ordered]@{ element_uid = $picked['wall-west']; parameter = $spec.Parameter; value = [ordered]@{ kind = 'length'; mm = 3200 } } }
        Check = {
            param($r)
            $set = Get-CataloguePath $r 'readback.set_parameter'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'simulacro' $false (Get-CatalogueMember $set 'dry_run')),
                (Test-CatalogueNumber 'después (mm)' 3200 (Get-CataloguePath $set 'after.mm') 0.01),
                (Test-CatalogueContains 'modified incluye el muro oeste' $picked['wall-west'] (Get-CatalogueList $r 'modified')))
        } })
    $steps.Add(@{ Key = 'm-height-retry'; Es = 'Repetir el cambio de altura con el mismo identificador (respuesta guardada)'; Command = 'element.set_parameter'; ReuseOf = 'm-height'; Expect = @{ Status = 'succeeded'; Replayed = $true }; Parameter = $height
        Build = { Need 'wall-west'; [ordered]@{ element_uid = $picked['wall-west']; parameter = $spec.Parameter; value = [ordered]@{ kind = 'length'; mm = 3200 } } }
        Check = { param($r) Test-CatalogueNumber 'después (mm, respuesta guardada)' 3200 (Get-CataloguePath $r 'readback.set_parameter.after.mm') 0.01 } })
    $steps.Add(@{ Key = 'm-inspect-after'; Es = 'Inspeccionar: la altura quedó en 3200 mm'; Command = 'query.inspect'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-west'; [ordered]@{ unique_id = $picked['wall-west'] } }
        Check = { param($r) Test-CatalogueInspectHeight $r $picked['wall-west'] 3200 } })
    $steps.Add(@{ Key = 'm-comments'; Es = 'Comentario de texto en el muro oeste (ALL_MODEL_INSTANCE_COMMENTS)'; Command = 'element.set_parameter'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-west'; [ordered]@{ element_uid = $picked['wall-west']; parameter = [ordered]@{ built_in = 'ALL_MODEL_INSTANCE_COMMENTS' }; value = [ordered]@{ kind = 'text'; text = "C04 r$Run" } } }
        Check = { param($r) Test-CatalogueSame 'texto leído' "C04 r$Run" (Get-CataloguePath $r 'readback.set_parameter.after.text') } })
    $steps.Add(@{ Key = 'm-readonly'; Es = 'Parámetro de solo lectura (largo del muro): debe rechazarse'; Command = 'element.set_parameter'; Expect = @{ Status = 'failed'; Code = 'args_invalid' }
        Build = { Need 'wall-west'; [ordered]@{ element_uid = $picked['wall-west']; parameter = [ordered]@{ built_in = 'CURVE_ELEM_LENGTH' }; value = [ordered]@{ kind = 'length'; mm = 1000 } } }
        Check = { param($r) Test-CatalogueRefusal $r 'args.parameter' '*read-only*' } })
    $steps.Add(@{ Key = 'm-delete-dry'; Es = 'Borrar el muro sur en simulacro: la puerta debe figurar como dependiente'; Command = 'element.delete'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-south'; [ordered]@{ element_uids = @($picked['wall-south']); dry_run = $true } }
        Check = {
            param($r)
            $delete = Get-CataloguePath $r 'readback.delete'
            $dependents = Get-CatalogueList $delete 'dependents'
            $reason = Join-CatalogueReasons @(
                (Test-CatalogueSame 'simulacro' $true (Get-CatalogueMember $delete 'dry_run')),
                (Test-CatalogueContains 'objetivo' $picked['wall-south'] (Get-CatalogueList $delete 'targets')),
                (Test-CatalogueContains 'la puerta es dependiente' (Created 'door') $dependents),
                (Test-CatalogueCondition 'deleted vacío en simulacro' 0 (Get-CatalogueList $r 'deleted').Count ((Get-CatalogueList $r 'deleted').Count -eq 0)))
            if ($null -eq $reason) { $picked['delete_dependents'] = @($dependents | ForEach-Object { [string]$_ }) }
            $reason
        } })
    $steps.Add(@{ Key = 'm-delete-unconfirmed'; Es = 'Borrar el muro sur sin confirmar dependientes: debe rechazarse'; Command = 'element.delete'; Expect = @{ Status = 'failed'; Code = 'args_invalid' }
        Build = { Need 'wall-south', 'delete_dependents'; [ordered]@{ element_uids = @($picked['wall-south']) } }
        Check = { param($r) Test-CatalogueRefusal $r 'args.confirm_dependents' '*unconfirmed*' } })
    $steps.Add(@{ Key = 'm-delete'; Es = 'Borrar el muro sur confirmando exactamente los dependientes del simulacro'; Command = 'element.delete'; Expect = @{ Status = 'succeeded' }
        Build = { Need 'wall-south', 'delete_dependents'; [ordered]@{ element_uids = @($picked['wall-south']); confirm_dependents = @($picked['delete_dependents']) } }
        Check = {
            param($r)
            $deleted = Get-CatalogueList $r 'deleted'
            Join-CatalogueReasons @(
                (Test-CatalogueSame 'dependientes confirmados' $true (Get-CataloguePath $r 'readback.delete.confirmed')),
                (Test-CatalogueContains 'deleted incluye el muro sur' $picked['wall-south'] $deleted),
                (Test-CatalogueContains 'deleted incluye la puerta' (Created 'door') $deleted))
        } })
    $steps.Add(@{ Key = 'm-inspect-deleted'; Es = 'Inspeccionar el muro borrado (debe responder reference_not_found)'; Command = 'query.inspect'; Expect = @{ Status = 'failed'; Code = 'reference_not_found' }
        Build = { Need 'wall-south'; [ordered]@{ unique_id = $picked['wall-south'] } }
        Check = { param($r) $null } })
    return , $steps.ToArray()
}

# The v1 group, then (unless V1Only) the later groups in a fixed order with the destructive one last. Group keys name
# the groups in the summary line.
function Get-CatalogueGroups([switch]$V1Only) {
    $groups = [Collections.Generic.List[hashtable]]::new()
    $groups.Add(@{ Key = 'v1'; Es = 'Catálogo v1 (C04): muros, puerta, ventana, suelo, habitación, reintento y rechazos'; Steps = (Get-CatalogueSteps) })
    if (!$V1Only) {
        $groups.Add(@{ Key = 'v2'; Es = 'Catálogo v2 (P9-01): techo, escalera, muro cortina, hueco en suelo'; Steps = (Get-CatalogueV2Steps) })
        $groups.Add(@{ Key = 'vistas'; Es = 'Vistas (P4-01): planta, corte y 3D con caja de sección'; Steps = (Get-CatalogueViewSteps) })
        $groups.Add(@{ Key = 'snapshot'; Es = 'Instantánea BIM-IR (P2-01): dos veces idéntica, aberturas sobre el eje, suelo sin mapear'; Steps = (Get-CatalogueSnapshotSteps) })
        $groups.Add(@{ Key = 'consultas'; Es = 'Consultas de elementos (P1-02): query.elements y query.inspect'; Steps = (Get-CatalogueQuerySteps) })
        $groups.Add(@{ Key = 'estructura'; Es = 'Estructura (P9-02): ejes, columna, viga, zapata, cimiento corrido'; Steps = (Get-CatalogueStructureSteps) })
        $groups.Add(@{ Key = 'documentacion'; Es = 'Documentación (P4-02): cotas, etiquetas, tabla, lámina y vistas en lámina; actualización con doc-update --execute tras mover un muro (P4-14)'; Steps = (Get-CatalogueDocumentationSteps) })
        $groups.Add(@{ Key = 'diario'; Es = 'Diario de cambios (P2-03): journal.read'; Steps = (Get-CatalogueJournalSteps) })
        $groups.Add(@{ Key = 'formas'; Es = 'Formas DirectShape (P9-09): caja, extrusión y contorno cruzado'; Steps = (Get-CatalogueDirectShapeSteps) })
        $groups.Add(@{ Key = 'extrusion'; Es = 'Techos por extrusión (P9-07): bóveda en plano alineado, invertido y diagonal; perfil cerrado'; Steps = (Get-CatalogueRoofExtrusionSteps) })
        $groups.Add(@{ Key = 'transformar'; Es = 'Mover, copiar, rotar y reflejar (P1-09, P1-10) sobre un muro propio'; Steps = (Get-CatalogueTransformSteps) })
        $groups.Add(@{ Key = 'copia'; Es = 'Guardar copia (P1-08) y guardar como (P9-08): copia y archivo nuevo con hash, rechazos fuera de la carpeta y por archivo existente; después todo va al archivo nuevo'; Steps = (Get-CatalogueSaveCopySteps) })
        $groups.Add(@{ Key = 'modificar'; Es = 'Modificar y borrar (P1-05, destructivo, al final)'; Steps = (Get-CatalogueModifySteps) })
    }
    return , $groups.ToArray()
}

function Get-CatalogueCounts($Records) {
    $list = @($Records | Where-Object { $null -ne $_ })
    $passed = @($list | Where-Object { $_.passed }).Count
    $blocked = @($list | Where-Object { $_['blocked'] }).Count
    return [ordered]@{ total = $list.Count; passed = $passed; failed = $list.Count - $passed - $blocked; blocked = $blocked; not_sent = @($list | Where-Object { $null -eq $_.request }).Count }
}

function Test-CatalogueWireResponse($Response, $ExitCode) {
    if ($null -eq $Response) { return 'no JSON response' }
    $status = Get-CatalogueMember $Response 'status'
    if ($status -ne 'succeeded') { return "journal.read: expected success, got ${status}/$(Get-CatalogueMember $Response 'code'): $(Get-CatalogueMember $Response 'detail')" }
    if ($ExitCode -ne 0) { return "journal.read: success reported with exit code $ExitCode" }
    if ($null -eq (Get-CatalogueMember $Response 'report')) { return 'journal.read: success without report' }
    return $null
}

# A pipe wire line for an Operation outside the revit.command catalogue (journal.read): the add-in Request record.
function New-CatalogueWireLine([string]$DocumentPath, [string]$Operation, $Options) {
    $line = [ordered]@{ DocumentPath = $DocumentPath; Operation = $Operation; UniqueId = $null; Metres = @() }
    if ($null -ne $Options) { $line['Journal'] = $Options }
    return ($line | ConvertTo-Json -Depth 8 -Compress)
}

# Runs every step of every group in order. $Invoke (param index, key, envelopeJson; returns @{ output; exit_code;
# timed_out }) sends catalogue envelopes; $InvokeWire (same shape, with the wire line) sends journal.read; without it
# those steps are not sent. $InvokeDocUpdate (same shape, with the doc-update request JSON: before, after, realized,
# bindings, accept, expected_document; owner: Invoke-CatalogueDocUpdate) runs the P4-14 doc-update steps; without it
# those steps are not sent. A doc-update line whose per-step report has an entry with reply_lost true (a write whose
# reply is missing) stops the run like a lost pipe. P4-16: the doc-realized step (DocRealized) goes through the same
# invoker with {record, fixture, assignments, document}: record = this run's own record so far (schema_version 1,
# document_unique_id, every earlier step as the owner session writes it), document = $DocumentUid; a realized_*
# refusal stops the run (its envelope is the step's response; the owner invoker keeps it in the run folder). $Progress (optional) receives one Spanish line per step; $Alive (optional) returns $false
# when the child Revit is gone. A timeout (unknown completion, also pipe_read_timeout), a lost pipe (pipe_connect_timeout, pipe_io_error,
# no_response), a CLI error or a closed Revit stops the run: the remaining steps of every group are not sent. A failed
# step or group never stops later groups: a later step that needs something a failed step should have made is not
# sent and says what is missing. A step whose fixture precondition is missing (New-CatalogueBlocked: no second level,
# no loaded type of a kind), and every step that needs what it should have picked or made, is blocked, not failed.
# The lost-pipe stop applies to the v1 steps too (before P1-06 each later v1 step was still sent and failed). Nothing
# is retried automatically. -V1Only runs only the 17 v1 steps. counts are the v1 counts (unchanged meaning, plus
# blocked); groups, overall, all_passed and aborted cover the whole run.
function Invoke-CatalogueSequence {
    param(
        [Parameter(Mandatory)][string]$Stamp,
        [Parameter(Mandatory)][int]$Run,
        [Parameter(Mandatory)][string]$DocumentPath,
        [Parameter(Mandatory)][string]$DocumentUid,
        [Parameter(Mandatory)][scriptblock]$Invoke,
        [scriptblock]$InvokeWire,
        [scriptblock]$InvokeDocUpdate,
        [scriptblock]$Progress,
        [scriptblock]$Alive,
        [switch]$V1Only
    )
    $picked = @{}
    # After a document.save_copy, Revit has the copy open in place of the source: c-save sets document_path to the
    # copy's path as read back, and every later request names it (UseSourceDocument steps keep the source on purpose).
    $createdBy = @{}
    $responses = @{}
    $rawText = @{}
    $blockedPicks = @{}
    $blockedSteps = @{}
    function Need([string[]]$Names) {
        foreach ($name in $Names) {
            if (!$picked.ContainsKey($name) -or !$picked[$name]) {
                if ($blockedPicks.ContainsKey($name)) { throw [InvalidOperationException]::new($blockedPicks[$name]) }
                throw "falta $name de un paso anterior"
            }
        }
    }
    function Created([string]$Key) {
        if (!$createdBy.ContainsKey($Key)) {
            if ($blockedSteps.ContainsKey($Key)) { throw [InvalidOperationException]::new($blockedSteps[$Key]) }
            throw "falta el elemento creado en el paso $Key"
        }
        return [string]$createdBy[$Key]
    }
    # Runs a selector; a missing precondition marks the named picks as blocked (for Need) and is rethrown.
    function Guard([string[]]$Names, [scriptblock]$Select) {
        try { return (& $Select) }
        catch {
            if (Test-CatalogueBlockedMessage $_.Exception.Message) { foreach ($name in $Names) { $blockedPicks[$name] = $_.Exception.Message } }
            throw
        }
    }
    function Block($Record, [string]$Message, [string]$Prefix) {
        $Record.blocked = $true
        $Record.reason = $Prefix + 'bloqueado: ' + $Message.Substring($script:CatalogueBlockedMarker.Length)
        $blockedSteps[$Record.key] = $Message
    }
    function Response([string]$Key) { if (!$responses.ContainsKey($Key)) { throw "falta la respuesta del paso $Key" }; return $responses[$Key] }
    $groups = Get-CatalogueGroups -V1Only:$V1Only
    $plan = [Collections.Generic.List[object]]::new()
    foreach ($group in $groups) { foreach ($step in $group.Steps) { $plan.Add(@{ Group = $group.Key; Spec = $step }) } }
    $records = [Collections.Generic.List[object]]::new()
    $aborted = $null
    $index = 0
    foreach ($item in $plan) {
        $index++
        $spec = $item.Spec
        $wire = $spec.ContainsKey('Wire') -and $spec.Wire
        # P4-14: a doc-update step is one CLI verb call; the CLI makes the operation ids of what it sends.
        $docUpdate = $spec.ContainsKey('DocUpdate') -and $spec.DocUpdate
        # P4-16: doc-realized is an offline CLI verb on the run record; it sends nothing to Revit.
        $docRealized = $spec.ContainsKey('DocRealized') -and $spec.DocRealized
        $operationId = if ($wire -or $docUpdate -or $docRealized) { $null } else { New-CatalogueOperationId $Stamp $Run $(if ($spec.ContainsKey('ReuseOf')) { $spec.ReuseOf } else { $spec.Key }) }
        $stepDocument = if (($spec.ContainsKey('UseSourceDocument') -and $spec.UseSourceDocument) -or !$picked.ContainsKey('document_path') -or !$picked['document_path']) { $DocumentPath } else { [string]$picked['document_path'] }
        $record = [ordered]@{ index = $index; group = $item.Group; key = $spec.Key; description = $spec.Es; command = $spec.Command; operation_id = $operationId; document_path = $stepDocument; expected = $spec.Expect; request = $null; response = $null; raw_response = $null; exit_code = $null; timed_out = $false; elapsed_ms = $null; passed = $false; blocked = $false; reason = $null; comparisons = [Collections.Generic.List[object]]::new() }
        $records.Add($record)
        if ($Progress) { & $Progress "Paso $index/$($plan.Count) [$($item.Group)]: $($spec.Es)" }
        if ($aborted) { $record.reason = "no enviado: $aborted"; continue }
        if ($Alive -and !(& $Alive)) { $aborted = 'el Revit de prueba se cerró'; $record.reason = "no enviado: $aborted"; continue }
        try {
            $stepArgs = & $spec.Build
            # Document (optional): the path this one step names instead (the copy probe after a save_copy).
            if ($spec.ContainsKey('Document')) { $stepDocument = [string](& $spec.Document); $record.document_path = $stepDocument }
            if ($spec.Command -eq 'document.save_as' -and ($unsafe = Test-CatalogueSaveTarget $spec $stepArgs $DocumentPath)) { throw $unsafe }
        }
        catch {
            if (Test-CatalogueBlockedMessage $_.Exception.Message) { Block $record $_.Exception.Message 'no enviado, ' }
            else { $record.reason = 'no enviado: ' + $_.Exception.Message }
            continue
        }
        if ($wire) {
            if (!$InvokeWire) { $record.reason = "no enviado: falta el canal directo al pipe para $($spec.Command)"; continue }
            $requestText = New-CatalogueWireLine $stepDocument $spec.Command $stepArgs
            $invoker = $InvokeWire
        }
        elseif ($docUpdate) {
            if (!$InvokeDocUpdate) { $record.reason = "no enviado: falta el canal doc-update (CLI) para $($spec.Key)"; continue }
            # The Revit document the CLI addresses is the one this step names.
            $stepArgs['bindings']['document'] = [ordered]@{ path = $stepDocument; unique_id = $DocumentUid }
            $requestText = $stepArgs | ConvertTo-Json -Depth 20 -Compress
            $invoker = $InvokeDocUpdate
        }
        elseif ($docRealized) {
            if (!$InvokeDocUpdate) { $record.reason = "no enviado: falta el canal doc-update (CLI) para $($spec.Key)"; continue }
            # This run's own record: every step before this one, as the owner session writes it (catalogue-runN.json).
            $stepArgs['record'] = [ordered]@{ schema_version = 1; document_unique_id = $DocumentUid; steps = $records.GetRange(0, $records.Count - 1).ToArray() }
            $stepArgs['document'] = $DocumentUid
            $requestText = $stepArgs | ConvertTo-Json -Depth 64 -Compress
            $invoker = $InvokeDocUpdate
        }
        else {
            $requestText = New-CatalogueEnvelope $operationId $stepDocument $DocumentUid $spec.Command $stepArgs
            $invoker = $Invoke
        }
        # P4-16: the doc-realized step records a stub of its request; the run record it sent is the invoker's record_file.
        $record.request = if ($docRealized) {
            [ordered]@{ record_steps = $stepArgs['record']['steps'].Count; record_document_unique_id = $DocumentUid; fixture = $stepArgs['fixture']; assignments = $stepArgs['assignments']; document = $DocumentUid; record_file = $null } | ConvertTo-Json -Depth 20 -Compress | ConvertFrom-Json
        } else { $requestText | ConvertFrom-Json }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try { $result = & $invoker $index $spec.Key $requestText } catch { $watch.Stop(); $aborted = 'error al ejecutar la CLI: ' + $_.Exception.Message; $record.reason = $aborted; continue }
        $watch.Stop()
        if ($docRealized -and $result -is [Collections.IDictionary] -and $result.Contains('record_file')) { $record.request.record_file = [string]$result['record_file'] }
        $record.elapsed_ms = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
        $record.exit_code = $result.exit_code
        $record.timed_out = [bool]$result.timed_out
        if ($record.timed_out) { $aborted = "el paso $index superó el tiempo (no se sabe si terminó)"; $record.reason = $aborted; continue }
        $text = ([string]$result.output).Trim()
        $parsed = $null
        try { $parsed = $text | ConvertFrom-Json -ErrorAction Stop } catch { $parsed = $null }
        # P4-16: only a JSON object is a reply; anything else ("x", [1], 2) is kept as raw_response like non-JSON text, so
        # the run record keeps response null or an object (as doc-realized reads it).
        $notObject = $null -ne $parsed -and $parsed -isnot [Management.Automation.PSCustomObject]
        if ($notObject) { $parsed = $null }
        # P4-14: an --execute call may have sent writes; without its usable line (a crash, a cut output, an exit code
        # the CLI never uses) their outcome is unknown, so nothing more is sent. A dry run sends no write: only the step fails.
        $executing = $docUpdate -and $null -ne (Get-CatalogueMember $stepArgs 'expected_document')
        $unusable = $null -eq $parsed -or ($executing -and ($result.exit_code -notin 0, 1 -or $null -eq (Get-CatalogueMember $parsed 'status') -or ([string](Get-CatalogueMember $parsed 'code') -eq 'doc_update_internal_error' -and $null -eq (Get-CatalogueMember $parsed 'report'))))
        if ($executing -and $unusable) {
            $record.raw_response = $text
            if ($null -ne $parsed) { $record.response = $parsed }
            $aborted = "doc-update --execute del paso $index ($($spec.Key)) terminó sin una línea utilizable de doc-update (código de salida $($result.exit_code)); pudo haber escrito, no se sabe qué: no se envía nada más"
            $record.reason = $aborted
            continue
        }
        if ($null -eq $parsed) { $record.raw_response = $text; $record.reason = $(if ($notObject) { 'response is not a JSON object' } else { 'response is not JSON' }); continue }
        $record.response = $parsed
        $responses[$spec.Key] = $parsed
        $rawText[$spec.Key] = $text
        $code = [string](Get-CatalogueMember $parsed 'code')
        # N1: Revit wrote a save outside the enabled roots or through a link (RevitSaveAsRules.WrittenElsewhereDetail):
        # the open document IS that file, so nothing more is sent (no probe switches the run to it).
        if (([string](Get-CatalogueMember $parsed 'detail')).Contains($script:CatalogueWrittenElsewhereMarker, [StringComparison]::Ordinal)) {
            $aborted = "Revit guardó el archivo fuera de las carpetas habilitadas o por un enlace en el paso $index; no se envía nada más: ciérrelo en Revit sin guardar y revise ese archivo"
            $record.reason = "$aborted ($(Get-CatalogueMember $parsed 'detail'))"
            continue
        }
        # P4-16: a realized_* refusal means this run's record does not say what the session believes it made (a step
        # missing, failed, deleted or reporting one element twice): nothing more is sent.
        if ($docRealized -and [string](Get-CatalogueMember $parsed 'status') -eq 'failed' -and $code.StartsWith('realized_', [StringComparison]::Ordinal)) {
            $aborted = "doc-realized rechazó el registro de esta ejecución en el paso $index ($($spec.Key)): $code $(Get-CatalogueMember $parsed 'detail'); el registro no coincide con lo que la sesión cree haber creado: no se envía nada más"
            $record.reason = $aborted
            continue
        }
        # P4-14: a doc-update write whose reply was lost may have run in Revit: nothing more is sent (never retried).
        if ($docUpdate) {
            $lost = @(Get-CatalogueList $parsed 'report' | ForEach-Object { $_ } | Where-Object { (Get-CatalogueMember $_ 'reply_lost') -eq $true })
            if ($lost.Count) {
                $first = $lost[0]
                $aborted = "doc-update perdió la respuesta de $(Get-CatalogueMember $first 'command') ($(Get-CatalogueMember $first 'key'), $(Get-CatalogueMember $first 'operation_id'): $(Get-CatalogueMember $first 'code')) en el paso $index; no se sabe si Revit lo ejecutó y no se envía nada más"
                $record.reason = $aborted
                continue
            }
        }
        if ($script:CatalogueTransportCodes -contains $code -and $spec.Expect['Code'] -ne $code) {
            $aborted = if ($code -eq 'pipe_read_timeout') { "el paso $index no recibió respuesta a tiempo ($code; no se sabe si terminó)" } else { "se perdió la conexión con el Revit de prueba en el paso $index ($code)" }
            $record.reason = $aborted
            continue
        }
        $reason = if ($wire) { Test-CatalogueWireResponse $parsed $result.exit_code } elseif ($docRealized) {
            $line = Test-CatalogueDocUpdateLine $parsed $result.exit_code $spec.Expect 'doc-realized'
            # The output file the CLI names must be the one the invoker asked for (when it says which).
            $printed = [string](Get-CatalogueMember $parsed 'output')
            if ($null -eq $line -and $result -is [Collections.IDictionary] -and $result.Contains('realized_file') -and $printed -and [IO.Path]::GetFullPath($printed) -ne [IO.Path]::GetFullPath([string]$result['realized_file'])) { $line = "doc-realized: output file $printed, expected $($result['realized_file'])" }
            $line
        } elseif ($docUpdate) {
            $line = Test-CatalogueDocUpdateLine $parsed $result.exit_code $spec.Expect
            # The plan file the CLI names must be the one the invoker asked for (when it says which).
            $printed = [string](Get-CatalogueMember $parsed 'output')
            if ($null -eq $line -and $result -is [Collections.IDictionary] -and $result.Contains('plan_file') -and $printed -and [IO.Path]::GetFullPath($printed) -ne [IO.Path]::GetFullPath([string]$result['plan_file'])) { $line = "doc-update: plan file $printed, expected $($result['plan_file'])" }
            $line
        } else { Test-CatalogueResponse $parsed $result.exit_code $operationId $spec.Expect }
        if ($null -eq $reason) {
            $script:CatalogueComparisons = $record.comparisons
            try { $reason = & $spec.Check $parsed } catch { $reason = $_.Exception.Message } finally { $script:CatalogueComparisons = $null }
        }
        $record.passed = $null -eq $reason
        $record.reason = $reason
        if ($reason -is [string] -and (Test-CatalogueBlockedMessage $reason)) { Block $record $reason '' }
        elseif (!$record.passed -and $spec.ContainsKey('Note')) { $record.reason = "$reason ($($spec.Note))" }
        if ($record.passed) {
            $first = Get-CatalogueFirstCreated $parsed
            if ($first) { $createdBy[$spec.Key] = $first }
        }
    }
    $all = $records.ToArray()
    $summaries = @(foreach ($group in $groups) {
        $counts = Get-CatalogueCounts @($all | Where-Object { $_.group -eq $group.Key })
        [ordered]@{ key = $group.Key; description = $group.Es; total = $counts.total; passed = $counts.passed; failed = $counts.failed; blocked = $counts.blocked; not_sent = $counts.not_sent }
    })
    $overall = Get-CatalogueCounts $all
    return [ordered]@{
        picked = $picked
        steps = $all
        counts = Get-CatalogueCounts @($all | Where-Object { $_.group -eq 'v1' })
        groups = $summaries
        overall = $overall
        all_passed = ($overall.failed -eq 0 -and $overall.blocked -eq 0)
        aborted = $aborted
    }
}

function Format-CatalogueResult($Counts) { "Catálogo: $($Counts.passed)/$($Counts.total) pasos OK" }

# The owner's result line: the v1 line unchanged ("Catálogo: 17/17 pasos OK"), then, when later groups ran, each
# group's passed/total (and blocked steps), the overall total and the verdict: TODO OK; OK con bloqueados: <group (n)>
# when nothing failed but fixture preconditions were missing; FALLÓ en <groups> (plus the blocked groups); and why the
# run stopped. A v1-only run that stopped adds the reason after the v1 line.
function Format-CatalogueSummary($Result) {
    $line = Format-CatalogueResult $Result.counts
    $extra = @(@($Result.groups) | Where-Object { $_.key -ne 'v1' })
    if (!$extra.Count) { if ($Result.aborted) { return "$line; DETENIDO: $($Result.aborted)" }; return $line }
    $parts = @($extra | ForEach-Object { "$($_.key) $($_.passed)/$($_.total)$(if ($_['blocked']) { " ($($_['blocked']) bloqueados)" })" })
    $failed = @(@($Result.groups) | Where-Object { $_.failed -gt 0 } | ForEach-Object { $_.key })
    $blocked = @(@($Result.groups) | Where-Object { $_['blocked'] -gt 0 } | ForEach-Object { "$($_.key) ($($_['blocked']))" })
    $verdict = if ($failed.Count) { 'FALLÓ en ' + ($failed -join ', ') + $(if ($blocked.Count) { '; bloqueados en ' + ($blocked -join ', ') }) }
    elseif ($blocked.Count) { 'OK con bloqueados: ' + ($blocked -join ', ') }
    else { 'TODO OK' }
    if ($Result.aborted) { $verdict += " (DETENIDO: $($Result.aborted))" }
    return "$line; grupos: $($parts -join ', '); total $($Result.overall.passed)/$($Result.overall.total); resultado: $verdict"
}

# Sums several runs' results (counts, groups by key, overall) for the session summary line.
function Join-CatalogueResults([object[]]$Results) {
    $names = @('total', 'passed', 'failed', 'blocked', 'not_sent')
    $v1 = [ordered]@{ total = 0; passed = 0; failed = 0; blocked = 0; not_sent = 0 }
    $overall = [ordered]@{ total = 0; passed = 0; failed = 0; blocked = 0; not_sent = 0 }
    $groups = [ordered]@{}
    $aborted = $null
    foreach ($result in @($Results | Where-Object { $null -ne $_ })) {
        foreach ($name in $names) { $v1[$name] += [int]$result.counts[$name]; $overall[$name] += [int]$result.overall[$name] }
        foreach ($group in @($result.groups)) {
            if (!$groups.Contains($group.key)) { $groups[$group.key] = [ordered]@{ key = $group.key; description = $group.description; total = 0; passed = 0; failed = 0; blocked = 0; not_sent = 0 } }
            foreach ($name in $names) { $groups[$group.key][$name] += [int]$group[$name] }
        }
        if (!$aborted -and $result.aborted) { $aborted = $result.aborted }
    }
    return [ordered]@{ counts = $v1; groups = @($groups.Values); overall = $overall; all_passed = ($overall.failed -eq 0 -and $overall.blocked -eq 0); aborted = $aborted }
}

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


# The member $Name of a JSON object element (System.Text.Json), or $null when $Element is not an object or lacks it.
function Get-CatalogueJsonProperty([Text.Json.JsonElement]$Element, [string]$Name) {
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { return $null }
    foreach ($property in $Element.EnumerateObject()) { if ($property.Name -ceq $Name) { return , $property.Value } }
    return $null
}

# P4-14: one doc-update call. The request JSON ({before, after, realized, bindings, accept, expected_document}) goes to a
# new numbered file like the CLI requests (NN-key.json) and each input member, its JSON text exactly as sent, to
# NN-key.doc-update/<member>.json; then "dotnet Bim.Cli.dll doc-update --before ... --realized ... --bindings ...
# --output NN-key.doc-update/plan.json --probe-dependents <pipe> [--accept uid]... [--execute --expected-document
# <sha256>]" (--execute only when expected_document is a string) with a bounded wait. On timeout only that CLI process
# is stopped (never Revit) and the step counts as unknown completion. The CLI's one stdout line (the dry summary, a
# refusal, or the per-step report of an --execute run) is kept in the run folder as NN-key.doc-update/report.json, its
# stderr as NN-key.doc-update/stderr.txt. P4-16: a request with realized_file (and realized_sha256) gives
# NN-key.doc-update/realized.json the bytes of that file (the doc-realized output), refused (thrown: the run stops,
# nothing is sent) unless they hash to realized_sha256; the realized member is then not written. A request with a record member is
# a doc-realized call: Invoke-CatalogueDocRealized.
function Invoke-CatalogueDocUpdate {
    param(
        [Parameter(Mandatory)][string]$Dotnet,
        [Parameter(Mandatory)][string]$CliDll,
        [Parameter(Mandatory)][string]$PipeName,
        [Parameter(Mandatory)][string]$RequestDirectory,
        [Parameter(Mandatory)][int]$Index,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Json,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300
    )
    function Write-NewText([string]$Path, [string]$Text) {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    }
    $probe = [Text.Json.JsonDocument]::Parse($Json, [Text.Json.JsonDocumentOptions]@{ MaxDepth = 128 })
    try { $isRealized = $null -ne (Get-CatalogueJsonProperty $probe.RootElement 'record') } finally { $probe.Dispose() }
    if ($isRealized) { return Invoke-CatalogueDocRealized -Dotnet $Dotnet -CliDll $CliDll -RequestDirectory $RequestDirectory -Index $Index -Key $Key -Json $Json -TimeoutSeconds $TimeoutSeconds }
    New-Item -ItemType Directory -Force $RequestDirectory | Out-Null
    $name = '{0:D2}-{1}' -f $Index, $Key
    $file = Join-Path $RequestDirectory "$name.json"
    $folder = Join-Path $RequestDirectory "$name.doc-update"
    Write-NewText $file $Json
    New-Item -ItemType Directory $folder -ErrorAction Stop | Out-Null
    $inputs = [ordered]@{}
    $accept = [Collections.Generic.List[string]]::new()
    $expected = $null
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        $root = $document.RootElement
        $realizedFile = $null
        $realizedSha = $null
        $fileElement = Get-CatalogueJsonProperty $root 'realized_file'
        if ($null -ne $fileElement -and $fileElement.ValueKind -eq [Text.Json.JsonValueKind]::String) {
            $realizedFile = $fileElement.GetString()
            $realizedSha = $root.GetProperty('realized_sha256').GetString()
        }
        foreach ($member in 'before', 'after', 'realized', 'bindings') {
            $inputs[$member] = Join-Path $folder "$member.json"
            if ($member -eq 'realized' -and $null -ne $realizedFile) {
                # P4-16: doc-update --realized reads exactly the doc-realized output (a copy of its bytes in this folder).
                $bytes = [IO.File]::ReadAllBytes($realizedFile)
                if (![string]::Equals([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)), $realizedSha, [StringComparison]::OrdinalIgnoreCase)) { throw "el mapa realizado $realizedFile ya no tiene el sha256 que informó doc-realized ($realizedSha)" }
                $stream = [IO.File]::Open($inputs[$member], [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
                continue
            }
            Write-NewText $inputs[$member] $root.GetProperty($member).GetRawText()
        }
        foreach ($item in $root.GetProperty('accept').EnumerateArray()) { $accept.Add($item.GetString()) }
        $expectedElement = $root.GetProperty('expected_document')
        if ($expectedElement.ValueKind -eq [Text.Json.JsonValueKind]::String) { $expected = $expectedElement.GetString() }
    } finally { $document.Dispose() }
    $plan = Join-Path $folder 'plan.json'
    $report = Join-Path $folder 'report.json'
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @($CliDll, 'doc-update', '--before', $inputs['before'], '--after', $inputs['after'], '--realized', $inputs['realized'], '--bindings', $inputs['bindings'], '--output', $plan, '--probe-dependents', $PipeName)) { $arguments.Add($argument) }
    foreach ($uid in $accept) { $arguments.Add('--accept'); $arguments.Add($uid) }
    if ($null -ne $expected) { foreach ($argument in @('--execute', '--expected-document', $expected)) { $arguments.Add($argument) } }
    $start = [Diagnostics.ProcessStartInfo]::new($Dotnet)
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
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
        $output = $out.GetAwaiter().GetResult()
        $stderr = $err.GetAwaiter().GetResult()
        if ($output.Trim()) { Write-NewText $report $output.Trim() }
        Write-NewText (Join-Path $folder 'stderr.txt') $stderr
        return @{ output = $output; stderr = $stderr; exit_code = $(if ($exited) { $process.ExitCode } else { $null }); timed_out = !$exited; request_file = $file; plan_file = $plan; report_file = $report }
    } finally { $process.Dispose() }
}

# P4-16: one doc-realized call (interfaces/cli/DocRealizedCommand.cs; offline: no pipe, no Revit). The request JSON
# ({record, fixture, assignments, document}) goes to a new numbered file NN-key.json and its members, their JSON text
# exactly as sent, to NN-key.doc-realized/record.json, fixture.json and assignments.json; then "dotnet Bim.Cli.dll
# doc-realized --record ... --fixture ... --assignments ... --document <UniqueId> --output NN-key.doc-realized/realized.json"
# with a bounded wait (on timeout only that CLI process is stopped). The CLI's one stdout line (success or a typed
# refusal envelope) is kept as NN-key.doc-realized/report.json, its stderr as stderr.txt; the output path is returned
# as realized_file, the record file as record_file.
function Invoke-CatalogueDocRealized {
    param(
        [Parameter(Mandatory)][string]$Dotnet,
        [Parameter(Mandatory)][string]$CliDll,
        [Parameter(Mandatory)][string]$RequestDirectory,
        [Parameter(Mandatory)][int]$Index,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Json,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300
    )
    function Write-NewText([string]$Path, [string]$Text) {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    }
    New-Item -ItemType Directory -Force $RequestDirectory | Out-Null
    $name = '{0:D2}-{1}' -f $Index, $Key
    $file = Join-Path $RequestDirectory "$name.json"
    $folder = Join-Path $RequestDirectory "$name.doc-realized"
    Write-NewText $file $Json
    New-Item -ItemType Directory $folder -ErrorAction Stop | Out-Null
    $inputs = [ordered]@{}
    $document = [Text.Json.JsonDocument]::Parse($Json, [Text.Json.JsonDocumentOptions]@{ MaxDepth = 128 })
    try {
        $root = $document.RootElement
        foreach ($member in 'record', 'fixture', 'assignments') {
            $inputs[$member] = Join-Path $folder "$member.json"
            Write-NewText $inputs[$member] $root.GetProperty($member).GetRawText()
        }
        $documentUid = $root.GetProperty('document').GetString()
    } finally { $document.Dispose() }
    $output = Join-Path $folder 'realized.json'
    $report = Join-Path $folder 'report.json'
    $start = [Diagnostics.ProcessStartInfo]::new($Dotnet)
    foreach ($argument in @($CliDll, 'doc-realized', '--record', $inputs['record'], '--fixture', $inputs['fixture'], '--assignments', $inputs['assignments'], '--document', $documentUid, '--output', $output)) { $start.ArgumentList.Add($argument) }
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
        $text = $out.GetAwaiter().GetResult()
        $stderr = $err.GetAwaiter().GetResult()
        if ($text.Trim()) { Write-NewText $report $text.Trim() }
        Write-NewText (Join-Path $folder 'stderr.txt') $stderr
        return @{ output = $text; stderr = $stderr; exit_code = $(if ($exited) { $process.ExitCode } else { $null }); timed_out = !$exited; request_file = $file; realized_file = $output; report_file = $report; record_file = $inputs['record'] }
    } finally { $process.Dispose() }
}

# One wire line straight to the add-in pipe, for Operations outside the revit.command catalogue (journal.read, which is
# read-only): the line is saved to a new numbered file like the CLI requests, sent once on the process pipe and one
# response line is read with a bounded wait. A pipe that cannot be reached answers pipe_connect_timeout or pipe_io_error
# (the run then stops); a reply that does not come in time counts as a timeout (unknown completion).
function Invoke-CatalogueWire {
    param(
        [Parameter(Mandatory)][string]$PipeName,
        [Parameter(Mandatory)][string]$Line,
        [string]$RequestDirectory,
        [int]$Index,
        [string]$Key,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 120,
        [ValidateRange(100, 60000)][int]$ConnectTimeoutMs = 5000
    )
    if ($RequestDirectory) {
        New-Item -ItemType Directory -Force $RequestDirectory | Out-Null
        $file = Join-Path $RequestDirectory ('{0:D2}-{1}.json' -f $Index, $Key)
        $stream = [IO.File]::Open($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Line); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    }
    function Failure([string]$Code, [string]$Detail) {
        $failure = [ordered]@{ status = 'failed'; code = $Code; field = 'pipe_name'; detail = $Detail; created = @(); modified = @(); deleted = @(); readback = $null; receipt = $null; warnings = @() }
        return @{ output = ($failure | ConvertTo-Json -Compress); exit_code = 1; timed_out = $false }
    }
    function Inner($Exception) {
        while ($Exception -is [Management.Automation.MethodInvocationException] -and $null -ne $Exception.InnerException) { $Exception = $Exception.InnerException }
        return $Exception
    }
    # Windows (the owner session): the named pipe. Elsewhere (headless tests only) .NET pipes are Unix sockets named
    # <temp>/CoreFxPipe_<name>; pwsh caches that prefix at startup, so the socket is opened at the current temp path,
    # where the test's fake add-in pipe (a child process) listens, retrying until the connect timeout like the CLI.
    $pipe = $null
    try {
        try {
            if ($IsWindows) {
                $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
                $pipe.Connect($ConnectTimeoutMs)
            }
            else {
                $endpoint = [Net.Sockets.UnixDomainSocketEndPoint]::new([IO.Path]::Combine([IO.Path]::GetTempPath(), "CoreFxPipe_$PipeName"))
                $deadline = [DateTime]::UtcNow.AddMilliseconds($ConnectTimeoutMs)
                while ($null -eq $pipe) {
                    $socket = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::Unix, [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Unspecified)
                    try { $socket.Connect($endpoint); $pipe = [Net.Sockets.NetworkStream]::new($socket, $true) }
                    catch {
                        $socket.Dispose()
                        if ([DateTime]::UtcNow -ge $deadline) { throw [TimeoutException]::new('no listener on ' + $endpoint.ToString()) }
                        Start-Sleep -Milliseconds 50
                    }
                }
            }
        }
        catch {
            $error0 = Inner $_.Exception
            if ($error0 -is [TimeoutException]) { return Failure 'pipe_connect_timeout' 'no add-in listening on this process pipe' }
            return Failure 'pipe_io_error' ($error0.GetType().Name + ': ' + $error0.Message)
        }
        $utf8 = [Text.UTF8Encoding]::new($false)
        $writer = [IO.StreamWriter]::new($pipe, $utf8, 4096, $true)
        $reader = [IO.StreamReader]::new($pipe, $utf8, $false, 4096, $true)
        try {
            $writer.AutoFlush = $true
            $writer.WriteLine($Line)
            $read = $reader.ReadLineAsync()
            if (!$read.Wait($TimeoutSeconds * 1000)) { return @{ output = ''; exit_code = $null; timed_out = $true } }
            $response = $read.GetAwaiter().GetResult()
        }
        catch { $error1 = Inner $_.Exception; return Failure 'pipe_io_error' ($error1.GetType().Name + ': ' + $error1.Message) }
        finally { try { $reader.Dispose(); $writer.Dispose() } catch { $null = $_ } }
        if ($null -eq $response) { return Failure 'no_response' 'pipe closed without a response line' }
        $status = $null
        try { $status = ($response | ConvertFrom-Json -ErrorAction Stop).status } catch { $status = $null }
        return @{ output = $response; exit_code = $(if ($status -eq 'succeeded') { 0 } else { 1 }); timed_out = $false }
    }
    finally { if ($null -ne $pipe) { $pipe.Dispose() } }
}
