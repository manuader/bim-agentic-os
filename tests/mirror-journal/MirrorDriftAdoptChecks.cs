using Bim.Core;
using System.Text.Json;
// P2-12 commit adopted drift into Core oracle: MirrorDriftAdoptCommit.Commit (core/Bim.Core/MirrorDriftAdopt.cs) commits a
// P2-09 selection's adopt operations to a real ProjectStore (temporary directory) as one revision with a deterministic
// key; stale heads are refused before anything is written, replays return the head, restore-only selections commit
// nothing, and no provenance ledger is written. No Revit is involved: a passing run is unit-tested evidence only.
static class MirrorDriftAdoptChecks
{
    const string P = "60f91daf-3dd7-4283-a86d-24137b73f3da-";
    const string W1 = P + "00001001";
    const string W2 = P + "00001002";
    const string W3 = P + "00001003";
    const string D1 = P + "00002001";
    const string F1 = P + "00004001";
    const string Doc = P + "0001fd0b";
    const string Adopt = MirrorDriftResolveActions.Adopt;
    const string Restore = MirrorDriftResolveActions.Restore;
    static readonly Guid CoreW2 = Guid.Parse("c0000000-0000-4000-8000-000000000003");
    static readonly Guid CoreD1 = Guid.Parse("c0000000-0000-4000-8000-000000000004");
    static readonly RevitDocumentRef Document = new(@"C:\Repo\.bim\native-runs\p2-12\copy.rvt", Doc);
    static RevitBindings Bindings() => new(Document, new Dictionary<Guid, string>(), new[] { new RevitWallTypeBinding(0.2, "Generic - 200mm") }, "Single-Flush: 0915 x 2134mm", "Fixed: 1000 x 1200mm");
    static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, "drift-adopt: " + name, detail);
    static readonly List<string> Temporary = new();

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        try { Checks(); }
        catch (Exception error) { Check(false, "oracle threw", error.GetType().Name + ": " + error.Message); }
        finally { foreach (var directory in Temporary) try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // The refusal code (first word of a MirrorDriftAdoptException) or a description of what happened instead.
    static string Code(Func<MirrorDriftAdoptResult> action)
    {
        try { var result = action(); return "accepted " + result.Outcome + " revision " + result.Revision; }
        catch (MirrorDriftAdoptException error) { return error.Code == error.Message.Split(' ')[0] ? error.Code : "code/message mismatch: " + error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    static MirrorDriftAdoptResult? Accept(Func<MirrorDriftAdoptResult> action, string label)
    {
        try { return action(); }
        catch (Exception error) { Check(false, label + " is accepted", error.GetType().Name + ": " + error.Message); return null; }
    }

    static MirrorDriftSelection? Choose(MirrorDriftResolution resolution, string label, params (string Id, string Action)[] choices)
    {
        try { return MirrorDriftSelect.Select(resolution, choices.Select(item => new MirrorDriftChoice(item.Id, item.Action)).ToArray()); }
        catch (Exception error) { Check(false, label + " selection is accepted", error.GetType().Name + ": " + error.Message); return null; }
    }

    static string NewStore(Project project)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bim-p2-12-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(directory);
        new ProjectStore(directory).Initialize(project);
        foreach (var file in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(file, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        return directory;
    }

    // Every file under the store, by relative path: bytes and mtime.
    static SortedDictionary<string, (byte[] Bytes, DateTime Time)> Files(string directory)
    {
        var result = new SortedDictionary<string, (byte[], DateTime)>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return result;
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(directory, file)] = (File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file));
        return result;
    }

    static bool Same(SortedDictionary<string, (byte[] Bytes, DateTime Time)> a, SortedDictionary<string, (byte[] Bytes, DateTime Time)> b) =>
        a.Keys.SequenceEqual(b.Keys, StringComparer.Ordinal) && a.All(pair => pair.Value.Bytes.AsSpan().SequenceEqual(b[pair.Key].Bytes) && pair.Value.Time == b[pair.Key].Time);

    static string Names(SortedDictionary<string, (byte[] Bytes, DateTime Time)> files) => string.Join(",", files.Keys);

    static Dictionary<string, string> Receipts(string store) => JsonSerializer.Deserialize<ProjectStore.State>(File.ReadAllText(Path.Combine(store, "project.json")))!.Receipts;

    static string J<T>(T value) => JsonSerializer.Serialize(value);

    static string Canonical(Project project) => JsonSerializer.Serialize(project.Elements.OrderBy(element => element.Id.ToString("D"), StringComparer.Ordinal));

    static Project Applied(Project project, MirrorDriftSelection selection)
    {
        var elements = project.Elements.ToDictionary(element => element.Id);
        foreach (var operation in selection.Operations)
            if (operation.Action == "delete") elements.Remove(operation.Element.Id); else elements[operation.Element.Id] = operation.Element;
        return project with { Elements = elements.Values.ToArray() };
    }

    static MirrorObservedElement Wall(MirrorObservedElement template, string uid, double ax, double ay, double bx, double by) =>
        template with { UniqueId = uid, Start = new MirrorPoint(ax, ay), End = new MirrorPoint(bx, by) };

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
        var floor = observation.Elements.Single(element => element.UniqueId == F1);
        var options = new MirrorDriftOptions(JournalAfterSequence: 4);
        // The P2-07/P2-09 drifted fixture: W2 moved, W3 added, D1 deleted; Core at revision 3.
        var drifted = Snap(wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), Wall(wall1, W3, 0, 0, 0, 4000), floor);
        var resolution = MirrorDriftResolve.Plan(MirrorDrift.Compare(drifted, committed, journal, options), drifted, committed, Bindings());
        var head = committed.Project;
        Check(head.Revision == 3, "the fixture Core project is at revision 3");

        // ---- Adopt all: one revision through ProjectStore.Commit ----
        var adoptAll = Choose(resolution, "adopt all", (D1, Adopt), (W3, Adopt), (W2, Adopt));
        if (adoptAll is null) return;
        var key = "";
        try { key = MirrorDriftAdoptCommit.Key(adoptAll); }
        catch (Exception error) { Check(false, "the key is computed", error.GetType().Name + ": " + error.Message); }
        Check(key.StartsWith("mirror-drift-adopt:" + head.Id.ToString("D") + ":3:", StringComparison.Ordinal) && key.Length == "mirror-drift-adopt:".Length + 36 + 3 + 64
            && key[^64..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "the key is mirror-drift-adopt:<project>:<revision>:<sha256 of the canonical selection>", key);
        var store = NewStore(head);
        var before = Files(store);
        var first = Accept(() => MirrorDriftAdoptCommit.Commit(store, adoptAll), "adopting all");
        if (first is not null)
        {
            var stored = new ProjectStore(store).Read();
            Check(first.Outcome == MirrorDriftAdoptOutcomes.Committed && first.Revision == 4 && first.Key == key && stored.Revision == 4 && first.Head is not null && J(first.Head) == J(stored),
                "adopting all commits one revision (3 -> 4) under the selection key", first.Outcome + " " + first.Revision + " " + stored.Revision);
            Check(Canonical(stored) == Canonical(Applied(head, adoptAll)) && Compiler.Validate(stored).Length == 0,
                "the committed head is the fixture head with the adopted update, create and delete", JsonSerializer.Serialize(stored));
            Check(first.Plan is { ExpectedRevision: 3 } plan && plan.Operations.Select(operation => (operation.Action, operation.Element.Id)).OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal)
                .SequenceEqual(adoptAll.Operations.Select(operation => (operation.Action, operation.Element.Id)).OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal)),
                "the committed plan is exactly the adopted operations against revision 3", JsonSerializer.Serialize(first.Plan));
            var after = Files(store);
            var added = after.Keys.Except(before.Keys, StringComparer.Ordinal).ToArray();
            Check(added.Order(StringComparer.Ordinal).SequenceEqual(new[] { "checkpoint-3.json", "journal.jsonl" }) && after.Count == before.Count + 2,
                "the store gains exactly the checkpoint and journal Commit writes: no provenance ledger or other file", Names(after));
            Check(File.ReadAllLines(Path.Combine(store, "journal.jsonl")).Length == 1 && Receipts(store).Keys.SequenceEqual(new[] { key }), "one journal line and one receipt, under the key",
                string.Join(",", Receipts(store).Keys));
            Check(!after.Keys.Any(name => name.Contains("provenance", StringComparison.OrdinalIgnoreCase)), "no provenance file is written", Names(after));

            // ---- Replay: same selection (choices permuted, selected again) returns the head, no second revision ----
            var again = Choose(resolution, "replay", (W2, Adopt), (W3, Adopt), (D1, Adopt));
            if (again is not null)
            {
                Check(again.ToJson() == adoptAll.ToJson(), "the replayed selection is canonical-equal");
                var settled = Files(store);
                var replay = Accept(() => MirrorDriftAdoptCommit.Commit(store, again), "replaying");
                Check(replay is { Outcome: MirrorDriftAdoptOutcomes.Replayed, Revision: 4 } && replay.Key == key && replay.Head is not null && J(replay.Head) == J(new ProjectStore(store).Read()),
                    "a replay of the same selection returns the existing head (revision 4)", replay is null ? "" : replay.Outcome + " " + replay.Revision);
                Check(Same(settled, Files(store)), "a replay writes nothing", Names(Files(store)));
                Check(Code(() => MirrorDriftAdoptCommit.Commit(store, adoptAll)) == "accepted replayed revision 4" && Same(settled, Files(store)), "a third call is still a replay and writes nothing");
            }
        }

        // ---- Deterministic: two fresh stores, permuted choices -> byte-identical project.json and key ----
        var permuted = Choose(resolution, "permuted", (W3, Adopt), (W2, Adopt), (D1, Adopt));
        if (permuted is not null && first is not null)
        {
            var twin = NewStore(head);
            var second = Accept(() => MirrorDriftAdoptCommit.Commit(twin, permuted), "adopting all on a twin store");
            Check(second?.Key == key && File.ReadAllBytes(Path.Combine(twin, "project.json")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(store, "project.json")))
                && File.ReadAllBytes(Path.Combine(twin, "journal.jsonl")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(store, "journal.jsonl"))),
                "two stores committing the same selection hold byte-identical project.json and journal under the same key", second?.Key ?? "");
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Check(MirrorDriftAdoptCommit.Key(permuted) == key, "the key is the same under de-DE");
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        }

        // ---- Partial adopt: only W3; a different selection has a different key ----
        var partial = Choose(resolution, "adopt W3", (W3, Adopt));
        if (partial is not null)
        {
            Check(MirrorDriftAdoptCommit.Key(partial) != key, "a different selection has a different key");
            var partialStore = NewStore(head);
            var result = Accept(() => MirrorDriftAdoptCommit.Commit(partialStore, partial), "adopting W3");
            var stored = new ProjectStore(partialStore).Read();
            Check(result is { Outcome: MirrorDriftAdoptOutcomes.Committed, Revision: 4 } && stored.Elements.Length == head.Elements.Length + 1
                && stored.Elements.Count(element => element.Id == MirrorSnapshotMapper.Id(Doc, W3)) == 1 && J(stored.Elements.Single(element => element.Id == CoreW2)) == J(head.Elements.Single(element => element.Id == CoreW2))
                && stored.Elements.Any(element => element.Id == CoreD1), "adopting W3 alone creates only W3", JsonSerializer.Serialize(stored));
            // A mixed selection commits its adopt side only.
            var mixed = Choose(resolution, "mixed", (W2, Adopt), (W3, Restore), (D1, Restore));
            if (mixed is not null)
            {
                var mixedStore = NewStore(head);
                var mixedResult = Accept(() => MirrorDriftAdoptCommit.Commit(mixedStore, mixed), "a mixed selection");
                var mixedHead = new ProjectStore(mixedStore).Read();
                Check(mixedResult is { Outcome: MirrorDriftAdoptOutcomes.Committed, Revision: 4 } && mixedResult.Plan?.Operations.Length == 1 && Canonical(mixedHead) == Canonical(Applied(head, mixed)),
                    "a mixed selection commits only its adopted W2 update", JsonSerializer.Serialize(mixedResult?.Plan));
            }

            // ---- Stale revision: the store moved past the selection's revision (no receipt for this key) ----
            var stale = Files(partialStore);
            Check(Code(() => MirrorDriftAdoptCommit.Commit(partialStore, adoptAll)) == MirrorDriftAdoptCodes.StaleRevision, "a store at revision 4 refuses a revision-3 selection (stale_revision)",
                Code(() => MirrorDriftAdoptCommit.Commit(partialStore, adoptAll)));
            Check(Same(stale, Files(partialStore)) && new ProjectStore(partialStore).Read().Revision == 4, "nothing is written on a stale refusal", Names(Files(partialStore)));
        }

        // ---- Stale content: the store is at revision 3 but holds another model ----
        var moved = head with { Elements = head.Elements.Select(element => element.Id == CoreW2 ? element with { GeometryMetres = element.GeometryMetres.Select((value, i) => i == 0 ? value + 0.5 : value).ToArray() } : element).ToArray() };
        var movedStore = NewStore(moved);
        var movedFiles = Files(movedStore);
        Check(Code(() => MirrorDriftAdoptCommit.Commit(movedStore, adoptAll)) == MirrorDriftAdoptCodes.StaleRevision, "a revision-3 store with another model hash is refused (stale_revision)",
            Code(() => MirrorDriftAdoptCommit.Commit(movedStore, adoptAll)));
        Check(Same(movedFiles, Files(movedStore)), "nothing is written when the head hash differs", Names(Files(movedStore)));
        // Elements stored in another order hash the same canonically: accepted.
        var reordered = NewStore(head with { Elements = head.Elements.Reverse().ToArray() });
        Check(Code(() => MirrorDriftAdoptCommit.Commit(reordered, adoptAll)) == "accepted committed revision 4", "the head hash ignores stored element order");

        // ---- Another project ----
        var otherStore = NewStore(head with { Id = Guid.Parse("b0000000-0000-4000-8000-000000000000") });
        var otherFiles = Files(otherStore);
        Check(Code(() => MirrorDriftAdoptCommit.Commit(otherStore, adoptAll)) == MirrorDriftAdoptCodes.ProjectMismatch, "a store of another project is refused (project_mismatch)",
            Code(() => MirrorDriftAdoptCommit.Commit(otherStore, adoptAll)));
        Check(Same(otherFiles, Files(otherStore)), "nothing is written for another project");

        // ---- Restore-only and empty selections commit nothing (the store is not touched) ----
        var restoreOnly = Choose(resolution, "restore only", (W2, Restore), (W3, Restore), (D1, Restore));
        if (restoreOnly is not null)
        {
            var quiet = NewStore(head);
            var quietFiles = Files(quiet);
            var none = Accept(() => MirrorDriftAdoptCommit.Commit(quiet, restoreOnly), "a restore-only selection");
            Check(none is { Outcome: MirrorDriftAdoptOutcomes.NothingToAdopt, Revision: 3, Key: null, Head: null, Plan: null } && Same(quietFiles, Files(quiet)),
                "a restore-only selection commits nothing and writes nothing", Names(Files(quiet)));
            var missing = Path.Combine(Path.GetTempPath(), "bim-p2-12-absent-" + Guid.NewGuid().ToString("N"));
            Check(Code(() => MirrorDriftAdoptCommit.Commit(missing, restoreOnly)) == "accepted nothing_to_adopt revision 3" && !Directory.Exists(missing), "a restore-only selection never opens the store");
        }
        var empty = Choose(resolution, "empty");
        if (empty is not null)
        {
            var quiet = NewStore(head);
            var quietFiles = Files(quiet);
            Check(Code(() => MirrorDriftAdoptCommit.Commit(quiet, empty)) == "accepted nothing_to_adopt revision 3" && Same(quietFiles, Files(quiet)), "an empty selection commits nothing");
        }

        // ---- Store failures ----
        var locked = NewStore(head);
        var lockedFiles = Files(locked);
        string lockedCode;
        using (new FileStream(Path.Combine(locked, "lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            lockedCode = Code(() => MirrorDriftAdoptCommit.Commit(locked, adoptAll));
        Check(lockedCode == MirrorDriftAdoptCodes.StoreUnavailable, "a held store lock is refused (store_unavailable)", lockedCode);
        Check(Same(lockedFiles, Files(locked)), "nothing is written while the lock is held");
        Check(Code(() => MirrorDriftAdoptCommit.Commit(locked, adoptAll)) == "accepted committed revision 4", "the same selection commits once the lock is free");

        var absent = Path.Combine(Path.GetTempPath(), "bim-p2-12-absent-" + Guid.NewGuid().ToString("N"));
        Check(Code(() => MirrorDriftAdoptCommit.Commit(absent, adoptAll)) == MirrorDriftAdoptCodes.StoreInvalid && !Directory.Exists(absent), "a missing store is refused (store_invalid) and not created");
        var garbled = NewStore(head);
        File.WriteAllText(Path.Combine(garbled, "project.json"), "{\"Project\":null,\"Receipts\":{}}");
        var garbledFiles = Files(garbled);
        Check(Code(() => MirrorDriftAdoptCommit.Commit(garbled, adoptAll)) == MirrorDriftAdoptCodes.StoreInvalid && Same(garbledFiles, Files(garbled)), "a store without a project is refused (store_invalid)");
        File.WriteAllText(Path.Combine(garbled, "project.json"), "not json");
        Check(Code(() => MirrorDriftAdoptCommit.Commit(garbled, adoptAll)) == MirrorDriftAdoptCodes.StoreInvalid, "a store whose project.json is not JSON is refused (store_invalid)");

        // A commit failing after it started writing (project.tmp is a directory): commit_incomplete, project.json unchanged.
        var broken = NewStore(head);
        Directory.CreateDirectory(Path.Combine(broken, "project.tmp"));
        var brokenProject = File.ReadAllBytes(Path.Combine(broken, "project.json"));
        Check(Code(() => MirrorDriftAdoptCommit.Commit(broken, adoptAll)) == MirrorDriftAdoptCodes.CommitIncomplete, "a failure inside Commit after the lock is commit_incomplete",
            Code(() => MirrorDriftAdoptCommit.Commit(broken, adoptAll)));
        Check(File.ReadAllBytes(Path.Combine(broken, "project.json")).AsSpan().SequenceEqual(brokenProject) && new ProjectStore(broken).Read().Revision == 3,
            "project.json still holds revision 3 after commit_incomplete");

        // ---- Malformed selections and invalid IR ----
        var sound = NewStore(head);
        var soundFiles = Files(sound);
        var update = adoptAll.Operations.Single(operation => operation.Action == "update");
        Check(Code(() => MirrorDriftAdoptCommit.Commit(sound, adoptAll with { Operations = new[] { update, update } })) == MirrorDriftAdoptCodes.SelectionInvalid, "two operations on one element are refused");
        Check(Code(() => MirrorDriftAdoptCommit.Commit(sound, adoptAll with { Operations = new[] { update with { Action = "create" } } })) == MirrorDriftAdoptCodes.SelectionInvalid, "creating an existing element is refused");
        Check(Code(() => MirrorDriftAdoptCommit.Commit(sound, adoptAll with { Operations = new[] { update with { Action = "move" } } })) == MirrorDriftAdoptCodes.SelectionInvalid, "an unknown action is refused");
        Check(Code(() => MirrorDriftAdoptCommit.Commit(sound, adoptAll with { Operations = new[] { update with { Element = update.Element with { Id = Guid.Parse("c0000000-0000-4000-8000-0000000000ff") } } } })) == MirrorDriftAdoptCodes.SelectionInvalid,
            "updating a missing element is refused");
        Check(Code(() => MirrorDriftAdoptCommit.Commit(sound, adoptAll with { Operations = new Operation[] { null! } })) == MirrorDriftAdoptCodes.SelectionInvalid, "a null operation is refused");
        var dangling = update with { Element = update.Element with { Dependencies = new[] { Guid.Parse("c0000000-0000-4000-8000-0000000000ee") } } };
        Check(Code(() => MirrorDriftAdoptCommit.Commit(sound, adoptAll with { Operations = new[] { dangling } })) == MirrorDriftAdoptCodes.InvalidIr, "an adopted model failing validation is refused (invalid_ir)",
            Code(() => MirrorDriftAdoptCommit.Commit(sound, adoptAll with { Operations = new[] { dangling } })));
        // Deleting W1's host level without its dependants leaves dangling dependencies.
        var level = head.Elements.Single(element => element.Kind == "level");
        Check(Code(() => MirrorDriftAdoptCommit.Commit(sound, adoptAll with { Operations = new[] { new Operation("delete", level) } })) == MirrorDriftAdoptCodes.InvalidIr, "deleting a prerequisite alone is refused (invalid_ir)");
        Check(Same(soundFiles, Files(sound)), "nothing is written on any malformed-selection refusal", Names(Files(sound)));
        try { MirrorDriftAdoptCommit.Commit(sound, null!); Check(false, "a null selection is refused"); }
        catch (ArgumentNullException) { Check(true, "a null selection is refused"); }
        catch (Exception error) { Check(false, "a null selection is refused", error.GetType().Name); }
    }
}
