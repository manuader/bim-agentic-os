param([Parameter(Mandatory)][string]$Dotnet,[Parameter(Mandatory)][string]$CliDll)
$ErrorActionPreference='Stop'
foreach($endpoint in @($null,'bim-agentic-os-2025','bim-agentic-os-2025-0','arbitrary')) {
 $arguments=@($CliDll,'native','nonexistent-request.json')
 if($null -ne $endpoint) { $arguments += $endpoint }
 $output=& $Dotnet @arguments
 $code=$LASTEXITCODE
 $result=($output -join "`n") | ConvertFrom-Json
 if($code -ne 1 -or $result.code -ne 'explicit_process_pipe_required') { throw "Unsafe CLI endpoint was not rejected before I/O: $($result.code)" }
}
Write-Output 'PASS CLI requires explicit process pipe before file or network I/O'
