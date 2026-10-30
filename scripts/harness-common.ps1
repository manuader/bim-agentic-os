function Invoke-EvidenceCommand {
    param([string]$Name,[string]$Executable,[string[]]$Arguments,[string]$EvidenceDirectory,[switch]$Quiet)
    New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
    $log = Join-Path $EvidenceDirectory "$Name.log"
    [IO.File]::WriteAllText($log, "")
    $temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $EvidenceDirectory 'tmp'))
    New-Item -ItemType Directory -Force $temporaryDirectory | Out-Null
    $originalEnvironment = @{}
    foreach ($variable in 'TEMP','TMP','TMPDIR') {
        $originalEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process')
    }
    try {
        foreach ($variable in 'TEMP','TMP','TMPDIR') { [Environment]::SetEnvironmentVariable($variable, $temporaryDirectory, 'Process') }
        if ($Quiet) { & $Executable @Arguments 2>&1 | Out-File -FilePath $log } else { & $Executable @Arguments 2>&1 | Tee-Object -FilePath $log | Out-Host }
        $code = $LASTEXITCODE
    } finally {
        foreach ($variable in 'TEMP','TMP','TMPDIR') {
            if ($null -eq $originalEnvironment[$variable]) { Remove-Item -LiteralPath "Env:$variable" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($variable, $originalEnvironment[$variable], 'Process') }
        }
    }
    if ($code -ne 0) {
        # CI job logs are not always reachable; an annotation carries the tail of the evidence log.
        if ($env:GITHUB_ACTIONS -eq 'true') {
            $tail = (@(Get-Content -LiteralPath $log -Tail 12 -ErrorAction SilentlyContinue) -join ' | ') -replace '[\r\n]+', ' '
            if ($tail.Length -gt 3000) { $tail = $tail.Substring($tail.Length - 3000) }
            Write-Host ("::error title=${Name}::" + ($tail -replace '%', '%25'))
        }
        throw "$Name failed with exit code $code (see $log)"
    }
}
function Get-HostCoreSource {
    param([string]$Root)
    $prefix = "#nullable enable`nusing System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using System.Diagnostics; using System.Text; using System.Text.Json; using System.Security.Cryptography; using Bim.Core;`n"
    $sources = foreach ($file in Get-ChildItem "$Root/core/Bim.Core/*.cs" | Sort-Object Name) {
        $source = Get-Content $file.FullName -Raw
        $source = $source -replace '(?m)^using [^\r\n]+;\r?\n', ''
        if ($source -match 'namespace Bim.Core;') { $source = $source.Replace('namespace Bim.Core;', 'namespace Bim.Core {') + "`n}" }
        $source
    }
    return $prefix + ($sources -join "`n")
}

