using Bim.Core;
using System.Text.Json;
// P2-07 drift resolution oracle: MirrorDriftResolve.Plan (core/Bim.Core/MirrorDriftResolve.cs) turns a P2-04 drift
// report into one proposal per drifted element, adopt (a BIM-IR operation from the mirror) or restore (catalogue
// commands back to Core state), never applied. Restore steps are checked against the catalogue through
// PlanToCommands.Envelope (RevitCommandProtocol.Parse). No Revit is involved: a passing run is unit-tested evidence only.
static class MirrorDriftResolveChecks
{
    const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string OtherDoc = "11111111-2222-3333-4444-555555555555-00000001";
    const string P = "60f91daf-3dd7-4283-a86d-24137b73f3da-";
    const string L1 = P + "00000137";
    const string W1 = P + "00001001";
    const string W2 = P + "00001002";
    const string W3 = P + "00001003";
    const string D1 = P + "00002001";
    const string F1 = P + "00004001";
    const string Created = P + "0000ffff";
    const string DoorSymbol = "Single-Flush: 0915 x 2134mm";
    const string WindowSymbol = "Fixed: 1000 x 1200mm";
    const string WallType = "Generic - 200mm";
    static readonly Guid CoreL = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    static readonly Guid CoreW1 = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    static readonly Guid CoreW2 = Guid.Parse("c0000000-0000-4000-8000-000000000003");
    static readonly Guid CoreD1 = Guid.Parse("c0000000-0000-4000-8000-000000000004");
    static readonly RevitDocumentRef Document = new(@"C:\Repo\.bim\native-runs\p2-07\copy.rvt", Doc);
    static RevitBindings Bindings() => new(Document, new Dictionary<Guid, string>(), new[] { new RevitWallTypeBinding(0.2, WallType) }, DoorSymbol, WindowSymbol);

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, "drift-resolve: " + name, detail);
    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (ArgumentException error) { return error.Message.Split(' ')[0]; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        try { Checks(); }
        catch (Exception error) { Check(false, "oracle threw", error.GetType().Name + ": " + error.Message); }
    }

    static MirrorObservedElement Wall(MirrorObservedElement template, string uid, double ax, double ay, double bx, double by) =>
        template with { UniqueId = uid, Start = new MirrorPoint(ax, ay), End = new MirrorPoint(bx, by) };

    static MirrorDriftProposal? Find(MirrorDriftResolution resolution, string uid) => resolution.Proposals.FirstOrDefault(proposal => proposal.UniqueId == uid);

    static RevitCommandStep[] StepsOf(MirrorDriftResolution resolution, MirrorDriftProposal? proposal) =>
        proposal is null ? Array.Empty<RevitCommandStep>() : proposal.Restore.Operations.Select(id => resolution.RestorePlan.Steps.FirstOrDefault(step => step.OperationId == id)).OfType<RevitCommandStep>().ToArray();

    static JsonElement Args(RevitCommandStep step)
    {
        using var parsed = JsonDocument.Parse(step.ArgsJson);
        return parsed.RootElement.Clone();
    }

    static string Strings(JsonElement array) => string.Join(",", array.EnumerateArray().Select(item => item.ToString()));

    static bool Near(IReadOnlyList<double> a, IReadOnlyList<double> b) => a.Count == b.Count && a.Zip(b).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9);

    // The committed model with every proposed adopt accepted (what a reviewer would commit; the planner never does).
    static MirrorCommittedModel Adopted(MirrorCommittedModel committed, MirrorDriftResolution resolution)
    {
        var elements = committed.Project.Elements.ToDictionary(element => element.Id);
        var map = committed.NativeIds.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var proposal in resolution.Proposals)
        {
            var adopt = proposal.Adopt;
            if (adopt.Status != MirrorDriftResolveStatus.Proposed || adopt.Element is null) continue;
            if (adopt.Action == MirrorDriftAdoptActions.Delete) { elements.Remove(adopt.Element.Id); map.Remove(adopt.Element.Id); continue; }
            elements[adopt.Element.Id] = adopt.Element;
            if (adopt.MapUniqueId is { } uid) map[adopt.Element.Id] = uid;
        }
        return committed with
        {
            Project = committed.Project with { Elements = elements.Values.OrderBy(element => element.Id.ToString("D"), StringComparer.Ordinal).ToArray() },
            NativeIds = map,
        };
    }

    // Every step parses as a catalogue command once its inputs are filled; every step belongs to exactly one proposal.
    static void CatalogueValid(MirrorDriftResolution resolution, string label)
    {
        var plan = resolution.RestorePlan;
        var created = plan.Steps.ToDictionary(step => step.OperationId, _ => Created, StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var step in plan.Steps)
        {
            if (RevitCommandCatalogue.Find(step.Command) is not { Writes: true }) failures.Add(step.Command + " is not a catalogue write command");
            if (!step.OperationId.StartsWith("drift.", StringComparison.Ordinal) || !RevitCommandProtocol.IsOperationId(step.OperationId)) failures.Add(step.OperationId + " is not a drift operation id");
            if (Code(() => PlanToCommands.Envelope(plan, step, created)) is { } code) failures.Add(step.Command + ": " + code);
        }
        var owned = resolution.Proposals.SelectMany(proposal => proposal.Restore.Operations).ToArray();
        if (!owned.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(plan.Steps.Select(step => step.OperationId).OrderBy(id => id, StringComparer.Ordinal))) failures.Add("proposal operations differ from plan steps");
        if (plan.Steps.Select(step => step.OperationId).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Count) failures.Add("duplicate operation ids");
        var position = plan.Steps.Select((step, index) => (step.OperationId, index)).ToDictionary(pair => pair.OperationId, pair => pair.index, StringComparer.Ordinal);
        foreach (var step in plan.Steps)
            if (step.DependsOn.Any(dependency => !position.TryGetValue(dependency, out var at) || at >= position[step.OperationId])) failures.Add(step.OperationId + " depends on a later or unknown step");
        Check(plan.Steps.Count > 0 && failures.Count == 0, label + ": every restore step validates against the catalogue schema and runs after its dependencies", string.Join("; ", failures));
    }

    static void Checks()
    {
        var repo = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
        var dir = repo is null ? "" : Path.Combine(repo, "tests", "mirror-journal", "fixtures", "drift");
        Check(Directory.Exists(dir), "fixture directory exists", dir);
        if (!Directory.Exists(dir)) return;
        string Read(string name) => File.ReadAllText(Path.Combine(dir, name));
        var observation = JsonSerializer.Deserialize<MirrorObservation>(Read("observation.json"), Snake)!;
        var committed = MirrorCommittedModel.Parse(Read("committed.json"));
        var journal = MirrorDrift.ParseJournalEntries(Read("journal.json"));
        MirrorSnapshot Snap(params MirrorObservedElement[] elements) => MirrorSnapshotMapper.Map(observation with { Elements = elements });
        var wall1 = observation.Elements.Single(element => element.UniqueId == W1);
        var wall2 = observation.Elements.Single(element => element.UniqueId == W2);
        var door = observation.Elements.Single(element => element.UniqueId == D1);
        var floor = observation.Elements.Single(element => element.UniqueId == F1);
        var options = new MirrorDriftOptions(JournalAfterSequence: 4);

        // ---- No drift: nothing to resolve ----
        var baseline = MirrorSnapshotMapper.Map(observation);
        var none = MirrorDriftResolve.Plan(MirrorDrift.Compare(baseline, committed), baseline, committed, Bindings());
        Check(none.Proposals.Count == 0 && none.RestorePlan.Steps.Count == 0 && none.RestorePlan.Unsupported.Count == 0 && !none.Applied,
            "a report without drift gives no proposals and no restore steps", none.ToJson());

        // ---- The drifted fixture: W2 moved (add-in pending), W3 added (external), D1 deleted (external) ----
        var drifted = Snap(wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), Wall(wall1, W3, 0, 0, 0, 4000), floor);
        var report = MirrorDrift.Compare(drifted, committed, journal, options);
        var before = JsonSerializer.Serialize(committed.Project);
        var resolution = MirrorDriftResolve.Plan(report, drifted, committed, Bindings());
        Check(resolution.Proposals.Select(proposal => (proposal.FindingKind, proposal.UniqueId)).SequenceEqual(report.Findings.Select(finding => (finding.Kind, finding.UniqueId))),
            "one proposal per drifted element, in report order", resolution.ToJson());
        Check(resolution.DocumentUid == Doc && resolution.CoreProjectId == committed.Project.Id && resolution.CoreRevision == 3 && !resolution.Applied
            && resolution.RestorePlan.ProjectId == committed.Project.Id && resolution.RestorePlan.ExpectedRevision == 3 && resolution.RestorePlan.AfterHash == Compiler.Hash(committed.Project)
            && resolution.RestorePlan.Document == Document, "the resolution carries the document, Core revision and a restore plan bound to Core's state");
        Check(JsonSerializer.Serialize(committed.Project) == before, "planning never changes the committed model");

        var w2 = Find(resolution, W2);
        Check(w2 is { FindingKind: MirrorDriftKinds.Changed, Classification: MirrorDriftClasses.AddInPending, Recommended: MirrorDriftResolveActions.Adopt, CoreId: { } w2Core } && w2Core == CoreW2,
            "an add-in pending change recommends adopt", resolution.ToJson());
        Check(w2?.Adopt is { Status: MirrorDriftResolveStatus.Proposed, Action: MirrorDriftAdoptActions.Update, Element: { } w2Element } && w2Element.Id == CoreW2 && w2Element.Kind == "wall"
            && Near(w2Element.GeometryMetres, new[] { 5.05, 0, 5.05, 4, 3, 0.2 }) && w2Element.Dependencies.SequenceEqual(new[] { CoreL }) && w2Element.Provenance == MirrorDriftResolve.AdoptProvenance,
            "adopting the moved wall updates its BIM-IR element to the mirror geometry with Core dependencies", resolution.ToJson());
        var w2Steps = StepsOf(resolution, w2);
        Check(w2?.Restore is { Status: MirrorDriftResolveStatus.Proposed, Mode: MirrorDriftRestoreModes.Replace } && w2Steps.Select(step => step.Command).SequenceEqual(new[] { "element.delete", "wall.create" })
            && Strings(Args(w2Steps[0]).GetProperty("element_uids")) == W2 && w2Steps[1].DependsOn.Contains(w2Steps[0].OperationId) && w2Steps[1].ElementId == CoreW2,
            "restoring the moved wall deletes it and re-creates it from Core", resolution.ToJson());
        if (w2Steps.Length == 2)
        {
            var args = Args(w2Steps[1]);
            Check(Strings(args.GetProperty("curve").GetProperty("start")) == "5000,0" && Strings(args.GetProperty("curve").GetProperty("end")) == "5000,4000" && args.GetProperty("curve").GetProperty("kind").GetString() == "line"
                && args.GetProperty("height_mm").GetDouble() == 3000 && args.GetProperty("level").GetString() == L1 && args.GetProperty("type").GetString() == WallType,
                "the re-created wall has Core's axis and height in millimetres, its mapped level UniqueId and the bound wall type", w2Steps[1].ArgsJson);
        }

        var w3 = Find(resolution, W3);
        Check(w3 is { FindingKind: MirrorDriftKinds.Extra, Classification: MirrorDriftClasses.ExternalEdit, Recommended: MirrorDriftResolveActions.Adopt, CoreId: null }
            && w3.Adopt is { Status: MirrorDriftResolveStatus.Proposed, Action: MirrorDriftAdoptActions.Create, MapUniqueId: W3, Element: { } w3Element }
            && w3Element.Id == MirrorSnapshotMapper.Id(Doc, W3) && w3Element.Kind == "wall" && Near(w3Element.GeometryMetres, new[] { 0, 0, 0, 4, 3, 0.2 }) && w3Element.Dependencies.SequenceEqual(new[] { CoreL }),
            "adopting an external extra wall creates a BIM-IR element with the snapshot id and maps its UniqueId", resolution.ToJson());
        var w3Steps = StepsOf(resolution, w3);
        Check(w3?.Restore is { Status: MirrorDriftResolveStatus.Proposed, Mode: MirrorDriftRestoreModes.Delete } && w3Steps.Length == 1 && w3Steps[0].Command == "element.delete" && Strings(Args(w3Steps[0]).GetProperty("element_uids")) == W3,
            "restoring an extra wall deletes it by UniqueId", resolution.ToJson());

        var d1 = Find(resolution, D1);
        Check(d1 is { FindingKind: MirrorDriftKinds.Missing, Classification: MirrorDriftClasses.ExternalEdit, Recommended: MirrorDriftResolveActions.Adopt }
            && d1.Adopt is { Status: MirrorDriftResolveStatus.Proposed, Action: MirrorDriftAdoptActions.Delete, Element: { } d1Element } && d1Element.Id == CoreD1,
            "adopting an externally deleted door deletes its BIM-IR element", resolution.ToJson());
        var d1Steps = StepsOf(resolution, d1);
        Check(d1?.Restore is { Status: MirrorDriftResolveStatus.Proposed, Mode: MirrorDriftRestoreModes.Create } && d1Steps.Length == 1 && d1Steps[0].Command == "opening.place" && d1Steps[0].Inputs.Count == 0,
            "restoring the deleted door places it again", resolution.ToJson());
        if (d1Steps.Length == 1)
        {
            var args = Args(d1Steps[0]);
            Check(args.GetProperty("category").GetString() == "door" && args.GetProperty("host_wall_uid").GetString() == W1 && args.GetProperty("position_along_mm").GetDouble() == 1000
                && args.GetProperty("symbol").GetString() == DoorSymbol && !args.TryGetProperty("sill_mm", out _), "the door is placed on its present host at Core's position with the bound symbol", d1Steps[0].ArgsJson);
        }
        Check(resolution.RestorePlan.Steps.Select(step => step.Command).SequenceEqual(new[] { "element.delete", "element.delete", "wall.create", "opening.place" }),
            "restore steps run deletes first, then walls, then openings", string.Join(",", resolution.RestorePlan.Steps.Select(step => step.Command)));
        CatalogueValid(resolution, "drifted fixture");

        // Accepting every adopt proposal gives a valid model that no longer drifts from the mirror.
        var adopted = Adopted(committed, resolution);
        Check(Compiler.Validate(adopted.Project).Length == 0 && !MirrorDrift.Compare(drifted, adopted, journal, options).HasDrift,
            "accepting every adopt proposal yields a valid BIM-IR model without drift", MirrorDrift.Compare(drifted, adopted, journal, options).ToJson());

        // ---- Without evidence the recommendation is restore ----
        var unknown = MirrorDriftResolve.Plan(MirrorDrift.Compare(drifted, committed, options: new MirrorDriftOptions(Scope: MirrorDriftScopes.Document)), drifted, committed, Bindings());
        Check(unknown.Proposals.Count == 3 && unknown.Proposals.All(proposal => proposal.Classification == MirrorDriftClasses.Unknown && proposal.Recommended == MirrorDriftResolveActions.Restore),
            "unknown drift recommends restoring Core state", unknown.ToJson());

        // ---- A height-only change restores through element.set_parameter ----
        var taller = Snap(wall1 with { HeightMm = 3200 }, wall2, door, floor);
        var heightOnly = MirrorDriftResolve.Plan(MirrorDrift.Compare(taller, committed), taller, committed, Bindings());
        var w1 = Find(heightOnly, W1);
        var w1Steps = StepsOf(heightOnly, w1);
        Check(w1?.Restore is { Status: MirrorDriftResolveStatus.Proposed, Mode: MirrorDriftRestoreModes.SetParameter } && w1Steps.Length == 1 && w1Steps[0].Command == "element.set_parameter",
            "a wall changed only in height restores with element.set_parameter", heightOnly.ToJson());
        if (w1Steps.Length == 1)
        {
            var args = Args(w1Steps[0]);
            Check(args.GetProperty("element_uid").GetString() == W1 && args.GetProperty("parameter").GetProperty("built_in").GetString() == "WALL_USER_HEIGHT_PARAM"
                && args.GetProperty("value").GetProperty("kind").GetString() == "length" && args.GetProperty("value").GetProperty("mm").GetDouble() == 3000,
                "the height is set back to Core's 3000 mm on the wall's unconnected height", w1Steps[0].ArgsJson);
        }
        Check(w1?.Adopt is { Action: MirrorDriftAdoptActions.Update, Element: { } tallElement } && Near(tallElement.GeometryMetres, new[] { 0, 0, 5, 0, 3.2, 0.2 }), "adopting the height change takes 3.2 m from the mirror");
        CatalogueValid(heightOnly, "height change");

        // ---- A host and its opening both missing: the opening takes the re-created host's UniqueId ----
        var bothGone = Snap(wall2, floor);
        var both = MirrorDriftResolve.Plan(MirrorDrift.Compare(bothGone, committed), bothGone, committed, Bindings());
        var hostSteps = StepsOf(both, Find(both, W1));
        var doorSteps = StepsOf(both, Find(both, D1));
        Check(hostSteps.Length == 1 && hostSteps[0].Command == "wall.create" && doorSteps.Length == 1 && doorSteps[0].Inputs.Count == 1
            && doorSteps[0].Inputs[0] == new RevitStepInput("args.host_wall_uid", hostSteps[0].OperationId, "created[0]") && doorSteps[0].DependsOn.Contains(hostSteps[0].OperationId)
            && Find(both, D1)!.Restore.Requires.SequenceEqual(new[] { W1 }), "a door whose host is re-created too takes the host's created UniqueId and requires its restore", both.ToJson());
        Check(Find(both, W1)?.Adopt is { Status: MirrorDriftResolveStatus.Proposed, Action: MirrorDriftAdoptActions.Delete } wallAdopt && wallAdopt.Requires.SequenceEqual(new[] { D1 }),
            "deleting a missing host from BIM-IR requires deleting its missing door too", both.ToJson());
        CatalogueValid(both, "host and opening missing");

        // ---- An element committed without a native id (no UniqueId) ----
        var unrealizedMap = committed.NativeIds.Where(pair => pair.Key != CoreD1).ToDictionary(pair => pair.Key, pair => pair.Value);
        var unrealizedModel = committed with { NativeIds = unrealizedMap };
        var noDoor = Snap(wall1, wall2, floor);
        var unrealized = MirrorDriftResolve.Plan(MirrorDrift.Compare(noDoor, unrealizedModel), noDoor, unrealizedModel, Bindings());
        Check(unrealized.Proposals.SingleOrDefault() is { UniqueId: "", FindingKind: MirrorDriftKinds.Missing, Adopt.Status: MirrorDriftResolveStatus.Proposed, Restore.Mode: MirrorDriftRestoreModes.Create } u && u.CoreId == CoreD1,
            "an unrealized element gets adopt (delete) and restore (create) proposals", unrealized.ToJson());

        // ---- Unsupported, typed ----
        var noWallType = MirrorDriftResolve.Plan(report, drifted, committed, Bindings() with { WallTypes = Array.Empty<RevitWallTypeBinding>() });
        var w2NoType = Find(noWallType, W2);
        Check(w2NoType?.Restore is { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.WallTypeUnbound, Operations.Count: 0 }
            && noWallType.RestorePlan.Steps.All(step => step.ElementId != CoreW2) && noWallType.RestorePlan.Unsupported.Any(item => item.ElementId == CoreW2 && item.Code == MirrorDriftResolveCodes.WallTypeUnbound),
            "a wall with no bound wall type cannot be restored and is listed unsupported, without a half replace", noWallType.ToJson());
        var noSymbol = MirrorDriftResolve.Plan(report, drifted, committed, Bindings() with { DoorSymbol = null });
        Check(Find(noSymbol, D1)?.Restore is { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.SymbolUnbound }, "a door with no bound family type cannot be restored", noSymbol.ToJson());
        // A level missing in Revit: no catalogue command creates levels.
        var level2 = Guid.Parse("c0000000-0000-4000-8000-000000000010");
        var withLevel2 = committed with
        {
            Project = committed.Project with { Elements = committed.Project.Elements.Append(new Element(level2, "level", new[] { 4.0 }, Array.Empty<Guid>(), new Provenance("intent", "approved"))).ToArray() },
            NativeIds = committed.NativeIds.Append(new KeyValuePair<Guid, string>(level2, P + "00000138")).ToDictionary(pair => pair.Key, pair => pair.Value),
        };
        var levelGone = MirrorDriftResolve.Plan(MirrorDrift.Compare(baseline, withLevel2), baseline, withLevel2, Bindings());
        Check(levelGone.Proposals.SingleOrDefault() is { ElementKind: "level", Recommended: MirrorDriftResolveActions.Adopt, Restore: { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.KindUnsupported }, Adopt: { Status: MirrorDriftResolveStatus.Proposed, Action: MirrorDriftAdoptActions.Delete } }
            && levelGone.RestorePlan.Unsupported.Single().Code == MirrorDriftResolveCodes.KindUnsupported, "a missing level is unsupported to restore, so adopt is recommended", levelGone.ToJson());
        // The level remapped to a UniqueId Revit does not have: walls reference an unowned level (adopt unsupported), the
        // committed level is missing with dependents (adopt unsupported) and no level can be bound (restore unsupported).
        var remap = committed.NativeIds.ToDictionary(pair => pair.Key, pair => pair.Key == CoreL ? P + "00000139" : pair.Value);
        var remapped = committed with { NativeIds = remap };
        var levelReport = MirrorDrift.Compare(baseline, remapped);
        var levelPlan = MirrorDriftResolve.Plan(levelReport, baseline, remapped, Bindings());
        Check(Find(levelPlan, P + "00000139") is { FindingKind: MirrorDriftKinds.Missing, Recommended: MirrorDriftResolveActions.None, Adopt: { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.DependentsRemain }, Restore.Code: MirrorDriftResolveCodes.KindUnsupported },
            "a missing level other elements still depend on cannot be adopted or restored", levelPlan.ToJson());
        Check(Find(levelPlan, W1) is { Adopt: { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.DependencyUnmapped }, Restore: { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.LevelUnbound }, Recommended: MirrorDriftResolveActions.None },
            "a wall on an unowned Revit level cannot be adopted and without a level binding cannot be restored", levelPlan.ToJson());
        var boundLevel = MirrorDriftResolve.Plan(levelReport, baseline, remapped, Bindings() with { Levels = new Dictionary<Guid, string> { [CoreL] = "Level 1" } });
        Check(Find(boundLevel, W2)?.Restore is { Status: MirrorDriftResolveStatus.Proposed, Mode: MirrorDriftRestoreModes.Replace } && StepsOf(boundLevel, Find(boundLevel, W2)).Last() is { Command: "wall.create" } create && Args(create).GetProperty("level").GetString() == "Level 1",
            "a level binding takes precedence over the map", boundLevel.ToJson());

        ReviewChecks(observation, committed, journal, report, resolution, heightOnly, taller);

        // ---- Refusals ----
        Check(Code(() => MirrorDriftResolve.Plan(MirrorDrift.Compare(drifted, committed, journal, options with { MaxFindings = 1 }), drifted, committed, Bindings())) == MirrorDriftResolveCodes.ReportTruncated,
            "a truncated report is refused");
        Check(Code(() => MirrorDriftResolve.Plan(report, drifted, committed with { Project = committed.Project with { Revision = 4 } }, Bindings())) == MirrorDriftResolveCodes.ReportStale,
            "a report of another Core revision is refused");
        Check(Code(() => MirrorDriftResolve.Plan(report, baseline, committed, Bindings())) == MirrorDriftResolveCodes.ReportInvalid, "a report whose findings are not in the given snapshot is refused");
        Check(Code(() => MirrorDriftResolve.Plan(report, drifted, committed, Bindings() with { Document = new RevitDocumentRef(Document.Path, OtherDoc) })) == MirrorDriftResolveCodes.DocumentMismatch,
            "bindings for another document are refused");
        Check(Code(() => MirrorDriftResolve.Plan(report, drifted, committed, Bindings() with { Document = new RevitDocumentRef("relative.rvt", Doc) })) == RevitCommandCodes.DocumentPathInvalid,
            "a relative document path is refused");

        // ---- Deterministic: two runs, permuted inputs, another culture ----
        var json = resolution.ToJson();
        Check(MirrorDriftResolve.Plan(report, drifted, committed, Bindings()).ToJson() == json, "two runs give byte-identical resolutions");
        var permutedSnapshot = Snap(floor, Wall(wall1, W3, 0, 0, 0, 4000), Wall(wall2, W2, 5050, 0, 5050, 4000), wall1);
        var permutedModel = committed with
        {
            Project = committed.Project with { Elements = committed.Project.Elements.Reverse().ToArray() },
            NativeIds = committed.NativeIds.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value),
        };
        var permuted = MirrorDriftResolve.Plan(MirrorDrift.Compare(permutedSnapshot, permutedModel, journal.Reverse().ToArray(), options), permutedSnapshot, permutedModel, Bindings());
        Check(permuted.ToJson() == json,
            "permuted snapshot, model, map and journal give the same resolution", permuted.ToJson());
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Check(MirrorDriftResolve.Plan(report, drifted, committed, Bindings()).ToJson() == json, "the resolution is byte-identical under de-DE");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        using (var parsed = JsonDocument.Parse(json))
        {
            var root = parsed.RootElement;
            Check(root.GetProperty("resolution_version").GetInt32() == 1 && root.GetProperty("document_unique_id").GetString() == Doc && root.GetProperty("core_revision").GetInt32() == 3
                && !root.GetProperty("applied").GetBoolean() && root.GetProperty("requires_approval").GetBoolean() && root.GetProperty("proposals").GetArrayLength() == 3
                && root.GetProperty("restore_plan").GetProperty("steps").GetArrayLength() == 4
                && root.GetProperty("proposals")[0].GetProperty("adopt").GetProperty("element").GetProperty("id").GetString() == CoreW2.ToString("D")
                && root.GetProperty("proposals")[0].GetProperty("restore").GetProperty("operations").GetArrayLength() == 2,
                "resolution JSON says it is not applied and carries proposals and the restore plan", json);
        }
    }
    // Independent review: operation ids follow the observed Revit state; replaces and deletes never take hosted or
    // dependent elements with them silently; malformed snapshot geometry and malformed reports are refused typed.
    static void ReviewChecks(MirrorObservation observation, MirrorCommittedModel committed, IReadOnlyList<MirrorJournalEntry> journal, MirrorDriftReport report,
        MirrorDriftResolution resolution, MirrorDriftResolution heightOnly, MirrorSnapshot taller)
    {
        MirrorSnapshot Snap(MirrorObservation source, params MirrorObservedElement[] elements) => MirrorSnapshotMapper.Map(source with { Elements = elements });
        var wall1 = observation.Elements.Single(element => element.UniqueId == W1);
        var wall2 = observation.Elements.Single(element => element.UniqueId == W2);
        var door = observation.Elements.Single(element => element.UniqueId == D1);
        var floor = observation.Elements.Single(element => element.UniqueId == F1);

        // (blocker) The same Core revision with another observed value is another request: new operation ids.
        var higher = Snap(observation, wall1 with { HeightMm = 3400 }, wall2, door, floor);
        var heightAgain = MirrorDriftResolve.Plan(MirrorDrift.Compare(higher, committed), higher, committed, Bindings());
        var firstIds = heightOnly.RestorePlan.Steps.Select(step => step.OperationId).ToArray();
        var againIds = heightAgain.RestorePlan.Steps.Select(step => step.OperationId).ToArray();
        Check(firstIds.Length == 1 && againIds.Length == 1 && firstIds[0] != againIds[0] && heightAgain.RestorePlan.Steps[0].ArgsJson == heightOnly.RestorePlan.Steps[0].ArgsJson,
            "the same restore command planned against another observed value (3.4 m instead of 3.2 m) gets another operation id", string.Join(",", firstIds) + " | " + string.Join(",", againIds));
        var sameIds = MirrorDriftResolve.Plan(MirrorDrift.Compare(taller, committed), taller, committed, Bindings()).RestorePlan.Steps.Select(step => step.OperationId).ToArray();
        Check(sameIds.SequenceEqual(firstIds), "identical inputs keep identical operation ids");
        // An element deleted again after a restore realized it under a new UniqueId is another delete-and-create request.
        var noDoor = Snap(observation, wall1, wall2, floor);
        var deletedOnce = MirrorDriftResolve.Plan(MirrorDrift.Compare(noDoor, committed), noDoor, committed, Bindings());
        const string D1Again = P + "00002002";
        var realized = committed with { NativeIds = committed.NativeIds.ToDictionary(pair => pair.Key, pair => pair.Key == CoreD1 ? D1Again : pair.Value) };
        var deletedTwice = MirrorDriftResolve.Plan(MirrorDrift.Compare(noDoor, realized), noDoor, realized, Bindings());
        Check(deletedOnce.RestorePlan.Steps.Count == 1 && deletedTwice.RestorePlan.Steps.Count == 1 && deletedOnce.RestorePlan.Steps[0].OperationId != deletedTwice.RestorePlan.Steps[0].OperationId,
            "a door deleted again after its restore was realized gets a new operation id", deletedOnce.ToJson() + " | " + deletedTwice.ToJson());
        // A snapshot that differs elsewhere (another element moved) also changes the ids of the shared restore steps.
        var elsewhere = Snap(observation, wall1 with { HeightMm = 3200 }, wall2 with { HeightMm = 3300 }, door, floor);
        var elsewherePlan = MirrorDriftResolve.Plan(MirrorDrift.Compare(elsewhere, committed), elsewhere, committed, Bindings());
        var w1Elsewhere = StepsOf(elsewherePlan, Find(elsewherePlan, W1));
        Check(w1Elsewhere.Length == 1 && w1Elsewhere[0].OperationId != firstIds[0], "operation ids follow the whole observed snapshot", elsewherePlan.ToJson());

        // (1) Replacing a host wall would delete its unchanged hosted door: refused, no half replace.
        var longer = Snap(observation, Wall(wall1, W1, 0, 0, 6000, 0), wall2, door, floor);
        var hostReport = MirrorDrift.Compare(longer, committed);
        var hostPlan = MirrorDriftResolve.Plan(hostReport, longer, committed, Bindings());
        Check(hostReport.Findings.Select(finding => finding.UniqueId).SequenceEqual(new[] { W1 }) && Find(hostPlan, W1)?.Restore is { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.DependentsRemain }
            && hostPlan.RestorePlan.Steps.Count == 0, "a host wall with an unchanged hosted door in Revit is not replaced (dependents_remain)", hostPlan.ToJson());
        Check(Find(hostPlan, W1)?.Adopt.Status == MirrorDriftResolveStatus.Proposed, "the lengthened host wall can still be adopted");

        // (2) Adopt refuses malformed snapshot geometry like restore does.
        var drifted = Snap(observation, wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), Wall(wall1, W3, 0, 0, 0, 4000), floor);
        var w2Snapshot = MirrorSnapshotMapper.Id(Doc, W2);
        var w3Snapshot = MirrorSnapshotMapper.Id(Doc, W3);
        var malformed = drifted with { Elements = drifted.Elements.Select(element => element.Id == w2Snapshot || element.Id == w3Snapshot ? element with { GeometryMetres = new[] { 5.05, 0, 5.05, 4, 3 } } : element).ToArray() };
        var malformedReport = MirrorDrift.Compare(malformed, committed, options: new MirrorDriftOptions(Scope: MirrorDriftScopes.Document));
        var malformedPlan = MirrorDriftResolve.Plan(malformedReport, malformed, committed, Bindings());
        Check(Find(malformedPlan, W2)?.Adopt is { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.GeometryInvalid }
            && Find(malformedPlan, W3)?.Adopt is { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.GeometryInvalid },
            "a changed or extra wall whose snapshot geometry has five values cannot be adopted (geometry_invalid)", malformedPlan.ToJson());

        // (3) Two findings on one committed element are a malformed report.
        var missingD1 = MirrorDrift.Compare(noDoor, committed);
        var duplicated = missingD1 with { Findings = missingD1.Findings.Append(missingD1.Findings[0] with { UniqueId = P + "00002999" }).ToArray() };
        Check(Code(() => MirrorDriftResolve.Plan(duplicated, noDoor, committed, Bindings())) == MirrorDriftResolveCodes.ReportInvalid, "duplicate committed ids in a report are refused typed");

        // (4) Deleting an extra level other Revit elements stand on is refused.
        const string L2 = P + "00000138";
        var withLevel2 = observation with { Levels = observation.Levels.Append(new MirrorObservedLevel(L2, "Level 2", 4000)).ToArray() };
        var onLevel2 = Snap(withLevel2, wall1, wall2 with { LevelUid = L2 }, door, floor);
        var levelExtra = MirrorDriftResolve.Plan(MirrorDrift.Compare(onLevel2, committed, options: new MirrorDriftOptions(Scope: MirrorDriftScopes.Document)), onLevel2, committed, Bindings());
        Check(Find(levelExtra, L2) is { FindingKind: MirrorDriftKinds.Extra, Restore: { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.DependentsRemain } }
            && levelExtra.RestorePlan.Steps.All(step => !step.ArgsJson.Contains(L2, StringComparison.OrdinalIgnoreCase)),
            "an extra level a kept wall stands on is not deleted (dependents_remain)", levelExtra.ToJson());
        // An extra wall and the extra door it hosts are both deleted, the door first.
        const string D9 = P + "00002009";
        var hostedExtra = Snap(observation, wall1, wall2, door, floor, Wall(wall1, W3, 0, 0, 0, 4000), door with { UniqueId = D9, HostUid = W3, Location = new MirrorPoint(0, 1000) });
        var extraPlan = MirrorDriftResolve.Plan(MirrorDrift.Compare(hostedExtra, committed, options: new MirrorDriftOptions(Scope: MirrorDriftScopes.Document)), hostedExtra, committed, Bindings());
        Check(Find(extraPlan, W3)?.Restore.Status == MirrorDriftResolveStatus.Proposed && Find(extraPlan, D9)?.Restore.Status == MirrorDriftResolveStatus.Proposed
            && extraPlan.RestorePlan.Steps.Select(step => Strings(Args(step).GetProperty("element_uids"))).SequenceEqual(new[] { D9, W3 }),
            "an extra wall whose hosted door is also extra is deleted after the door", extraPlan.ToJson());

        // (5) A changed finding without fields is refused, not replaced.
        var fieldless = report with { Findings = report.Findings.Select(finding => finding.UniqueId == W2 ? finding with { Fields = Array.Empty<MirrorDriftField>() } : finding).ToArray() };
        var drifted2 = Snap(observation, wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), Wall(wall1, W3, 0, 0, 0, 4000), floor);
        Check(Find(MirrorDriftResolve.Plan(fieldless, drifted2, committed, Bindings()), W2)?.Restore is { Status: MirrorDriftResolveStatus.Unsupported, Code: MirrorDriftResolveCodes.FieldsMissing },
            "a changed finding without fields is refused (fields_missing)");
        // AfterHash is Core's committed state independent of element order.
        var permuted = committed with { Project = committed.Project with { Elements = committed.Project.Elements.Reverse().ToArray() } };
        var permutedPlan = MirrorDriftResolve.Plan(MirrorDrift.Compare(drifted2, permuted, journal, new MirrorDriftOptions(JournalAfterSequence: 4)), drifted2, permuted, Bindings());
        Check(permutedPlan.RestorePlan.AfterHash == resolution.RestorePlan.AfterHash && permutedPlan.RestorePlan.AfterHash == Compiler.Hash(committed.Project)
            && Compiler.Hash(permuted.Project) != Compiler.Hash(committed.Project), "after_hash hashes the committed elements in id order whatever their stored order");
    }
}
