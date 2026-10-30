# Test double for the C03 add-in executor, used only by test-catalogue-session.ps1 (in process) and
# fake-revit-command-pipe.ps1 (behind a real named pipe). It answers catalogue v1 envelopes with the response shape
# of RevitCommandResponse.ToJson, keeps receipts by operation_id (replay on the same request text, conflict on a
# different one) and records every operation_id it receives. It proves nothing about Revit.
Set-StrictMode -Version Latest

function New-FakeRevitState([string]$DocumentUid, [hashtable]$Faults = @{}) {
    @{ DocumentUid = $DocumentUid; Receipts = @{}; Received = [Collections.Generic.List[string]]::new(); Next = 0; Faults = $Faults; Episode = '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0' }
}

function New-FakeUniqueId($State) { $State.Next++; '{0}-{1:x8}' -f $State.Episode, (4096 + $State.Next) }

function ConvertTo-FakeResponseJson($Response) { $Response | ConvertTo-Json -Depth 20 -Compress }

function New-FakeFailure([string]$Code, [string]$Field, [string]$Detail) {
    [ordered]@{ status = 'failed'; code = $Code; field = $Field; detail = $Detail; created = @(); modified = @(); deleted = @(); readback = $null; receipt = $null; warnings = @() }
}

function Get-FakeSha256([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($Text))).ToLowerInvariant()
}

# $CommandText is the envelope exactly as received (the pipe's Command member); returns one response line.
function Invoke-FakeRevitCommand($State, [string]$CommandText) {
    $envelope = $CommandText | ConvertFrom-Json
    $operationId = [string]$envelope.operation_id
    $State.Received.Add($operationId)
    $command = [string]$envelope.command
    $a = $envelope.args
    $sha = Get-FakeSha256 $CommandText
    $writes = $command -notlike 'query.*'
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
    # Client-side geometry rule, emulated only for the in-process run; behind the pipe the real CLI refuses first.
    if ($command -eq 'wall.create' -and $a.curve.kind -eq 'arc' -and $a.curve.PSObject.Properties['through']) {
        $s = $a.curve.start; $e = $a.curve.end; $t = $a.curve.through
        if ([Math]::Abs(($e[0] - $s[0]) * ($t[1] - $s[1]) - ($e[1] - $s[1]) * ($t[0] - $s[0])) -lt 1e-6) { return ConvertTo-FakeResponseJson (New-FakeFailure 'arc_collinear' 'args.curve' 'the three points are collinear') }
    }
    $created = @()
    $readback = switch ($command) {
        'query.levels' { [ordered]@{ levels = @([ordered]@{ name = 'Level 1'; unique_id = (New-FakeUniqueId $State); elevation_mm = 0; project_elevation_mm = 0 }, [ordered]@{ name = 'Level 2'; unique_id = (New-FakeUniqueId $State); elevation_mm = 4000; project_elevation_mm = 4000 }) } }
        'query.wall_types' { [ordered]@{ wall_types = @(
            [ordered]@{ name = 'Curtain Wall'; unique_id = (New-FakeUniqueId $State); kind = 'Curtain'; width_mm = $null },
            [ordered]@{ name = 'Exterior - Brick'; unique_id = (New-FakeUniqueId $State); kind = 'Stacked'; width_mm = $null },
            [ordered]@{ name = 'Generic - 200mm'; unique_id = (New-FakeUniqueId $State); kind = 'Basic'; width_mm = 200 }) } }
        'query.floor_types' { [ordered]@{ floor_types = @([ordered]@{ name = 'Generic 150mm'; unique_id = (New-FakeUniqueId $State); width_mm = 150 }) } }
        'query.family_symbols' {
            $category = [string]$a.category
            [ordered]@{ category = $category; family_symbols = @(
                [ordered]@{ family = 'A Face Based'; type = 'Default'; reference = 'A Face Based: Default'; unique_id = (New-FakeUniqueId $State); active = $true; wall_hosted = $false },
                [ordered]@{ family = "M_$category"; type = 'Standard'; reference = "M_${category}: Standard"; unique_id = (New-FakeUniqueId $State); active = $false; wall_hosted = $true }) }
        }
        'wall.create' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $curve = if ($a.curve.kind -eq 'line') { [ordered]@{ kind = 'line'; start = $a.curve.start; end = $a.curve.end; length_mm = 1 } }
            else { [ordered]@{ kind = 'arc'; start = @(0, 0); end = @(0, 0); length_mm = 1; radius_mm = $(if ($State.Faults.ContainsKey('WrongRadius')) { 1 } else { $a.curve.radius_mm }); center = $a.curve.center } }
            [ordered]@{ wall = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; type_unique_id = $a.type; curve = $curve; height_mm = $a.height_mm; base_offset_mm = 0; structural = $false } }
        }
        'opening.place' {
            $uid = New-FakeUniqueId $State; $created = @($uid)
            $sill = if ($a.PSObject.Properties['sill_mm']) { $a.sill_mm } else { 0 }
            [ordered]@{ opening = [ordered]@{ unique_id = $uid; category = $a.category; host_wall_unique_id = $a.host_wall_uid; symbol_unique_id = $a.symbol; point = @(0, 0); sill_mm = $sill } }
        }
        'floor.create' { $uid = New-FakeUniqueId $State; $created = @($uid); [ordered]@{ floor = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; type_unique_id = $a.type; area_mm2 = 24000000 } } }
        'room.create' { $uid = New-FakeUniqueId $State; $created = @($uid); [ordered]@{ room = [ordered]@{ unique_id = $uid; level_unique_id = $a.level; name = $a.name; number = '1'; area_mm2 = 23000000 } } }
        default { $null }
    }
    if ($null -eq $readback) { return ConvertTo-FakeResponseJson (New-FakeFailure 'command_unknown' 'command' $command) }
    $response = [ordered]@{ status = 'succeeded'; code = 'ok'; field = $null; detail = $null; created = $created; modified = @(); deleted = @(); readback = $readback
        receipt = [ordered]@{ operation_id = $operationId; command = $command; document_unique_id = [string]$envelope.document.unique_id; request_sha256 = $sha; replayed = $false }; warnings = @() }
    if ($writes) { $State.Receipts[$operationId] = @{ Sha = $sha; Response = $response } }
    return ConvertTo-FakeResponseJson $response
}
