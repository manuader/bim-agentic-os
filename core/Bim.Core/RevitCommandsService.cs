using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// Line is what the CLI prints and what an MCP tool returns as text; Forwarded tells whether the pipe was used.
public sealed record RevitCommandResult(string Line, bool IsError, bool Forwarded);

// The single client path shared by the CLI "revit-command" verb and the generated MCP tools: pipe name check,
// envelope validation, copies-only and link guards, wire size cap, one request/response line on the process pipe,
// then a check that a success answers this very request.
public static class RevitCommandService
{
    public const int ConnectTimeoutMs = 5000;

    // Same rule as the CLI "native" verb, applied before any file or pipe I/O.
    public static RevitCommandResult? RejectPipe(string? pipeName) =>
        RevitCommandProtocol.IsProcessPipe(pipeName) ? null : Failure(RevitCommandCodes.ExplicitProcessPipeRequired, "pipe_name", "bim-agentic-os-2025-<pid> required");

    public static RevitCommandResult Run(string pipeName, string envelopeJson, RevitCommandPolicy policy, Func<string, string, string?> transport, RevitFileSystemProbe? probe = null)
    {
        if (RejectPipe(pipeName) is { } rejected) return rejected;
        RevitCommandEnvelope envelope;
        try { envelope = RevitCommandProtocol.Parse(envelopeJson); }
        catch (RevitCommandException error) { return Failure(error.Code, error.Field, error.Detail); }
        if (RevitCopiesGuard.Check(envelope.Spec.Writes, envelope.Document.Path, policy) is { } refused)
            return Failure(refused, "document.path", refused == RevitCommandCodes.DocumentNotEnabled ? "write commands need a document under an enabled test root" : null);
        if (envelope.Spec.Writes && RevitCopiesGuard.MatchingRoot(envelope.Document.Path, policy) is { } root
            && RevitLinkGuard.Check(envelope.Document.Path, root, probe ?? RevitFileSystemProbe.Real) is { } linked)
            return Failure(linked, "document.path", linked == RevitCommandCodes.DocumentLinkRefused ? "a symlink, junction or hard link between the enabled root and the document" : null);
        var line = RevitCommandProtocol.WireLine(envelope);
        if (line.Length > RevitCommandProtocol.MaxRequestChars) return Failure(RevitCommandCodes.RequestTooLarge, null, "wire line exceeds 65536 characters");
        string? response;
        try { response = transport(pipeName, line); }
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

    static RevitCommandResult Failure(string code, string? field, string? detail, bool forwarded = false) =>
        new(RevitCommandResponse.Failure(code, field, detail).ToJson(), true, forwarded);

    // Same-user named pipe client, like the CLI "native" verb: one line out, one line back.
    public static string? PipeTransport(string pipeName, string line)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(ConnectTimeoutMs);
        var utf8 = new UTF8Encoding(false);
        using var writer = new StreamWriter(pipe, utf8, 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, utf8, false, 4096, leaveOpen: true);
        writer.WriteLine(line);
        return reader.ReadLine();
    }
}
