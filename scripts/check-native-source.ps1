# Source/API signature check only. Output targets the PowerShell host runtime.
# NEVER install this host-check assembly into Revit 2025.
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force "$PSScriptRoot/../.bim" | Out-Null
$refs = @(Get-ChildItem "$PSHOME/ref" -Filter '*.dll' | ForEach-Object FullName) + @('C:\Program Files\Autodesk\Revit 2025\RevitAPI.dll','C:\Program Files\Autodesk\Revit 2025\RevitAPIUI.dll')
$prefix = "#nullable enable`nusing System; using System.IO; using System.Linq; using System.Threading; using System.Threading.Tasks;`n"
$output = Join-Path $PSScriptRoot "../.bim/native-source-check-$([Guid]::NewGuid()).dll"
Add-Type -TypeDefinition ($prefix + (Get-Content "$PSScriptRoot/../runtimes/revit-native/App.cs" -Raw)) -ReferencedAssemblies $refs -OutputAssembly $output -CompilerOptions '/nowarn:1701'
'PASS native source/API signatures against installed 25.2 DLL; host runtime references, net8 build and native execution UNVERIFIED'
