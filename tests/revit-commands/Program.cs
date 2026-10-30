using Bim.Core;
using Bim.Mcp;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// C02 (ADR0009) contract oracle: envelope, every validator branch, arc geometry, copies-only guard, catalogue,
// generated MCP tools, and the CLI revit-command verb end to end against an in-process fake add-in pipe server.
// No Revit is involved; a passing run is unit-tested evidence only.
const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3";
const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
const string RealPath = @"C:\Projects\Real\Tower.rvt";
int passed = 0, failed = 0;
var work = Path.Combine(Path.GetTempPath(), "revit-commands-" + Guid.NewGuid().ToString("N"));

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::revit-commands FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}

JsonObject Envelope(string command, JsonNode commandArgs, string path = FixturePath) => new()
{
    ["protocol_version"] = 1,
    ["operation_id"] = "op-1",
    ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
    ["command"] = command,
    ["args"] = commandArgs,
};
JsonArray Pair(double x, double y) => new(x, y);
JsonObject Line(double x0, double y0, double x1, double y1) => new() { ["kind"] = "line", ["start"] = Pair(x0, y0), ["end"] = Pair(x1, y1) };
JsonObject Arc3(double sx, double sy, double ex, double ey, double tx, double ty) => new() { ["kind"] = "arc", ["start"] = Pair(sx, sy), ["end"] = Pair(ex, ey), ["through"] = Pair(tx, ty) };
JsonObject ArcC(double cx, double cy, double radius, double a0, double a1) => new() { ["kind"] = "arc", ["center"] = Pair(cx, cy), ["radius_mm"] = radius, ["start_angle_deg"] = a0, ["end_angle_deg"] = a1 };
JsonObject Wall(JsonNode curve) => new() { ["level"] = "Level 1", ["type"] = "Generic - 200mm", ["height_mm"] = 3000, ["curve"] = curve };
JsonObject Opening(string category) => new() { ["category"] = category, ["symbol"] = "M_Single-Flush: 0915 x 2134mm", ["host_wall_uid"] = WallUid, ["position_along_mm"] = 1500 };
double[] P(double x, double y) => new[] { x, y };
JsonObject Floor(params double[][] points) => new() { ["level"] = "Level 1", ["type"] = "Generic 150mm", ["boundary"] = new JsonArray(points.Select(p => (JsonNode?)Pair(p[0], p[1])).ToArray()) };
JsonObject Room() => new() { ["level"] = "Level 1", ["point"] = Pair(2000, 1500), ["name"] = "Kitchen", ["number"] = "101" };

