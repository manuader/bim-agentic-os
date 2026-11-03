using Bim.Core;
using System.Globalization;

// P5-03 oracle: two contrasting style packs (skills/bim.style.compact-modern and skills/bim.style.warm-masonry) load and
// validate, change the effective defaults visibly with specific values, trace every value to its skill, origin and key or
// rule id, compose with the typology and CABA code packs under priority-based resolution without relaxing any code minimum,
// and resolve equal-priority conflicts only through an explicit precedence order. Unit-tested only.
static class StyleSkillTests
{
    public const string Compact = "bim.style.compact-modern";
    public const string Masonry = "bim.style.warm-masonry";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Dwelling = "bim.typology.dwelling";
    const string Qa = "bim.qa.room-basics";

    // The specification of each style's defaults: dotted key to value.
    static readonly (string Key, SkillValue Value)[] CompactDefaults =
    {
        ("ceiling.finish", SkillValue.Of("exposed-concrete")),
        ("door.entrance.width_m", SkillValue.Of(1.0)),
        ("door.interior.width_m", SkillValue.Of(0.8)),
        ("floor.finish", SkillValue.Of("polished-concrete")),
        ("level.height_m", SkillValue.Of(2.9)),
        ("roof.type", SkillValue.Of("flat-green")),
        ("room.min_clear_height_m", SkillValue.Of(2.6)),
        ("wall.exterior.thickness_m", SkillValue.Of(0.2)),
        ("wall.exterior.type", SkillValue.Of("reinforced-concrete-150-etics-50")),
        ("wall.interior.thickness_m", SkillValue.Of(0.1)),
        ("window.head_height_m", SkillValue.Of(2.5)),
        ("window.sill_height_m", SkillValue.Of(0.3)),
        ("window.wall_ratio", SkillValue.Of(0.4)),
    };

    static readonly (string Key, SkillValue Value)[] MasonryDefaults =
    {
        ("ceiling.finish", SkillValue.Of("lime-plaster")),
        ("door.entrance.width_m", SkillValue.Of(1.2)),
        ("door.interior.width_m", SkillValue.Of(0.9)),
        ("floor.finish", SkillValue.Of("terracotta-tile")),
        ("level.height_m", SkillValue.Of(3.3)),
        ("roof.type", SkillValue.Of("pitched-clay-tile")),
        ("room.min_clear_height_m", SkillValue.Of(3.0)),
        ("wall.exterior.thickness_m", SkillValue.Of(0.45)),
        ("wall.exterior.type", SkillValue.Of("solid-brick-450-lime-render")),
        ("wall.interior.thickness_m", SkillValue.Of(0.15)),
        ("window.head_height_m", SkillValue.Of(2.4)),
        ("window.sill_height_m", SkillValue.Of(0.9)),
        ("window.wall_ratio", SkillValue.Of(0.18)),
    };

    // Stable rule ids and resolved thresholds of each style.
    static readonly (string Id, double Threshold)[] CompactRules =
    {
        ("bedroom-height-min", 2.6), ("biblioteca-height-min", 2.6), ("cocina-comedor-guion-height-min", 2.6), ("cocina-comedor-height-min", 2.6),
        ("comedor-height-min", 2.6), ("corredor-height-min", 2.6), ("corridor-height-min", 2.6), ("dining-height-min", 2.6), ("dormitorio-height-min", 2.6), ("escritorio-height-min", 2.6),
        ("estar-glazing-min", 0.25), ("estar-height-min", 2.6), ("estudio-height-min", 2.6), ("living-glazing-min", 0.25),
        ("living-height-min", 2.6), ("local-unico-height-min", 2.6), ("monoambiente-height-min", 2.6), ("pasillo-height-min", 2.6), ("studio-height-min", 2.6),
        ("wall-thickness-max", 0.25),
    };

