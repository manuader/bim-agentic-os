using Bim.Core;
using Bim.Ifc;
using System.Text;
using System.Text.Json;

namespace Bim.TypedGeometry.Tests;

// ADR0008 step 1: bytes frozen from the pre-typed-geometry baseline (relay3). Persisted JSON,
// snapshot hashes, retry receipts and the IFC packet must stay byte-identical after option A.
public static class Goldens
{
    public const string ExamplesBeforeHash = "C7161D95963A8ABD6E32E3032CAA58D373B977690810665804949D417B0EB669";
    public const string ExamplesAfterHash = "770CA2E9B7FAB49E05EA6FF30B29CE8F4628280DDCEDD9F187E0E90AADABF8A1";
    public const string ExamplesPlanSha = "5F0E768AA3821E5745DA3D53D5082F70C3A265A15D7F9288A6FE34BAC8630EC9";
    public const string StoreInitializedSha = "B5CCBB668B9F19F430BAB80C4D646036A8F9E657CE05D12E20DFD7397826B1A0";
    public const string StoreCommittedSha = "25651C9F21775D1DB6C47F29E60C1A04A19B03E1723B0151F7208ABB3585FE47";
    public const string StoreJournalSha = "A7AF889C15FA41BCB2FC17406C83997F8F26CDD2B1FD272B7C07419379FE5833";
    public const string RoomIntentCompiledSha = "1654FCE81ACD4A2F17D712511CCF0C7643C3192772B914CA9617F3AE34F29EA1";
    public const string IfcBeforeHash = "329B5BC394C216C7D0237AD704C0B6118DE02B6AC4919E049625BE1F49CEE211";
    public const string IfcAfterHash = "D660B71A44B03589E864FA358543BCA7BEA6AC09A584922C4419A014EA76750C";
    public const string IfcPlanSha = "9CBAD8D4AD41064504739CC15DD87E038D0F5BC9A5B9F69331FEBFE212A70C37";
    public const string IfcCommittedSha = "78BCC4AE66869E73297FA26F4FAA3F0FEAA3A3ADED1DAABC7323F57529EA7E6C";
    public const string IfcRequestSha = "1D48F02604314D2B02BC25440E42333BFBFEFBB7E5C5C95391A0F64CB7786DEC";

    public static Project Example(string root, string name) => JsonSerializer.Deserialize<Project>(ExampleText(root, name)) ?? throw new InvalidOperationException("missing_example");

    static string ExampleText(string root, string name) => File.ReadAllText(Path.Combine(root, "examples", name)).TrimEnd('\n', '\r');

    public static RoomIntent RoomIntentExample(string root) => JsonSerializer.Deserialize<RoomIntent>(File.ReadAllText(Path.Combine(root, "examples", "room-intent.json"))) ?? throw new InvalidOperationException("missing_room_intent");

    public static void Run(Suite suite, string root)
    {
        foreach (var name in new[] { "before.json", "after.json" })
        {
            var text = ExampleText(root, name);
            suite.Equal("golden examples/" + name + " reserializes byte-identical", text, JsonSerializer.Serialize(Example(root, name)));
        }
        var before = Example(root, "before.json");
        var after = Example(root, "after.json");
        suite.Equal("golden examples before snapshot hash", ExamplesBeforeHash, Compiler.Hash(before));
        suite.Equal("golden examples after snapshot hash", ExamplesAfterHash, Compiler.Hash(after));
        var plan = Compiler.Diff(before, after);
        suite.Sha("golden examples plan json", ExamplesPlanSha, JsonSerializer.Serialize(plan));

        var directory = Path.Combine(Path.GetTempPath(), "bim-golden-" + Guid.NewGuid());
        try { Store(suite, directory, before, after, plan); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        var room = RoomCompiler.Compile(Guid.Parse("00000000-0000-0000-0000-000000000001"), 0, RoomIntentExample(root));
        suite.Sha("golden examples/room-intent.json compiled snapshot", RoomIntentCompiledSha, JsonSerializer.Serialize(room));

        var request = IfcCompiler.EvaluationRequest();
        var requestPlan = JsonSerializer.Deserialize<ExecutionPlan>(request.PlanJson) ?? throw new InvalidOperationException("missing_plan");
        suite.Equal("golden IFC evaluation before hash", IfcBeforeHash, requestPlan.BeforeHash);
        suite.Equal("golden IFC evaluation after hash", IfcAfterHash, requestPlan.AfterHash);
        suite.Equal("golden IFC evaluation PlanSha256", IfcPlanSha, request.PlanSha256);
        suite.Sha("golden IFC evaluation CommittedAfterJson", IfcCommittedSha, request.CommittedAfterJson);
        suite.Sha("golden IFC evaluation packet", IfcRequestSha, JsonSerializer.Serialize(request));
    }

    static void Store(Suite suite, string directory, Project before, Project after, ExecutionPlan plan)
    {
        var store = new ProjectStore(directory);
        store.Initialize(before);
        var snapshot = Path.Combine(directory, "project.json");
        suite.Sha("golden project.json after initialize", StoreInitializedSha, File.ReadAllText(snapshot));
        suite.Check(store.Commit(after, plan, "golden").Revision == 1, "golden commit publishes revision+1");
        suite.Sha("golden project.json with retry receipt after commit", StoreCommittedSha, File.ReadAllText(snapshot));
        suite.Sha("golden checkpoint-0.json", ExamplesBeforeHash, File.ReadAllText(Path.Combine(directory, "checkpoint-0.json")));
        suite.Sha("golden journal.jsonl", StoreJournalSha, File.ReadAllText(Path.Combine(directory, "journal.jsonl")).Replace(Environment.NewLine, "\n"));
        // The text hashes above hide a BOM (ReadAllText strips it) and CR/LF changes (journal is normalized): pin those bytes too.
        var newLine = Encoding.UTF8.GetBytes(Environment.NewLine);
        foreach (var file in new[] { "project.json", "checkpoint-0.json", "journal.jsonl" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, file));
            suite.Check(bytes.Length > 0 && bytes[0] != 0xEF, "golden " + file + " has no UTF-8 BOM");
            var body = file == "journal.jsonl" && bytes.AsSpan().EndsWith(newLine) ? bytes.AsSpan(0, bytes.Length - newLine.Length) : bytes.AsSpan();
            suite.Check(!body.Contains((byte)'\r') && !body.Contains((byte)'\n'), "golden " + file + " has no CR or LF in its body");
        }
        suite.Check(File.ReadAllBytes(Path.Combine(directory, "journal.jsonl")).AsSpan().EndsWith(newLine), "golden journal.jsonl ends with Environment.NewLine exactly once");
        suite.Check(new ProjectStore(directory).Commit(after, plan, "golden").Revision == 1, "golden retry receipt replays");
        suite.Sha("golden project.json unchanged by retry", StoreCommittedSha, File.ReadAllText(snapshot));
    }
}
