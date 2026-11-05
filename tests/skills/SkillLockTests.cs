using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// P5-08 oracle: the project skill lock (skills.lock.json, a new standalone format, lock_version 1) pins resolved skill
// pack ids, versions and a content SHA-256 over a documented canonical byte set of each pack directory; a lock-checked
// load refuses packs whose content or version differs from the lock (skill_lock_drift) or that vanished
// (skill_lock_missing) unless SkillLocking.Update explicitly rewrites the lock; lock bytes are deterministic. The
// expected hashes below were computed independently (Python hashlib over the canonical stream). Unit-tested only.
static class SkillLockTests
{
    const string Locked = "bim.test.locked";
    const string Other = "bim.test.other";
    const string Third = "bim.test.third";

    // Fixture manifest bytes, LF-terminated; the hash is over raw bytes so the line ending is part of the content.
    static string ManifestText(string id, string version = "1.2.3") =>
        "{\"manifest_version\":1,\"id\":\"" + id + "\",\"version\":\"" + version + "\",\"kind\":\"qa\",\"description\":\"Locked pack\",\"priority\":10}\n";

    // sha256("bim-skill-pack-sha256-v1\n" + for each file in ordinal path order: path "\n" length "\n" bytes "\n").
    const string FullPackSha = "f6ccaf63b9bf2c657f04650ebda8c914bfedbd7aa84cb201bb9aba2d3e7091fe";
    const string ManifestOnlySha = "6a189c8ea208e8822c74efdf6b5c8d4a14d7e2a58126b1511eadfb92f48b3f60";

    static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static void WriteText(string path, string text) => WriteBytes(path, Encoding.UTF8.GetBytes(text));

    // The full fixture pack: skill.json, Z.txt, notes/readme.txt (CRLF kept) and an empty notes/empty.txt.
    static void FullPack(string root, bool reversed = false)
    {
        var dir = Path.Combine(root, Locked);
        var writes = new List<Action>
        {
            () => WriteText(Path.Combine(dir, "skill.json"), ManifestText(Locked)),
            () => WriteText(Path.Combine(dir, "Z.txt"), "zeta"),
            () => WriteText(Path.Combine(dir, "notes", "readme.txt"), "a\r\nb\n"),
            () => WriteText(Path.Combine(dir, "notes", "empty.txt"), ""),
        };
        if (reversed) writes.Reverse();
        foreach (var write in writes) write();
    }

    static void ManifestPack(string root, string id, string version = "1.2.3") => WriteText(Path.Combine(root, id, "skill.json"), ManifestText(id, version));

    static string Show(IEnumerable<SkillLockIssue> issues) => string.Join("; ", issues.Select(issue => issue.Code + "@" + issue.Subject + " " + issue.Detail));

    static SkillLockException? Throws(Action action)
    {
        try { action(); return null; }
        catch (SkillLockException error) { return error; }
        catch (Exception error) { return new SkillLockException("unexpected " + error.GetType().Name, "", error.Message); }
    }

    static string Lock(params string[] entries) =>
        "{\n  \"lock_version\": 1,\n  \"content_hash\": \"bim-skill-pack-sha256-v1\",\n  \"packs\": [" + (entries.Length == 0 ? "]" : "\n" + string.Join(",\n", entries) + "\n  ]") + "\n}\n";

    static string Entry(string id, string version, string sha) =>
        "    {\n      \"id\": \"" + id + "\",\n      \"version\": \"" + version + "\",\n      \"content_sha256\": \"" + sha + "\"\n    }";

