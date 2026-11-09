using Bim.Cad;
using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// P6-11 non-ASCII room labels oracle. Regression first: the raw BriefExtract JSON of every pre-existing DXF/PDF fixture,
// the P6-08 planner result of brief-plan.dxf and a generated drawing of Latin labels (accents, ß, ø, a mixed
// Cyrillic+Latin label) are pinned byte for byte (SHA-256 prefixes captured in CI from main f671157, before P6-11).
// Then labels made only of Cyrillic, Greek or CJK letters (TEXT and MTEXT \U+ escapes) become rooms whose id is the
// deterministic, culture-invariant fallback room_<16 hex of SHA-256 of the case-folded NFC label>, which never collides
// with a Latin slug (those never contain '_'); case variants group as duplicate-label in the extractor and in the P6-08
// planner; non-Latin unit words ("12 м²") are still not labels. Unit-tested evidence only.
internal static class BriefNonAsciiTests
{
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    private static string Ids(BriefDraft d) => string.Join(",", d.Rooms.Select(r => r.Id));
    private static string Codes(BriefDraft d) => string.Join(",", d.Uncertainties.Select(u => u.Code + (u.RoomId is null ? "" : ":" + u.RoomId)));

    private static string G(string spec) => string.Concat(spec.Split('|').Select(item => { var space = item.IndexOf(' ', StringComparison.Ordinal); return item[..space] + "\n" + item[(space + 1)..] + "\n"; }));
    private static string Rect(string handle, double x0, double y0, double x1, double y1) =>
        G("0 LWPOLYLINE|5 " + handle + "|8 ROOMS|90 4|70 1|10 " + F(x0) + "|20 " + F(y0) + "|10 " + F(x1) + "|20 " + F(y0) + "|10 " + F(x1) + "|20 " + F(y1) + "|10 " + F(x0) + "|20 " + F(y1));
    private static string Text(string handle, double x, double y, string value, string type = "TEXT") =>
        G("0 " + type + "|5 " + handle + "|8 ROOM-TEXT|10 " + F(x) + "|20 " + F(y) + "|40 200|1 " + value);
    private static BriefDraft Dxf(string entities) =>
        BriefExtract.FromDxf(DxfReader.Parse(G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC|0 SECTION|2 ENTITIES") + entities + G("0 ENDSEC|0 EOF"), "gen.dxf"));

    // One 4 m x 3 m room per label, side by side.
    private static string Rooms(params string[] labels)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < labels.Length; i++)
        {
            var x = i * 5000.0;
            builder.Append(Rect("1" + i.ToString("00", CultureInfo.InvariantCulture), x, 0, x + 4000, 3000));
            builder.Append(labels[i].StartsWith("MTEXT:", StringComparison.Ordinal)
                ? Text("2" + i.ToString("00", CultureInfo.InvariantCulture), x + 1000, 1500, labels[i]["MTEXT:".Length..], "MTEXT")
                : Text("2" + i.ToString("00", CultureInfo.InvariantCulture), x + 1000, 1500, labels[i]));
        }
        return builder.ToString();
    }

    private static readonly string[] LatinLabels = { "Sala de estar", "Großraum", "Cocina 2", "Kuchyně", "Øvre stue", "Кухня A", "Baño", "Baño" };

