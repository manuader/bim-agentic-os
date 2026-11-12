# Test double for the C03 add-in executor, used only by test-catalogue-session.ps1 (in process) and
# fake-revit-command-pipe.ps1 (behind a real named pipe). It answers catalogue envelopes (v1 and every command the P1-06
# one-run owner sequence sends: v2, views, query.snapshot, query.elements/inspect, structure, documentation,
# element.set_parameter/delete) with the response shape of RevitCommandResponse.ToJson and the readback member names
# of the add-in executor, keeps receipts by operation_id (replay on the same request text, conflict on a different
# one; dry runs store none), keeps a small element table so references, dependents, snapshots and the journal.read
# wire answer (Invoke-FakeRevitWire) are consistent, and records every operation_id it receives. Faults (hashtable):
# ReplayNotFlagged, ReplayNewElement, WrongRadius (v1); FailCommands = @(names) answer transaction_failed;
# RoofSlopeOff reports a different roof slope; SnapshotUnstable makes every snapshot differ; SingleLevel leaves the
# model with one level; NoStructuralTypes = @(categories) lists none of those structural types; P1-11:
# DirectShapeBoxOff widens every DirectShape box by 5 mm, RoofExtrusionMirrored extrudes roofs to the plane's right,
# RotateWrongWay turns clockwise, SaveCopyHashOff reports a wrong copy sha256, SaveCopyFailsAfterSave re-points the
# document to the copy and then answers transaction_failed; P1-14: SaveAsHashOff reports a wrong sha256 for the new
# file, SaveAsFailsAfterSave re-points the document to the new file and then answers transaction_failed,
# SaveAsWrittenElsewhere re-points it and answers the post-save "written elsewhere ... do not continue" failure; with BehindCli a client-side case answers fake_client_side_leak. It also answers direct_shape.create,
# roof.create_extrusion, element.move/copy/rotate/mirror and document.save_copy (which writes a small file at the
# target, re-points the state's DocumentPath to it and keeps no receipt), document.save_as (the same: a new file at the
# target, the open document re-pointed to it, the previous path in the readback, no receipt, so a repeat is refused
# target_exists), and emulates, in process only, the client-side refusals the real CLI gives those commands before
# the pipe (save_as: RevitSaveAsGuard.Check in its order against .bim/native-runs and the entries of
# .bim/revit-enabled-roots.txt; the Windows drive-path syntax rule is left to the real CLI). With DocumentPath set on the state, an
# envelope or journal.read for another path answers document_conflict. P2-18, test-only: New-FakeRevitState -ExternalEdits
# (operation ids; fake-revit-command-pipe.ps1 -ExternalEdits "id1,id2") makes those element.move / wall.create /
# element.delete requests external edits, as if made in Revit by the user: they still apply and answer as before, but are
# recorded as journal entries (origin external, not counted in skipped_add_in) and journal.read then answers like
# JournalRecorder.Read (RevitJournalQuery.RawRead): entries after after_sequence (default the baseline sequence), peek,
# max_entries paging, current_sequence, baseline_sequence; options are read as MirrorJournalReadOptions.Parse reads them
# (exact-case names, an object or null, no duplicates, the same journal_options_invalid codes). Fidelity is semantic:
# replies parse to the production members and values, not the same escaping bytes (ConvertTo-Json, not Utf8JsonWriter).
# The baseline is one per fake state (global), not per document as in MirrorJournalTracker. P2-20: with ExternalEdits the
# baseline is the last non-peek read's snapshot and report.diff is MirrorSnapshotDiffer.Compare(baseline or none, current
# snapshot), member for member (Get-FakeSnapshotDiff). An -ExternalEdits list with no ids is off. Without it every reply
# is the pre-P2-18 one (diff included), byte for byte.
# It proves nothing about Revit.
Set-StrictMode -Version Latest

$script:FakeBuiltInCategories = @{ wall = 'OST_Walls'; door = 'OST_Doors'; window = 'OST_Windows'; floor = 'OST_Floors'; room = 'OST_Rooms'; roof = 'OST_Roofs'; stair = 'OST_Stairs'; grid = 'OST_Grids'; level = 'OST_Levels' }
$script:FakeCategoryNames = @{ wall = 'Walls'; door = 'Doors'; window = 'Windows'; floor = 'Floors'; room = 'Rooms'; roof = 'Roofs'; stair = 'Stairs'; grid = 'Grids' }

function New-FakeRevitState([string]$DocumentUid, [hashtable]$Faults = @{}, [string[]]$ExternalEdits = $null) {
    # Trimmed, blanks dropped; no id left means the option is off (as if absent).
    $external = $null
    $ids = [string[]]@(@($ExternalEdits) | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    if ($ids.Count -gt 0) { $external = [Collections.Generic.HashSet[string]]::new($ids, [StringComparer]::Ordinal) }
    @{ DocumentUid = $DocumentUid; Receipts = @{}; Received = [Collections.Generic.List[string]]::new(); Next = 0; Faults = $Faults; Episode = '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0'
        Elements = [ordered]@{}; Cache = @{}; Placed = @{}; ElementNumber = 100000; SkippedAddIn = 0; JournalBaseline = $null; Snapshots = 0; DocumentPath = $null
        Copies = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        ExternalEdits = $external; Journal = [Collections.Generic.List[object]]::new(); JournalSequence = 0; JournalBaselineSequence = 0 }
}

# P2-18: the Revit transaction name a user edit of that kind commits under (what DocumentChanged reports).
$script:FakeExternalTransactions = @{ 'element.move' = 'Move'; 'wall.create' = 'Wall'; 'element.delete' = 'Delete' }

# Distinct ordinal-sorted strings, as MirrorJournal.Record keeps an entry's UniqueId lists.
function Get-FakeOrdinalSet($Values) {
    $set = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in @($Values)) { if (![string]::IsNullOrWhiteSpace([string]$value)) { $null = $set.Add([string]$value) } }
    return , ([string[]]@($set))
}

# One external journal entry (MirrorJournalEntry.Write member order), sequence and clock deterministic: the n-th entry is
# recorded at 2026-01-01T00:00:00Z plus n seconds. Deleted elements carry the element id Revit's UniqueId convention puts
# in the hex suffix (MirrorDrift.NativeElementId), as the recorder can no longer resolve their UniqueIds.
function Add-FakeJournalEntry($State, [string]$DocumentPath, [string]$Transaction, $Added, $Modified, $Deleted) {
    $State.JournalSequence++
    $sequence = [long]$State.JournalSequence
    $added = Get-FakeOrdinalSet $Added
    $modified = Get-FakeOrdinalSet $Modified
    $ids = [Collections.Generic.SortedSet[long]]::new()
    foreach ($uid in @($Deleted)) { if ($null -ne $uid) { $text = [string]$uid; $null = $ids.Add([Convert]::ToInt64($text.Substring($text.LastIndexOf('-') + 1), 16)) } }
    $deletedIds = [long[]]@($ids)
    $at = [DateTime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc).AddSeconds($sequence).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $State.Journal.Add([ordered]@{ sequence = $sequence; recorded_at = $at; document_unique_id = $State.DocumentUid; document_path = $DocumentPath; operation = 'TransactionCommitted'; origin = 'external'
        transaction_names = @($Transaction); added = $added; modified = $modified; deleted_element_ids = $deletedIds
        added_total = [int]$added.Count; modified_total = [int]$modified.Count; deleted_total = [int]$deletedIds.Count; truncated = $false })
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
        if (!$State.Faults.ContainsKey('SingleLevel')) { [ordered]@{ name = 'Level 2'; unique_id = (New-FakeUniqueId $State); elevation_mm = 4000; project_elevation_mm = 4000 } } })
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

