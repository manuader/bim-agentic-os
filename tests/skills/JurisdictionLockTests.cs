using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// P5-10 oracle: lock-checked jurisdiction sidecars. JurisdictionRegistry.LoadLocked / FromSnapshots parse every
// jurisdiction.json from the very bytes the skill lock hashed (a SkillPackSnapshot), never from a second disk read, so an
// edited, added or removed sidecar under a lock is refused skill_lock_drift and a snapshot whose bytes do not match its
// own content hash is refused too. Unlocked JurisdictionRegistry.Load is unchanged: its checked-in registry and its
// fixture refusal texts are pinned here verbatim. Unit-tested only.
static class JurisdictionLockTests
{
    const string Sample = "xx.sample.code";
    const string Plain = "xx.plain.code";
    const string Caba = "ar.caba.codigo-edificacion";

    static string Manifest(string id, string kind = "code") =>
        "{\"manifest_version\":1,\"id\":\"" + id + "\",\"version\":\"1.2.3\",\"kind\":\"" + kind + "\",\"description\":\"fixture\",\"priority\":10,\"rules\":[]}\n";

    static string Sidecar(string municipality = "Sample Town", Action<JsonObject>? change = null)
    {
        var node = new JsonObject
        {
            ["jurisdiction_version"] = 1,
            ["code"] = "xx.sample",
            ["pack"] = Sample,
            ["country_names"] = new JsonArray("Sample Land"),
            ["municipality_names"] = new JsonArray(municipality),
            ["stub"] = true,
        };
        change?.Invoke(node);
        return node.ToJsonString() + "\n";
    }

    static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    static string Render(JurisdictionRegistry registry) =>
        string.Join("\n", registry.Claims.Select(claim => claim.Code + "|" + claim.Pack + "|" + string.Join("/", claim.CountryNames) + "|" + string.Join("/", claim.MunicipalityNames) + "|"
            + (claim.Stub ? "stub" : "real") + "|" + (claim.Note ?? "-")))
        + "\nissues:" + string.Join(";", registry.Issues.Select(issue => issue.Code + "@" + issue.Subject + " " + issue.Detail))
        + "\nunregistered:" + string.Join(",", registry.UnregisteredCodePacks)
        + "\ncatalog:" + string.Join(",", registry.Catalog.Skills.Select(skill => skill.Id + "@" + skill.Version));

    static string Show(JurisdictionLoad load) =>
        load.Refusal is { } refusal ? refusal.Code + "@" + refusal.Subject + " (" + refusal.Detail + ")" : load.Registry is { } registry ? "ok " + Render(registry) : "(no registry, no refusal)";

