$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('smoke-cli-regression-'+[guid]::NewGuid())
New-Item -ItemType Directory -Path $root | Out-Null
try {
 $fixture=Join-Path $root 'BIM-Agentic-Test.rvt'
 $fake=Join-Path $root 'fake-cli.ps1'
 Set-Content -LiteralPath $fixture 'not a real RVT; no native operation permitted'
 Set-Content -LiteralPath $fake "Write-Output 'SMOKE_CLI_SENTINEL'; exit 17"
 $pwsh=(Get-Process -Id $PID).Path
 $output=& $pwsh -NoProfile -File "$PSScriptRoot/native-batch-smoke.ps1" -DocumentPath $fixture -DocumentIdentity 'fake-fixture-identity' -Dotnet $pwsh -CliDll $fake
 $code=$LASTEXITCODE
 $result=($output -join "`n") | ConvertFrom-Json
 if($code -ne 2) { throw "Expected smoke exit 2; got $code" }
 if($result.code -ne 'CLI failed: SMOKE_CLI_SENTINEL') { throw "CLI dispatch did not reach sentinel: $($result.code)" }
 if($result.native_verified -ne $false) { throw 'CLI failure must leave native verification false' }
 Write-Output 'PASS smoke CLI dispatch reaches executable and stops before native pipe'
} finally {
 $cleanup=[IO.Path]::GetFullPath($root)
 $parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
 if(!$cleanup.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or !([IO.Path]::GetFileName($cleanup)).StartsWith('smoke-cli-regression-',[StringComparison]::Ordinal)) { throw 'Unsafe temporary cleanup target' }
 Remove-Item -LiteralPath $cleanup -Recurse -Force
}
