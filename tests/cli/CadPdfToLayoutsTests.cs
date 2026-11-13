using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
//   2. The fixture bytes have a pinned SHA-256, and on Linux and macOS the generator rewrites exactly those bytes
//      (python3 required on Linux CI; a counted skip on Windows or when python3 is absent elsewhere).
//   3. Twin: both drafts propose 7 rooms with the same sorted uncertainty codes and the same room ids and confidences;
//      the planner keeps and drops the pinned rooms (KeptRooms, DroppedRooms); for each scale form the PDF planner brief has the same rooms (ids, names,
//      counts, target areas within 1e-9 m2) as the brief-plan.dxf planner brief (provenance references differ by design:
//      file name, PDF operator vs DXF handle), and the in-process PDF layouts are byte-identical to the brief-plan.dxf
//      layouts, so both pins equal the P6-13 CadToLayoutsTests digest.
//   4. Without a scale: cad-draft brief-plan.pdf refuses with cad_uncalibrated and writes nothing; cad-brief is not a
//      refusal there (P6-07 contract: an uncalibrated PDF brief is allowed) but reports scale "none", metres_per_unit
//      null and the always-reported uncalibrated uncertainty.
//   5. A second calibrated chain writes the same bytes; the in-process runs under de-DE are the same; on Linux the
//      calibrated chain with every child under LANG/LC_ALL=de_DE.UTF-8 is the same (culture data required on Linux CI).
// P6-17 compressed and Form XObject twin: tests/cad/fixtures/brief-plan-xobject.pdf (brief-plan-pdf.py --variant
// xobject-flate) draws the same regions in a FlateDecode page content stream (object 4) and shows every text from the
// FlateDecode Form XObject /Labels (object 6, /Matrix [1 0 0 1 100 150]) invoked by the page's operator 49.
//   6. The variant bytes have a pinned SHA-256 and the generator rewrites them on Linux and macOS; no text, path or Do
//      operator is readable in the raw file (both streams FlateDecode) and the form is a /Subtype /Form.
//   7. The parsed page has brief-plan.pdf's texts (value, origin, height, size) and paths at the same points; every
//      text carries XObjectPath ["o4:i49"] and object 6, every path an empty XObjectPath; the warning codes are equal.
//   8. For each scale form the variant draft has the brief-plan.pdf draft's uncertainty codes and room ids and
//      confidences; each room label (and text area) source has InsertPath ["o4:i49"] and a handle "p1:o6:i{n}@o4:i49",
//      each region source an empty InsertPath; the planner keeps and drops the pinned rooms; the planner brief equals the
//      brief-plan.pdf planner brief once provenance references are removed, and every room reference names the
//      XObject path; the layouts are byte-identical to brief-plan.pdf's and equal the same pinned digest.
//   9. Through the real CLI chain the variant layouts equal the in-process bytes and the pinned digest, the --brief-only
//      file equals the in-process planner brief, and draft.json / brief.json carry the XObject path.
// P6-19 nested and scaled Form XObject twins (uncompressed, brief-plan-pdf.py --variant nested-scaled |
// nested-scaled-regions): brief-plan-nested.pdf draws the regions in the page content (object 4) and shows every text
// from the Form XObject /Labels (object 7, /Matrix [1 0 0 1 5 -5]) invoked by operator 1 of the scaled Form XObject
// /Outer (object 6, /Matrix [2 0 0 2 100 150]) invoked by the page's operator 49, at half the font size;
// brief-plan-nested-regions.pdf also draws the regions from the scaled Form XObject /Rooms (object 8, page operator 1;
// /Outer is then operator 2).
//  10. Both fixtures have pinned SHA-256 digests that the generator rewrites on Linux and macOS.
//  11. Each parsed page has brief-plan.pdf's texts (value, page origin, height, rotation; half the font size) and paths
//      at the same points: texts carry the two-deep XObjectPath ["o4:i49", "o6:i1"] (["o4:i2", "o6:i1"]) and object 7,
//      paths an empty XObjectPath and object 4 (["o4:i1"] and object 8); the warning codes are equal.
//  12. For each scale form, brief-plan-nested.pdf (default options) and brief-plan-nested-regions.pdf (IncludeBlockRegions)
//      give brief-plan.pdf's draft codes, room ids and confidences, with every label, area and region source carrying
//      its XObject path; the planner keeps and drops the pinned rooms; the planner brief equals brief-plan.pdf's once
//      provenance references are removed and every room reference names the nested path; the layouts equal the
//      pinned digest. Without IncludeBlockRegions no room of brief-plan-nested-regions.pdf takes a /Rooms region.
//  13. Through the real CLI chain brief-plan-nested.pdf's layouts equal the in-process bytes and the pinned digest, the
//      --brief-only file equals the in-process planner brief, and draft.json / brief.json carry the nested path.
internal static class CadPdfToLayoutsTests
{
    // SHA-256 (lower-case hex) of tests/cad/fixtures/brief-plan.pdf as brief-plan-pdf.py writes it.
    const string FixtureDigest = "8af67cbaea8564f70de185ea7e1103557f839cc7d7afe2d729a54759af313146";
    // SHA-256 of layouts.json for brief-plan.pdf, site 10 x 8, name "Brief plan", 3 alternatives, seed 7.
    // Both equal the brief-plan.dxf digest pinned in CadToLayoutsTests (P6-13): layouts carry no drawing provenance.
    const string PinnedCalibrated = "566058984bb381768338f79137a18078bcc5ef6cfdfee0698226a7b84a86408a";
    const string PinnedMetresPerUnit = "566058984bb381768338f79137a18078bcc5ef6cfdfee0698226a7b84a86408a";
    // The planner decision for both scale forms (and brief-plan.dxf): kept room ids in order, dropped id:unconfirmed reasons.
    const string KeptRooms = "dormitorio-1,cocina,living-room,bano-2,lavadero,hall";
    const string DroppedRooms = "bano:area-mismatch,low-confidence";
    // SHA-256 of tests/cad/fixtures/brief-plan-xobject.pdf as brief-plan-pdf.py --variant xobject-flate writes it (P6-17).
    const string VariantFixtureDigest = "13b8e667c519b0d2222eb8f57549d61a595d1ec5eb5b32525a451fd5fc455551";
    const string VariantFile = "brief-plan-xobject.pdf";
    // The invoking Do operator of the labels form: page content object 4, operator index 49 (1 w, then 8 x 6 path operators).
    const string VariantXObjectPath = "o4:i49";
    const int VariantFormObject = 6;
    // P6-19: SHA-256 of the nested and scaled Form XObject twins as brief-plan-pdf.py --variant nested-scaled |
    // nested-scaled-regions writes them, the form object numbers and the invoking Do chains of texts and regions.
    const string NestedFixtureDigest = "f9b7d23122905725b4d9c2aa339a135de26de353e9166d7c09198c02c9e2dfdb";
    const string NestedRegionsFixtureDigest = "ac1305d0850c55645fb799354f5d365742a4bdafa683830074f4fd6bc9e2af83";
    const string NestedFile = "brief-plan-nested.pdf";
    const string NestedRegionsFile = "brief-plan-nested-regions.pdf";
    const int NestedLabelsFormObject = 7;
    const int NestedRoomsFormObject = 8;
    static readonly string[] NestedTextPath = { "o4:i49", "o6:i1" };
    static readonly string[] NestedRegionsTextPath = { "o4:i2", "o6:i1" };
    static readonly string[] NestedRegionsPath = { "o4:i1" };
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
        var unix = linux || OperatingSystem.IsMacOS();