    // Independent implementation of the bim-skill-pack-sha256-v1 stream over files in the order given, used to forge
    // snapshots that are self-consistent (their declared hash matches their bytes).
    static string StreamHash(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        var stream = new List<byte>(Encoding.ASCII.GetBytes("bim-skill-pack-sha256-v1\n"));
        foreach (var (path, bytes) in files)
        {
            stream.AddRange(Encoding.UTF8.GetBytes(path + "\n" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n"));
            stream.AddRange(bytes);
            stream.Add((byte)'\n');
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    static SkillPackSnapshot Forge(string id, params (string Path, byte[] Bytes)[] files) =>
        new(id, StreamHash(files.OrderBy(file => file.Path, StringComparer.Ordinal)), files.Select(file => new SkillPackFile(file.Path, file.Bytes)).ToList());

    static string Load(Func<JurisdictionLoad> load)
    {
        try { return Show(load()); }
        catch (Exception error) { return "threw " + error.GetType().Name + ": " + error.Message; }
    }

    static bool ThrowsNull(Func<object?> action)
    {
        try { action(); return false; }
        catch (ArgumentNullException) { return true; }
        catch (Exception) { return false; }
    }

    public static void Run(string root, Action<bool, string, string> report, Func<string> tempRoot)
    {
        void Check(bool condition, string name, string detail = "") => report(condition, "jurisdiction lock: " + name, detail);

        string Fixture(params (string Id, string Kind, byte[]? Sidecar)[] packs)
        {
            var directory = tempRoot();
            foreach (var (id, kind, sidecar) in packs)
            {
                Directory.CreateDirectory(Path.Combine(directory, id));
                File.WriteAllBytes(Path.Combine(directory, id, "skill.json"), Bytes(Manifest(id, kind)));
                if (sidecar is not null) File.WriteAllBytes(Path.Combine(directory, id, "jurisdiction.json"), sidecar);
            }
            return directory;
        }

        // ---- Unlocked loading is unchanged (pinned verbatim) ----
        var skillsRoot = Path.Combine(root, "skills");
        var unlocked = JurisdictionRegistry.Load(skillsRoot);
        var unlockedClaims = unlocked.Registry is { } unlockedRegistry ? string.Join("\n", Render(unlockedRegistry).Split('\n').Take(3)) : Show(unlocked);
        const string PinnedClaims =
            "ar.caba|ar.caba.codigo-edificacion|Argentina/Republica Argentina|CABA/Ciudad Autonoma de Buenos Aires/Ciudad de Buenos Aires|real|Codigo de Edificacion de la Ciudad Autonoma de Buenos Aires; per-rule source_confidence is in each citation.\n"
            + "xx.example|xx.example.codigo-ejemplo|Example Country|Example City|stub|Fictitious example jurisdiction for registry tests; not real law.\n"
            + "issues:";
        Check(unlockedClaims == PinnedClaims, "unlocked Load of skills/ renders the pinned claims and no issues", unlockedClaims);
        Check(unlocked.Registry is { UnregisteredCodePacks.Count: 0 } pinnedRegistry && pinnedRegistry.Catalog.Skills.Count == Directory.GetDirectories(skillsRoot).Length,
            "unlocked Load of skills/ still serves every pack and leaves no code pack unregistered", Show(unlocked));

        var refusalCases = new (string Name, byte[]? Sidecar, string? SubDirectory, string Expected)[]
        {
            ("a sidecar naming another pack", null, null, ""),
            ("an unknown member", Bytes(Sidecar(change: node => node["extra"] = true)), null, "member_unknown@xx.sample.code/jurisdiction.json ($.extra: unknown member)"),
            ("an unsupported version", Bytes(Sidecar(change: node => node["jurisdiction_version"] = 2)), null, "version_unsupported@xx.sample.code/jurisdiction.json ($.jurisdiction_version: only version 1 is supported)"),
            ("an empty sidecar", Array.Empty<byte>(), null, "not_regular_file@xx.sample.code/jurisdiction.json (zero-length entry is not read)"),
            ("an oversized sidecar", Enumerable.Repeat((byte)' ', JurisdictionClaim.MaxInputChars * 4 + 1).ToArray(), null, "input_too_large@xx.sample.code/jurisdiction.json (sidecar too large)"),
            ("a non-empty directory named jurisdiction.json", null, "jurisdiction.json", "not_regular_file@xx.sample.code/jurisdiction.json (a directory is not a sidecar)"),
        };
        foreach (var (name, sidecar, subDirectory, expected) in refusalCases)
        {
            string directory;
            string want;
            if (expected.Length == 0)
            {
                directory = Fixture(("xx.sample.other", "code", Bytes(Sidecar())));
                want = "pack_mismatch@xx.sample.other/jurisdiction.json (sidecar pack xx.sample.code differs from directory xx.sample.other)";
            }
            else
            {
                directory = Fixture((Sample, "code", sidecar));
                want = expected;
            }
            if (subDirectory is not null) Directory.CreateDirectory(Path.Combine(directory, Sample, subDirectory));
            if (subDirectory is not null) File.WriteAllBytes(Path.Combine(directory, Sample, subDirectory, "inner.txt"), Bytes("x"));
            var plain = Show(JurisdictionRegistry.Load(directory));
            Check(plain == want, "unlocked refusal text is unchanged for " + name, plain);
            var pinnedId = expected.Length == 0 ? "xx.sample.other" : Sample;
            var locked = Show(JurisdictionRegistry.LoadLocked(directory, SkillLocking.Pin(directory, new[] { pinnedId })));
            Check(locked == want, "a pinned pack with " + name + " is refused with the same code and text under the lock", locked);
        }
        var badUtf8 = Fixture((Sample, "code", new byte[] { 0x7B, 0xFF, 0x7D }));
        var badUtf8Plain = Show(JurisdictionRegistry.Load(badUtf8));
        var badUtf8Locked = Show(JurisdictionRegistry.LoadLocked(badUtf8, SkillLocking.Pin(badUtf8, new[] { Sample })));
        Check(badUtf8Plain.StartsWith("json_invalid@xx.sample.code/jurisdiction.json (invalid UTF-8: ", StringComparison.Ordinal) && badUtf8Locked == badUtf8Plain,
            "invalid UTF-8 is json_invalid in both modes with the same text", badUtf8Plain + " | " + badUtf8Locked);
        var bom = Fixture((Sample, "code", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Sidecar())).ToArray()));
        var bomPlain = Show(JurisdictionRegistry.Load(bom));
        var bomLocked = Show(JurisdictionRegistry.LoadLocked(bom, SkillLocking.Pin(bom, new[] { Sample })));
        Check(bomPlain.StartsWith("ok xx.sample|xx.sample.code|Sample Land|Sample Town|stub|-", StringComparison.Ordinal) && bomLocked == bomPlain,
            "a sidecar with a byte order mark loads in both modes to the same registry", bomPlain + " | " + bomLocked);

        // ---- Locked loading of the checked-in skills/ ----
        var everyPack = Directory.GetDirectories(skillsRoot).Select(path => Path.GetFileName(path)).ToArray();
        var lockAll = SkillLocking.Pin(skillsRoot, everyPack);
        var lockedAll = JurisdictionRegistry.LoadLocked(skillsRoot, lockAll);
        Check(lockedAll.Succeeded && unlocked.Registry is not null && Show(lockedAll) == Show(unlocked),
            "skills/ pinned in full loads under the lock to exactly the unlocked registry (claims, issues, unregistered packs, catalog)", Show(lockedAll));
        if (lockedAll.Registry is { } lockedRegistry && unlocked.Registry is { } plainRegistry)
        {
            var rooms = new[]
            {
                RoomFacts.Rectangle("r2", "Estar comedor", Length.FromMetres(3), Length.FromMetres(3.5), Length.FromMetres(2.6)),
                RoomFacts.Rectangle("r1", "Dormitorio", Length.FromMetres(2.5), Length.FromMetres(2.5), Length.FromMetres(2.6)),
            };
            var lockedJson = JurisdictionValidator.Validate(lockedRegistry, new ProjectJurisdiction("ar.caba"), rooms).ToJson();
            var plainJson = JurisdictionValidator.Validate(plainRegistry, new ProjectJurisdiction("ar.caba"), rooms).ToJson();
            Check(lockedJson == plainJson && lockedJson.Contains(Caba, StringComparison.Ordinal), "the validation envelope JSON is byte-identical between the locked and unlocked registry",
                lockedJson.Length > 300 ? lockedJson[..300] : lockedJson);
        }
        var cabaOnly = JurisdictionRegistry.LoadLocked(skillsRoot, SkillLocking.Pin(skillsRoot, new[] { Caba }));
        Check(cabaOnly.Registry is { } cabaRegistry && cabaRegistry.Codes.SequenceEqual(new[] { "ar.caba" }) && cabaRegistry.Catalog.Skills.Select(skill => skill.Id).SequenceEqual(new[] { Caba })
            && cabaRegistry.UnregisteredCodePacks.Count == 0 && cabaRegistry.Select(new ProjectJurisdiction("xx.example"), allowStub: true).Refusal?.Code == JurisdictionCodes.JurisdictionUnknown,
            "a lock pinning only the CABA pack serves only its claim; unpinned packs and sidecars are not served", Show(cabaOnly));

        // ---- Drift: an edited, added or removed sidecar under a lock ----
        var original = Bytes(Sidecar());
        var drift = Fixture((Sample, "code", original), (Plain, "code", null));
        var driftLock = SkillLocking.Pin(drift, new[] { Sample, Plain });
        var driftSidecar = Path.Combine(drift, Sample, "jurisdiction.json");
        var before = JurisdictionRegistry.LoadLocked(drift, driftLock);
        Check(before.Registry is { } beforeRegistry && beforeRegistry.Codes.SequenceEqual(new[] { "xx.sample" }) && beforeRegistry.UnregisteredCodePacks.SequenceEqual(new[] { Plain })
            && beforeRegistry.Claims[0].MunicipalityNames.SequenceEqual(new[] { "Sample Town" }), "an unchanged pinned fixture loads under the lock", Show(before));
        File.WriteAllBytes(driftSidecar, Bytes(Sidecar("Edited Town")));
        var edited = JurisdictionRegistry.LoadLocked(drift, driftLock);
        Check(edited.Registry is null && edited.Refusal is { Code: JurisdictionCodes.SkillLockDrift, Subject: Sample } editedIssue && editedIssue.Detail.Contains(driftLock.Find(Sample)!.ContentSha256, StringComparison.Ordinal),
            "an edited jurisdiction.json under a lock is refused skill_lock_drift naming the pack and the pinned hash", Show(edited));
        Check(JurisdictionCodes.SkillLockDrift == SkillLockCodes.SkillLockDrift && JurisdictionCodes.SkillLockMissing == SkillLockCodes.SkillLockMissing,
            "the jurisdiction lock codes are the skill lock codes");
        var editedPlain = JurisdictionRegistry.Load(drift);
        Check(editedPlain.Registry is { } editedRegistry && editedRegistry.Claims[0].MunicipalityNames.SequenceEqual(new[] { "Edited Town" }),
            "unlocked Load still reads the edited sidecar from disk as before", Show(editedPlain));
        File.WriteAllBytes(driftSidecar, Bytes(Sidecar().TrimEnd('\n') + "\r\n"));
        Check(JurisdictionRegistry.LoadLocked(drift, driftLock).Refusal?.Code == JurisdictionCodes.SkillLockDrift, "a line-ending-only edit of a sidecar is drift too (raw bytes are hashed)");
        File.WriteAllBytes(driftSidecar, original);
        Check(JurisdictionRegistry.LoadLocked(drift, driftLock).Succeeded, "restoring the pinned bytes loads again");
        File.WriteAllBytes(Path.Combine(drift, Plain, "jurisdiction.json"), Bytes(Sidecar(change: node => { node["code"] = "xx.plain"; node["pack"] = Plain; })));
        var added = JurisdictionRegistry.LoadLocked(drift, driftLock);
        Check(added.Refusal is { Code: JurisdictionCodes.SkillLockDrift, Subject: Plain }, "a sidecar added to a pinned pack is skill_lock_drift", Show(added));
        File.Delete(Path.Combine(drift, Plain, "jurisdiction.json"));
        File.Delete(driftSidecar);
        var removed = JurisdictionRegistry.LoadLocked(drift, driftLock);
        Check(removed.Refusal is { Code: JurisdictionCodes.SkillLockDrift, Subject: Sample }, "a sidecar removed from a pinned pack is skill_lock_drift", Show(removed));
        File.WriteAllBytes(driftSidecar, original);

        // ---- Lock file path, missing lock, missing pack, unpinned packs, invalid skills directory ----
        var lockPath = Path.Combine(tempRoot(), SkillLock.FileName);
        SkillLockFile.Write(lockPath, driftLock);
        var fromFile = JurisdictionRegistry.LoadLocked(drift, lockPath);
        Check(fromFile.Succeeded && Show(fromFile) == Show(JurisdictionRegistry.LoadLocked(drift, driftLock)), "LoadLocked reads skills.lock.json from a path", Show(fromFile));
        var noLock = JurisdictionRegistry.LoadLocked(drift, Path.Combine(tempRoot(), SkillLock.FileName));
        Check(noLock.Refusal?.Code == SkillLockCodes.LockMissing, "a missing lock file is lock_missing", Show(noLock));
        var oneLock = SkillLocking.Pin(drift, new[] { Sample });
        File.WriteAllBytes(Path.Combine(drift, Plain, "jurisdiction.json"), Bytes("{"));
        var unpinned = JurisdictionRegistry.LoadLocked(drift, oneLock);
        Check(unpinned.Registry is { } unpinnedRegistry && unpinnedRegistry.Catalog.Skills.Select(skill => skill.Id).SequenceEqual(new[] { Sample }) && unpinnedRegistry.Codes.SequenceEqual(new[] { "xx.sample" }),
            "a malformed sidecar of an unpinned pack is not read under the lock", Show(unpinned));
        Check(JurisdictionRegistry.Load(drift).Refusal?.Code == JurisdictionCodes.JsonInvalid, "unlocked Load still refuses that malformed sidecar");
        File.Delete(Path.Combine(drift, Plain, "jurisdiction.json"));
        Directory.Delete(Path.Combine(drift, Plain), true);
        var gone = JurisdictionRegistry.LoadLocked(drift, driftLock);
        Check(gone.Refusal is { Code: JurisdictionCodes.SkillLockMissing, Subject: Plain }, "a pinned pack that vanished is skill_lock_missing", Show(gone));
        var invalid = Fixture((Sample, "code", original));
        var invalidLock = SkillLocking.Pin(invalid, new[] { Sample });
        Directory.CreateDirectory(Path.Combine(invalid, "Not An Id"));
        var invalidLocked = JurisdictionRegistry.LoadLocked(invalid, invalidLock);
        var invalidPlain = JurisdictionRegistry.Load(invalid);
        Check(invalidLocked.Refusal is { Code: JurisdictionCodes.SkillsInvalid } invalidIssue && invalidIssue.Detail.StartsWith(SkillCodes.IdInvalid, StringComparison.Ordinal) && Show(invalidLocked) == Show(invalidPlain),
            "a skills directory SkillLoader refuses is skills_invalid under the lock, as unlocked", Show(invalidLocked));

        // ---- Snapshots: the registry consumes the hashed bytes, never a second disk read ----
        var snapRoot = Fixture((Sample, "code", original));
        var snapLock = SkillLocking.Pin(snapRoot, new[] { Sample });
        var snapshot = SkillPackHash.Snapshot(snapRoot, Sample);
        File.WriteAllBytes(Path.Combine(snapRoot, Sample, "jurisdiction.json"), Bytes(Sidecar("Edited Town")));
        var fromSnapshot = JurisdictionRegistry.FromSnapshots(snapLock, new[] { snapshot });
        Check(fromSnapshot.Registry is { } snapRegistry && snapRegistry.Claims.Count == 1 && snapRegistry.Claims[0].MunicipalityNames.SequenceEqual(new[] { "Sample Town" }),
            "FromSnapshots parses the snapshot bytes, not the sidecar edited on disk after hashing", Show(fromSnapshot));
        Check(JurisdictionRegistry.LoadLocked(snapRoot, snapLock).Refusal?.Code == JurisdictionCodes.SkillLockDrift, "the same edit is drift for a fresh LoadLocked");
        var forgedFiles = snapshot.Files.Select(file => file.Path == "jurisdiction.json" ? new SkillPackFile(file.Path, Bytes(Sidecar("Forged Town"))) : file).ToArray();
        var forged = JurisdictionRegistry.FromSnapshots(snapLock, new[] { new SkillPackSnapshot(Sample, snapshot.ContentSha256, forgedFiles) });
        Check(forged.Registry is null && forged.Refusal is { Code: JurisdictionCodes.SkillLockDrift, Subject: Sample },
            "a snapshot whose sidecar bytes do not match its own content hash is refused skill_lock_drift", Show(forged));
        var duplicated = JurisdictionRegistry.FromSnapshots(snapLock, new[] { new SkillPackSnapshot(Sample, snapshot.ContentSha256, snapshot.Files.Concat(snapshot.Files.Where(file => file.Path == "jurisdiction.json")).ToArray()) });
        Check(duplicated.Refusal is { Code: JurisdictionCodes.SkillLockDrift, Subject: Sample }, "a snapshot listing a file twice is refused skill_lock_drift", Show(duplicated));
        var reordered = JurisdictionRegistry.FromSnapshots(snapLock, new[] { new SkillPackSnapshot(Sample, snapshot.ContentSha256, snapshot.Files.Reverse().ToArray()) });
        Check(reordered.Succeeded && Show(reordered) == Show(fromSnapshot), "file order inside a snapshot does not matter (canonical ordinal order is hashed)", Show(reordered));
        var noSnapshot = JurisdictionRegistry.FromSnapshots(snapLock, Array.Empty<SkillPackSnapshot>());
        Check(noSnapshot.Refusal is { Code: JurisdictionCodes.SkillLockMissing, Subject: Sample }, "a pinned pack without a snapshot is skill_lock_missing", Show(noSnapshot));
        var extraRoot = Fixture((Plain, "code", Bytes("{")));
        var withExtra = JurisdictionRegistry.FromSnapshots(snapLock, new[] { snapshot, SkillPackHash.Snapshot(extraRoot, Plain) });
        Check(withExtra.Succeeded && Show(withExtra) == Show(fromSnapshot), "snapshots of unpinned packs are ignored", Show(withExtra));
        var sidecarBytes = snapshot.Files.First(file => file.Path == "jurisdiction.json").Bytes;
        var kept = Show(fromSnapshot);
        Array.Fill(sidecarBytes, (byte)' ');
        Check(Show(fromSnapshot) == kept && JurisdictionRegistry.FromSnapshots(snapLock, new[] { snapshot }).Refusal?.Code == JurisdictionCodes.SkillLockDrift,
            "mutating snapshot bytes afterwards changes nothing already loaded and is drift on the next load", kept);

        // ---- Links under a lock ----
        var linked = Fixture((Sample, "code", original));
        var linkedLock = SkillLocking.Pin(linked, new[] { Sample });
        var outside = Path.Combine(tempRoot(), "outside.json");
        File.WriteAllBytes(outside, original);
        File.Delete(Path.Combine(linked, Sample, "jurisdiction.json"));
        var linkMade = false;
        try { File.CreateSymbolicLink(Path.Combine(linked, Sample, "jurisdiction.json"), outside); linkMade = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (linkMade)
        {
            var linkedLoad = JurisdictionRegistry.LoadLocked(linked, linkedLock);
            Check(linkedLoad.Refusal is { Code: JurisdictionCodes.PathUnsafe, Subject: Sample }, "a sidecar replaced by a symbolic link to identical bytes is path_unsafe under the lock", Show(linkedLoad));
        }
        else Check(OperatingSystem.IsWindows() && !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal),
            "symlink fixture available", "could not create a symbolic link");

        // ---- Review: only paths Snapshot can produce are accepted, so the hash stream stays injective ----
        var manifestBytes = Bytes(Manifest(Sample));
        var forgedSidecar = Bytes(Sidecar("Forged Town"));
        // Real file "a" holds X = "pad\n1\nx\njurisdiction.json\n<len J>\n" + J; the forged snapshot splits the same stream into
        // a path "a\n<len X>\npad" (bytes "x"), a jurisdiction.json J and the real skill.json.
        var realA = Bytes("pad\n1\nx\njurisdiction.json\n" + forgedSidecar.Length.ToString(CultureInfo.InvariantCulture) + "\n").Concat(forgedSidecar).ToArray();
        var streamRoot = Fixture((Sample, "code", null));
        File.WriteAllBytes(Path.Combine(streamRoot, Sample, "a"), realA);
        var streamLock = SkillLocking.Pin(streamRoot, new[] { Sample });
        var streamReal = JurisdictionRegistry.LoadLocked(streamRoot, streamLock);
        Check(streamReal.Registry is { Claims.Count: 0 }, "the real pinned stream fixture has no jurisdiction claim", Show(streamReal));
        var forgedStream = new (string Path, byte[] Bytes)[] { ("a\n" + realA.Length.ToString(CultureInfo.InvariantCulture) + "\npad", Bytes("x")), ("jurisdiction.json", forgedSidecar), ("skill.json", manifestBytes) };
        Check(StreamHash(forgedStream) == streamLock.Find(Sample)!.ContentSha256, "the forged snapshot reproduces the pinned hash stream byte for byte (test premise)");
        var streamForged = Load(() => JurisdictionRegistry.FromSnapshots(streamLock, new[] { new SkillPackSnapshot(Sample, streamLock.Find(Sample)!.ContentSha256, forgedStream.Select(file => new SkillPackFile(file.Path, file.Bytes)).ToList()) }));
        Check(streamForged.StartsWith("skill_lock_drift@" + Sample + " (snapshot ", StringComparison.Ordinal), "a snapshot forging the pinned hash stream through a path with line breaks is refused skill_lock_drift", streamForged);
        foreach (var bad in new[] { "", "/lead", "dir//x", "trail/", "./x", "dir/./x", "..", "dir/../x", "back\\slash", "ctl\u0001x", "dir/ctl\u007Fx" })
        {
            var refused = Load(() => JurisdictionRegistry.FromSnapshots(snapLock, new[] { Forge(Sample, ("skill.json", manifestBytes), (bad, Bytes("x"))) }));
            Check(refused.StartsWith("skill_lock_drift@" + Sample + " (snapshot ", StringComparison.Ordinal) && refused.Contains("path", StringComparison.Ordinal),
                "a self-consistent snapshot path Snapshot never produces is refused: " + bad.Replace("\u0001", "\\u0001", StringComparison.Ordinal).Replace("\u007F", "\\u007F", StringComparison.Ordinal), refused);
        }
        var legal = Load(() => JurisdictionRegistry.FromSnapshots(snapLock, new[] { Forge(Sample, ("skill.json", manifestBytes), ("dir/.hidden/nñ.txt", Bytes("x"))) }));
        Check(legal.StartsWith("skill_lock_drift@" + Sample + " (pinned ", StringComparison.Ordinal), "a self-consistent snapshot with legal paths reaches the lock comparison (drift against the pin, not a path refusal)", legal);
        var manyFiles = Enumerable.Range(0, SkillPackHash.MaxFiles).Select(index => ("f" + index.ToString("D5", CultureInfo.InvariantCulture), Array.Empty<byte>())).Append(("skill.json", manifestBytes)).ToArray();
        var tooMany = Load(() => JurisdictionRegistry.FromSnapshots(snapLock, new[] { Forge(Sample, manyFiles) }));
        Check(tooMany.StartsWith("skill_lock_drift@" + Sample + " (snapshot ", StringComparison.Ordinal), "a snapshot with more than MaxFiles entries is refused as Snapshot would", tooMany);
        var deepPath = string.Join("/", Enumerable.Range(0, SkillPackHash.MaxDepth + 1).Select(index => "d" + index.ToString(CultureInfo.InvariantCulture))) + "/f.txt";
        var tooDeep = Load(() => JurisdictionRegistry.FromSnapshots(snapLock, new[] { Forge(Sample, ("skill.json", manifestBytes), (deepPath, Bytes("x"))) }));
        Check(tooDeep.StartsWith("skill_lock_drift@" + Sample + " (snapshot ", StringComparison.Ordinal), "a snapshot nested deeper than MaxDepth is refused as Snapshot would", tooDeep);
        var tooBig = Load(() => JurisdictionRegistry.FromSnapshots(snapLock, new[] { Forge(Sample, ("skill.json", manifestBytes), ("big.bin", new byte[SkillPackHash.MaxBytes - manifestBytes.Length + 1])) }));
        Check(tooBig.StartsWith("skill_lock_drift@" + Sample + " (snapshot ", StringComparison.Ordinal), "a snapshot over MaxBytes is refused as Snapshot would", tooBig);
        if (!OperatingSystem.IsWindows())
        {
            var backslash = Fixture((Sample, "code", null));
            File.WriteAllBytes(Path.Combine(backslash, Sample, "back\\slash"), Bytes("x"));
            string pinned;
            try { pinned = "pinned " + SkillLocking.Pin(backslash, new[] { Sample }).Packs.Count.ToString(CultureInfo.InvariantCulture); }
            catch (SkillLockException error) { pinned = error.Code + "@" + error.Subject; }
            Check(pinned == SkillLockCodes.PathUnsafe + "@" + Sample + "/back\\slash", "Snapshot refuses a file name with a backslash (path_unsafe), so it never hashes a path FromSnapshots refuses", pinned);
        }

        // ---- Review: FromSnapshots(Snapshot(...)) round-trips every checked-in pack and an unusual fixture ----
        var allSnapshots = everyPack.Select(id => SkillPackHash.Snapshot(skillsRoot, id)).ToArray();
        var roundTrip = Load(() => JurisdictionRegistry.FromSnapshots(lockAll, allSnapshots));
        Check(roundTrip == Show(lockedAll) && roundTrip == Show(unlocked) && allSnapshots.All(item => StreamHash(item.Files.Select(file => (file.Path, file.Bytes))) == lockAll.Find(item.Id)!.ContentSha256),
            "FromSnapshots over a fresh Snapshot of every skills/ pack equals LoadLocked and Load, and every hash matches the independent stream", roundTrip);
        var unusual = Fixture((Sample, "code", original));
        File.WriteAllBytes(Path.Combine(unusual, Sample, ".hidden"), Bytes("h"));
        File.WriteAllBytes(Path.Combine(unusual, Sample, "empty.txt"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(unusual, Sample, "ñandú.txt"), Bytes("n"));
        Directory.CreateDirectory(Path.Combine(unusual, Sample, "notes", "deep"));
        File.WriteAllBytes(Path.Combine(unusual, Sample, "notes", "deep", "x.txt"), Bytes("a\r\nb\n"));
        var unusualLock = SkillLocking.Pin(unusual, new[] { Sample });
        var unusualSnapshot = SkillPackHash.Snapshot(unusual, Sample);
        var unusualLoad = Load(() => JurisdictionRegistry.FromSnapshots(unusualLock, new[] { unusualSnapshot }));
        Check(unusualLoad.StartsWith("ok xx.sample|xx.sample.code|Sample Land|Sample Town|", StringComparison.Ordinal) && unusualLoad == Show(JurisdictionRegistry.LoadLocked(unusual, unusualLock))
            && StreamHash(unusualSnapshot.Files.Select(file => (file.Path, file.Bytes))) == unusualLock.Find(Sample)!.ContentSha256 && unusualSnapshot.Files.Count == 6,
            "a pack with nested directories, a non-ASCII name, hidden and empty files round-trips through FromSnapshots", unusualLoad + " files " + string.Join(",", unusualSnapshot.Files.Select(file => file.Path)));

        // ---- Review: a differently cased jurisdiction.json is refused under the lock on every OS ----
        var cased = Fixture((Sample, "code", null));
        File.WriteAllBytes(Path.Combine(cased, Sample, "Jurisdiction.json"), original);
        var casedLoad = JurisdictionRegistry.LoadLocked(cased, SkillLocking.Pin(cased, new[] { Sample }));
        Check(casedLoad.Refusal is { Code: JurisdictionCodes.PathUnsafe, Subject: Sample + "/Jurisdiction.json" }, "a pinned Jurisdiction.json (case differs) is path_unsafe under the lock", Show(casedLoad));
        var casedDirectory = Fixture((Sample, "code", null));
        Directory.CreateDirectory(Path.Combine(casedDirectory, Sample, "JURISDICTION.JSON"));
        File.WriteAllBytes(Path.Combine(casedDirectory, Sample, "JURISDICTION.JSON", "inner.txt"), Bytes("x"));
        var casedDirectoryLoad = JurisdictionRegistry.LoadLocked(casedDirectory, SkillLocking.Pin(casedDirectory, new[] { Sample }));
        Check(casedDirectoryLoad.Refusal is { Code: JurisdictionCodes.PathUnsafe, Subject: Sample + "/JURISDICTION.JSON" }, "a pinned JURISDICTION.JSON directory is path_unsafe under the lock", Show(casedDirectoryLoad));

        // ---- Review: pinned divergences, aggregation, manifests and malformed snapshots ----
        var emptyDirectory = Fixture((Sample, "code", null));
        Directory.CreateDirectory(Path.Combine(emptyDirectory, Sample, "jurisdiction.json"));
        var emptyDirectoryLocked = JurisdictionRegistry.LoadLocked(emptyDirectory, SkillLocking.Pin(emptyDirectory, new[] { Sample }));
        Check(Show(JurisdictionRegistry.Load(emptyDirectory)) == "not_regular_file@xx.sample.code/jurisdiction.json (a directory is not a sidecar)" && emptyDirectoryLocked.Registry is { Claims.Count: 0 },
            "an empty directory named jurisdiction.json: unlocked not_regular_file, under the lock no sidecar (the hash ignores empty directories)", Show(emptyDirectoryLocked));
        var aggregate = Fixture((Sample, "code", original), (Plain, "code", null));
        var aggregateLock = SkillLocking.Pin(aggregate, new[] { Sample, Plain });
        File.WriteAllBytes(Path.Combine(aggregate, Sample, "jurisdiction.json"), Bytes(Sidecar("Edited Town")));
        Directory.Delete(Path.Combine(aggregate, Plain), true);
        var aggregated = JurisdictionRegistry.LoadLocked(aggregate, aggregateLock);
        Check(aggregated.Refusal is { Code: JurisdictionCodes.SkillLockMissing, Subject: Plain } aggregatedIssue && aggregatedIssue.Detail.EndsWith(" (+1 more lock issue(s))", StringComparison.Ordinal),
            "one missing and one drifted pack: the first issue in pack order is reported with the count of the others", Show(aggregated));
        var badManifestRoot = tempRoot();
        Directory.CreateDirectory(Path.Combine(badManifestRoot, Sample));
        File.WriteAllBytes(Path.Combine(badManifestRoot, Sample, "skill.json"), Bytes("{"));
        var badManifest = Load(() => JurisdictionRegistry.FromSnapshots(snapLock, new[] { SkillPackHash.Snapshot(badManifestRoot, Sample) }));
        Check(badManifest.StartsWith("skills_invalid@" + Sample + " (", StringComparison.Ordinal), "a self-consistent snapshot whose skill.json does not parse is skills_invalid under FromSnapshots", badManifest);
        var genuine = SkillPackHash.Snapshot(snapRoot, Sample);
        var malformed = new (string Name, Func<SkillPackSnapshot> Make)[]
        {
            ("a null content hash", () => new SkillPackSnapshot(Sample, null!, genuine.Files)),
            ("a null file list", () => new SkillPackSnapshot(Sample, genuine.ContentSha256, null!)),
            ("a null file entry", () => new SkillPackSnapshot(Sample, genuine.ContentSha256, genuine.Files.Append(null!).ToList())),
            ("a file with a null path", () => new SkillPackSnapshot(Sample, genuine.ContentSha256, genuine.Files.Append(new SkillPackFile(null!, Bytes("x"))).ToList())),
            ("a file with null bytes", () => new SkillPackSnapshot(Sample, genuine.ContentSha256, genuine.Files.Append(new SkillPackFile("x", null!)).ToList())),
        };
        foreach (var (name, make) in malformed)
        {
            var result = Load(() => JurisdictionRegistry.FromSnapshots(snapLock, new[] { make() }));
            Check(result.StartsWith("skill_lock_drift@" + Sample + " (snapshot ", StringComparison.Ordinal), "a snapshot with " + name + " is a typed skill_lock_drift refusal", result);
        }
        Check(JurisdictionCodes.LockMissing == SkillLockCodes.LockMissing, "JurisdictionCodes.LockMissing is the skill lock code");

        // ---- Arguments ----
        Check(ThrowsNull(() => JurisdictionRegistry.LoadLocked(null!, snapLock)) && ThrowsNull(() => JurisdictionRegistry.LoadLocked(snapRoot, (SkillLock)null!))
            && ThrowsNull(() => JurisdictionRegistry.LoadLocked(null!, lockPath)) && ThrowsNull(() => JurisdictionRegistry.LoadLocked(snapRoot, (string)null!))
            && ThrowsNull(() => JurisdictionRegistry.FromSnapshots(null!, new[] { snapshot })) && ThrowsNull(() => JurisdictionRegistry.FromSnapshots(snapLock, null!))
            && ThrowsNull(() => JurisdictionRegistry.FromSnapshots(snapLock, new SkillPackSnapshot[] { null! })),
            "null arguments throw ArgumentNullException");
    }
}
