# Test double for the C03 add-in executor, used only by test-catalogue-session.ps1 (in process) and
# fake-revit-command-pipe.ps1 (behind a real named pipe). It answers catalogue envelopes (v1 and every command the P1-06
# one-run owner sequence sends: v2, views, query.snapshot, query.elements/inspect, structure, documentation,
# element.set_parameter/delete) with the response shape of RevitCommandResponse.ToJson and the readback member names
# of the add-in executor, keeps receipts by operation_id (replay on the same request text, conflict on a different
# one; dry runs store none), keeps a small element table so references, dependents, snapshots and the journal.read
# wire answer (Invoke-FakeRevitWire) are consistent, and records every operation_id it receives. Faults (hashtable):
# ReplayNotFlagged, ReplayNewElement, WrongRadius (v1); FailCommands = @(names) answer transaction_failed;
# RoofSlopeOff reports a different roof slope; SnapshotUnstable makes every snapshot differ. It proves nothing about Revit.
Set-StrictMode -Version Latest

$script:FakeBuiltInCategories = @{ wall = 'OST_Walls'; door = 'OST_Doors'; window = 'OST_Windows'; floor = 'OST_Floors'; room = 'OST_Rooms'; roof = 'OST_Roofs'; stair = 'OST_Stairs'; grid = 'OST_Grids'; level = 'OST_Levels' }
$script:FakeCategoryNames = @{ wall = 'Walls'; door = 'Doors'; window = 'Windows'; floor = 'Floors'; room = 'Rooms'; roof = 'Roofs'; stair = 'Stairs'; grid = 'Grids' }

function New-FakeRevitState([string]$DocumentUid, [hashtable]$Faults = @{}) {
    @{ DocumentUid = $DocumentUid; Receipts = @{}; Received = [Collections.Generic.List[string]]::new(); Next = 0; Faults = $Faults; Episode = '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0'
        Elements = [ordered]@{}; Cache = @{}; Placed = @{}; ElementNumber = 100000; SkippedAddIn = 0; JournalBaseline = $null; Snapshots = 0 }
}

function New-FakeUniqueId($State) { $State.Next++; '{0}-{1:x8}' -f $State.Episode, (4096 + $State.Next) }

function ConvertTo-FakeResponseJson($Response) { $Response | ConvertTo-Json -Depth 20 -Compress }

function New-FakeFailure([string]$Code, [string]$Field, [string]$Detail) {
    [ordered]@{ status = 'failed'; code = $Code; field = $Field; detail = $Detail; created = @(); modified = @(); deleted = @(); readback = $null; receipt = $null; warnings = @() }
}

function Get-FakeSha256([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($Text))).ToLowerInvariant()
}

# Optional member of a parsed request object; writes nothing when absent (arrays come out as their items).
function Get-FakeArg($Arguments, [string]$Name) {
    if ($null -ne $Arguments -and $null -ne $Arguments.PSObject.Properties[$Name]) { $Arguments.PSObject.Properties[$Name].Value }
}

# A list made once per state (stable UniqueIds across repeated queries).
function Get-FakeCached($State, [string]$Key, [scriptblock]$Create) {
    if (!$State.Cache.ContainsKey($Key)) { $State.Cache[$Key] = @(& $Create) }
    return , $State.Cache[$Key]
}

function Get-FakeLevels($State) {
    return , (Get-FakeCached $State 'levels' {
        [ordered]@{ name = 'Level 1'; unique_id = (New-FakeUniqueId $State); elevation_mm = 0; project_elevation_mm = 0 }
        [ordered]@{ name = 'Level 2'; unique_id = (New-FakeUniqueId $State); elevation_mm = 4000; project_elevation_mm = 4000 } })
}

function Get-FakeLevelRef($State, $LevelUid) {
    if ($null -eq $LevelUid) { return $null }
    foreach ($level in (Get-FakeLevels $State)) { if ($level.unique_id -eq $LevelUid) { return [ordered]@{ name = $level.name; unique_id = $level.unique_id } } }
    return [ordered]@{ name = $null; unique_id = [string]$LevelUid }
}

function Add-FakeElement($State, [string]$UniqueId, [string]$Category, [hashtable]$Facts) {
    $State.ElementNumber++
    $Facts['Id'] = $State.ElementNumber
    $Facts['UniqueId'] = $UniqueId
    $Facts['Category'] = $Category
    $State.Elements[$UniqueId] = $Facts
}

function Get-FakeGuid([string]$UniqueId) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($UniqueId))
    return ([guid]::new([byte[]]$hash[0..15])).ToString('D')
}

function Get-FakeArea($Points) {
    $list = @($Points)
    $twice = 0.0
    for ($i = 0; $i -lt $list.Count; $i++) {
        $p = $list[$i]; $q = $list[($i + 1) % $list.Count]
        $twice += [double]$p[0] * [double]$q[1] - [double]$q[0] * [double]$p[1]
    }
    return [Math]::Abs($twice) / 2
}

# Distance from point P to the line through A and B (millimetres).
function Get-FakeLineDistance($P, $A, $B) {
    $dx = [double]$B[0] - [double]$A[0]; $dy = [double]$B[1] - [double]$A[1]
    return [Math]::Abs(([double]$P[0] - [double]$A[0]) * $dy - ([double]$P[1] - [double]$A[1]) * $dx) / [Math]::Sqrt($dx * $dx + $dy * $dy)
}