# --- P1-11 helpers: client-side refusals (what the real CLI refuses before the pipe, emulated only for the in-process
# run), element locations for move/copy/rotate/mirror, the barrel roof box and the DirectShape box. ---
function Get-FakeLevelElevation($State, $LevelUid) {
    foreach ($level in (Get-FakeLevels $State)) { if ($level.unique_id -eq [string]$LevelUid) { return [double]$level.project_elevation_mm } }
    return 0.0
}

# Do segments P1-P2 and Q1-Q2 (plan [x, y]) cross or touch?
function Test-FakeSegmentsCross($P1, $P2, $Q1, $Q2) {
    function Side($A, $B, $C) { ([double]$B[0] - [double]$A[0]) * ([double]$C[1] - [double]$A[1]) - ([double]$B[1] - [double]$A[1]) * ([double]$C[0] - [double]$A[0]) }
    $d1 = Side $Q1 $Q2 $P1; $d2 = Side $Q1 $Q2 $P2; $d3 = Side $P1 $P2 $Q1; $d4 = Side $P1 $P2 $Q2
    return (($d1 -gt 0 -and $d2 -lt 0) -or ($d1 -lt 0 -and $d2 -gt 0)) -and (($d3 -gt 0 -and $d4 -lt 0) -or ($d3 -lt 0 -and $d4 -gt 0))
}

# The client-side refusal the CLI would answer for this envelope, or $null (only the cases the owner sequence sends).
function Get-FakeClientRefusal($State, [string]$Command, $Arguments, [string]$DocumentPath) {
    switch ($Command) {
        { $_ -in 'element.move', 'element.copy' } {
            $v = @($Arguments.vector_mm | ForEach-Object { [double]$_ })
            if (@($v | Where-Object { $_ -ne 0 }).Count -eq 0) { return New-FakeFailure 'length_invalid' 'args.vector_mm' 'zero vector: nothing to move or copy' }
        }
        'element.rotate' {
            $angle = [double]$Arguments.angle_deg
            if ($angle -eq 0) { return New-FakeFailure 'angle_invalid' 'args.angle_deg' 'zero angle: nothing to rotate' }
            if ([Math]::Abs($angle) -ge 360) { return New-FakeFailure 'angle_invalid' 'args.angle_deg' "a full turn or more ($angle degrees); use an angle under 360 either way" }
        }
        'roof.create_extrusion' {
            $profile = @($Arguments.profile)
            $first = Get-FakeArg $profile[0] 'start'
            $last = Get-FakeArg $profile[$profile.Count - 1] 'end'
            if ($null -ne $first -and $null -ne $last -and [Math]::Sqrt([Math]::Pow([double]$first[0] - [double]$last[0], 2) + [Math]::Pow([double]$first[1] - [double]$last[1], 2)) -lt 1.2) {
                return New-FakeFailure 'args_invalid' 'args.profile' 'the profile is closed (its last segment ends where the first starts); an extrusion roof takes an open profile'
            }
        }
        'direct_shape.create' {
            if ($Arguments.shape.kind -eq 'extrusion') {
                $outline = @($Arguments.shape.outline)
                $n = $outline.Count
                for ($i = 0; $i -lt $n; $i++) {
                    for ($j = $i + 2; $j -lt $n; $j++) {
                        if ($i -eq 0 -and $j -eq $n - 1) { continue }
                        if (Test-FakeSegmentsCross $outline[$i] $outline[($i + 1) % $n] $outline[$j] $outline[($j + 1) % $n]) { return New-FakeFailure 'boundary_self_intersecting' "args.shape.outline[$i]" "edges $i and $j intersect, overlap or come within 1.2 mm" }
                    }
                }
            }
        }
        'document.save_copy' {
            # RevitSaveCopyGuard.Check as the CLI applies it to the test layout <repo>/.bim/native-runs/<run>/BIM-Agentic-Test*.rvt.
            $target = [string]$Arguments.path
            $root = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($DocumentPath)) + [IO.Path]::DirectorySeparatorChar
            if (!$target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return New-FakeFailure 'save_copy_refused' 'args.path' "outside_copies_root: copies are saved only inside the repository's .bim/native-runs folder ($root)" }
            if ([string]::Equals($target, $DocumentPath, [StringComparison]::OrdinalIgnoreCase)) { return New-FakeFailure 'save_copy_refused' 'args.path' 'same_as_source: the copy must be a new file, not the open document''s own path (use document.save)' }
            if ($State.Copies.Contains($target)) { return New-FakeFailure 'save_copy_refused' 'args.path' "target_exists: $target already exists; copies never overwrite, pick a new name" }
        }
        'document.save_as' {
            # RevitSaveAsGuard.Check, in its order, for the test layout <repo>/.bim/native-runs/<run>/BIM-Agentic-Test*.rvt:
            # enabled roots are .bim/native-runs (the copies root) and the entries of .bim/revit-enabled-roots.txt.
            $target = [string]$Arguments.path
            $copies = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($DocumentPath))
            $owner = @(Get-FakeOwnerRoots ([IO.Path]::Combine([IO.Path]::GetDirectoryName($copies), 'revit-enabled-roots.txt')))
            $enabled = @($copies) + $owner
            if (!@($enabled | Where-Object { Test-FakeUnder $target $_ }).Count) { return New-FakeFailure 'save_as_refused' 'args.path' 'outside_enabled_roots: the new file must be inside .bim/native-runs or a folder listed in .bim/revit-enabled-roots.txt' }
            if (!@($enabled | Where-Object { Test-FakeUnder $DocumentPath $_ }).Count) { return New-FakeFailure 'save_as_refused' 'document.path' 'source_not_enabled: only a document inside .bim/native-runs or a folder listed in .bim/revit-enabled-roots.txt is saved as a new file' }
            if ((Test-FakeUnder $DocumentPath $copies) -and !(Test-FakeUnder $target $copies)) { return New-FakeFailure 'save_as_refused' 'args.path' 'outside_copies_root: a test copy under .bim/native-runs is saved as a new file only inside .bim/native-runs; owner-enabled folders take only documents already in an owner-enabled folder' }
            if ([string]::Equals($target, $DocumentPath, [StringComparison]::OrdinalIgnoreCase)) { return New-FakeFailure 'save_as_refused' 'args.path' 'same_as_source: the new file must differ from the open document''s own path (use document.save)' }
            $folder = [IO.Path]::GetDirectoryName($target)
            if (!(Test-Path -LiteralPath $folder -PathType Container)) { return New-FakeFailure 'save_as_refused' 'args.path' "folder_missing: the folder $folder does not exist" }
            if ($State.Copies.Contains($target)) { return New-FakeFailure 'save_as_refused' 'args.path' "target_exists: $target already exists; save as never overwrites, pick a new name" }
        }
    }
    return $null
}

