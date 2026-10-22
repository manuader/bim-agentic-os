$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/harness-common.ps1"
$root = Split-Path $PSScriptRoot
$bench = (Get-Content "$root/benchmarks/CoreBenchmark.cs" -Raw) -replace '(?m)^using [^\r\n]+;\r?\n', ''
Add-Type -TypeDefinition ((Get-HostCoreSource $root) + "`n" + $bench) -ReferencedAssemblies @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
[CoreBenchmark]::Run()
