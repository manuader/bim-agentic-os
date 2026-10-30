using Bim.Core;
using Bim.Revit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// C03 (ADR0009) executor oracle, Autodesk-free half only: the wire binding and request hash, the copies-only and
// identity admission the add-in repeats on the document it opened, reference resolution, the receipt store
// decisions, expected geometry and readback checks, and the response/readback JSON. The Revit half
// (runtimes/revit-native/CommandsExecutor.cs) only compiles in CI; a passing run here is unit-tested evidence.
const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3";
const string LevelUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
const string TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000aa1";
const string SymbolUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000bb2";
const string NewUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003c001";
const string Root = @"C:\Repo\.bim\native-runs";
const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
const string RealPath = @"C:\Projects\Real\Tower.rvt";
int passed = 0, failed = 0;

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::revit-command-guard FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}

JsonObject Envelope(string command, JsonNode args, string path = FixturePath, string operationId = "op-1") => new()
{
    ["protocol_version"] = 1,
    ["operation_id"] = operationId,
    ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
    ["command"] = command,
    ["args"] = args,
};
JsonArray Pair(double x, double y) => new(x, y);
JsonObject Wall() => new() { ["level"] = "Level 1", ["type"] = "Generic - 200mm", ["height_mm"] = 3000, ["curve"] = new JsonObject { ["kind"] = "line", ["start"] = Pair(0, 0), ["end"] = Pair(5000, 0) } };
string Canonical(JsonObject envelope) => RevitCommandProtocol.Parse(envelope.ToJsonString()).CanonicalJson;
string? CodeOf(Action action)
{
    try { action(); return null; }
    catch (RevitCommandException error) { return error.Code; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}
RevitCommandException? ErrorOf(Action action)
{
    try { action(); return null; }
    catch (RevitCommandException error) { return error; }
    catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
}
string Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
var policy = RevitCommandPolicy.FromAllowlist(null, Root);

try
{
    // ---- Wire binding and request hash ----
    var wallEnvelope = Envelope("wall.create", Wall());
    var canonical = Canonical(wallEnvelope);
    var bound = CommandWire.Bind(FixturePath, Uid, Array.Empty<double>(), false, canonical);
    Check(bound.Envelope.Command == "wall.create" && bound.Envelope.Args is WallCreateArgs, "executor re-parses the Command envelope with the Core protocol");
    Check(bound.RequestSha256 == Hex(canonical) && bound.RequestSha256 == bound.Envelope.RequestSha256, "request_sha256 of a canonical Command equals the client's hash");
    var spaced = wallEnvelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    var boundSpaced = CommandWire.Bind(FixturePath, Uid, null, false, spaced);
    Check(boundSpaced.RequestSha256 == Hex(spaced) && boundSpaced.RequestSha256 != boundSpaced.Envelope.RequestSha256, "request_sha256 hashes the Command text as received, never a re-canonicalization");
    Check(CommandWire.Sha256("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Sha256 is lowercase hex of the UTF-8 bytes (FIPS 180-2 'abc' vector)");
    Check(CommandWire.Sha256("\u00e9") == Hex("\u00e9") && CommandWire.Sha256("\u00e9") != CommandWire.Sha256("\\u00e9"), "hash binds the exact text: a literal and an escaped character differ");
    Check(CodeOf(() => CommandWire.Bind(FixturePath, Uid, null, false, null)) == RevitCommandCodes.EnvelopeInvalid, "missing Command is envelope_invalid");
    Check(CodeOf(() => CommandWire.Bind(FixturePath, Uid, new[] { 1.0 }, false, canonical)) == RevitCommandCodes.EnvelopeInvalid, "revit.command with Metres is envelope_invalid");
    Check(CodeOf(() => CommandWire.Bind(FixturePath, Uid, null, true, canonical)) == RevitCommandCodes.EnvelopeInvalid, "revit.command with a Batch or Checkpoint is envelope_invalid");
    Check(ErrorOf(() => CommandWire.Bind(RealPath, Uid, null, false, canonical)) is { Code: RevitCommandCodes.EnvelopeInvalid, Field: "DocumentPath" }, "Request.DocumentPath different from document.path is envelope_invalid");
    Check(ErrorOf(() => CommandWire.Bind(FixturePath, NewUid, null, false, canonical)) is { Code: RevitCommandCodes.EnvelopeInvalid, Field: "UniqueId" }, "Request.UniqueId different from document.unique_id is envelope_invalid");
    var badArgs = Envelope("wall.create", new JsonObject { ["level"] = "Level 1" });
    Check(CodeOf(() => CommandWire.Bind(FixturePath, Uid, null, false, badArgs.ToJsonString())) == RevitCommandCodes.ArgsInvalid, "executor repeats the catalogue validation (args_invalid)");
    Check(CodeOf(() => CommandWire.Bind(FixturePath, Uid, null, false, "\"" + canonical.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")) == RevitCommandCodes.EnvelopeInvalid, "a Command sent as a JSON string, not an object, is envelope_invalid");

    // The real CLI wire line, read the way App.Listen reads it (System.Text.Json into a record with JsonElement? Command).
    var wireLine = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(wallEnvelope.ToJsonString()));
    var wire = JsonSerializer.Deserialize<WireRequest>(wireLine)!;
    Check(wire.Operation == RevitCommandProtocol.WireOperation && wire.Command is { } command && command.GetRawText() == canonical, "raw Command text survives Request deserialization byte for byte");

    // ---- Admission on the document actually opened ----
    var writeEnvelope = bound.Envelope;
    var queryEnvelope = RevitCommandProtocol.Parse(Envelope("query.levels", new JsonObject()).ToJsonString());
    var realQuery = RevitCommandProtocol.Parse(Envelope("query.levels", new JsonObject(), RealPath).ToJsonString());
    var realWrite = RevitCommandProtocol.Parse(Envelope("wall.create", Wall(), RealPath).ToJsonString());
    CommandDocumentFacts Facts(string? path = FixturePath, string? uid = Uid, bool readOnly = false, bool family = false, bool workshared = false, bool modifiable = false) => new(path, uid, readOnly, family, workshared, modifiable);
    RevitCommandException? Admit(RevitCommandEnvelope envelope, CommandDocumentFacts facts, RevitFileSystemProbe? probe = null, Func<RevitCommandPolicy>? policyFactory = null) =>
        ErrorOf(() => CommandAdmission.Check(envelope, facts, policyFactory ?? (() => policy), probe ?? noLinks));
    Check(Admit(writeEnvelope, Facts()) is null, "write on the named enabled fixture is admitted");
    Check(Admit(writeEnvelope, Facts(@"c:\repo\.BIM\native-runs\run-1\bim-agentic-test.rvt")) is null, "drive paths compare case-insensitively against Document.PathName");
    Check(Admit(writeEnvelope, Facts(uid: NewUid)) is { Code: RevitCommandCodes.DocumentConflict, Field: "document.unique_id" }, "ProjectInformation.UniqueId different from the envelope is document_conflict");
    Check(Admit(writeEnvelope, Facts(uid: Uid.ToUpperInvariant())) is null, "UniqueId hex compares case-insensitively");
    Check(Admit(writeEnvelope, Facts(uid: null)) is { Code: RevitCommandCodes.DocumentConflict }, "missing ProjectInformation is document_conflict");
    Check(Admit(writeEnvelope, Facts(path: @"C:\Repo\.bim\native-runs\run-2\BIM-Agentic-Test.rvt")) is { Code: RevitCommandCodes.DocumentConflict, Field: "document.path" }, "another active document is document_conflict");
    Check(Admit(writeEnvelope, Facts(path: "")) is { Code: RevitCommandCodes.DocumentConflict }, "an unsaved active document (empty PathName) is document_conflict");
    Check(Admit(realQuery, Facts(RealPath)) is null, "read-only query is admitted on a production document");
    Check(Admit(queryEnvelope, Facts(readOnly: true, workshared: true)) is null, "read-only query is admitted on a read-only or workshared document");
    var policyCalls = 0;
    Admit(realQuery, Facts(RealPath), null, () => { policyCalls++; return policy; });
    Check(policyCalls == 0, "queries never load the allowlist");
    Check(Admit(realWrite, Facts(RealPath)) is { Code: RevitCommandCodes.DocumentNotEnabled, Field: "document.path" }, "write on a production document is document_not_enabled");
    Check(Admit(realWrite, Facts(RealPath), null, () => RevitCommandPolicy.None) is { Code: RevitCommandCodes.DocumentNotEnabled }, "write with no enabled root (no repository) is document_not_enabled");
    Check(Admit(writeEnvelope, Facts(), null, () => throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, "line 1", "too broad")) is { Code: RevitCommandCodes.AllowlistInvalid }, "an invalid owner allowlist refuses every write (allowlist_invalid)");
    var linkedRun = new RevitFileSystemProbe(path => path == @"C:\Repo\.bim\native-runs\run-1", _ => null);
    Check(Admit(writeEnvelope, Facts(), linkedRun) is { Code: RevitCommandCodes.DocumentLinkRefused }, "a junction between the enabled root and the opened document is document_link_refused");
    Check(Admit(writeEnvelope, Facts(), new RevitFileSystemProbe(_ => false, _ => 2)) is { Code: RevitCommandCodes.DocumentLinkRefused }, "an opened document with two hard links is document_link_refused");
    Check(Admit(writeEnvelope, Facts(), new RevitFileSystemProbe(_ => false, _ => -1)) is { Code: RevitCommandCodes.DocumentLinkRefused }, "an unreadable hard-link count fails closed");
    var probed = new List<string>();
    Admit(writeEnvelope, Facts(@"c:\repo\.bim\native-runs\run-1\bim-agentic-test.rvt"), new RevitFileSystemProbe(path => { probed.Add(path); return false; }, path => { probed.Add("links:" + path); return 1; }));
    Check(probed.Count > 0 && probed.All(path => path.StartsWith(@"c:\repo", StringComparison.Ordinal) || path.StartsWith(@"links:c:\repo", StringComparison.Ordinal)), "the link probe runs on Document.PathName as opened, not on the envelope text", string.Join(",", probed));
    Check(Admit(writeEnvelope, Facts(readOnly: true)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "write on a read-only document is document_not_enabled");
    Check(Admit(writeEnvelope, Facts(family: true)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "write on a family document is document_not_enabled");
    Check(Admit(writeEnvelope, Facts(workshared: true)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "write on a workshared document is document_not_enabled in v1");
    Check(Admit(writeEnvelope, Facts(modifiable: true)) is { Code: RevitCommandCodes.TransactionFailed }, "write while a transaction is already open is transaction_failed before any change");
    Check(CommandAdmission.SamePath(@"C:\A\b.rvt", @"c:/a/B.rvt") && !CommandAdmission.SamePath(@"C:\A\b.rvt", @"C:\A\b.rvt\x") && !CommandAdmission.SamePath("/a/B.rvt", "/a/b.rvt") && !CommandAdmission.SamePath("", @"C:\A"), "SamePath: whole-segment equality, case rules per path kind");

    // ---- Reference resolution ----
    var levels = new[] { new CommandCandidate(LevelUid, "Level 1"), new CommandCandidate(NewUid, "Level 2"), new CommandCandidate(WallUid, "Level 2") };
    Check(CommandReferences.Resolve("Level 1", levels, "args.level", "level") == 0, "a level resolves by exact name");
    Check(CommandReferences.Resolve(NewUid.ToUpperInvariant(), levels, "args.level", "level") == 1, "a reference shaped like a UniqueId resolves by UniqueId (hex case-insensitive)");
    Check(ErrorOf(() => CommandReferences.Resolve("level 1", levels, "args.level", "level")) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.level" }, "names are matched exactly (no case folding)");
    Check(ErrorOf(() => CommandReferences.Resolve("Level 2", levels, "args.level", "level")) is { Code: RevitCommandCodes.ReferenceNotFound } ambiguous && ambiguous.Detail!.Contains("UniqueId", StringComparison.Ordinal), "an ambiguous name is reference_not_found and asks for the UniqueId");
    Check(CodeOf(() => CommandReferences.Resolve(TypeUid, levels, "args.level", "level")) == RevitCommandCodes.ReferenceNotFound, "an unknown UniqueId is reference_not_found");
    var namedLikeUid = new[] { new CommandCandidate(LevelUid, TypeUid) };
    Check(CodeOf(() => CommandReferences.Resolve(TypeUid, namedLikeUid, "args.level", "level")) == RevitCommandCodes.ReferenceNotFound, "a UniqueId-shaped reference never falls back to a name");
    Check(CodeOf(() => CommandReferences.Resolve("Level 1", Array.Empty<CommandCandidate>(), "args.level", "level")) == RevitCommandCodes.ReferenceNotFound, "no candidates is reference_not_found");
    var symbols = new[] { new CommandCandidate(SymbolUid, "0915 x 2134mm", CommandReferences.Qualified("M_Single-Flush", "0915 x 2134mm")), new CommandCandidate(NewUid, "0915 x 2134mm", CommandReferences.Qualified("M_Double-Flush", "0915 x 2134mm")) };
    Check(CommandReferences.Resolve("M_Double-Flush: 0915 x 2134mm", symbols, "args.symbol", "family type") == 1, "a family type resolves by 'Family: Type'");
    Check(CodeOf(() => CommandReferences.Resolve("0915 x 2134mm", symbols, "args.symbol", "family type")) == RevitCommandCodes.ReferenceNotFound, "a type name shared by two families is ambiguous");

    // ---- Receipt store ----
    Check(CommandReceiptStore.SchemaGuid != Guid.Empty && CommandReceiptStore.SchemaGuid != new Guid("9f5a8262-a6b7-4817-8482-2fdb0359da87") && CommandReceiptStore.SchemaGuid != new Guid("be8622df-08dc-4842-95d5-787ff2a80a84"), "receipt schema GUID is its own, independent of the batch and room fixture schemas");
    var wallReadback = """{"wall":{"unique_id":"x"}}""";
    var success = CommandResponses.Success(bound, new[] { NewUid }, Array.Empty<string>(), wallReadback, new[] { "warning text" });
    Check(success.IsSuccess && success.Receipt == new RevitCommandReceipt("op-1", "wall.create", Uid, bound.RequestSha256, false), "success receipt echoes operation_id, command, document UniqueId and request_sha256, replayed false");
    var empty = CommandReceiptStore.Parse(null);
    Check(empty.Count == 0 && CommandReceiptStore.Decide(empty, "op-1", bound.RequestSha256) == CommandReceiptAction.Execute, "no stored receipt: execute");
    var stored = CommandReceiptStore.Record(bound, success);
    var receipts = new Dictionary<string, StoredCommandReceipt>(StringComparer.Ordinal) { ["op-1"] = stored };
    var payload = CommandReceiptStore.Serialize(receipts);
    var reread = CommandReceiptStore.Parse(payload);
    Check(reread.Count == 1 && reread["op-1"] == stored, "receipt payload round-trips");
    Check(CommandReceiptStore.Serialize(reread) == payload, "receipt payload serialization is deterministic");
    Check(CommandReceiptStore.Decide(reread, "op-1", bound.RequestSha256) == CommandReceiptAction.Replay, "same operation_id and same request_sha256: replay");
    Check(CommandReceiptStore.Decide(reread, "op-1", boundSpaced.RequestSha256) == CommandReceiptAction.Conflict, "same operation_id and different request_sha256: conflict");
    Check(CommandReceiptStore.Decide(reread, "op-2", bound.RequestSha256) == CommandReceiptAction.Execute, "another operation_id: execute");
    Check(CommandReceiptStore.Conflict("op-1") is { Code: RevitCommandCodes.OperationIdConflict, Field: "operation_id" }, "conflict is operation_id_conflict on operation_id");
    var replay = RevitCommandResponse.Parse(CommandReceiptStore.ReplayJson(reread["op-1"]));
    Check(replay.IsSuccess && replay.Receipt! == (success.Receipt! with { Replayed = true }) && replay.Created.SequenceEqual(success.Created) && replay.Warnings.SequenceEqual(success.Warnings) && replay.ReadbackJson == wallReadback, "replay returns the stored response with replayed true and nothing else changed");
    var twoPayload = CommandReceiptStore.Serialize(new Dictionary<string, StoredCommandReceipt>(StringComparer.Ordinal) { ["op-b"] = stored with { }, ["op-a"] = stored });
    Check(twoPayload.IndexOf("\"op-a\"", StringComparison.Ordinal) < twoPayload.IndexOf("\"op-b\"", StringComparison.Ordinal), "receipts are written in ordinal operation_id order");
    Check(CodeOf(() => CommandReceiptStore.Record(bound, RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed))) is not null, "a failure is never recorded as a receipt");
    var otherRequest = CommandWire.Bind(FixturePath, Uid, null, false, Canonical(Envelope("wall.create", Wall(), operationId: "op-9")));
    Check(CodeOf(() => CommandReceiptStore.Record(otherRequest, success)) is not null, "a response whose receipt answers another request is never recorded");
    foreach (var (label, bad) in new[]
    {
        ("not JSON", "nope"),
        ("array", "[]"),
        ("wrong version", payload.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)),
        ("extra member", payload[..^1] + ",\"x\":1}"),
        ("bad operation id key", payload.Replace("\"op-1\"", "\"-op\"", StringComparison.Ordinal)),
        ("tampered response", payload.Replace("\\u0022replayed\\u0022:false", "\\u0022replayed\\u0022:true", StringComparison.Ordinal).Replace("\\\"replayed\\\":false", "\\\"replayed\\\":true", StringComparison.Ordinal)),
        ("response for another hash", payload.Replace(bound.RequestSha256, new string('0', 64), StringComparison.Ordinal).Replace("\"request_sha256\":\"" + new string('0', 64) + "\"", "\"request_sha256\":\"" + bound.RequestSha256 + "\"", StringComparison.Ordinal)),
    })
        Check(CodeOf(() => CommandReceiptStore.Parse(bad)) == RevitCommandCodes.TransactionFailed && bad != payload, "unreadable receipt store fails closed: " + label);

    // ---- Expected geometry ----
    var line = CommandGeometry.Wall(new LineWallCurve(new(Length.FromMillimetres(0), Length.FromMillimetres(0)), new(Length.FromMillimetres(3000), Length.FromMillimetres(4000))));
    Check(!line.IsArc && line.LengthMm == 5000 && line.End == new PlanPoint(3000, 4000) && line.RadiusMm is null, "line wall: endpoints and length in millimetres");
    var semicircle = (WallCreateArgs)RevitCommandProtocol.Parse(Envelope("wall.create", new JsonObject { ["level"] = "Level 1", ["type"] = "T", ["height_mm"] = 3000, ["curve"] = new JsonObject { ["kind"] = "arc", ["start"] = Pair(0, 0), ["end"] = Pair(2000, 0), ["through"] = Pair(1000, 1000) } }).ToJsonString()).Args;
    var through = CommandGeometry.Wall(semicircle.Curve);
    Check(through.IsArc && Math.Abs(through.RadiusMm!.Value - 1000) < 1e-9 && Math.Abs(through.LengthMm - Math.PI * 1000) < 1e-6 && Math.Abs(through.Center!.X - 1000) < 1e-9 && Math.Abs(through.Center.Y) < 1e-9, "three-point arc: radius, centre and length r*sweep");
    var quarter = CommandGeometry.Wall(new ArcCenterWallCurve(new(Length.FromMillimetres(100), Length.FromMillimetres(200)), Length.FromMillimetres(1000), Angle.FromDegrees(0), Angle.FromDegrees(90)));
    Check(quarter.IsArc && Math.Abs(quarter.Start.X - 1100) < 1e-9 && Math.Abs(quarter.Start.Y - 200) < 1e-9 && Math.Abs(quarter.End.X - 100) < 1e-9 && Math.Abs(quarter.End.Y - 1200) < 1e-9 && Math.Abs(quarter.LengthMm - 500 * Math.PI) < 1e-6, "centre arc: degrees become radians, counter-clockwise endpoints, length r*sweep");
    Check(CommandGeometry.OpeningParameter(5000, 0, "args.position_along_mm") == 0 && CommandGeometry.OpeningParameter(5000, 1250, "args.position_along_mm") == 0.25 && CommandGeometry.OpeningParameter(5000, 5000, "args.position_along_mm") == 1, "opening position becomes a normalized curve parameter");
    Check(ErrorOf(() => CommandGeometry.OpeningParameter(5000, 5000.5, "args.position_along_mm")) is { Code: RevitCommandCodes.LengthInvalid, Field: "args.position_along_mm" }, "an opening beyond the host wall is length_invalid");
    Check(CommandGeometry.OpeningParameter(5000, 5000.0005, "args.position_along_mm") == 1, "within the readback tolerance the wall end is accepted and clamped");
    Check(CodeOf(() => CommandGeometry.OpeningParameter(0, 0, "args.position_along_mm")) == RevitCommandCodes.TransactionFailed, "a host wall with no length is transaction_failed");
    Point2 Mm(double x, double y) => new(Length.FromMillimetres(x), Length.FromMillimetres(y));
    Check(CommandGeometry.PolygonAreaMm2(new[] { Mm(0, 0), Mm(4000, 0), Mm(4000, 3000), Mm(0, 3000) }) == 12e6, "rectangle area in mm2");
    Check(CommandGeometry.PolygonAreaMm2(new[] { Mm(0, 0), Mm(0, 3000), Mm(4000, 3000), Mm(4000, 0) }) == 12e6, "clockwise outline has the same positive area");
    Check(Math.Abs(CommandGeometry.PolygonAreaMm2(new[] { Mm(9e7, 9e7), Mm(9e7 + 1000, 9e7), Mm(9e7 + 1000, 9e7 + 1000), Mm(9e7, 9e7 + 1000) }) - 1e6) < 1e-3, "area stays precise far from the origin (relative to the first vertex)");

    // ---- Readback checks ----
    ObservedWall Observed(PlanPoint start, PlanPoint end, double length, bool arc = false, double? radius = null, PlanPoint? center = null, double? height = 3000, double? offset = 0, bool? structural = false, string level = LevelUid, string type = TypeUid) =>
        new(NewUid, level, type, arc, start, end, length, radius, center, height, offset, structural);
    string? WallCheck(ExpectedWall expected, ObservedWall observed) => CommandReadback.Wall(expected, LevelUid, TypeUid, 3000, 0, false, observed);
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000)) is null, "wall readback matches");
    Check(WallCheck(line, Observed(new(3000, 4000), new(0, 0), 5000)) is null, "wall readback accepts reversed endpoints");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000.0005), 5000.0004)) is null, "wall readback tolerates sub-micrometre conversion noise");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000.01), 5000)) is not null, "wall endpoint off by 0.01 mm is a mismatch");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000, arc: true, radius: 1, center: new(0, 0))) is not null, "line requested but arc observed is a mismatch");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000, height: 2999)) is not null, "wall height mismatch");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000, height: null)) is not null, "unreadable wall height is a mismatch");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000, offset: 10)) is not null, "wall base offset mismatch");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000, structural: true)) is not null, "wall structural flag mismatch");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000, structural: null)) is null, "an absent structural parameter is not compared");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000, level: NewUid)) is not null, "wall level mismatch");
    Check(WallCheck(line, Observed(new(0, 0), new(3000, 4000), 5000, type: NewUid)) is not null, "wall type mismatch");
    Check(WallCheck(through, Observed(new(0, 0), new(2000, 0), Math.PI * 1000, true, 1000, new(1000, 0))) is null, "arc wall readback matches");
    Check(WallCheck(through, Observed(new(0, 0), new(2000, 0), Math.PI * 1000, true, 1001, new(1000, 0))) is not null, "arc radius mismatch");
    Check(WallCheck(through, Observed(new(0, 0), new(2000, 0), Math.PI * 1000, true, 1000, new(1000, 1))) is not null, "arc centre mismatch");
    Check(WallCheck(through, Observed(new(0, 0), new(2000, 0), 2000, false)) is not null, "arc requested but line observed is a mismatch");

    var opening = new ObservedOpening(NewUid, WallUid, SymbolUid, new PlanPoint(1500, 0), 900);
    Check(CommandReadback.Opening(WallUid, SymbolUid, new PlanPoint(1500, 0), 900, opening) is null, "opening readback matches");
    Check(CommandReadback.Opening(WallUid, SymbolUid, new PlanPoint(1500, 0), null, opening) is null, "sill is compared only when requested");
    Check(CommandReadback.Opening(WallUid, SymbolUid, new PlanPoint(1500, 0), 850, opening) is not null, "window sill mismatch");
    Check(CommandReadback.Opening(WallUid, SymbolUid, new PlanPoint(1500, 0), 900, opening with { SillMm = null }) is not null, "unreadable requested sill is a mismatch");
    Check(CommandReadback.Opening(WallUid, SymbolUid, new PlanPoint(1500, 0), null, opening with { HostUid = null }) is not null, "an opening without its host wall is a mismatch");
    Check(CommandReadback.Opening(WallUid, NewUid, new PlanPoint(1500, 0), null, opening) is not null, "opening family type mismatch");
    Check(CommandReadback.Opening(WallUid, SymbolUid, new PlanPoint(1500.5, 0), null, opening) is not null, "opening position mismatch");
    Check(CommandReadback.Opening(WallUid, SymbolUid, new PlanPoint(1500, 0), null, opening with { Point = null }) is not null, "opening without a location point is a mismatch");

    var floor = new ObservedFloor(NewUid, LevelUid, TypeUid, 12e6);
    Check(CommandReadback.Floor(LevelUid, TypeUid, 12e6, floor) is null, "floor readback matches");
    Check(CommandReadback.Floor(LevelUid, TypeUid, 12e6, floor with { AreaMm2 = 12e6 + 0.5 }) is null, "floor area tolerates rounding below 1 mm2");
    Check(CommandReadback.Floor(LevelUid, TypeUid, 12e6, floor with { AreaMm2 = 11.9e6 }) is not null, "floor area mismatch");
    Check(CommandReadback.Floor(LevelUid, TypeUid, 12e6, floor with { AreaMm2 = null }) is not null, "unreadable floor area is a mismatch");
    Check(CommandReadback.Floor(LevelUid, NewUid, 12e6, floor) is not null, "floor type mismatch");
    Check(CommandReadback.Floor(NewUid, TypeUid, 12e6, floor) is not null, "floor level mismatch");

    var room = new ObservedRoom(NewUid, LevelUid, "Kitchen", "101", 12e6);
    Check(CommandReadback.Room(LevelUid, "Kitchen", "101", room) is null, "room readback matches");
    Check(CommandReadback.Room(LevelUid, null, null, room with { Name = "Room", Number = "1" }) is null, "default name and number are not compared");
    Check(CommandReadback.Room(LevelUid, "Kitchen", "101", room with { AreaMm2 = 0 }) is { } notEnclosed && notEnclosed.Contains("enclosed", StringComparison.Ordinal), "a room with zero area is not enclosed: mismatch");
    Check(CommandReadback.Room(LevelUid, "Kitchen", "101", room with { Name = "Kitchen 101" }) is not null, "room name mismatch");
    Check(CommandReadback.Room(LevelUid, "Kitchen", "101", room with { Number = "102" }) is not null, "room number mismatch");
    Check(CommandReadback.Room(NewUid, null, null, room) is not null, "room level mismatch");

    // ---- Readback JSON ----
    var levelsJson = JsonNode.Parse(CommandReadback.LevelsJson(new[] { new LevelRow("Level 2", NewUid, 3000, 3000), new LevelRow("Level 1", LevelUid, 0, 0) }))!;
    Check(levelsJson["levels"]!.AsArray().Count == 2 && (string?)levelsJson["levels"]![0]!["name"] == "Level 1" && (double?)levelsJson["levels"]![1]!["elevation_mm"] == 3000 && (string?)levelsJson["levels"]![0]!["unique_id"] == LevelUid, "levels readback: name, unique_id, elevation_mm, ordered by elevation");
    var wallTypesJson = JsonNode.Parse(CommandReadback.WallTypesJson(new[] { new WallTypeRow("Generic - 200mm", TypeUid, "Basic", 200), new WallTypeRow("Curtain Wall", NewUid, "Curtain", null) }))!;
    Check((string?)wallTypesJson["wall_types"]![0]!["name"] == "Curtain Wall" && wallTypesJson["wall_types"]![0]!["width_mm"] is null && (double?)wallTypesJson["wall_types"]![1]!["width_mm"] == 200 && (string?)wallTypesJson["wall_types"]![1]!["kind"] == "Basic", "wall types readback: name order, kind, width_mm or null");
    var floorTypesJson = JsonNode.Parse(CommandReadback.FloorTypesJson(new[] { new FloorTypeRow("Generic 150mm", TypeUid, 150) }))!;
    Check((double?)floorTypesJson["floor_types"]![0]!["width_mm"] == 150 && (string?)floorTypesJson["floor_types"]![0]!["unique_id"] == TypeUid, "floor types readback: name, unique_id, width_mm");
    var symbolsJson = JsonNode.Parse(CommandReadback.SymbolsJson("door", new[] { new SymbolRow("M_Single-Flush", "0915 x 2134mm", SymbolUid, false, true) }))!;
    Check((string?)symbolsJson["category"] == "door" && (string?)symbolsJson["family_symbols"]![0]!["family"] == "M_Single-Flush" && (string?)symbolsJson["family_symbols"]![0]!["type"] == "0915 x 2134mm" && (bool?)symbolsJson["family_symbols"]![0]!["active"] == false && (bool?)symbolsJson["family_symbols"]![0]!["wall_hosted"] == true && (string?)symbolsJson["family_symbols"]![0]!["reference"] == "M_Single-Flush: 0915 x 2134mm", "family symbols readback: family, type, reference, unique_id, active, wall_hosted");
    var wallJson = JsonNode.Parse(CommandReadback.WallJson(Observed(new(0, 0), new(2000, 0), Math.PI * 1000, true, 1000, new(1000, 0))))!;
    Check((string?)wallJson["wall"]!["unique_id"] == NewUid && (string?)wallJson["wall"]!["curve"]!["kind"] == "arc" && (double?)wallJson["wall"]!["curve"]!["radius_mm"] == 1000 && (double?)wallJson["wall"]!["curve"]!["end"]![0] == 2000 && (double?)wallJson["wall"]!["height_mm"] == 3000, "wall readback JSON: curve kind, endpoints, radius, height");
    Check(JsonNode.Parse(CommandReadback.OpeningJson("window", opening))!["opening"]!["sill_mm"]!.GetValue<double>() == 900, "opening readback JSON carries the sill");
    Check(JsonNode.Parse(CommandReadback.FloorJson(floor))!["floor"]!["area_mm2"]!.GetValue<double>() == 12e6, "floor readback JSON carries the area");
    Check((string?)JsonNode.Parse(CommandReadback.RoomJson(room))!["room"]!["number"] == "101", "room readback JSON carries name and number");

    // ---- Add-in exposure: no pipe without a validated fixture bootstrap ----
    Check(!AddInGate.PipeAllowed(null, false) && !AddInGate.PipeAllowed("", true) && !AddInGate.PipeAllowed("   ", true), "no BIM_AGENTIC_TEST_CONFIG: the add-in opens no pipe, whatever else happened");
    Check(!AddInGate.PipeAllowed(@"C:\Repo\.bim\native-runs\run-1\config.json", false), "config set but bootstrap not validated and completed: no pipe");
    Check(AddInGate.PipeAllowed(@"C:\Repo\.bim\native-runs\run-1\config.json", true), "config set and bootstrap completed: the pipe may open");
    Check(AddInGate.ConfigVariable == "BIM_AGENTIC_TEST_CONFIG", "gate reads the same opt-in variable as FixtureBootstrap");

    // ---- Legacy primitive operations pass the same copies/link guard as revit.command writes ----
    foreach (var operation in new[] { "level.create", "wall.create", "wall.axis.update", "element.delete", "batch", "something.new" })
        Check(!AddInGate.IsLegacyRead(operation), "legacy " + operation + " is guarded as a write");
    Check(AddInGate.IsLegacyRead("inspect"), "legacy inspect is the only unguarded read");
    RevitCommandException? Legacy(string requestPath, CommandDocumentFacts facts, RevitFileSystemProbe? probe = null) =>
        ErrorOf(() => AddInGate.CheckLegacyWrite(requestPath, facts, () => policy, probe ?? noLinks));
    Check(Legacy(FixturePath, Facts()) is null, "legacy write on the enabled fixture is admitted");
    Check(Legacy(RealPath, Facts(RealPath)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "legacy write on a production document is document_not_enabled");
    Check(Legacy(FixturePath, Facts(), linkedRun) is { Code: RevitCommandCodes.DocumentLinkRefused }, "legacy write through a junction is document_link_refused");
    Check(Legacy(FixturePath, Facts(), new RevitFileSystemProbe(_ => false, _ => 2)) is { Code: RevitCommandCodes.DocumentLinkRefused }, "legacy write on a hard-linked document is document_link_refused");
    Check(Legacy(FixturePath, Facts(RealPath)) is { Code: RevitCommandCodes.DocumentConflict }, "legacy request naming another document than the active one is document_conflict");
    Check(Legacy(FixturePath, Facts(path: "")) is { Code: RevitCommandCodes.DocumentConflict }, "legacy write on an unsaved document is document_conflict");
    Check(Legacy(FixturePath, Facts(readOnly: true)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "legacy write on a read-only document is document_not_enabled");
    Check(Legacy(FixturePath, Facts(uid: null)) is null, "legacy writes do not carry the document UniqueId (their UniqueId is an element)");

    // ---- Catalogue v1 element rules ----
    Check(CommandRules.ReportsWallTypeWidth("Basic") && !CommandRules.ReportsWallTypeWidth("Stacked") && !CommandRules.ReportsWallTypeWidth("Curtain"), "wall type width is reported only for basic walls");
    Check(ErrorOf(() => CommandRules.CheckWallTypeForCreate("Stacked")) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.type" }, "stacked wall types are refused by wall.create (reference_not_found)");
    Check(ErrorOf(() => CommandRules.CheckWallTypeForCreate("Basic")) is null && ErrorOf(() => CommandRules.CheckWallTypeForCreate("Curtain")) is null, "basic and curtain wall types can be created");
    Check(ErrorOf(() => CommandRules.CheckOpeningHost("Curtain", true)) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.host_wall_uid" }, "a curtain wall cannot host a door or window");
    Check(ErrorOf(() => CommandRules.CheckOpeningHost("Basic", false)) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.host_wall_uid" } offCenter && offCenter.Detail!.Contains("Wall Centerline", StringComparison.Ordinal), "a host whose location line is not the wall centerline is refused");
    Check(ErrorOf(() => CommandRules.CheckOpeningHost("Basic", true)) is null && ErrorOf(() => CommandRules.CheckOpeningHost("Stacked", true)) is null, "basic and stacked hosts on their centerline are accepted");
    Check(CommandRules.OffersFloorType(isFoundationSlab: false) && !CommandRules.OffersFloorType(isFoundationSlab: true), "foundation slab types are not floor types for query or floor.create");

    // ---- Receipt store capacity ----
    Check(CommandReceiptStore.MaxReceipts == 2000 && CommandReceiptStore.MaxPayloadBytes == 4 * 1024 * 1024, "receipt store caps: 2000 receipts, 4 MB payload");
    var many = new Dictionary<string, StoredCommandReceipt>(StringComparer.Ordinal);
    for (var i = 0; i < CommandReceiptStore.MaxReceipts - 1; i++) many["op-" + i] = stored;
    Check(ErrorOf(() => CommandReceiptStore.EnsureRoom(many)) is null, "1999 receipts: room for one more");
    many["op-last"] = stored;
    Check(ErrorOf(() => CommandReceiptStore.EnsureRoom(many)) is { Code: RevitCommandCodes.TransactionFailed, Detail: "receipt store full" }, "2000 receipts: a new write is refused with 'receipt store full'");
    Check(CommandReceiptStore.SerializeBounded(many).Length > 0, "2000 small receipts still serialize");
    many["op-over"] = stored;
    Check(ErrorOf(() => CommandReceiptStore.SerializeBounded(many)) is { Code: RevitCommandCodes.TransactionFailed, Detail: "receipt store full" }, "2001 receipts are never persisted");
    var big = stored with { ResponseJson = stored.ResponseJson.Replace(wallReadback, "{\"wall\":{\"pad\":\"" + new string('x', 5 * 1024 * 1024) + "\"}}", StringComparison.Ordinal) };
    Check(ErrorOf(() => CommandReceiptStore.SerializeBounded(new Dictionary<string, StoredCommandReceipt>(StringComparer.Ordinal) { ["op-1"] = big })) is { Code: RevitCommandCodes.TransactionFailed, Detail: "receipt store full" }, "a payload over 4 MB is never persisted");
    Check(CommandReceiptStore.SerializeBounded(reread) == CommandReceiptStore.Serialize(reread), "bounded serialization equals the plain payload under the caps");

    // ---- End to end with the real client: CLI wire line in, executor response out ----
    string? FakeAddIn(string pipe, string sent, Func<CommandRequest, string> respond)
    {
        var request = JsonSerializer.Deserialize<WireRequest>(sent)!;
        try
        {
            var bind = CommandWire.Bind(request.DocumentPath, request.UniqueId, request.Metres, request.Batch is not null, request.Command?.GetRawText());
            CommandAdmission.Check(bind.Envelope, Facts(), () => policy, noLinks);
            return respond(bind);
        }
        catch (RevitCommandException error) { return CommandResponses.Failure(error); }
    }
    var replayStore = new Dictionary<string, StoredCommandReceipt>(StringComparer.Ordinal);
    var writes = 0;
    string Executor(CommandRequest request)
    {
        switch (CommandReceiptStore.Decide(replayStore, request.Envelope.OperationId, request.RequestSha256))
        {
            case CommandReceiptAction.Replay: return CommandReceiptStore.ReplayJson(replayStore[request.Envelope.OperationId]);
            case CommandReceiptAction.Conflict: throw CommandReceiptStore.Conflict(request.Envelope.OperationId);
        }
        writes++;
        var response = CommandResponses.Success(request, new[] { NewUid }, Array.Empty<string>(), CommandReadback.WallJson(Observed(new(0, 0), new(5000, 0), 5000)), Array.Empty<string>());
        replayStore[request.Envelope.OperationId] = CommandReceiptStore.Record(request, response);
        return response.ToJson();
    }
    var first = RevitCommandService.Run("bim-agentic-os-2025-42", wallEnvelope.ToJsonString(), policy, (pipe, sent) => FakeAddIn(pipe, sent, Executor), noLinks);
    Check(!first.IsError && first.Forwarded && writes == 1, "client accepts the executor's success: receipt hash binds the CLI's Command text", first.Line);
    var second = RevitCommandService.Run("bim-agentic-os-2025-42", wallEnvelope.ToJsonString(), policy, (pipe, sent) => FakeAddIn(pipe, sent, Executor), noLinks);
    Check(!second.IsError && writes == 1 && JsonNode.Parse(second.Line)!["receipt"]!["replayed"]!.GetValue<bool>(), "retry with the same envelope replays without a second write", second.Line);
    var changed = (JsonObject)wallEnvelope.DeepClone(); changed["args"]!["height_mm"] = 2800;
    var third = RevitCommandService.Run("bim-agentic-os-2025-42", changed.ToJsonString(), policy, (pipe, sent) => FakeAddIn(pipe, sent, Executor), noLinks);
    Check(third.IsError && writes == 1 && (string?)JsonNode.Parse(third.Line)!["code"] == RevitCommandCodes.OperationIdConflict, "same operation_id with different args is operation_id_conflict, no write", third.Line);
    var defaulted = (JsonObject)wallEnvelope.DeepClone(); defaulted["args"]!["base_offset_mm"] = 0;
    var fourth = RevitCommandService.Run("bim-agentic-os-2025-42", defaulted.ToJsonString(), policy, (pipe, sent) => FakeAddIn(pipe, sent, Executor), noLinks);
    Check(fourth.IsError && writes == 1 && (string?)JsonNode.Parse(fourth.Line)!["code"] == RevitCommandCodes.OperationIdConflict, "an explicit default value is a different request text (hash binds text, not meaning)", fourth.Line);
    var failure = RevitCommandService.Run("bim-agentic-os-2025-42", Envelope("query.levels", new JsonObject()).ToJsonString(), policy, (pipe, sent) => FakeAddIn(pipe, sent, _ => CommandResponses.Failure(new InvalidOperationException("Revit said no"))), noLinks);
    Check(failure.IsError && (string?)JsonNode.Parse(failure.Line)!["code"] == RevitCommandCodes.TransactionFailed && (string?)JsonNode.Parse(failure.Line)!["detail"] == "Revit said no", "an unexpected Revit exception is passed through as transaction_failed with Revit's message", failure.Line);
}
catch (Exception error)
{
    Check(false, "guard suite completed without an unexpected exception", error.GetType().Name + ": " + error.Message);
}

Console.WriteLine($"revit-command-guard: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

// Mirror of the add-in's Request shape (runtimes/revit-native/App.cs) as System.Text.Json reads it.
sealed record WireRequest(string DocumentPath, string Operation, string? UniqueId, double[] Metres, JsonElement? Batch = null, JsonElement? Checkpoint = null, JsonElement? Command = null);
