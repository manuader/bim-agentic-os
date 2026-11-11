using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Cad;
using Bim.Core;
using Bim.Generative;

// P6-13 DXF to ranked layouts end to end: tests/cad/fixtures/brief-plan.dxf goes through the real Release CLI chain
//   cad-brief brief-plan.dxf draft.json
//   cad-plan-brief draft.json planner.json --site-width 10 --site-depth 8 --name "Brief plan"
//   (the planner document's "brief" member, its raw bytes unchanged, saved as brief.json: plan-layouts reads a bare
//    P6-03 brief, not the cad-plan-brief envelope)
//   plan-layouts brief.json layouts.json --alternatives 3 --seed 7
// Oracles:
//   1. layouts.json is byte-identical to the in-process run BriefExtract.FromDxf(DxfReader.Parse(bytes)) ->
//      BriefPlanner.ToPlannerBrief(draft, site 10 x 8, name) -> SpacePlanner.Plan(brief, 7 + i), i = 0..2 ->
//      AlternativeRanker.Rank -> AlternativeSetSerializer.Serialize (UTF-8, no BOM), computed on the library's own
//      ProjectBrief object (no JSON round trip on the oracle side).
//   2. Its SHA-256 equals the pinned digest below, and the set is ranked with at least one alternative.
//   3. A second full chain run writes the same bytes; the in-process run under de-DE current culture is the same too;
//      the planner document's brief member equals BriefSerializer.Serialize of the in-process planner brief (so a
//      difference is localised to the cad stages before plan-layouts).
// Cultures: the de-DE culture data must exist on Linux CI (OperatingSystem.IsLinux() and CI=true): a missing culture
// there FAILS, elsewhere it is a counted skip. The child-process run sets LANG/LC_ALL=de_DE.UTF-8, which .NET reads
// for CurrentCulture only on Unix; on Windows it has no effect, so that check is a counted skip there. No existing CLI
// verb echoes its current culture, so there is no cheap positive probe that the child really ran under de-DE: the
// evidence is the in-process de-DE separator check (the ICU data exists for the same runtime) plus .NET's documented
// Unix behaviour (LC_ALL > LANG selects CurrentCulture).
internal static class CadToLayoutsTests
{
    // SHA-256 (lower-case hex) of layouts.json for brief-plan.dxf, site 10 x 8, name "Brief plan", 3 alternatives, seed 7.
    const string PinnedDigest = "566058984bb381768338f79137a18078bcc5ef6cfdfee0698226a7b84a86408a";
    const string Name = "Brief plan";
    const int Alternatives = 3;
    const int Seed = 7;

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        var scratch = Path.Combine(work, "cad-to-layouts");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "cad-to-layouts: " + name, detail);

        (int Exit, string Out, string Err) Cli(IReadOnlyDictionary<string, string>? environment, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000))
            {
                try { process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                return (-1, "<timed out>", "");
            }
            process.WaitForExit();
            var text = stdout.Result.TrimEnd('\n', '\r');
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        static string R((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static byte[] Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();

        var dxfPath = Path.Combine(root, "tests", "cad", "fixtures", "brief-plan.dxf");
        var dxfBytes = File.ReadAllBytes(dxfPath);

        // ---- in process: BriefExtract -> BriefPlanner (sited) -> SpacePlanner seeds 7..9 -> AlternativeRanker ----
        static (byte[] Bytes, AlternativeSet Set, ProjectBrief Brief) InProcessSet(byte[] dxf)
        {
            var draft = BriefExtract.FromDxf(DxfReader.Parse(dxf, "brief-plan.dxf"));
            var constraints = draft.Brief.Constraints with { Site = new BriefSite(new BriefValue<double>(10), new BriefValue<double>(8)) };
            var planned = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { Constraints = constraints, Name = Name });
            var plans = new SpacePlan[Alternatives];
            for (var index = 0; index < Alternatives; index++) plans[index] = SpacePlanner.Plan(planned.Brief, unchecked(Seed + index));
            var set = AlternativeRanker.Rank(planned.Brief, plans);
            return (Utf8(AlternativeSetSerializer.Serialize(set)), set, planned.Brief);
        }
        static byte[] InProcess(byte[] dxf) => InProcessSet(dxf).Bytes;
        var (expected, expectedSet, expectedBrief) = InProcessSet(dxfBytes);
        var expectedBriefText = BriefSerializer.Serialize(expectedBrief);
        Check(expectedSet.Status == AlternativeSetStatus.Ranked && expectedSet.Ranked.Count >= 1, "oracle: the in-process run ranks at least one alternative",
            expectedSet.Status + " ranked " + expectedSet.Ranked.Count.ToString(CultureInfo.InvariantCulture));

        // ---- the real CLI chain ----
        var runs = 0;
        byte[] Chain(string label, IReadOnlyDictionary<string, string>? environment)
        {
            var dir = Path.Combine(scratch, (++runs).ToString(CultureInfo.InvariantCulture) + "-" + label);
            Directory.CreateDirectory(dir);
            var draftPath = Path.Combine(dir, "draft.json");
            var plannerPath = Path.Combine(dir, "planner.json");
            var briefPath = Path.Combine(dir, "brief.json");
            var layoutsPath = Path.Combine(dir, "layouts.json");
            var a = Cli(environment, "cad-brief", dxfPath, draftPath);
            Check(a.Exit == 0 && File.Exists(draftPath), label + ": cad-brief writes the draft from brief-plan.dxf", R(a));
            if (!File.Exists(draftPath)) return Array.Empty<byte>();
            var b = Cli(environment, "cad-plan-brief", draftPath, plannerPath, "--site-width", "10", "--site-depth", "8", "--name", Name);
            Check(b.Exit == 0 && File.Exists(plannerPath), label + ": cad-plan-brief writes the sited planner brief", R(b));
            if (!File.Exists(plannerPath)) return Array.Empty<byte>();
            string? briefText = null;
            try
            {
                using var planner = JsonDocument.Parse(File.ReadAllBytes(plannerPath));
                if (planner.RootElement.GetProperty("valid").GetBoolean()) briefText = planner.RootElement.GetProperty("brief").GetRawText();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { Check(false, label + ": the planner document parses", e.Message); }
            Check(briefText is not null, label + ": the planner document is valid and carries a brief member", Encoding.UTF8.GetString(Read(plannerPath)));
            if (briefText is null) return Array.Empty<byte>();
            Check(briefText == expectedBriefText, label + ": the planner brief member equals BriefSerializer.Serialize of the in-process planner brief",
                "cli " + (briefText.Length > 400 ? briefText[..400] + "..." : briefText) + " in-process " + (expectedBriefText.Length > 400 ? expectedBriefText[..400] + "..." : expectedBriefText));
            File.WriteAllBytes(briefPath, Utf8(briefText));
            var c = Cli(environment, "plan-layouts", briefPath, layoutsPath, "--alternatives", Alternatives.ToString(CultureInfo.InvariantCulture), "--seed", Seed.ToString(CultureInfo.InvariantCulture));
            var written = Read(layoutsPath);
            Check(c.Exit == 0 && written.Length > 0, label + ": plan-layouts writes the ranked layouts", R(c));
            string? reported = null;
            try
            {
                using var envelope = JsonDocument.Parse(c.Out);
                if (envelope.RootElement.ValueKind == JsonValueKind.Object && envelope.RootElement.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String) reported = sha.GetString();
            }
            catch (JsonException) { }
            Check(reported == Hex(written), label + ": the plan-layouts envelope sha256 describes the file", c.Out);
            return written;
        }

        var first = Chain("first", null);
        Check(first.SequenceEqual(expected), "the CLI chain's layouts.json equals the in-process BriefExtract -> BriefPlanner -> SpacePlanner/AlternativeRanker bytes",
            "written " + first.Length.ToString(CultureInfo.InvariantCulture) + " sha256 " + Hex(first) + " expected " + expected.Length.ToString(CultureInfo.InvariantCulture) + " sha256 " + Hex(expected)
            + " first difference at " + FirstDifference(first, expected).ToString(CultureInfo.InvariantCulture));
        Check(first.Length > 3 && !(first[0] == 0xEF && first[1] == 0xBB && first[2] == 0xBF), "layouts.json has no byte order mark");
        Check(Hex(first) == PinnedDigest, "layouts.json SHA-256 equals the pinned digest", "actual " + Hex(first) + " pinned " + PinnedDigest);
        Check(Hex(expected) == PinnedDigest, "the in-process run SHA-256 equals the pinned digest", "actual " + Hex(expected) + " pinned " + PinnedDigest);

        var second = Chain("rerun", null);
        Check(second.Length > 0 && second.SequenceEqual(first), "a second full chain run writes byte-identical layouts", "sha256 " + Hex(second));

        // ---- cultures ----
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        var german = false;
        try { german = CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
        catch (CultureNotFoundException) { }
        var linuxCi = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("CI") == "true";
        if (!german)
        {
            if (linuxCi) Check(false, "de-DE culture data with a comma decimal separator exists on Linux CI", "CultureInfo.GetCultureInfo(\"de-DE\") unavailable or not comma-decimal");
            else skip("cad-to-layouts: culture checks", "de-DE culture data unavailable (invariant globalization)");
            return;
        }
        Check(true, "de-DE culture data with a comma decimal separator exists");
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var inGerman = InProcess(dxfBytes);
            Check(inGerman.SequenceEqual(expected), "the in-process run under de-DE current culture is byte-identical", "sha256 " + Hex(inGerman));
        }
        finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
        if (OperatingSystem.IsWindows())
        {
            skip("cad-to-layouts: child-process de_DE chain", "LANG/LC_ALL do not select the .NET culture on Windows");
            return;
        }
        var env = new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" };
        var germanChain = Chain("de_DE", env);
        Check(germanChain.Length > 0 && germanChain.SequenceEqual(first), "the chain with every child process under de_DE writes byte-identical layouts", "sha256 " + Hex(germanChain));
    }

    static int FirstDifference(byte[] a, byte[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var index = 0; index < length; index++) if (a[index] != b[index]) return index;
        return a.Length == b.Length ? -1 : length;
    }
}