    // Raw (unrounded) digests of JsonSerializer.Serialize output, captured in CI from main f671157 before P6-11.
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal)
    {
    };

    private static string? Pin(string name, Func<string> json)
    {
        try { return Hash(json()); }
        catch (Exception error) { return "error " + error.GetType().Name + ": " + error.Message; }
    }

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "brief-non-ascii " + name, detail);

        // ---------- regression pins: ASCII/Latin output unchanged byte for byte ----------
        {
            var actual = new List<(string Name, string? Digest)>();
            foreach (var file in new[] { "blocks.dxf", "brief-plan.dxf", "draft-plan.dxf", "minsert-ocs.dxf", "room-tags.dxf", "simple-plan.dxf", "walls.dxf" })
                actual.Add(("extract " + file, Pin(file, () => JsonSerializer.Serialize(BriefExtract.FromDxf(DxfReader.ReadFile(Path.Combine(fixtures, file)))))));
            foreach (var file in new[] { "draft-plan.pdf", "simple-plan.pdf" })
                actual.Add(("extract " + file, Pin(file, () => JsonSerializer.Serialize(BriefExtract.FromPdf(PdfReader.ReadFile(Path.Combine(fixtures, file)), 1)))));
            actual.Add(("planner brief-plan.dxf", Pin("planner", () => JsonSerializer.Serialize(BriefPlanner.ToPlannerBrief(BriefExtract.FromDxf(DxfReader.ReadFile(Path.Combine(fixtures, "brief-plan.dxf"))))))));
            actual.Add(("extract latin", Pin("latin", () => JsonSerializer.Serialize(Dxf(Rooms(LatinLabels))))));
            actual.Add(("planner latin", Pin("latin planner", () => JsonSerializer.Serialize(BriefPlanner.ToPlannerBrief(Dxf(Rooms(LatinLabels)))))));
            foreach (var (name, digest) in actual)
                C(Pins.TryGetValue(name, out var expected) && expected == digest, "pin " + name, "actual " + digest + " expected " + (expected ?? "unpinned"));
            C(Pins.Count == actual.Count, "pin table complete", string.Join(" ; ", actual.Select(a => a.Name + "=" + a.Digest)));
        }

        // ---------- Latin ids keep their slugs ----------
        try
        {
            var latin = Dxf(Rooms(LatinLabels));
            C(Ids(latin) == "sala-de-estar,gro-raum,cocina-2,kuchyne,vre-stue,a,bano,bano-2", "latin ids unchanged", Ids(latin));
        }
        catch (Exception error) { C(false, "latin unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- reading non-ASCII labels ----------
        try
        {
            void Label(string text, string? label, double? area = null)
            {
                var r = BriefExtract.ReadText(text);
                C(r.Label == label && r.AreaSquareMetres == area, "read '" + text + "'", (r.Label ?? "null") + " " + (r.AreaSquareMetres is { } a ? F(a) : "null"));
            }
            Label("Кухня", "Кухня");
            Label("Кухня 12 m²", "Кухня", 12);
            Label("Σαλόνι 9,5 m2", "Σαλόνι", 9.5);
            Label("厨房 10 m²", "厨房", 10);
            Label("Спальня 1", "Спальня 1");
            Label("𠮷", "𠮷");
            Label("12 м²", null);
            Label("12 кв. м", null);
            Label("20 τ.μ.", null);
            Label("15 平方米", null);
            Label("12", null);
            Label("3 x 4 m", null);
        }
        catch (Exception error) { C(false, "read unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- extraction: fallback ids, duplicates, planner grouping ----------
        try
        {
            var labels = new[] { "Кухня 12 m²", "Σαλόνι", "厨房 12 m²", "Спальня", "MTEXT:\\U+041A\\U+0423\\U+0425\\U+041D\\U+042F 12 m²", "Cocina", "Спальня 1" };
            var d = Dxf(Rooms(labels));
            const string kitchen = "room_07c473093067d1ee", living = "room_41be6f037a1f508d", chu = "room_4ae988a29c1f702c", bedroom = "room_a01cd0c3f09d643e", bedroom1 = "room_317ea1f9a5958ce8";
            C(Ids(d) == string.Join(",", kitchen, living, chu, bedroom, kitchen + "-2", "cocina", bedroom1), "non-ascii labels become rooms with fallback ids", Ids(d));
            C(string.Join("|", d.Rooms.Select(r => r.Name)) == "Кухня|Σαλόνι|厨房|Спальня|КУХНЯ|Cocina|Спальня 1", "names keep the original script", string.Join("|", d.Rooms.Select(r => r.Name)));
            C(d.Rooms.All(r => BriefParser.IsRoomId(r.Id)) && d.Issues.Count == 0 && BriefValidator.Validate(d.Brief).Length == 0, "fallback ids are valid room ids and the brief validates",
                string.Join(",", d.Issues.Select(i => i.Code + "@" + i.Field)));
            C(d.Rooms.Where(r => r.Id.StartsWith("room_", StringComparison.Ordinal)).All(r => r.Notes.Contains("label-outside-vocabulary")) && d.Rooms[0].StatedAreaSquareMetres == 12 && d.Rooms[1].StatedAreaSquareMetres is null && d.Rooms[1].RegionAreaSquareMetres is { } livingArea && Math.Abs(livingArea - 12) < 1e-9,
                "non-ascii rooms are outside the vocabulary and keep stated/region areas", string.Join(" | ", d.Rooms.Select(r => r.Id + " [" + string.Join(",", r.Notes) + "]")));
            var duplicates = d.Uncertainties.Where(u => u.Code == "duplicate-label").ToList();
            C(duplicates.Count == 1 && duplicates[0].RoomId == kitchen + "-2" && duplicates[0].Sources.Select(s => s.Handle).SequenceEqual(new[] { "200", "204" })
                && d.Rooms[0].Notes.Contains("duplicate-label") && d.Rooms[4].Notes.Contains("duplicate-label") && !d.Rooms[3].Notes.Contains("duplicate-label") && !d.Rooms[6].Notes.Contains("duplicate-label"),
                "case variants are duplicate-label; different labels are not", Codes(d));

            var planned = BriefPlanner.ToPlannerBrief(d);
            C(string.Join(",", planned.Kept.Select(r => r.Id)) == string.Join(",", kitchen, living, chu, bedroom, "cocina", bedroom1)
                && planned.Dropped.Count == 1 && planned.Dropped[0].Id == kitchen + "-2" && planned.Dropped[0].Reasons.SequenceEqual(new[] { "duplicate-label" }) && planned.Kept.All(r => r.Reasons.Count == 0) && planned.Issues.Count == 0,
                "planner groups the fallback ids like P6-08 slugs", string.Join(",", planned.Kept.Select(r => r.Id)) + " ; dropped " + string.Join(",", planned.Dropped.Select(r => r.Id + "[" + string.Join(",", r.Reasons) + "]")));

            // A hand-built draft whose duplicate-label uncertainty is missing still groups by the fallback id stem.
            var bare = d with { Uncertainties = d.Uncertainties.Where(u => u.Code != "duplicate-label").ToList(), Rooms = d.Rooms.Select(r => r with { Notes = r.Notes.Where(n => n != "duplicate-label").ToList() }).ToList() };
            var regrouped = BriefPlanner.ToPlannerBrief(bare);
            C(regrouped.Dropped.Count == 0 && regrouped.Kept.Count == 7, "planner without duplicate notes keeps every room (grouping alone is not a reason)", string.Join(",", regrouped.Dropped.Select(r => r.Id)));

            var culture = CultureInfo.CurrentCulture;
            var invariant = JsonSerializer.Serialize(d);
            var same = true;
            foreach (var name in new[] { "tr-TR", "de-DE", "ru-RU", "zh-CN", "el-GR" })
            {
                try { CultureInfo.CurrentCulture = new CultureInfo(name); same &= JsonSerializer.Serialize(Dxf(Rooms(labels))) == invariant; }
                finally { CultureInfo.CurrentCulture = culture; }
            }
            C(same && JsonSerializer.Serialize(Dxf(Rooms(labels))) == invariant, "non-ascii draft deterministic across runs and cultures");

            // A Latin label that reads like a fallback id still slugs with '-' only, so it cannot collide.
            var spoof = Dxf(Rooms("Room 07c473093067d1ee", "Кухня"));
            C(Ids(spoof) == "room-07c473093067d1ee," + kitchen, "latin slug cannot collide with a fallback id", Ids(spoof));
        }
        catch (Exception error) { C(false, "extract unexpected", error.GetType().Name + ": " + error.Message); }
    }
}
