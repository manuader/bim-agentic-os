param([string]$Python, [string]$Dotnet, [int]$Samples = 10, [string]$OutputDirectory, [switch]$Notice)
# P01: same +400 mm correction on the real IFC worker, incremental compiled plan vs full rebuild.
# Order: Core packet (C#) -> equivalence tests (must pass) -> timed samples -> JSON report.
$ErrorActionPreference = 'Stop'
function Invoke-Annotated([string]$Title, [string]$Executable, [object[]]$Arguments) {
    # Full CI logs are not retrievable by agents: surface a failing command's tail as an error annotation.
    $ErrorActionPreference = 'Continue'
    $output = & $Executable @Arguments 2>&1 | ForEach-Object { [string]$_ }
    $code = $LASTEXITCODE
    $output | ForEach-Object { $_ }
    if ($code -ne 0) {
        $text = ($output -join "`n")
        if ($text.Length -gt 3500) { $text = $text.Substring($text.Length - 3500) }
        $escaped = $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Host "::error title=${Title}::${escaped}"
        throw "${Title} failed with exit code $code"
    }
}
$root = Split-Path $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root '.bim/rebuild-benchmark' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if (!$Python) {
    foreach ($candidate in @((Join-Path $root '.tools/ifc/Scripts/python.exe'), (Join-Path $root '.tools/ifc/bin/python'))) {
        if (Test-Path -LiteralPath $candidate) { $Python = $candidate; break }
    }
}
if (!$Python) { throw 'IFC_PYTHON_MISSING: create .tools/ifc from requirements/ifc.txt or pass -Python' }
if (!$Dotnet) {
    $local = Join-Path $root ('.tools/dotnet/' + $(if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }))
    if (Test-Path $local) { $Dotnet = $local } else { $Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
}
if (!$Dotnet) { throw 'NET8_SDK_MISSING: install .NET 8 or pass -Dotnet' }
$dotnetVersion = (& $Dotnet --version).Trim()
$packet = Join-Path $OutputDirectory 'compiled-5.json'
Invoke-Annotated 'rebuild-packet' $Dotnet @('run', '--project', (Join-Path $root 'tests/ifc-compiler/Ifc.Compiler.Tests.csproj'), '--configuration', 'Release', '--', $packet)
$env:BIM_REBUILD_PACKET = $packet
Invoke-Annotated 'rebuild-equivalence' $Python @('-m', 'unittest', 'discover', '-s', (Join-Path $root 'tests/harness/rebuild'), '-p', 'test_*.py', '-v')
$report = Join-Path $OutputDirectory 'ifc-rebuild-benchmark.json'
$arguments = @((Join-Path $root 'tests/harness/rebuild/rebuild_benchmark.py'), '--packet', $packet, '--samples', $Samples, '--output', $report, '--dotnet-version', $dotnetVersion)
$invoked = foreach ($entry in $PSBoundParameters.GetEnumerator()) {
    if ($entry.Value -is [System.Management.Automation.SwitchParameter]) { if ($entry.Value.IsPresent) { '-' + $entry.Key } }
    else { '-' + $entry.Key + ' ' + [string]$entry.Value }
}
$command = (@('pwsh', '-File', 'scripts/benchmark-rebuild.ps1') + @($invoked)) -join ' '
$command += ' => ' + $Python + ' ' + (($arguments | ForEach-Object { [string]$_ }) -join ' ')
$arguments += @('--command', $command)
if ($Notice) { $arguments += '--notice' }
Invoke-Annotated 'rebuild-benchmark-run' $Python $arguments
if ($env:GITHUB_STEP_SUMMARY) {
    @('### IFC incremental vs full rebuild (P01)', '```json', (Get-Content $report -Raw), '```') | Add-Content -Path $env:GITHUB_STEP_SUMMARY
}
"PASS rebuild benchmark: $report"