        // ---- the fixture and its generator ----
        Check(Hex(pdfBytes) == FixtureDigest, "brief-plan.pdf SHA-256 equals the pinned fixture digest", "actual " + Hex(pdfBytes) + " pinned " + FixtureDigest);
        if (unix)
        {
            var regenerated = Path.Combine(scratch, "regenerated.pdf");
            (int Exit, string Out, string Err)? py = null;
            try { py = Exec("python3", null, "-I", Path.Combine(fixtures, "brief-plan-pdf.py"), regenerated); }
            catch (Win32Exception) { }
            if (py is null)
            {
                if (linuxCi) Check(false, "python3 is available on Linux CI to rerun the fixture generator");
                else skip("cad-pdf-to-layouts: fixture generator rerun", "python3 not found (required on Linux CI only)");
            }
            else Check(py.Value.Exit == 0 && Read(regenerated).SequenceEqual(pdfBytes), "brief-plan-pdf.py rewrites brief-plan.pdf byte for byte", R(py.Value) + " sha256 " + Hex(Read(regenerated)));
        }
        else skip("cad-pdf-to-layouts: fixture generator rerun", "run on Linux and macOS only (the python3 launcher differs on Windows)");

        // ---- in process: BriefExtract.FromPdf -> BriefPlanner (sited) -> SpacePlanner seeds 7..9 -> AlternativeRanker ----
        static PdfCalibration Calibration() => PdfCalibration.FromKnownDimension(new Bim.Cad.Point2(100, 150), new Bim.Cad.Point2(300, 150), 4000);
        static (byte[] Bytes, AlternativeSet Set, ProjectBrief Brief, BriefPlannerResult Planned) Plan(BriefDraft draft)
        {
            var constraints = draft.Brief.Constraints with { Site = new BriefSite(new BriefValue<double>(10), new BriefValue<double>(8)) };
            var planned = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { Constraints = constraints, Name = Name });
            var plans = new SpacePlan[Alternatives];
            for (var index = 0; index < Alternatives; index++) plans[index] = SpacePlanner.Plan(planned.Brief, unchecked(Seed + index));
            var set = AlternativeRanker.Rank(planned.Brief, plans);
            return (Utf8(AlternativeSetSerializer.Serialize(set)), set, planned.Brief, planned);
        }
        static BriefDraft Draft(byte[] pdf, bool calibrated) => calibrated
            ? BriefExtract.FromPdf(PdfReader.Parse(pdf, "brief-plan.pdf"), 1, Calibration(), new BriefExtractOptions())
            : BriefExtract.FromPdf(PdfReader.Parse(pdf, "brief-plan.pdf"), 1, null, new BriefExtractOptions { MetresPerUnit = 0.02 });
        static (byte[] Bytes, AlternativeSet Set, ProjectBrief Brief, BriefPlannerResult Planned) InProcess(byte[] pdf, bool calibrated) => Plan(Draft(pdf, calibrated));

        var calibrated = InProcess(pdfBytes, true);
        var perUnit = InProcess(pdfBytes, false);
        foreach (var (label, run) in new[] { ("calibrated", calibrated), ("metres-per-unit", perUnit) })
            Check(run.Set.Status == AlternativeSetStatus.Ranked && run.Set.Ranked.Count >= 1, "oracle: the in-process " + label + " run ranks at least one alternative",
                run.Set.Status + " ranked " + run.Set.Ranked.Count.ToString(CultureInfo.InvariantCulture));

        // ---- twin: the same draft rooms, planner rooms and layouts as brief-plan.dxf ----
        var dxfDraft = BriefExtract.FromDxf(DxfReader.Parse(File.ReadAllBytes(Path.Combine(fixtures, "brief-plan.dxf")), "brief-plan.dxf"));
        var dxf = Plan(dxfDraft);
        static string Codes(BriefDraft draft) => string.Join(",", draft.Uncertainties.Select(u => u.Code).OrderBy(c => c, StringComparer.Ordinal));
        static string Confidences(BriefDraft draft) => string.Join("; ", draft.Rooms.Select(r => r.Id + "=" + r.Confidence.ToString("R", CultureInfo.InvariantCulture)));
        foreach (var (label, pdfDraft) in new[] { ("calibrated", Draft(pdfBytes, true)), ("metres-per-unit", Draft(pdfBytes, false)) })
        {
            Check(dxfDraft.Rooms.Count == 7 && pdfDraft.Rooms.Count == 7, label + ": the brief-plan.dxf and brief-plan.pdf drafts both propose 7 rooms",
                "dxf " + dxfDraft.Rooms.Count.ToString(CultureInfo.InvariantCulture) + " pdf " + pdfDraft.Rooms.Count.ToString(CultureInfo.InvariantCulture));
            Check(Codes(pdfDraft) == Codes(dxfDraft) && pdfDraft.Uncertainties.Count > 0, label + ": the PDF draft's sorted uncertainty codes equal the brief-plan.dxf draft's",
                "pdf " + Codes(pdfDraft) + " dxf " + Codes(dxfDraft));
            Check(Confidences(pdfDraft) == Confidences(dxfDraft), label + ": each PDF draft room has the brief-plan.dxf room's id and confidence (exact)",
                "pdf " + Confidences(pdfDraft) + " dxf " + Confidences(dxfDraft));
        }
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
            var kept = string.Join(",", run.Planned.Kept.Select(r => r.Id));
            var dropped = string.Join("; ", run.Planned.Dropped.Select(r => r.Id + ":" + string.Join(",", r.Unconfirmed)));
            Check(kept == KeptRooms, label + ": the planner keeps exactly the pinned rooms", "actual " + kept + " pinned " + KeptRooms);
            Check(dropped == DroppedRooms, label + ": the planner drops exactly the pinned rooms with their unconfirmed reasons", "actual " + dropped + " pinned " + DroppedRooms);
            Check(run.Bytes.SequenceEqual(dxf.Bytes), label + ": the in-process PDF layouts are byte-identical to the in-process brief-plan.dxf layouts (P6-13)",
                "pdf sha256 " + Hex(run.Bytes) + " dxf sha256 " + Hex(dxf.Bytes));
        }

        // ---- the real CLI chain ----
        var runs = 0;
        var lastChainDir = "";
        byte[] Chain(string label, string[] scale, ProjectBrief expectedBrief, IReadOnlyDictionary<string, string>? environment, string? input = null)
        {
            var source = input ?? pdfPath;
            var dir = Path.Combine(scratch, (++runs).ToString(CultureInfo.InvariantCulture) + "-" + label);
            Directory.CreateDirectory(dir);
            lastChainDir = dir;
            var draftPath = Path.Combine(dir, "draft.json");
            var briefPath = Path.Combine(dir, "brief.json");
            var layoutsPath = Path.Combine(dir, "layouts.json");
            var a = Cli(environment, new[] { "cad-brief", source, draftPath }.Concat(scale).ToArray());
            Check(a.Exit == 0 && File.Exists(draftPath) && Envelope(a.Out) is { } ae && ae.TryGetProperty("format", out var format) && format.GetString() == "pdf",
                label + ": cad-brief " + string.Join(" ", scale) + " writes the draft from " + Path.GetFileName(source), R(a));
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

        // ---- P6-17: the FlateDecode / Form XObject variant brief-plan-xobject.pdf ----
        {
            var variantPath = Path.Combine(fixtures, VariantFile);
            var variantBytes = Read(variantPath);
            Check(Hex(variantBytes) == VariantFixtureDigest, "xobject: brief-plan-xobject.pdf SHA-256 equals the pinned fixture digest", "actual " + Hex(variantBytes) + " pinned " + VariantFixtureDigest);
            if (unix)
            {
                // The flag after the output path, then before it: both forms write the pinned bytes.
                foreach (var (order, flagFirst) in new[] { ("output then flag", false), ("flag then output", true) })
                {
                    var regeneratedVariant = Path.Combine(scratch, flagFirst ? "regenerated-xobject-flag-first.pdf" : "regenerated-xobject.pdf");
                    var script = Path.Combine(fixtures, "brief-plan-pdf.py");
                    var generatorArguments = flagFirst
                        ? new[] { "-I", script, "--variant", "xobject-flate", regeneratedVariant }
                        : new[] { "-I", script, regeneratedVariant, "--variant", "xobject-flate" };
                    (int Exit, string Out, string Err)? generator = null;
                    try { generator = Exec("python3", null, generatorArguments); }
                    catch (Win32Exception) { }
                    if (generator is null)
                    {
                        if (linuxCi) Check(false, "xobject: python3 is available on Linux CI to rerun the fixture generator (" + order + ")");
                        else skip("cad-pdf-to-layouts: xobject fixture generator rerun (" + order + ")", "python3 not found (required on Linux CI only)");
                    }
                    else Check(generator.Value.Exit == 0 && Hex(Read(regeneratedVariant)) == VariantFixtureDigest,
                        "xobject: brief-plan-pdf.py --variant xobject-flate (" + order + ") writes the pinned brief-plan-xobject.pdf bytes", R(generator.Value) + " sha256 " + Hex(Read(regeneratedVariant)));
                }
            }
            else
            {
                skip("cad-pdf-to-layouts: xobject fixture generator rerun (output then flag)", "run on Linux and macOS only (the python3 launcher differs on Windows)");
                skip("cad-pdf-to-layouts: xobject fixture generator rerun (flag then output)", "run on Linux and macOS only (the python3 launcher differs on Windows)");
            }

            var raw = Encoding.Latin1.GetString(variantBytes);
            static int CountOf(string text, string part) => text.Split(part).Length - 1;
            Check(variantBytes.Length > 0 && CountOf(raw, "/FlateDecode") == 2 && CountOf(raw, "/Subtype /Form") == 1
                && !raw.Contains("DORMITORIO", StringComparison.Ordinal) && !raw.Contains(" Tj", StringComparison.Ordinal)
                && !raw.Contains(" Do", StringComparison.Ordinal) && !raw.Contains(" l\n", StringComparison.Ordinal),
                "xobject: both content streams are FlateDecode (no text, path or Do operator readable in the raw file) and the labels stream is a Form XObject",
                "FlateDecode " + CountOf(raw, "/FlateDecode").ToString(CultureInfo.InvariantCulture) + " Form " + CountOf(raw, "/Subtype /Form").ToString(CultureInfo.InvariantCulture));
            Check(variantBytes.Length > 0, "xobject: the fixture brief-plan-xobject.pdf is present and not empty", variantPath);
            if (variantBytes.Length > 0)
            {

                // The parsed page: brief-plan.pdf's elements, texts drawn through the form.
                var plainDocument = PdfReader.Parse(pdfBytes, "brief-plan.pdf");
                var variantDocument = PdfReader.Parse(variantBytes, VariantFile);
                var plainPage = plainDocument.Pages.First(pg => pg.Number == 1);
                var variantPage = variantDocument.Pages.FirstOrDefault(pg => pg.Number == 1);
                static string Warnings(PdfDocument document) => string.Join(",", document.Warnings.Select(w => w.Code).OrderBy(code => code, StringComparer.Ordinal));
                Check(Warnings(variantDocument) == Warnings(plainDocument), "xobject: the variant's warning codes equal brief-plan.pdf's",
                    "variant [" + string.Join("; ", variantDocument.Warnings.Select(w => w.Code + " " + w.Message)) + "] plain [" + Warnings(plainDocument) + "]");
                var plainTexts = plainPage.Elements.OfType<PdfText>().ToList();
                var variantTexts = variantPage?.Elements.OfType<PdfText>().ToList() ?? new List<PdfText>();
                static string TextKey(PdfText t) => t.Value + "|" + t.Origin.X.ToString("R", CultureInfo.InvariantCulture) + "," + t.Origin.Y.ToString("R", CultureInfo.InvariantCulture)
                    + "|" + t.Height.ToString("R", CultureInfo.InvariantCulture) + "|" + t.FontSize.ToString("R", CultureInfo.InvariantCulture) + "|" + t.RotationDegrees.ToString("R", CultureInfo.InvariantCulture);
                Check(plainTexts.Count == 16 && string.Join("; ", variantTexts.Select(TextKey)) == string.Join("; ", plainTexts.Select(TextKey)),
                    "xobject: the page shows brief-plan.pdf's 16 texts in order with the same value, page origin, height, font size and rotation",
                    "variant " + string.Join("; ", variantTexts.Select(TextKey)) + " plain " + string.Join("; ", plainTexts.Select(TextKey)));
                Check(variantTexts.Count == 16 && variantTexts.All(t => t.Provenance.XObjectPath.SequenceEqual(new[] { VariantXObjectPath }) && t.Provenance.ObjectNumber == VariantFormObject && t.Provenance.Operator == "Tj"),
                    "xobject: every text carries XObjectPath [" + VariantXObjectPath + "] and the form's object number",
                    string.Join("; ", variantTexts.Select(t => t.Provenance.ObjectNumber.ToString(CultureInfo.InvariantCulture) + ":" + t.Provenance.OperatorIndex.ToString(CultureInfo.InvariantCulture) + "@" + string.Join(">", t.Provenance.XObjectPath))));
                var plainPaths = plainPage.Elements.OfType<PdfPath>().ToList();
                var variantPaths = variantPage?.Elements.OfType<PdfPath>().ToList() ?? new List<PdfPath>();
                static string PathKey(PdfPath path) => string.Join(" ", path.Subpaths.Select(sp => string.Join(",", sp.Flatten(1).Select(pt => pt.X.ToString("R", CultureInfo.InvariantCulture) + " " + pt.Y.ToString("R", CultureInfo.InvariantCulture))) + (sp.Closed ? " h" : "")));
                Check(plainPaths.Count > 0 && string.Join("; ", variantPaths.Select(PathKey)) == string.Join("; ", plainPaths.Select(PathKey))
                    && variantPaths.All(path => path.Provenance.XObjectPath.Count == 0 && path.Provenance.ObjectNumber == 4),
                    "xobject: the page paths are brief-plan.pdf's, drawn by the page content (object 4) with an empty XObjectPath",
                    "variant " + variantPaths.Count.ToString(CultureInfo.InvariantCulture) + " plain " + plainPaths.Count.ToString(CultureInfo.InvariantCulture));

                // Drafts, planner briefs and layouts in process.
                static BriefDraft VariantDraft(byte[] pdf, bool calibrated) => calibrated
                    ? BriefExtract.FromPdf(PdfReader.Parse(pdf, VariantFile), 1, Calibration(), new BriefExtractOptions())
                    : BriefExtract.FromPdf(PdfReader.Parse(pdf, VariantFile), 1, null, new BriefExtractOptions { MetresPerUnit = 0.02 });
                static string StripReferences(string json)
                {
                    var node = JsonNode.Parse(json);
                    void Walk(JsonNode? current)
                    {
                        if (current is JsonObject obj)
                        {
                            obj.Remove("reference");
                            foreach (var child in obj.Select(pair => pair.Value).ToList()) Walk(child);
                        }
                        else if (current is JsonArray array) foreach (var child in array.ToList()) Walk(child);
                    }
                    Walk(node);
                    return node?.ToJsonString() ?? "";
                }
                var expectedPath = new[] { VariantXObjectPath };
                var handleSuffix = "@" + VariantXObjectPath;
                var variantRuns = new List<(string Label, string[] Scale, string Pinned, (byte[] Bytes, AlternativeSet Set, ProjectBrief Brief, BriefPlannerResult Planned) Run)>();
                foreach (var (vLabel, vCalibrated, vScale, vPlain, vPinned) in new[] { ("calibrated", true, Calibrate, calibrated, PinnedCalibrated), ("metres-per-unit", false, MetresPerUnit, perUnit, PinnedMetresPerUnit) })
                {
                    var variantDraft = VariantDraft(variantBytes, vCalibrated);
                    var plainDraft = Draft(pdfBytes, vCalibrated);
                    Check(variantDraft.Rooms.Count == 7 && Codes(variantDraft) == Codes(plainDraft) && Confidences(variantDraft) == Confidences(plainDraft),
                        "xobject " + vLabel + ": the variant draft has brief-plan.pdf's 7 rooms (ids, confidences) and sorted uncertainty codes",
                        "variant " + Codes(variantDraft) + " / " + Confidences(variantDraft) + " plain " + Codes(plainDraft) + " / " + Confidences(plainDraft));
                    var labelsInForm = variantDraft.Rooms.All(room => room.Label.InsertPath.SequenceEqual(expectedPath)
                        && room.Label.Handle is { } handle && handle.StartsWith("p1:o6:i", StringComparison.Ordinal) && handle.EndsWith(handleSuffix, StringComparison.Ordinal)
                        && (room.Area is null || (room.Area.InsertPath.SequenceEqual(expectedPath) && room.Area.Handle is { } areaHandle && areaHandle.EndsWith(handleSuffix, StringComparison.Ordinal)))
                        && (room.Region is null || (room.Region.InsertPath.Count == 0 && room.Region.Handle is { } regionHandle && !regionHandle.Contains('@', StringComparison.Ordinal))));
                    Check(variantDraft.Rooms.Count > 0 && labelsInForm && variantDraft.Rooms.Any(room => room.Region is not null),
                        "xobject " + vLabel + ": every room label and area source carries InsertPath [" + VariantXObjectPath + "] and handle p1:o6:i{n}" + handleSuffix + ", every region source none",
                        string.Join("; ", variantDraft.Rooms.Select(room => room.Id + " label " + room.Label.Handle + " [" + string.Join(">", room.Label.InsertPath) + "] area " + (room.Area?.Handle ?? "-") + " region " + (room.Region?.Handle ?? "-"))));
                    var variantRun = Plan(variantDraft);
                    var vKept = string.Join(",", variantRun.Planned.Kept.Select(r => r.Id));
                    var vDropped = string.Join("; ", variantRun.Planned.Dropped.Select(r => r.Id + ":" + string.Join(",", r.Unconfirmed)));
                    Check(vKept == KeptRooms && vDropped == DroppedRooms, "xobject " + vLabel + ": the planner keeps and drops exactly the pinned rooms", "kept " + vKept + " dropped " + vDropped);
                    Check(Rooms(variantRun.Brief) == Rooms(vPlain.Brief) && variantRun.Brief.Rooms.Count > 0, "xobject " + vLabel + ": the planner brief has brief-plan.pdf's rooms (ids, names, counts, target areas)",
                        "variant " + Rooms(variantRun.Brief) + " plain " + Rooms(vPlain.Brief));
                    var variantText = BriefSerializer.Serialize(variantRun.Brief);
                    var plainText = BriefSerializer.Serialize(vPlain.Brief);
                    Check(StripReferences(variantText) == StripReferences(plainText) && variantText != plainText,
                        "xobject " + vLabel + ": the planner brief equals brief-plan.pdf's once provenance references are removed",
                        "variant " + StripReferences(variantText) + " plain " + StripReferences(plainText));
                    Check(variantRun.Brief.Rooms.All(room => room.Provenance?.Reference is { } reference && reference.StartsWith(VariantFile + " page 1 handle p1:o6:i", StringComparison.Ordinal) && reference.Contains(handleSuffix + " PDF-TEXT", StringComparison.Ordinal))
                        && vPlain.Brief.Rooms.All(room => room.Provenance?.Reference is { } reference && !reference.Contains('@', StringComparison.Ordinal)),
                        "xobject " + vLabel + ": every planner room's provenance reference names the variant file and the XObject path (brief-plan.pdf's none)",
                        string.Join("; ", variantRun.Brief.Rooms.Select(room => room.Provenance?.Reference ?? "-")));
                    Check(variantRun.Bytes.SequenceEqual(vPlain.Bytes) && Hex(variantRun.Bytes) == vPinned, "xobject " + vLabel + ": the in-process layouts are byte-identical to brief-plan.pdf's and equal the pinned digest",
                        "variant sha256 " + Hex(variantRun.Bytes) + " pinned " + vPinned);
                    variantRuns.Add((vLabel, vScale, vPinned, variantRun));
                }

                // The real CLI chain on the variant.
                foreach (var (vLabel, vScale, vPinned, variantRun) in variantRuns)
                {
                    var vWritten = Chain("xobject-" + vLabel, vScale, variantRun.Brief, null, variantPath);
                    Check(vWritten.SequenceEqual(variantRun.Bytes) && Hex(vWritten) == vPinned, "xobject " + vLabel + ": the CLI chain's layouts.json equals the in-process bytes and the pinned digest",
                        "written sha256 " + Hex(vWritten) + " expected " + Hex(variantRun.Bytes) + " pinned " + vPinned);
                    var draftJson = Envelope(Encoding.UTF8.GetString(Read(Path.Combine(lastChainDir, "draft.json"))));
                    var draftCarries = draftJson is { } dj && dj.TryGetProperty("rooms", out var draftRooms) && draftRooms.ValueKind == JsonValueKind.Array && draftRooms.GetArrayLength() == 7
                        && draftRooms.EnumerateArray().All(room => room.ValueKind == JsonValueKind.Object && room.TryGetProperty("Label", out var label) && label.ValueKind == JsonValueKind.Object
                            && label.TryGetProperty("InsertPath", out var insertPath) && insertPath.ValueKind == JsonValueKind.Array && insertPath.GetArrayLength() == 1
                            && insertPath[0].ValueKind == JsonValueKind.String && insertPath[0].GetString() == VariantXObjectPath
                            && label.TryGetProperty("Handle", out var labelHandle) && labelHandle.ValueKind == JsonValueKind.String && (labelHandle.GetString() ?? "").EndsWith(handleSuffix, StringComparison.Ordinal));
                    Check(draftCarries, "xobject " + vLabel + ": the cad-brief draft.json room labels carry InsertPath [" + VariantXObjectPath + "] and the XObject handle suffix",
                        draftJson?.ToString() is { } dt && dt.Length > 600 ? dt[..600] + "..." : draftJson?.ToString() ?? "<no draft.json>");
                    var briefJson = Envelope(Encoding.UTF8.GetString(Read(Path.Combine(lastChainDir, "brief.json"))));
                    var briefCarries = briefJson is { } bj && bj.TryGetProperty("rooms", out var briefRooms) && briefRooms.ValueKind == JsonValueKind.Array && briefRooms.GetArrayLength() > 0
                        && briefRooms.EnumerateArray().All(room => room.ValueKind == JsonValueKind.Object && room.TryGetProperty("provenance", out var provenance) && provenance.ValueKind == JsonValueKind.Object
                            && provenance.TryGetProperty("reference", out var reference) && reference.ValueKind == JsonValueKind.String && (reference.GetString() ?? "").Contains(handleSuffix + " PDF-TEXT", StringComparison.Ordinal));
                    Check(briefCarries, "xobject " + vLabel + ": every room of the --brief-only brief.json has a provenance reference with the XObject path",
                        briefJson?.ToString() is { } bt && bt.Length > 600 ? bt[..600] + "..." : briefJson?.ToString() ?? "<no brief.json>");
                }
            }
        }

        // ---- P6-19: labels in a Form XObject nested two deep under a scaled form; regions in a form ----
        {
            var nestedPath = Path.Combine(fixtures, NestedFile);
            var regionsPath = Path.Combine(fixtures, NestedRegionsFile);
            var nestedBytes = Read(nestedPath);
            var regionsBytes = Read(regionsPath);
            Check(nestedBytes.Length > 0, "nested: the fixture " + NestedFile + " is present and not empty", nestedPath);
            Check(regionsBytes.Length > 0, "nested: the fixture " + NestedRegionsFile + " is present and not empty", regionsPath);
            Check(Hex(nestedBytes) == NestedFixtureDigest, "nested: " + NestedFile + " SHA-256 equals the pinned fixture digest", "actual " + Hex(nestedBytes) + " pinned " + NestedFixtureDigest);
            Check(Hex(regionsBytes) == NestedRegionsFixtureDigest, "nested: " + NestedRegionsFile + " SHA-256 equals the pinned fixture digest", "actual " + Hex(regionsBytes) + " pinned " + NestedRegionsFixtureDigest);
            foreach (var (variant, pinnedFixture, flagFirst) in new[] { ("nested-scaled", NestedFixtureDigest, false), ("nested-scaled-regions", NestedRegionsFixtureDigest, true) })
            {
                if (!unix)
                {
                    skip("cad-pdf-to-layouts: " + variant + " fixture generator rerun", "run on Linux and macOS only (the python3 launcher differs on Windows)");
                    continue;
                }
                var regeneratedNested = Path.Combine(scratch, "regenerated-" + variant + ".pdf");
                var script = Path.Combine(fixtures, "brief-plan-pdf.py");
                var generatorArguments = flagFirst
                    ? new[] { "-I", script, "--variant", variant, regeneratedNested }
                    : new[] { "-I", script, regeneratedNested, "--variant", variant };
                (int Exit, string Out, string Err)? generator = null;
                try { generator = Exec("python3", null, generatorArguments); }
                catch (Win32Exception) { }
                if (generator is null)
                {
                    if (linuxCi) Check(false, "nested: python3 is available on Linux CI to rerun the fixture generator (" + variant + ")");
                    else skip("cad-pdf-to-layouts: " + variant + " fixture generator rerun", "python3 not found (required on Linux CI only)");
                }
                else Check(generator.Value.Exit == 0 && Hex(Read(regeneratedNested)) == pinnedFixture,
                    "nested: brief-plan-pdf.py --variant " + variant + " writes the pinned fixture bytes", R(generator.Value) + " sha256 " + Hex(Read(regeneratedNested)) + " pinned " + pinnedFixture);
            }
            static int Occurrences(byte[] bytes, string part) => Encoding.Latin1.GetString(bytes).Split(part).Length - 1;
            Check(Occurrences(nestedBytes, "/Subtype /Form") == 2 && Occurrences(nestedBytes, "/Matrix [2 0 0 2 100 150]") == 1 && Occurrences(nestedBytes, "/Matrix [1 0 0 1 5 -5]") == 1
                && Occurrences(nestedBytes, "/Labels Do") == 1 && Occurrences(nestedBytes, "/Outer Do") == 1 && Occurrences(nestedBytes, "/FlateDecode") == 0,
                "nested: " + NestedFile + " has the scaled /Outer form and the nested /Labels form (uncompressed)",
                "Form " + Occurrences(nestedBytes, "/Subtype /Form").ToString(CultureInfo.InvariantCulture) + " scaled " + Occurrences(nestedBytes, "/Matrix [2 0 0 2 100 150]").ToString(CultureInfo.InvariantCulture));
            Check(Occurrences(regionsBytes, "/Subtype /Form") == 3 && Occurrences(regionsBytes, "/Matrix [2 0 0 2 100 150]") == 2 && Occurrences(regionsBytes, "/Rooms Do") == 1,
                "nested: " + NestedRegionsFile + " also draws the regions from the scaled /Rooms form",
                "Form " + Occurrences(regionsBytes, "/Subtype /Form").ToString(CultureInfo.InvariantCulture) + " scaled " + Occurrences(regionsBytes, "/Matrix [2 0 0 2 100 150]").ToString(CultureInfo.InvariantCulture));
            if (nestedBytes.Length > 0 && regionsBytes.Length > 0)
            {
                // The parsed pages: brief-plan.pdf's texts and paths at the same page points, through the nested forms.
                var plainDocument = PdfReader.Parse(pdfBytes, "brief-plan.pdf");
                var plainPage = plainDocument.Pages.First(pg => pg.Number == 1);
                var plainTexts = plainPage.Elements.OfType<PdfText>().ToList();
                var plainPaths = plainPage.Elements.OfType<PdfPath>().ToList();
                static string Fmt(double value) => value.ToString("R", CultureInfo.InvariantCulture);
                static string NestedTextKey(PdfText t) => t.Value + "|" + Fmt(t.Origin.X) + "," + Fmt(t.Origin.Y) + "|" + Fmt(t.Height) + "|" + Fmt(t.RotationDegrees);
                static string NestedPathKey(PdfPath path) => string.Join(" ", path.Subpaths.Select(sp => string.Join(",", sp.Flatten(1).Select(pt => Fmt(pt.X) + " " + Fmt(pt.Y))) + (sp.Closed ? " h" : "")));
                static string Chain2(IReadOnlyList<string> path) => "[" + string.Join(">", path) + "]";
                foreach (var (file, bytes, textPath, pathPath, pathObject) in new[]
                {
                    (NestedFile, nestedBytes, NestedTextPath, Array.Empty<string>(), 4),
                    (NestedRegionsFile, regionsBytes, NestedRegionsTextPath, NestedRegionsPath, NestedRoomsFormObject),
                })
                {
                    var document = PdfReader.Parse(bytes, file);
                    var page = document.Pages.FirstOrDefault(pg => pg.Number == 1);
                    var texts = page?.Elements.OfType<PdfText>().ToList() ?? new List<PdfText>();
                    var paths = page?.Elements.OfType<PdfPath>().ToList() ?? new List<PdfPath>();
                    static string WarningCodes(PdfDocument d) => string.Join(",", d.Warnings.Select(w => w.Code).OrderBy(code => code, StringComparer.Ordinal));
                    Check(WarningCodes(document) == WarningCodes(plainDocument), "nested " + file + ": the warning codes equal brief-plan.pdf's",
                        "variant [" + string.Join("; ", document.Warnings.Select(w => w.Code + " " + w.Message)) + "] plain [" + WarningCodes(plainDocument) + "]");
                    Check(plainTexts.Count == 16 && string.Join("; ", texts.Select(NestedTextKey)) == string.Join("; ", plainTexts.Select(NestedTextKey)),
                        "nested " + file + ": the page shows brief-plan.pdf's 16 texts in order at the same page origin, height and rotation (inner matrix applied before the scaled outer one)",
                        "variant " + string.Join("; ", texts.Select(NestedTextKey)) + " plain " + string.Join("; ", plainTexts.Select(NestedTextKey)));
                    Check(texts.Count == 16 && texts.Zip(plainTexts).All(p => p.First.FontSize * 2 == p.Second.FontSize),
                        "nested " + file + ": each text is set at half brief-plan.pdf's font size (the outer form scales by 2)",
                        string.Join(",", texts.Select(t => Fmt(t.FontSize))));
                    Check(texts.Count == 16 && texts.All(t => t.Provenance.XObjectPath.SequenceEqual(textPath) && t.Provenance.ObjectNumber == NestedLabelsFormObject && t.Provenance.Operator == "Tj"),
                        "nested " + file + ": every text carries the two-deep XObjectPath " + Chain2(textPath) + " and the inner form's object number " + NestedLabelsFormObject.ToString(CultureInfo.InvariantCulture),
                        string.Join("; ", texts.Select(t => t.Provenance.ObjectNumber.ToString(CultureInfo.InvariantCulture) + ":" + t.Provenance.OperatorIndex.ToString(CultureInfo.InvariantCulture) + "@" + Chain2(t.Provenance.XObjectPath))));
                    Check(plainPaths.Count > 0 && string.Join("; ", paths.Select(NestedPathKey)) == string.Join("; ", plainPaths.Select(NestedPathKey))
                        && paths.All(p => p.Provenance.XObjectPath.SequenceEqual(pathPath) && p.Provenance.ObjectNumber == pathObject),
                        "nested " + file + ": the paths are brief-plan.pdf's at the same page points, drawn by object " + pathObject.ToString(CultureInfo.InvariantCulture) + " with XObjectPath " + Chain2(pathPath),
                        "variant " + string.Join("; ", paths.Select(p => p.Provenance.ObjectNumber.ToString(CultureInfo.InvariantCulture) + Chain2(p.Provenance.XObjectPath) + " " + NestedPathKey(p))));
                }

                // Drafts, planner briefs and layouts in process.
                static string StripNestedReferences(string json)
                {
                    var node = JsonNode.Parse(json);
                    void Walk(JsonNode? current)
                    {
                        if (current is JsonObject obj)
                        {
                            obj.Remove("reference");
                            foreach (var child in obj.Select(pair => pair.Value).ToList()) Walk(child);
                        }
                        else if (current is JsonArray array) foreach (var child in array.ToList()) Walk(child);
                    }
                    Walk(node);
                    return node?.ToJsonString() ?? "";
                }
                static BriefDraft NestedDraft(byte[] pdf, string file, bool calibrated, bool includeBlockRegions) => calibrated
                    ? BriefExtract.FromPdf(PdfReader.Parse(pdf, file), 1, Calibration(), new BriefExtractOptions { IncludeBlockRegions = includeBlockRegions })
                    : BriefExtract.FromPdf(PdfReader.Parse(pdf, file), 1, null, new BriefExtractOptions { MetresPerUnit = 0.02, IncludeBlockRegions = includeBlockRegions });
                var nestedRuns = new List<(string Label, string[] Scale, string Pinned, (byte[] Bytes, AlternativeSet Set, ProjectBrief Brief, BriefPlannerResult Planned) Run)>();
                foreach (var (vLabel, vCalibrated, vScale, vPlain, vPinned) in new[] { ("calibrated", true, Calibrate, calibrated, PinnedCalibrated), ("metres-per-unit", false, MetresPerUnit, perUnit, PinnedMetresPerUnit) })
                {
                    var plainDraft = Draft(pdfBytes, vCalibrated);
                    // Without IncludeBlockRegions the regions drawn by /Rooms are not read (the P6-05 rule for forms).
                    var withoutRegions = NestedDraft(regionsBytes, NestedRegionsFile, vCalibrated, false);
                    Check(withoutRegions.Rooms.Count > 0 && withoutRegions.Rooms.All(room => room.Region is null),
                        "nested-regions " + vLabel + ": without IncludeBlockRegions no room takes a region drawn by the /Rooms form",
                        string.Join("; ", withoutRegions.Rooms.Select(room => room.Id + " region " + (room.Region?.Handle ?? "-"))));
                    foreach (var (file, bytes, includeBlockRegions, labelPath, labelSuffix, regionPath, regionPrefix) in new[]
                    {
                        (NestedFile, nestedBytes, false, NestedTextPath, "@" + string.Join(">", NestedTextPath), Array.Empty<string>(), "p1:o4:i"),
                        (NestedRegionsFile, regionsBytes, true, NestedRegionsTextPath, "@" + string.Join(">", NestedRegionsTextPath), NestedRegionsPath, "p1:o" + NestedRoomsFormObject.ToString(CultureInfo.InvariantCulture) + ":i"),
                    })
                    {
                        var tag = (includeBlockRegions ? "nested-regions " : "nested ") + vLabel;
                        var draft = NestedDraft(bytes, file, vCalibrated, includeBlockRegions);
                        Check(draft.Rooms.Count == 7 && Codes(draft) == Codes(plainDraft) && Confidences(draft) == Confidences(plainDraft),
                            tag + ": the draft" + (includeBlockRegions ? " (IncludeBlockRegions)" : "") + " has brief-plan.pdf's 7 rooms (ids, confidences) and sorted uncertainty codes",
                            "variant " + Codes(draft) + " / " + Confidences(draft) + " plain " + Codes(plainDraft) + " / " + Confidences(plainDraft));
                        var regionSuffix = regionPath.Length > 0 ? "@" + string.Join(">", regionPath) : "";
                        var sourced = draft.Rooms.All(room => room.Label.InsertPath.SequenceEqual(labelPath)
                            && room.Label.Handle is { } handle && handle.StartsWith("p1:o" + NestedLabelsFormObject.ToString(CultureInfo.InvariantCulture) + ":i", StringComparison.Ordinal) && handle.EndsWith(labelSuffix, StringComparison.Ordinal)
                            && (room.Area is null || (room.Area.InsertPath.SequenceEqual(labelPath) && room.Area.Handle is { } areaHandle && areaHandle.EndsWith(labelSuffix, StringComparison.Ordinal)))
                            && (room.Region is null || (room.Region.InsertPath.SequenceEqual(regionPath) && room.Region.Handle is { } regionHandle && regionHandle.StartsWith(regionPrefix, StringComparison.Ordinal)
                                && (regionSuffix.Length > 0 ? regionHandle.EndsWith(regionSuffix, StringComparison.Ordinal) : !regionHandle.Contains('@', StringComparison.Ordinal)))));
                        Check(sourced && draft.Rooms.Any(room => room.Region is not null),
                            tag + ": every room label and area source carries InsertPath " + Chain2(labelPath) + " and a handle ending " + labelSuffix + ", every region source InsertPath " + Chain2(regionPath),
                            string.Join("; ", draft.Rooms.Select(room => room.Id + " label " + room.Label.Handle + " " + Chain2(room.Label.InsertPath) + " area " + (room.Area?.Handle ?? "-") + " region " + (room.Region?.Handle ?? "-") + " " + (room.Region is null ? "" : Chain2(room.Region.InsertPath)))));
                        var run = Plan(draft);
                        var kept = string.Join(",", run.Planned.Kept.Select(r => r.Id));
                        var dropped = string.Join("; ", run.Planned.Dropped.Select(r => r.Id + ":" + string.Join(",", r.Unconfirmed)));
                        Check(kept == KeptRooms && dropped == DroppedRooms, tag + ": the planner keeps and drops exactly the pinned rooms", "kept " + kept + " dropped " + dropped);
                        var text = BriefSerializer.Serialize(run.Brief);
                        var plainText = BriefSerializer.Serialize(vPlain.Brief);
                        Check(StripNestedReferences(text) == StripNestedReferences(plainText) && text != plainText,
                            tag + ": the planner brief equals brief-plan.pdf's once provenance references are removed",
                            "variant " + StripNestedReferences(text) + " plain " + StripNestedReferences(plainText));
                        Check(run.Brief.Rooms.Count > 0 && run.Brief.Rooms.All(room => room.Provenance?.Reference is { } reference
                                && reference.StartsWith(file + " page 1 handle p1:o" + NestedLabelsFormObject.ToString(CultureInfo.InvariantCulture) + ":i", StringComparison.Ordinal)
                                && reference.Contains(labelSuffix + " PDF-TEXT", StringComparison.Ordinal)),
                            tag + ": every planner room's provenance reference names the file and the nested XObject path " + labelSuffix,
                            string.Join("; ", run.Brief.Rooms.Select(room => room.Provenance?.Reference ?? "-")));
                        Check(run.Bytes.SequenceEqual(vPlain.Bytes) && Hex(run.Bytes) == vPinned, tag + ": the in-process layouts are byte-identical to brief-plan.pdf's and equal the pinned digest",
                            "variant sha256 " + Hex(run.Bytes) + " pinned " + vPinned);
                        if (!includeBlockRegions) nestedRuns.Add((vLabel, vScale, vPinned, run));
                    }
                }

                // The real CLI chain on brief-plan-nested.pdf (its regions are drawn by the page, so no extractor option is needed).
                var nestedSuffix = "@" + string.Join(">", NestedTextPath);
                foreach (var (vLabel, vScale, vPinned, run) in nestedRuns)
                {
                    var written = Chain("nested-" + vLabel, vScale, run.Brief, null, nestedPath);
                    Check(written.SequenceEqual(run.Bytes) && Hex(written) == vPinned, "nested " + vLabel + ": the CLI chain's layouts.json equals the in-process bytes and the pinned digest",
                        "written sha256 " + Hex(written) + " expected " + Hex(run.Bytes) + " pinned " + vPinned);
                    var draftJson = Envelope(Encoding.UTF8.GetString(Read(Path.Combine(lastChainDir, "draft.json"))));
                    var draftCarries = draftJson is { } dj && dj.TryGetProperty("rooms", out var draftRooms) && draftRooms.ValueKind == JsonValueKind.Array && draftRooms.GetArrayLength() == 7
                        && draftRooms.EnumerateArray().All(room => room.ValueKind == JsonValueKind.Object && room.TryGetProperty("Label", out var label) && label.ValueKind == JsonValueKind.Object
                            && label.TryGetProperty("InsertPath", out var insertPath) && insertPath.ValueKind == JsonValueKind.Array
                            && insertPath.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).SequenceEqual(NestedTextPath)
                            && label.TryGetProperty("Handle", out var labelHandle) && labelHandle.ValueKind == JsonValueKind.String && (labelHandle.GetString() ?? "").EndsWith(nestedSuffix, StringComparison.Ordinal));
                    Check(draftCarries, "nested " + vLabel + ": the cad-brief draft.json room labels carry InsertPath " + Chain2(NestedTextPath) + " and the handle suffix " + nestedSuffix,
                        draftJson?.ToString() is { } dt && dt.Length > 600 ? dt[..600] + "..." : draftJson?.ToString() ?? "<no draft.json>");
                    var briefJson = Envelope(Encoding.UTF8.GetString(Read(Path.Combine(lastChainDir, "brief.json"))));
                    var briefCarries = briefJson is { } bj && bj.TryGetProperty("rooms", out var briefRooms) && briefRooms.ValueKind == JsonValueKind.Array && briefRooms.GetArrayLength() > 0
                        && briefRooms.EnumerateArray().All(room => room.ValueKind == JsonValueKind.Object && room.TryGetProperty("provenance", out var provenance) && provenance.ValueKind == JsonValueKind.Object
                            && provenance.TryGetProperty("reference", out var reference) && reference.ValueKind == JsonValueKind.String && (reference.GetString() ?? "").Contains(nestedSuffix + " PDF-TEXT", StringComparison.Ordinal));
                    Check(briefCarries, "nested " + vLabel + ": every room of the --brief-only brief.json has a provenance reference with the nested XObject path " + nestedSuffix,
                        briefJson?.ToString() is { } bt && bt.Length > 600 ? bt[..600] + "..." : briefJson?.ToString() ?? "<no brief.json>");
                }
            }
        }

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
                    && uncertainties.ValueKind == JsonValueKind.Array && uncertainties.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("Code", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() == "uncalibrated");
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