function New-FakeTextParameter([string]$Name, [string]$BuiltIn, $Value) {
    [ordered]@{ name = $Name; id = -1; built_in = $BuiltIn; source = 'instance'; storage_type = 'string'; spec = $null; has_value = ($null -ne $Value); read_only = $false; shared = $false; value = $Value; unit = $null; value_unique_id = $null; display = $Value; value_truncated = $false }
}

function New-FakeLengthParameter([string]$Name, [string]$BuiltIn, [double]$Mm, [bool]$ReadOnly) {
    [ordered]@{ name = $Name; id = -2; built_in = $BuiltIn; source = 'instance'; storage_type = 'double'; spec = 'autodesk.spec.aec:length-2.0.0'; has_value = $true; read_only = $ReadOnly; shared = $false; value = $Mm; unit = 'mm'; value_unique_id = $null; display = "$Mm mm"; value_truncated = $false }
}

function New-FakeQueryRow($State, $Element, [string[]]$Names) {
    $parameters = [Collections.Generic.List[object]]::new()
    $missing = [Collections.Generic.List[string]]::new()
    foreach ($name in $Names) {
        if ($name -in 'Mark', 'ALL_MODEL_MARK') { $parameters.Add((New-FakeTextParameter 'Mark' 'ALL_MODEL_MARK' $Element['Mark'])) }
        elseif ($name -in 'Comments', 'ALL_MODEL_INSTANCE_COMMENTS') { $parameters.Add((New-FakeTextParameter 'Comments' 'ALL_MODEL_INSTANCE_COMMENTS' $Element['Comments'])) }
        else { $missing.Add($name) }
    }
    [ordered]@{ id = $Element.Id; unique_id = $Element.UniqueId; category = $script:FakeCategoryNames[$Element.Category]; built_in_category = $script:FakeBuiltInCategories[$Element.Category]
        level = (Get-FakeLevelRef $State $Element['Level']); family = 'Fake'; type = 'Fake'; type_unique_id = $Element['Type']; parameters = $parameters.ToArray(); missing_parameters = $missing.ToArray() }
}

function Get-FakeParameters($Element) {
    New-FakeTextParameter 'Comments' 'ALL_MODEL_INSTANCE_COMMENTS' $Element['Comments']
    if ($Element.Category -eq 'wall') {
        New-FakeLengthParameter 'Unconnected Height' 'WALL_USER_HEIGHT_PARAM' ([double]$Element['HeightMm']) $false
        New-FakeLengthParameter 'Length' 'CURVE_ELEM_LENGTH' 1000 $true
    }
}

# Elements removed with the targets: hosted by or referencing a removed element, transitively.
function Get-FakeDependents($State, [string[]]$Targets) {
    $gone = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($target in $Targets) { $null = $gone.Add($target) }
    $result = [Collections.Generic.List[string]]::new()
    do {
        $grew = $false
        foreach ($element in @($State.Elements.Values)) {
            if ($gone.Contains($element.UniqueId)) { continue }
            $links = @(@($element['Host']) + @($element['References']) | Where-Object { $null -ne $_ })
            if (@($links | Where-Object { $gone.Contains([string]$_) }).Count) { $null = $gone.Add($element.UniqueId); $result.Add($element.UniqueId); $grew = $true }
        }
    } while ($grew)
    return , $result.ToArray()
}

