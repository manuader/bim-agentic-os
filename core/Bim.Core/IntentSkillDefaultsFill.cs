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

        // ---- Draft keys are unique per collection ----
        void Unique<T>(string collection, IEnumerable<T> items, Func<T, string> keyOf)
        {
            foreach (var group in items.GroupBy(item => keyOf(item) ?? "", StringComparer.Ordinal).Where(group => group.Count() > 1).OrderBy(group => group.Key, StringComparer.Ordinal))
                issues.Add(new(IntentDefaultCodes.DraftKeyDuplicate, collection + "[" + group.Key + "]", collection + " key used " + group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture) + " times"));
        }
        Unique("levels", draft.Levels, item => item.Key);
        Unique("wall_types", draft.WallTypes, item => item.Key);
        Unique("openings", draft.Openings, item => item.Key);
        if (issues.Count > 0) return Refused(issues);

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

        var levels = FillLevels(draft.Levels, numbers, sources, issues, Explicit, Missing);

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
            if (opening.HeightMetres is { } explicitHeight)
            {
                Explicit(prefix + "height_m", Number(explicitHeight));
                height = explicitHeight;
                // A style sill under an explicit height must keep the window head at or below the style head.
                if (sillTrace is not null && sill is { } styleSill && numbers.TryGetValue(WindowHead, out var styleHead) && double.IsFinite(explicitHeight)
                    && Round(styleSill + explicitHeight) > styleHead.Value)
                {
                    issues.Add(new(IntentDefaultCodes.DerivedValueInvalid, prefix + "sill_m", "window.sill_height_m " + Number(styleSill) + " (" + sillTrace + ") plus explicit height "
                        + Number(explicitHeight) + " exceeds window.head_height_m " + Number(styleHead.Value) + " (" + styleHead.Source + ")"));
                    height = null;
                }
            }
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

    static List<IntentLevel> FillLevels(IReadOnlyList<IntentDraftLevel> draftLevels, Dictionary<string, (double Value, SkillTrace Source)> numbers,
        List<IntentFieldSource> sources, List<IntentIssue> issues, Action<string, string> explicitSource, Action<string, string, string> missing)
    {
        static string Field(IntentDraftLevel level) => "levels[" + level.Key + "].elevation_m";
        var before = issues.Count;
        foreach (var level in draftLevels.OrderBy(item => item.Key, StringComparer.Ordinal))
            if (level.Storey is < 0)
                issues.Add(new(IntentDefaultCodes.StoreyInvalid, "levels[" + level.Key + "].storey", "storey must be 0 (ground) or above; give a level below ground an explicit elevation_m and no storey"));
        foreach (var group in draftLevels.Where(item => item.Storey is not null).GroupBy(item => item.Storey!.Value).OrderBy(group => group.Key))
        {
            var keys = group.Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
            if (keys.Length > 1)
                issues.Add(new(IntentDefaultCodes.StoreyDuplicate, "levels[" + keys[0] + "].storey", "storey " + group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + " is also used by " + string.Join(", ", keys.Skip(1))));
        }
        if (issues.Count > before) return new List<IntentLevel>();

        // Explicit levels with a storey anchor the open storeys above them.
        var anchors = draftLevels.Where(item => item.ElevationMetres is not null && item.Storey is not null).OrderBy(item => item.Storey!.Value).ToArray();
        var levels = new List<IntentLevel>();
        var elevations = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var level in draftLevels)
        {
            var field = Field(level);
            if (level.ElevationMetres is { } elevation)
            {
                explicitSource(field, Number(elevation));
                levels.Add(new(level.Key, level.Name, elevation));
                elevations[level.Key] = elevation;
                continue;
            }
            if (level.Storey is not { } storey)
            {
                issues.Add(new(IntentDefaultCodes.DefaultMissing, field, "elevation_m is open and storey is not set; give a storey (0 = ground) or an explicit elevation_m"));
                continue;
            }
            var anchor = anchors.LastOrDefault(item => item.Storey!.Value < storey);
            if (storey == 0)
            {
                sources.Add(new(field, "0", IntentFieldSource.RuleOrigin, "level-elevation-ground", Array.Empty<SkillTrace>()));
                levels.Add(new(level.Key, level.Name, 0));
                elevations[level.Key] = 0;
                continue;
            }
            var rule = anchor is null ? "level-elevation-from-storey" : "level-elevation-above-explicit";
            if (!numbers.TryGetValue(LevelHeight, out var height)) { missing(field, LevelHeight, rule); continue; }
            var value = anchor is null ? Round(storey * height.Value) : Round(anchor.ElevationMetres!.Value + (storey - anchor.Storey!.Value) * height.Value);
            sources.Add(new(field, Number(value), IntentFieldSource.DerivedOrigin, rule, new[] { height.Source }));
            levels.Add(new(level.Key, level.Name, value));
            elevations[level.Key] = value;
        }
        if (issues.Count > before) return levels;

        // Elevations must rise strictly with storey (explicit values are never moved, so a clash is refused).
        var stack = draftLevels.Where(item => item.Storey is not null).OrderBy(item => item.Storey!.Value).ToArray();
        for (var index = 1; index < stack.Length; index++)
        {
            double low = elevations[stack[index - 1].Key], high = elevations[stack[index].Key];
            if (!(high > low))
                issues.Add(new(IntentDefaultCodes.StoreyOrderInvalid, Field(stack[index]), "elevation " + Number(high) + " of storey " + stack[index].Storey!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " must be above " + Number(low) + " of " + stack[index - 1].Key + " (storey " + stack[index - 1].Storey!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"));
        }
        return levels;
    }

    static IntentDefaultsResult Refused(List<IntentIssue> issues) => new(null, Array.Empty<IntentFieldSource>(), issues
        .OrderBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Key, StringComparer.Ordinal).ThenBy(issue => issue.Detail, StringComparer.Ordinal)
        .ToArray());
}
