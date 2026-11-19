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
// P6-26 (nested, MINSERT cell and mirrored inserts) is the RunTransformed section below; P6-28 (rotated inserts,
// rotated MINSERT cells and non-zero block base points) is the RunRotated section.
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
            if (!process.WaitForExit(180000))
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
        Check(included.Rooms.Count == 2 && Room(included, "dormitorio") is { Region: { } dormitorioRegion } d && dormitorioRegion.InsertPath.SequenceEqual(new[] { "I1" }) && Near(d.RegionAreaSquareMetres, 12)
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
            var flagged = Cli(new[] { "cad-brief", input, withOutput }.Concat(withFlag).ToArray());
            var withRooms = RoomsOf(withOutput);
            Check(flagged.Exit == 0 && withRooms == expectedWith && expectedWith.Contains("\"InsertPath\":[\"", StringComparison.Ordinal),
                label + ": with " + Flag + " draft.json rooms equal the in-process IncludeBlockRegions rooms (a region from the block insert)",
                R(flagged) + " rooms " + Clip(withRooms));

            var expectedWithout = JsonSerializer.Serialize(InProcess(false, metres).Rooms, RoomJson);
            var withoutOutput = Path.Combine(scratch, "without-" + label + ".json");
            var plain = Cli(new[] { "cad-brief", input, withoutOutput }.Concat(withoutFlag).ToArray());
            var withoutRooms = RoomsOf(withoutOutput);
            Check(plain.Exit == 0 && withoutRooms == expectedWithout && withoutRooms != withRooms && !withoutRooms.Contains("\"InsertPath\":[\"", StringComparison.Ordinal),
                label + ": without " + Flag + " draft.json rooms equal the default-option in-process rooms (no block region)",
                R(plain) + " rooms " + Clip(withoutRooms));
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
            Check(r.Exit == 1 && r.Out == expected && r.Err.Length == 0 && !File.Exists(output),
                option + " " + Flag + " is the exact cad_option_value_invalid envelope, empty stderr, and writes nothing", R(r) + " expected " + expected);
        }

        RunTransformed(scratch, Cli, RoomsOf, R, Clip, Check);
        RunRotated(scratch, Cli, RoomsOf, R, Clip, Check);
    }

    // P6-26 block regions through nested, MINSERT and mirrored inserts. A second generated millimetre DXF holds three
    // labelled rooms whose only closed regions are reached through transformed inserts:
    //   * "Bano": block INNER (1000 x 1250 rectangle) inserted as N2 at (500, 0) inside block OUTER, which model space
    //     inserts as N1 at (20000, 0) with scale 2: world rectangle (21000, 0)-(23000, 2500), 5 m2, InsertPath [N1, N2].
    //   * "Estudio": block CELL (3000 x 2000 rectangle) placed by MINSERT M1 at (0, 10000), 3 columns (70) x 2 rows (71),
    //     column spacing 5000, row spacing 4000; the label sits in cell row 0, column 2: world (10000, 10000)-(13000, 12000),
    //     6 m2, InsertPath [M1[0,2]] (asymmetric, so a row/column swap or a 70/71 swap has no such cell and fails).
    //   * "Sala": block MIR (4000 x 2500 rectangle) inserted as X1 at (40000, 0) with X scale -1.5 (mirrored): world
    //     rectangle (34000, 0)-(40000, 2500), 15 m2 (positive although the transformed outline runs clockwise), InsertPath [X1].
    // Oracles: through the real CLI with the flag draft.json rooms equal the in-process IncludeBlockRegions rooms, each
    // room's region has the pinned InsertPath, centre and positive area of the transformed rectangle; without the flag the
    // rooms equal the default-option in-process rooms and none has a region or an insert path.
    const string TransformedFileName = "block-region-transformed.dxf";

    static string TransformedDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS")
        + G("0 BLOCK|5 B10|8 0|2 INNER|70 0|10 0|20 0|30 0") + Rect("B11", 0, 0, 1000, 1250, "0") + G("0 ENDBLK|5 B12|8 0")
        + G("0 BLOCK|5 B20|8 0|2 OUTER|70 0|10 0|20 0|30 0") + G("0 INSERT|5 N2|8 0|2 INNER|10 500|20 0|30 0") + G("0 ENDBLK|5 B22|8 0")
        + G("0 BLOCK|5 B30|8 0|2 CELL|70 0|10 0|20 0|30 0") + Rect("B31", 0, 0, 3000, 2000, "0") + G("0 ENDBLK|5 B32|8 0")
        + G("0 BLOCK|5 B40|8 0|2 MIR|70 0|10 0|20 0|30 0") + Rect("B41", 0, 0, 4000, 2500, "0") + G("0 ENDBLK|5 B42|8 0")
        + G("0 ENDSEC|0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 N1|8 ROOMS|2 OUTER|10 20000|20 0|30 0|41 2|42 2") + Text("21", 22000, 1000, "Bano")
        + G("0 INSERT|5 M1|8 ROOMS|2 CELL|10 0|20 10000|30 0|70 3|71 2|44 5000|45 4000") + Text("22", 11000, 10500, "Estudio")
        + G("0 INSERT|5 X1|8 ROOMS|2 MIR|10 40000|20 0|30 0|41 -1.5|42 1") + Text("23", 37000, 1000, "Sala")
        + G("0 ENDSEC|0 EOF");

    static void RunTransformed(string scratch, Func<string[], (int Exit, string Out, string Err)> cli, Func<string, string> roomsOf,
        Func<(int Exit, string Out, string Err), string> r, Func<string, string> clip, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "transformed: " + name, detail);
        var bytes = new UTF8Encoding(false).GetBytes(TransformedDrawing());
        var input = Path.Combine(scratch, TransformedFileName);
        File.WriteAllBytes(input, bytes);
        BriefDraft InProcess(bool include) =>
            BriefExtract.FromDxf(DxfReader.Parse(bytes, TransformedFileName), new BriefExtractOptions { IncludeBlockRegions = include });
        static bool Near(double? value, double expected) => value is { } v && Math.Abs(v - expected) < 1e-9;
        // Shoelace area in m2 of the expected world rectangle (millimetre corners), independent of the extractor.
        static double Area(double x0, double y0, double x1, double y1)
        {
            var corners = new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };
            double twice = 0;
            for (var i = 0; i < corners.Length; i++) { var (ax, ay) = corners[i]; var (bx, by) = corners[(i + 1) % corners.Length]; twice += ax * by - bx * ay; }
            return Math.Abs(twice) / 2 * 1e-6;
        }

        var expectations = new[]
        {
            (Id: "bano", Path: new[] { "N1", "N2" }, X0: 21000.0, Y0: 0.0, X1: 23000.0, Y1: 2500.0),
            (Id: "estudio", Path: new[] { "M1[0,2]" }, X0: 10000.0, Y0: 10000.0, X1: 13000.0, Y1: 12000.0),
            (Id: "sala", Path: new[] { "X1" }, X0: 34000.0, Y0: 0.0, X1: 40000.0, Y1: 2500.0),
        };

        var included = InProcess(true);
        var excluded = InProcess(false);
        foreach (var e in expectations)
        {
            var room = included.Rooms.FirstOrDefault(x => x.Id == e.Id);
            var area = Area(e.X0, e.Y0, e.X1, e.Y1);
            Check(room is { Region: { } region } && region.InsertPath.SequenceEqual(e.Path)
                    && Near(region.At.X, (e.X0 + e.X1) / 2) && Near(region.At.Y, (e.Y0 + e.Y1) / 2)
                    && room.RegionAreaSquareMetres > 0 && Near(room.RegionAreaSquareMetres, area),
                "in process IncludeBlockRegions: " + e.Id + " region InsertPath [" + string.Join(", ", e.Path) + "], centre and positive area " + F(area) + " m2 of the transformed rectangle",
                clip(JsonSerializer.Serialize(room, RoomJson)));
            var plainRoom = excluded.Rooms.FirstOrDefault(x => x.Id == e.Id);
            Check(plainRoom is { Region: null, RegionAreaSquareMetres: null },
                "in process default options: " + e.Id + " has no region", clip(JsonSerializer.Serialize(plainRoom, RoomJson)));
        }

        var expectedWith = JsonSerializer.Serialize(included.Rooms, RoomJson);
        var withOutput = Path.Combine(scratch, "transformed-with.json");
        var flagged = cli(new[] { "cad-brief", input, withOutput, Flag });
        var withRooms = roomsOf(withOutput);
        Check(flagged.Exit == 0 && withRooms == expectedWith
                && expectedWith.Contains("\"InsertPath\":[\"N1\",\"N2\"]", StringComparison.Ordinal)
                && expectedWith.Contains("\"InsertPath\":[\"M1[0,2]\"]", StringComparison.Ordinal)
                && expectedWith.Contains("\"InsertPath\":[\"X1\"]", StringComparison.Ordinal),
            "with " + Flag + " draft.json rooms equal the in-process IncludeBlockRegions rooms (nested, MINSERT cell and mirrored insert paths)",
            r(flagged) + " rooms " + clip(withRooms));

        var expectedWithout = JsonSerializer.Serialize(excluded.Rooms, RoomJson);
        var withoutOutput = Path.Combine(scratch, "transformed-without.json");
        var plain = cli(new[] { "cad-brief", input, withoutOutput });
        var withoutRooms = roomsOf(withoutOutput);
        Check(plain.Exit == 0 && withoutRooms == expectedWithout && withoutRooms != withRooms && !withoutRooms.Contains("\"InsertPath\":[\"", StringComparison.Ordinal),
            "without " + Flag + " draft.json rooms equal the default-option in-process rooms (no block region)",
            r(plain) + " rooms " + clip(withoutRooms));
    }

    // P6-28 block regions through rotated inserts, a rotated MINSERT cell and blocks with a non-zero base point. A third
    // generated millimetre DXF holds five labelled rooms:
    //   * "Comedor": block ROT (4000 x 2000 rectangle at the origin) inserted as R1 at (60000, 0) rotated 30 degrees:
    //     centre (61232.05, 1866.03), 8 m2, InsertPath [R1].
    //   * "Despensa": a label at (63500, 200), inside the unrotated footprint (60000, 0)-(64000, 2000) of R1 (and inside the
    //     footprint rotated by -30 degrees) but outside the true 30-degree rectangle, so it must have no region.
    //   * "Oficina": block CELL2 (2000 x 1000) placed by MINSERT M2 at (0, 30000) rotated 60 degrees, 2 columns x 3 rows,
    //     column spacing 3000, row spacing 2000; the label sits in cell row 2, column 1 (the cell offset turns with the
    //     insert): centre (-1897.11, 35714.10), 2 m2, InsertPath [M2[2,1]].
    //   * "Lavadero": block BASE with base point (1000, 500) (BLOCK groups 10/20) holding the rectangle (2000, 1000)-(5000,
    //     3500), inserted as L1 at (0, 60000): world (1000, 60500)-(4000, 63000), centre (2500, 61750), 7.5 m2, [L1].
    //   * "Garaje": the same block BASE inserted as G1 at (20000, 60000), rotated 30 degrees, non-uniform scale X 2, Y 1.5:
    //     world = insertion + rotate(scale(p - base)), centre (23017.63, 64773.32), 22.5 m2, InsertPath [G1] (the wrong
    //     order scale(rotate(p - base)) would centre at (22580.13, 64148.32)).
    // Out of scope: MINSERT spacing under a non-unit insert scale (M2 has scale 1), rotated inserts inside nested chains,
    // and mirrored OCS combined with rotation.
    // Expected corners come from an independent transform written here (insertion + R(angle) * (cell offset + S * (p -
    // base))), cross-checked against hand-computed centres; the centre is the bounding-box centre of the transformed
    // corners and the area their shoelace area, positive. Through the real CLI with the flag draft.json rooms equal the
    // in-process IncludeBlockRegions rooms; without the flag they equal the default-option rooms and none has a region.
    const string RotatedFileName = "block-region-rotated.dxf";

    static string RotatedDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS")
        + G("0 BLOCK|5 B50|8 0|2 ROT|70 0|10 0|20 0|30 0") + Rect("B51", 0, 0, 4000, 2000, "0") + G("0 ENDBLK|5 B52|8 0")
        + G("0 BLOCK|5 B60|8 0|2 CELL2|70 0|10 0|20 0|30 0") + Rect("B61", 0, 0, 2000, 1000, "0") + G("0 ENDBLK|5 B62|8 0")
        + G("0 BLOCK|5 B70|8 0|2 BASE|70 0|10 1000|20 500|30 0") + Rect("B71", 2000, 1000, 5000, 3500, "0") + G("0 ENDBLK|5 B72|8 0")
        + G("0 ENDSEC|0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 R1|8 ROOMS|2 ROT|10 60000|20 0|30 0|50 30")
        + G("0 INSERT|5 M2|8 ROOMS|2 CELL2|10 0|20 30000|30 0|50 60|70 2|71 3|44 3000|45 2000")
        + G("0 INSERT|5 L1|8 ROOMS|2 BASE|10 0|20 60000|30 0")
        + G("0 INSERT|5 G1|8 ROOMS|2 BASE|10 20000|20 60000|30 0|41 2|42 1.5|50 30")
        + string.Concat(RotatedLabels.Select((l, i) => Text((31 + i).ToString(CultureInfo.InvariantCulture), l.X, l.Y, l.Name)))
        + G("0 ENDSEC|0 EOF");

    // Independent placement: insertion + R(degrees) * (offset + (sx, sy) * (p - base)).
    static (double X, double Y) Place((double X, double Y) insertion, double degrees, double sx, double sy, (double X, double Y) basePoint, (double X, double Y) offset, (double X, double Y) p)
    {
        var radians = degrees * Math.PI / 180;
        double lx = offset.X + sx * (p.X - basePoint.X), ly = offset.Y + sy * (p.Y - basePoint.Y);
        return (insertion.X + lx * Math.Cos(radians) - ly * Math.Sin(radians), insertion.Y + lx * Math.Sin(radians) + ly * Math.Cos(radians));
    }

    sealed record RotatedCase(string Id, string Name, string[] Path, (double X, double Y)[] Corners, (double X, double Y) Hand, double HandArea);

    static RotatedCase Case(string id, string name, string[] path, (double X, double Y) insertion, double degrees, double sx, double sy, (double X, double Y) basePoint, (double X, double Y) offset,
        double x0, double y0, double x1, double y1, (double X, double Y) hand, double handArea) =>
        new(id, name, path, new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) }.Select(c => Place(insertion, degrees, sx, sy, basePoint, offset, c)).ToArray(), hand, handArea);

    static readonly RotatedCase[] RotatedCases =
    {
        Case("comedor", "Comedor", new[] { "R1" }, (60000, 0), 30, 1, 1, (0, 0), (0, 0), 0, 0, 4000, 2000, (61232.0508, 1866.0254), 8),
        Case("oficina", "Oficina", new[] { "M2[2,1]" }, (0, 30000), 60, 1, 1, (0, 0), (1 * 3000, 2 * 2000), 0, 0, 2000, 1000, (-1897.1143, 35714.1016), 2),
        Case("lavadero", "Lavadero", new[] { "L1" }, (0, 60000), 0, 1, 1, (1000, 500), (0, 0), 2000, 1000, 5000, 3500, (2500, 61750), 7.5),
        Case("garaje", "Garaje", new[] { "G1" }, (20000, 60000), 30, 2, 1.5, (1000, 500), (0, 0), 2000, 1000, 5000, 3500, (23017.6270, 64773.3168), 22.5),
    };

    static (double X, double Y) BoxCentre((double X, double Y)[] corners) =>
        ((corners.Min(c => c.X) + corners.Max(c => c.X)) / 2, (corners.Min(c => c.Y) + corners.Max(c => c.Y)) / 2);

    static double ShoelaceSquareMetres((double X, double Y)[] corners)
    {
        double twice = 0;
        for (var i = 0; i < corners.Length; i++) { var (ax, ay) = corners[i]; var (bx, by) = corners[(i + 1) % corners.Length]; twice += ax * by - bx * ay; }
        return Math.Abs(twice) / 2 * 1e-6;
    }

    // Labels at the transformed rectangle centres, plus Despensa outside the rotated rectangle.
    static readonly (string Name, double X, double Y)[] RotatedLabels =
        RotatedCases.Select(c => { var (x, y) = BoxCentre(c.Corners); return (Name: c.Name, X: x, Y: y); }).Append(("Despensa", 63500.0, 200.0)).ToArray();

    static void RunRotated(string scratch, Func<string[], (int Exit, string Out, string Err)> cli, Func<string, string> roomsOf,
        Func<(int Exit, string Out, string Err), string> r, Func<string, string> clip, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "rotated: " + name, detail);
        var bytes = new UTF8Encoding(false).GetBytes(RotatedDrawing());
        var input = Path.Combine(scratch, RotatedFileName);
        File.WriteAllBytes(input, bytes);
        BriefDraft InProcess(bool include) =>
            BriefExtract.FromDxf(DxfReader.Parse(bytes, RotatedFileName), new BriefExtractOptions { IncludeBlockRegions = include });
        static bool Near(double? value, double expected, double tolerance = 1e-6) => value is { } v && Math.Abs(v - expected) < tolerance;
        static string P((double X, double Y) p) => "(" + F(p.X) + ", " + F(p.Y) + ")";

        var included = InProcess(true);
        var excluded = InProcess(false);
        foreach (var c in RotatedCases)
        {
            var centre = BoxCentre(c.Corners);
            var area = ShoelaceSquareMetres(c.Corners);
            // The independent transform agrees with the hand-computed centre and area (guards the oracle itself).
            Check(Near(centre.X, c.Hand.X, 1e-3) && Near(centre.Y, c.Hand.Y, 1e-3) && Near(area, c.HandArea, 1e-9),
                "oracle: " + c.Id + " independently transformed centre " + P(centre) + " and area " + F(area) + " m2 match the hand values " + P(c.Hand) + ", " + F(c.HandArea),
                "corners " + string.Join(" ", c.Corners.Select(P)));
            var room = included.Rooms.FirstOrDefault(x => x.Id == c.Id);
            Check(room is { Region: { } region } && region.InsertPath.SequenceEqual(c.Path)
                    && Near(region.At.X, centre.X) && Near(region.At.Y, centre.Y)
                    && room.RegionAreaSquareMetres > 0 && Near(room.RegionAreaSquareMetres, area, 1e-9),
                "in process IncludeBlockRegions: " + c.Id + " region InsertPath [" + string.Join(", ", c.Path) + "], centre " + P(centre) + " and positive area " + F(area) + " m2 of the independently transformed rectangle",
                clip(JsonSerializer.Serialize(room, RoomJson)));
            var plainRoom = excluded.Rooms.FirstOrDefault(x => x.Id == c.Id);
            Check(plainRoom is { Region: null, RegionAreaSquareMetres: null },
                "in process default options: " + c.Id + " has no region", clip(JsonSerializer.Serialize(plainRoom, RoomJson)));
        }
        // Containment follows the rotated outline, not the unrotated footprint nor a -30 degree rotation. Self-check of the
        // fixture: Despensa lies inside the unrotated and the -30 degree 4000 x 2000 rectangles at R1, outside the 30-degree one.
        static bool InRot(double degrees, (double X, double Y) q)
        {
            var radians = -degrees * Math.PI / 180;
            double dx = q.X - 60000, dy = q.Y;
            double lx = dx * Math.Cos(radians) - dy * Math.Sin(radians), ly = dx * Math.Sin(radians) + dy * Math.Cos(radians);
            return lx > 0 && lx < 4000 && ly > 0 && ly < 2000;
        }
        var despensaPoint = (X: 63500.0, Y: 200.0);
        Check(InRot(0, despensaPoint) && InRot(-30, despensaPoint) && !InRot(30, despensaPoint),
            "oracle: Despensa (63500, 200) is inside R1's unrotated and -30 degree rectangles and outside the 30-degree one", "");
        var despensa = included.Rooms.FirstOrDefault(x => x.Id == "despensa");
        var comedor = included.Rooms.FirstOrDefault(x => x.Id == "comedor");
        Check(despensa is { Region: null, RegionAreaSquareMetres: null } && comedor is { Region: not null } && included.Rooms.Count == 5,
            "in process IncludeBlockRegions: Despensa (63500, 200), outside the 30-degree rectangle of R1, has no region; Comedor keeps it; five rooms",
            clip(JsonSerializer.Serialize(included.Rooms.Select(x => new { x.Id, x.Region, x.RegionAreaSquareMetres }), RoomJson)));

        var expectedWith = JsonSerializer.Serialize(included.Rooms, RoomJson);
        var withOutput = Path.Combine(scratch, "rotated-with.json");
        var flagged = cli(new[] { "cad-brief", input, withOutput, Flag });
        var withRooms = roomsOf(withOutput);
        Check(flagged.Exit == 0 && withRooms == expectedWith && RotatedCases.All(c => withRooms.Contains("\"InsertPath\":[\"" + c.Path[0] + "\"]", StringComparison.Ordinal)),
            "with " + Flag + " draft.json rooms equal the in-process IncludeBlockRegions rooms (rotated insert, rotated MINSERT cell, base-point inserts)",
            r(flagged) + " rooms " + clip(withRooms));

        var expectedWithout = JsonSerializer.Serialize(excluded.Rooms, RoomJson);
        var withoutOutput = Path.Combine(scratch, "rotated-without.json");
        var plain = cli(new[] { "cad-brief", input, withoutOutput });
        var withoutRooms = roomsOf(withoutOutput);
        Check(plain.Exit == 0 && withoutRooms == expectedWithout && withoutRooms != withRooms && !withoutRooms.Contains("\"InsertPath\":[\"", StringComparison.Ordinal)
                && excluded.Rooms.All(x => x.Region is null),
            "without " + Flag + " draft.json rooms equal the default-option in-process rooms (no block region)",
            r(plain) + " rooms " + clip(withoutRooms));
    }
}
