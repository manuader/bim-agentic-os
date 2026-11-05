using Bim.Core;
using System.Text.Json;
// P2-09 selective drift resolution oracle: MirrorDriftSelect.Select (core/Bim.Core/MirrorDriftSelect.cs) takes a P2-07
// resolution and a chosen subset of proposals with adopt or restore each, and returns the BIM-IR operations, the
// restore plan filtered to the chosen restores (same operation ids, checked against the catalogue through
// PlanToCommands.Envelope) and the map entries a replace or re-create invalidates. No Revit is involved: a passing run
// is unit-tested evidence only.
static class MirrorDriftSelectChecks
{
    const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string P = "60f91daf-3dd7-4283-a86d-24137b73f3da-";
    const string L1 = P + "00000137";
    const string W1 = P + "00001001";
    const string W2 = P + "00001002";
    const string W3 = P + "00001003";
    const string D1 = P + "00002001";
    const string F1 = P + "00004001";
    const string Created = P + "0000ffff";
    const string Adopt = MirrorDriftResolveActions.Adopt;
    const string Restore = MirrorDriftResolveActions.Restore;
    static readonly Guid CoreL = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    static readonly Guid CoreW1 = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    static readonly Guid CoreW2 = Guid.Parse("c0000000-0000-4000-8000-000000000003");
    static readonly Guid CoreD1 = Guid.Parse("c0000000-0000-4000-8000-000000000004");
    static readonly RevitDocumentRef Document = new(@"C:\Repo\.bim\native-runs\p2-09\copy.rvt", Doc);
    static RevitBindings Bindings() => new(Document, new Dictionary<Guid, string>(), new[] { new RevitWallTypeBinding(0.2, "Generic - 200mm") }, "Single-Flush: 0915 x 2134mm", "Fixed: 1000 x 1200mm");

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, "drift-select: " + name, detail);
    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (ArgumentException error) { return error.Message.Split(' ')[0]; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }
    static string Describe(Func<MirrorDriftSelection> select)
    {
        try { return select().ToJson(); }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
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

    static MirrorDriftChoice C(string id, string action) => new(id, action);
    static MirrorDriftChoice[] Cs(params (string Id, string Action)[] items) => items.Select(item => C(item.Id, item.Action)).ToArray();

    static MirrorDriftProposal Find(MirrorDriftResolution resolution, string uid) => resolution.Proposals.Single(proposal => proposal.UniqueId == uid);

    static string[] Ops(MirrorDriftSelection selection) => selection.RestorePlan.Steps.Select(step => step.OperationId).ToArray();

    // Every kept step is the resolution's step unchanged, parses as a catalogue command once its inputs are filled and
    // runs after the steps it depends on and takes inputs from.
    static void CatalogueValid(MirrorDriftResolution resolution, MirrorDriftSelection selection, string label)
    {
        var plan = selection.RestorePlan;
        var original = resolution.RestorePlan.Steps.ToDictionary(step => step.OperationId, StringComparer.Ordinal);
        var created = plan.Steps.ToDictionary(step => step.OperationId, _ => Created, StringComparer.Ordinal);
        var failures = new List<string>();
        var position = plan.Steps.Select((step, index) => (step.OperationId, index)).ToDictionary(pair => pair.OperationId, pair => pair.index, StringComparer.Ordinal);
        var order = resolution.RestorePlan.Steps.Select(step => step.OperationId).Where(position.ContainsKey).ToArray();
        if (!order.SequenceEqual(plan.Steps.Select(step => step.OperationId))) failures.Add("kept steps are not in the resolution's order");
        foreach (var step in plan.Steps)
        {
            if (!original.TryGetValue(step.OperationId, out var same) || !ReferenceEquals(same, step) && JsonSerializer.Serialize(same) != JsonSerializer.Serialize(step)) failures.Add(step.OperationId + " is not the resolution's step");
            if (Code(() => PlanToCommands.Envelope(plan, step, created)) is { } code) failures.Add(step.Command + ": " + code);
            if (step.DependsOn.Concat(step.Inputs.Select(input => input.FromOperation)).Any(dependency => !position.TryGetValue(dependency, out var at) || at >= position[step.OperationId]))
                failures.Add(step.OperationId + " depends on a later or dropped step");
        }
        if (plan.ProjectId != resolution.RestorePlan.ProjectId || plan.ExpectedRevision != resolution.RestorePlan.ExpectedRevision || plan.AfterHash != resolution.RestorePlan.AfterHash
            || plan.Document != resolution.RestorePlan.Document || plan.Version != resolution.RestorePlan.Version) failures.Add("plan header differs from the resolution's");
        Check(failures.Count == 0, label + ": the selected restore plan keeps the resolution's steps, order, ids and binding and validates against the catalogue", string.Join("; ", failures));
    }

    // The committed model with the selection's operations and map changes applied (what a reviewer would commit).
    static MirrorCommittedModel Applied(MirrorCommittedModel committed, MirrorDriftSelection selection)
    {
        var elements = committed.Project.Elements.ToDictionary(element => element.Id);
        var map = committed.NativeIds.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var operation in selection.Operations)
            if (operation.Action == "delete") elements.Remove(operation.Element.Id); else elements[operation.Element.Id] = operation.Element;
        foreach (var entry in selection.MapRemovals) map.Remove(Guid.Parse(entry.Key));
        foreach (var entry in selection.MapAdds) map[Guid.Parse(entry.Key)] = entry.UniqueId;
        return committed with
        {
            Project = committed.Project with { Elements = elements.Values.OrderBy(element => element.Id.ToString("D"), StringComparer.Ordinal).ToArray() },
            NativeIds = map,
        };
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

        // The drifted fixture: W2 moved (add-in pending), W3 added (external), D1 deleted (external).
        var drifted = Snap(wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), Wall(wall1, W3, 0, 0, 0, 4000), floor);
        var report = MirrorDrift.Compare(drifted, committed, journal, options);
        var resolution = MirrorDriftResolve.Plan(report, drifted, committed, Bindings());
        var resolutionJson = resolution.ToJson();
        Check(resolution.Proposals.Count == 3 && resolution.RestorePlan.Steps.Count == 4, "the P2-07 fixture resolution has three proposals and four restore steps", resolutionJson);
        Check(MirrorDriftSelect.ProposalId(Find(resolution, W2)) == W2, "a proposal id is its UniqueId");

        // ---- Mixed: adopt W2, restore W3 and D1 ----
        MirrorDriftSelection? mixed = null;
        Check(Code(() => mixed = MirrorDriftSelect.Select(resolution, Cs((W2, Adopt), (W3, Restore), (D1, Restore)))) is null, "a mixed selection is accepted", Describe(() => MirrorDriftSelect.Select(resolution, Cs((W2, Adopt), (W3, Restore), (D1, Restore)))));
        if (mixed is not null)
        {
            Check(mixed.Operations.Count == 1 && mixed.Operations[0].Action == "update" && mixed.Operations[0].Element == Find(resolution, W2).Adopt.Element,
                "adopting W2 gives one BIM-IR update with the proposal's element", mixed.ToJson());
            var expected = Find(resolution, W3).Restore.Operations.Concat(Find(resolution, D1).Restore.Operations).ToHashSet(StringComparer.Ordinal);
            Check(Ops(mixed).Length == 2 && Ops(mixed).All(expected.Contains) && mixed.RestorePlan.Steps.Select(step => step.Command).SequenceEqual(new[] { "element.delete", "opening.place" }),
                "restoring W3 and D1 keeps exactly their delete and opening.place steps", mixed.ToJson());
            Check(mixed.RestorePlan.Steps.All(step => step.ElementId != CoreW2), "the adopted W2 has no restore step");
            CatalogueValid(resolution, mixed, "mixed");
            Check(mixed.Invalidated.Count == 1 && mixed.Invalidated[0] is { Mode: MirrorDriftRestoreModes.Create, UniqueId: D1, ProposalId: D1 } inv
                && inv.Key == CoreD1.ToString("D") && inv.ByOperation == Find(resolution, D1).Restore.Operations.Last(),
                "re-creating the missing D1 invalidates its map entry, named by the step whose created[0] is the new UniqueId", mixed.ToJson());
            Check(mixed.MapAdds.Count == 0 && mixed.MapRemovals.Count == 0 && mixed.Omitted.Count == 0 && !mixed.Applied && mixed.RequiresApproval
                && mixed.DocumentUid == Doc && mixed.CoreRevision == 3 && mixed.CoreProjectId == committed.Project.Id, "a mixed selection is a proposal: no map changes, nothing omitted, not applied", mixed.ToJson());
        }
        Check(resolution.ToJson() == resolutionJson, "selecting never changes the resolution");

        // ---- A replace invalidates the replaced element's map entry ----
        var replace = Accept(() => MirrorDriftSelect.Select(resolution, Cs((W2, Restore))), "restoring W2 alone");
        if (replace is not null)
        {
            var w2Ops = Find(resolution, W2).Restore.Operations;
            Check(Ops(replace).SequenceEqual(w2Ops) && replace.RestorePlan.Steps.Select(step => step.Command).SequenceEqual(new[] { "element.delete", "wall.create" }),
                "restoring the moved W2 keeps its delete then wall.create", replace.ToJson());
            Check(replace.Invalidated.SingleOrDefault() is { Mode: MirrorDriftRestoreModes.Replace, UniqueId: W2, ProposalId: W2 } r && r.Key == CoreW2.ToString("D") && r.ByOperation == w2Ops[1],
                "replacing W2 invalidates the CoreW2 -> W2 map entry, renewed by the wall.create step", replace.ToJson());
            Check(replace.Operations.Count == 0 && replace.Omitted.SequenceEqual(new[] { W3, D1 }), "the unchosen proposals are omitted in proposal order", replace.ToJson());
            CatalogueValid(resolution, replace, "replace");
        }

        // ---- Adopt everything: a valid model without drift ----
        var adoptAll = Accept(() => MirrorDriftSelect.Select(resolution, Cs((D1, Adopt), (W3, Adopt), (W2, Adopt))), "adopting all");
        if (adoptAll is not null)
        {
            Check(adoptAll.Operations.Select(operation => (operation.Action, operation.Element.Id)).SequenceEqual(new[] { ("update", CoreW2), ("create", MirrorSnapshotMapper.Id(Doc, W3)), ("delete", CoreD1) }),
                "adopting all gives update, create and delete in proposal order", adoptAll.ToJson());
            Check(adoptAll.MapAdds.SingleOrDefault() is { UniqueId: W3, ProposalId: W3 } add && add.Key == MirrorSnapshotMapper.Id(Doc, W3).ToString("D")
                && adoptAll.MapRemovals.SingleOrDefault() is { UniqueId: D1 } removal && removal.Key == CoreD1.ToString("D"),
                "an adopted create adds its map entry and an adopted delete removes its own", adoptAll.ToJson());
            Check(adoptAll.RestorePlan.Steps.Count == 0 && adoptAll.Invalidated.Count == 0 && adoptAll.RestorePlan.Unsupported.Count == 0, "adopting all restores nothing and invalidates nothing", adoptAll.ToJson());
            var model = Applied(committed, adoptAll);
            Check(Compiler.Validate(model.Project).Length == 0 && !MirrorDrift.Compare(drifted, model, journal, options).HasDrift,
                "applying the adopted operations and map changes yields a valid model without drift", MirrorDrift.Compare(drifted, model, journal, options).ToJson());
        }
        // Partial adopt keeps a valid model and leaves exactly the unchosen drift.
        var partial = Accept(() => MirrorDriftSelect.Select(resolution, Cs((W3, Adopt))), "adopting W3 alone");
        if (partial is not null)
        {
            var model = Applied(committed, partial);
            var left = MirrorDrift.Compare(drifted, model, journal, options);
            Check(Compiler.Validate(model.Project).Length == 0 && left.Findings.Select(finding => finding.UniqueId).Order(StringComparer.Ordinal).SequenceEqual(new[] { W2, D1 }.Order(StringComparer.Ordinal)),
                "adopting W3 alone gives a valid model that still drifts only on W2 and D1", left.ToJson());
        }

        // ---- Coupling: W1 (host) and D1 (its door) both missing ----
        var bothGone = Snap(wall2, floor);
        var both = MirrorDriftResolve.Plan(MirrorDrift.Compare(bothGone, committed), bothGone, committed, Bindings());
        Check(Find(both, D1).Restore.Requires.SequenceEqual(new[] { W1 }) && Find(both, W1).Adopt.Requires.SequenceEqual(new[] { D1 }), "the coupled fixture has restore D1 -> W1 and adopt W1 -> D1", both.ToJson());
        MirrorDriftSelectException? incomplete = null;
        try { MirrorDriftSelect.Select(both, Cs((D1, Restore))); }
        catch (MirrorDriftSelectException error) { incomplete = error; }
        Check(incomplete is { Code: MirrorDriftSelectCodes.RequiresIncomplete, ProposalId: D1 } && incomplete.Missing.SequenceEqual(new[] { W1 }) && incomplete.Message.StartsWith(MirrorDriftSelectCodes.RequiresIncomplete + " ", StringComparison.Ordinal),
            "restoring the door without its re-created host is refused (requires_incomplete, missing W1)", incomplete?.Message ?? "no refusal");
        Check(Code(() => MirrorDriftSelect.Select(both, Cs((D1, Restore), (W1, Adopt)))) == MirrorDriftSelectCodes.RequiresIncomplete,
            "restoring the door while adopting the host's deletion is refused");
        Check(Code(() => MirrorDriftSelect.Select(both, Cs((W1, Adopt)))) == MirrorDriftSelectCodes.RequiresIncomplete, "deleting the host from BIM-IR without its door is refused");
        Check(Code(() => MirrorDriftSelect.Select(both, Cs((W1, Adopt), (D1, Restore)))) == MirrorDriftSelectCodes.RequiresIncomplete, "adopting the host deletion while restoring its door is refused");
        var coupled = Accept(() => MirrorDriftSelect.Select(both, Cs((D1, Restore), (W1, Restore))), "restoring host and door");
        if (coupled is not null)
        {
            var wallStep = coupled.RestorePlan.Steps.FirstOrDefault(step => step.Command == "wall.create");
            var doorStep = coupled.RestorePlan.Steps.FirstOrDefault(step => step.Command == "opening.place");
            Check(coupled.RestorePlan.Steps.Count == 2 && wallStep is not null && doorStep is not null && doorStep.Inputs.SingleOrDefault()?.FromOperation == wallStep.OperationId,
                "restoring host and door keeps the wall.create the door takes its host UniqueId from", coupled.ToJson());
            Check(coupled.Invalidated.Select(item => (item.UniqueId, item.Mode)).SequenceEqual(new[] { (W1, MirrorDriftRestoreModes.Create), (D1, MirrorDriftRestoreModes.Create) }),
                "both re-created elements invalidate their map entries, in proposal order", coupled.ToJson());
            CatalogueValid(both, coupled, "coupled restore");
        }
        var coupledAdopt = Accept(() => MirrorDriftSelect.Select(both, Cs((W1, Adopt), (D1, Adopt))), "adopting host and door deletions");
        if (coupledAdopt is not null)
            Check(coupledAdopt.Operations.All(operation => operation.Action == "delete") && coupledAdopt.Operations.Count == 2 && Compiler.Validate(Applied(committed, coupledAdopt).Project).Length == 0,
                "adopting both deletions gives a valid model", coupledAdopt.ToJson());

        // ---- Unsupported sides, malformed choices, unknown ids ----
        var noWallType = MirrorDriftResolve.Plan(report, drifted, committed, Bindings() with { WallTypes = Array.Empty<RevitWallTypeBinding>() });
        Check(Code(() => MirrorDriftSelect.Select(noWallType, Cs((W2, Restore)))) == MirrorDriftSelectCodes.ActionUnsupported, "choosing an unsupported restore is refused (action_unsupported)");
        Check(Code(() => MirrorDriftSelect.Select(noWallType, Cs((W2, Adopt)))) is null, "the other side of the same proposal stays selectable");
        Check(Code(() => MirrorDriftSelect.Select(resolution, Cs(("60f91daf-3dd7-4283-a86d-24137b73f3da-00009999", Adopt)))) == MirrorDriftSelectCodes.ProposalUnknown, "an unknown proposal id is refused");
        Check(Code(() => MirrorDriftSelect.Select(resolution, Cs((W2, Adopt), (W2.ToUpperInvariant(), Restore)))) == MirrorDriftSelectCodes.ChoiceDuplicate, "two choices for one proposal (ignoring case) are refused");
        Check(Code(() => MirrorDriftSelect.Select(resolution, Cs((W2, MirrorDriftResolveActions.None)))) == MirrorDriftSelectCodes.ChoiceInvalid, "the action none is not a choice");
        Check(Code(() => MirrorDriftSelect.Select(resolution, Cs((W2, "Adopt")))) == MirrorDriftSelectCodes.ChoiceInvalid, "actions are exact");
        Check(Code(() => MirrorDriftSelect.Select(resolution, new MirrorDriftChoice[] { null! })) == MirrorDriftSelectCodes.ChoiceInvalid, "a null choice is refused");
        Check(Code(() => MirrorDriftSelect.Select(resolution, null!)) == MirrorDriftSelectCodes.ChoiceInvalid, "null choices are refused");
        Check(Code(() => MirrorDriftSelect.Select(resolution, Cs(("", Adopt)))) == MirrorDriftSelectCodes.ChoiceInvalid, "an empty proposal id is refused");
        var nothing = Accept(() => MirrorDriftSelect.Select(resolution, Array.Empty<MirrorDriftChoice>()), "an empty choice");
        Check(nothing is { Operations.Count: 0, RestorePlan.Steps.Count: 0, Invalidated.Count: 0 } && nothing.Omitted.SequenceEqual(new[] { W2, W3, D1 }), "an empty choice selects nothing and omits every proposal", nothing?.ToJson() ?? "");
        // A tampered resolution: a restore names an operation the plan does not have.
        var tampered = resolution with { Proposals = resolution.Proposals.Select(proposal => proposal.UniqueId == W3 ? proposal with { Restore = proposal.Restore with { Operations = new[] { "drift.0000000000000000ffffffffffffffff" } } } : proposal).ToArray() };
        Check(Code(() => MirrorDriftSelect.Select(tampered, Cs((W3, Restore)))) == MirrorDriftSelectCodes.ResolutionInvalid, "a restore naming an operation the plan lacks is a malformed resolution");
        var dropped = resolution with { RestorePlan = resolution.RestorePlan with { Steps = resolution.RestorePlan.Steps.Where(step => step.Command != "element.delete" || step.ElementId == CoreW2).ToArray() } };
        Check(Code(() => MirrorDriftSelect.Select(dropped, Cs((W2, Adopt)))) == MirrorDriftSelectCodes.ResolutionInvalid, "a plan whose steps do not match the proposals is a malformed resolution");
        var unknownRequire = resolution with { Proposals = resolution.Proposals.Select(proposal => proposal.UniqueId == W3 ? proposal with { Adopt = proposal.Adopt with { Requires = new[] { P + "00008888" } } } : proposal).ToArray() };
        Check(Code(() => MirrorDriftSelect.Select(unknownRequire, Cs((W2, Adopt)))) == MirrorDriftSelectCodes.ResolutionInvalid, "a Requires key that names no proposal is a malformed resolution");

        // ---- An unrealized element: proposal id core:<id> ----
        var unrealizedModel = committed with { NativeIds = committed.NativeIds.Where(pair => pair.Key != CoreD1).ToDictionary(pair => pair.Key, pair => pair.Value) };
        var noDoor = Snap(wall1, wall2, floor);
        var unrealized = MirrorDriftResolve.Plan(MirrorDrift.Compare(noDoor, unrealizedModel), noDoor, unrealizedModel, Bindings());
        var coreKey = "core:" + CoreD1.ToString("D");
        Check(unrealized.Proposals.Count == 1 && MirrorDriftSelect.ProposalId(unrealized.Proposals[0]) == coreKey, "an element without a UniqueId has the proposal id core:<id>", unrealized.ToJson());
        var unrealizedRestore = Accept(() => MirrorDriftSelect.Select(unrealized, Cs((coreKey.ToUpperInvariant(), Restore))), "restoring core:<id>");
        Check(unrealizedRestore is { RestorePlan.Steps.Count: 1, Invalidated.Count: 0 } && unrealizedRestore.Choices.Single().ProposalId == coreKey,
            "restoring an unrealized element creates it and invalidates no map entry; the choice carries the canonical id", unrealizedRestore?.ToJson() ?? "");

        // ---- Restoring every proposal of an all-restorable resolution is the resolution's plan ----
        var unknown = MirrorDriftResolve.Plan(MirrorDrift.Compare(drifted, committed, options: new MirrorDriftOptions(Scope: MirrorDriftScopes.Document)), drifted, committed, Bindings());
        var allRestore = Accept(() => MirrorDriftSelect.Select(unknown, unknown.Proposals.Select(proposal => C(MirrorDriftSelect.ProposalId(proposal), Restore)).ToArray()), "restoring all");
        Check(allRestore is not null && allRestore.RestorePlan.ToJson() == unknown.RestorePlan.ToJson(), "restoring every proposal gives the resolution's restore plan byte for byte",
            (allRestore?.RestorePlan.ToJson() ?? "") + " | " + unknown.RestorePlan.ToJson());

        // ---- Deterministic: any choice order, two runs, another culture; JSON shape ----
        var forward = Cs((W2, Adopt), (W3, Restore), (D1, Restore));
        var json = Describe(() => MirrorDriftSelect.Select(resolution, forward));
        Check(Describe(() => MirrorDriftSelect.Select(resolution, forward.Reverse().ToArray())) == json && Describe(() => MirrorDriftSelect.Select(resolution, forward)) == json,
            "permuted choices and a second run give byte-identical selections", json);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Check(Describe(() => MirrorDriftSelect.Select(resolution, forward)) == json, "the selection is byte-identical under de-DE");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            Check(root.GetProperty("selection_version").GetInt32() == 1 && !root.GetProperty("applied").GetBoolean() && root.GetProperty("requires_approval").GetBoolean()
                && root.GetProperty("choices").GetArrayLength() == 3 && root.GetProperty("choices")[0].GetProperty("proposal_id").GetString() == W2
                && root.GetProperty("operations")[0].GetProperty("action").GetString() == "update" && root.GetProperty("operations")[0].GetProperty("element").GetProperty("id").GetString() == CoreW2.ToString("D")
                && root.GetProperty("invalidated_map")[0].GetProperty("key").GetString() == CoreD1.ToString("D") && root.GetProperty("invalidated_map")[0].GetProperty("select").GetString() == "created[0]"
                && root.GetProperty("restore_plan").GetProperty("steps").GetArrayLength() == 2, "selection JSON carries choices, operations, invalidated map entries and the restore plan", json);
        }
        catch (Exception error) { Check(false, "selection JSON parses", error.GetType().Name + ": " + error.Message + " | " + json); }

        ReviewChecks(observation, committed, resolution, both);
    }

    static MirrorDriftSelectException? Refusal(Action action)
    {
        try { action(); return null; }
        catch (MirrorDriftSelectException error) { return error; }
        catch (Exception) { return null; }
    }

    // Independent review: deletes Revit cascades to unchosen elements, evidence, ordering, malformed resolutions.
    static void ReviewChecks(MirrorObservation observation, MirrorCommittedModel committed, MirrorDriftResolution resolution, MirrorDriftResolution both)
    {
        MirrorSnapshot Snap(MirrorObservation source, params MirrorObservedElement[] elements) => MirrorSnapshotMapper.Map(source with { Elements = elements });
        MirrorDriftResolution Plan(MirrorSnapshot snapshot) =>
            MirrorDriftResolve.Plan(MirrorDrift.Compare(snapshot, committed, options: new MirrorDriftOptions(Scope: MirrorDriftScopes.Document)), snapshot, committed, Bindings());
        var wall1 = observation.Elements.Single(element => element.UniqueId == W1);
        var wall2 = observation.Elements.Single(element => element.UniqueId == W2);
        var door = observation.Elements.Single(element => element.UniqueId == D1);
        var floor = observation.Elements.Single(element => element.UniqueId == F1);
        const string DX = P + "00002009";
        const string DY = P + "00002008";
        const string DZ = P + "00002007";
        const string L2 = P + "00000138";

        // (blocker 1) An extra wall hosting an extra door: restoring the wall alone would make Revit delete the door too.
        var hosted = Plan(Snap(observation, wall1, wall2, door, floor, Wall(wall1, W3, 0, 0, 0, 4000), door with { UniqueId = DX, HostUid = W3, Location = new MirrorPoint(0, 1000) }));
        var wx = hosted.Proposals.FirstOrDefault(proposal => proposal.UniqueId == W3);
        Check(wx?.Restore is { Status: MirrorDriftResolveStatus.Proposed, Mode: MirrorDriftRestoreModes.Delete } && wx.Restore.Requires.SequenceEqual(new[] { DX }),
            "restoring (deleting) an extra wall requires restoring its extra hosted door, which Revit deletes with it", hosted.ToJson());
        var alone = Refusal(() => MirrorDriftSelect.Select(hosted, Cs((W3, Restore))));
        Check(alone is { Code: MirrorDriftSelectCodes.RequiresIncomplete, ProposalId: W3 } && alone.Missing.SequenceEqual(new[] { DX }),
            "selecting the extra wall's restore without its hosted door is refused (requires_incomplete, missing the door)", alone?.Message ?? "accepted");
        var pair = Accept(() => MirrorDriftSelect.Select(hosted, Cs((W3, Restore), (DX, Restore))), "restoring the extra wall with its door");
        Check(pair is not null && pair.RestorePlan.Steps.Select(step => Args(step).GetProperty("element_uids")[0].GetString()).SequenceEqual(new[] { DX, W3 }),
            "restoring the extra wall with its door deletes the door first, then the wall", pair?.ToJson() ?? "");
        // Several hosted doors: the missing keys are sorted.
        var twoDoors = Plan(Snap(observation, wall1, wall2, door, floor, Wall(wall1, W3, 0, 0, 0, 4000),
            door with { UniqueId = DX, HostUid = W3, Location = new MirrorPoint(0, 1000) }, door with { UniqueId = DY, HostUid = W3, Location = new MirrorPoint(0, 3000) }));
        var several = Refusal(() => MirrorDriftSelect.Select(twoDoors, Cs((W3, Restore))));
        Check(several is { Code: MirrorDriftSelectCodes.RequiresIncomplete } && several.Missing.SequenceEqual(new[] { DY, DX }),
            "a wall hosting two extra doors misses both, sorted", several?.Message ?? "accepted");
        Check(Code(() => MirrorDriftSelect.Select(twoDoors, Cs((W3, Restore), (DX, Restore), (DY, Adopt)))) == MirrorDriftSelectCodes.RequiresIncomplete,
            "adopting one hosted door while deleting its wall is refused");
        // An extra level with an extra wall on it.
        var withLevel2 = observation with { Levels = observation.Levels.Append(new MirrorObservedLevel(L2, "Level 2", 4000)).ToArray() };
        var level = Plan(Snap(withLevel2, wall1, wall2, door, floor, Wall(wall1, W3, 0, 0, 0, 4000) with { LevelUid = L2 }));
        var l2 = level.Proposals.FirstOrDefault(proposal => proposal.UniqueId == L2);
        Check(l2?.Restore is { Status: MirrorDriftResolveStatus.Proposed } && l2.Restore.Requires.SequenceEqual(new[] { W3 }),
            "deleting an extra level requires deleting the extra wall on it", level.ToJson());
        Check(Code(() => MirrorDriftSelect.Select(level, Cs((L2, Restore)))) == MirrorDriftSelectCodes.RequiresIncomplete, "deleting the extra level alone is refused");
        // A replace of a changed wall hosting an extra door being deleted.
        var replaced = Plan(Snap(observation, wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), door, floor, door with { UniqueId = DZ, HostUid = W2, Location = new MirrorPoint(5050, 2000) }));
        var w2 = replaced.Proposals.FirstOrDefault(proposal => proposal.UniqueId == W2);
        Check(w2?.Restore is { Status: MirrorDriftResolveStatus.Proposed, Mode: MirrorDriftRestoreModes.Replace } && w2.Restore.Requires.SequenceEqual(new[] { DZ }),
            "replacing a wall whose extra hosted door is deleted requires that door's restore", replaced.ToJson());
        Check(Code(() => MirrorDriftSelect.Select(replaced, Cs((W2, Restore)))) == MirrorDriftSelectCodes.RequiresIncomplete, "replacing that wall alone is refused");

        // (2) Evidence completeness is carried.
        var incomplete = Accept(() => MirrorDriftSelect.Select(resolution with { EvidenceIncomplete = true }, Cs((W2, Adopt))), "an incomplete-evidence selection");
        var complete = Accept(() => MirrorDriftSelect.Select(resolution, Cs((W2, Adopt))), "a complete-evidence selection");
        Check(incomplete is { EvidenceIncomplete: true } && complete is not null && complete.EvidenceIncomplete == resolution.EvidenceIncomplete
            && incomplete.ToJson().Contains("\"evidence_incomplete\":true", StringComparison.Ordinal) && complete.ToJson().Contains("\"evidence_incomplete\":" + (resolution.EvidenceIncomplete ? "true" : "false"), StringComparison.Ordinal),
            "the selection carries the resolution's evidence_incomplete", incomplete?.ToJson() ?? "");

        // (4) Mixed selections state their order: restore plan first (bound to Core's revision), then the adopt commit.
        Check(complete is { Order: MirrorDriftSelection.RestoreThenAdopt } && MirrorDriftSelection.RestoreThenAdopt == "restore_then_adopt"
            && complete.ToJson().Contains("\"order\":\"restore_then_adopt\"", StringComparison.Ordinal), "a selection states the order restore_then_adopt", complete?.ToJson() ?? "");

        // (3) A kept step needing a dropped one is a malformed resolution even when Requires says nothing.
        var unrequired = both with { Proposals = both.Proposals.Select(proposal => proposal.UniqueId == D1 ? proposal with { Restore = proposal.Restore with { Requires = Array.Empty<string>() } } : proposal).ToArray() };
        Check(Code(() => MirrorDriftSelect.Select(unrequired, Cs((D1, Restore)))) == MirrorDriftSelectCodes.ResolutionInvalid, "a door step taking a dropped host step is refused (resolution_invalid)");
        // (5) A plan step that no proposal owns.
        var orphan = resolution with { RestorePlan = resolution.RestorePlan with { Steps = resolution.RestorePlan.Steps.Append(resolution.RestorePlan.Steps[0] with { OperationId = "drift.00000000000000000000000000000001" }).ToArray() } };
        Check(Code(() => MirrorDriftSelect.Select(orphan, Cs((W2, Adopt)))) == MirrorDriftSelectCodes.ResolutionInvalid, "a restore step no proposal lists is refused (resolution_invalid)");
        // Nits: null preconditions; non-finite geometry in an adopt element.
        var noPreconditions = resolution with { RestorePlan = resolution.RestorePlan with { Preconditions = null! } };
        Check(Code(() => MirrorDriftSelect.Select(noPreconditions, Cs((W2, Adopt)))) == MirrorDriftSelectCodes.ResolutionInvalid, "null preconditions are refused (resolution_invalid)");
        var nan = resolution with { Proposals = resolution.Proposals.Select(proposal => proposal.UniqueId == W2 ? proposal with { Adopt = proposal.Adopt with { Element = proposal.Adopt.Element! with { GeometryMetres = new[] { 5.05, 0, 5.05, double.NaN, 3, 0.2 } } } } : proposal).ToArray() };
        Check(Code(() => MirrorDriftSelect.Select(nan, Cs((W3, Adopt)))) == MirrorDriftSelectCodes.ResolutionInvalid, "an adopt element with NaN geometry is refused (resolution_invalid)");
    }

    static JsonElement Args(RevitCommandStep step)
    {
        using var parsed = JsonDocument.Parse(step.ArgsJson);
        return parsed.RootElement.Clone();
    }

    static MirrorDriftSelection? Accept(Func<MirrorDriftSelection> select, string label)
    {
        try { return select(); }
        catch (Exception error) { Check(false, label + " is accepted", error.GetType().Name + ": " + error.Message); return null; }
    }
}
