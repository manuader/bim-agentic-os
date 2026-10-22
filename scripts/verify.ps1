param([switch]$HostOnly, [string]$Dotnet)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/harness-common.ps1"
$root = Split-Path $PSScriptRoot
$evidence = Join-Path $root ('.bim/verification/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$pwsh = (Get-Process -Id $PID).Path
try {
    Invoke-EvidenceCommand harness $pwsh @('-NoProfile','-File',"$root/tests/harness/Program.ps1") $evidence
    Invoke-EvidenceCommand static $pwsh @('-NoProfile','-File',"$PSScriptRoot/harness-check.ps1") $evidence
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
        foreach ($project in @('core/Bim.Core','interfaces/cli','tests/Bim.Tests','tests/harness/Benchmark')) {
            Invoke-EvidenceCommand ('build-' + ($project -replace '/', '-')) $Dotnet @('build',"$root/$project",'--configuration','Release','--warnaserror') $evidence
        }
        Invoke-EvidenceCommand core-tests $Dotnet @('run','--project',"$root/tests/Bim.Tests",'--configuration','Release','--no-build') $evidence
        Invoke-EvidenceCommand benchmark $Dotnet @('run','--project',"$root/tests/harness/Benchmark",'--configuration','Release','--no-build') $evidence
    }
    'BLOCKED native integration: executable licensed Revit acceptance harness not implemented; no native verification claimed.' | Tee-Object -FilePath "$evidence/native-status.log"
    "PASS available gates; evidence: $evidence"
} catch { Write-Error $_; exit 1 }
