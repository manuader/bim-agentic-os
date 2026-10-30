param([switch]$HostOnly, [string]$Dotnet)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/harness-common.ps1"
$root = Split-Path $PSScriptRoot
$evidence = Join-Path $root ('.bim/verification/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$pwsh = (Get-Process -Id $PID).Path
try {
    Invoke-EvidenceCommand harness $pwsh @('-NoProfile','-File',"$root/tests/harness/Program.ps1") $evidence
    Invoke-EvidenceCommand static $pwsh @('-NoProfile','-File',"$PSScriptRoot/harness-check.ps1") $evidence
    Invoke-EvidenceCommand baton-coordination $pwsh @('-NoProfile','-File',"$root/tests/baton/verify.ps1") $evidence
    Invoke-EvidenceCommand native-smoke-cli $pwsh @('-NoProfile','-File',"$root/tests/integration/test-native-smoke-cli.ps1") $evidence
    Invoke-EvidenceCommand fixture-launcher $pwsh @('-NoProfile','-File',"$root/tests/integration/test-fixture-launcher.ps1") $evidence
    if ($HostOnly) {
        'MODE host-only: net8 target build and native execution UNVERIFIED'
        Invoke-EvidenceCommand core-tests $pwsh @('-NoProfile','-File',"$PSScriptRoot/test-roslyn.ps1") $evidence
        Invoke-EvidenceCommand benchmark $pwsh @('-NoProfile','-File',"$PSScriptRoot/benchmark-roslyn.ps1") $evidence
    } else {
        if (!$Dotnet) {
            $local = Join-Path $root '.tools/dotnet/dotnet.exe'
            if (Test-Path $local) { $Dotnet = $local } else { $Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
        }
        if (!$Dotnet) { throw 'net8 SDK missing. Install .NET 8 SDK or explicitly use -HostOnly; target build remains unverified.' }
        Invoke-EvidenceCommand sdk $Dotnet @('--info') $evidence
        foreach ($project in @('core/Bim.Core','evaluation/Bim.Evaluation','adapters/ifc/Bim.Ifc.Compiler','interfaces/cli','tests/ifc-compiler','tests/typed-geometry','tests/evaluation','tests/Bim.Tests','tests/reconcile','tests/harness/Benchmark','tests/integration/FixtureGuard.Tests','tests/integration/CheckpointGuard.Tests','tests/integration/NativeRoomGuard.Tests','tests/integration/NativeReconcileGuard.Tests','tests/native-room','interfaces/mcp','tests/mcp','tests/cli','tests/revit-commands','tests/integration/RevitCommandGuard.Tests')) {
            Invoke-EvidenceCommand ('build-' + ($project -replace '/', '-')) $Dotnet @('build',"$root/$project",'--configuration','Release','--warnaserror') $evidence
        }
        Invoke-EvidenceCommand evaluator-unit-health $Dotnet @('run','--project',"$root/tests/evaluation",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand ifc-compiler-contract $Dotnet @('run','--project',"$root/tests/ifc-compiler",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand typed-geometry-contract $Dotnet @('run','--project',"$root/tests/typed-geometry",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand evaluator-cli-health $pwsh @('-NoProfile','-File',"$root/tests/evaluation/test-evaluate-cli.ps1",'-Dotnet',$Dotnet) $evidence
        Invoke-EvidenceCommand native-cli-endpoint $pwsh @('-NoProfile','-File',"$root/tests/integration/test-native-cli-endpoint.ps1",'-Dotnet',$Dotnet,'-CliDll',"$root/interfaces/cli/bin/Release/net8.0/Bim.Cli.dll") $evidence
        Invoke-EvidenceCommand native-room-contract $Dotnet @('run','--project',"$root/tests/native-room",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand mcp-contract $Dotnet @('run','--project',"$root/tests/mcp",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand native-room-guards $Dotnet @('run','--project',"$root/tests/integration/NativeRoomGuard.Tests",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand native-reconcile-guards $Dotnet @('run','--project',"$root/tests/integration/NativeReconcileGuard.Tests",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand checkpoint-guards $Dotnet @('run','--project',"$root/tests/integration/CheckpointGuard.Tests",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand fixture-guards $Dotnet @('run','--project',"$root/tests/integration/FixtureGuard.Tests",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand core-tests $Dotnet @('run','--project',"$root/tests/Bim.Tests",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand reconcile-tests $Dotnet @('run','--project',"$root/tests/reconcile",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand benchmark $Dotnet @('run','--project',"$root/tests/harness/Benchmark",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand cli-inputs $Dotnet @('run','--project',"$root/tests/cli",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand revit-commands-contract $Dotnet @('run','--project',"$root/tests/revit-commands",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand revit-command-guards $Dotnet @('run','--project',"$root/tests/integration/RevitCommandGuard.Tests",'--configuration','Release','--no-build') $evidence
    }
    'Native acceptance is not executed by this engineering gate. Run tests/integration/native-room-ab.ps1 against a newly opted-in isolated licensed Revit fixture; require its actual report for any native acceptance claim.' | Tee-Object -FilePath "$evidence/native-status.log"
    "PASS available gates; evidence: $evidence"
} catch { Write-Error $_; exit 1 }

