# Explicit native operation: run only against the same isolated revision-2 fixture as prior smoke.
param(
 [Parameter(Mandatory)][string]$DocumentPath,
 [Parameter(Mandatory)][string]$DocumentIdentity,
 [Parameter(Mandatory)][string]$PipeName,
 [Parameter(Mandatory)][string]$Dotnet,
 [Parameter(Mandatory)][string]$CliDll,
 [Parameter(Mandatory)][string]$PriorEvidenceDirectory
)
$ErrorActionPreference='Stop'
$evidence=$null
try {
 if($PipeName -notmatch '\Abim-agentic-os-2025-[1-9][0-9]*\z') { throw 'explicit_process_pipe_required' }
 foreach($path in @($DocumentPath,$PriorEvidenceDirectory,$Dotnet,$CliDll)) { if(![IO.Path]::IsPathFullyQualified($path) -or !(Test-Path -LiteralPath $path)) { throw 'absolute_existing_inputs_required' } }
 if([IO.Path]::GetFileName($DocumentPath) -notlike 'BIM-Agentic-Test*.rvt' -or [string]::IsNullOrWhiteSpace($DocumentIdentity)) { throw 'isolated_fixture_identity_required' }
 $requests=@(Get-ChildItem -LiteralPath $PriorEvidenceDirectory -Filter 'request-*.json' -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } | Where-Object { $_.Batch.IdempotencyKey -eq 'fixture-resize' })
 if($requests.Count -ne 1) { throw 'unique_prior_resize_request_required' }
 $resize=$requests[0]
 if($resize.DocumentPath -ne $DocumentPath -or $resize.Batch.DocumentIdentity -ne $DocumentIdentity) { throw 'prior_fixture_identity_conflict' }
 $responses=@(Get-Content -LiteralPath (Join-Path $PriorEvidenceDirectory 'responses.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.status -eq 'succeeded' -and $_.project.Revision -eq 2 -and $_.verification -eq 'native_geometry_readback' })
 if($responses.Count -ne 1) { throw 'unique_verified_revision_two_response_required' }
 $baseline=$responses[0]; $current=$baseline.project
 if($current.Id -ne $resize.Batch.After.Id -or @($current.Elements).Count -ne 5 -or @($current.Elements | Where-Object Kind -eq 'level').Count -ne 1 -or @($current.Elements | Where-Object Kind -eq 'wall').Count -ne 4) { throw 'expected_one_level_four_walls_required' }
 $evidence=Join-Path $PriorEvidenceDirectory ('rollback-'+[guid]::NewGuid())
 New-Item -ItemType Directory -Path $evidence | Out-Null
 function Save-Json($value,$name) { $path=Join-Path $evidence $name; $value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path; return $path }
 function Send-Request($request,$name) {
  $json=$request | ConvertTo-Json -Depth 100 -Compress
  Set-Content -LiteralPath (Join-Path $evidence ($name+'-request.json')) $json
  $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$PipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
  try {
   $pipe.Connect(5000)
   $writer=[IO.StreamWriter]::new($pipe); $writer.AutoFlush=$true
   $reader=[IO.StreamReader]::new($pipe)
   $writer.WriteLine($json)
   $line=$reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
   Set-Content -LiteralPath (Join-Path $evidence ($name+'-response.json')) $line
   return ($line | ConvertFrom-Json)
  } finally { $pipe.Dispose() }
 }
 $after=$current | ConvertTo-Json -Depth 100 | ConvertFrom-Json
 $level=@($after.Elements | Where-Object Kind -eq 'level')[0]
 $wall=@($after.Elements | Where-Object Kind -eq 'wall')[0]
 $level.GeometryMetres[0]=[double]$level.GeometryMetres[0]+0.1
 $wall.GeometryMetres[2]=[double]$wall.GeometryMetres[0]+0.000001
 $wall.GeometryMetres[3]=[double]$wall.GeometryMetres[1]
 $beforeFile=Save-Json $current 'before.json'; $afterFile=Save-Json $after 'after.json'
 $output=& $Dotnet $CliDll diff $beforeFile $afterFile
 if($LASTEXITCODE -ne 0) { throw "CLI diff failed: $output" }
 $plan=($output -join "`n") | ConvertFrom-Json
 Save-Json $plan 'plan.json' | Out-Null
 if(@($plan.Operations | Where-Object { $_.Element.Kind -eq 'level' -and $_.Action -eq 'update' }).Count -ne 1 -or @($plan.Operations | Where-Object { $_.Element.Kind -eq 'wall' -and $_.Action -eq 'update' }).Count -ne 1) { throw 'expected_level_then_wall_updates_required' }
 $forced=@{DocumentPath=$DocumentPath;Operation='batch';UniqueId=$null;Metres=@();Batch=@{DocumentIdentity=$DocumentIdentity;IdempotencyKey='fixture-forcedrollback';Before=$current;After=$after;Plan=$plan}}
 $failed=Send-Request $forced 'forced-rollback'
 if($failed.status -ne 'failed' -or $failed.code -notmatch '(?i)(curve.*(small|short|tolerance)|short.*curve)') { throw "forced native failure not observed: $($failed | ConvertTo-Json -Depth 100 -Compress)" }
 $retried=Send-Request $resize 'retry-original-resize'
 if($retried.status -ne 'succeeded' -or $retried.retry -ne $true -or $retried.verification -ne 'native_geometry_readback' -or $retried.project.Revision -ne 2) { throw "rollback readback/retry failed: $($retried | ConvertTo-Json -Depth 100 -Compress)" }
 foreach($property in $baseline.mapping.PSObject.Properties) { if($retried.mapping.($property.Name) -ne $property.Value) { throw 'rollback_changed_native_identity' } }
 if(@($retried.mapping.PSObject.Properties).Count -ne @($baseline.mapping.PSObject.Properties).Count) { throw 'rollback_mapping_count_changed' }
 @{status='succeeded';scope='forced_level_update_then_short_wall_failure_and_original_resize_retry';rollback_verified=$true;native_geometry_readback=$true;revision=2;failure=$failed;evidence=$evidence;save_reopen_verified=$false;documentation_verified=$false} | ConvertTo-Json -Depth 100
 exit 0
} catch { @{status='blocked_or_failed';code=$_.Exception.Message;rollback_verified=$false;evidence=$evidence} | ConvertTo-Json -Depth 100; exit 2 }

