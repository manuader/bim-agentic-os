using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bim.Cad;

// P6-23 cad-brief --include-block-regions on DXF block inserts. A generated millimetre DXF (written to the scratch
// directory, no committed fixture) has two labelled rooms: "Cocina" whose closed LWPOLYLINE region sits in model space,
// and "Dormitorio" whose only closed region (4 m x 3 m) is drawn inside block ROOM inserted at (10000, 0).
// Oracles, all against the real Release CLI process:
//   1. With the flag (before the scale option, after it, or with no scale option) draft.json "rooms" equals the
//      in-process BriefExtract.FromDxf(DxfReader.Parse(bytes), IncludeBlockRegions = true) rooms serialized with the
//      CLI's JSON options, and Dormitorio takes the block region (one insert in its path, 12 m2).
//   2. Without the flag draft.json "rooms" equals the default-option in-process rooms, and Dormitorio has no region
//      while Cocina keeps its model-space region (the flag changes only block regions).
//   3. A value option followed by the flag (--page --include-block-regions, --metres-per-unit --include-block-regions)
//      is the exact cad_option_value_invalid envelope for that option, exit 1, nothing written.
internal static class CadBriefBlockRegionTests
{
    const string Flag = "--include-block-regions";
    const string FileName = "block-region-room.dxf";
    static readonly JsonSerializerOptions RoomJson = new() { Converters = { new JsonStringEnumConverter() } };

    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string G(string spec) => string.Concat(spec.Split('|').Select(item => { var space = item.IndexOf(' ', StringComparison.Ordinal); return item[..space] + "\n" + item[(space + 1)..] + "\n"; }));
    static string Rect(string handle, double x0, double y0, double x1, double y1, string layer) =>
        G("0 LWPOLYLINE|5 " + handle + "|8 " + layer + "|90 4|70 1|10 " + F(x0) + "|20 " + F(y0) + "|10 " + F(x1) + "|20 " + F(y0) + "|10 " + F(x1) + "|20 " + F(y1) + "|10 " + F(x0) + "|20 " + F(y1));
    static string Text(string handle, double x, double y, string value) =>
        G("0 TEXT|5 " + handle + "|8 ROOM-TEXT|10 " + F(x) + "|20 " + F(y) + "|40 200|1 " + value);

