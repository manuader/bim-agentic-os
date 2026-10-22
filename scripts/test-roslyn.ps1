# Host reference compilation is NOT a net8 target build or native Revit verification.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/harness-common.ps1"
$root = Split-Path $PSScriptRoot
$env:BIM_TEST_ROOT = Join-Path $root '.bim/tests'
New-Item -ItemType Directory -Force $env:BIM_TEST_ROOT | Out-Null
$tests = (Get-Content "$root/tests/Bim.Tests/Program.cs" -Raw) -replace '(?m)^using [^\r\n]+;\r?\n', ''
Add-Type -TypeDefinition ((Get-HostCoreSource $root) + "`npublic static class TestRunner { public static void Run() {`n" + $tests + "`n} }") -ReferencedAssemblies @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
[TestRunner]::Run()