# The query.snapshot readback (MirrorSnapshot.ToJson shape) of the element table: levels, straight basic walls, hosted
# doors and windows as BIM-IR elements; curtain and curved walls and floors under unmapped.
function Get-FakeSnapshot($State) {
    $elements = [Collections.Generic.List[object]]::new()
    $sources = [Collections.Generic.List[object]]::new()
    $unmapped = [Collections.Generic.List[object]]::new()
    $provenance = [ordered]@{ Source = 'revit-snapshot'; Approval = 'observed' }
    $levelIds = @{}
    foreach ($level in (Get-FakeLevels $State)) {
        $id = Get-FakeGuid $level.unique_id
        $levelIds[[string]$level.unique_id] = $id
        $elements.Add([ordered]@{ Id = $id; Kind = 'level'; GeometryMetres = @([double]$level.elevation_mm / 1000); Dependencies = @(); Provenance = $provenance })
        $sources.Add([ordered]@{ id = $id; unique_id = $level.unique_id; category = 'level' })
    }
    $walls = @{}
    foreach ($element in @($State.Elements.Values)) {
        if ($element.Category -ne 'wall') { continue }
        $uid = $element.UniqueId
        if ($element['Kind'] -eq 'Curtain') { $unmapped.Add([ordered]@{ category = 'wall'; unique_id = $uid; reason = 'curtain_wall_unsupported'; detail = 'curtain wall' }); continue }
        if ($element['Curve'] -ne 'line') { $unmapped.Add([ordered]@{ category = 'wall'; unique_id = $uid; reason = 'curved_wall_unsupported'; detail = 'curved wall' }); continue }
        $id = Get-FakeGuid $uid
        $levelId = $levelIds[[string]$element['Level']]
        $walls[$uid] = @{ Id = $id; Start = $element.Start; End = $element.End; LevelId = $levelId }
        $elements.Add([ordered]@{ Id = $id; Kind = 'wall'; GeometryMetres = @(([double]$element.Start[0] / 1000), ([double]$element.Start[1] / 1000), ([double]$element.End[0] / 1000), ([double]$element.End[1] / 1000), ([double]$element.HeightMm / 1000), 0.2); Dependencies = @($levelId); Provenance = $provenance })
        $sources.Add([ordered]@{ id = $id; unique_id = $uid; category = 'wall' })
    }
    foreach ($element in @($State.Elements.Values)) {
        if ($element.Category -notin 'door', 'window') { continue }
        $hostWall = $walls[[string]$element['Host']]
        if ($null -eq $hostWall) { $unmapped.Add([ordered]@{ category = $element.Category; unique_id = $element.UniqueId; reason = 'host_unknown'; detail = 'host not mapped' }); continue }
        $sx = [double]$hostWall.Start[0]; $sy = [double]$hostWall.Start[1]; $ex = [double]$hostWall.End[0]; $ey = [double]$hostWall.End[1]
        $length = [Math]::Sqrt(($ex - $sx) * ($ex - $sx) + ($ey - $sy) * ($ey - $sy))
        $ux = ($ex - $sx) / $length; $uy = ($ey - $sy) / $length
        $from = [double]$element.Along - [double]$element.WidthMm / 2
        $to = [double]$element.Along + [double]$element.WidthMm / 2
        $id = Get-FakeGuid $element.UniqueId
        $elements.Add([ordered]@{ Id = $id; Kind = $element.Category; GeometryMetres = @((($sx + $ux * $from) / 1000), (($sy + $uy * $from) / 1000), (($sx + $ux * $to) / 1000), (($sy + $uy * $to) / 1000), ([double]$element.Sill / 1000), ([double]$element.HeightMm / 1000)); Dependencies = @($hostWall.LevelId, $hostWall.Id); Provenance = $provenance })
        $sources.Add([ordered]@{ id = $id; unique_id = $element.UniqueId; category = $element.Category })
    }
    foreach ($element in @($State.Elements.Values)) {
        if ($element.Category -eq 'floor') { $unmapped.Add([ordered]@{ category = 'floor'; unique_id = $element.UniqueId; reason = 'no_bim_ir_kind'; detail = 'BIM-IR has no floor or slab element kind' }) }
    }
    if ($State.Faults.ContainsKey('SnapshotUnstable')) { $State.Snapshots++; $unmapped.Add([ordered]@{ category = 'wall'; unique_id = 'unstable'; reason = 'missing_data'; detail = "read $($State.Snapshots)" }) }
    return [ordered]@{ snapshot_version = 1; document_unique_id = $State.DocumentUid; units = 'm'; elements = $elements.ToArray(); sources = $sources.ToArray(); unmapped = $unmapped.ToArray() }
}

function New-FakeMissing([string]$Field, [string]$UniqueId) { New-FakeFailure 'reference_not_found' $Field "no element with UniqueId $UniqueId" }