RevitCommandException? Refusal(string json)
{
    try { RevitCommandProtocol.Parse(json); return null; }
    catch (RevitCommandException error) { return error; }
    catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
}
void RejectsText(string name, string json, string code, string? field = null)
{
    var error = Refusal(json);
    Check(error is not null && error.Code == code && (field is null || error.Field == field), name,
        error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
}
void Rejects(string name, JsonNode envelope, string code, string? field = null) => RejectsText(name, envelope.ToJsonString(), code, field);
RevitCommandEnvelope? Accepts(string name, JsonNode envelope)
{
    try { var parsed = RevitCommandProtocol.Parse(envelope.ToJsonString()); Check(true, name); return parsed; }
    catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
}
string? CodeOf(Action action)
{
    try { action(); return null; }
    catch (RevitCommandException error) { return error.Code; }
}
bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

try
{
    // ---- Envelope ----
    var straight = Accepts("valid straight wall envelope parses", Envelope("wall.create", Wall(Line(0, 0, 5000, 0))));
    Check(straight?.Args is WallCreateArgs { Curve: LineWallCurve straightLine } straightWall && straightWall.Height.Millimetres == 3000 && straightLine.End.X.Millimetres == 5000 && straightWall.BaseOffset.Metres == 0 && !straightWall.Structural && straightWall.Level == "Level 1",
        "straight wall binds typed millimetres (height 3000, end x 5000, default offset 0, non-structural)");
    Check(straight is { ProtocolVersion: 1, OperationId: "op-1", Command: "wall.create" } && straight.Document == new RevitDocumentRef(FixturePath, Uid) && straight.Spec.Writes, "envelope carries version, operation id, document identity and write spec");
    RejectsText("non-JSON envelope", "not json", RevitCommandCodes.EnvelopeInvalid);
    RejectsText("array envelope", "[]", RevitCommandCodes.EnvelopeInvalid);
    var extra = Envelope("query.levels", new JsonObject()); extra["colour"] = "red";
    Rejects("unexpected envelope member", extra, RevitCommandCodes.EnvelopeInvalid, "colour");
    RejectsText("duplicate envelope member", "{\"protocol_version\":1," + Envelope("query.levels", new JsonObject()).ToJsonString()[1..], RevitCommandCodes.EnvelopeInvalid, "protocol_version");
    foreach (var (label, value) in new (string, JsonNode?)[] { ("missing", null), ("2", 2), ("string", "1"), ("fraction", 1.5) })
    {
        var envelope = Envelope("query.levels", new JsonObject());
        if (value is null) envelope.Remove("protocol_version"); else envelope["protocol_version"] = value;
        Rejects("protocol_version " + label + " is unsupported", envelope, RevitCommandCodes.ProtocolVersionUnsupported, "protocol_version");
    }
    foreach (var (label, value) in new (string, JsonNode?)[] { ("missing", null), ("empty", ""), ("leading dash", "-x"), ("space", "a b"), ("129 chars", "a" + new string('b', 128)), ("number", 7) })
    {
        var envelope = Envelope("query.levels", new JsonObject());
        if (value is null) envelope.Remove("operation_id"); else envelope["operation_id"] = value;
        Rejects("operation_id " + label + " is invalid", envelope, RevitCommandCodes.OperationIdInvalid, "operation_id");
    }
    var longId = Envelope("query.levels", new JsonObject()); longId["operation_id"] = "a" + new string('b', 127);
    Accepts("operation_id of 128 chars accepted", longId);
    var noDocument = Envelope("query.levels", new JsonObject()); noDocument.Remove("document");
    Rejects("missing document", noDocument, RevitCommandCodes.DocumentPathInvalid, "document");
    foreach (var (label, path) in new[] { ("relative", @"fixture.rvt"), ("dot-dot", @"C:\Repo\.bim\native-runs\..\..\Projects\Tower.rvt"), ("device namespace", @"\\?\C:\Repo\a.rvt"), ("empty", "") })
        Rejects("document path " + label + " is invalid", Envelope("query.levels", new JsonObject(), path), RevitCommandCodes.DocumentPathInvalid, "document.path");
    var badIdentity = Envelope("query.levels", new JsonObject()); badIdentity["document"]!["unique_id"] = "abc";
    Rejects("document unique_id not a Revit UniqueId", badIdentity, RevitCommandCodes.DocumentIdentityInvalid, "document.unique_id");
    var extraDocument = Envelope("query.levels", new JsonObject()); extraDocument["document"]!["title"] = "x";
    Rejects("unexpected document member", extraDocument, RevitCommandCodes.EnvelopeInvalid, "document.title");
    Rejects("unknown command", Envelope("wall.delete", new JsonObject()), RevitCommandCodes.CommandUnknown, "command");
    var numericCommand = Envelope("query.levels", new JsonObject()); numericCommand["command"] = 5;
    Rejects("non-string command", numericCommand, RevitCommandCodes.CommandUnknown, "command");
    var noArgs = Envelope("query.levels", new JsonObject()); noArgs.Remove("args");
    Rejects("missing args", noArgs, RevitCommandCodes.ArgsInvalid, "args");

    // ---- Schema (structure) ----
    var textHeight = Wall(Line(0, 0, 5000, 0)); textHeight["height_mm"] = "3000";
    Rejects("height_mm as string", Envelope("wall.create", textHeight), RevitCommandCodes.ArgsInvalid, "args.height_mm");
    var noType = Wall(Line(0, 0, 5000, 0)); noType.Remove("type");
    Rejects("missing wall type", Envelope("wall.create", noType), RevitCommandCodes.ArgsInvalid, "args.type");
    var colour = Wall(Line(0, 0, 5000, 0)); colour["colour"] = "red";
    Rejects("unexpected wall argument", Envelope("wall.create", colour), RevitCommandCodes.ArgsInvalid, "args.colour");
    var structuralText = Wall(Line(0, 0, 5000, 0)); structuralText["structural"] = "no";
    Rejects("structural as string", Envelope("wall.create", structuralText), RevitCommandCodes.ArgsInvalid, "args.structural");
    var emptyLevel = Wall(Line(0, 0, 5000, 0)); emptyLevel["level"] = "";
    Rejects("empty level reference", Envelope("wall.create", emptyLevel), RevitCommandCodes.ArgsInvalid, "args.level");
    var controlLevel = Wall(Line(0, 0, 5000, 0)); controlLevel["level"] = "Level\u00011";
    Rejects("level reference with control character", Envelope("wall.create", controlLevel), RevitCommandCodes.ArgsInvalid, "args.level");
    var longType = Wall(Line(0, 0, 5000, 0)); longType["type"] = new string('t', 257);
    Rejects("wall type reference over 256 chars", Envelope("wall.create", longType), RevitCommandCodes.ArgsInvalid, "args.type");
    var spline = Line(0, 0, 5000, 0); spline["kind"] = "spline";
    Rejects("unknown curve kind", Envelope("wall.create", Wall(spline)), RevitCommandCodes.ArgsInvalid, "args.curve.kind");
    var shortPoint = Line(0, 0, 5000, 0); shortPoint["start"] = new JsonArray(1);
    Rejects("curve point with one coordinate", Envelope("wall.create", Wall(shortPoint)), RevitCommandCodes.ArgsInvalid, "args.curve.start");
    var textPoint = Line(0, 0, 5000, 0); textPoint["start"] = new JsonArray(1, "2");
    Rejects("curve coordinate as string", Envelope("wall.create", Wall(textPoint)), RevitCommandCodes.ArgsInvalid, "args.curve.start[1]");
    var mixedArc = Arc3(0, 0, 2000, 0, 1000, 1000); mixedArc["center"] = Pair(1000, 0);
    Rejects("arc mixing three-point and centre forms", Envelope("wall.create", Wall(mixedArc)), RevitCommandCodes.ArgsInvalid, "args.curve.center");
    var incompleteCentre = ArcC(0, 0, 1000, 0, 90); incompleteCentre.Remove("end_angle_deg");
    Rejects("centre arc without end angle", Envelope("wall.create", Wall(incompleteCentre)), RevitCommandCodes.ArgsInvalid, "args.curve.end_angle_deg");
    Rejects("query.levels with an argument", Envelope("query.levels", new JsonObject { ["x"] = 1 }), RevitCommandCodes.ArgsInvalid, "args.x");
    Rejects("query args not an object", Envelope("query.levels", new JsonArray()), RevitCommandCodes.ArgsInvalid, "args");
    Rejects("family symbol category plural (vocabulary is door|window, as in opening.place)", Envelope("query.family_symbols", new JsonObject { ["category"] = "doors" }), RevitCommandCodes.ArgsInvalid, "args.category");
    Rejects("family symbol category missing", Envelope("query.family_symbols", new JsonObject()), RevitCommandCodes.ArgsInvalid, "args.category");
    Rejects("opening category wall", Envelope("opening.place", Opening("wall")), RevitCommandCodes.ArgsInvalid, "args.category");
    var badHost = Opening("door"); badHost["host_wall_uid"] = "not-a-uid";
    Rejects("opening host wall not a UniqueId", Envelope("opening.place", badHost), RevitCommandCodes.ArgsInvalid, "args.host_wall_uid");
    var newlineHost = Opening("door"); newlineHost["host_wall_uid"] = WallUid + "\n";
    Rejects("opening host UniqueId with trailing newline", Envelope("opening.place", newlineHost), RevitCommandCodes.ArgsInvalid, "args.host_wall_uid");
    var noPosition = Opening("door"); noPosition.Remove("position_along_mm");
    Rejects("opening without position", Envelope("opening.place", noPosition), RevitCommandCodes.ArgsInvalid, "args.position_along_mm");
    Rejects("floor with two vertices", Envelope("floor.create", Floor(P(0, 0), P(1000, 0))), RevitCommandCodes.ArgsInvalid, "args.boundary");
    Rejects("floor over 1000 vertices", Envelope("floor.create", Floor(Enumerable.Range(0, 1001).Select(i => P(Math.Cos(i * 0.006) * 5000, Math.Sin(i * 0.006) * 5000)).ToArray())), RevitCommandCodes.ArgsInvalid, "args.boundary");
    var longName = Room(); longName["name"] = new string('n', 257);
    Rejects("room name over 256 chars", Envelope("room.create", longName), RevitCommandCodes.ArgsInvalid, "args.name");
    var emptyNumber = Room(); emptyNumber["number"] = "";
    Rejects("empty room number", Envelope("room.create", emptyNumber), RevitCommandCodes.ArgsInvalid, "args.number");
    var noPoint = Room(); noPoint.Remove("point");
    Rejects("room without point", Envelope("room.create", noPoint), RevitCommandCodes.ArgsInvalid, "args.point");
    RejectsText("duplicate argument member", Envelope("query.family_symbols", new JsonObject { ["category"] = "door" }).ToJsonString().Replace("{\"category\":\"door\"}", "{\"category\":\"door\",\"category\":\"window\"}"), RevitCommandCodes.ArgsInvalid, "args.category");

    // ---- Units ----
    foreach (var (label, value) in new (string, double)[] { ("zero", 0), ("negative", -1) })
    {
        var height = Wall(Line(0, 0, 5000, 0)); height["height_mm"] = value;
        Rejects("height_mm " + label, Envelope("wall.create", height), RevitCommandCodes.LengthInvalid, "args.height_mm");
    }
    RejectsText("height_mm overflowing to infinity", Envelope("wall.create", Wall(Line(0, 0, 5000, 0))).ToJsonString().Replace("\"height_mm\":3000", "\"height_mm\":1e400"), RevitCommandCodes.LengthInvalid, "args.height_mm");
    var tinyHeight = Wall(Line(0, 0, 5000, 0)); tinyHeight["height_mm"] = 1;
    Rejects("height_mm under 1.2 mm", Envelope("wall.create", tinyHeight), RevitCommandCodes.LengthInvalid, "args.height_mm");
    var negativeOffset = Wall(Line(0, 0, 5000, 0)); negativeOffset["base_offset_mm"] = -500;
    Check(Accepts("negative base offset accepted", Envelope("wall.create", negativeOffset))?.Args is WallCreateArgs { BaseOffset.Millimetres: -500 }, "negative base offset binds -500 mm");
    var structural = Wall(Line(0, 0, 5000, 0)); structural["structural"] = true;
    Check(Accepts("structural wall accepted", Envelope("wall.create", structural))?.Args is WallCreateArgs { Structural: true }, "structural flag binds");
    Rejects("coordinate beyond 1e8 mm", Envelope("wall.create", Wall(Line(2e8, 0, 5000, 0))), RevitCommandCodes.LengthInvalid, "args.curve.start[0]");
    RejectsText("coordinate overflowing to infinity", Envelope("wall.create", Wall(Line(0, 0, 5000, 0))).ToJsonString().Replace("\"end\":[5000,0]", "\"end\":[5000,-1e999]"), RevitCommandCodes.LengthInvalid, "args.curve.end[1]");
    var negativePosition = Opening("door"); negativePosition["position_along_mm"] = -1;
    Rejects("negative position along wall", Envelope("opening.place", negativePosition), RevitCommandCodes.LengthInvalid, "args.position_along_mm");
    var zeroPosition = Opening("door"); zeroPosition["position_along_mm"] = 0;
    Check(Accepts("position along wall 0 accepted", Envelope("opening.place", zeroPosition))?.Args is OpeningPlaceArgs { PositionAlong.Metres: 0, Sill: null }, "door at wall start binds with no sill");
    var negativeSill = Opening("window"); negativeSill["sill_mm"] = -1;
    Rejects("negative sill", Envelope("opening.place", negativeSill), RevitCommandCodes.LengthInvalid, "args.sill_mm");
    var window = Opening("window"); window["sill_mm"] = 900;
    Check(Accepts("window with sill accepted", Envelope("opening.place", window))?.Args is OpeningPlaceArgs { Category: "window", Sill: { } sill } && Near(sill.Millimetres, 900), "window sill binds 900 mm");
    var doorSill = Opening("door"); doorSill["sill_mm"] = 0;
    Rejects("sill_mm on a door", Envelope("opening.place", doorSill), RevitCommandCodes.ArgsInvalid, "args.sill_mm");
    var zeroSill = Opening("window"); zeroSill["sill_mm"] = 0;
    Accepts("sill 0 accepted", Envelope("opening.place", zeroSill));
    RejectsText("room point overflowing to infinity", Envelope("room.create", Room()).ToJsonString().Replace("\"point\":[2000,1500]", "\"point\":[1e400,1500]"), RevitCommandCodes.LengthInvalid, "args.point[0]");
    Check(CodeOf(() => RevitUnits.Millimetres(double.NaN, "f", true, true)) == RevitCommandCodes.LengthInvalid, "typed millimetres reject NaN");
    Check(CodeOf(() => RevitUnits.Millimetres(double.PositiveInfinity, "f", true, true)) == RevitCommandCodes.LengthInvalid, "typed millimetres reject +infinity");
    Check(CodeOf(() => RevitUnits.Millimetres(double.NegativeInfinity, "f", true, true)) == RevitCommandCodes.LengthInvalid, "typed millimetres reject -infinity");
    Check(CodeOf(() => RevitUnits.Millimetres(-1, "f", false, true)) == RevitCommandCodes.LengthInvalid && CodeOf(() => RevitUnits.Millimetres(0, "f", true, false)) == RevitCommandCodes.LengthInvalid, "typed millimetres reject negative and zero where invalid");
    Check(Near(RevitUnits.Millimetres(400, "f", false, false).Metres, 0.4, 0) && RevitUnits.Millimetres(-250, "f", true, false).Millimetres == -250, "typed millimetres convert to Length exactly");
    Check(CodeOf(() => RevitUnits.Degrees(double.NaN, "f")) == RevitCommandCodes.AngleInvalid && CodeOf(() => RevitUnits.Degrees(double.PositiveInfinity, "f")) == RevitCommandCodes.AngleInvalid, "typed degrees reject NaN and infinity");
    Check(Near(RevitUnits.Degrees(180, "f").Radians, Math.PI, 1e-15), "typed degrees convert to Angle");

    // ---- Line ----
    Rejects("zero-length line", Envelope("wall.create", Wall(Line(0, 0, 0, 0))), RevitCommandCodes.CurveTooShort, "args.curve");
    Rejects("1 mm line below short-curve tolerance", Envelope("wall.create", Wall(Line(0, 0, 1, 0))), RevitCommandCodes.CurveTooShort, "args.curve");
    Accepts("1.3 mm line accepted", Envelope("wall.create", Wall(Line(0, 0, 1.3, 0))));

    // ---- Three-point arcs ----
    var semicircle = Accepts("three-point semicircle accepted", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, 1000))));
    Check(semicircle?.Args is WallCreateArgs { Curve: ArcThroughWallCurve arc } && Near(arc.Center.X.Millimetres, 1000) && Near(arc.Center.Y.Millimetres, 0) && Near(arc.Radius.Millimetres, 1000) && Near(arc.Sweep.Degrees, 180) && !arc.CounterClockwise,
        "semicircle through (1000,1000): centre (1000,0), radius 1000, sweep 180, clockwise");
    var lower = Accepts("three-point arc below chord accepted", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, -1000))));
    Check(lower?.Args is WallCreateArgs { Curve: ArcThroughWallCurve lowerArc } && lowerArc.CounterClockwise && Near(lowerArc.Sweep.Degrees, 180), "arc through a point below the chord is counter-clockwise");
    var major = Accepts("three-point major arc accepted", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, -3000))));
    Check(major?.Args is WallCreateArgs { Curve: ArcThroughWallCurve majorArc } && Near(majorArc.Radius.Millimetres, 5000.0 / 3, 1e-6) && majorArc.Sweep.Degrees > 180 && majorArc.Sweep.Degrees < 360 && Near(majorArc.Center.Y.Millimetres, -4000.0 / 3, 1e-6),
        "major arc: radius 5000/3, centre y -4000/3, sweep between 180 and 360");
    Rejects("collinear three-point arc", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, 0))), RevitCommandCodes.ArcCollinear, "args.curve");
    Rejects("through point beyond end is collinear", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 3000, 0))), RevitCommandCodes.ArcCollinear, "args.curve");
    Rejects("through point equal to start", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 0, 0))), RevitCommandCodes.ArcCollinear, "args.curve");
    Rejects("through point equal to end", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 2000, 0))), RevitCommandCodes.ArcCollinear, "args.curve");
    Rejects("nearly collinear three-point arc", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, 1e-9))), RevitCommandCodes.ArcCollinear, "args.curve");
    Rejects("three-point arc with start equal to end (sweep 360)", Envelope("wall.create", Wall(Arc3(0, 0, 0, 0, 1000, 1000))), RevitCommandCodes.ArcSweepInvalid, "args.curve");
    Rejects("three-point arc with radius beyond 1e8 mm", Envelope("wall.create", Wall(Arc3(-1e8, 0, 1e8, 0, 0, 2))), RevitCommandCodes.ArcRadiusInvalid, "args.curve");
    Rejects("three-point arc with sagitta under 1.2 mm is collinear", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, 1))), RevitCommandCodes.ArcCollinear, "args.curve");
    Rejects("thin arc 0.1 mm off a 20 km chord is collinear", Envelope("wall.create", Wall(Arc3(0, 0, 2e7, 0, 1e7, 0.1))), RevitCommandCodes.ArcCollinear, "args.curve");
    Accepts("three-point arc with sagitta 2 mm accepted", Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, 2))));
    double Deg(double degrees) => degrees * Math.PI / 180;
    Rejects("three-point arc whose centre lies beyond 1e8 mm", Envelope("wall.create", Wall(Arc3(9.7e7, 0, 1.02e8 + 5e6 * Math.Cos(Deg(181)), 5e6 * Math.Sin(Deg(181)), 1.02e8 + 5e6 * Math.Cos(Deg(179)), 5e6 * Math.Sin(Deg(179))))), RevitCommandCodes.ArcRadiusInvalid, "args.curve");

    // ---- Centre arcs ----
    var quarter = Accepts("centre quarter arc accepted", Envelope("wall.create", Wall(ArcC(0, 0, 1000, 0, 90))));
    Check(quarter?.Args is WallCreateArgs { Curve: ArcCenterWallCurve quarterArc } && Near(quarterArc.Sweep.Degrees, 90) && Near(quarterArc.Radius.Millimetres, 1000), "centre arc binds radius 1000 and sweep 90");
    Accepts("centre arc from -90 to 180 (sweep 270) accepted", Envelope("wall.create", Wall(ArcC(0, 0, 1000, -90, 180))));
    foreach (var (label, radius) in new (string, double)[] { ("zero", 0), ("negative", -5), ("beyond 1e8", 2e8) })
        Rejects("centre arc radius " + label, Envelope("wall.create", Wall(ArcC(0, 0, radius, 0, 90))), RevitCommandCodes.ArcRadiusInvalid, "args.curve.radius_mm");
    RejectsText("centre arc radius overflowing to infinity", Envelope("wall.create", Wall(ArcC(0, 0, 1000, 0, 90))).ToJsonString().Replace("\"radius_mm\":1000", "\"radius_mm\":1e400"), RevitCommandCodes.ArcRadiusInvalid, "args.curve.radius_mm");
    foreach (var (label, a0, a1) in new (string, double, double)[] { ("zero sweep", 45, 45), ("full circle", 0, 360), ("over 360", 0, 400), ("negative sweep", 90, 0) })
        Rejects("centre arc " + label, Envelope("wall.create", Wall(ArcC(0, 0, 1000, a0, a1))), RevitCommandCodes.ArcSweepInvalid, "args.curve");
    RejectsText("centre arc start angle overflowing to infinity", Envelope("wall.create", Wall(ArcC(0, 0, 1000, 0, 90))).ToJsonString().Replace("\"start_angle_deg\":0", "\"start_angle_deg\":1e400"), RevitCommandCodes.AngleInvalid, "args.curve.start_angle_deg");
    Rejects("centre arc shorter than tolerance", Envelope("wall.create", Wall(ArcC(0, 0, 1, 0, 10))), RevitCommandCodes.CurveTooShort, "args.curve");
    Rejects("centre arc whose ends nearly meet (chord under 1.2 mm)", Envelope("wall.create", Wall(ArcC(0, 0, 1000, 0, 359.99))), RevitCommandCodes.ArcSweepInvalid, "args.curve");
    Rejects("centre arc extending beyond 1e8 mm", Envelope("wall.create", Wall(ArcC(9.99e7, 0, 1e6, 0, 90))), RevitCommandCodes.ArcRadiusInvalid, "args.curve");
    Rejects("centre arc centre beyond 1e8 mm", Envelope("wall.create", Wall(ArcC(-2e8, 0, 1000, 0, 90))), RevitCommandCodes.LengthInvalid, "args.curve.center[0]");

    // ---- Revit's own 30000 ft limit (9 144 000 mm) on arc radius, wall height and base offset ----
    Check(RevitCommandGeometry.MaxRevitLengthMm == 9_144_000, "Revit length limit is 30000 ft = 9 144 000 mm");
    Accepts("centre arc radius at the Revit limit accepted", Envelope("wall.create", Wall(ArcC(0, 0, 9_144_000, 0, 10))));
    Rejects("centre arc radius just above the Revit limit", Envelope("wall.create", Wall(ArcC(0, 0, 9_144_001, 0, 10))), RevitCommandCodes.ArcRadiusInvalid, "args.curve.radius_mm");
    Rejects("three-point arc with radius about 9.19e6 mm (above the Revit limit)", Envelope("wall.create", Wall(Arc3(0, 0, 10000, 0, 5000, 1.36))), RevitCommandCodes.ArcRadiusInvalid, "args.curve");
    Accepts("three-point arc with radius about 8.33e6 mm accepted", Envelope("wall.create", Wall(Arc3(0, 0, 10000, 0, 5000, 1.5))));
    var limitHeight = Wall(Line(0, 0, 5000, 0)); limitHeight["height_mm"] = 9_144_000;
    Accepts("wall height at the Revit limit accepted", Envelope("wall.create", limitHeight));
    var highWall = Wall(Line(0, 0, 5000, 0)); highWall["height_mm"] = 9_144_001;
    Rejects("wall height above the Revit limit", Envelope("wall.create", highWall), RevitCommandCodes.LengthInvalid, "args.height_mm");
    var deepOffset = Wall(Line(0, 0, 5000, 0)); deepOffset["base_offset_mm"] = -9_144_001;
    Rejects("wall base offset beyond the Revit limit", Envelope("wall.create", deepOffset), RevitCommandCodes.LengthInvalid, "args.base_offset_mm");

    // ---- Floor boundaries ----
    var square = Accepts("rectangular floor accepted", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(4000, 3000), P(0, 3000))));
    Check(square?.Args is FloorCreateArgs { Boundary.Count: 4 } squareFloor && Near(squareFloor.Boundary[2].X.Millimetres, 4000) && squareFloor.Type == "Generic 150mm", "rectangle binds four typed vertices");
    var closed = Accepts("explicitly closed outline accepted", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(4000, 3000), P(0, 3000), P(0, 0))));
    Check(closed?.Args is FloorCreateArgs { Boundary.Count: 4 }, "repeated closing vertex is dropped");
    Accepts("concave L-shaped floor accepted", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(4000, 2000), P(2000, 2000), P(2000, 4000), P(0, 4000))));
    Accepts("clockwise floor accepted", Envelope("floor.create", Floor(P(0, 0), P(0, 3000), P(4000, 3000), P(4000, 0))));
    Rejects("outline collapsing to two vertices", Envelope("floor.create", Floor(P(0, 0), P(1000, 0), P(0, 0))), RevitCommandCodes.BoundaryTooFewPoints, "args.boundary");
    Rejects("repeated consecutive vertex", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(4000, 0), P(4000, 3000))), RevitCommandCodes.BoundaryEdgeTooShort, "args.boundary[1]");
    Rejects("bow-tie outline", Envelope("floor.create", Floor(P(0, 0), P(2000, 2000), P(2000, 0), P(0, 2000))), RevitCommandCodes.BoundarySelfIntersecting);
    Rejects("collinear outline overlaps itself", Envelope("floor.create", Floor(P(0, 0), P(1000, 0), P(2000, 0))), RevitCommandCodes.BoundarySelfIntersecting);
    Rejects("spike folding back on an edge", Envelope("floor.create", Floor(P(0, 0), P(2000, 0), P(2000, 1000), P(2000, 500))), RevitCommandCodes.BoundarySelfIntersecting);
    Rejects("outline touching itself at a vertex", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(2000, 2000), P(4000, 4000), P(0, 4000), P(2000, 2000))), RevitCommandCodes.BoundarySelfIntersecting);
    Rejects("sliver whose apex is within 1.2 mm of the opposite edge", Envelope("floor.create", Floor(P(0, 0), P(10000, 0), P(5000, 0.0001))), RevitCommandCodes.BoundarySelfIntersecting);
    Rejects("small triangle with area under 1.44 mm2", Envelope("floor.create", Floor(P(0, 0), P(1.4, 0), P(0.7, 1.2124))), RevitCommandCodes.BoundaryZeroArea, "args.boundary");
    Rejects("non-adjacent edges 1 mm apart", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(4000, 3000), P(2001, 3000), P(2001, 1), P(1999, 1), P(1999, 3000), P(0, 3000))), RevitCommandCodes.BoundarySelfIntersecting);
    Rejects("vertex 1 mm from a neighbouring edge", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(4000, 3000), P(3999, 1))), RevitCommandCodes.BoundarySelfIntersecting);
    Accepts("narrow notch 2 mm wide accepted", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(4000, 3000), P(2001, 3000), P(2001, 10), P(1999, 10), P(1999, 3000), P(0, 3000))));
    Accepts("floor far from the origin accepted (orientation computed relative to the first vertex)", Envelope("floor.create", Floor(P(9.9e7, 9.9e7), P(9.9e7 + 4000, 9.9e7), P(9.9e7 + 4000, 9.9e7 + 3000), P(9.9e7, 9.9e7 + 3000))));
    RejectsText("floor vertex overflowing to infinity", Envelope("floor.create", Floor(P(0, 0), P(4000, 0), P(4000, 3000))).ToJsonString().Replace("[4000,0]", "[1e400,0]"), RevitCommandCodes.LengthInvalid, "args.boundary[1][0]");

    // ---- Rooms, openings, queries ----
    Check(Accepts("room with name and number accepted", Envelope("room.create", Room()))?.Args is RoomCreateArgs { Name: "Kitchen", Number: "101" } room && Near(room.Point.X.Millimetres, 2000), "room binds point, name and number");
    var bareRoom = Room(); bareRoom.Remove("name"); bareRoom.Remove("number");
    Check(Accepts("room without name and number accepted", Envelope("room.create", bareRoom))?.Args is RoomCreateArgs { Name: null, Number: null }, "room name and number are optional");
    Check(Accepts("door accepted", Envelope("opening.place", Opening("door")))?.Args is OpeningPlaceArgs { Category: "door", HostWallUid: WallUid } door && Near(door.PositionAlong.Millimetres, 1500), "door binds host wall and position");
    foreach (var query in new[] { "query.levels", "query.wall_types", "query.floor_types" })
        Check(Accepts(query + " with {} accepted", Envelope(query, new JsonObject()))?.Args is EmptyQueryArgs, query + " binds empty arguments");
    foreach (var category in new[] { "door", "window" })
        Check(Accepts("family symbols " + category + " accepted", Envelope("query.family_symbols", new JsonObject { ["category"] = category }))?.Args is FamilySymbolQueryArgs symbols && symbols.Category == category, "family symbols binds " + category);

    // ---- Canonical form and wire line ----
    const string orderedA = """{"protocol_version":1,"operation_id":"op-9","document":{"path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt","unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b"},"command":"wall.create","args":{"level":"L1","type":"T","height_mm":3000,"curve":{"kind":"line","start":[0,0],"end":[5000,0]}}}""";
    const string orderedB = """{"args":{"curve":{"end":[5000.0,0],"start":[0,0e0],"kind":"line"},"height_mm":3.0e3,"type":"T","level":"L1"},"command":"wall.create","document":{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b","path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt"},"operation_id":"op-9","protocol_version":1}""";
    var a = RevitCommandProtocol.Parse(orderedA);
    var b = RevitCommandProtocol.Parse(orderedB);
    Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "member order and number spelling do not change the canonical request or its hash", a.CanonicalJson + " vs " + b.CanonicalJson);
    Check(a.CanonicalJson.StartsWith("{\"args\":{\"curve\":{\"end\":[5000,0]", StringComparison.Ordinal) && a.CanonicalJson.EndsWith("\"operation_id\":\"op-9\",\"protocol_version\":1}", StringComparison.Ordinal), "canonical request sorts members and writes shortest numbers", a.CanonicalJson);
    Check(a.RequestSha256.Length == 64 && a.RequestSha256 == a.RequestSha256.ToLowerInvariant(), "request hash is lowercase SHA-256 hex");
    var c = RevitCommandProtocol.Parse(orderedA.Replace("\"height_mm\":3000", "\"height_mm\":3100"));
    var negativeZero = RevitCommandProtocol.Parse(orderedA.Replace("\"height_mm\":3000", "\"height_mm\":3000,\"base_offset_mm\":-0"));
    var positiveZero = RevitCommandProtocol.Parse(orderedA.Replace("\"height_mm\":3000", "\"height_mm\":3000,\"base_offset_mm\":0"));
    Check(negativeZero.CanonicalJson == positiveZero.CanonicalJson && negativeZero.CanonicalJson.Contains("\"base_offset_mm\":0,", StringComparison.Ordinal), "canonical form writes -0 as 0", negativeZero.CanonicalJson);
    Check(positiveZero.RequestSha256 != a.RequestSha256, "an explicit default (base_offset_mm 0) changes the hash; the hash binds the text, not the meaning");
    RejectsText("lone surrogate in a level name", orderedA.Replace("\"level\":\"L1\"", "\"level\":\"L\\ud800\""), RevitCommandCodes.EnvelopeInvalid);
    RejectsText("lone surrogate in a document path", orderedA.Replace("a.rvt", "\\udc00.rvt"), RevitCommandCodes.EnvelopeInvalid);
    Check(c.RequestSha256 != a.RequestSha256, "a different height changes the request hash");
    var wire = JsonNode.Parse(RevitCommandProtocol.WireLine(a))!.AsObject();
    Check(string.Join(",", wire.Select(member => member.Key)) == "DocumentPath,Operation,UniqueId,Metres,Command", "wire line keeps the add-in Request shape and adds Command", wire.ToJsonString());
    Check(wire["DocumentPath"]!.GetValue<string>() == @"C:\Repo\.bim\native-runs\r\a.rvt" && wire["Operation"]!.GetValue<string>() == RevitCommandProtocol.WireOperation && wire["UniqueId"]!.GetValue<string>() == Uid && wire["Metres"]!.AsArray().Count == 0,
        "wire line carries exact document path, revit.command, document UniqueId and empty Metres");
    Check(wire["Command"]!.ToJsonString() == a.CanonicalJson, "wire Command is the canonical envelope");
    Check(!RevitCommandProtocol.WireLine(a).Contains('\n') && RevitCommandProtocol.WireLine(a).Length <= RevitCommandProtocol.MaxRequestChars, "wire line is one line within the add-in limit");
    var accented = RevitCommandProtocol.Parse(Envelope("query.levels", new JsonObject(), @"C:\Repo\Caf" + "\u00e9" + @" <copy>\a.rvt").ToJsonString());
    Check(JsonNode.Parse(RevitCommandProtocol.WireLine(accented))!["DocumentPath"]!.GetValue<string>() == @"C:\Repo\Caf" + "\u00e9" + @" <copy>\a.rvt", "non-ASCII and HTML-sensitive document path round-trips through the wire line");

    // ---- Response shape ----
    Check(RevitCommandResponse.Failure(RevitCommandCodes.ArgsInvalid, "args.height_mm", "expected number").ToJson() ==
        """{"status":"failed","code":"args_invalid","field":"args.height_mm","detail":"expected number","created":[],"modified":[],"deleted":[],"readback":null,"receipt":null,"warnings":[]}""", "failure response has the fixed shape");
    var receipt = new RevitCommandReceipt("op-1", "wall.create", Uid, a.RequestSha256, false);
    var success = RevitCommandResponse.Success(receipt, new[] { WallUid }, Array.Empty<string>(), Array.Empty<string>(), """{"walls":[{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3","length_mm":5000}]}""", new[] { "joined at ends" });
    var reparsed = RevitCommandResponse.Parse(success.ToJson());
    Check(reparsed.IsSuccess && reparsed.Created.SequenceEqual(new[] { WallUid }) && reparsed.Receipt == receipt && reparsed.Warnings.SequenceEqual(new[] { "joined at ends" }) && reparsed.ReadbackJson == """{"walls":[{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3","length_mm":5000}]}""",
        "success response round-trips created UniqueIds, readback, receipt and warnings");
    Check(success.ToJson().StartsWith("{\"status\":\"succeeded\",\"code\":\"ok\",\"field\":null,\"detail\":null,\"created\":[\"" + WallUid + "\"]", StringComparison.Ordinal), "success response member order", success.ToJson());
    Check(CodeOf(() => RevitCommandResponse.Parse(success.ToJson().Replace(WallUid, "not-a-uid"))) == RevitCommandCodes.ResponseInvalid, "response with a malformed UniqueId is invalid");
    Check(CodeOf(() => RevitCommandResponse.Parse(success.ToJson().Replace("\"receipt\":{", "\"receipt\":null,\"x\":{"))) == RevitCommandCodes.ResponseInvalid, "success without receipt is invalid");
    Check(CodeOf(() => RevitCommandResponse.Parse("""{"status":"failed","code":"x"}""")) == RevitCommandCodes.ResponseInvalid, "response missing members is invalid");
    Check(CodeOf(() => RevitCommandResponse.Parse("not json")) == RevitCommandCodes.ResponseInvalid, "non-JSON response is invalid");
    Check(RevitCommandResponse.Parse(RevitCommandResponse.Failure("reference_not_found", "args.level", null).ToJson()) is { IsSuccess: false, Code: "reference_not_found", Field: "args.level", Receipt: null }, "failure response round-trips");

    // ---- Copies-only guard (pure) ----
    var windows = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
    Check(RevitCopiesGuard.Check(true, @"C:\Repo\.bim\native-runs\run-1\a.rvt", windows) is null, "write under the enabled test root is allowed");
    Check(RevitCopiesGuard.Check(true, RealPath, windows) == RevitCommandCodes.DocumentNotEnabled, "write on a real project is document_not_enabled");
    Check(RevitCopiesGuard.Check(false, RealPath, windows) is null, "read-only query is allowed on any document");
    Check(RevitCopiesGuard.Check(true, @"C:\Repo\.bim\native-runs-evil\a.rvt", windows) == RevitCommandCodes.DocumentNotEnabled, "sibling directory sharing the root prefix is not enabled");
    Check(RevitCopiesGuard.Check(true, @"C:\Repo\.bim\native-runs", windows) is null && RevitCopiesGuard.Check(true, @"C:\Repo\.bim", windows) == RevitCommandCodes.DocumentNotEnabled, "the root itself is inside, its parent is not");
    Check(RevitCopiesGuard.Check(true, "c:/repo/.BIM/Native-Runs/run-1/a.rvt", windows) is null, "drive paths compare case-insensitively with either separator");
    Check(RevitCopiesGuard.Check(true, @"C:\Repo\\.bim\native-runs\\run-1\a.rvt", windows) is null, "repeated separators are ignored");
    Check(RevitCopiesGuard.Check(true, @"D:\Repo\.bim\native-runs\a.rvt", windows) == RevitCommandCodes.DocumentNotEnabled, "another drive is not enabled");
    foreach (var (label, path) in new[] { ("dot-dot escape", @"C:\Repo\.bim\native-runs\..\..\Projects\Tower.rvt"), ("dot segment", @"C:\Repo\.bim\native-runs\.\a.rvt"), ("device namespace", @"\\?\C:\Repo\.bim\native-runs\a.rvt"), ("dot device", @"\\.\pipe\x"), ("alternate data stream", @"C:\Repo\.bim\native-runs\a.rvt:hidden"), ("relative", @"Repo\.bim\native-runs\a.rvt"), ("drive-relative", @"C:Repo\a.rvt"), ("control character", "C:\\Repo\\.bim\\native-runs\\a\u0007.rvt"), ("too long", @"C:\" + new string('a', 1100)) })
        Check(RevitCopiesGuard.Check(true, path, windows) == RevitCommandCodes.DocumentPathInvalid && RevitCopiesGuard.Check(false, path, windows) == RevitCommandCodes.DocumentPathInvalid, "path " + label + " is document_path_invalid for writes and reads");
    var unix = new RevitCommandPolicy(new[] { "/home/u/repo/.bim/native-runs" });
    Check(RevitCopiesGuard.Check(true, "/home/u/repo/.bim/native-runs/r/a.rvt", unix) is null, "rooted path under the enabled root is allowed");
    Check(RevitCopiesGuard.Check(true, "/home/u/repo/.BIM/native-runs/r/a.rvt", unix) == RevitCommandCodes.DocumentNotEnabled, "'/'-rooted paths compare case-sensitively");
    Check(RevitCopiesGuard.Check(true, @"C:\home\u\repo\.bim\native-runs\a.rvt", unix) == RevitCommandCodes.DocumentNotEnabled, "drive path never matches a '/'-rooted root");
    var unc = new RevitCommandPolicy(new[] { @"\\fileserver\share\tests" });
    Check(RevitCopiesGuard.Check(true, @"\\FileServer\Share\tests\a.rvt", unc) is null && RevitCopiesGuard.Check(true, @"\\fileserver\share\prod\a.rvt", unc) == RevitCommandCodes.DocumentNotEnabled, "UNC roots match by server, share and segments");
    Check(RevitCopiesGuard.Check(true, @"\\fileserver", unc) == RevitCommandCodes.DocumentPathInvalid, "UNC path without a share is invalid");
    Check(RevitCopiesGuard.Check(true, FixturePath, RevitCommandPolicy.None) == RevitCommandCodes.DocumentNotEnabled && RevitCopiesGuard.Check(false, FixturePath, RevitCommandPolicy.None) is null, "an empty policy enables no writes and still allows reads");
    var allowlist = RevitCommandPolicy.FromAllowlist("# owner-enabled copies\r\n\r\n  C:\\Projects\\Copies\\Tower-copy.rvt  \r\n\\\\fileserver\\share\\tests\n", null);
    Check(allowlist.EnabledRoots.Count == 2, "allowlist ignores comments and blank lines and trims entries");
    Check(RevitCopiesGuard.Check(true, @"C:\Projects\Copies\Tower-copy.rvt", allowlist) is null && RevitCopiesGuard.Check(true, @"C:\Projects\Copies\Tower.rvt", allowlist) == RevitCommandCodes.DocumentNotEnabled, "an exact document entry enables only that document");
    var combined = RevitCommandPolicy.FromAllowlist(@"C:\Projects\Copies", @"C:\Repo\.bim\native-runs");
    Check(combined.EnabledRoots.SequenceEqual(new[] { @"C:\Repo\.bim\native-runs", @"C:\Projects\Copies" }), "default root comes first, then allowlist entries");
    foreach (var (label, text) in new[] { ("relative entry", "Projects/Copies"), ("dot-dot entry", @"C:\Projects\..\Real"), ("device entry", @"\\?\C:\x") })
        Check(CodeOf(() => RevitCommandPolicy.FromAllowlist(text, null)) == RevitCommandCodes.AllowlistInvalid, "allowlist " + label + " is rejected");
    Check(CodeOf(() => RevitCommandPolicy.FromAllowlist(string.Join("\n", Enumerable.Range(0, 257).Select(i => @"C:\Copies\" + i)), null)) == RevitCommandCodes.AllowlistInvalid, "allowlist over 256 entries is rejected");
    Check(CodeOf(() => RevitCommandPolicy.FromAllowlist(null, "relative")) == RevitCommandCodes.AllowlistInvalid, "a relative default root is rejected");
    foreach (var (label, text) in new[] { ("drive root", @"C:"), ("drive root with slash", "D:/"), ("filesystem root", "/"), ("UNC share root", @"\fileserver\share"), ("UNC share root with separator", @"\fileserver\share") })
        Check(CodeOf(() => RevitCommandPolicy.FromAllowlist(text, null)) == RevitCommandCodes.AllowlistInvalid, "too-broad allowlist entry " + label + " is rejected");
    Check(CodeOf(() => RevitCommandPolicy.FromAllowlist(null, "/")) == RevitCommandCodes.AllowlistInvalid, "a too-broad default root is rejected");
    Check(RevitCommandPolicy.FromAllowlist("C:\\Copies\n/srv\n\\\\fileserver\\share\\tests\n", null).EnabledRoots.Count == 3, "one directory below a drive, '/' or UNC share is accepted");
    var repository = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("repository root with global.json not found");
    var repoPolicy = RevitCommandPolicy.ForRepository(AppContext.BaseDirectory);
    Check(repoPolicy.EnabledRoots.SequenceEqual(new[] { Path.Combine(repository, ".bim", "native-runs") }), "repository default root is .bim/native-runs", string.Join(";", repoPolicy.EnabledRoots));
    Check(RevitCommandPolicy.ForRepository(Path.GetPathRoot(Path.GetTempPath())!).EnabledRoots.Count == 0 || RevitCommandPolicy.RepositoryRoot(Path.GetPathRoot(Path.GetTempPath())!) is not null, "outside a repository there is no default root");

    // ---- Catalogue ----
    var names = RevitCommandCatalogue.V1.Select(spec => spec.Name).ToArray();
    Check(names.SequenceEqual(new[] { "query.levels", "query.wall_types", "query.floor_types", "query.family_symbols", "wall.create", "opening.place", "floor.create", "room.create" }), "catalogue v1 lists the eight commands in order", string.Join(",", names));
    Check(RevitCommandCatalogue.V1.Where(spec => !spec.Writes).Select(spec => spec.Name).All(name => name.StartsWith("query.", StringComparison.Ordinal)) && RevitCommandCatalogue.V1.Count(spec => spec.Writes) == 4, "queries are read-only and the four creators write");
    Check(RevitCommandCatalogue.Find("wall.create")?.Writes == true && RevitCommandCatalogue.Find("wall.delete") is null, "catalogue lookup by name");
    Check(RevitCommandCatalogue.V1.Select(spec => spec.ToolName).Distinct().Count() == 8 && RevitCommandCatalogue.V1.All(spec => System.Text.RegularExpressions.Regex.IsMatch(spec.ToolName, @"\Arevit_[a-z_]+\z") && spec.ToolName == "revit_" + spec.Name.Replace('.', '_')), "tool names are revit_<command with . replaced by _>");
    Check(RevitCommandCatalogue.V1.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.')), "every command has a plain-English description");
    foreach (var spec in RevitCommandCatalogue.V1)
    {
        string? problem = null;
        try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
        Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
    }
    Check(Throws<InvalidOperationException>(() => { using var bad = JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false,"minimum":0}"""); RevitCommandSchema.CheckSupported(bad.RootElement); }), "a schema keyword the validator does not enforce is refused");
    Check(Throws<InvalidOperationException>(() => { using var open = JsonDocument.Parse("""{"type":"object","properties":{}}"""); RevitCommandSchema.CheckSupported(open.RootElement); }), "object schemas must be closed");
    using (var anchored = JsonDocument.Parse("""{"type":"string","pattern":"^a$"}"""))
    using (var trailing = JsonDocument.Parse("\"a\\n\""))
    using (var exact = JsonDocument.Parse("\"a\""))
        Check(RevitCommandSchema.Validate(trailing.RootElement, anchored.RootElement, "x") is not null && RevitCommandSchema.Validate(exact.RootElement, anchored.RootElement, "x") is null, "schema pattern $ means end of string: a trailing newline is refused");
    Check(RevitCommandCodes.ClientSide.Concat(RevitCommandCodes.ExecutorReserved).Distinct().Count() == RevitCommandCodes.ClientSide.Count + RevitCommandCodes.ExecutorReserved.Count
        && RevitCommandCodes.ClientSide.Concat(RevitCommandCodes.ExecutorReserved).All(code => System.Text.RegularExpressions.Regex.IsMatch(code, @"\A[a-z]+(_[a-z0-9]+)*\z")), "error codes are distinct snake_case");

    // ---- Link guard (injected probe) ----
    var probed = new List<string>();
    RevitFileSystemProbe Probe(Func<string, bool> isLink, Func<string, int?> links) => new(path => { probed.Add(path); return isLink(path); }, links);
    Check(RevitLinkGuard.Check(@"C:\Repo\.bim\native-runs\run-1\a.rvt", @"c:/repo/.bim/native-runs", Probe(_ => false, _ => 1)) is null
        && probed.SequenceEqual(new[] { @"C:\Repo\.bim\native-runs", @"C:\Repo\.bim\native-runs\run-1", @"C:\Repo\.bim\native-runs\run-1\a.rvt" }),
        "link guard probes every component from the matched root down to the document, spelled as the document", string.Join(" | ", probed));
    foreach (var linked in new[] { @"C:\Repo\.bim\native-runs", @"C:\Repo\.bim\native-runs\run-1", @"C:\Repo\.bim\native-runs\run-1\a.rvt" })
        Check(RevitLinkGuard.Check(@"C:\Repo\.bim\native-runs\run-1\a.rvt", @"C:\Repo\.bim\native-runs", Probe(path => path == linked, _ => 1)) == RevitCommandCodes.DocumentLinkRefused, "a link at " + linked + " is document_link_refused");
    Check(RevitLinkGuard.Check(@"C:\Repo\.bim\native-runs\run-1\a.rvt", @"C:\Repo\.bim\native-runs", Probe(_ => false, _ => 2)) == RevitCommandCodes.DocumentLinkRefused, "a document with two hard links is document_link_refused");
    Check(RevitLinkGuard.Check(@"C:\Repo\.bim\native-runs\run-1\a.rvt", @"C:\Repo\.bim\native-runs", Probe(_ => false, _ => null)) is null, "unknown hard-link count (no file yet, or not Windows) is allowed");
    Check(RevitLinkGuard.Check("/home/u/repo/.bim/native-runs/r/a.rvt", "/home/u/repo/.bim/native-runs", Probe(path => path == "/home/u/repo/.bim/native-runs/r", _ => null)) == RevitCommandCodes.DocumentLinkRefused, "'/'-rooted components are probed too");

    // ---- Link guard (real file system) ----
    Directory.CreateDirectory(work);
    var linkRoot = Path.Combine(work, "copies");
    var realRun = Directory.CreateDirectory(Path.Combine(linkRoot, "run-real")).FullName;
    File.WriteAllText(Path.Combine(realRun, "a.rvt"), "fixture");
    var outsideTarget = Directory.CreateDirectory(Path.Combine(work, "production")).FullName;
    File.WriteAllText(Path.Combine(outsideTarget, "Tower.rvt"), "production");
    var linkPolicy = new RevitCommandPolicy(new[] { linkRoot });
    string WireOk(string line) => SuccessFor(line, Array.Empty<string>(), null);
    var linkForwarded = 0;
    string? LinkTransport(string pipe, string line) { linkForwarded++; return WireOk(line); }
    string RunWall(string path, RevitFileSystemProbe? probe = null) => RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("wall.create", Wall(Line(0, 0, 5000, 0)), path).ToJsonString(), linkPolicy, LinkTransport, probe).Line;
    Check(JsonNode.Parse(RunWall(Path.Combine(realRun, "a.rvt"), RevitFileSystemProbe.Real))!["status"]!.GetValue<string>() == "succeeded" && linkForwarded == 1, "a plain directory and file under the root pass the real link guard", RunWall(Path.Combine(realRun, "a.rvt"), RevitFileSystemProbe.Real));
    linkForwarded = 0;
    Check(RunWall(Path.Combine(realRun, "a.rvt"), new RevitFileSystemProbe(_ => false, _ => 2)).Contains("\"document_link_refused\"", StringComparison.Ordinal) && linkForwarded == 0, "an injected hard-link count of 2 refuses the write before the pipe");
    Check(RunWall(Path.Combine(realRun, "a.rvt"), new RevitFileSystemProbe(path => path.EndsWith("run-real", StringComparison.Ordinal), _ => null)).Contains("\"document_link_refused\"", StringComparison.Ordinal) && linkForwarded == 0, "an injected directory link refuses the write before the pipe");
    var readThroughLink = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("query.levels", new JsonObject(), Path.Combine(realRun, "a.rvt")).ToJsonString(), linkPolicy, LinkTransport, new RevitFileSystemProbe(_ => true, _ => 9));
    Check(!readThroughLink.IsError && linkForwarded == 1, "read-only queries are not link-checked", readThroughLink.Line);
    linkForwarded = 0;
    bool TryLink(Action create, string what)
    {
        try { create(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP " + what + ": " + error.GetType().Name + " (symlink privilege)"); return false; }
            Check(false, what + " could be created", error.Message);
            return false;
        }
    }
    var linkedDirectory = Path.Combine(linkRoot, "run-link");
    if (TryLink(() => Directory.CreateSymbolicLink(linkedDirectory, outsideTarget), "directory symlink"))
        Check(FailureCode(RunWall(Path.Combine(linkedDirectory, "Tower.rvt"), RevitFileSystemProbe.Real)) == RevitCommandCodes.DocumentLinkRefused && linkForwarded == 0, "a symlinked directory under the root pointing at production is refused (real file system)");
    var linkedFile = Path.Combine(realRun, "tower-link.rvt");
    if (TryLink(() => File.CreateSymbolicLink(linkedFile, Path.Combine(outsideTarget, "Tower.rvt")), "file symlink"))
        Check(FailureCode(RunWall(linkedFile, RevitFileSystemProbe.Real)) == RevitCommandCodes.DocumentLinkRefused && linkForwarded == 0, "a symlinked document under the root is refused (real file system)");
    var dangling = Path.Combine(realRun, "dangling.rvt");
    if (TryLink(() => File.CreateSymbolicLink(dangling, Path.Combine(work, "nowhere.rvt")), "dangling symlink"))
        Check(FailureCode(RunWall(dangling, RevitFileSystemProbe.Real)) == RevitCommandCodes.DocumentLinkRefused && linkForwarded == 0, "a dangling symlink is refused (real file system)");
    if (OperatingSystem.IsWindows())
    {
        var hardLink = Path.Combine(realRun, "hard.rvt");
        if (NativeTestLinks.CreateHardLink(hardLink, Path.Combine(outsideTarget, "Tower.rvt")))
            Check(RevitFileSystemProbe.Real.HardLinkCount(hardLink) == 2 && FailureCode(RunWall(hardLink, RevitFileSystemProbe.Real)) == RevitCommandCodes.DocumentLinkRefused && linkForwarded == 0, "a hard-linked document is refused on Windows (GetFileInformationByHandle)");
        else Check(false, "hard link could be created");
        Check(RevitFileSystemProbe.Real.HardLinkCount(Path.Combine(realRun, "a.rvt")) == 1 && RevitFileSystemProbe.Real.HardLinkCount(Path.Combine(realRun, "missing.rvt")) is null, "Windows hard-link count is 1 for a plain file and unknown for a missing one");
    }
    else Check(RevitFileSystemProbe.Real.HardLinkCount(Path.Combine(realRun, "a.rvt")) is null, "hard-link counting is Windows-only");

    // ---- Owner policy file ----
    string FakeRepo(string name, string? enabledRoots)
    {
        var repo = Directory.CreateDirectory(Path.Combine(work, name)).FullName;
        File.WriteAllText(Path.Combine(repo, "global.json"), "{}");
        Directory.CreateDirectory(Path.Combine(repo, ".bim"));
        if (enabledRoots is not null) File.WriteAllText(Path.Combine(repo, ".bim", "revit-enabled-roots.txt"), enabledRoots);
        return repo;
    }
    var ownerRepo = FakeRepo("owner-repo", "# owner edits this file\n" + linkRoot + "\n");
    Check(RevitCommandPolicy.ForRepository(ownerRepo).EnabledRoots.SequenceEqual(new[] { Path.Combine(ownerRepo, ".bim", "native-runs"), linkRoot }), "owner file .bim/revit-enabled-roots.txt adds enabled roots after the default", string.Join(";", RevitCommandPolicy.ForRepository(ownerRepo).EnabledRoots));
    Check(RevitCommandPolicy.ForRepository(FakeRepo("plain-repo", null)).EnabledRoots.Count == 1, "without the owner file only .bim/native-runs is enabled");
    Check(CodeOf(() => RevitCommandPolicy.ForRepository(FakeRepo("bad-repo", "relative/copies\n"))) == RevitCommandCodes.AllowlistInvalid, "a malformed owner file is allowlist_invalid");
    Check(CodeOf(() => RevitCommandPolicy.ForRepository(FakeRepo("huge-repo", new string('#', 70000)))) == RevitCommandCodes.AllowlistInvalid, "an oversized owner file is allowlist_invalid");
    var linkedPolicyRepo = FakeRepo("linked-repo", null);
    var policyTarget = Path.Combine(work, "elsewhere-roots.txt"); File.WriteAllText(policyTarget, linkRoot + "\n");
    if (TryLink(() => File.CreateSymbolicLink(Path.Combine(linkedPolicyRepo, ".bim", "revit-enabled-roots.txt"), policyTarget), "policy file symlink"))
        Check(CodeOf(() => RevitCommandPolicy.ForRepository(linkedPolicyRepo)) == RevitCommandCodes.AllowlistInvalid, "a symlinked owner file is allowlist_invalid");

    // ---- Generated MCP tools (fake transport) ----
    var forwarded = new List<(string Pipe, string Line)>();
    Func<string, string> respond = line => SuccessFor(line, Array.Empty<string>(), """{"levels":[]}""");
    Exception? transportFault = null;
    string? Fake(string pipe, string line)
    {
        forwarded.Add((pipe, line));
        if (transportFault is not null) throw transportFault;
        return respond(line);
    }
    var server = new McpServer(windows, Fake, new RevitFileSystemProbe(_ => false, _ => null));
    JsonObject Call(string line) => JsonNode.Parse(server.Handle(line) ?? throw new InvalidOperationException("no response"))!.AsObject();
    JsonObject ToolCall(string name, JsonObject arguments) => Call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } }.ToJsonString());
    JsonObject RevitArguments(JsonNode commandArgs, string path = FixturePath, string pipe = "bim-agentic-os-2025-4242") => new() { ["pipe_name"] = pipe, ["operation_id"] = "op-1", ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid }, ["args"] = commandArgs };
    string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
    bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
    int ErrorCode(JsonObject response) => response["error"]!["code"]!.GetValue<int>();

    var listed = Call("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")["result"]!["tools"]!.AsArray();
    var listedNames = listed.Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
    Check(listedNames.SequenceEqual(new[] { "diff", "validate", "room", "plan" }.Concat(RevitCommandCatalogue.V1.Select(spec => spec.ToolName))), "tools/list = core tools then exactly the catalogue tools", string.Join(",", listedNames));
    foreach (var spec in RevitCommandCatalogue.V1)
    {
        var tool = listed.Single(t => t!["name"]!.GetValue<string>() == spec.ToolName)!;
        var schema = tool["inputSchema"]!;
        var document = schema["properties"]!["document"]!["properties"]!;
        Check(schema["properties"]!["args"]!.ToJsonString() == JsonNode.Parse(spec.ArgsSchemaJson)!.ToJsonString()
            && string.Join(",", schema["required"]!.AsArray().Select(x => x!.GetValue<string>())) == "pipe_name,operation_id,document,args"
            && schema["additionalProperties"]!.GetValue<bool>() == false
            && tool["description"]!.GetValue<string>().StartsWith(spec.Description, StringComparison.Ordinal),
            spec.ToolName + " inputSchema embeds the catalogue args schema and requires pipe, operation, document and args");
        Check(document["path"]!["maxLength"]!.GetValue<int>() == RevitCommandProtocol.MaxPathChars && document["path"]!["minLength"]!.GetValue<int>() == 1
            && document["unique_id"]!["minLength"]!.GetValue<int>() == 45 && document["unique_id"]!["maxLength"]!.GetValue<int>() == 45
            && System.Text.RegularExpressions.Regex.IsMatch(Uid, document["unique_id"]!["pattern"]!.GetValue<string>()) && !System.Text.RegularExpressions.Regex.IsMatch("abc", document["unique_id"]!["pattern"]!.GetValue<string>()),
            spec.ToolName + " document schema bounds path length and states the UniqueId pattern");
    }

    var queryCall = ToolCall("revit_query_levels", RevitArguments(new JsonObject(), RealPath));
    var expectedQueryWire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope("query.levels", new JsonObject(), RealPath).ToJsonString()));
    Check(!IsError(queryCall) && Text(queryCall) == respond(expectedQueryWire) && forwarded.Count == 1 && forwarded[0] == ("bim-agentic-os-2025-4242", expectedQueryWire), "read-only tool on a real project forwards the exact wire line and returns the add-in line verbatim", forwarded.Count > 0 ? forwarded[^1].Line : "nothing forwarded");
    var wallCall = ToolCall("revit_wall_create", RevitArguments(Wall(Arc3(0, 0, 2000, 0, 1000, 1000))));
    Check(!IsError(wallCall) && forwarded.Count == 2 && forwarded[1].Line == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, 1000))).ToJsonString())), "curved wall tool on an enabled copy is forwarded", Text(wallCall));
    var refusedWrite = ToolCall("revit_wall_create", RevitArguments(Wall(Line(0, 0, 5000, 0)), RealPath));
    Check(IsError(refusedWrite) && FailureCode(Text(refusedWrite)) == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 2, "write tool on a real project is document_not_enabled and not forwarded", Text(refusedWrite));
    var linkServer = new McpServer(windows, Fake, new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null));
    var linkCall = JsonNode.Parse(linkServer.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "revit_wall_create", ["arguments"] = RevitArguments(Wall(Line(0, 0, 5000, 0))) } }.ToJsonString())!)!;
    Check(FailureCode(linkCall["result"]!["content"]![0]!["text"]!.GetValue<string>()) == RevitCommandCodes.DocumentLinkRefused && forwarded.Count == 2, "MCP write through an injected link is document_link_refused and not forwarded");
    var badHeight = Wall(Line(0, 0, 5000, 0)); badHeight["height_mm"] = -3000;
    var invalidCall = ToolCall("revit_wall_create", RevitArguments(badHeight));
    Check(IsError(invalidCall) && Text(invalidCall) == RevitCommandResponse.Failure(RevitCommandCodes.LengthInvalid, "args.height_mm", "negative length").ToJson() && forwarded.Count == 2, "invalid tool arguments return the typed failure and are not forwarded", Text(invalidCall));
    var collinearCall = ToolCall("revit_wall_create", RevitArguments(Wall(Arc3(0, 0, 2000, 0, 1000, 0))));
    Check(IsError(collinearCall) && FailureCode(Text(collinearCall)) == RevitCommandCodes.ArcCollinear && forwarded.Count == 2, "collinear arc tool call is refused before the pipe");
    var schemaCall = ToolCall("revit_floor_create", RevitArguments(new JsonObject { ["level"] = "Level 1" }));
    Check(IsError(schemaCall) && FailureCode(Text(schemaCall)) == RevitCommandCodes.ArgsInvalid && JsonNode.Parse(Text(schemaCall))!["field"]!.GetValue<string>() == "args.type", "schema failure names the missing field", Text(schemaCall));
    var surrogateCall = Call("{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"revit_query_levels\",\"arguments\":{\"pipe_name\":\"bim-agentic-os-2025-4242\",\"operation_id\":\"op-1\",\"document\":{\"path\":\"C:\\\\Repo\\\\\\ud800.rvt\",\"unique_id\":\"" + Uid + "\"},\"args\":{}}}}");
    Check(IsError(surrogateCall) && FailureCode(Text(surrogateCall)) == RevitCommandCodes.EnvelopeInvalid && forwarded.Count == 2, "a lone surrogate in a tool argument is envelope_invalid in the response shape", surrogateCall.ToJsonString());
    foreach (var badPipe in new[] { "bim-agentic-os-2025-0", "bim-agentic-os-2025", "arbitrary", "bim-agentic-os-2025-12\n" })
    {
        var pipeCall = ToolCall("revit_query_levels", RevitArguments(new JsonObject(), FixturePath, badPipe));
        Check(IsError(pipeCall) && FailureCode(Text(pipeCall)) == RevitCommandCodes.ExplicitProcessPipeRequired && forwarded.Count == 2, "pipe " + JsonSerializer.Serialize(badPipe) + " is refused before I/O");
    }
    var noPipe = RevitArguments(new JsonObject()); noPipe.Remove("pipe_name");
    Check(ErrorCode(ToolCall("revit_query_levels", noPipe)) == -32602, "missing pipe_name is -32602");
    var extraArgument = RevitArguments(new JsonObject()); extraArgument["allowlist_path"] = "/tmp/allow.txt";
    Check(ErrorCode(ToolCall("revit_query_levels", extraArgument)) == -32602, "a tool call cannot supply an allowlist");
    var arrayArgs = RevitArguments(new JsonObject()); arrayArgs["args"] = new JsonArray();
    Check(ErrorCode(ToolCall("revit_query_levels", arrayArgs)) == -32602, "args must be an object (-32602)");
    var textDocument = RevitArguments(new JsonObject()); textDocument["document"] = FixturePath;
    Check(ErrorCode(ToolCall("revit_query_levels", textDocument)) == -32602, "document must be an object (-32602)");
    var executorFailureLine = RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args.level", "no level named Level 9").ToJson();
    respond = _ => executorFailureLine;
    var executorFailure = ToolCall("revit_query_levels", RevitArguments(new JsonObject()));
    Check(IsError(executorFailure) && Text(executorFailure) == executorFailureLine, "an add-in failure line is returned verbatim with isError");
    foreach (var (label, tamper) in new (string, Func<RevitCommandReceipt, RevitCommandReceipt>)[]
    {
        ("operation_id", r => r with { OperationId = "op-2" }),
        ("command", r => r with { Command = "query.floor_types" }),
        ("document unique_id", r => r with { DocumentUniqueId = WallUid }),
        ("request_sha256", r => r with { RequestSha256 = new string('0', 64) }),
    })
    {
        respond = line => SuccessFor(line, Array.Empty<string>(), null, tamper);
        var mismatch = ToolCall("revit_query_levels", RevitArguments(new JsonObject()));
        Check(IsError(mismatch) && FailureCode(Text(mismatch)) == RevitCommandCodes.ResponseInvalid && JsonNode.Parse(Text(mismatch))!["field"]?.GetValue<string>() == "receipt", "a success whose receipt " + label + " does not match the request is response_invalid", Text(mismatch));
    }
    respond = line => SuccessFor(line, Array.Empty<string>(), null).Replace("\"replayed\":false", "\"replayed\":\"no\"", StringComparison.Ordinal);
    Check(FailureCode(Text(ToolCall("revit_query_levels", RevitArguments(new JsonObject())))) == RevitCommandCodes.ResponseInvalid, "a malformed success response is response_invalid");
    respond = _ => "not json";
    var nonJson = ToolCall("revit_query_levels", RevitArguments(new JsonObject()));
    Check(IsError(nonJson) && FailureCode(Text(nonJson)) == RevitCommandCodes.ResponseInvalid, "a non-JSON add-in line is response_invalid");
    respond = line => SuccessFor(line, Array.Empty<string>(), null);
    transportFault = new TimeoutException();
    var timeout = ToolCall("revit_query_levels", RevitArguments(new JsonObject()));
    Check(IsError(timeout) && FailureCode(Text(timeout)) == RevitCommandCodes.PipeConnectTimeout, "connect timeout is pipe_connect_timeout");
    transportFault = new IOException("broken");
    Check(FailureCode(Text(ToolCall("revit_query_levels", RevitArguments(new JsonObject())))) == RevitCommandCodes.PipeIoError, "pipe I/O failure is pipe_io_error");
    transportFault = null;
    var silent = new McpServer(windows, (_, _) => null);
    var noResponse = JsonNode.Parse(silent.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 8, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "revit_query_levels", ["arguments"] = RevitArguments(new JsonObject()) } }.ToJsonString())!)!;
    Check(FailureCode(noResponse["result"]!["content"]![0]!["text"]!.GetValue<string>()) == RevitCommandCodes.NoResponse, "pipe closed without a line is no_response");
    var surrogateRun = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("query.levels", new JsonObject()).ToJsonString().Replace("\"op-1\"", "\"op\\udc00\"", StringComparison.Ordinal), windows, Fake);
    Check(surrogateRun.IsError && FailureCode(surrogateRun.Line) == RevitCommandCodes.EnvelopeInvalid, "a lone surrogate in the envelope is envelope_invalid", surrogateRun.Line);

    // ---- CLI end to end against an in-process fake add-in pipe server ----
    string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
    var cliDll = Path.Combine(repository, "interfaces", "cli", "bin", "Release", "net8.0", "Bim.Cli.dll");
    if (!File.Exists(cliDll)) throw new InvalidOperationException("CLI binary missing: " + cliDll + " (build interfaces/cli --configuration Release first, as scripts/verify.ps1 does)");
    (int Exit, string Out) CliAt(string dll, params string[] arguments)
    {
        var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); return (-1, "<timed out>"); }
        process.WaitForExit();
        var text = stdout.Result;
        if (text.EndsWith('\n')) text = text[..^1];
        if (text.EndsWith('\r')) text = text[..^1];
        if (stderr.Result.Length > 0) Console.WriteLine("cli stderr: " + stderr.Result.ReplaceLineEndings(" "));
        return (process.ExitCode, text);
    }
    (int Exit, string Out) Cli(params string[] arguments) => CliAt(cliDll, arguments);
    string Write(string name, string content) { var path = Path.Combine(work, name); File.WriteAllText(path, content); return path; }
    var pipeName = "bim-agentic-os-2025-" + Environment.ProcessId;
    // On Unix the pipe is a socket at $TMPDIR/CoreFxPipe_<name>; verify.ps1 points TMPDIR at a deep evidence folder
    // that can exceed the ~104-byte socket path limit, so this process and its CLI children use /tmp instead.
    if (!OperatingSystem.IsWindows() && Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName).Length > 100)
    {
        Environment.SetEnvironmentVariable("TMPDIR", "/tmp/");
        Console.WriteLine("NOTE TMPDIR set to /tmp/ for the pipe socket path limit");
    }
    var repoFixture = Path.Combine(repository, ".bim", "native-runs", "c02-contract", "BIM-Agentic-Test.rvt");
    var outside = Path.Combine(work, "Real-Project.rvt");
    using var addIn = new FakeAddIn(pipeName);
    addIn.Respond = line => SuccessFor(line, new[] { WallUid }, """{"length_mm":5000}""");

    var (badPipeExit, badPipeOut) = Cli("revit-command", Path.Combine(work, "missing.json"), "arbitrary");
    Check(badPipeExit == 1 && badPipeOut == RevitCommandService.RejectPipe("arbitrary")!.Line, "CLI refuses a non-process pipe before reading the envelope", badPipeExit + " " + badPipeOut);
    var (noPipeExit, noPipeOut) = Cli("revit-command", Path.Combine(work, "missing.json"));
    Check(noPipeExit == 1 && FailureCode(noPipeOut) == RevitCommandCodes.ExplicitProcessPipeRequired, "CLI without a pipe argument is explicit_process_pipe_required", noPipeOut);
    var (missingExit, missingOut) = Cli("revit-command", Path.Combine(work, "missing.json"), pipeName);
    Check(missingExit == 1 && FailureCode(missingOut) == RevitCommandCodes.EnvelopeInvalid && JsonNode.Parse(missingOut)!["detail"]?.GetValue<string>() == "input_not_found" && JsonNode.Parse(missingOut)!["created"] is JsonArray, "CLI with an unreadable envelope file is envelope_invalid in the response shape", missingOut);
    var readEnvelope = Write("query.json", Envelope("query.levels", new JsonObject(), outside).ToJsonString());
    var (queryExit, queryOut) = Cli("revit-command", readEnvelope, pipeName);
    var queryWire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(File.ReadAllText(readEnvelope)));
    Check(queryExit == 0 && queryOut == addIn.Respond(queryWire) && addIn.Received.Count == 1 && addIn.Received[0] == queryWire, "CLI forwards a read-only query on any document over the real pipe and prints the add-in line", queryExit + " " + queryOut + " received " + addIn.Received.Count);
    var (fourthExit, fourthOut) = Cli("revit-command", readEnvelope, pipeName, Write("allow.txt", work + "\n"));
    Check(fourthExit == 1 && FailureCode(fourthOut) == RevitCommandCodes.ExplicitProcessPipeRequired && addIn.Received.Count == 1, "CLI no longer accepts a caller-supplied allowlist argument", fourthOut);
    var outsideWall = Write("outside-wall.json", Envelope("wall.create", Wall(Line(0, 0, 5000, 0)), outside).ToJsonString());
    var (refusedExit, refusedOut) = Cli("revit-command", outsideWall, pipeName);
    Check(refusedExit == 1 && FailureCode(refusedOut) == RevitCommandCodes.DocumentNotEnabled && addIn.Received.Count == 1, "CLI write outside the test root is document_not_enabled and never reaches the pipe", refusedOut);
    var fixtureWall = Write("fixture-wall.json", Envelope("wall.create", Wall(ArcC(0, 0, 3000, 0, 120)), repoFixture).ToJsonString());
    var (wallExit, wallOut) = Cli("revit-command", fixtureWall, pipeName);
    Check(wallExit == 0 && wallOut == addIn.Respond(addIn.Received[^1]) && addIn.Received.Count == 2, "CLI write under the repository .bim/native-runs is forwarded", wallExit + " " + wallOut);
    // A copy of the CLI inside a fake repository reads that repository's owner file .bim/revit-enabled-roots.txt.
    string CopyCli(string repo)
    {
        var target = Directory.CreateDirectory(Path.Combine(repo, "cli")).FullName;
        var source = Path.GetDirectoryName(cliDll)!;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        return Path.Combine(target, "Bim.Cli.dll");
    }
    var ownerCli = CopyCli(FakeRepo("cli-owner-repo", "# copies enabled for this test\n" + work + "\n"));
    var (allowedExit, allowedOut) = CliAt(ownerCli, "revit-command", outsideWall, pipeName);
    Check(allowedExit == 0 && addIn.Received.Count == 3 && allowedOut == addIn.Respond(addIn.Received[^1]), "CLI write enabled by the owner file is forwarded", allowedOut);
    var badOwnerCli = CopyCli(FakeRepo("cli-bad-repo", "C:\\\n"));
    var (badAllowExit, badAllowOut) = CliAt(badOwnerCli, "revit-command", outsideWall, pipeName);
    Check(badAllowExit == 1 && FailureCode(badAllowOut) == RevitCommandCodes.AllowlistInvalid && JsonNode.Parse(badAllowOut)!["created"] is JsonArray && addIn.Received.Count == 3, "CLI with a too-broad owner file entry fails closed with the response shape", badAllowOut);
    var invalidEnvelope = Envelope("wall.create", Wall(Arc3(0, 0, 2000, 0, 1000, 0)), repoFixture);
    var (invalidExit, invalidOut) = Cli("revit-command", Write("invalid.json", invalidEnvelope.ToJsonString()), pipeName);
    Check(invalidExit == 1 && FailureCode(invalidOut) == RevitCommandCodes.ArcCollinear && addIn.Received.Count == 3, "CLI refuses a collinear arc before the pipe", invalidOut);
    var repoServer = new McpServer();
    JsonObject RepoCall(string name, JsonObject arguments) => JsonNode.Parse(repoServer.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 9, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } }.ToJsonString())!)!.AsObject();
    var mcpInvalid = RepoCall("revit_wall_create", RevitArguments(Wall(Arc3(0, 0, 2000, 0, 1000, 0)), repoFixture, pipeName));
    Check(Text(mcpInvalid) == invalidOut, "MCP failure text is byte-equal to CLI stdout", Text(mcpInvalid) + " vs " + invalidOut);
    var mcpWall = RepoCall("revit_wall_create", RevitArguments(Wall(ArcC(0, 0, 3000, 0, 120)), repoFixture, pipeName));
    Check(!IsError(mcpWall) && Text(mcpWall) == wallOut && addIn.Received.Count == 4 && addIn.Received[3] == addIn.Received[1], "MCP tool over the real pipe is byte-equal to the CLI and sends the identical wire line", Text(mcpWall));
    var mcpRefused = RepoCall("revit_wall_create", RevitArguments(Wall(Line(0, 0, 5000, 0)), outside, pipeName));
    Check(Text(mcpRefused) == refusedOut && addIn.Received.Count == 4, "MCP copies-only refusal is byte-equal to the CLI");
    var conflictLine = RevitCommandResponse.Failure(RevitCommandCodes.OperationIdConflict, "operation_id", null).ToJson();
    addIn.Respond = _ => conflictLine;
    var (conflictExit, conflictOut) = Cli("revit-command", fixtureWall, pipeName);
    Check(conflictExit == 1 && conflictOut == conflictLine, "CLI exits 1 and prints an add-in failure verbatim", conflictOut);
    addIn.Respond = line => SuccessFor(line, Array.Empty<string>(), null, r => r with { OperationId = "op-other" });
    var (tamperExit, tamperOut) = Cli("revit-command", fixtureWall, pipeName);
    Check(tamperExit == 1 && FailureCode(tamperOut) == RevitCommandCodes.ResponseInvalid, "CLI refuses a success whose receipt does not match the request", tamperOut);
    var (absentExit, absentOut) = Cli("revit-command", readEnvelope, "bim-agentic-os-2025-999999999");
    Check(absentExit == 1 && FailureCode(absentOut) == RevitCommandCodes.PipeConnectTimeout, "CLI with no add-in listening is pipe_connect_timeout", absentOut);
}
catch (Exception error)
{
    Console.WriteLine("::error::revit-commands crashed: " + error);
    failed++;
}
finally
{
    if (Directory.Exists(work)) Directory.Delete(work, true);
}
Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, revit_commands_contract = failed == 0, native_verified = false }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;

static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
}

static string FailureCode(string text) => JsonNode.Parse(text)!["code"]!.GetValue<string>();

// What the C03 executor will do: hash the received Command text exactly as sent and echo the request identity.
static string SuccessFor(string wireLine, string[] created, string? readbackJson, Func<RevitCommandReceipt, RevitCommandReceipt>? tamper = null)
{
    using var wire = JsonDocument.Parse(wireLine);
    var commandText = wire.RootElement.GetProperty("Command").GetRawText();
    using var command = JsonDocument.Parse(commandText);
    var receipt = new RevitCommandReceipt(command.RootElement.GetProperty("operation_id").GetString()!, command.RootElement.GetProperty("command").GetString()!,
        command.RootElement.GetProperty("document").GetProperty("unique_id").GetString()!,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(commandText))).ToLowerInvariant(), false);
    if (tamper is not null) receipt = tamper(receipt);
    return RevitCommandResponse.Success(receipt, created, Array.Empty<string>(), Array.Empty<string>(), readbackJson, Array.Empty<string>()).ToJson();
}

static class NativeTestLinks
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    static extern bool CreateHardLinkNative(string fileName, string existingFileName, IntPtr securityAttributes);

    public static bool CreateHardLink(string fileName, string existingFileName) => OperatingSystem.IsWindows() && CreateHardLinkNative(fileName, existingFileName, IntPtr.Zero);
}

// One same-user pipe server standing in for the Revit add-in: it records each request line and answers Respond(line),
// keeping the connection open until the client closes it, like the add-in listener.
sealed class FakeAddIn : IDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    readonly object gate = new();
    readonly List<string> received = new();

    public FakeAddIn(string name)
    {
        Name = name;
        loop = Task.Run(Serve);
    }

    public string Name { get; }
    public Func<string, string> Respond { get; set; } = _ => "";
    public IReadOnlyList<string> Received { get { lock (gate) return received.ToArray(); } }

    async Task Serve()
    {
        var utf8 = new UTF8Encoding(false);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(server, utf8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, utf8, 4096, leaveOpen: true) { AutoFlush = true };
                while (await reader.ReadLineAsync(stop.Token) is { } line)
                {
                    lock (gate) received.Add(line);
                    await writer.WriteLineAsync(Respond(line));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loop.Wait(5000); } catch (AggregateException) { }
        stop.Dispose();
    }
}
