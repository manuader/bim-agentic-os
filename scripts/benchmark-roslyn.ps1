$ErrorActionPreference = 'Stop'
$refs = @(Get-ChildItem "$PSHOME/ref" -Filter '*.dll' | ForEach-Object FullName)
$prefix = "using System; using System.IO; using System.Linq; using System.Collections.Generic;`n"
$core = (Get-Content "$PSScriptRoot/../core/Bim.Core/Model.cs" -Raw).Replace('namespace Bim.Core;', 'namespace Bim.Core {') + "`n}"
$bench = Get-Content "$PSScriptRoot/../benchmarks/CoreBenchmark.cs" -Raw
$bench = $bench -replace '(?m)^using .*;\r?\n', ''
Add-Type -TypeDefinition ($prefix + "using Bim.Core; using System.Diagnostics;`n" + $core + "`n" + $bench) -ReferencedAssemblies $refs
[CoreBenchmark]::Run()