# $CommandText is the envelope exactly as received (the pipe's Command member); returns one response line.
function Invoke-FakeRevitCommand($State, [string]$CommandText) {
    $envelope = $CommandText | ConvertFrom-Json
    $operationId = [string]$envelope.operation_id
    $State.Received.Add($operationId)
    $command = [string]$envelope.command
    $a = $envelope.args
    $sha = Get-FakeSha256 $CommandText
    $writes = $command -notlike 'query.*'
    $dry = [bool](Get-FakeArg $a 'dry_run')
    if ($writes -and $State.Receipts.ContainsKey($operationId)) {
        $stored = $State.Receipts[$operationId]
        if ($stored.Sha -ne $sha) { return ConvertTo-FakeResponseJson (New-FakeFailure 'operation_id_conflict' 'operation_id' 'operation_id already used for a different request') }
        $replay = [ordered]@{}
        foreach ($entry in $stored.Response.GetEnumerator()) { $replay[$entry.Key] = $entry.Value }
        $receipt = [ordered]@{}
        foreach ($entry in $stored.Response.receipt.GetEnumerator()) { $receipt[$entry.Key] = $entry.Value }
        $receipt.replayed = -not $State.Faults.ContainsKey('ReplayNotFlagged')
        $replay.receipt = $receipt
        if ($State.Faults.ContainsKey('ReplayNewElement')) { $replay.created = @(New-FakeUniqueId $State) }
        return ConvertTo-FakeResponseJson $replay
    }
    if ($State.Faults.ContainsKey('FailCommands') -and @($State.Faults['FailCommands']) -contains $command) { return ConvertTo-FakeResponseJson (New-FakeFailure 'transaction_failed' $null "injected fault for $command") }
    # Client-side geometry rule, emulated only for the in-process run; behind the pipe the real CLI refuses first.
    if ($command -eq 'wall.create' -and $a.curve.kind -eq 'arc' -and $a.curve.PSObject.Properties['through']) {
        $s = $a.curve.start; $e = $a.curve.end; $t = $a.curve.through
        if ([Math]::Abs(($e[0] - $s[0]) * ($t[1] - $s[1]) - ($e[1] - $s[1]) * ($t[0] - $s[0])) -lt 1e-6) { return ConvertTo-FakeResponseJson (New-FakeFailure 'arc_collinear' 'args.curve' 'the three points are collinear') }
    }
    $created = @()
    $modified = @()
    $deleted = @()
    $readback = switch ($command) {
        'query.levels' { [ordered]@{ levels = (Get-FakeLevels $State) } }
        'query.wall_types' { [ordered]@{ wall_types = (Get-FakeCached $State 'wall_types' {
            [ordered]@{ name = 'Curtain Wall'; unique_id = (New-FakeUniqueId $State); kind = 'Curtain'; width_mm = $null }
            [ordered]@{ name = 'Exterior - Brick'; unique_id = (New-FakeUniqueId $State); kind = 'Stacked'; width_mm = $null }
            [ordered]@{ name = 'Generic - 200mm'; unique_id = (New-FakeUniqueId $State); kind = 'Basic'; width_mm = 200 } }) } }
        'query.floor_types' { [ordered]@{ floor_types = (Get-FakeCached $State 'floor_types' { [ordered]@{ name = 'Generic 150mm'; unique_id = (New-FakeUniqueId $State); width_mm = 150 } }) } }
        'query.family_symbols' {
            $category = [string]$a.category
            [ordered]@{ category = $category; family_symbols = (Get-FakeCached $State "symbols:$category" {
                [ordered]@{ family = 'A Face Based'; type = 'Default'; reference = 'A Face Based: Default'; unique_id = (New-FakeUniqueId $State); active = $true; wall_hosted = $false }
                [ordered]@{ family = "M_$category"; type = 'Standard'; reference = "M_${category}: Standard"; unique_id = (New-FakeUniqueId $State); active = $false; wall_hosted = $true } }) }
        }
        'wall.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $line = $a.curve.kind -eq 'line'
            $curve = if ($line) { [ordered]@{ kind = 'line'; start = $a.curve.start; end = $a.curve.end; length_mm = 1 } }
            else { [ordered]@{ kind = 'arc'; start = @(0, 0); end = @(0, 0); length_mm = 1; radius_mm = $(if ($State.Faults.ContainsKey('WrongRadius')) { 1 } else { $a.curve.radius_mm }); center = $a.curve.center } }
            Add-FakeElement $State $uid 'wall' @{ Kind = 'Basic'; Curve = [string]$a.curve.kind; Start = $(if ($line) { @($a.curve.start) } else { $null }); End = $(if ($line) { @($a.curve.end) } else { $null }); Level = [string]$a.level; Type = [string]$a.type; HeightMm = [double]$a.height_mm }
            [ordered]@{ wall = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; type_unique_id = $a.type; curve = $curve; height_mm = $a.height_mm; base_offset_mm = 0; structural = $false } }
        }
        'opening.place' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $sill = if ($a.PSObject.Properties['sill_mm']) { $a.sill_mm } else { 0 }
            $hostLevel = if ($State.Elements.Contains([string]$a.host_wall_uid)) { $State.Elements[[string]$a.host_wall_uid]['Level'] } else { $null }
            $door = $a.category -eq 'door'
            Add-FakeElement $State $uid ([string]$a.category) @{ Host = [string]$a.host_wall_uid; Level = $hostLevel; Along = [double]$a.position_along_mm; Sill = [double]$sill; WidthMm = $(if ($door) { 900 } else { 1200 }); HeightMm = $(if ($door) { 2100 } else { 1500 }) }
            [ordered]@{ opening = [ordered]@{ unique_id = $uid; category = $a.category; host_wall_unique_id = $a.host_wall_uid; symbol_unique_id = $a.symbol; point = @(0, 0); sill_mm = $sill } }
        }
        'floor.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'floor' @{ Level = [string]$a.level }
            [ordered]@{ floor = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; type_unique_id = $a.type; area_mm2 = 24000000 } }
        }
        'room.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'room' @{ Level = [string]$a.level }
            [ordered]@{ room = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; name = $a.name; number = '1'; area_mm2 = 23000000 } }
        }
        # --- catalogue v2 ---
        'query.roof_types' { [ordered]@{ roof_types = (Get-FakeCached $State 'roof_types' { [ordered]@{ name = 'Generic - 400mm'; unique_id = (New-FakeUniqueId $State); width_mm = 400 } }) } }
        'roof.create_footprint' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $boundary = @($a.boundary)
            $slope = Get-FakeArg $a 'slope_deg'
            if ($null -ne $slope -and $State.Faults.ContainsKey('RoofSlopeOff')) { $slope = [double]$slope + 5 }
            $offset = Get-FakeArg $a 'base_offset_mm'
            if ($null -eq $offset) { $offset = 0 }
            Add-FakeElement $State $uid 'roof' @{ Level = [string]$a.level }
            $edges = @(foreach ($vertex in $boundary) { [ordered]@{ defines_slope = ($null -ne $slope); slope_deg = $slope } })
            [ordered]@{ roof = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; type_unique_id = $a.type; base_offset_mm = $offset; edges = $edges; slope_parameter_deg = $slope; loops = 1; footprint = $boundary; area_mm2 = (Get-FakeArea $boundary) } }
        }
        'stair.create' {
            $uid = New-FakeUniqueId $State; $railing = New-FakeUniqueId $State; $created = @($uid, $railing)
            Add-FakeElement $State $uid 'stair' @{ Level = [string]$a.base_level }
            Add-FakeElement $State $railing 'railing' @{ Host = $uid }
            [ordered]@{ stairs = [ordered]@{ unique_id = $uid; base_level_unique_id = $a.base_level; top_level_unique_id = $a.top_level; run_widths_mm = @($a.width_mm); risers = 23; desired_risers = 23; run = [ordered]@{ start = $a.run.start; end = $a.run.end }; railing_unique_ids = @($railing) } }
        }
        'curtain_wall.create' {
            $uid = New-FakeUniqueId $State
            $start = @($a.curve.start); $end = @($a.curve.end)
            $length = [Math]::Sqrt(([double]$end[0] - [double]$start[0]) * ([double]$end[0] - [double]$start[0]) + ([double]$end[1] - [double]$start[1]) * ([double]$end[1] - [double]$start[1]))
            $grid = Get-FakeArg $a 'grid'
            $vertical = 0; $horizontal = 0
            $verticalSpacing = Get-FakeArg $grid 'vertical_spacing_mm'
            $horizontalSpacing = Get-FakeArg $grid 'horizontal_spacing_mm'
            if ($null -ne $verticalSpacing) { $vertical = [int][Math]::Floor(($length - 1.2) / [double]$verticalSpacing) }
            if ($null -ne $horizontalSpacing) { $horizontal = [int][Math]::Floor(([double]$a.height_mm - 1.2) / [double]$horizontalSpacing) }
            $lines = @(for ($i = 0; $i -lt $vertical + $horizontal; $i++) { New-FakeUniqueId $State })
            $created = @($uid) + $lines
            Add-FakeElement $State $uid 'wall' @{ Kind = 'Curtain'; Curve = 'line'; Start = $start; End = $end; Level = [string]$a.level; Type = [string]$a.type; HeightMm = [double]$a.height_mm }
            [ordered]@{ wall = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; type_unique_id = $a.type; curve = [ordered]@{ kind = 'line'; start = $start; end = $end; length_mm = $length }; height_mm = $a.height_mm; base_offset_mm = 0; structural = $false }
                curtain_grid = [ordered]@{ vertical_lines = $vertical; horizontal_lines = $horizontal; added_grid_line_unique_ids = $lines; panels = ($vertical + 1) * ($horizontal + 1); mullions = 0 } }
        }
        'floor.create_opening' {
            $hostUid = [string]$a.host_floor_uid
            if (!$State.Elements.Contains($hostUid)) { return ConvertTo-FakeResponseJson (New-FakeMissing 'args.host_floor_uid' $hostUid) }
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'floor_opening' @{ Host = $hostUid }
            $profile = @($a.profile)
            [ordered]@{ floor_opening = [ordered]@{ unique_id = $uid; host_floor_unique_id = $hostUid; rect_boundary = $false; profile = $profile; area_mm2 = (Get-FakeArea $profile) } }
        }
        # --- views ---
        'query.view_family_types' {
            $names = @{ floor_plan = 'FloorPlan'; ceiling_plan = 'CeilingPlan'; section = 'Section'; three_d = 'ThreeDimensional' }
            $rows = Get-FakeCached $State 'view_family_types' { foreach ($family in 'FloorPlan', 'CeilingPlan', 'Section', 'ThreeDimensional') { [ordered]@{ name = "Fake $family"; unique_id = (New-FakeUniqueId $State); view_family = $family; is_default = $true } } }
            $family = Get-FakeArg $a 'family'
            $wanted = if ($null -ne $family) { $names[[string]$family] } else { $null }
            [ordered]@{ view_family_types = @($rows | Where-Object { $null -eq $wanted -or $_.view_family -eq $wanted }) }
        }
        'query.view_templates' { [ordered]@{ view_templates = @() } }
        { $_ -in 'view.create_plan', 'view.create_section', 'view.create_3d' } {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $name = Get-FakeArg $a 'name'
            if (!$name) { $name = "Fake view $uid" }
            $viewType = switch ($command) { 'view.create_plan' { if ($a.kind -eq 'ceiling_plan') { 'CeilingPlan' } else { 'FloorPlan' } } 'view.create_section' { 'Section' } default { 'ThreeD' } }
            Add-FakeElement $State $uid 'view' @{ ViewType = $viewType }
            $view = [ordered]@{ unique_id = $uid; name = $name; view_type = $viewType; type_unique_id = (New-FakeUniqueId $State); scale = 100; view_template_unique_id = $null; level_unique_id = (Get-FakeArg $a 'level') }
            if ($command -eq 'view.create_section') {
                $s = @($a.line.start); $e = @($a.line.end)
                $dx = [double]$e[0] - [double]$s[0]; $dy = [double]$e[1] - [double]$s[1]; $length = [Math]::Sqrt($dx * $dx + $dy * $dy)
                $ux = $dx / $length; $uy = $dy / $length
                $view['section'] = [ordered]@{ right_direction = @($ux, $uy, 0); up_direction = @(0, 0, 1); view_direction = @($uy, (0 - $ux), 0); origin = @($s[0], $s[1], $a.bottom_elevation_mm); crop_min = @(0, $a.bottom_elevation_mm, 0); crop_max = @($length, $a.top_elevation_mm, $a.depth_mm); far_clip_mm = $a.depth_mm; crop_box_active = $true }
            }
            if ($command -eq 'view.create_3d') {
                $box = Get-FakeArg $a 'section_box'
                $sectionBox = $null
                if ($null -ne $box) { $sectionBox = [ordered]@{ min = @($box.min); max = @($box.max) } }
                $view['three_d'] = [ordered]@{ perspective = $false; section_box_active = ($null -ne $box); section_box = $sectionBox; forward_direction = @(-0.5774, 0.5774, -0.5774); up_direction = @(-0.4082, 0.4082, 0.8165) }
            }
            [ordered]@{ view = $view }
        }
        # --- snapshot and element queries ---
        'query.snapshot' { Get-FakeSnapshot $State }
        'query.elements' {
            $category = Get-FakeArg $a 'category'
            $level = Get-FakeArg $a 'level'
            $names = @(Get-FakeArg $a 'parameters')
            if (!$names.Count) { $names = @('ALL_MODEL_MARK', 'ALL_MODEL_INSTANCE_COMMENTS') }
            $limit = Get-FakeArg $a 'limit'
            if ($null -eq $limit) { $limit = 1000 }
            $builtIn = if ($null -eq $category) { $null } elseif ([string]$category -like 'OST_*') { [string]$category } else { $script:FakeBuiltInCategories[[string]$category] }
            $matched = @(@($State.Elements.Values) | Where-Object { ($null -eq $category -or $_.Category -eq $category) -and ($null -eq $level -or $_['Level'] -eq $level) } | Sort-Object { $_.Id })
            $rows = @($matched | Select-Object -First ([int]$limit) | ForEach-Object { New-FakeQueryRow $State $_ $names })
            $truncated = $matched.Count -gt $limit
            [ordered]@{ category = $category; built_in_category = $builtIn; level = (Get-FakeLevelRef $State $level); limit = $limit; matched = $matched.Count; returned = $rows.Count; truncated = $truncated; truncated_reason = $(if ($truncated) { 'limit' } else { $null }); elements = $rows }
        }
        'query.inspect' {
            $uid = [string]$a.unique_id
            if (!$State.Elements.Contains($uid)) { return ConvertTo-FakeResponseJson (New-FakeMissing 'args.unique_id' $uid) }
            $element = $State.Elements[$uid]
            $parameters = @(Get-FakeParameters $element)
            $row = New-FakeQueryRow $State $element @()
            $row.Remove('parameters'); $row.Remove('missing_parameters')
            [ordered]@{ element = $row; parameter_count = $parameters.Count; returned = $parameters.Count; truncated = $false; truncated_reason = $null; parameters = $parameters }
        }
        # --- structure ---
        'query.grids' { [ordered]@{ grids = @(@($State.Elements.Values) | Where-Object { $_.Category -eq 'grid' } | ForEach-Object { [ordered]@{ name = $_.Name; unique_id = $_.UniqueId; curved = $false; start = $_.Start; end = $_.End } }) } }
        'query.structural_types' {
            $category = [string]$a.category
            [ordered]@{ category = $category; structural_types = (Get-FakeCached $State "structural:$category" {
                $family = if ($category -eq 'wall_foundation') { $null } else { "Fake $category" }
                [ordered]@{ family = $family; type = 'Standard'; reference = $(if ($family) { "${family}: Standard" } else { 'Standard' }); unique_id = (New-FakeUniqueId $State); active = $true; placement_type = $null } }) }
        }
        'grid.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $name = Get-FakeArg $a 'name'
            if (!$name) { $name = 'G' + $State.ElementNumber }
            Add-FakeElement $State $uid 'grid' @{ Start = @($a.line.start); End = @($a.line.end); Name = $name }
            [ordered]@{ grid = [ordered]@{ unique_id = $uid; name = $name; curved = $false; start = @($a.line.start); end = @($a.line.end) } }
        }
        'structural_column.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'structural_column' @{ Level = [string]$a.base_level }
            $baseOffset = Get-FakeArg $a 'base_offset_mm'; if ($null -eq $baseOffset) { $baseOffset = 0 }
            $topOffset = Get-FakeArg $a 'top_offset_mm'; if ($null -eq $topOffset) { $topOffset = 0 }
            [ordered]@{ structural_column = [ordered]@{ unique_id = $uid; type_unique_id = $a.type; structural_type = 'Column'; slanted = $false; point = @($a.point); base_level_unique_id = $a.base_level; top_level_unique_id = $a.top_level; base_offset_mm = $baseOffset; top_offset_mm = $topOffset } }
        }
        'structural_beam.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'structural_framing' @{ Level = [string]$a.level }
            [ordered]@{ structural_beam = [ordered]@{ unique_id = $uid; type_unique_id = $a.type; structural_type = 'Beam'; level_unique_id = $a.level; start = @($a.line.start); end = @($a.line.end); start_level_offset_mm = 0; end_level_offset_mm = 0 } }
        }
        'foundation.create_isolated' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'structural_foundation' @{ Level = [string]$a.level }
            [ordered]@{ foundation = [ordered]@{ unique_id = $uid; type_unique_id = $a.type; structural_type = 'Footing'; level_unique_id = $a.level; point = @($a.point); z_mm = 0 } }
        }
        'foundation.create_wall' {
            $hostUid = [string]$a.host_wall_uid
            if (!$State.Elements.Contains($hostUid)) { return ConvertTo-FakeResponseJson (New-FakeMissing 'args.host_wall_uid' $hostUid) }
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'wall_foundation' @{ Host = $hostUid }
            [ordered]@{ wall_foundation = [ordered]@{ unique_id = $uid; type_unique_id = $a.type; host_wall_unique_id = $hostUid } }
        }
        # --- documentation ---
        'query.title_blocks' { [ordered]@{ title_blocks = (Get-FakeCached $State 'title_blocks' { [ordered]@{ family = 'M_A1 metric'; type = 'A1'; reference = 'M_A1 metric: A1'; unique_id = (New-FakeUniqueId $State); active = $false } }) } }
        'dimension.create_linear' {
            foreach ($member in 'view_uid', 'first_uid', 'second_uid') { if (!$State.Elements.Contains([string]$a.$member)) { return ConvertTo-FakeResponseJson (New-FakeMissing "args.$member" ([string]$a.$member)) } }
            $first = $State.Elements[[string]$a.first_uid]; $second = $State.Elements[[string]$a.second_uid]
            $value = Get-FakeLineDistance $first.Start $second.Start $second.End
            $face = Get-FakeArg $a 'wall_face'
            if ($first.Category -eq 'wall' -and $second.Category -eq 'wall') { if ($face -eq 'interior') { $value -= 200 } else { $value += 200 } }
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'dimension' @{ References = @([string]$a.first_uid, [string]$a.second_uid) }
            [ordered]@{ dimension = [ordered]@{ unique_id = $uid; view_unique_id = $a.view_uid; reference_unique_ids = @($a.first_uid, $a.second_uid); value_mm = $value; direction = @(1, 0) } }
        }
        'tag.create' {
            foreach ($member in 'view_uid', 'element_uid') { if (!$State.Elements.Contains([string]$a.$member)) { return ConvertTo-FakeResponseJson (New-FakeMissing "args.$member" ([string]$a.$member)) } }
            $head = @(Get-FakeArg $a 'head')
            if (!$head.Count) { $head = @(0, 0) }
            $orientation = Get-FakeArg $a 'orientation'
            if (!$orientation) { $orientation = 'horizontal' }
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'tag' @{ References = @([string]$a.element_uid) }
            [ordered]@{ tag = [ordered]@{ unique_id = $uid; view_unique_id = $a.view_uid; tagged_unique_ids = @($a.element_uid); has_leader = [bool](Get-FakeArg $a 'leader'); orientation = $orientation; head = $head; orphaned = $false } }
        }
        'schedule.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $name = Get-FakeArg $a 'name'
            if (!$name) { $name = 'Fake Schedule' }
            $sortFields = @()
            $sort = Get-FakeArg $a 'sort_by'
            if ($sort) { $sortFields = @($sort) }
            Add-FakeElement $State $uid 'schedule' @{ Name = $name }
            [ordered]@{ schedule = [ordered]@{ unique_id = $uid; name = $name; built_in_category = $script:FakeBuiltInCategories[[string]$a.category]; fields = @($a.fields); sort_fields = $sortFields } }
        }
        'sheet.create' {
            $number = [string]$a.number
            if (@(@($State.Elements.Values) | Where-Object { $_.Category -eq 'sheet' -and $_['Number'] -eq $number }).Count) { return ConvertTo-FakeResponseJson (New-FakeFailure 'args_invalid' 'args.number' "a sheet numbered '$number' (ignoring case) already exists") }
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'sheet' @{ Number = $number }
            $titleBlocks = @()
            $titleBlock = Get-FakeArg $a 'title_block'
            if ($titleBlock) { $titleBlocks = @($titleBlock) }
            [ordered]@{ sheet = [ordered]@{ unique_id = $uid; number = $number; name = $a.name; title_block_type_unique_ids = $titleBlocks } }
        }
        'sheet.place_view' {
            foreach ($member in 'sheet_uid', 'view_uid') { if (!$State.Elements.Contains([string]$a.$member)) { return ConvertTo-FakeResponseJson (New-FakeMissing "args.$member" ([string]$a.$member)) } }
            $viewUid = [string]$a.view_uid
            $schedule = $State.Elements[$viewUid].Category -eq 'schedule'
            if (!$schedule -and $State.Placed.ContainsKey($viewUid)) { return ConvertTo-FakeResponseJson (New-FakeFailure 'args_invalid' 'args.view_uid' ('the view is already placed on sheet ' + $State.Placed[$viewUid])) }
            if (!$schedule) { $State.Placed[$viewUid] = $State.Elements[[string]$a.sheet_uid]['Number'] }
            $uid = New-FakeUniqueId $State; $created = @($uid)
            Add-FakeElement $State $uid 'placement' @{ References = @($viewUid, [string]$a.sheet_uid) }
            [ordered]@{ placement = [ordered]@{ unique_id = $uid; kind = $(if ($schedule) { 'schedule' } else { 'viewport' }); sheet_unique_id = $a.sheet_uid; view_unique_id = $viewUid; center = @($a.center) } }
        }
        # --- modify and delete ---
        'element.set_parameter' {
            $uid = [string]$a.element_uid
            if (!$State.Elements.Contains($uid)) { return ConvertTo-FakeResponseJson (New-FakeMissing 'args.element_uid' $uid) }
            $element = $State.Elements[$uid]
            $builtIn = Get-FakeArg $a.parameter 'built_in'
            $parameterName = Get-FakeArg $a.parameter 'name'
            if ($builtIn -eq 'CURVE_ELEM_LENGTH') { return ConvertTo-FakeResponseJson (New-FakeFailure 'args_invalid' 'args.parameter' 'parameter Length is read-only') }
            if ($builtIn -eq 'WALL_USER_HEIGHT_PARAM' -and $element.Category -eq 'wall') { $kind = 'length'; $member = 'HeightMm'; $label = 'Unconnected Height'; $storage = 'double' }
            elseif ($parameterName -eq 'Comments' -or $builtIn -eq 'ALL_MODEL_INSTANCE_COMMENTS') { $kind = 'text'; $member = 'Comments'; $label = 'Comments'; $storage = 'string'; $builtIn = 'ALL_MODEL_INSTANCE_COMMENTS' }
            else { return ConvertTo-FakeResponseJson (New-FakeFailure 'reference_not_found' 'args.parameter' 'the element has no such parameter') }
            if ([string]$a.value.kind -ne $kind) { return ConvertTo-FakeResponseJson (New-FakeFailure 'args_invalid' 'args.value.kind' "parameter $label takes a $kind value") }
            $before = $element[$member]
            $after = if ($kind -eq 'length') { [double]$a.value.mm } else { [string]$a.value.text }
            $changed = [string]$before -ne [string]$after
            if (!$dry) { $element[$member] = $after; if ($changed) { $modified = @($uid) } }
            $beforeValue = $null
            if ($null -ne $before) { $beforeValue = if ($kind -eq 'length') { [ordered]@{ kind = 'length'; mm = $before } } else { [ordered]@{ kind = 'text'; text = $before } } }
            $afterValue = if ($kind -eq 'length') { [ordered]@{ kind = 'length'; mm = $after } } else { [ordered]@{ kind = 'text'; text = $after } }
            [ordered]@{ set_parameter = [ordered]@{ dry_run = $dry; element_unique_id = $uid; parameter = [ordered]@{ name = $label; id = -1; built_in = $builtIn; storage = $storage; spec = $null; shared = $false }; kind = $kind; before = $beforeValue; after = $afterValue; changed = $changed; also_modified = @(); also_modified_count = 0; also_modified_listed = $true } }
        }
        'element.delete' {
            $targets = @($a.element_uids | ForEach-Object { [string]$_ })
            foreach ($target in $targets) { if (!$State.Elements.Contains($target)) { return ConvertTo-FakeResponseJson (New-FakeMissing 'args.element_uids' $target) } }
            $dependents = Get-FakeDependents $State $targets
            $confirm = @(Get-FakeArg $a 'confirm_dependents' | ForEach-Object { [string]$_ })
            $same = (@($dependents | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object) -join "`n") -eq (@($confirm | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object) -join "`n")
            if (!$dry -and !$same) { return ConvertTo-FakeResponseJson (New-FakeFailure 'args_invalid' 'args.confirm_dependents' "Revit would also delete $($dependents.Count) unconfirmed element(s); run a dry run and confirm exactly its dependents") }
            if (!$dry) {
                foreach ($uid in @($targets + $dependents)) { $State.Elements.Remove($uid) }
                $deleted = @($targets + $dependents)
            }
            [ordered]@{ delete = [ordered]@{ dry_run = $dry; targets = $targets; dependents = $dependents; dependents_count = $dependents.Count; dependents_sha256 = (Get-FakeSha256 (@($dependents | Sort-Object) -join "`n")); dependents_listed = $true; deleted_count = $targets.Count + $dependents.Count; confirmed = $same } }
        }
        default { $null }
    }
    if ($null -eq $readback) { return ConvertTo-FakeResponseJson (New-FakeFailure 'command_unknown' 'command' $command) }
    $response = [ordered]@{ status = 'succeeded'; code = 'ok'; field = $null; detail = $null; created = $created; modified = $modified; deleted = $deleted; readback = $readback
        receipt = [ordered]@{ operation_id = $operationId; command = $command; document_unique_id = [string]$envelope.document.unique_id; request_sha256 = $sha; replayed = $false }; warnings = @() }
    if ($writes -and !$dry) { $State.Receipts[$operationId] = @{ Sha = $sha; Response = $response }; $State.SkippedAddIn++ }
    return ConvertTo-FakeResponseJson $response
}

