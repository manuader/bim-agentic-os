$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('fixture-launcher-'+[guid]::NewGuid())
New-Item -ItemType Directory $root | Out-Null
try {
 foreach($name in @('test.rte','Bim.Revit.dll','Bim.Core.dll','Revit.exe')) { Set-Content (Join-Path $root $name) 'test' }
 $run=Join-Path $root 'run'
 $arguments=@{RunDirectory=$run;TemplatePath=(Join-Path $root 'test.rte');AddinAssembly=(Join-Path $root 'Bim.Revit.dll');RevitExe=(Join-Path $root 'Revit.exe')}
 $rejected=$false
 try { & "$PSScriptRoot/launch-native-fixture.ps1" @arguments } catch { $rejected=$true }
 if(!$rejected -or (Test-Path $run)) { throw 'Missing confirmation must reject before creating outputs' }
 & "$PSScriptRoot/launch-native-fixture.ps1" @arguments -ConfirmIsolatedTestProcess
 if(!(Test-Path (Join-Path $run 'Bim.Agentic.Fixture.addin'))) { throw 'Manifest missing' }
 if((Get-Content (Join-Path $run 'Bim.Agentic.Fixture.addin') -Raw) -notmatch '<AllowLoadIntoExistingSession>false</AllowLoadIntoExistingSession>') { throw 'Manifest may load into production session' }
 if(Test-Path (Join-Path $run 'fixture-result.json')) { throw 'Preparation fabricated native evidence' }
 $rejected=$false
 try { & "$PSScriptRoot/launch-native-fixture.ps1" @arguments -ConfirmIsolatedTestProcess } catch { $rejected=$true }
 if(!$rejected) { throw 'Existing run directory accepted' }
 Write-Output 'PASS launcher preparation and overwrite guards; no process launched'
} finally {
 $cleanup=[IO.Path]::GetFullPath($root)
 $parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
 if(!$cleanup.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or !([IO.Path]::GetFileName($cleanup)).StartsWith('fixture-launcher-',[StringComparison]::Ordinal)) { throw 'Unsafe temporary cleanup target' }
 Remove-Item -LiteralPath $cleanup -Recurse -Force
}


