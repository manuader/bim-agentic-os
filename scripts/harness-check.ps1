param([string]$Root = (Split-Path $PSScriptRoot))$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Root)
$issues = [Collections.Generic.List[string]]::new()
function Fail([string]$message) { $issues.Add($message) }
# Core must remain vendor independent in source and project references.
foreach ($file in Get-ChildItem "$root/core" -Recurse -File | Where-Object { $_.Extension -in '.cs','.csproj' -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }) {
    if ((Get-Content $file.FullName -Raw) -match '(?i)Autodesk|RevitAPI|runtimes[/\\]|interfaces[/\\]') { Fail "Core dependency boundary: $($file.FullName)" }
}
foreach ($file in Get-ChildItem "$root/.agents/skills" -Filter SKILL.md -Recurse -ErrorAction SilentlyContinue) {
    if ((Get-Content $file.FullName -Raw) -notmatch '(?s)\A---\r?\n.*?\bname:\s*[^\r\n]+.*?\bdescription:\s*[^\r\n]+.*?\r?\n---') { Fail "Invalid skill frontmatter: $($file.FullName)" }
}
$featuresPath = "$root/docs/features.json"
if (!(Test-Path $featuresPath)) { Fail 'Missing docs/features.json' } else {
    $data = Get-Content $featuresPath -Raw | ConvertFrom-Json -NoEnumerate
    $features = if ($data -is [array]) { $data } else { $data.features }
    if (!$features) { Fail 'Feature ledger is empty' }
    $ids = @{}
    foreach ($feature in $features) {
        if (!$feature.id -or $ids.ContainsKey([string]$feature.id)) { Fail 'Missing or duplicate feature id' } else { $ids[[string]$feature.id] = $true }
        if ($feature.status -notin 'implemented','partial','blocked','pending') { Fail "Invalid feature status: $($feature.id)" }
        if (!$feature.PSObject.Properties['evidence'] -or !$feature.PSObject.Properties['blocker']) { Fail "Missing evidence/blocker field: $($feature.id)" }
        if ($feature.status -eq 'implemented' -and !$feature.evidence) { Fail "Implemented without evidence: $($feature.id)" }
        foreach ($evidence in $feature.evidence) {
            if ($evidence -isnot [string] -or [string]::IsNullOrWhiteSpace($evidence)) { Fail "Invalid feature evidence: $($feature.id)"; continue }
            $resolved = [IO.Path]::GetFullPath((Join-Path $root $evidence))
            $boundary = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if (!$resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { Fail "Evidence escapes repository: $($feature.id): $evidence" }
            elseif (!(Test-Path -LiteralPath $resolved -PathType Leaf)) { Fail "Missing feature evidence: $($feature.id): $evidence" }
        }
        if ($feature.status -eq 'blocked' -and !$feature.blocker) { Fail "Blocked without reason: $($feature.id)" }
    }
}
foreach ($required in 'ARCHITECTURE.md','docs/contracts/module-contracts.md') { if (!(Test-Path "$root/$required")) { Fail "Missing contract: $required" } }
$tracked = & git -C $root ls-files
if ($LASTEXITCODE -ne 0) { throw "Git inventory failed with exit code $LASTEXITCODE" }
foreach ($relative in $tracked) {
    if ($relative -match '(?i)(RevitAPI(UI)?\.dll|\.pfx$|(^|/)\.env$|id_rsa$)') { Fail "Prohibited tracked artifact: $relative" }
    if ($relative -match '\.(cs|ps1|json|yml|yaml|md)$' -and (Test-Path "$root/$relative")) {
        $content = Get-Content "$root/$relative" -Raw
        if ($content -match '(?i)-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|sk-proj-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}') { Fail "Possible committed credential: $relative" }
    }
    if ($relative -match '\.md$' -and $relative -notmatch '^\.agents/' -and (Test-Path "$root/$relative")) {
        $content = Get-Content "$root/$relative" -Raw
        foreach ($match in [regex]::Matches($content,'\[[^\]]*\]\(([^)]+)\)')) {
            $target = $match.Groups[1].Value.Trim('<','>') -replace '#.*$', ''
            if (!$target -or $target -match '^([a-zA-Z]+:|/)' -or $target -match '\s+"') { continue }
            $resolved = Join-Path (Split-Path "$root/$relative") ([Uri]::UnescapeDataString($target))
            if (!(Test-Path -LiteralPath $resolved)) { Fail "Broken doc link in ${relative}: $target" }
        }
    }
}
foreach ($file in Get-ChildItem $root -Filter *.ps1 -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](\.agents|\.tools|\.bim|bin|obj)[\\/]' }) {
    $tokens = $null; $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    foreach ($parseError in $parseErrors) { Fail "PowerShell syntax: $($file.FullName): $($parseError.Message)" }
}
if ($issues.Count) { throw ($issues -join "`n") }
'PASS architecture, contracts, feature ledger, skill metadata, local doc links and static credential/artifact boundaries'
'NOTE static security checks do not prove runtime pipe authorization or native transaction safety'
