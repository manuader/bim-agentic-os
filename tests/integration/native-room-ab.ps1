[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$RunDirectory,
 [Parameter(Mandatory)][string]$DocumentPath,
 [Parameter(Mandatory)][string]$DocumentIdentity,
 [Parameter(Mandatory)][int]$ProcessId,
 [Parameter(Mandatory)][string]$PipeName,
 [Parameter(Mandatory)][string]$Dotnet,
 [Parameter(Mandatory)][string]$CliDll,
 [ValidateRange(1,900)][int]$TimeoutSeconds=180
)
$ErrorActionPreference='Stop'
foreach($path in @($RunDirectory,$DocumentPath,$Dotnet,$CliDll)){if(![IO.Path]::IsPathFullyQualified($path)){throw 'Absolute paths required'}}
if(!(Test-Path -LiteralPath $RunDirectory -PathType Container)){throw 'Existing opted-in run required'}
$options=Join-Path $RunDirectory 'native-room-options.json'
# The evaluator binds the saved bootstrap result/config, live PID and pipe server before dispatch.
$json=@{run_directory=$RunDirectory;document_path=$DocumentPath;document_identity=$DocumentIdentity;process_id=$ProcessId;pipe_name=$PipeName;timeout_seconds=$TimeoutSeconds}|ConvertTo-Json
$stream=[IO.File]::Open($options,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$writer=[IO.StreamWriter]::new($stream);$writer.Write($json);$writer.Dispose()}finally{$stream.Dispose()}
$output=& $Dotnet $CliDll evaluate revit $options
$code=$LASTEXITCODE
$text=$output -join "`n"
$null=$text|ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $RunDirectory 'native-room-report.json'),$text)
[Console]::Out.WriteLine($text)
# A dispatched timeout is UNKNOWN completion; this wrapper never retries.
exit $code
