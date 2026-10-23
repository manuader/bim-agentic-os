[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidateSet('Status','Acquire','Heartbeat','Release','HeavyStatus','AcquireHeavy','ReleaseHeavy','Progress')][string]$Action,
 [string]$RunDirectory, [string]$OwnerToken, [string]$SessionId,
 [string]$BoardPath, [switch]$Update, [switch]$Json
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot))
function SafePath([string]$path,[string]$parent) {
 if (![IO.Path]::IsPathFullyQualified($path)) { throw 'Absolute path required' }
 $full=[IO.Path]::GetFullPath($path)
 $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
 if (!$full.StartsWith($parent.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar,$comparison)) { throw 'Path is outside the permitted repository directory' }
 $cursor=$full
 while ($cursor.Length -gt $repo.Length) {
  if (Test-Path -LiteralPath $cursor) { $item=Get-Item -Force -LiteralPath $cursor; if ($item.LinkType) { throw 'Linked paths are not permitted' } }
  $cursor=Split-Path $cursor
 }
 return $full
}
function Emit($value) { if($Json){ $value | ConvertTo-Json -Depth 12 -Compress }else{ $value | ConvertTo-Json -Depth 12 } }
function AtomicWrite($path,$text) {
 $temp=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
 try { [IO.File]::WriteAllText($temp,$text,[Text.UTF8Encoding]::new($false)); [IO.File]::Move($temp,$path,$true) }
 finally { if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp} }
}
if($Action -eq 'Progress') {
 $board=SafePath $BoardPath $repo
 $source=[IO.File]::ReadAllText($board)
 $counts=[ordered]@{done=0;coded=0;fixing=0;flight=0;ready=0;blocked=0;owner=0;dropped=0}
 $marks=@{'✅'='done';'🟢'='coded';'🔧'='fixing';'🔄'='flight';'⏳'='ready';'⛔'='blocked';'🔒'='owner';'⏭️'='dropped';'⏭'='dropped'}
 $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 $phases=[ordered]@{}; $phase='(no phase)'; $ready=[Collections.Generic.List[string]]::new()
 $header=@(); $skip=$false
 foreach($line in ($source -split '\r?\n')) {
  if($line -match '^##\s+(.+)$'){ $skip=$Matches[1] -eq 'Progress'; $phase=$Matches[1]; $header=@(); continue }
  if($skip){continue}
  if($line -match '^###\s+(.+)$'){ $phase=$Matches[1]; $header=@(); continue }
  if($line -notmatch '^\s*\|'){ $header=@(); continue }
  $cells=@($line.Trim().Trim('|').Split('|') | ForEach-Object {$_.Trim()})
  if($cells -contains 'ID' -and $cells -contains 'Status'){ $header=$cells; continue }
  if(!$header.Count){continue}
  if(@($cells | Where-Object { $_ -notmatch '^:?-+:?$' }).Count -eq 0){continue}
  if($cells.Count -ne $header.Count){throw 'Malformed task table row'}
  $id=$cells[[array]::IndexOf($header,'ID')]; $status=$cells[[array]::IndexOf($header,'Status')]
  if(!$id -or !$ids.Add($id)){throw "Empty or duplicate task ID: $id"}
  if(!$marks.ContainsKey($status)){throw "Unknown task status: $status"}
  $state=$marks[$status]; $counts[$state]++
  if(!$phases.Contains($phase)){ $phases[$phase]=[ordered]@{name=$phase;done=0;total=0} }
  if($state -ne 'dropped'){ $phases[$phase].total++ }
  if($state -eq 'done'){ $phases[$phase].done++ }
  if($state -eq 'ready' -and $ready.Count -lt 8){$ready.Add($id)}
 }
 $total=$ids.Count-$counts.dropped
 $pct=if($total){[math]::Round(100*$counts.done/$total,1)}else{0}
 $result=[ordered]@{done=$counts.done;total=$total;percent=$pct;counts=$counts;phases=@($phases.Values);next_ready=@($ready.ToArray())}
 if($Update){
  $pattern='(?ms)^## Progress\r?\n.*?(?=^## |\z)'
  if([regex]::Matches($source,$pattern).Count -ne 1){throw 'Exactly one ## Progress section required'}
  $nl=if($source.Contains("`r`n")){"`r`n"}else{"`n"}
  $report="## Progress${nl}${nl}<!-- computed by baton.ps1; do not edit by hand -->${nl}Progress: $($counts.done)/$total done ($pct %)${nl}"
  foreach($p in $phases.Values){ $report+="- $($p.name): $($p.done)/$($p.total)${nl}" }
  $report+="Next ready: $($ready -join ', ')${nl}${nl}"
  $replacement=[Text.RegularExpressions.MatchEvaluator]{param($m) $report}
  AtomicWrite $board ([regex]::Replace($source,$pattern,$replacement))
 }
 Emit $result; return
}
$run=SafePath $RunDirectory (Join-Path $repo '.bim/baton')
if(!(Test-Path -LiteralPath $run -PathType Container)){throw 'Run directory must already exist'}
$heavy=$Action -in @('HeavyStatus','AcquireHeavy','ReleaseHeavy')
$name=if($heavy){'HEAVYLOCK'}else{'LOCK'}
$path=Join-Path $run $name
foreach($candidate in @($path,$path+'.guard')) { if(Test-Path -LiteralPath $candidate){ $null=SafePath $candidate $run } }
$guard=$null
$deadline=[DateTime]::UtcNow.AddSeconds(10)
while(!$guard){
 try{$guard=[IO.File]::Open($path+'.guard',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
 catch [IO.IOException]{if([DateTime]::UtcNow -gt $deadline){throw 'Lease guard busy'}; Start-Sleep -Milliseconds 30}
}
try {
 $lease=@{state='FREE'}
 if(Test-Path -LiteralPath $path){
  $lease=([IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable)
  if($lease -isnot [Collections.IDictionary] -or $lease.state -notin @('FREE','TAKEN')){throw 'Unknown or malformed lease; manual recovery required'}
  if($lease.state -eq 'TAKEN'){
   foreach($key in @('owner_token','session','taken_by','taken_at','last_heartbeat')){if(!$lease.Contains($key) -or !$lease[$key]){throw 'Incomplete held lease; manual recovery required'}}
   foreach($key in @('taken_at','last_heartbeat')){ $parsed=[DateTimeOffset]::MinValue; if(![DateTimeOffset]::TryParse($lease[$key],[ref]$parsed)){throw 'Malformed lease timestamp'} }
  }
 }
 if($Action -in @('Status','HeavyStatus')){Emit $lease; return}
 if(!$OwnerToken -or !$SessionId){throw 'OwnerToken and SessionId required'}
 if($Action -in @('Acquire','AcquireHeavy')){
  if($lease.state -ne 'FREE'){throw 'Lease held; no automatic takeover'}
  $now=[DateTimeOffset]::UtcNow.ToString('o')
  $lease=@{state='TAKEN';taken_by=$SessionId;session=$SessionId;owner_token=$OwnerToken;taken_at=$now;last_heartbeat=$now;platform='codex'}
 }else{
  if($lease.state -ne 'TAKEN' -or $lease.owner_token -cne $OwnerToken -or $lease.session -cne $SessionId){throw 'Lease ownership mismatch'}
  if($Action -eq 'Heartbeat'){ $lease.last_heartbeat=[DateTimeOffset]::UtcNow.ToString('o') }else{ $lease=@{state='FREE'} }
 }
 AtomicWrite $path ($lease | ConvertTo-Json -Depth 8)
 Emit $lease
} finally { $guard.Dispose() }