    public static void Run(string root, Action<bool, string, string> report, Func<string> tempRoot)
    {
        void Check(bool condition, string name, string detail = "") => report(condition, "lock: " + name, detail);

        // ---- Canonical content hash ----
        var full = tempRoot();
        FullPack(full);
        Check(SkillPackHash.Compute(full, Locked) == FullPackSha, "the content hash matches the independently computed vector", SkillPackHash.Compute(full, Locked));
        Check(SkillPackHash.Scheme == "bim-skill-pack-sha256-v1", "the hash scheme is named and versioned");
        var reversed = tempRoot();
        FullPack(reversed, reversed: true);
        File.SetLastWriteTimeUtc(Path.Combine(reversed, Locked, "Z.txt"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Check(SkillPackHash.Compute(reversed, Locked) == FullPackSha, "the hash does not depend on creation order or timestamps");
        var manifestOnly = tempRoot();
        ManifestPack(manifestOnly, Locked);
        Check(SkillPackHash.Compute(manifestOnly, Locked) == ManifestOnlySha, "a manifest-only pack matches its vector");
        Directory.CreateDirectory(Path.Combine(manifestOnly, Locked, "empty-dir", "nested"));
        Check(SkillPackHash.Compute(manifestOnly, Locked) == ManifestOnlySha, "empty directories are not content");
        var crlf = tempRoot();
        FullPack(crlf);
        WriteText(Path.Combine(crlf, Locked, "notes", "readme.txt"), "a\nb\n");
        Check(SkillPackHash.Compute(crlf, Locked) != FullPackSha, "line endings are hashed unchanged (CRLF and LF differ)");
        var renamed = tempRoot();
        FullPack(renamed);
        File.Move(Path.Combine(renamed, Locked, "Z.txt"), Path.Combine(renamed, Locked, "Y.txt"));
        Check(SkillPackHash.Compute(renamed, Locked) != FullPackSha, "renaming a file changes the hash");
        var moved = tempRoot();
        FullPack(moved);
        File.Move(Path.Combine(moved, Locked, "Z.txt"), Path.Combine(moved, Locked, "notes", "Z.txt"));
        Check(SkillPackHash.Compute(moved, Locked) != FullPackSha, "moving a file into a subdirectory changes the hash");
        var added = tempRoot();
        FullPack(added);
        WriteText(Path.Combine(added, Locked, ".hidden"), "");
        Check(SkillPackHash.Compute(added, Locked) != FullPackSha, "every file counts, hidden and empty ones included");
        var hex = SkillPackHash.Compute(full, Locked);
        Check(hex.Length == 64 && hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "the hash is 64 lowercase hex digits");
        Check(Throws(() => SkillPackHash.Compute(full, "bim.test.absent"))?.Code == SkillLockCodes.SkillLockMissing, "hashing a pack that is not there is skill_lock_missing");
        Check(Throws(() => SkillPackHash.Compute(full, "../outside"))?.Code == SkillLockCodes.ValueInvalid, "hashing a non-id is value_invalid");
        var linkRoot = tempRoot();
        ManifestPack(linkRoot, Locked);
        var outside = Path.Combine(tempRoot(), "outside.txt");
        File.WriteAllText(outside, "outside");
        var linkMade = false;
        try { File.CreateSymbolicLink(Path.Combine(linkRoot, Locked, "linked.txt"), outside); linkMade = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (linkMade)
        {
            var linkError = Throws(() => SkillPackHash.Compute(linkRoot, Locked));
            Check(linkError is { Code: SkillLockCodes.PathUnsafe, Subject: Locked + "/linked.txt" }, "a symbolic link inside a pack is path_unsafe", linkError?.Code + " " + linkError?.Subject);
        }
        else Check(OperatingSystem.IsWindows() && !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal),
            "symlink fixture available", "could not create a symbolic link");

        // ---- Pinning and deterministic bytes ----
        var project = tempRoot();
        FullPack(project);
        ManifestPack(project, Other, "0.4.0");
        ManifestPack(project, Third, "2.0.1");
        var otherSha = SkillPackHash.Compute(project, Other);
        var pinned = SkillLocking.Pin(project, new[] { Other, Locked });
        Check(pinned.Packs.Select(entry => entry.Id).SequenceEqual(new[] { Locked, Other }) && pinned.Packs[0].Version == new SkillVersion(1, 2, 3)
            && pinned.Packs[0].ContentSha256 == FullPackSha && pinned.Packs[1].Version == new SkillVersion(0, 4, 0) && pinned.Packs[1].ContentSha256 == otherSha,
            "pinning records ids in ordinal order with manifest versions and content hashes");
        var expectedText = Lock(Entry(Locked, "1.2.3", FullPackSha), Entry(Other, "0.4.0", otherSha));
        var bytes = SkillLockFile.Serialize(pinned);
        Check(bytes.SequenceEqual(Encoding.UTF8.GetBytes(expectedText)), "lock bytes are the documented canonical text (two-space indent, LF, trailing newline, no BOM)", Encoding.UTF8.GetString(bytes));
        Check(SkillLockFile.Serialize(SkillLocking.Pin(project, new[] { Locked, Other, Locked })).SequenceEqual(bytes), "pin order and repeated ids do not change the bytes");
        Check(SkillLockFile.Serialize(new SkillLock(pinned.Packs.Reverse())).SequenceEqual(bytes), "a lock built from entries in any order serializes identically");
        Check(Encoding.UTF8.GetString(SkillLockFile.Serialize(new SkillLock(Array.Empty<SkillLockEntry>()))) == Lock(), "an empty lock serializes with an empty packs array");
        var reparsed = SkillLockFile.Parse(expectedText);
        Check(SkillLockFile.Serialize(reparsed).SequenceEqual(bytes) && reparsed.Find(Other)?.ContentSha256 == otherSha && reparsed.Find(Third) is null, "the lock text parses back to the same lock");
        var resolution = SkillResolver.Resolve(SkillLoader.LoadDirectory(project), new[] { new SkillRequest(Locked), new SkillRequest(Other) });
        Check(resolution.Succeeded && SkillLockFile.Serialize(SkillLocking.Pin(project, resolution)).SequenceEqual(bytes), "a successful resolution pins its resolved skills");
        Check(Throws(() => SkillLocking.Pin(project, new[] { "bim.test.absent" }))?.Code == SkillCodes.SkillMissing, "pinning an unknown pack is skill_missing");
        var failed = SkillResolver.Resolve(SkillLoader.LoadDirectory(project), new[] { new SkillRequest("bim.test.absent") });
        Check(Throws(() => SkillLocking.Pin(project, failed))?.Code == SkillLockCodes.ResolutionFailed, "pinning a failed resolution is refused");

        // ---- Durable write and read ----
        var lockDir = tempRoot();
        var lockPath = Path.Combine(lockDir, SkillLock.FileName);
        Check(SkillLock.FileName == "skills.lock.json", "the lock file name is skills.lock.json");
        SkillLockFile.Write(lockPath, pinned);
        Check(File.ReadAllBytes(lockPath).SequenceEqual(bytes) && Directory.GetFiles(lockDir).Length == 1, "writing stores exactly the canonical bytes and leaves no temporary file");
        SkillLockFile.Write(lockPath, pinned);
        Check(File.ReadAllBytes(lockPath).SequenceEqual(bytes), "writing the same lock twice is byte-identical");
        Check(SkillLockFile.Serialize(SkillLockFile.Read(lockPath)).SequenceEqual(bytes), "reading the written lock gives the same lock");
        var bom = Path.Combine(tempRoot(), SkillLock.FileName);
        WriteBytes(bom, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray());
        Check(SkillLockFile.Serialize(SkillLockFile.Read(bom)).SequenceEqual(bytes), "one leading byte order mark is accepted on read");
        var crlfLock = Path.Combine(tempRoot(), SkillLock.FileName);
        WriteText(crlfLock, expectedText.Replace("\n", "\r\n", StringComparison.Ordinal));
        Check(SkillLockFile.Serialize(SkillLockFile.Read(crlfLock)).SequenceEqual(bytes), "a CRLF checkout of the lock reads as the same lock");
        Check(Throws(() => SkillLockFile.Read(Path.Combine(lockDir, "absent.json")))?.Code == SkillLockCodes.LockMissing, "reading a missing lock is lock_missing");
        var badUtf8 = Path.Combine(tempRoot(), SkillLock.FileName);
        WriteBytes(badUtf8, new byte[] { 0x7B, 0xFF, 0x7D });
        Check(Throws(() => SkillLockFile.Read(badUtf8))?.Code == SkillLockCodes.JsonInvalid, "invalid UTF-8 is json_invalid");

        // ---- Lock-checked load ----
        var load = SkillLocking.Load(project, lockPath);
        Check(load.Succeeded && load.Catalog is { Skills.Count: 2 } catalog && catalog.Find(Locked) is { Version: { Major: 1, Minor: 2, Patch: 3 } } && catalog.Find(Third) is null
            && load.Lock is not null && SkillLockFile.Serialize(load.Lock).SequenceEqual(bytes), "matching content loads only the pinned packs", Show(load.Issues));
        var unpinned = SkillResolver.Resolve(load.Catalog!, new[] { new SkillRequest(Third) });
        Check(unpinned.Issues.Any(issue => issue.Code == SkillCodes.SkillMissing && issue.Subject == Third), "an unpinned pack is not available through the locked catalog");

        WriteText(Path.Combine(project, Locked, "notes", "readme.txt"), "a\nb\n");
        var drift = SkillLocking.Load(project, lockPath);
        Check(!drift.Succeeded && drift.Catalog is null && drift.Issues.Count == 1 && drift.Issues[0] is { Code: SkillLockCodes.SkillLockDrift, Subject: Locked } contentIssue
            && contentIssue.Detail.Contains(FullPackSha, StringComparison.Ordinal), "a non-manifest content change is refused as skill_lock_drift naming the pinned hash", Show(drift.Issues));
        ManifestPack(project, Other, "0.5.0");
        var both = SkillLocking.Load(project, lockPath);
        Check(both.Issues.Count == 2 && both.Issues.All(issue => issue.Code == SkillLockCodes.SkillLockDrift) && both.Issues.Select(issue => issue.Subject).SequenceEqual(new[] { Locked, Other })
            && both.Issues[1].Detail.Contains("0.4.0", StringComparison.Ordinal) && both.Issues[1].Detail.Contains("0.5.0", StringComparison.Ordinal),
            "every drifted pack is reported in id order and a version change names both versions", Show(both.Issues));
        Check(SkillLockFile.Read(lockPath) is { } untouched && SkillLockFile.Serialize(untouched).SequenceEqual(bytes), "a refused load never rewrites the lock");

        // ---- Explicit update ----
        var update = SkillLocking.Update(project, lockPath);
        var updatedBytes = File.ReadAllBytes(lockPath);
        Check(update.Changes.Select(change => change.Id).SequenceEqual(new[] { Locked, Other }) && update.Changes[0].Before?.ContentSha256 == FullPackSha
            && update.Changes[0].After?.ContentSha256 == SkillPackHash.Compute(project, Locked) && update.Changes[1].After?.Version == new SkillVersion(0, 5, 0),
            "an explicit update re-pins the locked set and reports each change");
        Check(updatedBytes.SequenceEqual(SkillLockFile.Serialize(update.Lock)) && !updatedBytes.SequenceEqual(bytes), "the update rewrites the lock file with the new canonical bytes");
        Check(SkillLocking.Load(project, lockPath).Succeeded, "after the update the drifted content loads");
        var again = SkillLocking.Update(project, lockPath);
        Check(again.Changes.Count == 0 && File.ReadAllBytes(lockPath).SequenceEqual(updatedBytes), "updating an up-to-date lock reports no change and keeps the bytes");
        var otherPin = SkillLockFile.Read(lockPath).Find(Other);
        var grow = SkillLocking.Update(project, lockPath, new[] { Third, Locked });
        Check(grow.Lock.Packs.Select(entry => entry.Id).SequenceEqual(new[] { Locked, Other, Third }) && grow.Lock.Find(Other) == otherPin
            && grow.Changes.Count == 1 && grow.Changes[0] is { Id: Third, Before: null, After: not null },
            "an update with explicit ids re-pins those ids and keeps every other pin unchanged");
        var kept = SkillLocking.Update(project, lockPath, Array.Empty<string>(), new[] { Other });
        Check(kept.Lock.Packs.Select(entry => entry.Id).SequenceEqual(new[] { Locked, Third }) && kept.Changes is [{ Id: Other, Before: not null, After: null }]
            && File.ReadAllBytes(lockPath).SequenceEqual(SkillLockFile.Serialize(kept.Lock)), "dropping a pin is explicit (unpin)");
        Check(Throws(() => SkillLocking.Update(project, lockPath, Array.Empty<string>(), new[] { Other }))?.Code == SkillLockCodes.ValueInvalid
            && File.ReadAllBytes(lockPath).SequenceEqual(SkillLockFile.Serialize(kept.Lock)), "unpinning a pack that is not pinned is value_invalid and writes nothing");
        Check(Throws(() => SkillLocking.Update(project, lockPath, new[] { Third }, new[] { Third }))?.Code == SkillLockCodes.ValueInvalid, "re-pinning and unpinning the same id is value_invalid");
        Check(Throws(() => SkillLocking.Update(project, Path.Combine(tempRoot(), SkillLock.FileName), Array.Empty<string>(), new[] { Locked }))?.Code == SkillLockCodes.LockMissing, "unpinning needs an existing lock");
        var fresh = Path.Combine(tempRoot(), SkillLock.FileName);
        var created = SkillLocking.Update(project, fresh, new[] { Locked });
        Check(File.Exists(fresh) && created.Changes.Count == 1 && created.Changes[0].Before is null, "an update with ids creates a missing lock");
        Check(Throws(() => SkillLocking.Update(project, Path.Combine(tempRoot(), SkillLock.FileName)))?.Code == SkillLockCodes.LockMissing, "an update without ids needs an existing lock");

        // ---- Missing packs and invalid directories ----
        Directory.Delete(Path.Combine(project, Third), true);
        var missing = SkillLocking.Load(project, lockPath);
        Check(missing.Issues.Count == 1 && missing.Issues[0] is { Code: SkillLockCodes.SkillLockMissing, Subject: Third }, "a pinned pack that disappeared is skill_lock_missing", Show(missing.Issues));
        Check(Throws(() => SkillLocking.Update(project, lockPath))?.Code == SkillLockCodes.SkillLockMissing && File.ReadAllBytes(lockPath).SequenceEqual(SkillLockFile.Serialize(kept.Lock)),
            "an update that cannot hash a pinned pack fails and leaves the lock unchanged");
        var broken = tempRoot();
        Directory.CreateDirectory(Path.Combine(broken, "Not An Id"));
        var brokenLoad = SkillLocking.Load(broken, new SkillLock(Array.Empty<SkillLockEntry>()));
        Check(brokenLoad.Issues.Count == 1 && brokenLoad.Issues[0].Code == SkillLockCodes.SkillsInvalid && brokenLoad.Issues[0].Detail.StartsWith(SkillCodes.IdInvalid, StringComparison.Ordinal),
            "a skills directory SkillLoader refuses is skills_invalid with the loader code", Show(brokenLoad.Issues));
        var lockLoad = SkillLocking.Load(project, Path.Combine(tempRoot(), "absent.json"));
        Check(lockLoad.Issues.Count == 1 && lockLoad.Issues[0].Code == SkillLockCodes.LockMissing, "a lock-checked load without a lock file is lock_missing");

        // ---- Strict parse refusals ----
        var good = Entry(Locked, "1.2.3", FullPackSha);
        void Rejects(string name, string text, string code, string? subject = null)
        {
            var error = Throws(() => SkillLockFile.Parse(text));
            Check(error is not null && error.Code == code && (subject is null || error.Subject == subject), name, error is null ? "accepted" : error.Code + "@" + error.Subject + " " + error.Detail);
        }
        Rejects("malformed JSON is json_invalid", "{", SkillLockCodes.JsonInvalid);
        Rejects("a non-object is type_invalid", "[]", SkillLockCodes.TypeInvalid, "$");
        Rejects("an unknown member is member_unknown", Lock(good).Replace("\"packs\"", "\"extra\": 1,\n  \"packs\"", StringComparison.Ordinal), SkillLockCodes.MemberUnknown, "$.extra");
        Rejects("a duplicate member is member_duplicate", Lock(good).Replace("\"packs\"", "\"lock_version\": 1,\n  \"packs\"", StringComparison.Ordinal), SkillLockCodes.MemberDuplicate, "$.lock_version");
        Rejects("a missing packs member is member_missing", "{\"lock_version\":1,\"content_hash\":\"bim-skill-pack-sha256-v1\"}", SkillLockCodes.MemberMissing, "$.packs");
        Rejects("lock_version 2 is version_unsupported", Lock(good).Replace("\"lock_version\": 1", "\"lock_version\": 2", StringComparison.Ordinal), SkillLockCodes.VersionUnsupported, "$.lock_version");
        Rejects("lock_version 1.0 is version_unsupported", Lock(good).Replace("\"lock_version\": 1", "\"lock_version\": 1.0", StringComparison.Ordinal), SkillLockCodes.VersionUnsupported, "$.lock_version");
        Rejects("an unknown hash scheme is value_invalid", Lock(good).Replace("bim-skill-pack-sha256-v1", "sha1", StringComparison.Ordinal), SkillLockCodes.ValueInvalid, "$.content_hash");
        Rejects("packs that are not an array are type_invalid", "{\"lock_version\":1,\"content_hash\":\"bim-skill-pack-sha256-v1\",\"packs\":{}}", SkillLockCodes.TypeInvalid, "$.packs");
        Rejects("an unknown entry member is member_unknown", Lock(good.Replace("\"id\"", "\"note\": \"x\",\n      \"id\"", StringComparison.Ordinal)), SkillLockCodes.MemberUnknown, "$.packs[0].note");
        Rejects("an entry without a hash is member_missing", Lock("{\"id\":\"" + Locked + "\",\"version\":\"1.2.3\"}"), SkillLockCodes.MemberMissing, "$.packs[0].content_sha256");
        Rejects("an invalid id is value_invalid", Lock(Entry("Not.An.Id", "1.2.3", FullPackSha)), SkillLockCodes.ValueInvalid, "$.packs[0].id");
        Rejects("an invalid version is value_invalid", Lock(Entry(Locked, "1.2", FullPackSha)), SkillLockCodes.ValueInvalid, "$.packs[0].version");
        Rejects("an uppercase hash is value_invalid", Lock(Entry(Locked, "1.2.3", FullPackSha.ToUpperInvariant())), SkillLockCodes.ValueInvalid, "$.packs[0].content_sha256");
        Rejects("a short hash is value_invalid", Lock(Entry(Locked, "1.2.3", FullPackSha[..63])), SkillLockCodes.ValueInvalid, "$.packs[0].content_sha256");
        Rejects("a numeric version is type_invalid", Lock("{\"id\":\"" + Locked + "\",\"version\":1,\"content_sha256\":\"" + FullPackSha + "\"}"), SkillLockCodes.TypeInvalid, "$.packs[0].version");
        Rejects("a repeated pack is entry_duplicate", Lock(good, good), SkillLockCodes.EntryDuplicate, "$.packs[1].id");
        Rejects("packs out of ordinal order are value_invalid", Lock(Entry(Other, "0.4.0", otherSha), good), SkillLockCodes.ValueInvalid, "$.packs[1].id");
        var many = Enumerable.Range(0, SkillLock.MaxPacks + 1).Select(index => Entry("bim.test.p" + index.ToString("D5", CultureInfo.InvariantCulture), "1.0.0", FullPackSha)).ToArray();
        Rejects("too many packs are too_many_items", Lock(many), SkillLockCodes.TooManyItems, "$.packs");
        Rejects("an oversized lock is input_too_large", Lock(good) + new string(' ', SkillLock.MaxInputChars), SkillLockCodes.InputTooLarge, "$");
        Check(Throws(() => new SkillLock(new[] { new SkillLockEntry(Locked, new SkillVersion(1, 0, 0), "abc") }))?.Code == SkillLockCodes.ValueInvalid, "a hand-built entry with a bad hash is refused");
        Check(Throws(() => new SkillLock(new[] { new SkillLockEntry(Locked, new SkillVersion(1, 0, 0), FullPackSha), new SkillLockEntry(Locked, new SkillVersion(1, 0, 0), FullPackSha) }))?.Code == SkillLockCodes.EntryDuplicate,
            "a hand-built lock with a repeated id is refused");

        // ---- Review fixes: snapshot hashing, write guards, repair, stale resolutions, limits ----
        var snapRoot = tempRoot();
        FullPack(snapRoot);
        var snapshot = SkillPackHash.Snapshot(snapRoot, Locked);
        Check(snapshot.Id == Locked && snapshot.ContentSha256 == FullPackSha && snapshot.Files.Select(file => file.Path).SequenceEqual(new[] { "Z.txt", "notes/empty.txt", "notes/readme.txt", "skill.json" })
            && snapshot.Files[3].Bytes.SequenceEqual(Encoding.UTF8.GetBytes(ManifestText(Locked))) && snapshot.Manifest().Version == new SkillVersion(1, 2, 3),
            "a snapshot holds the hashed files in canonical order and parses its manifest from them");
        var snapLock = SkillLocking.Pin(snapRoot, new[] { Locked });
        ManifestPack(snapRoot, Locked, "9.9.9");
        var fromSnapshot = SkillLocking.Verify(snapLock, new[] { snapshot });
        Check(fromSnapshot.Succeeded && fromSnapshot.Catalog!.Find(Locked)?.Version == new SkillVersion(1, 2, 3),
            "a verified manifest comes from the hashed bytes, not from a later read of the disk", Show(fromSnapshot.Issues));
        Check(SkillLocking.Load(snapRoot, snapLock).Issues.Any(issue => issue.Code == SkillLockCodes.SkillLockDrift), "the edited disk content itself is drift");
        Check(SkillLocking.Verify(snapLock, Array.Empty<SkillPackSnapshot>()).Issues is [{ Code: SkillLockCodes.SkillLockMissing, Subject: Locked }], "a pinned pack without a snapshot is skill_lock_missing");
        var noManifest = tempRoot();
        WriteText(Path.Combine(noManifest, Locked, "notes.txt"), "x");
        Check(Throws(() => SkillPackHash.Snapshot(noManifest, Locked).Manifest())?.Code == SkillLockCodes.SkillsInvalid, "a snapshot without skill.json has no manifest (skills_invalid)");

        var guardDir = tempRoot();
        var dirTarget = Path.Combine(guardDir, "as-directory");
        Directory.CreateDirectory(dirTarget);
        Check(Throws(() => SkillLockFile.Write(dirTarget, pinned))?.Code == SkillLockCodes.NotRegularFile, "writing over a directory is not_regular_file");
        var linkTarget = Path.Combine(guardDir, "real.json");
        File.WriteAllText(linkTarget, "{}");
        var linkLock = Path.Combine(guardDir, SkillLock.FileName);
        var lockLinkMade = false;
        try { File.CreateSymbolicLink(linkLock, linkTarget); lockLinkMade = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (lockLinkMade)
        {
            Check(Throws(() => SkillLockFile.Write(linkLock, pinned))?.Code == SkillLockCodes.PathUnsafe && File.ReadAllText(linkTarget) == "{}", "writing through a symlinked lock is path_unsafe and leaves the target alone");
            Check(Throws(() => SkillLockFile.Read(linkLock))?.Code == SkillLockCodes.PathUnsafe, "reading a symlinked lock is path_unsafe");
        }
        else Check(OperatingSystem.IsWindows() && !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal),
            "lock symlink fixture available", "could not create a symbolic link");

        var repairRoot = tempRoot();
        ManifestPack(repairRoot, Other, "0.4.0");
        var corrupt = Path.Combine(tempRoot(), SkillLock.FileName);
        File.WriteAllText(corrupt, "{");
        var repaired = SkillLocking.Update(repairRoot, corrupt, new[] { Other });
        Check(File.ReadAllBytes(corrupt).SequenceEqual(SkillLockFile.Serialize(repaired.Lock)) && repaired.Changes is [{ Before: null, After: not null }],
            "an update with explicit ids repairs an unreadable lock");
        File.WriteAllText(corrupt, "{");
        Check(Throws(() => SkillLocking.Update(repairRoot, corrupt))?.Code == SkillLockCodes.JsonInvalid && File.ReadAllText(corrupt) == "{", "an update without ids still refuses an unreadable lock");

        var staleRoot = tempRoot();
        ManifestPack(staleRoot, Other, "0.4.0");
        var staleResolution = SkillResolver.Resolve(SkillLoader.LoadDirectory(staleRoot), new[] { new SkillRequest(Other) });
        WriteText(Path.Combine(staleRoot, Other, "skill.json"), ManifestText(Other, "0.4.0").Replace("Locked pack", "Edited pack", StringComparison.Ordinal));
        Check(Throws(() => SkillLocking.Pin(staleRoot, staleResolution))?.Code == SkillLockCodes.ResolutionStale, "pinning a resolution whose manifest changed on disk (same version) is resolution_stale");

        Check(Throws(() => new SkillLock(new[] { new SkillLockEntry(Locked, new SkillVersion(-1, 0, 0), FullPackSha) }))?.Code == SkillLockCodes.ValueInvalid, "a hand-built entry with a negative version component is refused");
        var wide = tempRoot();
        ManifestPack(wide, Locked);
        for (var index = 0; index <= SkillPackHash.MaxFiles; index++) Directory.CreateDirectory(Path.Combine(wide, Locked, "d" + index.ToString(CultureInfo.InvariantCulture)));
        Check(Throws(() => SkillPackHash.Compute(wide, Locked))?.Code == SkillLockCodes.TooManyItems, "directories count toward the pack entry limit");

        // ---- Checked-in skills keep their bytes on every checkout ----
        var attributes = Path.Combine(root, ".gitattributes");
        var rules = File.Exists(attributes) ? File.ReadAllLines(attributes).Select(line => line.Trim()).ToArray() : Array.Empty<string>();
        Check(rules.Contains("skills/** -text") && rules.Contains("skills.lock.json -text"), "the root .gitattributes keeps skills/ and lock files byte-exact (-text)", string.Join(" | ", rules));
        var withCr = Directory.GetFiles(Path.Combine(root, "skills"), "*", SearchOption.AllDirectories).Where(path => File.ReadAllBytes(path).Contains((byte)'\r'))
            .Select(path => Path.GetRelativePath(root, path)).ToArray();
        Check(withCr.Length == 0, "no checked-out file under skills/ contains a carriage return", string.Join(", ", withCr));

        // ---- Checked-in skills ----
        var skills = Path.Combine(root, "skills");
        var all = SkillLoader.LoadDirectory(skills).Skills.Select(skill => skill.Id).ToArray();
        var repoLock = SkillLocking.Pin(skills, all);
        var repoLoad = SkillLocking.Load(skills, repoLock);
        Check(repoLock.Packs.Count == all.Length && repoLoad.Succeeded && repoLoad.Catalog!.Skills.Count == all.Length, "every checked-in pack pins and verifies", Show(repoLoad.Issues));
        Check(SkillLockFile.Serialize(SkillLocking.Pin(skills, all.Reverse())).SequenceEqual(SkillLockFile.Serialize(repoLock)), "pinning the checked-in packs is deterministic");
        var caba = Path.Combine(skills, "ar.caba.codigo-edificacion");
        Check(repoLock.Find("ar.caba.codigo-edificacion")?.ContentSha256 == Reference(caba), "a checked-in pack with a sidecar hashes both files (reference recomputation)");
    }

    // Test-side recomputation of the canonical stream over the files of one pack directory (no subdirectories expected).
    static string Reference(string directory)
    {
        var files = Directory.GetFiles(directory).Select(path => Path.GetFileName(path)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        using var stream = new MemoryStream();
        void Put(byte[] data) => stream.Write(data, 0, data.Length);
        Put(Encoding.ASCII.GetBytes("bim-skill-pack-sha256-v1\n"));
        foreach (var name in files)
        {
            var data = File.ReadAllBytes(Path.Combine(directory, name));
            Put(Encoding.UTF8.GetBytes(name + "\n" + data.Length.ToString(CultureInfo.InvariantCulture) + "\n"));
            Put(data);
            Put(new byte[] { (byte)'\n' });
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
