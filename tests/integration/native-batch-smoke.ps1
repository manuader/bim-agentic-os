# Run ONLY with a dedicated saved BIM-Agentic-Test*.rvt fixture and add-in loaded.
# This is a native integration test: absent prerequisites are BLOCKED (exit 2).
param(
    [Parameter(Mandatory=$true)][string]$DocumentPath,
    [Parameter(Mandatory=$true)][string]$DocumentIdentity,
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [Parameter(Mandatory=$true)][string]$CliDll
)
$ErrorActionPreference='Stop'
try {
    if(!(Test-Path -LiteralPath $DocumentPath -PathType Leaf) -or [IO.Path]::GetFileNameWithoutExtension($DocumentPath) -notlike 'BIM-Agentic-Test*') { throw 'blocked: saved isolated BIM-Agentic-Test fixture required' }
    if(!(Test-Path -LiteralPath $Dotnet -PathType Leaf) -or !(Test-Path -LiteralPath $CliDll -PathType Leaf)) { throw 'blocked: built net8 CLI and dotnet runtime required' }
    if([string]::IsNullOrWhiteSpace($DocumentIdentity)) { throw 'blocked: ProjectInformation.UniqueId required' }
    $caseRoot=Join-Path $PSScriptRoot ('../../.bim/native-smoke-'+[guid]::NewGuid())
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    function SaveJson($Value,$Name) { $path=Join-Path $caseRoot $Name; $Value | ConvertTo-Json -Depth 100 | Set-Content $path; return $path }
    function Cli([string[]]$Arguments) { $output=& $Dotnet $CliDll @Arguments; if($LASTEXITCODE -ne 0){throw "CLI failed: $output"}; return ($output | ConvertFrom-Json) }
    function Send($Before,$After,$Plan,$Key) {
        $request=@{DocumentPath=$DocumentPath;Operation='batch';UniqueId=$null;Metres=@();Batch=@{DocumentIdentity=$DocumentIdentity;IdempotencyKey=$Key;Before=$Before;After=$After;Plan=$Plan}}
        $json=$request | ConvertTo-Json -Depth 100 -Compress
        $json | Set-Content (Join-Path $caseRoot ('request-'+[guid]::NewGuid()+'.json'))
        $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','bim-agentic-os-2025',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $pipe.Connect(5000)
            $writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
            $reader=[IO.StreamReader]::new($pipe)
            $writer.WriteLine($json)
            $line=$reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            $line | Add-Content (Join-Path $caseRoot 'responses.jsonl')
            return ($line | ConvertFrom-Json)
        } finally { $pipe.Dispose() }
    }
    function Assert($Condition,$Message) { if(!$Condition){throw $Message} }
    $projectId=[guid]::NewGuid().ToString();$roomId=[guid]::NewGuid().ToString()
    $intent=@{RoomId=$roomId;WidthMetres=5.0;DepthMetres=4.0;HeightMetres=3.0;ThicknessMetres=0.2;AnchorXMetres=0.0;AnchorYMetres=0.0;Units='m';Provenance=@{Source='native integration fixture';Approval='test only'}}
    $intentFile=SaveJson $intent 'intent.json'
    $target=Cli -Arguments @('room',$projectId,'0',$intentFile)
    # Explicit primitive subset. Full room space and documentation remain unsupported.
    $target.Elements=@($target.Elements | Where-Object Kind -In @('level','wall'))
    $before=@{Id=$projectId;Revision=0;Elements=@()}
    $beforeFile=SaveJson $before 'before.json';$targetFile=SaveJson $target 'target.json'
    $plan=Cli -Arguments @('diff',$beforeFile,$targetFile)
    $first=Send $before $target $plan 'fixture-create'
    Assert ($first.status -eq 'succeeded' -and $first.verification -eq 'native_geometry_readback') 'native creation did not pass geometry readback; fixture requires an existing basic 200mm wall type'
    $retry=Send $before $target $plan 'fixture-create'
    Assert ($retry.status -eq 'succeeded' -and $retry.retry -eq $true -and $retry.project.Revision -eq 1) 'persistent retry failed'
    Assert (($first.mapping | ConvertTo-Json -Compress) -eq ($retry.mapping | ConvertTo-Json -Compress)) 'retry changed UniqueIds'
    $intent.WidthMetres=5.4;$intentFile=SaveJson $intent 'resize-intent.json'
    $resized=Cli -Arguments @('room',$projectId,'1',$intentFile);$resized.Elements=@($resized.Elements | Where-Object Kind -In @('level','wall'))
    $currentFile=SaveJson $first.project 'current.json';$resizedFile=SaveJson $resized 'resized.json'
    $resizePlan=Cli -Arguments @('diff',$currentFile,$resizedFile)
    Assert ($resizePlan.Operations.Count -eq 3) 'primitive width correction must update exactly three walls'
    $revision=Send $first.project $resized $resizePlan 'fixture-resize'
    Assert ($revision.status -eq 'succeeded' -and $revision.project.Revision -eq 2) 'native revision failed'
    Assert (($first.mapping | ConvertTo-Json -Compress) -eq ($revision.mapping | ConvertTo-Json -Compress)) 'resize changed native identities'
    $resizePlan.Operations=@()
    $malformed=Send $first.project $resized $resizePlan 'fixture-tampered'
    Assert ($malformed.status -eq 'blocked' -and $malformed.issues.Code -contains 'plan_mismatch') 'tampered native plan was not rejected'
    @{status='succeeded';scope='level_and_walls_only';native_geometry_readback=$true;rollback_verified=$false;save_reopen_verified=$false;documentation_verified=$false;evidence=$caseRoot} | ConvertTo-Json
    exit 0
} catch { @{status='blocked_or_failed';code=$_.Exception.Message;native_verified=$false} | ConvertTo-Json; exit 2 }
