param([string]$Backend='ifc', [string]$Dotnet, [string]$OutputDirectory, [string]$Python)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
# Build output is evidence, never mixed with the JSON protocol on stdout.
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('.bim/evaluation/'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))}
$previousTemp=@{}
foreach($name in @('TEMP','TMP','TMPDIR')){$previousTemp[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $tempRoot=Join-Path $root '.bim/tmp'
    New-Item -ItemType Directory -Force $tempRoot | Out-Null
    $env:TEMP=$tempRoot; $env:TMP=$tempRoot; $env:TMPDIR=$tempRoot
    if(!$Dotnet){
        $local=Join-Path $root ('.tools/dotnet/'+$(if($IsWindows){'dotnet.exe'}else{'dotnet'}))
        if(Test-Path $local){$Dotnet=$local}else{$Dotnet=(Get-Command dotnet -ErrorAction SilentlyContinue).Source}
    }
    if(!$Dotnet){throw 'NET8_SDK_MISSING: install .NET8 or pass -Dotnet'}
    & $Dotnet build "$root/interfaces/cli/Bim.Cli.csproj" --configuration Release --warnaserror *> (Join-Path $OutputDirectory 'build.log')
    if($LASTEXITCODE -ne 0){throw 'BUILD_BLOCKED: inspect build.log'}
    $cliArguments=@('evaluate',$Backend)
    if($Backend -eq 'ifc') {
        if(!$Python){foreach($candidate in @((Join-Path $root '.tools/ifc/Scripts/python.exe'),(Join-Path $root '.tools/ifc/bin/python'))){if(Test-Path -LiteralPath $candidate){$Python=$candidate;break}}}
        if($Python){$cliArguments=@('evaluate','ifc',$Python,(Join-Path $root 'runtimes/ifc/worker.py'),[IO.Path]::GetFullPath($OutputDirectory))}
    }
    $raw=& $Dotnet "$root/interfaces/cli/bin/Release/net8.0/Bim.Cli.dll" @cliArguments 2> (Join-Path $OutputDirectory 'runtime.stderr.log')
    $code=$LASTEXITCODE
    $json=$raw -join "`n"
    $null=$json | ConvertFrom-Json
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.json') -Value $json
    [Console]::Out.WriteLine($json)
    exit $code
} catch {
    $metrics=@{}
    foreach($name in @('intent_parse_ms','semantic_query_ms','solver_ms','diff_ms','runtime_wait_ms','revit_execute_ms','revit_regeneration_ms','native_readback_ms','validation_ms','documentation_ms','total_ms','llm_calls','model_elements_read','model_elements_written','transaction_count','unintended_element_changes')){$metrics[$name]=@{value=$null;reason='evaluator could not start'}}
    $json=@{verification_state='blocked';outcome='fail';scope='full Test A/B';backend=$Backend;backend_version='unavailable';source='launcher prerequisite inspection';evidence_origin='backend-prerequisite-inspection';product_acceptance=$false;timing_scope='no evaluator execution';counts=@{passed=0;failed=0;blocked=1;unsupported=0};metrics=$metrics;cases=@();gaps=@($_.Exception.Message)} | ConvertTo-Json -Depth 12
    try { if(Test-Path -LiteralPath $OutputDirectory -PathType Container){Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.json') -Value $json} } catch { }
    [Console]::Out.WriteLine($json)
    exit 2
}
 finally {
    foreach($name in $previousTemp.Keys){if($null -eq $previousTemp[$name]){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}else{[Environment]::SetEnvironmentVariable($name,$previousTemp[$name],'Process')}}
}
