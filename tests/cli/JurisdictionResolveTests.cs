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
//   6. P10-24 refusal edges: an absent lock is lock_missing; a NUL or over-length --lock path (in process) is
//      skills_lock_path_invalid (detail "lock"), never jurisdiction_resolve_internal_error; registry issues of a
//      successful load (claim_duplicate, alias_overlap) are dropped and the CABA success bytes stay unchanged.
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
        Same("absent lock file", cliDll, JurisdictionRegistry.LoadLocked(skills, missingLock), new ProjectJurisdiction(Code: "ar.caba"), false, SkillLockCodes.LockMissing, "--code", "ar.caba", "--lock", missingLock);
        Check(!File.Exists(missingLock), "an absent lock is not created");
        // P10-24: a lock path SkillLockTool.PathProblem refuses (NUL, over MaxPathChars) is the typed lock refusal
        // skills_lock_path_invalid (detail "lock"), as in skills-lock, never jurisdiction_resolve_internal_error. A NUL
        // cannot travel in a process argument, so these run the command in process (stdout, stderr and exit captured).
        var pathInvalidLine = JsonSerializer.Serialize(new { status = "failed", code = "skills_lock_path_invalid", detail = "lock" });
        var exitBeforeInProcess = Environment.ExitCode;
        // The command run in process: stdout, stderr and the exit code captured. P10-26: Environment.ExitCode is reset and the
        // console redirected inside the try, and the exit code is read and every global restored in the finally, so a
        // throwing Run cannot leak the command's exit code or the redirected console into the test process.
        (int Exit, string Line, string Error) InProcess(string lockArgument)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var exit = -1;
            string thrown = "";
            try
            {
                Environment.ExitCode = 0;
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                Bim.Cli.JurisdictionResolveCommand.Run(new[] { "jurisdiction-resolve", "--code", "ar.caba", "--lock", lockArgument });
            }
            catch (Exception failure) { thrown = " threw " + failure.GetType().Name + ": " + failure.Message; }
            finally { exit = Environment.ExitCode; Environment.ExitCode = previousExit; Console.SetOut(previousOut); Console.SetError(previousError); }
            return (exit, capturedOut.ToString().TrimEnd('\r', '\n'), (capturedError.ToString().Trim() + thrown).Trim());
        }
        static string R((int Exit, string Line, string Error) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + r.Line + (r.Error.Length > 0 ? " stderr " + r.Error : "");
        foreach (var (label, badLock) in new (string, string)[]
        {
            ("NUL in the lock path", scratch + Path.DirectorySeparatorChar + "sk\0ills.lock.json"),
            ("lock path over 4096 characters", Path.Combine(scratch, new string('l', 5000) + ".json")),
        })
        {
            var result = InProcess(badLock);
            Check(result.Exit == 1 && result.Line == pathInvalidLine && result.Error.Length == 0, label + " (in process) is " + pathInvalidLine + " with nothing on stderr", R(result));
        }
        // P10-26 narrowed contract: SkillLockTool.PathProblem refuses blank, NUL and over-MaxPathChars paths before it calls
        // Path.GetFullPath, and on .NET 8 Path.GetFullPath throws only for null, empty or NUL-containing input on both Linux
        // and Windows (no invalid-character validation, no MAX_PATH / PathTooLongException since .NET Core 2.1). So no lock
        // path is rejected *only* by Path.GetFullPath on either test OS: the skills_lock_path_invalid contract this file pins
        // is the NUL and length cases above; the GetFullPath catch is defensive. (A Windows GetFullPathNameW failure on an
        // over-long input is unreachable too: PathProblem caps the path at MaxPathChars = 4096 characters first.) Pinned here: inputs the .NET Framework
        // GetFullPath rejected (wildcards, pipes, quotes, extra colons, a control character, a bare \?\ prefix) and a path of
        // exactly MaxPathChars characters pass PathProblem, each checked against Path.GetFullPath on this runtime.
        var boundary = Path.Combine(scratch, "b");
        boundary += new string('b', Bim.Mcp.SkillLockTool.MaxPathChars - boundary.Length - ".json".Length) + ".json";
        foreach (var (label, candidate) in new (string, string)[]
        {
            ("wildcards", Path.Combine(scratch, "lo*ck?.json")),
            ("pipe and angle brackets", Path.Combine(scratch, "lo|ck<>.json")),
            ("double quote", Path.Combine(scratch, "lo\"ck.json")),
            ("extra colons", Path.Combine(scratch, "lock:stream:$DATA.json")),
            ("control character U+0001", Path.Combine(scratch, "lo\u0001ck.json")),
            ("a bare device prefix", @"\\?\"),
            ("exactly MaxPathChars characters", boundary),
        })
        {
            string? rejection = null;
            try { Path.GetFullPath(candidate); }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException) { rejection = failure.GetType().Name + ": " + failure.Message; }
            var problem = Bim.Mcp.SkillLockTool.PathProblem(candidate, "lock");
            Check(rejection is null && problem is null, "a lock path with " + label + " is accepted by Path.GetFullPath and by SkillLockTool.PathProblem (no GetFullPath-only rejection; contract narrowed to NUL/length)",
                "length " + candidate.Length.ToString(CultureInfo.InvariantCulture) + " GetFullPath " + (rejection ?? "accepted") + " PathProblem " + (problem ?? "null"));
        }
        // P10-26: accepted odd paths reach the registry load: run in process, an absent lock under each is the typed load
        // refusal lock_missing (exit 1, nothing on stderr, equal to the in-process LoadLocked refusal), never
        // jurisdiction_resolve_internal_error.
        foreach (var (label, candidate, subject) in new (string, string, string)[]
        {
            ("an alternate data stream suffix", Path.Combine(scratch, "lock:stream:$DATA.json"), "lock:stream:$DATA.json"),
            ("wildcards", Path.Combine(scratch, "lo*ck?.json"), "lo*ck?.json"),
            // Observed (P10-26 run 44250658836): the subject is "" on Windows, where \\?\ is a device root, and the whole
            // name on Linux and macOS, where it is an ordinary relative file name.
            ("a bare device prefix", @"\\?\", OperatingSystem.IsWindows() ? "" : @"\\?\"),
        })
        {
            var pinned = JsonSerializer.Serialize(new { status = "failed", code = SkillLockCodes.LockMissing, subject, detail = "lock file not found" });
            var result = InProcess(candidate);
            string inProcess;
            try { inProcess = JurisdictionRegistry.LoadLocked(skills, candidate).Refusal is { } refusal ? Failed(refusal) : "<loaded>"; }
            catch (Exception failure) { inProcess = "<threw " + failure.GetType().Name + ": " + failure.Message + ">"; }
            Check(result.Exit == 1 && result.Line == inProcess && result.Line == pinned
                && !result.Line.Contains("jurisdiction_resolve_internal_error", StringComparison.Ordinal) && result.Error.Length == 0,
                "a lock path with " + label + " (in process) is the typed lock_missing refusal (pinned line, equal to the in-process load), nothing on stderr", R(result) + " | in-process load " + inProcess + " | pinned " + pinned);
        }
        Check(Environment.ExitCode == exitBeforeInProcess, "the in-process lock-path checks restore Environment.ExitCode (finally)",
            "Environment.ExitCode " + Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBeforeInProcess.ToString(CultureInfo.InvariantCulture));
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
        // P10-24: registry issues of a successful load (JurisdictionRegistry.Issues: claim_duplicate, alias_overlap,
        // pack_missing, ...) are dropped by the verb: the success envelope is exactly {status,code,pack,version,stub} and
        // never carries them. The package fixture's xx.dup is claimed twice (claim_duplicate); a third pack under the
        // distinct code xx.twin with the same aliases adds alias_overlap.
        var twin = "xx.twin.pack-c";
        var twinDirectory = Path.Combine(packageSkills, twin);
        // P10-26: the twin pack is deleted in a finally, so a throwing check cannot leave it behind for the lock-drift
        // block below (which pins only CABA from the same package skills).
        try
        {
            Directory.CreateDirectory(twinDirectory);
            File.WriteAllText(Path.Combine(packageSkills, twin, "skill.json"), stubManifest.Replace("\"id\": \"" + Stub + "\"", "\"id\": \"" + twin + "\"", StringComparison.Ordinal), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(packageSkills, twin, "jurisdiction.json"),
                "{\"jurisdiction_version\":1,\"code\":\"xx.twin\",\"pack\":\"" + twin + "\",\"country_names\":[\"Duplicate Country\"],\"municipality_names\":[\"Duplicate City\"],\"stub\":false,\"note\":\"fictitious test fixture\"}\n",
                new UTF8Encoding(false));
            var issuesLoad = JurisdictionRegistry.Load(packageSkills);
            var issueCodes = issuesLoad.Registry?.Issues.Select(issue => issue.Code).ToArray() ?? Array.Empty<string>();
            Check(issuesLoad.Succeeded && issueCodes.Contains(JurisdictionCodes.ClaimDuplicate) && issueCodes.Contains(JurisdictionCodes.AliasOverlap),
                "the package fixture with xx.twin loads in process with registry issues claim_duplicate and alias_overlap", issuesLoad.Refusal?.ToString() ?? string.Join(",", issueCodes));
            var withIssues = Cli(packageDll, "--code", "ar.caba");
            Check(withIssues.Exit == 0 && withIssues.Out == cabaLine && withIssues.Err.Length == 0,
                "registry issues are dropped: CABA from a package with claim_duplicate and alias_overlap prints the plain CABA envelope byte for byte", D(withIssues) + " | expected " + cabaLine);
            Same("ambiguous metadata with alias_overlap", packageDll, issuesLoad, new ProjectJurisdiction(Country: "Duplicate Country", Municipality: "Duplicate City"), false,
                JurisdictionCodes.JurisdictionAmbiguous, "--country", "Duplicate Country", "--municipality", "Duplicate City");
        }
        finally
        {
            try { if (Directory.Exists(twinDirectory)) Directory.Delete(twinDirectory, true); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Check(false, "the twin pack directory is deleted", twinDirectory + ": " + failure.Message); }
        }
        Check(!Directory.Exists(twinDirectory), "the twin pack directory is gone before the lock-drift block", twinDirectory);
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
