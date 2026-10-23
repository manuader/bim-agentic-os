$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$app=Get-Content (Join-Path $root 'runtimes/revit-native/App.cs') -Raw
if ($app -notmatch 'FixtureBootstrap.Attach') { throw 'Missing explicitly opt-in Idling bootstrap hook' }
$launcher=Join-Path $PSScriptRoot 'launch-native-fixture.ps1'
if (!(Test-Path $launcher)) { throw 'Missing bounded child-only fixture launcher' }
Write-Output 'PASS bootstrap integration source contract (not native execution)'