    // INSUNITS 4 (millimetres); block ROOM holds the Dormitorio region; Cocina's region is in model space.
    static string Drawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 B1|8 0|2 ROOM|70 0|10 0|20 0|30 0") + Rect("B2", 0, 0, 4000, 3000, "0") + G("0 ENDBLK|5 B3|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + Rect("1", 0, 0, 3000, 3000, "ROOMS") + Text("2", 1000, 1000, "Cocina")
        + G("0 INSERT|5 I1|8 ROOMS|2 ROOM|10 10000|20 0|30 0") + Text("3", 11000, 1000, "Dormitorio")
        + G("0 ENDSEC|0 EOF");

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "cad-brief-block-region");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "cad-brief-block-region: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000))
            {
                try { process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
                return (-1, "<timed out>", "");
            }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result.TrimEnd('\n', '\r'), stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        static string R((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + r.Out + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        static string Clip(string text) => text.Length > 600 ? text[..600] + "..." : text;
        static string RoomsOf(string path)
        {
            if (!File.Exists(path)) return "<no output>";
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("rooms", out var rooms) ? rooms.GetRawText() : "<no rooms>";
            }
            catch (JsonException e) { return "<invalid json: " + e.Message + ">"; }
        }

        var bytes = new UTF8Encoding(false).GetBytes(Drawing());
        var input = Path.Combine(scratch, FileName);
        File.WriteAllBytes(input, bytes);
        BriefDraft InProcess(bool include, double? metresPerUnit) =>
            BriefExtract.FromDxf(DxfReader.Parse(bytes, FileName), new BriefExtractOptions { MetresPerUnit = metresPerUnit, IncludeBlockRegions = include });
        static BriefRoomProposal? Room(BriefDraft draft, string id) => draft.Rooms.FirstOrDefault(room => room.Id == id);
        static bool Near(double? value, double expected) => value is { } v && Math.Abs(v - expected) < 1e-9;

        // The in-process oracle discriminates: only IncludeBlockRegions gives Dormitorio the block region.
        var included = InProcess(true, null);
        var excluded = InProcess(false, null);
        Check(included.Rooms.Count == 2 && Room(included, "dormitorio") is { Region: { } dormitorioRegion } d && dormitorioRegion.InsertPath.Count == 1 && Near(d.RegionAreaSquareMetres, 12)
                && Room(included, "cocina") is { Region: { InsertPath.Count: 0 } } c && Near(c.RegionAreaSquareMetres, 9),
            "in process IncludeBlockRegions: Dormitorio takes the 12 m2 region inside insert I1, Cocina its 9 m2 model-space region",
            Clip(JsonSerializer.Serialize(included.Rooms, RoomJson)));
        Check(excluded.Rooms.Count == 2 && Room(excluded, "dormitorio") is { Region: null, RegionAreaSquareMetres: null }
                && Room(excluded, "cocina") is { Region: { InsertPath.Count: 0 } } c2 && Near(c2.RegionAreaSquareMetres, 9),
            "in process default options: Dormitorio has no region, Cocina keeps its model-space region",
            Clip(JsonSerializer.Serialize(excluded.Rooms, RoomJson)));

        // Through the real CLI, with and without the flag, for each scale form and flag position.
        foreach (var (label, metres, withFlag, withoutFlag) in new[]
        {
            ("insunits", (double?)null, new[] { Flag }, Array.Empty<string>()),
            ("flag-first", (double?)0.001, new[] { Flag, "--metres-per-unit", "0.001" }, new[] { "--metres-per-unit", "0.001" }),
            ("flag-last", (double?)0.001, new[] { "--metres-per-unit", "0.001", Flag }, new[] { "--metres-per-unit", "0.001" }),
        })
        {
            var expectedWith = JsonSerializer.Serialize(InProcess(true, metres).Rooms, RoomJson);
            var withOutput = Path.Combine(scratch, "with-" + label + ".json");
            var with = Cli(new[] { "cad-brief", input, withOutput }.Concat(withFlag).ToArray());
            var withRooms = RoomsOf(withOutput);
            Check(with.Exit == 0 && withRooms == expectedWith && expectedWith.Contains("\"InsertPath\":[\"", StringComparison.Ordinal),
                label + ": with " + Flag + " draft.json rooms equal the in-process IncludeBlockRegions rooms (a region from the block insert)",
                R(with) + " rooms " + Clip(withRooms));

            var expectedWithout = JsonSerializer.Serialize(InProcess(false, metres).Rooms, RoomJson);
            var withoutOutput = Path.Combine(scratch, "without-" + label + ".json");
            var without = Cli(new[] { "cad-brief", input, withoutOutput }.Concat(withoutFlag).ToArray());
            var withoutRooms = RoomsOf(withoutOutput);
            Check(without.Exit == 0 && withoutRooms == expectedWithout && withoutRooms.Contains("\"InsertPath\":[\"", StringComparison.Ordinal) /* deliberate red: region WITHOUT the flag */,
                label + ": without " + Flag + " draft.json rooms equal the default-option in-process rooms (no block region)",
                R(without) + " rooms " + Clip(withoutRooms));
        }

        // A value option followed by the flag: the flag is read as that option's value and refused, nothing written.
        foreach (var (option, detail) in new[]
        {
            ("--page", "--page: a page number in [1, 10000]"),
            ("--metres-per-unit", "--metres-per-unit: a finite number of metres in [1e-9, 1000]"),
        })
        {
            var output = Path.Combine(scratch, "refused" + option + ".json");
            var r = Cli("cad-brief", input, output, option, Flag);
            var expected = JsonSerializer.Serialize(new { status = "failed", code = "cad_option_value_invalid", detail });
            Check(r.Exit == 1 && r.Out == expected && !File.Exists(output),
                option + " " + Flag + " is the exact cad_option_value_invalid envelope and writes nothing", R(r) + " expected " + expected);
        }
    }
}
