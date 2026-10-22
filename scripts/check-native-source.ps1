# Source/API signature check against host references only. NEVER install output into Revit.
param([string]$RevitInstallDirectory = (Join-Path $env:ProgramFiles 'Autodesk/Revit 2025'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$directory = Join-Path $root ('.bim/native-source-check/' + [guid]::NewGuid())
New-Item -ItemType Directory -Force $directory | Out-Null
$globals = Join-Path $directory 'GlobalUsings.cs'
'global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;' | Set-Content $globals
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
foreach ($name in 'RevitAPI.dll','RevitAPIUI.dll') {
    $assembly = Join-Path $RevitInstallDirectory $name
    if (!(Test-Path $assembly)) { throw "Native source check blocked: missing $assembly" }
    $refs += $assembly
}
$sources = @($globals) + @(Get-ChildItem "$root/core/Bim.Core/*.cs", "$root/runtimes/revit-native/*.cs" | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $refs -OutputAssembly (Join-Path $directory 'native-host-check.dll') -CompilerOptions @('/nowarn:1701','/nullable:enable')
'PASS native source/API signatures against installed Revit DLLs; host references only. net8 native build, add-in load and native execution UNVERIFIED.'
