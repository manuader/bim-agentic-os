using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// Line is what the CLI prints and what an MCP tool returns as text; Forwarded tells whether the pipe was used.
public sealed record RevitCommandResult(string Line, bool IsError, bool Forwarded);

// The single client path shared by the CLI "revit-command" verb and the generated MCP tools: pipe name check,
// envelope validation, copies-only guard, wire size cap, then one request/response line on the process pipe.
public static class RevitCommandService
{
    public const int ConnectTimeoutMs = 5000;

    // Same rule as the CLI "native" verb, applied before any file or pipe I/O.
    public static RevitCommandResult? RejectPipe(string? pipeName) =>
        RevitCommandProtocol.IsProcessPipe(pipeName) ? null : Failure(RevitCommandCodes.ExplicitProcessPipeRequired, "pipe_name", "bim-agentic-os-2025-<pid> required");

    public static RevitCommandResult Run(string pipeName, string envelopeJson, RevitCommandPolicy policy, Func<string, string, string?> transport)
    {
        throw new NotImplementedException("C02 red: not implemented yet");
    }

    // The executor's line is passed through verbatim; it is an error unless it is a JSON object with status "succeeded".
    static bool Succeeded(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String && status.GetString() == RevitCommandResponse.Succeeded;
        }
        catch (JsonException) { return false; }
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
