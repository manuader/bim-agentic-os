using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Cad;
using Bim.Core;
using Bim.Generative;

// P6-15 PDF drawing to ranked layouts end to end: tests/cad/fixtures/brief-plan.pdf, the vector PDF twin of
// brief-plan.dxf written by the committed stdlib generator tests/cad/fixtures/brief-plan-pdf.py (one page unit is 20 mm,
// page = mm / 20 + (100, 150); same closed regions in the same vertex order, same texts in the same order and heights),
// goes through the real Release CLI chain
//   cad-brief brief-plan.pdf draft.json (--calibrate 100,150,300,150,4000 | --metres-per-unit 0.02)
//   cad-plan-brief draft.json brief.json --site-width 10 --site-depth 8 --name "Brief plan" --brief-only
//   plan-layouts brief.json layouts.json --alternatives 3 --seed 7
// Oracles:
//   1. For each scale form, layouts.json is byte-identical to the in-process run BriefExtract.FromPdf(PdfReader.Parse(
//      bytes), page 1, calibration | MetresPerUnit) -> BriefPlanner.ToPlannerBrief(site 10 x 8, name) ->
//      SpacePlanner.Plan(brief, 7 + i), i = 0..2 -> AlternativeRanker.Rank -> AlternativeSetSerializer.Serialize (UTF-8,
//      no BOM), and its SHA-256 equals that form's pinned digest; the --brief-only file equals BriefSerializer.Serialize
//      of the in-process planner brief.
//   2. The fixture bytes have a pinned SHA-256, and on Linux the generator rewrites exactly those bytes (python3
//      required on Linux CI, a counted skip elsewhere or when absent off CI).
//   3. Twin: both drafts propose 7 rooms; for each scale form the PDF planner brief has the same rooms (ids, names,
//      counts, target areas within 1e-9 m2) as the brief-plan.dxf planner brief (provenance references differ by design:
//      file name, PDF operator vs DXF handle), and the in-process PDF layouts are byte-identical to the brief-plan.dxf
//      layouts, so both pins equal the P6-13 CadToLayoutsTests digest.
//   4. Without a scale: cad-draft brief-plan.pdf refuses with cad_uncalibrated and writes nothing; cad-brief is not a
//      refusal there (P6-07 contract: an uncalibrated PDF brief is allowed) but reports scale "none", metres_per_unit
//      null and the always-reported uncalibrated uncertainty.
//   5. A second calibrated chain writes the same bytes; the in-process runs under de-DE are the same; on Linux the
//      calibrated chain with every child under LANG/LC_ALL=de_DE.UTF-8 is the same (culture data required on Linux CI).
internal static class CadPdfToLayoutsTests
{
    // SHA-256 (lower-case hex) of tests/cad/fixtures/brief-plan.pdf as brief-plan-pdf.py writes it.
    const string FixtureDigest = "8af67cbaea8564f70de185ea7e1103557f839cc7d7afe2d729a54759af313146";
    // SHA-256 of layouts.json for brief-plan.pdf, site 10 x 8, name "Brief plan", 3 alternatives, seed 7.
    // Both equal the brief-plan.dxf digest pinned in CadToLayoutsTests (P6-13): layouts carry no drawing provenance.
    const string PinnedCalibrated = "566058984bb381768338f79137a18078bcc5ef6cfdfee0698226a7b84a86408a";
    const string PinnedMetresPerUnit = "566058984bb381768338f79137a18078bcc5ef6cfdfee0698226a7b84a86408a";
    const string Name = "Brief plan";
    const int Alternatives = 3;
    const int Seed = 7;
    static readonly string[] Calibrate = { "--calibrate", "100,150,300,150,4000" };
    static readonly string[] MetresPerUnit = { "--metres-per-unit", "0.02" };

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        var scratch = Path.Combine(work, "cad-pdf-to-layouts");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "cad-pdf-to-layouts: " + name, detail);

        (int Exit, string Out, string Err) Exec(string file, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("process did not start: " + file);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000))
            {
                try { process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
                return (-1, "<timed out>", "");
            }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result.TrimEnd('\n', '\r'), stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        (int Exit, string Out, string Err) Cli(IReadOnlyDictionary<string, string>? environment, params string[] arguments) =>
            Exec(dotnet, environment, new[] { cliDll }.Concat(arguments).ToArray());
        static string R((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static byte[] Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        static JsonElement? Envelope(string text)
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null; }
            catch (JsonException) { return null; }
        }

        var fixtures = Path.Combine(root, "tests", "cad", "fixtures");
        var pdfPath = Path.Combine(fixtures, "brief-plan.pdf");
        var pdfBytes = File.ReadAllBytes(pdfPath);
        var linux = OperatingSystem.IsLinux();
        var linuxCi = linux && Environment.GetEnvironmentVariable("CI") == "true";

        // ---- the fixture and its generator ----
        Check(Hex(pdfBytes) == FixtureDigest, "brief-plan.pdf SHA-256 equals the pinned fixture digest", "actual " + Hex(pdfBytes) + " pinned " + FixtureDigest);
        if (linux)
        {
            var regenerated = Path.Combine(scratch, "regenerated.pdf");
            (int Exit, string Out, string Err)? py = null;
            try { py = Exec("python3", null, "-I", Path.Combine(fixtures, "brief-plan-pdf.py"), regenerated); }
            catch (Win32Exception) { }
            if (py is null)
            {
                if (linuxCi) Check(false, "python3 is available on Linux CI to rerun the fixture generator");
                else skip("cad-pdf-to-layouts: fixture generator rerun", "python3 not found");
            }
            else Check(py.Value.Exit == 0 && Read(regenerated).SequenceEqual(pdfBytes), "brief-plan-pdf.py rewrites brief-plan.pdf byte for byte", R(py.Value) + " sha256 " + Hex(Read(regenerated)));
        }
        else skip("cad-pdf-to-layouts: fixture generator rerun", "run on Linux only (python3 launcher differs elsewhere)");

        // ---- in process: BriefExtract.FromPdf -> BriefPlanner (sited) -> SpacePlanner seeds 7..9 -> AlternativeRanker ----
        static PdfCalibration Calibration() => PdfCalibration.FromKnownDimension(new Bim.Cad.Point2(100, 150), new Bim.Cad.Point2(300, 150), 4000);
        static (byte[] Bytes, AlternativeSet Set, ProjectBrief Brief) Plan(BriefDraft draft)
        {
            var constraints = draft.Brief.Constraints with { Site = new BriefSite(new BriefValue<double>(10), new BriefValue<double>(8)) };
            var planned = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { Constraints = constraints, Name = Name });
            var plans = new SpacePlan[Alternatives];
            for (var index = 0; index < Alternatives; index++) plans[index] = SpacePlanner.Plan(planned.Brief, unchecked(Seed + index));
            var set = AlternativeRanker.Rank(planned.Brief, plans);
            return (Utf8(AlternativeSetSerializer.Serialize(set)), set, planned.Brief);
        }
        static (byte[] Bytes, AlternativeSet Set, ProjectBrief Brief) InProcess(byte[] pdf, bool calibrated) =>
            Plan(calibrated
                ? BriefExtract.FromPdf(PdfReader.Parse(pdf, "brief-plan.pdf"), 1, Calibration(), new BriefExtractOptions())
                : BriefExtract.FromPdf(PdfReader.Parse(pdf, "brief-plan.pdf"), 1, null, new BriefExtractOptions { MetresPerUnit = 0.02 }));

        var calibrated = InProcess(pdfBytes, true);
        var perUnit = InProcess(pdfBytes, false);
        foreach (var (label, run) in new[] { ("calibrated", calibrated), ("metres-per-unit", perUnit) })
            Check(run.Set.Status == AlternativeSetStatus.Ranked && run.Set.Ranked.Count >= 1, "oracle: the in-process " + label + " run ranks at least one alternative",
                run.Set.Status + " ranked " + run.Set.Ranked.Count.ToString(CultureInfo.InvariantCulture));

        // ---- twin: the same draft rooms, planner rooms and layouts as brief-plan.dxf ----
        var dxfDraft = BriefExtract.FromDxf(DxfReader.Parse(File.ReadAllBytes(Path.Combine(fixtures, "brief-plan.dxf")), "brief-plan.dxf"));
        var pdfDraft = BriefExtract.FromPdf(PdfReader.Parse(pdfBytes, "brief-plan.pdf"), 1, Calibration(), new BriefExtractOptions());
        var dxf = Plan(dxfDraft);
        Check(dxfDraft.Rooms.Count == 7 && pdfDraft.Rooms.Count == 7, "the brief-plan.dxf and calibrated brief-plan.pdf drafts both propose 7 rooms",
            "dxf " + dxfDraft.Rooms.Count.ToString(CultureInfo.InvariantCulture) + " pdf " + pdfDraft.Rooms.Count.ToString(CultureInfo.InvariantCulture));
        static string Rooms(ProjectBrief brief) => string.Join("; ", brief.Rooms.Select(r => r.Id + "|" + r.Name + "|" + r.EffectiveCount.ToString(CultureInfo.InvariantCulture) + "|"
            + (r.TargetAreaSquareMetres?.Value.ToString("R", CultureInfo.InvariantCulture) ?? "-")));
        foreach (var (label, run) in new[] { ("calibrated", calibrated), ("metres-per-unit", perUnit) })
        {
            var twin = run.Brief.Rooms.Count > 0 && run.Brief.Rooms.Count == dxf.Brief.Rooms.Count && run.Brief.Rooms.Zip(dxf.Brief.Rooms).All(p =>
                p.First.Id == p.Second.Id && p.First.Name == p.Second.Name && p.First.EffectiveCount == p.Second.EffectiveCount
                && (p.First.TargetAreaSquareMetres is null) == (p.Second.TargetAreaSquareMetres is null)
                && (p.First.TargetAreaSquareMetres is null || Math.Abs(p.First.TargetAreaSquareMetres.Value - p.Second.TargetAreaSquareMetres!.Value) <= 1e-9));
            Check(twin, label + ": the PDF planner brief has the brief-plan.dxf planner brief's rooms (ids, names, counts, target areas)",
                "pdf " + Rooms(run.Brief) + " dxf " + Rooms(dxf.Brief));
            Check(run.Bytes.SequenceEqual(dxf.Bytes), label + ": the in-process PDF layouts are byte-identical to the in-process brief-plan.dxf layouts (P6-13)",
                "pdf sha256 " + Hex(run.Bytes) + " dxf sha256 " + Hex(dxf.Bytes));
        }

        // ---- the real CLI chain ----
        var runs = 0;
        byte[] Chain(string label, string[] scale, ProjectBrief expectedBrief, IReadOnlyDictionary<string, string>? environment)
        {
            var dir = Path.Combine(scratch, (++runs).ToString(CultureInfo.InvariantCulture) + "-" + label);
            Directory.CreateDirectory(dir);
            var draftPath = Path.Combine(dir, "draft.json");
            var briefPath = Path.Combine(dir, "brief.json");
            var layoutsPath = Path.Combine(dir, "layouts.json");
            var a = Cli(environment, new[] { "cad-brief", pdfPath, draftPath }.Concat(scale).ToArray());
            Check(a.Exit == 0 && File.Exists(draftPath) && Envelope(a.Out) is { } ae && ae.TryGetProperty("format", out var format) && format.GetString() == "pdf",
                label + ": cad-brief " + string.Join(" ", scale) + " writes the draft from brief-plan.pdf", R(a));
            if (!File.Exists(draftPath)) return Array.Empty<byte>();
            var b = Cli(environment, "cad-plan-brief", draftPath, briefPath, "--site-width", "10", "--site-depth", "8", "--name", Name, "--brief-only");
            var briefOnly = Envelope(b.Out) is { } be && be.TryGetProperty("brief_only", out var flag) && flag.ValueKind == JsonValueKind.True
                && be.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.True;
            Check(b.Exit == 0 && File.Exists(briefPath) && briefOnly, label + ": cad-plan-brief --brief-only writes the sited planner brief (envelope brief_only and valid true)", R(b));
            if (!File.Exists(briefPath)) return Array.Empty<byte>();
            var briefBytes = Read(briefPath);
            var expectedText = BriefSerializer.Serialize(expectedBrief);
            Check(briefBytes.SequenceEqual(Utf8(expectedText)), label + ": the --brief-only file equals BriefSerializer.Serialize of the in-process planner brief",
                "cli " + Encoding.UTF8.GetString(briefBytes.Length > 400 ? briefBytes[..400] : briefBytes) + " in-process " + (expectedText.Length > 400 ? expectedText[..400] + "..." : expectedText));
            var c = Cli(environment, "plan-layouts", briefPath, layoutsPath, "--alternatives", Alternatives.ToString(CultureInfo.InvariantCulture), "--seed", Seed.ToString(CultureInfo.InvariantCulture));
            var written = Read(layoutsPath);
            Check(c.Exit == 0 && written.Length > 0, label + ": plan-layouts writes the ranked layouts", R(c));
            var reported = Envelope(c.Out) is { } ce && ce.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null;
            Check(reported == Hex(written), label + ": the plan-layouts envelope sha256 describes the file", c.Out);
            return written;
        }

        foreach (var (label, scale, expected, pinned) in new[] { ("calibrated", Calibrate, calibrated, PinnedCalibrated), ("metres-per-unit", MetresPerUnit, perUnit, PinnedMetresPerUnit) })
        {
            var written = Chain(label, scale, expected.Brief, null);
            Check(written.SequenceEqual(expected.Bytes), label + ": the CLI chain's layouts.json equals the in-process BriefExtract.FromPdf -> BriefPlanner -> SpacePlanner/AlternativeRanker bytes",
                "written " + written.Length.ToString(CultureInfo.InvariantCulture) + " sha256 " + Hex(written) + " expected " + expected.Bytes.Length.ToString(CultureInfo.InvariantCulture) + " sha256 " + Hex(expected.Bytes)
                + " first difference at " + FirstDifference(written, expected.Bytes).ToString(CultureInfo.InvariantCulture));
            Check(written.Length > 3 && !(written[0] == 0xEF && written[1] == 0xBB && written[2] == 0xBF), label + ": layouts.json has no byte order mark");
            Check(Hex(written) == pinned, label + ": layouts.json SHA-256 equals the pinned digest", "actual " + Hex(written) + " pinned " + pinned);
            Check(Hex(expected.Bytes) == pinned, label + ": the in-process run SHA-256 equals the pinned digest", "actual " + Hex(expected.Bytes) + " pinned " + pinned);
        }
        var first = Read(Path.Combine(scratch, "1-calibrated", "layouts.json"));
        var second = Chain("rerun", Calibrate, calibrated.Brief, null);
        Check(second.Length > 0 && second.SequenceEqual(first), "a second calibrated chain run writes byte-identical layouts", "sha256 " + Hex(second));

        // ---- without a scale ----
        {
            var output = Path.Combine(scratch, "uncalibrated-draft.json");
            var r = Cli(null, "cad-draft", pdfPath, output);
            Check(r.Exit == 1 && Envelope(r.Out) is { } e && e.TryGetProperty("status", out var status) && status.GetString() == "failed"
                && e.TryGetProperty("code", out var code) && code.GetString() == "cad_uncalibrated" && !File.Exists(output),
                "cad-draft brief-plan.pdf without --calibrate or --metres-per-unit refuses with cad_uncalibrated and writes nothing", R(r));
            var briefOutput = Path.Combine(scratch, "uncalibrated-brief.json");
            var u = Cli(null, "cad-brief", pdfPath, briefOutput);
            var reportsUncalibrated = false;
            if (File.Exists(briefOutput) && Envelope(File.ReadAllText(briefOutput)) is { } d && d.TryGetProperty("source", out var source) && d.TryGetProperty("uncertainties", out var uncertainties))
                reportsUncalibrated = source.TryGetProperty("scale", out var s) && s.GetString() == "none"
                    && source.TryGetProperty("metres_per_unit", out var m) && m.ValueKind == JsonValueKind.Null
                    && uncertainties.ValueKind == JsonValueKind.Array && uncertainties.EnumerateArray().Any(x => x.GetRawText().Contains("\"uncalibrated\"", StringComparison.Ordinal));
            Check(u.Exit == 0 && reportsUncalibrated, "cad-brief brief-plan.pdf without a scale is not a refusal but reports scale none, metres_per_unit null and the uncalibrated uncertainty", R(u));
        }

        // ---- cultures ----
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        var german = false;
        try { german = CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
        catch (CultureNotFoundException) { }
        if (!german)
        {
            if (linuxCi) Check(false, "de-DE culture data with a comma decimal separator exists on Linux CI", "CultureInfo.GetCultureInfo(\"de-DE\") unavailable or not comma-decimal");
            else skip("cad-pdf-to-layouts: culture checks", "de-DE culture data unavailable (invariant globalization)");
            return;
        }
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Check(InProcess(pdfBytes, true).Bytes.SequenceEqual(calibrated.Bytes), "the calibrated in-process run under de-DE current culture is byte-identical");
            Check(InProcess(pdfBytes, false).Bytes.SequenceEqual(perUnit.Bytes), "the metres-per-unit in-process run under de-DE current culture is byte-identical");
        }
        finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
        if (OperatingSystem.IsWindows())
        {
            skip("cad-pdf-to-layouts: child-process de_DE chain", "LANG/LC_ALL do not select the .NET culture on Windows");
            return;
        }
        var env = new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" };
        var germanChain = Chain("de_DE", Calibrate, calibrated.Brief, env);
        Check(germanChain.Length > 0 && germanChain.SequenceEqual(first), "the calibrated chain with every child process under de_DE writes byte-identical layouts", "sha256 " + Hex(germanChain));
    }

    static int FirstDifference(byte[] a, byte[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var index = 0; index < length; index++) if (a[index] != b[index]) return index;
        return a.Length == b.Length ? -1 : length;
    }
}
