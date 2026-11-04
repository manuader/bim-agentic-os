namespace Bim.Core;

// P5-04 fill step (contract in IntentSkillDefaults.cs).
public static partial class IntentSkillDefaults
{
    static readonly string[] NumberKeys = { ClearHeight, DoorHeight, ExteriorThickness, InteriorThickness, LevelHeight, WindowHead, WindowSill };
    static readonly string[] TextKeys = { ExteriorType, InteriorType };

    static double Round(double value)
    {
        var rounded = Math.Round(value, 9, MidpointRounding.ToEven);
        return rounded == 0 ? 0.0 : rounded;
    }

    static string Quote(string text) => "\"" + text + "\"";

    static IntentDefaultsResult FillCore(IntentDraft draft, SkillResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(draft.Levels);
        ArgumentNullException.ThrowIfNull(draft.WallTypes);
        ArgumentNullException.ThrowIfNull(draft.Rooms);
        ArgumentNullException.ThrowIfNull(draft.Openings);
        if (draft.Levels.Any(item => item is null) || draft.WallTypes.Any(item => item is null) || draft.Openings.Any(item => item is null))
            throw new ArgumentException("null_intent_item");

        var issues = new List<IntentIssue>();
        if (!resolution.Succeeded)
        {
            var codes = string.Join(", ", resolution.Issues.Select(issue => issue.Code + " " + issue.Subject).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal));
            return Refused(new List<IntentIssue> { new(IntentDefaultCodes.ResolutionFailed, "", "skill resolution has issues: " + codes) });
        }

        // ---- Validate every present recognised default ----
        var numbers = new Dictionary<string, (double Value, SkillTrace Source)>(StringComparer.Ordinal);
        var texts = new Dictionary<string, (string Value, SkillTrace Source)>(StringComparer.Ordinal);
        foreach (var key in NumberKeys)
        {
            if (resolution.Setting(key) is not { } setting) continue;
            if (setting.Value.Kind != SkillValueKind.Number) { issues.Add(new(IntentDefaultCodes.DefaultTypeInvalid, key, "a number is required, got " + setting.Value + " from " + setting.Source)); continue; }
            var value = setting.Value.Number;
            var valid = key == WindowSill ? double.IsFinite(value) && value >= 0 : double.IsFinite(value) && value > 0;
            if (!valid) { issues.Add(new(IntentDefaultCodes.DefaultValueInvalid, key, (key == WindowSill ? "must be finite and non-negative" : "must be finite and positive") + ", got " + setting.Value + " from " + setting.Source)); continue; }
            numbers[key] = (value, setting.Source);
        }
        foreach (var key in TextKeys)
        {
            if (resolution.Setting(key) is not { } setting) continue;
            if (setting.Value.Kind != SkillValueKind.Text) { issues.Add(new(IntentDefaultCodes.DefaultTypeInvalid, key, "a text is required, got " + setting.Value + " from " + setting.Source)); continue; }
            if (!BriefParser.IsText(setting.Value.Text)) { issues.Add(new(IntentDefaultCodes.DefaultValueInvalid, key, "must be non-blank text of at most 2048 UTF-16 code units, from " + setting.Source)); continue; }
            texts[key] = (setting.Value.Text!, setting.Source);
        }
        void Order(string low, string high, bool strict, string lowKeyIssue)
        {
            if (!numbers.TryGetValue(low, out var a) || !numbers.TryGetValue(high, out var b)) return;
            if (strict ? a.Value < b.Value : a.Value <= b.Value) return;
            issues.Add(new(IntentDefaultCodes.DefaultInconsistent, lowKeyIssue,
                low + " " + Number(a.Value) + " (" + a.Source + ") must be " + (strict ? "below " : "at most ") + high + " " + Number(b.Value) + " (" + b.Source + ")"));
        }
        Order(WindowSill, WindowHead, true, WindowSill);
        Order(ClearHeight, LevelHeight, false, ClearHeight);
        Order(WindowHead, ClearHeight, false, WindowHead);
        if (issues.Count > 0) return Refused(issues);

        // ---- Fill ----
        var sources = new List<IntentFieldSource>();
        void Explicit(string field, string value) => sources.Add(new(field, value, IntentFieldSource.ExplicitOrigin, IntentFieldSource.ExplicitRule, Array.Empty<SkillTrace>()));
        void Missing(string field, string key, string rule) =>
            issues.Add(new(IntentDefaultCodes.DefaultMissing, field, key + " is not set by the resolved skills (needed by " + rule + ")"));

        var levels = new List<IntentLevel>();
        foreach (var level in draft.Levels)
        {
            var field = "levels[" + level.Key + "].elevation_m";
            if (level.ElevationMetres is { } elevation) { Explicit(field, Number(elevation)); levels.Add(new(level.Key, level.Name, elevation)); continue; }
            if ((level.Storey ?? 0) == 0)
            {
                sources.Add(new(field, "0", IntentFieldSource.RuleOrigin, "level-elevation-ground", Array.Empty<SkillTrace>()));
                levels.Add(new(level.Key, level.Name, 0));
                continue;
            }
            if (!numbers.TryGetValue(LevelHeight, out var height)) { Missing(field, LevelHeight, "level-elevation-from-storey"); continue; }
            var value = Round((level.Storey ?? 0) * height.Value);
            sources.Add(new(field, Number(value), IntentFieldSource.DerivedOrigin, "level-elevation-from-storey", new[] { height.Source }));
            levels.Add(new(level.Key, level.Name, value));
        }

