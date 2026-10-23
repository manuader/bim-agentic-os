$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot)
$adapter=Join-Path $repo 'scripts/baton.ps1'
$run=Join-Path $repo ('.bim/baton/tests-'+[guid]::NewGuid())
New-Item -ItemType Directory -Force $run | Out-Null
$count=0
function Assert($ok,$message) { if (!$ok) { throw $message }; $script:count++ }
function Call($action,$owner='one') { & $adapter -Action $action -RunDirectory $run -OwnerToken $owner -SessionId 'test-session' -Json }
function Reject($body) { $failed=$false; try { & $body | Out-Null } catch { $failed=$true }; Assert $failed 'Expected rejection' }
try {
 if (!(Test-Path $adapter)) { throw 'Expected adapter scripts/baton.ps1 is missing (intentional TDD red)' }
 $jobs=@(1,2 | ForEach-Object { Start-Job -ScriptBlock { param($a,$r,$o) try { & $a -Action Acquire -RunDirectory $r -OwnerToken $o -SessionId 'test-session' -Json; 'WON' } catch { 'LOST' } } -ArgumentList $adapter,$run,"owner$_" })
 $results=@($jobs | Receive-Job -Wait -AutoRemoveJob)
 Assert (@($results | Where-Object { $_ -eq 'WON' }).Count -eq 1) 'Exactly one process must acquire'
 $held=Get-Content "$run/LOCK" -Raw | ConvertFrom-Json
 $before=[IO.File]::ReadAllText("$run/LOCK")
 Reject { Call Heartbeat 'foreign' }; Reject { Call Release 'foreign' }
 Assert ([IO.File]::ReadAllText("$run/LOCK") -ceq $before) 'Foreign operations must preserve bytes'
 Call Heartbeat $held.owner_token | Out-Null
 Call Release $held.owner_token | Out-Null
 Assert ((Call Status | ConvertFrom-Json).state -eq 'FREE') 'Release frees lock'
 Call AcquireHeavy | Out-Null
 Reject { Call AcquireHeavy 'other' }; Reject { Call ReleaseHeavy 'other' }
 Call ReleaseHeavy | Out-Null
 Set-Content "$run/LOCK" '{broken'
 Reject { Call Acquire }
 Assert ((Get-Content "$run/LOCK" -Raw).Trim() -eq '{broken') 'Corrupt lease preserved'
 Set-Content "$run/LOCK" '{"state":"ALIEN"}'
 Reject { Call Acquire }
 $board=Join-Path $run 'tasks.md'
 $marks=@('✅','🟢','🔧','🔄','⏳','⛔','🔒','⏭️')
 $rows=for($i=0;$i -lt 8;$i++){ "| T$i | task | $($marks[$i]) |" }
 $text="# Board`n`n### Phase one`n| ID | Task | Status |`n|---|---|---|`n"+($rows -join "`n")+"`n`n## Progress`nold`n`n## Other`npreserve`n"
 [IO.File]::WriteAllText($board,$text)
 $p=& $adapter -Action Progress -BoardPath $board -Json | ConvertFrom-Json
 Assert ($p.total -eq 7 -and $p.done -eq 1 -and $p.counts.dropped -eq 1) 'Count all states, exclude dropped'
 Assert ($p.next_ready[0] -eq 'T4' -and $p.phases[0].total -eq 7) 'Ready and phase derived'
 & $adapter -Action Progress -BoardPath $board -Update -Json | Out-Null
 $updated=[IO.File]::ReadAllText($board)
 Assert ($updated.StartsWith($text.Split('## Progress')[0]) -and $updated.EndsWith("## Other`npreserve`n")) 'Update preserves other sections'
 Assert ($updated.Contains('<!-- computed by baton.ps1')) 'Computed marker'
 [IO.File]::WriteAllText($board,$text.Replace('| T1 |','| T0 |'))
 Reject { & $adapter -Action Progress -BoardPath $board -Json }
 [IO.File]::WriteAllText($board,$text.Replace('| T1 | task | 🟢 |','| T1 | task | unknown |'))
 Reject { & $adapter -Action Progress -BoardPath $board -Json }
 Reject { & $adapter -Action Acquire -RunDirectory $repo -OwnerToken x -SessionId x }
 "Baton assertions passed: $count"
} finally {
 if ($run.StartsWith((Join-Path $repo '.bim/baton/tests-'),[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $run -Recurse -Force }
}