# The journal.read wire line ({"DocumentPath","Operation","Journal"}), answered like JournalRecorder.Read: add-in writes
# are counted in skipped_add_in (never entries), and the diff compares the snapshot sources with the last non-peek read.
function Invoke-FakeRevitWire($State, [string]$Line) {
    $request = $Line | ConvertFrom-Json
    $State.Received.Add('journal.read')
    if ([string]$request.Operation -ne 'journal.read') { return '{"status":"failed","code":"unsupported_capability"}' }
    $options = Get-FakeArg $request 'Journal'
    $peek = [bool](Get-FakeArg $options 'peek')
    $current = [ordered]@{}
    foreach ($source in (Get-FakeSnapshot $State).sources) { $current[[string]$source.unique_id] = [string]$source.category }
    $baselineExisted = $null -ne $State.JournalBaseline
    $base = [ordered]@{}
    if ($baselineExisted) { $base = $State.JournalBaseline }
    $added = @(foreach ($uid in @($current.Keys)) { if (!$base.Contains($uid)) { [ordered]@{ unique_id = $uid; category = $current[$uid] } } })
    $removed = @(foreach ($uid in @($base.Keys)) { if (!$current.Contains($uid)) { [ordered]@{ unique_id = $uid; category = $base[$uid] } } })
    if (!$peek) { $State.JournalBaseline = $current }
    $report = [ordered]@{ journal_version = 1; document_unique_id = $State.DocumentUid; document_path = [string]$request.DocumentPath; baseline_sequence = 0; current_sequence = 0; after_sequence = 0; baseline_existed = $baselineExisted; peek = $peek
        journal_complete = $true; failures_since_baseline = 0; has_more = $false; next_after_sequence = 0; entries = @()
        diff = [ordered]@{ diff_version = 1; document_unique_id = $State.DocumentUid; added = $added; removed = $removed; changed = @() } }
    return ConvertTo-FakeResponseJson ([ordered]@{ status = 'succeeded'; verification = 'unverified'; attached = $true; dropped = 0; skipped_add_in = $State.SkippedAddIn; failures = 0; unidentified = 0; report = $report })
}
