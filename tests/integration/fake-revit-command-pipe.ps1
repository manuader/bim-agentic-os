# Headless stand-in for the add-in pipe, for test-catalogue-session.ps1 only: serves one request line per connection
# on the given process-shaped pipe name, answers it with fake-revit-command-executor.ps1 and logs every received
# operation_id (one per line) so the test can prove which requests the CLI forwarded. Stops after MaxRequests
# connections or TimeoutSeconds.
param(
    [Parameter(Mandatory)][string]$PipeName,
    [Parameter(Mandatory)][string]$DocumentUid,
    [Parameter(Mandatory)][string]$ReadyFile,
    [Parameter(Mandatory)][string]$LogFile,
    [ValidateRange(1, 100)][int]$MaxRequests = 40,
    [ValidateRange(5, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/fake-revit-command-executor.ps1"
$state = New-FakeRevitState $DocumentUid
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$utf8 = [Text.UTF8Encoding]::new($false)
$ready = $false
for ($served = 0; $served -lt $MaxRequests -and [DateTime]::UtcNow -lt $deadline; $served++) {
    $server = [IO.Pipes.NamedPipeServerStream]::new($PipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        if (!$ready) { [IO.File]::WriteAllText($ReadyFile, [string]$PID); $ready = $true }
        $wait = $server.WaitForConnectionAsync()
        $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        if (!$wait.Wait($remaining)) { break }
        $reader = [IO.StreamReader]::new($server, $utf8, $false, 4096, $true)
        $writer = [IO.StreamWriter]::new($server, $utf8, 4096, $true)
        $writer.AutoFlush = $true
        $line = $reader.ReadLine()
        if ($null -eq $line) { continue }
        # WireLine writes Command last, verbatim: {"DocumentPath":...,"Operation":"revit.command",...,"Command":<envelope>}
        $marker = '"Command":'
        $at = $line.LastIndexOf($marker, [StringComparison]::Ordinal)
        $outer = $line | ConvertFrom-Json
        if ($at -lt 0 -or $outer.Operation -ne 'revit.command' -or !$line.EndsWith('}')) { $response = ConvertTo-FakeResponseJson (New-FakeFailure 'envelope_invalid' $null 'not a revit.command wire line') }
        else {
            $commandText = $line.Substring($at + $marker.Length, $line.Length - $at - $marker.Length - 1)
            $response = Invoke-FakeRevitCommand $state $commandText
            [IO.File]::AppendAllText($LogFile, $state.Received[$state.Received.Count - 1] + "`n")
        }
        $writer.WriteLine($response)
        if ($IsWindows) { $server.WaitForPipeDrain() }
        $reader.Dispose(); $writer.Dispose()
    } finally { $server.Dispose() }
}
