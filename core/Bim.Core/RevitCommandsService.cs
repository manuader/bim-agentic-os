using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// Line is what the CLI prints and what an MCP tool returns as text; Forwarded tells whether the pipe was used.
public sealed record RevitCommandResult(string Line, bool IsError, bool Forwarded);

// The single client path shared by the CLI "revit-command" verb and the generated MCP tools: pipe name check,
// envelope validation, copies-only and link guards, the family.load source guard, wire size cap, one request/response line on the process pipe,
// then a check that a success answers this very request.
public static class RevitCommandService
{
    public const int ConnectTimeoutMs = 5000;

    // P1-17: how long PipeTransport waits for the reply line once the request is written. The default is the longest
    // per-command budget of the owner catalogue session (scripts/owner-native-session.ps1 -StepTimeoutSeconds, at most
    // 1800 s), so a slow but live command is never cut short by the client; a stalled add-in ends with
    // pipe_read_timeout instead of blocking forever. Callers pick another value with PipeTransportWithReadTimeout.
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(1800);
    public static readonly TimeSpan MaxReadTimeout = TimeSpan.FromHours(24);

    // Same rule as the CLI "native" verb, applied before any file or pipe I/O.
    public static RevitCommandResult? RejectPipe(string? pipeName) =>
        RevitCommandProtocol.IsProcessPipe(pipeName) ? null : Failure(RevitCommandCodes.ExplicitProcessPipeRequired, "pipe_name", "bim-agentic-os-2025-<pid> required");

