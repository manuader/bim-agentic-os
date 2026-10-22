# Fallback compiler in bundled PowerShell. This verifies source against the host
# reference assemblies, NOT the net8 SDK target or Revit load compatibility.
$ErrorActionPreference = 'Stop'
$env:BIM_TEST_ROOT = Join-Path $PSScriptRoot '../.bim/tests'
New-Item -ItemType Directory -Force $env:BIM_TEST_ROOT | Out-Null
$refs = @(Get-ChildItem "$PSHOME/ref" -Filter '*.dll' | ForEach-Object FullName)
$prefix = "using System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;`n"
$core = Get-Content "$PSScriptRoot/../core/Bim.Core/Model.cs" -Raw
$tests = Get-Content "$PSScriptRoot/../tests/Bim.Tests/Program.cs" -Raw
$tests = $tests.Replace('using Bim.Core;', '').Replace('using System.Diagnostics;', '')
$core = $core.Replace('namespace Bim.Core;', 'namespace Bim.Core {') + "`n}"
Add-Type -TypeDefinition ($prefix + "using Bim.Core; using System.Diagnostics;`n" + $core + "`npublic static class TestRunner { public static void Run() {`n" + $tests + "`n} }") -ReferencedAssemblies $refs
[TestRunner]::Run()
