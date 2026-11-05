#Requires -Version 7.2
# P10-08 release archive (stub; implementation follows the failing tests).
[CmdletBinding(DefaultParameterSetName = 'Create')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Create')][string]$PackageDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Extract')][switch]$Extract,
    [Parameter(Mandatory, ParameterSetName = 'Extract')][string]$DestinationDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][switch]$Verify,
    [Parameter(Mandatory)][string]$ArchivePath,
    [string]$ChecksumsPath
)
[Console]::Error.WriteLine('package-archive: not implemented')
exit 1