    static readonly (string Id, double Threshold)[] MasonryRules =
    {
        ("bedroom-height-min", 3.0), ("biblioteca-height-min", 3.0), ("cocina-comedor-guion-height-min", 3.0), ("cocina-comedor-height-min", 3.0),
        ("comedor-height-min", 3.0), ("corredor-height-min", 3.0), ("corridor-height-min", 3.0), ("dining-height-min", 3.0), ("dormitorio-height-min", 3.0), ("escritorio-height-min", 3.0),
        ("estar-glazing-min", 0.125), ("estar-height-min", 3.0), ("estudio-height-min", 3.0), ("living-glazing-min", 0.125),
        ("living-height-min", 3.0), ("local-unico-height-min", 3.0), ("monoambiente-height-min", 3.0), ("pasillo-height-min", 3.0), ("studio-height-min", 3.0),
        ("wall-thickness-min", 0.15),
    };

    static SkillRequest[] Requests(params string[] ids) => ids.Select(id => new SkillRequest(id)).ToArray();

    static string Render(SkillResolution resolution) => string.Join("\n", resolution.Settings.Select(setting => setting.Key + "=" + setting.Value + " from " + setting.Source + " " + setting.Decision
            + " over [" + string.Join(",", setting.Overridden) + "]")
        .Concat(resolution.Rules.Select(rule => "rule " + rule.QualifiedId + " " + rule.Threshold + " " + rule.ThresholdSource))
        .Concat(resolution.Issues.Select(issue => "issue " + issue.Code + "@" + issue.Subject + " " + issue.Detail)));

    static string Show(RoomValidationReport report) => string.Join("\n", report.Findings.Select(finding => finding.RoomId + " " + finding.RuleId + " " + finding.Outcome + " "
        + finding.Property + " measured=" + (finding.Measured?.ToString("R", CultureInfo.InvariantCulture) ?? "-") + " required=" + (finding.Required?.ToString("R", CultureInfo.InvariantCulture) ?? "-")
        + " | " + finding.Detail)) + "\nunmatched " + string.Join(",", report.UnmatchedRoomIds);

