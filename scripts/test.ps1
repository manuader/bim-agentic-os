param([switch]$HostOnly,[string]$Dotnet)
& "$PSScriptRoot/verify.ps1" -HostOnly:$HostOnly -Dotnet $Dotnet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
