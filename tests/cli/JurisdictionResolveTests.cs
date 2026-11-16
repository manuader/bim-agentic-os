using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P10-21 `jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]`: the
// P10-06 JurisdictionRegistry (P5-10 LoadLocked under --lock) on the CLI, read-only. Oracles, all against real CLI
// processes:
//   1. Every resolution's stdout line equals the envelope built here from the in-process registry result for the same
//      skills directory and arguments: success {"status":"succeeded","code","pack","version","stub"}, refusal
//      {"status":"failed","code","subject","detail"} with the registry's JurisdictionIssue unchanged; exit 0 / 1.
//   2. Pinned cases: CABA by code and by country+municipality (literal envelope), jurisdiction_unknown,
//      jurisdiction_stub_refused without --allow-stub (and the stub resolved with it), jurisdiction_ambiguous (two packs
//      claiming one code), skill_lock_drift (a sidecar edited after pinning), plus a locked CABA resolution.
//   3. Package-first: from a CLI copied into <package>/cli beside VERSION, manifest.json and skills/, the package's
//      skills/ are resolved (SkillPackLookup.PackageRoot), not the repository's.
//   4. Usage: missing, conflicting, repeated, unknown or valueless options print jurisdiction_resolve_usage with the
//      usage text; nothing is written (the lock bytes and the whole skills tree digest are unchanged).
//   5. Static: the command loads through JurisdictionRegistry.Load/LoadLocked, finds the package via
//      SkillPackLookup.PackageRoot, routes defects through IntentPlan.Internal and has no file write API.
internal static class JurisdictionResolveTests
{
    const string Usage = "usage: jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]";
    const string UsageCode = "jurisdiction_resolve_usage";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Stub = "xx.example.codigo-ejemplo";

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "jurisdiction-resolve");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "jurisdiction-resolve: " + name, detail);

        (int Exit, string Out, string Err) Cli(string dll, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add("jurisdiction-resolve");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        static string D((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");

        // The envelope the verb must print for an in-process registry load and selection.
        static (int Exit, string Line) Expected(JurisdictionLoad load, ProjectJurisdiction project, bool allowStub)
        {
            if (load.Refusal is { } loadRefusal) return (1, Failed(loadRefusal));
            var registry = load.Registry!;
            var selection = registry.Select(project, allowStub);
            if (selection.Refusal is { } refusal) return (1, Failed(refusal));
            var claim = selection.Claim!;
            var pack = registry.Catalog.Find(claim.Pack)!;
            return (0, JsonSerializer.Serialize(new { status = "succeeded", code = claim.Code, pack = claim.Pack, version = pack.Version.ToString(), stub = claim.Stub }));
        }
        static string Failed(JurisdictionIssue issue) => JsonSerializer.Serialize(new { status = "failed", code = issue.Code, subject = issue.Subject, detail = issue.Detail });

        void Same(string name, string dll, JurisdictionLoad load, ProjectJurisdiction project, bool allowStub, string? expectCode, params string[] arguments)
        {
            var expected = Expected(load, project, allowStub);
            var result = Cli(dll, arguments);
            Check(result.Exit == expected.Exit && result.Out == expected.Line, name + ": stdout equals the in-process registry result", D(result) + " | expected " + expected.Line);
            if (expectCode is not null)
            {
                string? code = null;
                try { using var document = JsonDocument.Parse(result.Out); code = document.RootElement.GetProperty("code").GetString(); }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
                Check(code == expectCode && expected.Line.Contains("\"code\":\"" + expectCode + "\"", StringComparison.Ordinal), name + ": pinned code " + expectCode, D(result));
            }
        }

        var skills = Path.Combine(root, "skills");
        var unlocked = JurisdictionRegistry.Load(skills);
        Check(unlocked.Succeeded, "the repository skills load in process", unlocked.Refusal?.ToString() ?? "");
        var cabaVersion = SkillLoader.LoadDirectory(skills).Find(Caba)!.Version.ToString();
        var skillsDigest = TreeDigest(skills);

        // ---- 1/2. repository resolutions ----
        var cabaLine = JsonSerializer.Serialize(new { status = "succeeded", code = "ar.caba", pack = Caba, version = cabaVersion, stub = false });
        var byCode = Cli(cliDll, "--code", "ar.caba");
        Check(byCode.Exit == 0 && byCode.Out == cabaLine, "CABA by code prints {status,code,pack,version,stub}", D(byCode) + " | expected " + cabaLine);
        Same("CABA by code", cliDll, unlocked, new ProjectJurisdiction(Code: "ar.caba"), false, null, "--code", "ar.caba");
        var byMetadata = Cli(cliDll, "--country", "Argentina", "--municipality", "Ciudad Autonoma de Buenos Aires");
        Check(byMetadata.Exit == 0 && byMetadata.Out == cabaLine, "CABA by country and municipality prints the same envelope", D(byMetadata));
        Same("CABA by metadata", cliDll, unlocked, new ProjectJurisdiction(Country: "Argentina", Municipality: "Ciudad Autonoma de Buenos Aires"), false, null,
            "--country", "Argentina", "--municipality", "Ciudad Autonoma de Buenos Aires");
        Same("CABA by code with --allow-stub", cliDll, unlocked, new ProjectJurisdiction(Code: "ar.caba"), true, null, "--code", "ar.caba", "--allow-stub");
        Same("unknown code", cliDll, unlocked, new ProjectJurisdiction(Code: "zz.nowhere"), false, JurisdictionCodes.JurisdictionUnknown, "--code", "zz.nowhere");
        Same("unknown municipality", cliDll, unlocked, new ProjectJurisdiction(Country: "Argentina", Municipality: "Rosario"), false, JurisdictionCodes.JurisdictionUnknown,
            "--country", "Argentina", "--municipality", "Rosario");
        Same("stub without --allow-stub", cliDll, unlocked, new ProjectJurisdiction(Code: "xx.example"), false, JurisdictionCodes.JurisdictionStubRefused, "--code", "xx.example");
        Same("stub with --allow-stub", cliDll, unlocked, new ProjectJurisdiction(Code: "xx.example"), true, null, "--code", "xx.example", "--allow-stub");
        var stubAllowed = Cli(cliDll, "--allow-stub", "--code", "xx.example");
        Check(stubAllowed.Exit == 0 && stubAllowed.Out.Contains("\"stub\":true", StringComparison.Ordinal) && stubAllowed.Out.Contains("\"pack\":\"" + Stub + "\"", StringComparison.Ordinal),
            "--allow-stub (in any position) resolves the stub pack with stub true", D(stubAllowed));
        Same("malformed code", cliDll, unlocked, new ProjectJurisdiction(Code: "Not A Code"), false, JurisdictionCodes.JurisdictionInvalid, "--code", "Not A Code");

        // ---- locked resolutions over the repository skills ----
        var lockPath = Path.Combine(scratch, "skills.lock.json");
        File.WriteAllBytes(lockPath, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Caba, Stub })));
        var lockBytes = File.ReadAllBytes(lockPath);
        var locked = JurisdictionRegistry.LoadLocked(skills, lockPath);
        Check(locked.Succeeded, "the repository skills load under the pinned lock in process", locked.Refusal?.ToString() ?? "");
        Same("CABA by code under --lock", cliDll, locked, new ProjectJurisdiction(Code: "ar.caba"), false, null, "--code", "ar.caba", "--lock", lockPath);
        var lockedCaba = Cli(cliDll, "--lock", lockPath, "--code", "ar.caba");
        Check(lockedCaba.Exit == 0 && lockedCaba.Out == cabaLine, "CABA under --lock prints the CABA envelope", D(lockedCaba));
        Same("stub refused under --lock", cliDll, locked, new ProjectJurisdiction(Code: "xx.example"), false, JurisdictionCodes.JurisdictionStubRefused, "--code", "xx.example", "--lock", lockPath);
        var onlyStubLock = Path.Combine(scratch, "stub-only.lock.json");
        File.WriteAllBytes(onlyStubLock, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Stub })));
        Same("CABA unpinned under --lock is unknown", cliDll, JurisdictionRegistry.LoadLocked(skills, onlyStubLock), new ProjectJurisdiction(Code: "ar.caba"), false,
            JurisdictionCodes.JurisdictionUnknown, "--code", "ar.caba", "--lock", onlyStubLock);
        var forged = Path.Combine(scratch, "forged.lock.json");
        File.WriteAllBytes(forged, SkillLockFile.Serialize(new SkillLock(new[] { new SkillLockEntry(Caba, SkillLoader.LoadDirectory(skills).Find(Caba)!.Version, new string('0', 64)) })));
        Same("forged content hash under --lock", cliDll, JurisdictionRegistry.LoadLocked(skills, forged), new ProjectJurisdiction(Code: "ar.caba"), false,
            JurisdictionCodes.SkillLockDrift, "--code", "ar.caba", "--lock", forged);
        var missingLock = Path.Combine(scratch, "absent.lock.json");
        Same("absent lock file", cliDll, JurisdictionRegistry.LoadLocked(skills, missingLock), new ProjectJurisdiction(Code: "ar.caba"), false, null, "--code", "ar.caba", "--lock", missingLock);
        Check(!File.Exists(missingLock), "an absent lock is not created");
        Check(File.ReadAllBytes(lockPath).AsSpan().SequenceEqual(lockBytes), "the lock file is unchanged (read-only verb)");

        // ---- 3. package-first: a CLI copied into <package>/cli beside VERSION, manifest.json and skills/ ----
        var package = Path.Combine(scratch, "pkg");
        var packageCli = Path.Combine(package, "cli");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var packageSkills = Path.Combine(package, "skills");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(packageSkills, Caba));
        // Two fictitious code packs claiming the one jurisdiction xx.dup.
        var stubManifest = File.ReadAllText(Path.Combine(skills, Stub, "skill.json"));
        foreach (var id in new[] { "xx.dup.pack-a", "xx.dup.pack-b" })
        {
            var directory = Path.Combine(packageSkills, id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "skill.json"), stubManifest.Replace("\"id\": \"" + Stub + "\"", "\"id\": \"" + id + "\"", StringComparison.Ordinal), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "jurisdiction.json"),
                "{\"jurisdiction_version\":1,\"code\":\"xx.dup\",\"pack\":\"" + id + "\",\"country_names\":[\"Duplicate Country\"],\"municipality_names\":[\"Duplicate City\"],\"stub\":false,\"note\":\"fictitious test fixture\"}\n",
                new UTF8Encoding(false));
        }
        var packageDll = Path.Combine(packageCli, "Bim.Cli.dll");
        var packageLoad = JurisdictionRegistry.Load(packageSkills);
        Check(packageLoad.Succeeded && packageLoad.Registry!.Codes.SequenceEqual(new[] { "ar.caba", "xx.dup" }), "the package fixture loads in process with codes ar.caba and xx.dup",
            packageLoad.Refusal?.ToString() ?? string.Join(",", packageLoad.Registry!.Codes));
        Same("ambiguous code from the package", packageDll, packageLoad, new ProjectJurisdiction(Code: "xx.dup"), false, JurisdictionCodes.JurisdictionAmbiguous, "--code", "xx.dup");
        Same("ambiguous metadata from the package", packageDll, packageLoad, new ProjectJurisdiction(Country: "Duplicate Country", Municipality: "Duplicate City"), false,
            JurisdictionCodes.JurisdictionAmbiguous, "--country", "Duplicate Country", "--municipality", "Duplicate City");
        Same("stub code is unknown from the package (package skills, not the repository's)", packageDll, packageLoad, new ProjectJurisdiction(Code: "xx.example"), true,
            JurisdictionCodes.JurisdictionUnknown, "--code", "xx.example", "--allow-stub");
        Same("CABA from the package", packageDll, packageLoad, new ProjectJurisdiction(Code: "ar.caba"), false, null, "--code", "ar.caba");
        // Lock drift: pin the package's CABA pack, then edit its sidecar.
        var packageLock = Path.Combine(scratch, "package.lock.json");
        File.WriteAllBytes(packageLock, SkillLockFile.Serialize(SkillLocking.Pin(packageSkills, new[] { Caba })));
        Same("CABA from the package under --lock", packageDll, JurisdictionRegistry.LoadLocked(packageSkills, packageLock), new ProjectJurisdiction(Code: "ar.caba"), false, null,
            "--code", "ar.caba", "--lock", packageLock);
        var sidecar = Path.Combine(packageSkills, Caba, "jurisdiction.json");
        File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("\"CABA\",", "\"CABA\", \"Buenos Aires\",", StringComparison.Ordinal), new UTF8Encoding(false));
        var drifted = JurisdictionRegistry.LoadLocked(packageSkills, packageLock);
        Same("edited sidecar under --lock", packageDll, drifted, new ProjectJurisdiction(Code: "ar.caba"), false, JurisdictionCodes.SkillLockDrift, "--code", "ar.caba", "--lock", packageLock);
        Same("edited sidecar without --lock still resolves", packageDll, JurisdictionRegistry.Load(packageSkills), new ProjectJurisdiction(Code: "ar.caba"), false, null, "--code", "ar.caba");

        // ---- 4. usage ----
        var usageLine = JsonSerializer.Serialize(new { status = "failed", code = UsageCode, detail = Usage });
        foreach (var (label, arguments) in new (string, string[])[]
        {
            ("no options", Array.Empty<string>()),
            ("only --allow-stub", new[] { "--allow-stub" }),
            ("only --lock", new[] { "--lock", lockPath }),
            ("--code and --country", new[] { "--code", "ar.caba", "--country", "Argentina" }),
            ("--code and --municipality", new[] { "--code", "ar.caba", "--municipality", "CABA" }),
            ("--country without --municipality", new[] { "--country", "Argentina" }),
            ("--municipality without --country", new[] { "--municipality", "CABA" }),
            ("--code repeated", new[] { "--code", "ar.caba", "--code", "ar.caba" }),
            ("--lock repeated", new[] { "--code", "ar.caba", "--lock", lockPath, "--lock", lockPath }),
            ("--allow-stub repeated", new[] { "--code", "ar.caba", "--allow-stub", "--allow-stub" }),
            ("--code without a value", new[] { "--code" }),
            ("--code followed by an option", new[] { "--code", "--allow-stub" }),
            ("--lock without a value", new[] { "--code", "ar.caba", "--lock" }),
            ("blank --code", new[] { "--code", " " }),
            ("empty --lock", new[] { "--code", "ar.caba", "--lock", "" }),
            ("unknown option", new[] { "--code", "ar.caba", "--skills", skills }),
            ("positional argument", new[] { "ar.caba" }),
        })
        {
            var result = Cli(cliDll, arguments);
            Check(result.Exit == 1 && result.Out == usageLine, "usage refusal: " + label, D(result));
        }
        Check(File.ReadAllBytes(lockPath).AsSpan().SequenceEqual(lockBytes), "usage refusals leave the lock unchanged");
        var skillsAfter = TreeDigest(skills);
        Check(skillsAfter == skillsDigest, "every run leaves the repository skills tree unchanged (paths and bytes)", skillsDigest + " -> " + skillsAfter);

        // ---- 5. static ----
        var sourcePath = Path.Combine(root, "interfaces", "cli", "JurisdictionResolveCommand.cs");
        var source = File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : "";
        Check(source.Contains("JurisdictionRegistry.LoadLocked(", StringComparison.Ordinal) && source.Contains("JurisdictionRegistry.Load(", StringComparison.Ordinal)
            && source.Contains("SkillPackLookup.PackageRoot(", StringComparison.Ordinal) && source.Contains("IntentPlan.Internal(\"jurisdiction-resolve\"", StringComparison.Ordinal),
            "the command loads through JurisdictionRegistry.Load/LoadLocked, finds the package with SkillPackLookup.PackageRoot and reports defects with IntentPlan.Internal");
        Check(!source.Contains(" internal error: ", StringComparison.Ordinal), "the command hand-writes no internal-error stderr line");
        Check(new[] { "File.Write", "File.Create", "File.Move", "File.Copy", "File.Delete", "File.Open", "FileMode.", "FileStream", "Directory.Create", "Directory.Move", "Directory.Delete", "SkillLockFile.Write" }
            .All(api => !source.Contains(api, StringComparison.Ordinal)), "the command has no file write API (read-only)");
    }

    // SHA-256 over the sorted relative paths, sizes and bytes of every file under a directory (P10-21 review): any file
    // created, removed or rewritten under skills/ changes it.
    static string TreeDigest(string directory)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, file));
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\n" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n"));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
