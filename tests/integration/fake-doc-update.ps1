# Headless stand-in for the CLI doc-update verb (P4-14), for tests only: dot-sourced after
# scripts/owner-catalogue-steps.ps1 and fake-revit-command-executor.ps1 by tests/integration/test-catalogue-session.ps1
# and tests/cli/catalogue-report-record.ps1, which pass Invoke-FakeDocUpdate as the sequence's -InvokeDocUpdate.
# New-FakeDocUpdate writes its plan files in a new folder under -Directory. Never Revit, never the CLI.
# Mirrors, for the owner script's fixture (New-CatalogueDocUpdateProject) and against the in-process fake add-in, only what
# the session reads of interfaces/cli/DocUpdateCommand.cs and DocUpdateExecute.cs: the 9 DocDefaults keys of the
# fixture, all forced (created) while the realized map has no doc: entry, else only the door tag when the wall moved;
# each stale delete probed by two element.delete dry runs (collect, confirm) through the fake, a dependent refused
# dependent_unknown; the plan file (the members the session reads: Regeneration.Regenerated, Forced, Retired,
# Replacement.Commands.Steps[] OperationId / Command / ArgsJson with realized inputs filled in) and its SHA-256 in the
# dry summary; --execute (expected_document) refusing doc_update_plan_changed before any write and writing no file, else
# the deletes then the creates in DocReplace order through the fake (created[0] of a parent filled in), a per-step
# report (done / failed / not_sent, reply_lost) and the updated realized map. The real verb runs in the real-CLI pass.
# Faults (in the sequence's $Faults): DocUpdateLoseReplyOf = '<step key>|<command>' (that write runs, its reply is lost:
# failed no_response with reply_lost, the rest not_sent); DocUpdateDrift = '<step key>' (that call plans other bytes);
# DocUpdateDependents = @{ '<step key>' = uids } (its collect dry runs report them).
# P4-16: a request with a record member is the session's doc-realized call (same channel, -InvokeDocUpdate): see
# Invoke-FakeDocRealized; its calls are listed in Realized, not in Calls.
function Get-FakeDocSha([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($Text))).ToLowerInvariant() }
function New-FakeDocUpdate($Fake, [string]$Directory, [hashtable]$Faults = @{}) {
    $directory = Join-Path $Directory ('doc-update-' + [guid]::NewGuid())
    New-Item -ItemType Directory -Force $directory | Out-Null
    return @{ Fake = $Fake; Directory = $directory; Faults = $Faults; Calls = [Collections.Generic.List[string]]::new(); Realized = [Collections.Generic.List[string]]::new() }
}
function Get-FakeDocElement($Project, [string]$Kind) {
    $elements = Get-CatalogueList $Project 'Elements'
    foreach ($element in $elements) { if ((Get-CatalogueMember $element 'Kind') -eq $Kind) { return $element } }
    return $null
}
# P4-16: stand-in for the CLI doc-realized verb (interfaces/cli/DocRealizedCommand.cs over core DocRealized.FromRecord)
# for the session's request {record, fixture, assignments, document}. Mirrors only what the session can meet: the
# record's schema_version, steps (index i+1) and document_unique_id (realized_document_mismatch); per source in ordinal
# order: element sources of the fixture only (else realized_source_unknown), the assigned step (realized_step_unknown),
# passed, not blocked and status succeeded (realized_step_not_succeeded), its first created UniqueId
# (realized_step_no_created), not in any succeeded step's deleted[] (realized_unique_id_deleted), not realized twice
# (realized_unique_id_duplicate). Writes the compact map sorted by source to a new file and prints the CLI's line
# {status, output, entries, bytes, sha256}; a refusal prints {status: failed, code, detail}, exit 1, no file.
function Invoke-FakeDocRealized($State, [int]$Index, [string]$Key, $Request) {
    $State.Realized.Add($Key)
    function Line($Value, [int]$Exit) { @{ output = ($Value | ConvertTo-Json -Depth 20 -Compress); exit_code = $Exit; timed_out = $false } }
    function Refuse([string]$Code, [string]$Detail) { Line ([ordered]@{ status = 'failed'; code = $Code; detail = $Detail }) 1 }
    $record = Get-CatalogueMember $Request 'record'
    if ((Get-CatalogueMember $record 'schema_version') -ne 1) { return Refuse 'realized_record_invalid' 'schema_version' }
    $documentUid = [string](Get-CatalogueMember $record 'document_unique_id')
    $steps = Get-CatalogueList $record 'steps'
    if (!$steps.Count) { return Refuse 'realized_record_invalid' 'steps' }
    for ($i = 0; $i -lt $steps.Count; $i++) { if ([int](Get-CatalogueMember $steps[$i] 'index') -ne $i + 1) { return Refuse 'realized_record_invalid' "steps[$i].index" } }
    if (![string]::Equals([string](Get-CatalogueMember $Request 'document'), $documentUid, [StringComparison]::OrdinalIgnoreCase)) { return Refuse 'realized_document_mismatch' 'document_unique_id' }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($element in (Get-CatalogueList (Get-CatalogueMember $Request 'fixture') 'Elements')) { $null = $ids.Add([string](Get-CatalogueMember $element 'Id')) }
    $succeeded = { param($Step) (Get-CatalogueMember $Step 'passed') -eq $true -and (Get-CatalogueMember $Step 'blocked') -ne $true -and [string](Get-CatalogueMember (Get-CatalogueMember $Step 'response') 'status') -eq 'succeeded' }
    $deleted = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($step in $steps) { if ([string](Get-CatalogueMember (Get-CatalogueMember $step 'response') 'status') -eq 'succeeded') { foreach ($uid in (Get-CatalogueList (Get-CatalogueMember $step 'response') 'deleted')) { $null = $deleted.Add([string]$uid) } } }
    $assignments = Get-CatalogueMember $Request 'assignments'
    $sources = [string[]]@(if ($assignments -is [Collections.IDictionary]) { $assignments.Keys } else { $assignments.PSObject.Properties | ForEach-Object { $_.Name } })
    [Array]::Sort($sources, [StringComparer]::Ordinal)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $map = [ordered]@{}
    foreach ($source in $sources) {
        $stepKey = [string](Get-CatalogueMember $assignments $source)
        if (!$source.StartsWith('element:', [StringComparison]::Ordinal) -or !$ids.Contains($source.Substring(8))) { return Refuse 'realized_source_unknown' $source }
        $step = @($steps | Where-Object { [string](Get-CatalogueMember $_ 'key') -ceq $stepKey }) | Select-Object -First 1
        if ($null -eq $step) { return Refuse 'realized_step_unknown' $source }
        if (!(& $succeeded $step)) { return Refuse 'realized_step_not_succeeded' $source }
        $created = Get-CatalogueList (Get-CatalogueMember $step 'response') 'created'
        if (!$created.Count) { return Refuse 'realized_step_no_created' $source }
        $uid = [string]$created[0]
        if ($deleted.Contains($uid)) { return Refuse 'realized_unique_id_deleted' $source }
        if (!$seen.Add($uid)) { return Refuse 'realized_unique_id_duplicate' $source }
        $map[$source] = $uid
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $map -Compress))
    $output = Join-Path $State.Directory ('{0:D2}-{1}.realized.json' -f $Index, $Key)
    [IO.File]::WriteAllBytes($output, $bytes)
    $reply = Line ([ordered]@{ status = 'succeeded'; output = $output; entries = $map.Count; bytes = $bytes.Length; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }) 0
    $reply['realized_file'] = $output
    return $reply
}
function Invoke-FakeDocUpdate($State, [int]$Index, [string]$Key, [string]$Json) {
    $request = $Json | ConvertFrom-Json
    if ($null -ne (Get-CatalogueMember $request 'record')) { return Invoke-FakeDocRealized $State $Index $Key $request }
    $State.Calls.Add($Key)
    $faults = $State.Faults
    $bindings = Get-CatalogueMember $request 'bindings'
    $document = Get-CatalogueMember $bindings 'document'
    $docPath = [string](Get-CatalogueMember $document 'path')
    $docUid = [string](Get-CatalogueMember $document 'unique_id')
    $level = [string](Get-CatalogueMember (Get-CatalogueMember $bindings 'levels') $script:CatalogueDocUpdateLevelId)
    $realized = [ordered]@{}
    foreach ($property in (Get-CatalogueMember $request 'realized').PSObject.Properties) { $realized[$property.Name] = [string]$property.Value }
    $before = Get-CatalogueMember $request 'before'
    $after = Get-CatalogueMember $request 'after'
    $g = @(Get-CatalogueMember (Get-FakeDocElement $after 'wall') 'GeometryMetres' | ForEach-Object { [double]$_ })
    $beforeY = [double]@(Get-CatalogueMember (Get-FakeDocElement $before 'wall') 'GeometryMetres')[1]
    $d = @(Get-CatalogueMember (Get-FakeDocElement $after 'door') 'GeometryMetres' | ForEach-Object { [double]$_ })
    $length = [Math]::Sqrt(($g[2] - $g[0]) * ($g[2] - $g[0]) + ($g[3] - $g[1]) * ($g[3] - $g[1]))
    $along = $d[0] + $d[1] / 2
    $head = @((($g[0] + ($g[2] - $g[0]) / $length * $along) * 1000), (($g[1] + ($g[3] - $g[1]) / $length * $along) * 1000))
    $plan = Get-CatalogueDocUpdateKey 'plan-' $script:CatalogueDocUpdateLevelId
    $sheet = Get-CatalogueDocUpdateKey 'sheet-' $script:CatalogueDocUpdateLevelId
    $tagKey = Get-CatalogueDocUpdateKey 'tag-door-' $script:CatalogueDocUpdateDoorId
    $items = [ordered]@{
        $plan = @{ Kind = 0; Command = 'view.create_plan'; Args = [ordered]@{ kind = 'floor_plan'; level = $level; name = 'Level 1 Plan'; scale = 100 }; Inputs = [ordered]@{} }
        'schedule-doors' = @{ Kind = 1; Command = 'schedule.create'; Args = [ordered]@{ category = 'door'; fields = @('Mark', 'Width'); sort_by = 'Mark'; name = 'Door Schedule' }; Inputs = [ordered]@{} }
        'schedule-rooms' = @{ Kind = 1; Command = 'schedule.create'; Args = [ordered]@{ category = 'room'; fields = @('Number', 'Name', 'Area'); sort_by = 'Number'; name = 'Room Schedule' }; Inputs = [ordered]@{} }
        $sheet = @{ Kind = 2; Command = 'sheet.create'; Args = [ordered]@{ number = 'A101'; name = 'Level 1' }; Inputs = [ordered]@{} }
        'sheet-schedules' = @{ Kind = 2; Command = 'sheet.create'; Args = [ordered]@{ number = 'A900'; name = 'Schedules' }; Inputs = [ordered]@{} }
        $tagKey = @{ Kind = 3; Command = 'tag.create'; Args = [ordered]@{ view_uid = $null; element_uid = $realized['element:' + $script:CatalogueDocUpdateDoorId]; head = $head; leader = $false; orientation = 'horizontal' }; Inputs = [ordered]@{ view_uid = $plan } }
        (Get-CatalogueDocUpdateKey 'place-plan-' $script:CatalogueDocUpdateLevelId) = @{ Kind = 5; Command = 'sheet.place_view'; Args = [ordered]@{ sheet_uid = $null; view_uid = $null; center = @(400, 300) }; Inputs = [ordered]@{ sheet_uid = $sheet; view_uid = $plan } }
        'place-schedule-doors' = @{ Kind = 5; Command = 'sheet.place_view'; Args = [ordered]@{ sheet_uid = $null; view_uid = $null; center = @(200, 400) }; Inputs = [ordered]@{ sheet_uid = 'sheet-schedules'; view_uid = 'schedule-doors' } }
        'place-schedule-rooms' = @{ Kind = 5; Command = 'sheet.place_view'; Args = [ordered]@{ sheet_uid = $null; view_uid = $null; center = @(600, 400) }; Inputs = [ordered]@{ sheet_uid = 'sheet-schedules'; view_uid = 'schedule-rooms' } }
    }
    $hasDoc = @($realized.Keys | Where-Object { $_ -like 'doc:*' }).Count -gt 0
    $all = [string[]]@($items.Keys)
    [Array]::Sort($all, [StringComparer]::Ordinal)
    $regenerated = [string[]]@(if (!$hasDoc) { $all } elseif ($beforeY -ne $g[1]) { $tagKey })
    $forced = [string[]]@(if (!$hasDoc) { $all })
    $steps = [Collections.Generic.List[hashtable]]::new()
    $deleteOf = @{}
    foreach ($item in @($regenerated | Sort-Object { 5 - $items[$_].Kind }, { $_ })) {
        if (!$realized.Contains("doc:$item")) { continue }
        $operation = 'docdel.' + (Get-FakeDocSha "$item`n$($realized["doc:$item"])`n$($g[1])").Substring(0, 32)
        $deleteOf[$item] = $operation
        $steps.Add(@{ Key = $item; Command = 'element.delete'; Args = [ordered]@{ element_uids = @($realized["doc:$item"]) }; Runtime = @{}; OperationId = $operation })
    }
    $deletes = $steps.Count
    foreach ($item in @($regenerated | Sort-Object { $items[$_].Kind }, { $_ })) {
        $spec = $items[$item]
        $values = [ordered]@{}
        foreach ($entry in $spec.Args.GetEnumerator()) { $values[$entry.Key] = $entry.Value }
        $runtime = @{}
        foreach ($link in $spec.Inputs.GetEnumerator()) {
            if ($regenerated -contains $link.Value) { $values[$link.Key] = '00000000-0000-0000-0000-000000000000-00000000'; $runtime[$link.Key] = $link.Value }
            else { $values[$link.Key] = $realized["doc:$($link.Value)"] }
        }
        $operation = 'docnew.' + (Get-FakeDocSha "$item`n$($values | ConvertTo-Json -Depth 8 -Compress)`n$($deleteOf[$item])").Substring(0, 32)
        $steps.Add(@{ Key = $item; Command = $spec.Command; Args = $values; Runtime = $runtime; OperationId = $operation })
    }
    function Line($Value, [int]$Exit) { @{ output = ($Value | ConvertTo-Json -Depth 20 -Compress); exit_code = $Exit; timed_out = $false } }
    function Send([string]$Operation, [string]$Command, $Arguments) { Invoke-FakeRevitCommand $State.Fake (New-CatalogueEnvelope $Operation $docPath $docUid $Command $Arguments) | ConvertFrom-Json }
    # The two passes of dry runs (P4-09) before anything is planned for good.
    $injected = if ($faults.ContainsKey('DocUpdateDependents') -and $faults['DocUpdateDependents'].ContainsKey($Key)) { @($faults['DocUpdateDependents'][$Key]) } else { @() }
    foreach ($pass in 'collect', 'confirm') {
        for ($i = 0; $i -lt $deletes; $i++) {
            $step = $steps[$i]
            $dry = 'docdry.' + (Get-FakeDocSha "$pass`n$($step.OperationId)").Substring(0, 16)
            $reply = Send $dry 'element.delete' ([ordered]@{ element_uids = @($step.Args.element_uids); dry_run = $true })
            if ($reply.status -ne 'succeeded') { return Line ([ordered]@{ status = 'failed'; code = $reply.code; key = $step.Key; operation_id = $dry; field = $reply.field; detail = $reply.detail }) 1 }
            $found = @(@(Get-CataloguePath $reply 'readback.delete.dependents') + $(if ($pass -eq 'collect') { $injected } else { @() }) | Where-Object { $_ })
            if ($found.Count) { return Line ([ordered]@{ status = 'failed'; code = 'dependent_unknown'; key = $step.Key; operation_id = $dry; detail = [string]$found[0] }) 1 }
        }
    }
    $planSteps = @(foreach ($step in $steps) { [ordered]@{ OperationId = $step.OperationId; Command = $step.Command; ArgsJson = ($step.Args | ConvertTo-Json -Depth 8 -Compress) } })
    $planObject = [ordered]@{ Regeneration = [ordered]@{ Regenerated = @($regenerated) }; Replacement = [ordered]@{ Commands = [ordered]@{ Steps = $planSteps } }; Forced = @($forced); Retired = @(); Realized = $realized; Gone = @() }
    if ($faults['DocUpdateDrift'] -eq $Key) { $planObject['Drift'] = $true }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($planObject | ConvertTo-Json -Depth 20 -Compress))
    $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $expected = Get-CatalogueMember $request 'expected_document'
    if ($null -ne $expected -and ![string]::Equals([string]$expected, $sha, [StringComparison]::OrdinalIgnoreCase)) { return Line ([ordered]@{ status = 'failed'; code = 'doc_update_plan_changed'; field = 'expected_document'; detail = $sha }) 1 }
    $output = Join-Path $State.Directory ('{0:D2}-{1}.plan.json' -f $Index, $Key)
    [IO.File]::WriteAllBytes($output, $bytes)
    $summary = [ordered]@{ output = $output; bytes = $bytes.Length; sha256 = $sha; regenerated = $regenerated.Count; steps = $steps.Count; forced = $forced.Count; retired = 0; probed = $deletes; dependents = 0 }
    if ($null -eq $expected) {
        $dryLine = [ordered]@{ status = 'succeeded' }
        foreach ($entry in $summary.GetEnumerator()) { $dryLine[$entry.Key] = $entry.Value }
        $dryLine['applied'] = $false
        return Line $dryLine 0
    }
    $created = @{}
    $report = [Collections.Generic.List[object]]::new()
    $failure = $null
    foreach ($step in $steps) {
        $entry = [ordered]@{ operation_id = $step.OperationId; key = $step.Key; command = $step.Command; sent = 0 }
        if ($null -ne $failure) { $entry['status'] = 'not_sent'; $report.Add($entry); continue }
        $values = [ordered]@{}
        foreach ($pair in $step.Args.GetEnumerator()) { $values[$pair.Key] = $pair.Value }
        foreach ($pair in $step.Runtime.GetEnumerator()) { $values[$pair.Key] = $created[$pair.Value] }
        $reply = Send $step.OperationId $step.Command $values
        $entry['sent'] = 1
        if ($faults['DocUpdateLoseReplyOf'] -eq "$Key|$($step.Command)") { $entry['status'] = 'failed'; $entry['code'] = 'no_response'; $entry['field'] = $null; $entry['detail'] = 'no reply'; $entry['reply_lost'] = $true; $failure = $entry }
        elseif ($reply.status -eq 'succeeded') {
            $entry['status'] = 'done'; $entry['replayed'] = [bool](Get-CataloguePath $reply 'receipt.replayed'); $entry['created'] = @(Get-CatalogueList $reply 'created' | ForEach-Object { $_ })
            if (@($entry['created']).Count) { $created[$step.Key] = [string]@($entry['created'])[0] }
        }
        else { $entry['status'] = 'failed'; $entry['code'] = $reply.code; $entry['field'] = $reply.field; $entry['detail'] = $reply.detail; $entry['reply_lost'] = $false; $failure = $entry }
        $report.Add($entry)
    }
    $line = if ($null -eq $failure) { [ordered]@{ status = 'succeeded' } } else { [ordered]@{ status = 'failed'; code = $failure['code']; key = $failure['key']; operation_id = $failure['operation_id']; detail = $failure['detail'] } }
    foreach ($pair in $summary.GetEnumerator()) { $line[$pair.Key] = $pair.Value }
    $line['applied'] = $null -eq $failure
    $line['run'] = if ($null -eq $failure) { 'completed' } else { 'failed' }
    $line['gone'] = @()
    $line['report'] = $report.ToArray()
    if ($null -eq $failure) {
        $map = [ordered]@{}
        foreach ($pair in $realized.GetEnumerator()) { if (!($pair.Key -like 'doc:*' -and $regenerated -contains $pair.Key.Substring(4))) { $map[$pair.Key] = $pair.Value } }
        foreach ($pair in $created.GetEnumerator()) { $map["doc:$($pair.Key)"] = $pair.Value }
        $sorted = [ordered]@{}
        $names = [string[]]@($map.Keys)
        [Array]::Sort($names, [StringComparer]::Ordinal)
        foreach ($name in $names) { $sorted[$name] = $map[$name] }
        $line['realized'] = $sorted
    }
    return Line $line $(if ($null -eq $failure) { 0 } else { 1 })
}
