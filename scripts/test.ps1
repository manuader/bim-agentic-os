$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/../.tools/dotnet/dotnet.exe" run --project "$PSScriptRoot/../tests/Bim.Tests"
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