    public static void Run(string root, Action<bool, string, string> sink)
    {
        void check(bool condition, string name, string detail = "") => sink(condition, name, detail);
        SkillCatalog catalog;
        try { catalog = SkillLoader.LoadDirectory(Path.Combine(root, "skills")); }
        catch (SkillException error) { check(false, "style: skills/ loads", error.Code + " " + error.File + " at " + error.Field + " (" + error.Detail + ")"); return; }
        var compact = catalog.Find(Compact);
        var masonry = catalog.Find(Masonry);
        check(compact is not null && masonry is not null, "style: both style packs load through SkillLoader", string.Join(",", catalog.Skills.Select(skill => skill.Id)));
        if (compact is null || masonry is null) return;
        var caba = catalog.Find(Caba)!;

        // ---- Manifest shape ----
        foreach (var (style, other) in new[] { (compact, masonry), (masonry, compact) })
        {
            check(style.Kind == SkillKind.Style && style.Priority == 30 && style.Version == new SkillVersion(1, 0, 0) && style.Requires.Count == 0,
                "style: " + style.Id + " is a style skill 1.0.0 with priority 30 (above typology 20, below qa 50 and code 100)", style.Kind + " " + style.Priority + " " + style.Version);
            check(style.ConflictsWith.SequenceEqual(new[] { other.Id }), "style: " + style.Id + " declares the other style as a conflict", string.Join(",", style.ConflictsWith));
            check(style.Description.Contains("not a building code", StringComparison.OrdinalIgnoreCase)
                && style.Rules.All(rule => rule.Citation is not null && rule.Citation.Contains("not normative", StringComparison.Ordinal)),
                "style: " + style.Id + " states it is illustrative, not a building code, and cites every rule as not normative");
        }

        // Vertical consistency of the effective defaults: sill < head <= clear height < level height.
        void ConsistentHeights(SkillResolution resolution, string label)
        {
            double Of(string key) => resolution.Setting(key)?.Value.Number ?? double.NaN;
            var (sill, head, clearHeight, level) = (Of("window.sill_height_m"), Of("window.head_height_m"), Of("room.min_clear_height_m"), Of("level.height_m"));
            check(sill < head && head <= clearHeight && clearHeight < level,
                "style: " + label + " keeps window sill < window head <= clear height < level height",
                string.Create(CultureInfo.InvariantCulture, $"sill={sill} head={head} clear={clearHeight} level={level}"));
        }

        // ---- Each style alone: specific, traced defaults and rules ----
        SkillResolution Alone(SkillManifest style, (string Key, SkillValue Value)[] expected, (string Id, double Threshold)[] rules)
        {
            var resolution = SkillResolver.Resolve(catalog, Requests(style.Id));
            check(resolution.Succeeded && resolution.Skills.Count == 1, "style: " + style.Id + " resolves on its own", Render(resolution));
            check(resolution.Settings.Select(setting => (setting.Key, setting.Value)).SequenceEqual(expected),
                "style: " + style.Id + " resolves exactly its specified defaults in ordinal key order", Render(resolution));
            check(resolution.Settings.All(setting => setting.Decision == SkillDecision.Only && setting.Overridden.Count == 0
                    && setting.Source == new SkillTrace(style.Id, SkillTrace.DefaultOrigin, setting.Key) && setting.Source.ToString() == style.Id + ":default:" + setting.Key),
                "style: every " + style.Id + " default reports its trace id <skill>:default:<key>", Render(resolution));
            check(resolution.Rules.Select(rule => (rule.Rule.Id, rule.Threshold?.Number ?? double.NaN)).SequenceEqual(rules)
                    && resolution.Rules.All(rule => rule.Source == new SkillTrace(style.Id, SkillTrace.RuleOrigin, rule.Rule.Id) && rule.QualifiedId == style.Id + "/" + rule.Rule.Id),
                "style: " + style.Id + " rules carry the specified stable ids and thresholds, each traced to <skill>/<rule id>", Render(resolution));
            check(resolution.Rules.Where(rule => rule.Rule.Check.Parameter is not null).All(rule => rule.ThresholdSource == new SkillTrace(style.Id, SkillTrace.ParameterOrigin, rule.Rule.Check.Parameter!))
                    && resolution.Rules.Count(rule => rule.ThresholdSource is not null) == 19,
                "style: " + style.Id + " parameter thresholds trace to <skill>:parameter:<name>", Render(resolution));
            ConsistentHeights(resolution, style.Id + " alone");
            var clear = resolution.Setting("room.min_clear_height_m")?.Value.Number;
            check(style.Parameters.FirstOrDefault(parameter => parameter.Name == "clear_height_min_m")?.Default.Number == clear && clear is not null,
                "style: " + style.Id + " clear height parameter equals its room.min_clear_height_m default");
            return resolution;
        }
        var compactAlone = Alone(compact, CompactDefaults, CompactRules);
        var masonryAlone = Alone(masonry, MasonryDefaults, MasonryRules);

        // ---- Visible contrast ----
        check(compactAlone.Settings.Select(setting => setting.Key).SequenceEqual(masonryAlone.Settings.Select(setting => setting.Key))
                && compactAlone.Settings.Zip(masonryAlone.Settings).All(pair => pair.First.Value != pair.Second.Value),
            "style: the two styles set the same keys and differ on every one of them");
        double Number(SkillResolution resolution, string key) => resolution.Setting(key)?.Value.Number ?? double.NaN;
        check(Number(compactAlone, "wall.exterior.thickness_m") < Number(masonryAlone, "wall.exterior.thickness_m")
                && Number(compactAlone, "window.wall_ratio") > 2 * Number(masonryAlone, "window.wall_ratio")
                && Number(compactAlone, "window.sill_height_m") < Number(masonryAlone, "window.sill_height_m")
                && Number(compactAlone, "room.min_clear_height_m") < Number(masonryAlone, "room.min_clear_height_m")
                && Number(compactAlone, "door.interior.width_m") < Number(masonryAlone, "door.interior.width_m"),
            "style: compact modern is slimmer-walled, more glazed, lower-silled and lower than warm masonry");

        // ---- Composition with the typology pack: style priority 30 beats typology 20 ----
        foreach (var (style, expected) in new[] { (compact, CompactDefaults), (masonry, MasonryDefaults) })
        {
            var withTypology = SkillResolver.Resolve(catalog, Requests(Dwelling, style.Id));
            check(withTypology.Succeeded, "style: " + style.Id + " composes with the dwelling typology", Render(withTypology));
            var typologyTrace = (string key) => new SkillTrace(Dwelling, SkillTrace.DefaultOrigin, key);
            check(new[] { "level.height_m", "room.min_clear_height_m", "wall.exterior.thickness_m", "wall.interior.thickness_m" }.All(key =>
                    withTypology.Setting(key) is { Decision: SkillDecision.Priority } setting && setting.Source == new SkillTrace(style.Id, SkillTrace.DefaultOrigin, key)
                    && setting.Value == expected.First(item => item.Key == key).Value && setting.Overridden.SequenceEqual(new[] { typologyTrace(key) })),
                "style: on keys shared with the typology the style wins by priority and the typology default is recorded as overridden", Render(withTypology));
            check(withTypology.Setting("room.bedroom.min_area_m2") is { Decision: SkillDecision.Only, Overridden.Count: 0 } bedroom && bedroom.Value == SkillValue.Of(9)
                    && bedroom.Source == typologyTrace("room.bedroom.min_area_m2") && withTypology.Settings.Count == expected.Length + 1,
                "style: typology-only keys keep their typology source; settings are the union", Render(withTypology));
            var reversed = SkillResolver.Resolve(new SkillCatalog(catalog.Skills.Reverse().ToArray()), Requests(style.Id, Dwelling));
            check(Render(reversed) == Render(withTypology), "style: " + style.Id + " composition does not depend on catalog or request order");
        }

        // ---- Composition with the CABA code pack: no style relaxes a code minimum ----
        var cabaHeights = caba.Rules.Where(rule => rule.Check is { Op: SkillCheckOp.Min, Property: RoomFacts.ClearHeightKey, Value: not null }).Select(rule => rule.Check.Value!.Number).ToArray();
        foreach (var style in new[] { compact, masonry })
        {
            var composed = SkillResolver.Resolve(catalog, Requests(Caba, Dwelling, style.Id));
            check(composed.Succeeded && composed.Rules.Count == caba.Rules.Count + 2 + style.Rules.Count,
                "style: " + style.Id + " composes with the CABA code pack and the typology without conflicts; code rules are all kept", Render(composed));
            ConsistentHeights(composed, style.Id + " with CABA and the typology");
            var withQa = SkillResolver.Resolve(catalog, Requests(Caba, Qa, Dwelling, style.Id));
            check(withQa.Succeeded && withQa.Setting("room.min_clear_height_m") is { Value.Number: >= 2.6 } && withQa.Setting("window.head_height_m") is { } head
                    && head.Value.Number <= withQa.Setting("room.min_clear_height_m")!.Value.Number,
                "style: with CABA, qa and the typology the effective clear height of " + style.Id + " stays at or above the CABA 2.60 m and above the window head", Render(withQa));
            ConsistentHeights(withQa, style.Id + " with CABA, qa and the typology");
            check(cabaHeights.Length > 0 && composed.Setting("room.min_clear_height_m")?.Value.Number >= cabaHeights.Max(),
                "style: " + style.Id + " default clear height meets the highest CABA clear-height minimum", string.Join(",", cabaHeights));
            // Every style room rule that shares a CABA rule's room glob, op and properties is at least as strict.
            var pairs = (from mine in composed.Rules.Where(rule => rule.SkillId == style.Id && rule.Rule.Target.RoomPattern is not null)
                         from code in composed.Rules.Where(rule => rule.SkillId == Caba)
                         where code.Rule.Target.RoomPattern == mine.Rule.Target.RoomPattern && code.Rule.Check.Op == mine.Rule.Check.Op
                             && code.Rule.Check.Property == mine.Rule.Check.Property && code.Rule.Check.Over == mine.Rule.Check.Over
                         select (mine, code)).ToArray();
            check(caba.Rules.Where(rule => rule.Check.Property == RoomFacts.ClearHeightKey).All(code => pairs.Any(pair => pair.code.Rule.Id == code.Id)),
                "style: " + style.Id + " has a clear-height rule for every room glob the CABA pack sets a clear height for",
                string.Join(",", caba.Rules.Where(rule => rule.Check.Property == RoomFacts.ClearHeightKey && !pairs.Any(pair => pair.code.Rule.Id == rule.Id)).Select(rule => rule.Id)));
            check(pairs.Length >= 19 && pairs.All(pair => pair.mine.Threshold!.Number >= pair.code.Threshold!.Number - 1e-12),
                "style: every " + style.Id + " room rule is at least as strict as the matching CABA minimum",
                string.Join("; ", pairs.Select(pair => pair.mine.QualifiedId + "=" + pair.mine.Threshold + " vs " + pair.code.QualifiedId + "=" + pair.code.Threshold)));
            // Parameter bounds sit at the code floor, so a run-time override cannot go below it.
            check(style.Parameters.First(parameter => parameter.Name == "living_glazing_ratio_min").Min == 0.1
                    && style.Parameters.First(parameter => parameter.Name == "clear_height_min_m").Min == 2.6,
                "style: " + style.Id + " parameter lower bounds equal the CABA illumination (A/10) and living clear height (2.60 m) minimums");
            var styleRooms = new[] { new RoomFacts("living", "Living", 16, 4, 2.6, 4.0, 2.0) };
            bool Refused(string parameter, double value)
            {
                try { SkillRoomValidator.Validate(composed, styleRooms, new Dictionary<string, double> { [style.Id + "/" + parameter] = value }); return false; }
                catch (ArgumentException error) { return error.Message.Contains(style.Id + "/" + parameter, StringComparison.Ordinal); }
            }
            check(Refused("living_glazing_ratio_min", 0.09) && Refused("clear_height_min_m", 2.5) && !Refused("living_glazing_ratio_min", 0.1) && !Refused("clear_height_min_m", 2.6),
                "style: overriding a " + style.Id + " parameter below the code floor is refused naming the parameter; the floor itself is accepted");

            // A room built exactly to the style's defaults complies with code, typology and style.
            var clear = composed.Setting("room.min_clear_height_m")!.Value.Number;
            var glazing = composed.Rules.First(rule => rule.QualifiedId == style.Id + "/living-glazing-min").Threshold!.Number;
            var living = new RoomFacts("a-living", "Living", 16, 4, clear, glazing * 16, glazing * 8);
            var dormitorio = new RoomFacts("b-dormitorio", "Dormitorio 1", 9.6, 3, clear, 1.2, 0.4);
            var bedroom = new RoomFacts("c-bedroom", "Bedroom 2", 9.6, 3, clear, 1.2, 0.4);
            var report = SkillRoomValidator.Validate(composed, new[] { living, dormitorio, bedroom });
            check(report.Compliant && report.Failures.Count == 0 && report.Findings.Count(finding => finding.RuleId.StartsWith(Caba + "/", StringComparison.Ordinal)) == 15
                    && report.Findings.Count(finding => finding.RuleId.StartsWith(style.Id + "/", StringComparison.Ordinal)) == 4
                    && report.Findings.Where(finding => finding.RuleId.StartsWith(Dwelling + "/", StringComparison.Ordinal)).Select(finding => finding.RuleId).OrderBy(id => id, StringComparer.Ordinal)
                        .SequenceEqual(new[] { Dwelling + "/bedroom-min-area", Dwelling + "/living-daylight" }),
                "style: rooms built to " + style.Id + " defaults pass every CABA, typology and style rule", Show(report));

            // Rules are additive: the style cannot remove or relax a code rule.
            var low = SkillRoomValidator.Validate(composed, new[] { new RoomFacts("low", "Living", 16, 4, 2.5, 4, 2) });
            check(low.Findings.FirstOrDefault(finding => finding.RuleId == Caba + "/living-height-min") is { Outcome: RoomCheckOutcome.Fail, Required: 2.6 }
                    && low.Findings.FirstOrDefault(finding => finding.RuleId == style.Id + "/living-height-min") is { Outcome: RoomCheckOutcome.Fail, Required: var styleRequired } && styleRequired == clear,
                "style: a living room below the CABA 2.60 m clear height still fails the code rule with " + style.Id, Show(low));
        }
        var atCodeFloor = SkillRoomValidator.Validate(SkillResolver.Resolve(catalog, Requests(Caba, Masonry)), new[] { new RoomFacts("floor", "Living", 16, 4, 3.0, 1.6, 0.6) });
        check(atCodeFloor.Findings.FirstOrDefault(finding => finding.RuleId == Caba + "/living-illumination-ratio") is { Outcome: RoomCheckOutcome.Pass }
                && atCodeFloor.Findings.FirstOrDefault(finding => finding.RuleId == Masonry + "/living-glazing-min") is { Outcome: RoomCheckOutcome.Fail, Required: 0.125, Severity: SkillSeverity.Warning },
            "style: glazing at the CABA A/10 floor passes the code but warns under the warm masonry style, which is stricter", Show(atCodeFloor));

        // ---- The two styles together ----
        var both = SkillResolver.Resolve(catalog, Requests(Compact, Masonry));
        check(!both.Succeeded && both.Issues.Count == 1 && both.Issues[0] is { Code: SkillCodes.SkillConflict, Subject: Compact } && both.Settings.Count == 0,
            "style: requesting both styles is a declared skill_conflict reported once", Render(both));
        // Without the declared conflict, equal priority with different values is a default_conflict on every key unless a
        // precedence order is given; the order then decides deterministically.
        var free = new SkillCatalog(new[] { compact with { ConflictsWith = Array.Empty<string>() }, masonry with { ConflictsWith = Array.Empty<string>() }, caba });
        var tied = SkillResolver.Resolve(free, Requests(Compact, Masonry));
        check(!tied.Succeeded && tied.Issues.All(issue => issue.Code == SkillCodes.DefaultConflict)
                && tied.Issues.Select(issue => issue.Subject).SequenceEqual(CompactDefaults.Select(item => item.Key)),
            "style: equal-priority styles without precedence report a default_conflict for each of their keys", Render(tied));
        foreach (var (first, second, expected) in new[] { (Masonry, Compact, MasonryDefaults), (Compact, Masonry, CompactDefaults) })
        {
            var ordered = SkillResolver.Resolve(free, Requests(Compact, Masonry, Caba), new[] { first, second });
            check(ordered.Succeeded && ordered.Settings.Select(setting => (setting.Key, setting.Value)).SequenceEqual(expected)
                    && ordered.Settings.All(setting => setting.Decision == SkillDecision.Precedence && setting.Source.SkillId == first
                        && setting.Overridden.SequenceEqual(new[] { new SkillTrace(second, SkillTrace.DefaultOrigin, setting.Key) })),
                "style: precedence " + first + " over " + second + " picks every " + first + " default and records the other as overridden", Render(ordered));
            var again = SkillResolver.Resolve(new SkillCatalog(free.Skills.Reverse().ToArray()), Requests(Caba, Masonry, Compact), new[] { first, second });
            check(Render(again) == Render(ordered), "style: precedence resolution does not depend on catalog or request order");
        }
    }
}
