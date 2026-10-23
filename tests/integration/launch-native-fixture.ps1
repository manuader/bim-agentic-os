[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$RunDirectory,
 [Parameter(Mandatory)][string]$TemplatePath,
 [Parameter(Mandatory)][string]$AddinAssembly,
 [string]$RevitExe='C:\Program Files\Autodesk\Revit 2025\Revit.exe',
 [ValidateRange(10,900)][int]$TimeoutSeconds=180,
 [switch]$Launch,
 [switch]$ConfirmIsolatedTestProcess
)
$ErrorActionPreference='Stop'
if (!$ConfirmIsolatedTestProcess) { throw 'Explicit isolated test process confirmation required' }
foreach ($path in @($RunDirectory,$TemplatePath,$AddinAssembly,$RevitExe)) {
 if (![IO.Path]::IsPathFullyQualified($path)) { throw 'Absolute paths required' }
}
if (Test-Path -LiteralPath $RunDirectory) { throw 'Run directory must be new; outputs are never overwritten' }
foreach ($path in @($TemplatePath,$AddinAssembly,$RevitExe)) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required input missing: $path" } }
if ([IO.Path]::GetExtension($TemplatePath) -ne '.rte') { throw 'Revit template required' }
New-Item -ItemType Directory -Path $RunDirectory | Out-Null
$package=New-Item -ItemType Directory -Path (Join-Path $RunDirectory 'addin')
Copy-Item -LiteralPath $AddinAssembly -Destination $package.FullName
$core=Join-Path (Split-Path $AddinAssembly) 'Bim.Core.dll'
if (!(Test-Path -LiteralPath $core)) { throw 'Bim.Core.dll missing beside add-in assembly' }
Copy-Item -LiteralPath $core -Destination $package.FullName
$assembly=Join-Path $package.FullName ([IO.Path]::GetFileName($AddinAssembly))
$escaped=[Security.SecurityElement]::Escape($assembly)
@"
<RevitAddIns><AddIn Type="Application"><Name>BIM Agentic OS Fixture</Name><Assembly>$escaped</Assembly><AddInId>A38C9DBF-CE60-48BA-98AD-4F131C0F15D3</AddInId><FullClassName>Bim.Revit.App</FullClassName><VendorId>BIMOS</VendorId><AllowLoadIntoExistingSession>false</AllowLoadIntoExistingSession></AddIn></RevitAddIns>
"@ | Set-Content -LiteralPath (Join-Path $RunDirectory 'Bim.Agentic.Fixture.addin')
$configPath=Join-Path $RunDirectory 'fixture-config.json'
$resultPath=Join-Path $RunDirectory 'fixture-result.json'
@{ConfirmIsolatedTestProcess=$true;RunDirectory=$RunDirectory;TemplatePath=$TemplatePath;FixturePath=(Join-Path $RunDirectory 'BIM-Agentic-Test.rvt');ResultPath=$resultPath} | ConvertTo-Json | Set-Content -LiteralPath $configPath
if (!$Launch) { Write-Output "Prepared only. Install the staged manifest explicitly before launch: $RunDirectory"; return }
$start=[Diagnostics.ProcessStartInfo]::new($RevitExe)
$start.UseShellExecute=$false
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$start.Environment['BIM_AGENTIC_TEST_CONFIG']=$configPath
$process=[Diagnostics.Process]::Start($start)
$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while ([DateTime]::UtcNow -lt $deadline -and !(Test-Path -LiteralPath $resultPath)) {
 if ($process.HasExited) { throw "Revit child exited without fixture evidence; PID $($process.Id)" }
 Start-Sleep -Milliseconds 500
}
if (!(Test-Path -LiteralPath $resultPath)) { throw "Timed out waiting for fixture evidence; child PID $($process.Id) left running. Check add-in loading or dialogs." }
$result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($result.status -ne 'succeeded') { throw "Fixture bootstrap failed: $($result.code)" }
if ($result.processId -ne $process.Id) { throw 'Fixture evidence PID mismatch' }
if ($result.pipeName -ne ("bim-agentic-os-2025-"+$result.processId)) { throw 'Fixture evidence pipe/PID mismatch' }
if (!(Test-Path -LiteralPath $result.documentPath -PathType Leaf)) { throw 'Fixture evidence references missing RVT' }
$result | ConvertTo-Json


