#Requires -Version 7.2
# P10-04 verifies a release layout against its manifest.json: every listed file present with matching size and
# SHA-256, no unlisted files, ordinal order, VERSION equal to the manifest version and no forbidden content.
# P10-13: sbom.json must exist and be the canonical SBOM of the package (typed sbom_missing/sbom_invalid/
# sbom_build_only/sbom_mismatch problems), even if manifest.json was re-hashed to match an edited sbom.
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/package-common.ps1"
if (!(Test-Path -LiteralPath $PackageDirectory -PathType Container)) { [Console]::Error.WriteLine("package-verify: not a directory: $PackageDirectory"); exit 2 }
$problems = @(Test-PackageManifest $PackageDirectory)
if ($problems.Count) {
    foreach ($problem in $problems) { [Console]::Error.WriteLine("package-verify: $problem") }
    exit 1
}
"PASS package-verify: $([IO.Path]::GetFullPath($PackageDirectory)) matches manifest.json"
