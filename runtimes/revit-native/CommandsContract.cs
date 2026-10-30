using Bim.Core;
using System.Text.Json;
namespace Bim.Revit;

// C03 (ADR0009) red step: signatures only; the guard suite must fail until the decisions are implemented.
public sealed record CommandRequest(RevitCommandEnvelope Envelope, string RequestSha256);
public sealed record CommandDocumentFacts(string? PathName, string? ProjectUniqueId, bool IsReadOnly, bool IsFamilyDocument, bool IsWorkshared, bool IsModifiable);
public sealed record CommandCandidate(string UniqueId, string Name, string? QualifiedName = null);
public sealed record StoredCommandReceipt(string Command, string DocumentUniqueId, string RequestSha256, string ResponseJson);
public enum CommandReceiptAction { Execute, Replay, Conflict }

public static class CommandWire
{
    public static string Sha256(string commandText) => throw new NotImplementedException(commandText);
    public static CommandRequest Bind(string? documentPath, string? uniqueId, double[]? metres, bool hasOtherPayload, string? commandText) => throw new NotImplementedException();
}

public static class CommandAdmission
{
    public static void Check(RevitCommandEnvelope envelope, CommandDocumentFacts document, Func<RevitCommandPolicy> policy, RevitFileSystemProbe probe) => throw new NotImplementedException();
    public static bool SamePath(string a, string b) => throw new NotImplementedException();
}

public static class CommandReferences
{
    public static int Resolve(string reference, IReadOnlyList<CommandCandidate> candidates, string field, string kind) => throw new NotImplementedException();
    public static string Qualified(string family, string type) => throw new NotImplementedException();
}

public static class CommandReceiptStore
{
    public static readonly Guid SchemaGuid = Guid.Empty;
    public static Dictionary<string, StoredCommandReceipt> Parse(string? payload) => throw new NotImplementedException();
    public static string Serialize(IReadOnlyDictionary<string, StoredCommandReceipt> receipts) => throw new NotImplementedException();
    public static CommandReceiptAction Decide(IReadOnlyDictionary<string, StoredCommandReceipt> receipts, string operationId, string requestSha256) => throw new NotImplementedException();
    public static StoredCommandReceipt Record(CommandRequest request, RevitCommandResponse response) => throw new NotImplementedException();
    public static string ReplayJson(StoredCommandReceipt stored) => throw new NotImplementedException();
    public static RevitCommandException Conflict(string operationId) => throw new NotImplementedException();
}

public static class CommandResponses
{
    public static RevitCommandResponse Success(CommandRequest request, IReadOnlyList<string> created, IReadOnlyList<string> modified, string readbackJson, IReadOnlyList<string> warnings) => throw new NotImplementedException();
    public static string Failure(RevitCommandException error) => JsonSerializer.Serialize(error.Code);
    public static string Failure(Exception error) => JsonSerializer.Serialize(error.Message);
}
