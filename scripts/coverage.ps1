$ErrorActionPreference = 'Stop'
$items = @('level.create','wall.create','wall.axis.update','element.inspect','element.delete') | ForEach-Object {
    @{ id="revit.$_"; version_scope=@('25.2.0.38'); coverage_status='source-implemented'; native_verified=$false; limitations=@('No loaded add-in test yet','No independent native acceptance readback'); verification_tests=@('tests/integration/README.md') }
}
New-Item -ItemType Directory -Force "$PSScriptRoot/../capabilities" | Out-Null
@{schema_version=1; census_complete=$false; discovered_in_initial_subset=5; native_verified=0; capabilities=$items} | ConvertTo-Json -Depth 8 | Set-Content "$PSScriptRoot/../capabilities/capabilities.json"
@('# Revit initial subset — census incomplete', '', '| Capability | Status | Native verified |', '|---|---|---|') + ($items | ForEach-Object { "| $($_.id) | source implemented | no |" }) | Set-Content "$PSScriptRoot/../capabilities/REVIT_DESKTOP_PARITY_MATRIX.md"
