# P4-01 owner-run native probe (not CI): creates one section and one boxed 3D view through the catalogue on an isolated
# fixture copy and dumps requested vs observed values, so an owner session can confirm the exact-match readback in
# runtimes/revit-native/CommandsViews.cs. Run only against a saved BIM-Agentic-Test*.rvt under an enabled test root, in
# a child Revit process with the add-in loaded. Each request is sent once and never retried (a timeout means unknown
# completion). A readback mismatch comes back as transaction_failed with the rolled-back detail, which is dumped too.
# Exit 0: both views created and every requested value read back; 2: blocked, refused or mismatched.
param(
    [Parameter(Mandatory)][string]$DocumentPath,
    [Parameter(Mandatory)][string]$DocumentIdentity,
    [Parameter(Mandatory)][string]$PipeName,
    [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$evidence = $null
try {
    if ($PipeName -notmatch '\Abim-agentic-os-2025-[1-9][0-9]*\z') { throw 'explicit_process_pipe_required' }
    foreach ($path in @($DocumentPath, $Dotnet, $CliDll)) { if (![IO.Path]::IsPathFullyQualified($path) -or !(Test-Path -LiteralPath $path)) { throw 'absolute_existing_inputs_required' } }
    if (![IO.Path]::IsPathFullyQualified($EvidenceDirectory)) { throw 'absolute_evidence_directory_required' }
    if ([IO.Path]::GetFileName($DocumentPath) -notlike 'BIM-Agentic-Test*.rvt') { throw 'isolated_fixture_required' }
    if ($DocumentIdentity -notmatch '\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-[0-9a-fA-F]{8}\z') { throw 'document_unique_id_required' }
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $evidence = Join-Path $EvidenceDirectory ('views-probe-' + $stamp)
    New-Item -ItemType Directory -Path $evidence | Out-Null

    function Send-Command([string]$Key, [string]$Command, [hashtable]$CommandArgs) {
        $envelope = [ordered]@{ protocol_version = 1; operation_id = "p4-01-$stamp-$Key"; document = [ordered]@{ path = $DocumentPath; unique_id = $DocumentIdentity }; command = $Command; args = $CommandArgs }
        $file = Join-Path $evidence ($Key + '-request.json')
        $stream = [IO.File]::Open($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 20 -Compress)); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        $start = [Diagnostics.ProcessStartInfo]::new($Dotnet)
        foreach ($argument in @($CliDll, 'revit-command', $file, $PipeName)) { $start.ArgumentList.Add($argument) }
        $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.CreateNoWindow = $true
        $process = [Diagnostics.Process]::Start($start)
        try {
            $out = $process.StandardOutput.ReadToEndAsync(); $err = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill($true); throw "dispatched_timeout_unknown_completion: $Key" }
            $process.WaitForExit()
            $line = $out.GetAwaiter().GetResult().Trim()
            Set-Content -LiteralPath (Join-Path $evidence ($Key + '-response.json')) $line
            if ($err.GetAwaiter().GetResult()) { Set-Content -LiteralPath (Join-Path $evidence ($Key + '-stderr.txt')) $err.GetAwaiter().GetResult() }
            return ($line | ConvertFrom-Json)
        } finally { $process.Dispose() }
    }
    function Row([string]$Name, $Requested, $Observed) { [ordered]@{ value = $Name; requested = $Requested; observed = $Observed } }

    # Section along +X from (0,0) to (6000,0): looks toward +Y; ViewDirection (toward the viewer) is expected (0,-1,0).
    $sectionArgs = @{ line = @{ start = @(0, 0); end = @(6000, 0) }; depth_mm = 3000; bottom_elevation_mm = -500; top_elevation_mm = 4000 }
    $section = Send-Command 'section' 'view.create_section' $sectionArgs
    $boxArgs = @{ section_box = @{ min = @(0, 0, 0); max = @(8000, 6000, 3500) } }
    $threeD = Send-Command 'three-d' 'view.create_3d' $boxArgs

    $rows = @()
    if ($section.status -eq 'succeeded') {
        $s = $section.readback.view.section
        $rows += Row 'section.right_direction' @(1, 0, 0) $s.right_direction
        $rows += Row 'section.up_direction' @(0, 0, 1) $s.up_direction
        $rows += Row 'section.view_direction' @(0, -1, 0) $s.view_direction
        $rows += Row 'section.origin (y = cut plane offset)' @('any', 0, 'any') $s.origin
        $rows += Row 'section.crop x span along line' @(0, 6000) @($s.crop_min[0], $s.crop_max[0])
        $rows += Row 'section.crop z span' @(-500, 4000) @($s.crop_min[2], $s.crop_max[2])
        $rows += Row 'section.far_clip_mm' 3000 $s.far_clip_mm
        $rows += Row 'section.crop_box_active' $true $s.crop_box_active
    }
    if ($threeD.status -eq 'succeeded') {
        $t = $threeD.readback.view.three_d
        $rows += Row 'three_d.perspective' $false $t.perspective
        $rows += Row 'three_d.section_box_active' $true $t.section_box_active
        $rows += Row 'three_d.section_box.min' @(0, 0, 0) $t.section_box.min
        $rows += Row 'three_d.section_box.max' @(8000, 6000, 3500) $t.section_box.max
        $rows += Row 'three_d.forward_direction (informative)' 'isometric default' $t.forward_direction
    }
    $ok = $section.status -eq 'succeeded' -and $threeD.status -eq 'succeeded'
    $report = [ordered]@{
        status = $(if ($ok) { 'succeeded' } else { 'blocked_or_failed' })
        scope = 'P4-01 view.create_section and boxed view.create_3d readback on an isolated fixture copy'
        section = [ordered]@{ status = $section.status; code = $section.code; detail = $section.detail; created = $section.created }
        three_d = [ordered]@{ status = $threeD.status; code = $threeD.code; detail = $threeD.detail; created = $threeD.created }
        comparison = $rows
        readback_tolerance = 'CommandGeometry.Near (1e-3 mm + 1e-9 relative); directions 1e-6'
        verified_in_revit = $ok
        evidence = $evidence
    }
    $report | ConvertTo-Json -Depth 20 | Tee-Object -FilePath (Join-Path $evidence 'report.json')
    exit $(if ($ok) { 0 } else { 2 })
} catch { [ordered]@{ status = 'blocked_or_failed'; code = $_.Exception.Message; verified_in_revit = $false; evidence = $evidence } | ConvertTo-Json -Depth 10; exit 2 }
