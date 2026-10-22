param([string]$Dotnet = 'dotnet', [string]$EvidenceDirectory = "$PSScriptRoot/../.bim/dependency-audit")
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
$root = Split-Path $PSScriptRoot
foreach ($project in Get-ChildItem $root -Filter *.csproj -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](\.tools|\.bim|bin|obj|\.agents)[\\/]' }) {
    & $Dotnet restore $project.FullName
    if ($LASTEXITCODE -ne 0) { throw "Dependency restore failed: $($project.FullName)" }
    $json = & $Dotnet list $project.FullName package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw "Dependency audit failed: $($project.FullName)" }
    $logName = [IO.Path]::GetRelativePath($root,$project.FullName) -replace '[/\\]', '_'
    $json | Set-Content (Join-Path $EvidenceDirectory "$logName.json")
    $audit = $json -join "`n" | ConvertFrom-Json
    if ($audit.errors -or $audit.logs.level -contains 'error') { throw "Dependency audit reported errors: $logName" }
    foreach ($entry in $audit.projects) {
        foreach ($framework in $entry.frameworks) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if ($package.vulnerabilities.Count -gt 0) { throw "Vulnerable package: $($package.id) in $logName" }
            }
        }
    }
}
'PASS dependency vulnerability audit for all maintained projects'