        var types = new List<IntentWallType>();
        foreach (var type in draft.WallTypes)
        {
            var prefix = "wall_types[" + type.Key + "].";
            var ok = true;
            string name = "";
            if (type.Name is not null) { Explicit(prefix + "name", Quote(type.Name)); name = type.Name; }
            else if (texts.TryGetValue(TypeKey(type.Role), out var text))
            {
                sources.Add(new(prefix + "name", Quote(text.Value), IntentFieldSource.DefaultOrigin, "wall-type-name-from-role", new[] { text.Source }));
                name = text.Value;
            }
            else { Missing(prefix + "name", TypeKey(type.Role), "wall-type-name-from-role"); ok = false; }

            double thickness = 0;
            if (type.ThicknessMetres is { } explicitThickness) { Explicit(prefix + "thickness_m", Number(explicitThickness)); thickness = explicitThickness; }
            else if (numbers.TryGetValue(ThicknessKey(type.Role), out var fill))
            {
                sources.Add(new(prefix + "thickness_m", Number(fill.Value), IntentFieldSource.DefaultOrigin, "wall-thickness-from-role", new[] { fill.Source }));
                thickness = fill.Value;
            }
            else { Missing(prefix + "thickness_m", ThicknessKey(type.Role), "wall-thickness-from-role"); ok = false; }

            double wallHeight = 0;
            if (type.HeightMetres is { } explicitHeight) { Explicit(prefix + "height_m", Number(explicitHeight)); wallHeight = explicitHeight; }
            else if (numbers.TryGetValue(LevelHeight, out var fill))
            {
                sources.Add(new(prefix + "height_m", Number(fill.Value), IntentFieldSource.DefaultOrigin, "wall-height-from-level", new[] { fill.Source }));
                wallHeight = fill.Value;
            }
            else { Missing(prefix + "height_m", LevelHeight, "wall-height-from-level"); ok = false; }
            if (ok) types.Add(new(type.Key, name, thickness, wallHeight));
        }

        var openings = new List<IntentOpening>();
        foreach (var opening in draft.Openings)
        {
            var prefix = "openings[" + opening.Key + "].";
            double? sill = null;
            SkillTrace? sillTrace = null;
            if (opening.SillMetres is { } explicitSill) { Explicit(prefix + "sill_m", Number(explicitSill)); sill = explicitSill; }
            else if (opening.Kind == IntentOpeningKind.Door)
            {
                sources.Add(new(prefix + "sill_m", "0", IntentFieldSource.RuleOrigin, "door-sill-on-level", Array.Empty<SkillTrace>()));
                sill = 0;
            }
            else if (numbers.TryGetValue(WindowSill, out var fill))
            {
                sources.Add(new(prefix + "sill_m", Number(fill.Value), IntentFieldSource.DefaultOrigin, "window-sill-from-style", new[] { fill.Source }));
                sill = fill.Value;
                sillTrace = fill.Source;
            }
            else Missing(prefix + "sill_m", WindowSill, "window-sill-from-style");

            double? height = null;
            if (opening.HeightMetres is { } explicitHeight) { Explicit(prefix + "height_m", Number(explicitHeight)); height = explicitHeight; }
            else if (opening.Kind == IntentOpeningKind.Door)
            {
                if (numbers.TryGetValue(DoorHeight, out var fill))
                {
                    sources.Add(new(prefix + "height_m", Number(fill.Value), IntentFieldSource.DefaultOrigin, "door-height-from-style", new[] { fill.Source }));
                    height = fill.Value;
                }
                else Missing(prefix + "height_m", DoorHeight, "door-height-from-style");
            }
            else if (!numbers.TryGetValue(WindowHead, out var head)) Missing(prefix + "height_m", WindowHead, "window-height-from-head");
            else if (sill is { } from)
            {
                // An unknown sill is already refused above; the height is then not derived.
                var value = Round(head.Value - from);
                if (!double.IsFinite(value) || value <= 0)
                    issues.Add(new(IntentDefaultCodes.DerivedValueInvalid, prefix + "height_m",
                        "window.head_height_m " + Number(head.Value) + " (" + head.Source + ") minus sill " + Number(from) + " leaves no positive height"));
                else
                {
                    var traces = sillTrace is null ? new[] { head.Source } : new[] { head.Source, sillTrace };
                    sources.Add(new(prefix + "height_m", Number(value), IntentFieldSource.DerivedOrigin, "window-height-from-head", traces));
                    height = value;
                }
            }
            if (sill is { } s && height is { } h) openings.Add(new(opening.Key, opening.Kind, opening.Host, opening.OffsetMetres, opening.WidthMetres, h, s));
        }
        if (issues.Count > 0) return Refused(issues);

        var intent = new LayoutIntent(draft.Version, draft.Provenance, levels.ToArray(), types.ToArray(), draft.Rooms.ToArray(), openings.ToArray());
        var sorted = sources.OrderBy(source => source.Field, StringComparer.Ordinal).ToArray();
        return new IntentDefaultsResult(intent, sorted, Array.Empty<IntentIssue>());
    }

    static IntentDefaultsResult Refused(List<IntentIssue> issues) => new(null, Array.Empty<IntentFieldSource>(), issues
        .OrderBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Key, StringComparer.Ordinal).ThenBy(issue => issue.Detail, StringComparer.Ordinal)
        .ToArray());
}
