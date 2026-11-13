# Headless stand-in for the add-in pipe, for test-catalogue-session.ps1 only: serves one request line per connection
# on the given process-shaped pipe name, answers it with fake-revit-command-executor.ps1 (revit.command lines through
# Invoke-FakeRevitCommand, journal.read lines through Invoke-FakeRevitWire) and logs every received operation_id, or
# "journal.read", one per line, so the test can prove which requests reached the pipe. Stops after MaxRequests
# connections or TimeoutSeconds.
# The next listening instance is created as soon as a connection is accepted, before the current reply is written and
# its instance disposed (two instances at most). On Unix a .NET pipe server is a Unix domain socket shared by the
# process's instances of that name: disposing the last instance closes the listener and unlinks the socket, so a
# client that reconnected at once (revit-command --all, one connection per page) and sat in the old listener's backlog
# was reset (pipe_io_error). With the next instance alive the listener never closes between pages.
# P2-24, test-only: -RecorderFailures "<operation_id>:<count>,..." (needs -ExternalEdits with ids) calls
# Add-FakeRecorderFailure with that count once the named revit.command has run (its first time), before its reply is
# written, so the recorder failure lands between that call and the next; journal.read then reports it as the in-process
# fake does (P2-23). A malformed pair, a count below 1, a duplicate id or no pair is refused before the pipe is ready.
# Without the option nothing is injected and every reply is unchanged.
param(
    [Parameter(Mandatory)][string]$PipeName,
    [Parameter(Mandatory)][string]$DocumentUid,
    [Parameter(Mandatory)][string]$ReadyFile,
    [Parameter(Mandatory)][string]$LogFile,
    [ValidateRange(1, 10000)][int]$MaxRequests = 400,
    [ValidateRange(5, 1800)][int]$TimeoutSeconds = 600,
    # Test-only (P2-18): comma-separated operation ids the fake records as external edits (journal.read entries).
    [string]$ExternalEdits,
    # Test-only (P4-14): a write of this catalogue command (not a dry run) runs, then its connection is closed without a
    # reply (the reply is lost after the write happened).
    [string]$DropReplyCommand,
    # Test-only (P2-24): "<operation_id>:<count>,..." recorder failures injected after those revit.command calls.
    [string]$RecorderFailures
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/fake-revit-command-executor.ps1"
$external = if ($PSBoundParameters.ContainsKey('ExternalEdits')) { [string[]]@($ExternalEdits -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { $null }
$state = New-FakeRevitState $DocumentUid @{} $external
# Behind the real CLI: a client-side case that arrives here answers fake_client_side_leak.
$state.BehindCli = $true
$failuresAfter = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
if ($PSBoundParameters.ContainsKey('RecorderFailures')) {
    if ($null -eq $state.ExternalEdits) { throw '-RecorderFailures needs -ExternalEdits with at least one operation id' }
    foreach ($pair in @($RecorderFailures -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $colon = $pair.LastIndexOf(':')
        $count = 0
        if ($colon -lt 1 -or ![int]::TryParse($pair.Substring($colon + 1), [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$count)) { throw "-RecorderFailures pair '$pair' is not <operation_id>:<count>" }
        if ($count -lt 1) { throw "-RecorderFailures pair '$pair' needs a count of 1 or more" }
        $id = $pair.Substring(0, $colon).Trim()
        if (!$id -or $failuresAfter.ContainsKey($id)) { throw "-RecorderFailures names operation id '$id' twice or not at all" }
        $failuresAfter[$id] = $count
    }
    if ($failuresAfter.Count -eq 0) { throw '-RecorderFailures needs at least one <operation_id>:<count> pair' }
}
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$utf8 = [Text.UTF8Encoding]::new($false)
$ready = $false
$newServer = { [IO.Pipes.NamedPipeServerStream]::new($PipeName, [IO.Pipes.PipeDirection]::InOut, 2, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous) }
$next = & $newServer
try {
    for ($served = 0; $served -lt $MaxRequests -and [DateTime]::UtcNow -lt $deadline; $served++) {
        $server = $next
        $next = $null
        try {
            if (!$ready) { [IO.File]::WriteAllText($ReadyFile, [string]$PID); $ready = $true }
            $wait = $server.WaitForConnectionAsync()
            $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if (!$wait.Wait($remaining)) { break }
            # Listen for the next connection before this one is answered and closed.
            if ($served + 1 -lt $MaxRequests) { $next = & $newServer }
            # A client that vanishes mid-request (killed, timed out) costs only its own connection.
            try {
                $reader = [IO.StreamReader]::new($server, $utf8, $false, 4096, $true)
                $writer = [IO.StreamWriter]::new($server, $utf8, 4096, $true)
                $writer.AutoFlush = $true
                $line = $reader.ReadLine()
                if ($null -eq $line) { continue }
                # WireLine writes Command last, verbatim: {"DocumentPath":...,"Operation":"revit.command",...,"Command":<envelope>}
                $marker = '"Command":'
                $at = $line.LastIndexOf($marker, [StringComparison]::Ordinal)
                # The Operation as System.Text.Json reads it (last member of that exact name): a line ConvertFrom-Json refuses
                # (duplicate or differently cased members) still gets a reply instead of stopping the fake.
                $operation = $null
                try {
                    $parsed = [Text.Json.JsonDocument]::Parse($line, [Text.Json.JsonDocumentOptions]::new())
                    try { $operation = Get-FakeWireText $parsed.RootElement 'Operation' } finally { $parsed.Dispose() }
                } catch { $operation = $null }
                if ($operation -eq 'journal.read') {
                    $response = Invoke-FakeRevitWire $state $line
                    [IO.File]::AppendAllText($LogFile, "journal.read`n")
                }
                elseif ($at -lt 0 -or $operation -ne 'revit.command' -or !$line.EndsWith('}')) { $response = ConvertTo-FakeResponseJson (New-FakeFailure 'envelope_invalid' $null 'not a revit.command wire line') }
                else {
                    $commandText = $line.Substring($at + $marker.Length, $line.Length - $at - $marker.Length - 1)
                    $before = $state.Received.Count
                    $response = Invoke-FakeRevitCommand $state $commandText
                    [IO.File]::AppendAllText($LogFile, $state.Received[$state.Received.Count - 1] + "`n")
                    # P2-24: the recorder failures scheduled after this operation, once, before its reply is written.
                    $inject = 0
                    if ($state.Received.Count -gt $before -and $failuresAfter.Remove($state.Received[$state.Received.Count - 1], [ref]$inject)) { Add-FakeRecorderFailure $state $inject }
                    if ($DropReplyCommand) {
                        $sent = $null
                        try { $sent = $commandText | ConvertFrom-Json } catch { $sent = $null }
                        $dryRun = $null -ne $sent -and $null -ne $sent.args -and $null -ne $sent.args.PSObject.Properties['dry_run'] -and [bool]$sent.args.dry_run
                        if ($null -ne $sent -and [string]$sent.command -eq $DropReplyCommand -and !$dryRun) { $reader.Dispose(); $writer.Dispose(); continue }
                    }
                }
                $writer.WriteLine($response)
                if ($IsWindows) { $server.WaitForPipeDrain() }
                $reader.Dispose(); $writer.Dispose()
            } catch {
                $io = $_.Exception
                while ($null -ne $io -and $io -isnot [IO.IOException]) { $io = $io.InnerException }
                if ($null -eq $io) { throw }
                [Console]::Error.WriteLine('fake pipe: connection dropped: ' + $io.GetType().Name)
            }
        } finally { $server.Dispose() }
        if ($null -eq $next) { break }
    }
} finally { if ($null -ne $next) { $next.Dispose() } }
