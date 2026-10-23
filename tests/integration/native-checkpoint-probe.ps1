# Save/reopen probe ONLY for a child explicitly started with BIM_AGENTIC_TEST_CONFIG.
# Mandatory PID + pipe bind the request to that same dedicated process.
param(
 [Parameter(Mandatory)][string]$DocumentPath,
 [Parameter(Mandatory)][string]$DocumentIdentity,
 [Parameter(Mandatory)][int]$ProcessId,
 [Parameter(Mandatory)][string]$PipeName,
 [Parameter(Mandatory)][string]$PriorEvidenceDirectory,
 [Parameter(Mandatory)][string]$RunDirectory
)
$ErrorActionPreference='Stop'
$evidence=$null
$checkpointSent=$false
$checkpointResponseReceived=$false
try {
 if($ProcessId -le 0 -or $PipeName -cne ('bim-agentic-os-2025-'+$ProcessId)) { throw 'explicit_matching_process_and_pipe_required' }
 foreach($path in @($DocumentPath,$PriorEvidenceDirectory,$RunDirectory)) { if(![IO.Path]::IsPathFullyQualified($path) -or !(Test-Path -LiteralPath $path)) { throw 'absolute_existing_inputs_required' } }
 if([IO.Path]::GetFileName($DocumentPath) -notlike 'BIM-Agentic-Test*.rvt' -or [string]::IsNullOrWhiteSpace($DocumentIdentity)) { throw 'isolated_fixture_identity_required' }
 if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($DocumentPath)) -ne [IO.Path]::GetFullPath($RunDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'fixture_must_be_direct_run_child' }
 $requests=@(Get-ChildItem -LiteralPath $PriorEvidenceDirectory -Filter 'request-*.json' -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } | Where-Object { $_.Batch.IdempotencyKey -eq 'fixture-resize' })
 if($requests.Count -ne 1) { throw 'unique_prior_resize_request_required' }
 $resize=$requests[0]
 if($resize.DocumentPath -ne $DocumentPath -or $resize.Batch.DocumentIdentity -ne $DocumentIdentity) { throw 'prior_fixture_identity_conflict' }
 $responses=@(Get-Content -LiteralPath (Join-Path $PriorEvidenceDirectory 'responses.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.status -eq 'succeeded' -and $_.project.Revision -eq 2 -and $_.verification -eq 'native_geometry_readback' })
 if($responses.Count -ne 1) { throw 'unique_verified_revision_two_response_required' }
 $baseline=$responses[0]
 if($baseline.project.Id -ne $resize.Batch.After.Id -or @($baseline.project.Elements).Count -ne 5 -or @($baseline.project.Elements | Where-Object Kind -eq 'level').Count -ne 1 -or @($baseline.project.Elements | Where-Object Kind -eq 'wall').Count -ne 4) { throw 'expected_one_level_four_walls_required' }
 $evidence=Join-Path $PriorEvidenceDirectory ('checkpoint-'+[guid]::NewGuid())
 New-Item -ItemType Directory -Path $evidence | Out-Null
 $parking=Join-Path $RunDirectory ('BIM-Agentic-Test-parking-'+[guid]::NewGuid()+'.rvt')
 if(Test-Path -LiteralPath $parking) { throw 'parking_already_exists' }
 function Send-Request($request,$name) {
  $json=$request | ConvertTo-Json -Depth 100 -Compress
  Set-Content -LiteralPath (Join-Path $evidence ($name+'-request.json')) $json
  $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$PipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
  try {
   $pipe.Connect(5000)
   $writer=[IO.StreamWriter]::new($pipe); $writer.AutoFlush=$true
   $reader=[IO.StreamReader]::new($pipe)
   $writer.WriteLine($json)
   if($name -eq 'checkpoint') { $script:checkpointSent=$true }
   $line=$reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(180)).GetAwaiter().GetResult()
   if($name -eq 'checkpoint') { $script:checkpointResponseReceived=$true }
   Set-Content -LiteralPath (Join-Path $evidence ($name+'-response.json')) $line
   return ($line | ConvertFrom-Json)
  } finally { $pipe.Dispose() }
 }
 function Assert-Mapping($actual) {
  if(@($actual.PSObject.Properties).Count -ne @($baseline.mapping.PSObject.Properties).Count) { throw 'checkpoint_mapping_count_changed' }
  foreach($property in $baseline.mapping.PSObject.Properties) { if($actual.($property.Name) -cne $property.Value) { throw 'checkpoint_changed_native_identity' } }
 }
 $request=@{DocumentPath=$DocumentPath;Operation='fixture.checkpoint';UniqueId=$null;Metres=@();Batch=$resize.Batch;Checkpoint=@{ParkingPath=$parking;ExpectedRevision=2;ExpectedMapping=$baseline.mapping}}
 $timer=[Diagnostics.Stopwatch]::StartNew()
 $result=Send-Request $request 'checkpoint'
 $timer.Stop()
 if($result.status -ne 'succeeded' -or $result.verification -ne 'saved_reopened_native_geometry_readback' -or $result.saved -ne $true -or $result.fixtureClosed -ne $true -or $result.reopened -ne $true -or $result.parkingClosed -ne $true -or $result.project.Revision -ne 2) { throw "checkpoint did not verify: $($result | ConvertTo-Json -Depth 100 -Compress)" }
 if($result.documentPath -ne $DocumentPath -or $result.documentUniqueId -cne $DocumentIdentity -or $result.receipt.key -cne 'fixture-resize' -or $result.receipt.hash -cne $resize.Batch.Plan.AfterHash -or $result.documentation -ne 'unsupported') { throw 'checkpoint_identity_or_receipt_conflict' }
 Assert-Mapping $result.mapping
 $retried=Send-Request $resize 'retry-original-resize'
 if($retried.status -ne 'succeeded' -or $retried.retry -ne $true -or $retried.verification -ne 'native_geometry_readback' -or $retried.project.Revision -ne 2) { throw "reopened retry failed: $($retried | ConvertTo-Json -Depth 100 -Compress)" }
 Assert-Mapping $retried.mapping
 $summary=@{status='succeeded';scope='saved_fixture_reopen_and_original_resize_retry';save_reopen_verified=$true;native_geometry_readback=$true;retry_verified=$true;revision=2;documentPath=$DocumentPath;parkingPath=$parking;checkpoint_roundtrip_ms=$timer.Elapsed.TotalMilliseconds;timing_scope='one checkpoint Send roundtrip including save, parking, close, reopen and readback; excludes initial planning/patch and all documentation; not complete validated delivery';documentation_verified=$false;evidence=$evidence}
 $summary | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json')
 $summary | ConvertTo-Json -Depth 100
 exit 0
} catch {
 $summary=@{status='blocked_or_failed';code=$_.Exception.Message;save_reopen_verified=$false;checkpoint_completion_unknown=($checkpointSent -and !$checkpointResponseReceived);note='A timed-out dispatched checkpoint may still be running; do not automatically retry and do not assume rollback';evidence=$evidence}
 if($evidence) { $summary | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') }
 $summary | ConvertTo-Json -Depth 100
 exit 2
}