    public static RevitCommandResult Run(string pipeName, string envelopeJson, RevitCommandPolicy policy, Func<string, string, string?> transport, RevitFileSystemProbe? probe = null)
    {
        if (RejectPipe(pipeName) is { } rejected) return rejected;
        RevitCommandEnvelope envelope;
        try { envelope = RevitCommandProtocol.Parse(envelopeJson); }
        catch (RevitCommandException error) { return Failure(error.Code, error.Field, error.Detail); }
        // Writes and styled exports (RevitCommandCatalogue.NeedsWriteGuard) get the copies-only and link guards.
        var guarded = RevitCommandCatalogue.NeedsWriteGuard(envelope);
        if (RevitCopiesGuard.Check(guarded, envelope.Document.Path, policy) is { } refused)
            return Failure(refused, "document.path", refused == RevitCommandCodes.DocumentNotEnabled ? "write commands need a document under an enabled test root" : null);
        if (guarded && RevitCopiesGuard.MatchingRoot(envelope.Document.Path, policy) is { } root
            && RevitLinkGuard.Check(envelope.Document.Path, root, probe ?? RevitFileSystemProbe.Real) is { } linked)
            return Failure(linked, "document.path", linked == RevitCommandCodes.DocumentLinkRefused ? "a symlink, junction or hard link between the enabled root and the document" : null);
        // document.save_copy (P1-08): target folder, name, links and existence, and that the open document is a test copy.
        if (envelope.Args is DocumentSaveCopyArgs copy
            && RevitSaveCopyGuard.Check(copy.Path, envelope.Document.Path, policy.RepositoryDirectory, probe ?? RevitFileSystemProbe.Real, RevitSaveCopyProbe.Real) is { } copyRefused)
            return Failure(RevitCommandCodes.SaveCopyRefused, copyRefused.Field, copyRefused.Detail);
        // document.save_as (P9-08): target path shape, enabled root, links and existence, and an enabled source.
        if (envelope.Args is DocumentSaveAsArgs saveAs
            && RevitSaveAsGuard.Check(saveAs.Path, envelope.Document.Path, policy, probe ?? RevitFileSystemProbe.Real, RevitSaveCopyProbe.Real) is { } saveAsRefused)
            return Failure(RevitCommandCodes.SaveAsRefused, saveAsRefused.Field, saveAsRefused.Detail);
        // command.batch: every family.load step is checked too (field args.steps[i].args.path).
        foreach (var (prefix, args) in RevitCommandBatch.GuardedArgs(envelope))
            if (args is FamilyLoadArgs load && RevitFamilySourceGuard.Check(load.Path, policy.FamilySourceRoots, probe ?? RevitFileSystemProbe.Real) is { } source)
                return Failure(RevitCommandCodes.FamilySourceRefused, prefix + "args.path", source);
        var line = RevitCommandProtocol.WireLine(envelope);
        if (line.Length > RevitCommandProtocol.MaxRequestChars) return Failure(RevitCommandCodes.RequestTooLarge, null, "wire line exceeds 65536 characters");
        string? response;
        try { response = transport(pipeName, line); }
        catch (RevitPipeReadTimeoutException error) { return ReadTimeout(envelope, error.Timeout); }
        catch (TimeoutException) { return Failure(RevitCommandCodes.PipeConnectTimeout, "pipe_name", "no add-in listening on this process pipe", forwarded: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Failure(RevitCommandCodes.PipeIoError, "pipe_name", error.GetType().Name, forwarded: true); }
        if (response is null) return Failure(RevitCommandCodes.NoResponse, "pipe_name", "pipe closed without a response line", forwarded: true);
        return Verify(envelope, response);
    }

    // The add-in line is returned verbatim when it is a well-formed failure, or a success whose receipt echoes this
    // request (operation_id, command, document UniqueId and the SHA-256 of the Command text sent). Anything else is
    // response_invalid: a success for another request must never be reported as this one.
    static RevitCommandResult Verify(RevitCommandEnvelope envelope, string response)
    {
        RevitCommandResponse parsed;
        try { parsed = RevitCommandResponse.Parse(response); }
        catch (RevitCommandException error) { return Failure(RevitCommandCodes.ResponseInvalid, error.Field, error.Detail, forwarded: true); }
        if (!parsed.IsSuccess) return new RevitCommandResult(response, true, true);
        var receipt = parsed.Receipt!;
        var mismatch = receipt.OperationId != envelope.OperationId ? "operation_id"
            : receipt.Command != envelope.Command ? "command"
            : receipt.DocumentUniqueId != envelope.Document.UniqueId ? "document_unique_id"
            : receipt.RequestSha256 != envelope.RequestSha256 ? "request_sha256"
            : null;
        return mismatch is null ? new RevitCommandResult(response, false, true)
            : Failure(RevitCommandCodes.ResponseInvalid, "receipt", "receipt " + mismatch + " does not match the request", forwarded: true);
    }

    // The request reached the add-in but no line came back in time. A read-only command changed nothing and may be
    // resent; a write (or styled export) may or may not have been applied, so it is never resent here and the detail
    // says how to replay it safely: the add-in answers a repeated operation_id from its stored receipt.
    static RevitCommandResult ReadTimeout(RevitCommandEnvelope envelope, TimeSpan timeout)
    {
        var waited = ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
        var detail = RevitCommandCatalogue.NeedsWriteGuard(envelope)
            ? "outcome_unknown: no reply within " + waited + " after the write was sent; it may or may not have been applied and was not retried; "
                + "check the document, then resend this same envelope (same operation_id) so the add-in answers from its receipt instead of applying it twice"
            : "no reply within " + waited + "; read-only command, the document is unchanged and the request may be resent";
        return Failure(RevitCommandCodes.PipeReadTimeout, "pipe_name", detail, forwarded: true);
    }

    static RevitCommandResult Failure(string code, string? field, string? detail, bool forwarded = false) =>
        new(RevitCommandResponse.Failure(code, field, detail).ToJson(), true, forwarded);

    // Same-user named pipe client, like the CLI "native" verb: one line out, one line back, waiting at most
    // DefaultReadTimeout for the line (RevitPipeReadTimeoutException after that; null when the pipe closes first).
    public static string? PipeTransport(string pipeName, string line) => PipeTransport(pipeName, line, DefaultReadTimeout);

    // A transport with another read timeout (greater than zero, at most MaxReadTimeout), checked when it is made.
    public static Func<string, string, string?> PipeTransportWithReadTimeout(TimeSpan readTimeout)
    {
        CheckReadTimeout(readTimeout);
        return (pipeName, line) => PipeTransport(pipeName, line, readTimeout);
    }

    public static string? PipeTransport(string pipeName, string line, TimeSpan readTimeout)
    {
        CheckReadTimeout(readTimeout);
        // Asynchronous (overlapped) so a pending read can be cancelled on Windows as well as on Unix sockets.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(ConnectTimeoutMs);
        var utf8 = new UTF8Encoding(false);
        // The same bytes StreamWriter.WriteLine sent before, without a writer whose disposal would flush a pipe the
        // add-in already closed (IOException "Pipe is broken" on Windows instead of no_response).
        pipe.Write(utf8.GetBytes(line + Environment.NewLine));
        using var reader = new StreamReader(pipe, utf8, false, 4096, leaveOpen: true);
        using var cancel = new CancellationTokenSource();
        var read = reader.ReadLineAsync(cancel.Token).AsTask();
        bool done;
        try { done = read.Wait(readTimeout); }
        catch (AggregateException error) when (error.InnerException is OperationCanceledException) { done = false; }
        catch (AggregateException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        if (done) return read.Result;
        // Stop the pending read and close the pipe; its late outcome is observed and dropped.
        cancel.Cancel();
        _ = read.ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        throw new RevitPipeReadTimeoutException(readTimeout);
    }

    static void CheckReadTimeout(TimeSpan readTimeout)
    {
        if (readTimeout <= TimeSpan.Zero || readTimeout > MaxReadTimeout)
            throw new ArgumentOutOfRangeException(nameof(readTimeout), readTimeout, "the read timeout must be greater than zero and at most 24 hours");
    }
}

// Thrown by RevitCommandService.PipeTransport when the add-in accepted the request but sent no line within the read
// timeout; RevitCommandService.Run reports it as pipe_read_timeout. An IOException, not a TimeoutException, so a caller
// that maps TimeoutException to pipe_connect_timeout can never report a sent request as "no add-in listening".
public sealed class RevitPipeReadTimeoutException : IOException
{
    public RevitPipeReadTimeoutException(TimeSpan timeout) : base(RevitCommandCodes.PipeReadTimeout) => Timeout = timeout;

    public TimeSpan Timeout { get; }
}
