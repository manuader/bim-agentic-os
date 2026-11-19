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
# Add-FakeRecorderFailure with that count once a revit.command with that operation id has run, before its reply is
# written, so the recorder failure lands between that call and the next; journal.read then reports it as the in-process
# fake does (P2-23). A malformed pair, a count below 1, a duplicate id or no pair is refused before the pipe is ready.
# P2-26: only the first request with that id whose reply status is succeeded and which is not a dry run (args.dry_run)
# injects; a refused or dry-run request with that id injects nothing and the id stays scheduled. At exit every scheduled
# id never matched is written to stderr and the fake exits 1. Without the option nothing is injected and every reply is
# unchanged; with it, replies differ only by the failures injected.
# P2-27: -DropReplyCommand drops a reply only for a request whose command equals it and whose args.dry_run is not true
# (absent, null or false): that write has run (state changed, operation id logged) and its connection is closed without
# a reply. A dry run of that command (args.dry_run true) is answered normally, so no dry-run reply is ever dropped. The
# drop is decided after the -RecorderFailures check, by the same dry-run rule: a dropped write whose reply was succeeded
# and whose operation id is scheduled still injects its failures and that id counts as matched; a dropped write that was
# refused injects nothing and its id stays scheduled.
# P2-30: -BreakPipeCommand reads each request line itself in 64-byte reads and, as soon as what it has read holds
# "command":"<that command>","document": (the envelope's own command member: the canonical envelope sorts its members,
# so args comes first, then command, document, operation_id and protocol_version; a command.batch step's "command" is
# followed by "operation_id" and never matches), closes the connection without reading the rest of the line and without
# a reply. The request never reaches the executor: nothing runs, no operation id is known or logged, no
# -RecorderFailures id matches, dry run or not; the log gets one "broken:<command>" line instead. Because args precede
# the command member, the fake has read the args when it breaks; what stays unread is the tail (document, operation_id,
# protocol_version, newline). On Linux (Unix socket) the client's write has completed into the socket buffer and closing
# with that unread receive queue resets the connection, so the client's read fails (ECONNRESET); on Windows (named
# pipe) the client's pending write of the tail fails when the server end closes. Every other request is read up to its
# newline and served exactly as without the option. Without the option requests are read by ReadLine, as before.
# P2-31: -BreakAfterBytes <N> reads each request itself, at most N bytes (4096-byte reads, never past N) and, when it
# has read N bytes without a newline, waits 200 ms, measures the bytes queued in the server end and closes the
# connection without reading the rest of the line and without a reply; the log gets one
# "broken-after:<bytes read>:<InBufferSize>:<bytes queued>" line instead. InBufferSize is what the server end reports
# ("unknown" when it throws): on Windows it is the requested quota (0 here), not a measured buffer. Bytes queued is
# measured: Windows PeekNamedPipe TotalBytesAvail on the server handle, Linux ioctl FIONREAD on its socket fd (-1 - error
# code when the call fails, -1 on other OSes), so a test can tell whether the client still had bytes unsent. The
# request never reaches the executor: nothing runs, no operation id is known or logged, no -RecorderFailures id
# matches. A request whose newline lies within its first N bytes is read up to it and served exactly as without the
# option. It cannot be combined with -BreakPipeCommand (refused before the pipe is ready).
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
    [string]$RecorderFailures,
    # Test-only (P2-30): a request of this catalogue command has its connection closed before its line is fully read.
    [string]$BreakPipeCommand,
    # Test-only (P2-31): a request line longer than this many bytes has its connection closed after exactly that many.
    [ValidateRange(1, 1048576)][int]$BreakAfterBytes
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
$breakAfter = if ($PSBoundParameters.ContainsKey('BreakAfterBytes')) { $BreakAfterBytes } else { 0 }
if ($breakAfter -gt 0 -and $BreakPipeCommand) { throw '-BreakAfterBytes cannot be combined with -BreakPipeCommand' }
if ($breakAfter -gt 0) {
    # P2-31: the bytes queued in the server end of a connection (see the header).
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FakePipeQueue
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);
    [DllImport("libc", SetLastError = true)]
    static extern int ioctl(int fd, UIntPtr request, out int available);
    public static long Queued(SafeHandle handle)
    {
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            var raw = handle.DangerousGetHandle();
            if (OperatingSystem.IsWindows()) return PeekNamedPipe(raw, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero) ? available : -1L - Marshal.GetLastWin32Error();
            if (OperatingSystem.IsLinux()) return ioctl(checked((int)raw), (UIntPtr)0x541Bu, out var queued) == 0 ? queued : -1L - Marshal.GetLastWin32Error();
            return -1;
        }
        finally { if (added) handle.DangerousRelease(); }
    }
}
'@
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
                # P2-30: with -BreakPipeCommand the line is read below without a reader (StreamReader.Null: Dispose is a no-op).
                # P2-31: so is it with -BreakAfterBytes.
                $reader = if ($BreakPipeCommand -or $breakAfter -gt 0) { [IO.StreamReader]::Null } else { [IO.StreamReader]::new($server, $utf8, $false, 4096, $true) }
                $writer = [IO.StreamWriter]::new($server, $utf8, 4096, $true)
                $writer.AutoFlush = $true
                if ($BreakPipeCommand) {
                    # P2-30: read the line in small chunks so the rest of a broken request stays unread on the connection.
                    $breakMarker = '"command":"' + $BreakPipeCommand + '","document":'
                    $markerBytes = $utf8.GetByteCount($breakMarker)
                    $bytes = [IO.MemoryStream]::new()
                    $chunk = [byte[]]::new(64)
                    $broken = $false
                    while ($true) {
                        $count = $server.Read($chunk, 0, $chunk.Length)
                        if ($count -le 0) { break }
                        $bytes.Write($chunk, 0, $count)
                        # Only the new bytes plus an overlap of the marker's length (the marker is ASCII, so a window that
                        # starts or ends inside a multi-byte character cannot hide it).
                        $from = [Math]::Max(0, [int]$bytes.Length - $count - $markerBytes)
                        if ($utf8.GetString($bytes.GetBuffer(), $from, [int]$bytes.Length - $from).Contains($breakMarker, [StringComparison]::Ordinal)) { $broken = $true; break }
                        if ([Array]::IndexOf($chunk, [byte]10, 0, $count) -ge 0) { break }
                    }
                    if ($broken) {
                        [IO.File]::AppendAllText($LogFile, "broken:$BreakPipeCommand`n")
                        $reader.Dispose(); $writer.Dispose(); continue
                    }
                    if ($bytes.Length -eq 0) { continue }
                    $line = $utf8.GetString($bytes.GetBuffer(), 0, [int]$bytes.Length)
                    $end = $line.IndexOf([char]10)
                    if ($end -ge 0) { $line = $line.Substring(0, $end) }
                    if ($line.EndsWith([char]13)) { $line = $line.Substring(0, $line.Length - 1) }
                }
                elseif ($breakAfter -gt 0) {
                    # P2-31: read at most N bytes; a line not ended within them is broken there, the rest left unsent or unread.
                    $bytes = [IO.MemoryStream]::new()
                    $chunk = [byte[]]::new(4096)
                    $ended = $false
                    while ($bytes.Length -lt $breakAfter) {
                        $count = $server.Read($chunk, 0, [Math]::Min($chunk.Length, $breakAfter - [int]$bytes.Length))
                        if ($count -le 0) { break }
                        $bytes.Write($chunk, 0, $count)
                        if ([Array]::IndexOf($chunk, [byte]10, 0, $count) -ge 0) { $ended = $true; break }
                    }
                    if (!$ended -and $bytes.Length -ge $breakAfter) {
                        $inBuffer = 'unknown'
                        try { $inBuffer = [string]$server.InBufferSize } catch { $inBuffer = 'unknown' }
                        # Let the client write what it can, then measure what sits in the server end.
                        Start-Sleep -Milliseconds 200
                        $queued = [FakePipeQueue]::Queued($server.SafePipeHandle)
                        [IO.File]::AppendAllText($LogFile, 'broken-after:' + $bytes.Length + ':' + $inBuffer + ':' + $queued + "`n")
                        $reader.Dispose(); $writer.Dispose(); continue
                    }
                    if ($bytes.Length -eq 0) { continue }
                    $line = $utf8.GetString($bytes.GetBuffer(), 0, [int]$bytes.Length)
                    $end = $line.IndexOf([char]10)
                    if ($end -ge 0) { $line = $line.Substring(0, $end) }
                    if ($line.EndsWith([char]13)) { $line = $line.Substring(0, $line.Length - 1) }
                }
                else { $line = $reader.ReadLine() }
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
                    # P2-24 / P2-26: the recorder failures scheduled after this operation id, once, before its reply is written,
                    # and only when that reply is succeeded and the request is not a dry run.
                    $inject = 0
                    if ($state.Received.Count -gt $before -and $failuresAfter.ContainsKey($state.Received[$state.Received.Count - 1])) {
                        $sent = $null
                        try { $sent = $commandText | ConvertFrom-Json } catch { $sent = $null }
                        $reply = $null
                        try { $reply = $response | ConvertFrom-Json } catch { $reply = $null }
                        $dryRun = $null -ne $sent -and $null -ne $sent.args -and $null -ne $sent.args.PSObject.Properties['dry_run'] -and [bool]$sent.args.dry_run
                        $succeeded = $null -ne $reply -and $null -ne $reply.PSObject.Properties['status'] -and [string]::Equals([string]$reply.status, 'succeeded', [StringComparison]::Ordinal)
                        if ($succeeded -and $null -ne $sent -and !$dryRun -and $failuresAfter.Remove($state.Received[$state.Received.Count - 1], [ref]$inject)) { Add-FakeRecorderFailure $state $inject }
                    }
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
# P2-26: a scheduled recorder failure that never landed fails the run instead of vanishing.
if ($failuresAfter.Count -gt 0) {
    $unmatched = [string[]]@($failuresAfter.Keys)
    [Array]::Sort($unmatched, [StringComparer]::Ordinal)
    [Console]::Error.WriteLine('fake pipe: -RecorderFailures operation ids never matched by a succeeded, non-dry-run revit.command: ' + ($unmatched -join ','))
    exit 1
}