# The entries of an enabled-roots file as the CLI reads them (trimmed, blank lines and '#' comments skipped).
function Get-FakeOwnerRoots([string]$File) {
    if (!(Test-Path -LiteralPath $File -PathType Leaf)) { return @() }
    return @([IO.File]::ReadAllLines($File) | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 -and !$_.StartsWith('#') } | ForEach-Object { $_.TrimEnd('/', '\') })
}

# True when Path is Root or lies inside it (whole segments, case-insensitive like drive paths).
function Test-FakeUnder([string]$Path, [string]$Root) {
    $root0 = $Root.TrimEnd('/', '\')
    return [string]::Equals($Path, $root0, [StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($root0 + '/', [StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($root0 + '\', [StringComparison]::OrdinalIgnoreCase)
}

# The move/copy/rotate/mirror location of a fake element (straight walls: a curve with z 0), or $null.
function Get-FakeLocation($Element) {
    if ($null -eq $Element['Start'] -or $null -eq $Element['End']) { return $null }
    [ordered]@{ kind = 'curve'; start = @([double]$Element['Start'][0], [double]$Element['Start'][1], 0.0); end = @([double]$Element['End'][0], [double]$Element['End'][1], 0.0) }
}

# A copy of a fake element's facts (without its identity) for element.copy / element.mirror.
function Copy-FakeFacts($Element) {
    $facts = @{}
    foreach ($name in @($Element.Keys)) { if ($name -notin 'Id', 'UniqueId', 'Category') { $facts[$name] = $Element[$name] } }
    return $facts
}

# Plan point P mapped by a scriptblock on [x, y].
function Move-FakeEnds($Element, [scriptblock]$Map) {
    $Element['Start'] = @(& $Map @([double]$Element['Start'][0], [double]$Element['Start'][1]))
    $Element['End'] = @(& $Map @([double]$Element['End'][0], [double]$Element['End'][1]))
}

# Plan points of one extrusion-roof profile segment ([u, z]): a line's ends, or 721 points along a three-point arc.
function Get-FakeProfilePoints($Segment) {
    $s = @($Segment.start | ForEach-Object { [double]$_ }); $e = @($Segment.end | ForEach-Object { [double]$_ })
    $through = Get-FakeArg $Segment 'through'
    if ($Segment.kind -ne 'arc' -or $null -eq $through) { return @{ Points = @(, $s) + @(, $e); Mid = @((($s[0] + $e[0]) / 2), (($s[1] + $e[1]) / 2)) } }
    $t = @($through | ForEach-Object { [double]$_ })
    $d = 2 * ($s[0] * ($t[1] - $e[1]) + $t[0] * ($e[1] - $s[1]) + $e[0] * ($s[1] - $t[1]))
    $cx = (($s[0] * $s[0] + $s[1] * $s[1]) * ($t[1] - $e[1]) + ($t[0] * $t[0] + $t[1] * $t[1]) * ($e[1] - $s[1]) + ($e[0] * $e[0] + $e[1] * $e[1]) * ($s[1] - $t[1])) / $d
    $cz = (($s[0] * $s[0] + $s[1] * $s[1]) * ($e[0] - $t[0]) + ($t[0] * $t[0] + $t[1] * $t[1]) * ($s[0] - $e[0]) + ($e[0] * $e[0] + $e[1] * $e[1]) * ($t[0] - $s[0])) / $d
    $radius = [Math]::Sqrt(($s[0] - $cx) * ($s[0] - $cx) + ($s[1] - $cz) * ($s[1] - $cz))
    $turn = 2 * [Math]::PI
    $a0 = [Math]::Atan2($s[1] - $cz, $s[0] - $cx)
    $ccw = (([Math]::Atan2($e[1] - $cz, $e[0] - $cx) - $a0) % $turn + $turn) % $turn
    $toThrough = (([Math]::Atan2($t[1] - $cz, $t[0] - $cx) - $a0) % $turn + $turn) % $turn
    $sweep = if ($toThrough -lt $ccw) { $ccw } else { $ccw - $turn }
    $points = @(for ($k = 0; $k -le 720; $k++) { $theta = $a0 + $sweep * $k / 720; , @(($cx + $radius * [Math]::Cos($theta)), ($cz + $radius * [Math]::Sin($theta))) })
    return @{ Points = $points; Mid = $points[360] }
}

# $CommandText is the envelope exactly as received (the pipe's Command member); returns one response line.
# P1-13 query.elements cursor, issued exactly as core RevitElementQueryCursor so the real CLI accepts it on the next
# request: base64url of {"v":1,"document","filter","after","check"} where filter is the SHA-256 of the canonical filter
# JSON and check the first 16 bytes of SHA-256 over the domain line plus the payload without check. Texts written here
# are plain ASCII only (the fake's categories, level UniqueIds and parameter names); anything else throws.
function ConvertTo-FakeJsonText($Value) {
    if ($null -eq $Value) { return 'null' }
    $text = [string]$Value
    if ($text -notmatch '\A[A-Za-z0-9 _.:-]*\z') { throw "fake cursor: unsupported text $text" }
    return '"' + $text + '"'
}

function Get-FakeFilterDigest($BuiltIn, $Level, [object[]]$Parameters) {
    $names = @(@($Parameters) | Where-Object { $null -ne $_ } | ForEach-Object { ConvertTo-FakeJsonText $_ }) -join ','
    Get-FakeSha256 ('{"built_in_category":' + (ConvertTo-FakeJsonText $BuiltIn) + ',"level":' + (ConvertTo-FakeJsonText $Level) + ',"parameters":[' + $names + ']}')
}

function New-FakeCursor([string]$Document, [string]$Filter, [long]$After) {
    $utf8 = [Text.UTF8Encoding]::new($false)
    $body = '"v":1,"document":' + (ConvertTo-FakeJsonText $Document) + ',"filter":"' + $Filter + '","after":' + $After.ToString([Globalization.CultureInfo]::InvariantCulture)
    $hash = [Security.Cryptography.SHA256]::HashData($utf8.GetBytes("bim-agentic-os/query.elements/cursor`n{" + $body + '}'))
    $check = [Convert]::ToHexString($hash, 0, 16).ToLowerInvariant()
    return [Convert]::ToBase64String($utf8.GetBytes('{' + $body + ',"check":"' + $check + '"}')).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# The element id a cursor resumes after (the CLI has already refused malformed, tampered, stale or foreign cursors).
function Get-FakeCursorAfter([string]$Cursor) {
    $text = $Cursor.Replace('-', '+').Replace('_', '/')
    $text += '=' * ((4 - $text.Length % 4) % 4)
    return [long](([Text.UTF8Encoding]::new($false).GetString([Convert]::FromBase64String($text)) | ConvertFrom-Json).after)
}

function Invoke-FakeRevitCommand($State, [string]$CommandText) {
    $envelope = $CommandText | ConvertFrom-Json
    $operationId = [string]$envelope.operation_id
    $State.Received.Add($operationId)
    $command = [string]$envelope.command
    $a = $envelope.args
    $sha = Get-FakeSha256 $CommandText
    # What the real CLI refuses before the pipe (in process only; behind the pipe the CLI already refused it).
    $refusal = Get-FakeClientRefusal $State $command $a ([string]$envelope.document.path)
    # Behind the pipe (BehindCli, set by fake-revit-command-pipe.ps1) such a request means the CLI let it through: its
    # own code, so the step fails instead of passing on the emulated answer.
    if ($null -ne $refusal -and $State.ContainsKey('BehindCli') -and $State.BehindCli) { return ConvertTo-FakeResponseJson (New-FakeFailure 'fake_client_side_leak' $refusal.field "the CLI forwarded a request it must refuse ($($refusal.code)): $($refusal.detail)") }
    if ($null -ne $refusal) { return ConvertTo-FakeResponseJson $refusal }
    # Admission: the envelope must name the document Revit has open (after document.save_copy, the copy).
    if ($null -ne $State.DocumentPath -and ![string]::Equals([string]$envelope.document.path, $State.DocumentPath, [StringComparison]::OrdinalIgnoreCase)) {
        return ConvertTo-FakeResponseJson (New-FakeFailure 'document_conflict' 'document.path' 'the active document is not the saved document the envelope names')
    }
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
    # P2-18: a request named in ExternalEdits stands for an edit made in Revit (journal entry, not an add-in write).
    $external = $null -ne $State.ExternalEdits -and $State.ExternalEdits.Contains($operationId) -and $writes -and !$dry
    if ($external -and !$script:FakeExternalTransactions.ContainsKey($command)) { return ConvertTo-FakeResponseJson (New-FakeFailure 'fake_external_unsupported' 'command' "only element.move, wall.create and element.delete can be recorded as external edits, not $command") }
    # Client-side geometry rule, emulated only for the in-process run; behind the pipe the real CLI refuses first.
    if ($command -eq 'wall.create' -and $a.curve.kind -eq 'arc' -and $a.curve.PSObject.Properties['through']) {
        $s = $a.curve.start; $e = $a.curve.end; $t = $a.curve.through
        if ([Math]::Abs(($e[0] - $s[0]) * ($t[1] - $s[1]) - ($e[1] - $s[1]) * ($t[0] - $s[0])) -lt 1e-6) { return ConvertTo-FakeResponseJson (New-FakeFailure $(if ($State.ContainsKey('BehindCli') -and $State.BehindCli) { 'fake_client_side_leak' } else { 'arc_collinear' }) 'args.curve' 'the three points are collinear') }
    }
    $created = @()
    $modified = @()
    $deleted = @()
    $warnings = @()
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
            # P4-11: a section's plan marker (its viewer), deleted with the section like Revit's; not in created.
            if ($command -eq 'view.create_section') { Add-FakeElement $State (New-FakeUniqueId $State) 'viewer' @{ References = @($uid) } }
            $view = [ordered]@{ unique_id = $uid; name = $name; view_type = $viewType; type_unique_id = (New-FakeUniqueId $State); scale = 100; view_template_unique_id = $null; level_unique_id = (Get-FakeArg $a 'level') }
            if ($command -eq 'view.create_section') {
                $s = @($a.line.start); $e = @($a.line.end)
                $dx = [double]$e[0] - [double]$s[0]; $dy = [double]$e[1] - [double]$s[1]; $length = [Math]::Sqrt($dx * $dx + $dy * $dy)
                $ux = $dx / $length; $uy = $dy / $length
                $view['section'] = [ordered]@{ right_direction = @($ux, $uy, 0); up_direction = @(0, 0, 1); view_direction = @($uy, (0 - $ux), 0); origin = @($s[0], $s[1], $a.bottom_elevation_mm); crop_min = @($e[0], $e[1], $a.top_elevation_mm); crop_max = @($s[0], $s[1], $a.bottom_elevation_mm); far_clip_mm = $a.depth_mm; crop_box_active = $true }
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
            $requested = @(Get-FakeArg $a 'parameters')
            $names = $requested
            if (!$names.Count) { $names = @('ALL_MODEL_MARK', 'ALL_MODEL_INSTANCE_COMMENTS') }
            $cursor = Get-FakeArg $a 'cursor'
            $after = if ($null -eq $cursor) { $null } else { Get-FakeCursorAfter ([string]$cursor) }
            $limit = Get-FakeArg $a 'limit'
            if ($null -eq $limit) { $limit = 1000 }
            $builtIn = if ($null -eq $category) { $null } elseif ([string]$category -like 'OST_*') { [string]$category } else { $script:FakeBuiltInCategories[[string]$category] }
            $matched = @(@($State.Elements.Values) | Where-Object { ($null -eq $category -or $_.Category -eq $category) -and ($null -eq $level -or $_['Level'] -eq $level) } | Sort-Object { $_.Id })
            $remaining = @($matched | Where-Object { $null -eq $after -or [long]$_.Id -gt $after })
            $paged = @($remaining | Select-Object -First ([int]$limit))
            $rows = @($paged | ForEach-Object { New-FakeQueryRow $State $_ $names })
            $truncated = $remaining.Count -gt $limit
            $next = if ($truncated) { New-FakeCursor ([string]$envelope.document.unique_id) (Get-FakeFilterDigest $builtIn $level $requested) ([long]$paged[-1].Id) } else { $null }
            [ordered]@{ category = $category; built_in_category = $builtIn; level = (Get-FakeLevelRef $State $level); limit = $limit; matched = $matched.Count; returned = $rows.Count; truncated = $truncated; truncated_reason = $(if ($truncated) { 'limit' } else { $null }); after = $after; next_cursor = $next; elements = $rows }
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
            $types = @()
            if (!($State.Faults.ContainsKey('NoStructuralTypes') -and @($State.Faults['NoStructuralTypes']) -contains $category)) {
                $types = Get-FakeCached $State "structural:$category" {
                    $family = if ($category -eq 'wall_foundation') { $null } else { "Fake $category" }
                    [ordered]@{ family = $family; type = 'Standard'; reference = $(if ($family) { "${family}: Standard" } else { 'Standard' }); unique_id = (New-FakeUniqueId $State); active = $true; placement_type = $null } }
            }
            [ordered]@{ category = $category; structural_types = $types }
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
            [ordered]@{ set_parameter = [ordered]@{ dry_run = $dry; element_unique_id = $uid; parameter = [ordered]@{ name = $label; id = -1; built_in = $builtIn; storage = $storage; spec = $null; shared = $false }; kind = $kind; before = $beforeValue; after = $afterValue; changed = $changed; also_modified = @(); also_modified_count = 0; also_modified_listed = $true; deleted_listed = $true; deleted = @(); deleted_count = 0; deletions_confirmed = $false } }
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
        # --- P1-11: DirectShapes, extrusion roofs, move/copy/rotate/mirror, save_copy ---
        'direct_shape.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $shape = $a.shape
            if ($shape.kind -eq 'box') {
                $min = @($shape.min | ForEach-Object { [double]$_ }); $max = @($shape.max | ForEach-Object { [double]$_ })
                $volume = ($max[0] - $min[0]) * ($max[1] - $min[1]) * ($max[2] - $min[2])
            }
            else {
                $outline = @($shape.outline)
                $offset = Get-FakeArg $shape 'base_offset_mm'
                $z = (Get-FakeLevelElevation $State $shape.level) + $(if ($null -ne $offset) { [double]$offset } else { 0.0 })
                $xs = @($outline | ForEach-Object { [double]$_[0] }); $ys = @($outline | ForEach-Object { [double]$_[1] })
                $min = @(($xs | Measure-Object -Minimum).Minimum, ($ys | Measure-Object -Minimum).Minimum, $z)
                $max = @(($xs | Measure-Object -Maximum).Maximum, ($ys | Measure-Object -Maximum).Maximum, ($z + [double]$shape.height_mm))
                $volume = (Get-FakeArea $outline) * [double]$shape.height_mm
            }
            if ($State.Faults.ContainsKey('DirectShapeBoxOff')) { $max = @(($max[0] + 5), $max[1], $max[2]) }
            Add-FakeElement $State $uid 'direct_shape' @{ Min = $min; Max = $max }
            $builtIns = @{ generic_model = 'OST_GenericModel'; site = 'OST_Site'; planting = 'OST_Planting'; entourage = 'OST_Entourage'; topography = 'OST_Topography' }
            [ordered]@{ direct_shape = [ordered]@{ unique_id = $uid; category = $a.category; built_in_category = $builtIns[[string]$a.category]; shape_kind = $shape.kind; name = (Get-FakeArg $a 'name'); material_unique_id = $null; volume_mm3 = $volume
                    bounding_box_mm = [ordered]@{ min = $min; max = $max } } }
        }
        'roof.create_extrusion' {
            $uid = New-FakeUniqueId $State; $planeUid = New-FakeUniqueId $State; $created = @($uid, $planeUid)
            $p0 = @($a.plane.start | ForEach-Object { [double]$_ }); $p1 = @($a.plane.end | ForEach-Object { [double]$_ })
            $length = [Math]::Sqrt(($p1[0] - $p0[0]) * ($p1[0] - $p0[0]) + ($p1[1] - $p0[1]) * ($p1[1] - $p0[1]))
            $ux = ($p1[0] - $p0[0]) / $length; $uy = ($p1[1] - $p0[1]) / $length
            $wStart = [double]$a.extrusion_start_mm; $wEnd = [double]$a.extrusion_end_mm
            # Fault: the roof extruded along the right normal instead of the left one.
            $nx = -$uy; $ny = $ux
            if ($State.Faults.ContainsKey('RoofExtrusionMirrored')) { $nx = $uy; $ny = -$ux }
            $elevation = Get-FakeLevelElevation $State $a.level
            $edges = [Collections.Generic.List[object]]::new()
            $us = [Collections.Generic.List[double]]::new(); $zs = [Collections.Generic.List[double]]::new()
            foreach ($segment in @($a.profile)) {
                $sampled = Get-FakeProfilePoints $segment
                foreach ($point in $sampled.Points) { $us.Add([double]$point[0]); $zs.Add([double]$point[1]) }
                $edge = [ordered]@{ kind = [string]$segment.kind; start = @($segment.start); end = @($segment.end) }
                if ($segment.kind -eq 'arc') { $edge['mid'] = @($sampled.Mid) }
                $edges.Add($edge)
            }
            $uMin = ($us | Measure-Object -Minimum).Minimum; $uMax = ($us | Measure-Object -Maximum).Maximum
            $xs = [Collections.Generic.List[double]]::new(); $ys = [Collections.Generic.List[double]]::new()
            foreach ($u in @($uMin, $uMax)) { foreach ($w in @($wStart, $wEnd)) { $xs.Add($p0[0] + $u * $ux + $w * $nx); $ys.Add($p0[1] + $u * $uy + $w * $ny) } }
            Add-FakeElement $State $uid 'roof' @{ Level = [string]$a.level }
            [ordered]@{ roof_extrusion = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; type_unique_id = $a.type; reference_plane_unique_id = $planeUid; plane_start_mm = $p0; plane_end_mm = $p1
                    extrusion_start_mm = $wStart; extrusion_end_mm = $wEnd; profile_mm = $edges.ToArray()
                    bounding_box_mm = [ordered]@{ min = @(($xs | Measure-Object -Minimum).Minimum, ($ys | Measure-Object -Minimum).Minimum, ($elevation + ($zs | Measure-Object -Minimum).Minimum)); max = @(($xs | Measure-Object -Maximum).Maximum, ($ys | Measure-Object -Maximum).Maximum, ($elevation + ($zs | Measure-Object -Maximum).Maximum)) } } }
        }
        { $_ -in 'element.move', 'element.copy' } {
            $uids = @($a.element_uids | ForEach-Object { [string]$_ })
            $v = @($a.vector_mm | ForEach-Object { [double]$_ })
            for ($i = 0; $i -lt $uids.Count; $i++) { if (!$State.Elements.Contains($uids[$i])) { return ConvertTo-FakeResponseJson (New-FakeMissing "args.element_uids[$i]" $uids[$i]) } }
            $shift = { param($p) @(($p[0] + $v[0]), ($p[1] + $v[1])) }
            if ($command -eq 'element.move') {
                $moved = @(foreach ($uid in $uids) {
                    $element = $State.Elements[$uid]
                    $before = Get-FakeLocation $element
                    if ($null -ne $before) { Move-FakeEnds $element $shift }
                    [ordered]@{ unique_id = $uid; before = $before; after = (Get-FakeLocation $element) }
                })
                $modified = $uids
                [ordered]@{ vector_mm = $v; moved = $moved }
            }
            else {
                $copies = @(foreach ($uid in $uids) {
                    $source = $State.Elements[$uid]
                    $copyUid = New-FakeUniqueId $State
                    Add-FakeElement $State $copyUid $source.Category (Copy-FakeFacts $source)
                    $copy = $State.Elements[$copyUid]
                    if ($null -ne (Get-FakeLocation $copy)) { Move-FakeEnds $copy $shift }
                    [ordered]@{ source_unique_id = $uid; copy_unique_id = $copyUid; location = (Get-FakeLocation $copy) }
                })
                $created = @($copies | ForEach-Object { $_.copy_unique_id })
                [ordered]@{ vector_mm = $v; copies = $copies; also_created = @() }
            }
        }
        'element.rotate' {
            $uids = @($a.element_uids | ForEach-Object { [string]$_ })
            for ($i = 0; $i -lt $uids.Count; $i++) {
                if (!$State.Elements.Contains($uids[$i])) { return ConvertTo-FakeResponseJson (New-FakeMissing "args.element_uids[$i]" $uids[$i]) }
                $element = $State.Elements[$uids[$i]]
                $hostUid = $element['Host']
                if ($null -ne $hostUid -and $uids -notcontains [string]$hostUid) { return ConvertTo-FakeResponseJson (New-FakeFailure 'element_not_rotatable' "args.element_uids[$i]" "$($uids[$i]) is hosted by $hostUid, which is not a target: a hosted element turns only with its host; include the host") }
                if ($null -eq (Get-FakeLocation $element)) { return ConvertTo-FakeResponseJson (New-FakeFailure 'element_not_rotatable' "args.element_uids[$i]" "$($uids[$i]) has no location point, location curve or sketch profile to turn and read back") }
            }
            $c = @($a.center | ForEach-Object { [double]$_ })
            $radians = [double]$a.angle_deg * [Math]::PI / 180
            # Fault: the turn applied clockwise.
            if ($State.Faults.ContainsKey('RotateWrongWay')) { $radians = -$radians }
            $cos = [Math]::Cos($radians); $sin = [Math]::Sin($radians)
            $turn = { param($p) $dx = $p[0] - $c[0]; $dy = $p[1] - $c[1]; @(($c[0] + $dx * $cos - $dy * $sin), ($c[1] + $dx * $sin + $dy * $cos)) }
            $rotated = @(foreach ($uid in $uids) {
                $element = $State.Elements[$uid]
                $before = Get-FakeLocation $element
                Move-FakeEnds $element $turn
                [ordered]@{ unique_id = $uid; before = $before; after = (Get-FakeLocation $element) }
            })
            $modified = $uids
            [ordered]@{ center_mm = $c; angle_deg = [double]$a.angle_deg; rotated = $rotated }
        }
        'element.mirror' {
            $uids = @($a.element_uids | ForEach-Object { [string]$_ })
            for ($i = 0; $i -lt $uids.Count; $i++) { if (!$State.Elements.Contains($uids[$i])) { return ConvertTo-FakeResponseJson (New-FakeMissing "args.element_uids[$i]" $uids[$i]) } }
            $m0 = @($a.axis.start | ForEach-Object { [double]$_ }); $m1 = @($a.axis.end | ForEach-Object { [double]$_ })
            $axisLength = [Math]::Sqrt(($m1[0] - $m0[0]) * ($m1[0] - $m0[0]) + ($m1[1] - $m0[1]) * ($m1[1] - $m0[1]))
            $mx = ($m1[0] - $m0[0]) / $axisLength; $my = ($m1[1] - $m0[1]) / $axisLength
            $reflect = { param($p) $along = ($p[0] - $m0[0]) * $mx + ($p[1] - $m0[1]) * $my; $fx = $m0[0] + $along * $mx; $fy = $m0[1] + $along * $my; @((2 * $fx - $p[0]), (2 * $fy - $p[1])) }
            $keep = [bool]$a.keep_original
            $mirrored = @(foreach ($uid in $uids) {
                $source = $State.Elements[$uid]
                $before = Get-FakeLocation $source
                if ($keep) {
                    $newUid = New-FakeUniqueId $State
                    Add-FakeElement $State $newUid $source.Category (Copy-FakeFacts $source)
                    $result = $State.Elements[$newUid]
                }
                else { $newUid = $uid; $result = $source }
                if ($null -ne $before) { Move-FakeEnds $result $reflect }
                [ordered]@{ source_unique_id = $uid; unique_id = $newUid; new = $keep; before = $before; after = (Get-FakeLocation $result) }
            })
            if ($keep) { $created = @($mirrored | ForEach-Object { $_.unique_id }) } else { $modified = $uids }
            [ordered]@{ axis = [ordered]@{ start = $m0; end = $m1 }; keep_original = $keep; mirrored = $mirrored; also_created = @(); deleted = @() }
        }
        'document.save_copy' {
            $target = [string]$a.path
            $source = [string]$envelope.document.path
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes("fake Revit copy of $source ($operationId)")
            [IO.File]::WriteAllBytes($target, $bytes)
            # Not $sha: that is the request hash the receipt echoes.
            $copySha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            if ($State.Faults.ContainsKey('SaveCopyHashOff')) { $copySha = '0' * 64 }
            $null = $State.Copies.Add($target)
            $State.DocumentPath = $target
            # Fault: Save As wrote the copy and re-pointed the document, then the location check failed.
            if ($State.Faults.ContainsKey('SaveCopyFailsAfterSave')) { return ConvertTo-FakeResponseJson (New-FakeFailure 'transaction_failed' 'args.path' "Revit saved the copy, but its location check failed after the save: injected fault; the open document now points to $target (send later requests with that document.path, or close it in Revit)") }
            $warnings = @("the open document is now the copy $target (Revit Save As re-points it); send later requests with document.path $target; $source keeps its last saved state")
            [ordered]@{ copy = [ordered]@{ path = $target; bytes = $bytes.Length; sha256 = $copySha; source_path = $source; source_unchanged = $true; had_unsaved_changes = $true; document_path = $target; is_modified = $false } }
        }
        'document.save_as' {
            # Like CommandsExecutorSaveAs: a new file at the target, the open document re-pointed to it, the previous file
            # left as it was, the readback of both paths and the moved-document warning (RevitSaveAsRules.DocumentMovedWarning).
            $target = [string]$a.path
            $previous = [string]$envelope.document.path
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes("fake Revit save as of $previous ($operationId)")
            [IO.File]::WriteAllBytes($target, $bytes)
            $newSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            if ($State.Faults.ContainsKey('SaveAsHashOff')) { $newSha = '0' * 64 }
            $null = $State.Copies.Add($target)
            $State.DocumentPath = $target
            # Fault: the post-save check found the file written outside the enabled roots (RevitSaveAsRules.WrittenElsewhereDetail).
            if ($State.Faults.ContainsKey('SaveAsWrittenElsewhere')) { return ConvertTo-FakeResponseJson (New-FakeFailure 'transaction_failed' 'args.path' "Revit saved the document, but the post-save check found it written outside the enabled roots or through a link: injected fault; the open document now points to $target. Close it in Revit without sending further commands to it, and do not continue until the owner has checked that file.") }
            if ($State.Faults.ContainsKey('SaveAsFailsAfterSave')) { return ConvertTo-FakeResponseJson (New-FakeFailure 'transaction_failed' $null "Revit saved the new file, but the readback differs (the open document may now be the new file): injected fault") }
            $warnings = @("the open document is now $target (Revit Save As re-points it); send later requests with document.path $target; $previous keeps its last saved state")
            [ordered]@{ save_as = [ordered]@{ path = $target; bytes = $bytes.Length; sha256 = $newSha; previous_path = $previous; previous_unchanged = $true; had_unsaved_changes = $true; document_path = $target; is_modified = $false } }
        }
        default { $null }
    }
    if ($null -eq $readback) { return ConvertTo-FakeResponseJson (New-FakeFailure 'command_unknown' 'command' $command) }
    $response = [ordered]@{ status = 'succeeded'; code = 'ok'; field = $null; detail = $null; created = $created; modified = $modified; deleted = $deleted; readback = $readback
        receipt = [ordered]@{ operation_id = $operationId; command = $command; document_unique_id = [string]$envelope.document.unique_id; request_sha256 = $sha; replayed = $false }; warnings = $warnings }
    # document.save_copy and document.save_as keep no receipt (a repeat is refused target_exists before the pipe).
    if ($writes -and !$dry -and $command -notin 'document.save_copy', 'document.save_as') { $State.Receipts[$operationId] = @{ Sha = $sha; Response = $response }; if (!$external) { $State.SkippedAddIn++ } }
    if ($external) { Add-FakeJournalEntry $State ([string]$envelope.document.path) $script:FakeExternalTransactions[$command] $created $modified $deleted }
    return ConvertTo-FakeResponseJson $response
}

# P2-20: MirrorSnapshotDiffer.Compare over two fake snapshots (Get-FakeSnapshot shape), member for member: every element
# and every unmapped entry with a UniqueId keyed by its lower-cased UniqueId (first one wins), added / removed / changed in
# ordinal key order, a changed element's fields in the differ's order (category, mapped, kind, the named geometry values
# beyond the 1e-3 mm tolerance or the whole geometry, dependencies, reason, detail), values as invariant "R" text in
# metres or null where a field does not apply.
$script:FakeDiffFieldNames = @{ level = @('elevation'); wall = @('ax', 'ay', 'bx', 'by', 'height', 'thickness'); door = @('ax', 'ay', 'bx', 'by', 'sill', 'height')
    window = @('ax', 'ay', 'bx', 'by', 'sill', 'height'); space = @('x', 'y', 'width', 'depth') }
$script:FakeDiffToleranceMetres = 1e-3 / 1000

function Get-FakeDiffStates($Snapshot) {
    $states = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    if ($null -eq $Snapshot) { return , $states }
    $sources = @{}
    foreach ($source in @($Snapshot['sources'])) { $sources[[string]$source['id']] = $source }
    foreach ($element in @($Snapshot['elements'])) {
        $source = $sources[[string]$element['Id']]
        $key = ([string]$source['unique_id']).ToLowerInvariant()
        if (!$states.ContainsKey($key)) { $states[$key] = @{ UniqueId = [string]$source['unique_id']; Category = [string]$source['category']; Element = $element; Unmapped = $null } }
    }
    foreach ($entry in @($Snapshot['unmapped'])) {
        if ([string]::IsNullOrWhiteSpace([string]$entry['unique_id'])) { continue }
        $key = ([string]$entry['unique_id']).ToLowerInvariant()
        if (!$states.ContainsKey($key)) { $states[$key] = @{ UniqueId = [string]$entry['unique_id']; Category = [string]$entry['category']; Element = $null; Unmapped = $entry } }
    }
    return , $states
}

function Get-FakeDiffNumber([double]$Value) { $Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture) }

function Add-FakeDiffField($Fields, [string]$Field, $Before, $After) {
    $same = if ($null -eq $Before) { $null -eq $After } else { $null -ne $After -and [string]::Equals([string]$Before, [string]$After, [StringComparison]::Ordinal) }
    if (!$same) { $Fields.Add([ordered]@{ field = $Field; before = $Before; after = $After }) }
}

function Get-FakeDiffFields($Before, $After) {
    $fields = [Collections.Generic.List[object]]::new()
    $a = $Before.Element; $b = $After.Element
    Add-FakeDiffField $fields 'category' $Before.Category $After.Category
    Add-FakeDiffField $fields 'mapped' $(if ($null -eq $a) { 'false' } else { 'true' }) $(if ($null -eq $b) { 'false' } else { 'true' })
    Add-FakeDiffField $fields 'kind' $(if ($null -eq $a) { $null } else { [string]$a['Kind'] }) $(if ($null -eq $b) { $null } else { [string]$b['Kind'] })
    # Assigned directly, not from an if (which would unroll a one-value array into a scalar).
    $ga = @(); if ($null -ne $a) { $ga = @($a['GeometryMetres']) }
    $gb = @(); if ($null -ne $b) { $gb = @($b['GeometryMetres']) }
    if ($null -ne $a -and $null -ne $b -and [string]$a['Kind'] -ceq [string]$b['Kind'] -and $ga.Count -eq $gb.Count) {
        $names = $null
        if ($script:FakeDiffFieldNames.ContainsKey([string]$a['Kind']) -and @($script:FakeDiffFieldNames[[string]$a['Kind']]).Count -eq $ga.Count) { $names = @($script:FakeDiffFieldNames[[string]$a['Kind']]) }
        for ($i = 0; $i -lt $ga.Count; $i++) {
            $x = [double]$ga[$i]; $y = [double]$gb[$i]
            if ($x -eq $y -or [Math]::Abs($x - $y) -le $script:FakeDiffToleranceMetres) { continue }
            $name = if ($null -ne $names) { [string]$names[$i] } else { "geometry[$i]" }
            $fields.Add([ordered]@{ field = $name; before = (Get-FakeDiffNumber $x); after = (Get-FakeDiffNumber $y) })
        }
    }
    else {
        Add-FakeDiffField $fields 'geometry' $(if ($null -eq $a) { $null } else { @($ga | ForEach-Object { Get-FakeDiffNumber ([double]$_) }) -join ',' }) $(if ($null -eq $b) { $null } else { @($gb | ForEach-Object { Get-FakeDiffNumber ([double]$_) }) -join ',' })
    }
    Add-FakeDiffField $fields 'dependencies' $(if ($null -eq $a) { $null } else { @(@($a['Dependencies']) | Where-Object { $null -ne $_ } | ForEach-Object { [guid]::Parse([string]$_).ToString('D') }) -join ',' }) $(if ($null -eq $b) { $null } else { @(@($b['Dependencies']) | Where-Object { $null -ne $_ } | ForEach-Object { [guid]::Parse([string]$_).ToString('D') }) -join ',' })
    Add-FakeDiffField $fields 'reason' $(if ($null -eq $Before.Unmapped) { $null } else { [string]$Before.Unmapped['reason'] }) $(if ($null -eq $After.Unmapped) { $null } else { [string]$After.Unmapped['reason'] })
    Add-FakeDiffField $fields 'detail' $(if ($null -eq $Before.Unmapped) { $null } else { [string]$Before.Unmapped['detail'] }) $(if ($null -eq $After.Unmapped) { $null } else { [string]$After.Unmapped['detail'] })
    return , $fields
}

function Get-FakeSnapshotDiff([string]$DocumentUid, $Before, $After) {
    $old = Get-FakeDiffStates $Before
    $new = Get-FakeDiffStates $After
    $added = [Collections.Generic.List[object]]::new()
    $removed = [Collections.Generic.List[object]]::new()
    $changed = [Collections.Generic.List[object]]::new()
    foreach ($key in @($new.Keys)) {
        $state = $new[$key]
        if (!$old.ContainsKey($key)) { $added.Add([ordered]@{ unique_id = $state.UniqueId; category = $state.Category }); continue }
        $fields = Get-FakeDiffFields $old[$key] $state
        if ($fields.Count -gt 0) { $changed.Add([ordered]@{ unique_id = $state.UniqueId; category = $state.Category; fields = $fields.ToArray() }) }
    }
    foreach ($key in @($old.Keys)) { if (!$new.ContainsKey($key)) { $state = $old[$key]; $removed.Add([ordered]@{ unique_id = $state.UniqueId; category = $state.Category }) } }
    return [ordered]@{ diff_version = 1; document_unique_id = $DocumentUid; added = $added.ToArray(); removed = $removed.ToArray(); changed = $changed.ToArray() }
}

# The journal.read wire line ({"DocumentPath","Operation","Journal"}), answered like JournalRecorder.Read: add-in writes
# are counted in skipped_add_in (never entries), and the diff compares the snapshot sources with the last non-peek read.
# The last member of a System.Text.Json object with exactly this name (the add-in's Request binding), or $null.
function Get-FakeWireMember($Element, [string]$Name) {
    $found = $null
    if ([string]$Element.ValueKind -eq 'Object') { foreach ($member in $Element.EnumerateObject()) { if ($member.Name -ceq $Name) { $found = $member.Value } } }
    return $found
}

function Get-FakeWireText($Element, [string]$Name) {
    $value = Get-FakeWireMember $Element $Name
    if ($null -eq $value -or [string]$value.ValueKind -eq 'Null') { return '' }
    if ([string]$value.ValueKind -eq 'String') { return $value.GetString() }
    return $value.GetRawText()
}

function New-FakeJournalFailure([string]$Code) { ConvertTo-FakeResponseJson ([ordered]@{ status = 'failed'; code = $Code; verification = 'unverified' }) }

function Invoke-FakeRevitWire($State, [string]$Line) {
    $State.Received.Add('journal.read')
    # ConvertFrom-Json for the pre-P2-18 path (unchanged replies); System.Text.Json, which reads duplicate and
    # differently cased members the way the add-in does, when ConvertFrom-Json refuses the line or with ExternalEdits.
    $request = try { $Line | ConvertFrom-Json -ErrorAction Stop } catch { $null }
    $document = $null
    try { $document = [Text.Json.JsonDocument]::Parse($Line, [Text.Json.JsonDocumentOptions]::new()) } catch { }
    if ($null -eq $request -and $null -eq $document) { return New-FakeJournalFailure 'request_invalid' }
    try {
        if ($null -ne $request -and $null -eq $State.ExternalEdits) { $operation = [string]$request.Operation; $path = [string]$request.DocumentPath }
        elseif ($null -ne $document) { $operation = Get-FakeWireText $document.RootElement 'Operation'; $path = Get-FakeWireText $document.RootElement 'DocumentPath' }
        else { return New-FakeJournalFailure 'request_invalid' }
        if ($operation -ne 'journal.read') { return '{"status":"failed","code":"unsupported_capability"}' }
        if ($null -ne $State.DocumentPath -and ![string]::Equals($path, $State.DocumentPath, [StringComparison]::OrdinalIgnoreCase)) { return '{"status":"failed","code":"document_conflict"}' }
        $peek = $false
        if ($null -ne $request) { $peek = [bool](Get-FakeArg (Get-FakeArg $request 'Journal') 'peek') }
        # Pre-P2-18 values (no ExternalEdits): no entries, every sequence 0, options other than peek ignored.
        $baselineSequence = 0; $currentSequence = 0; $afterSequence = 0; $hasMore = $false; $nextAfter = 0; $entries = @()
        if ($null -ne $State.ExternalEdits) {
            if ($null -eq $document) { return New-FakeJournalFailure 'request_invalid' }
            # MirrorJournalReadOptions.Parse, then MirrorJournalTracker.Report; failures as App.Execute answers an exception.
            $after = $null; $maxEntries = 256; $peek = $false
            $options = Get-FakeWireMember $document.RootElement 'Journal'
            if ($null -ne $options -and [string]$options.ValueKind -ne 'Null') {
                if ([string]$options.ValueKind -ne 'Object') { return New-FakeJournalFailure 'journal_options_invalid options must be an object' }
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach ($member in $options.EnumerateObject()) {
                    if (!$seen.Add($member.Name)) { return New-FakeJournalFailure "journal_options_invalid duplicate $($member.Name)" }
                    $value = $member.Value
                    $kind = [string]$value.ValueKind
                    $long = [long]0; $int = [int]0
                    if ($member.Name -ceq 'after_sequence' -and $kind -eq 'Number' -and $value.TryGetInt64([ref]$long) -and $long -ge 0) { $after = $long }
                    elseif ($member.Name -ceq 'peek' -and $kind -in 'True', 'False') { $peek = $value.GetBoolean() }
                    elseif ($member.Name -ceq 'max_entries' -and $kind -eq 'Number' -and $value.TryGetInt32([ref]$int) -and $int -ge 1 -and $int -le 256) { $maxEntries = $int }
                    else { return New-FakeJournalFailure "journal_options_invalid $($member.Name)" }
                }
            }
            $last = [long]$State.JournalSequence
            $since = if ($null -ne $State.JournalBaseline) { [long]$State.JournalBaselineSequence } else { [long]0 }
            if ($null -eq $after) { $after = $since }
            if ($after -gt $last) { return New-FakeJournalFailure 'journal_after_sequence_invalid' }
            $candidates = @($State.Journal | Where-Object { $_.sequence -gt $after -and [string]::Equals($_.document_path, $path, [StringComparison]::OrdinalIgnoreCase) })
            $page = [Collections.Generic.List[object]]::new()
            $ids = 0
            foreach ($entry in $candidates) {
                $count = $entry.added.Count + $entry.modified.Count + $entry.deleted_element_ids.Count
                if ($page.Count -gt 0 -and ($page.Count -ge $maxEntries -or $ids + $count -gt 20000)) { break }
                $page.Add($entry); $ids += $count
            }
            $hasMore = $page.Count -lt $candidates.Count
            $baselineSequence = $since; $currentSequence = $last; $afterSequence = $after
            $nextAfter = if ($hasMore) { $page[$page.Count - 1].sequence } else { $last }
            $entries = $page.ToArray()
            if (!$peek) { $State.JournalBaselineSequence = $last }
        }
        $baselineExisted = $null -ne $State.JournalBaseline
        if ($null -ne $State.ExternalEdits) {
            # P2-20: MirrorSnapshotDiffer.Compare(baseline snapshot or none, current snapshot); the baseline is the snapshot.
            $snapshot = Get-FakeSnapshot $State
            $diff = Get-FakeSnapshotDiff $State.DocumentUid $(if ($baselineExisted) { $State.JournalBaseline } else { $null }) $snapshot
            if (!$peek) { $State.JournalBaseline = $snapshot }
        }
        else {
            # Pre-P2-20 diff (unchanged replies): sources only, snapshot order, changed always empty.
            $current = [ordered]@{}
            foreach ($source in (Get-FakeSnapshot $State).sources) { $current[[string]$source.unique_id] = [string]$source.category }
            $base = [ordered]@{}
            if ($baselineExisted) { $base = $State.JournalBaseline }
            $added = @(foreach ($uid in @($current.Keys)) { if (!$base.Contains($uid)) { [ordered]@{ unique_id = $uid; category = $current[$uid] } } })
            $removed = @(foreach ($uid in @($base.Keys)) { if (!$current.Contains($uid)) { [ordered]@{ unique_id = $uid; category = $base[$uid] } } })
            if (!$peek) { $State.JournalBaseline = $current }
            $diff = [ordered]@{ diff_version = 1; document_unique_id = $State.DocumentUid; added = $added; removed = $removed; changed = @() }
        }
        $report = [ordered]@{ journal_version = 1; document_unique_id = $State.DocumentUid; document_path = $path; baseline_sequence = $baselineSequence; current_sequence = $currentSequence; after_sequence = $afterSequence; baseline_existed = $baselineExisted; peek = $peek
            journal_complete = $true; failures_since_baseline = 0; has_more = $hasMore; next_after_sequence = $nextAfter; entries = $entries; diff = $diff }
        return ConvertTo-FakeResponseJson ([ordered]@{ status = 'succeeded'; verification = 'unverified'; attached = $true; dropped = 0; skipped_add_in = $State.SkippedAddIn; failures = 0; unidentified = 0; report = $report })
    } finally { if ($null -ne $document) { $document.Dispose() } }
}
