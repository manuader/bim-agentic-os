using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P5-21 `skill-list [--skills dir]`: the skill packs of a skills directory on the CLI, read-only. Oracles, all against
// real CLI processes:
//   1. Every listing's stdout line equals the envelope built here from the in-process loader result for the same skills
//      directory: SkillLoader.LoadDirectory, then JurisdictionRegistry.Load for the stub flags (ClaimFor), giving
//      {"status":"succeeded","skills_path","packs":[{"id","version","kind","stub","source_confidence":{"rules","cited",
//      "owner-confirmed","to-verify","lowest"}}]} in ordinal id order, exit 0; a SkillException is
//      {"status":"failed","code","field","detail","file"} with the loader's values unchanged, a registry refusal
//      {"status":"failed","code","subject","detail"}, exit 1.
//   2. Pinned cases: a malformed jurisdiction.json (json_invalid, subject <id>/jurisdiction.json), a trailing separator on
//      --skills (same skills_path as the default), the repository listing (ids equal the skills/ directory names in ordinal order, CABA kind code not
//      stub, xx.example stub), a malformed skill.json (json_invalid at <id>/skill.json) and a missing directory
//      (root_missing at ".", not created).
//   3. Package-first: from a CLI copied into <package>/cli beside VERSION, manifest.json and skills/, the package's
//      skills/ are listed (SkillPackLookup.PackageRoot), not the repository's; --skills overrides it.
//   4. Usage: unknown, repeated, valueless or positional arguments print skill_list_usage with the usage text.
//   5. Read-only: the repository skills tree and the fixture trees are unchanged (paths and bytes).
//   6. Static: the command loads through JurisdictionRegistry.Load, finds the package via SkillPackLookup.PackageRoot,
//      routes defects through IntentPlan.Internal("skill-list", ...), exposes a public static handler and has no file
//      write API; Program.cs registers the verb.
//   7. P5-23/P5-24 one load: the skills directory is read once on every path. The source calls JurisdictionRegistry.Load(
//      exactly once, takes the catalogue from registry.Catalog and the loader's typed error from load.LoaderError (P5-24),
//      and never reads the directory itself: no LoadDirectory(, LoadLocked(, SkillPackHash., File./Directory. or
//      file-system info type, no SkillLoader use, `using static` or alias (so no second read after a skills_invalid
//      refusal and no race branch); the refusal block prints the loader's error members {code,field,detail,file} from
//      LoaderError. Comments are stripped by a lexer that keeps string and char literals whole ("http://x" is not cut),
//      and checker self-tests on crafted sources prove each bypass is caught. Refusal bytes are pinned by oracle 1/2.
//   8. P5-23 registry issues are dropped: a directory whose registry loads with claim_duplicate (two packs claiming xx.dup)
//      and pack_not_code (a qa pack claiming xx.qa) lists exactly the in-process envelope, which never carries them; the
//      duplicated and the non-code claims flag no pack as stub. (pack_missing cannot arise from a directory load: a
//      sidecar's pack must equal its own directory, which the catalogue holds.)
//   9. P5-23 culture: in process, the oracle envelope built under CurrentCulture de-DE and tr-TR equals the invariant one
//      (every OS). On Linux only (LANG/LC_ALL do not select the .NET culture on Windows or macOS; SKIP elsewhere), CLI
//      children under LANG/LC_ALL de_DE.UTF-8 and tr_TR.UTF-8 (DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0) print the
//      repository listing, the claim fixture listing and a loader refusal byte for byte as under the default culture. A
//      sentinel proves the environment really selects the culture in a .NET child: a pwsh child under the same environment
//      must report CurrentCulture de-DE / tr-TR, and the CLI's runtimeconfig must not force invariant globalization; a
//      missing pwsh or culture data on Linux is a failure, not a skip, so a silently invariant child cannot pass.
//  10. P5-28 twice-read: over one generated package skills directory (a code pack + sidecar), skill-list and
//      jurisdiction-resolve run twice in process and twice as real processes print byte-identical lines on success and
//      on a sidecar refusal, while the two in-process JurisdictionRegistry loads behind them are unequal (TwiceRead).
//  11. P5-29 internal error through the seam: with SkillListCommand.ListingWorkProbe throwing a non-refusal exception
//      (in process), Run prints exactly {"status":"failed","code":"skill_list_internal_error"}, exit 1, and stderr is the
//      IntentPlan diagnostic "skill-list internal error: <type>: <message>" once, for the default directory, an explicit
//      --skills, a missing directory (root_missing otherwise) and a malformed sidecar; List returns the same envelope
//      (Diagnostic set, never throws); a usage refusal never reaches the probe. With the seam null (before and after)
//      the in-process Run prints the real process bytes for a listing and a refusal. Static: over interfaces/**/*.cs
//      (comments and literals blanked) ListingWorkProbe is named exactly twice, both in SkillListCommand.cs: the
//      initializer-free declaration `internal static Action? ListingWorkProbe { get; set; }` and one
//      `ListingWorkProbe?.Invoke();` inside Listing's body before its JurisdictionRegistry.Load(, with List calling
//      Listing inside its try whose catch is IntentPlan.Internal; never assigned; checker self-tests prove each evasion
//      is caught.
//  13. P5-31 post-load seam and a throwing stderr: with SkillListCommand.LoadedProbe (a second null-by-default seam,
//      invoked with the load right after JurisdictionRegistry.Load) throwing, Run prints exactly the typed
//      skill_list_internal_error line, exit 1, the IntentPlan diagnostic once, for the default directory, --skills, a
//      missing directory and a malformed sidecar, the probe holding the load of the one read (a registry, the loader's
//      root_missing, the sidecar json_invalid: so it ran after Load); List returns that envelope; usage never reaches it;
//      a throwing ListingWorkProbe ends the verb first; a non-throwing post-load probe keeps the process bytes. With a
//      Console.Error whose every write throws (IOException, ObjectDisposedException, InvalidOperationException) the
//      internal error (post-load probe, listing probe, NUL path) still prints the typed line, exit 1, one write attempt,
//      nothing escapes Run; a listing, root_missing, sidecar and usage refusal never write stderr and keep their bytes.
//      Static: LoadedProbe is named exactly twice under interfaces/ (initializer-free Action<JurisdictionLoad>
//      declaration, one LoadedProbe?.Invoke(load); right after Listing's `var load = JurisdictionRegistry.Load(skills);`
//      and before `if (load.Refusal`), never assigned (checker self-tests); Run's body and WriteDiagnostic's body are
//      pinned, Console.Error appears in the command only in WriteDiagnostic and not in the MCP tool, which calls
//      SkillListCommand.WriteDiagnostic(envelope) once; documented deltas CommandDeltaP531 and ToolDeltaP531, reversed,
//      give the pre-P5-31 SkillListCommand.cs and SkillListTool.cs byte for byte (SHA-256, "\n" line endings), so the
//      P5-21/P5-22/P5-29 code read by the earlier pins is otherwise unchanged.
//  14. P5-32 selection twice-read: over a generated package with a real code pack (xx.p532) and a stub code pack
//      (xx.p532s) and an outside lock pinning both, jurisdiction-resolve --country/--municipality and --code/--allow-stub
//      under --lock run twice in process (APP_CONTEXT_BASE_DIRECTORY override, restored; negative control
//      skill_lock_missing) and twice as real processes print byte-identical, literally pinned lines: selection success,
//      jurisdiction_unknown, allow-stub success, jurisdiction_stub_refused (by code and by metadata), exit 0/1, empty
//      stderr; with the stub sidecar truncated and repinned, json_invalid whose detail is exactly "$: " + the documented
//      System.Text.Json message (equal to JsonDocument.Parse in this runtime, .NET 8 required and named in the check).
//  15. P5-33 a throwing stdout and the jurisdiction-resolve diagnostic: with a Console.Out whose every write throws
//      (IOException, ObjectDisposedException, InvalidOperationException; the shared tests/cli/RejectingWriter.cs, also
//      linked into tests/mcp), skill-list's success listing, root_missing and usage refusals and internal error (post-load
//      and listing probes) end with exit 1, nothing escaping Run, one stdout write attempt, stderr untouched except the
//      internal error's diagnostic once (also with both streams rejecting); with working streams the in-process bytes
//      equal the real process. jurisdiction-resolve with WorkProbe throwing and a rejecting Console.Error prints the typed
//      jurisdiction_resolve_internal_error line, exit 1, nothing escapes, one stderr attempt; its success and
//      jurisdiction_unknown bytes are unchanged and never write stderr. Real processes (Linux/macOS; SKIP on Windows)
//      with stderr closed (2>&-) or, on Linux, rejecting (2>/dev/full) print the same lines and exits as with stderr open
//      (both verbs); on Linux skill-list with stdout >/dev/full exits 1 with empty stderr. Static: both Run bodies pinned;
//      documented deltas CommandDeltaP533 and ResolveDeltaP533 reversed give the pre-P5-33 files (SHA-256), and section 13
//      reads SkillListCommand.cs with CommandDeltaP533 reversed; RejectingWriter is declared only in the shared file.
//  16. P5-34 ambiguous, conflict and drifted-lock twice-read: over a generated package whose skills hold xx.p534a.pack and
//      xx.p534b.pack (distinct codes, both Fixture Country / Fixture Town: alias_overlap) and xx.p534d.one and
//      xx.p534d.two (both claiming code xx.p534d, Twin Town: claim_duplicate), all pinned in an outside lock,
//      jurisdiction-resolve under --lock runs twice in process (APP_CONTEXT_BASE_DIRECTORY override, restored; negative
//      control skill_lock_missing) and twice as real processes and prints byte-identical, literally pinned lines:
//      jurisdiction_ambiguous by metadata over the overlap, by code over the duplicate and by metadata over the duplicate,
//      the code route success on an overlapping pack; jurisdiction_conflict is pinned in process from
//      JurisdictionRegistry.Select (code with a mismatched country or municipality) and its CLI unreachability as the
//      jurisdiction_resolve_usage line (--code with --country or --municipality is a usage refusal, exit 1); then the
//      xx.p534a.pack sidecar's note is edited after pinning and both a metadata and a code selection over that drifted
//      lock print the literal skill_lock_drift line on xx.p534a.pack.
//  17. P5-35 a throwing stdout in jurisdiction-resolve: with a Console.Out whose every write throws (IOException,
//      ObjectDisposedException, InvalidOperationException; the shared RejectingWriter), the success, jurisdiction_unknown
//      and usage refusals and the internal error (WorkProbe) end with exit 1, nothing escaping Run, one stdout attempt,
//      stderr untouched except the internal error's diagnostic once (also with both streams rejecting); with working
//      streams the in-process bytes equal the real process and the internal error is the typed line. On Linux real
//      processes with >/dev/full exit 1 with empty stderr. Static: Run's body pinned (the P5-33 statements in a try whose
//      catch (Exception) sets exit 1), no Console in the command; the documented delta ResolveDeltaP535 reversed gives the
//      P5-33 file (SHA-256) and section 15 reads the file with it reversed. WriteDiagnostic's home stays SkillListCommand.cs
//      (pinned with the reason: every project compiling a caller already links it; moving it would rewrite the pinned
//      P5-31/P5-33 bytes): declared once there and called exactly by SkillListCommand.Run, SkillListTool,
//      JurisdictionResolveCommand.Run and JurisdictionResolveTool.
internal static class SkillListTests
{
    const string Usage = "usage: skill-list [--skills skills-directory]";
    const string UsageCode = "skill_list_usage";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Stub = "xx.example.codigo-ejemplo";

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "skill-list");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: " + name, detail);

        (int Exit, string Out, string Err) Cli(string dll, params string[] arguments) => CliWith(null, dll, arguments);
        (int Exit, string Out, string Err) CliWith(IReadOnlyDictionary<string, string>? environment, string dll, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add("skill-list");
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
        static string D((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 800 ? r.Out[..800] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");

        void Same(string name, string dll, string skillsDirectory, string? expectCode, params string[] arguments)
        {
            var expected = Expected(skillsDirectory);
            var result = Cli(dll, arguments);
            Check(result.Exit == expected.Exit && result.Out == expected.Line, name + ": stdout equals the in-process loader result", D(result) + " | expected " + expected.Line);
            if (expectCode is not null)
            {
                string? code = null;
                try { using var document = JsonDocument.Parse(result.Out); code = document.RootElement.GetProperty("code").GetString(); }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
                Check(code == expectCode && expected.Line.Contains("\"code\":\"" + expectCode + "\"", StringComparison.Ordinal), name + ": pinned code " + expectCode, D(result));
            }
        }

        var skills = Path.GetFullPath(Path.Combine(root, "skills"));
        var skillsDigest = TreeDigest(skills);

        // ---- 1/2. the repository listing ----
        var repository = Expected(skills);
        Check(repository.Exit == 0, "the repository skills load in process", repository.Line);
        var listing = Cli(cliDll);
        Same("repository listing (no options)", cliDll, skills, null);
        Same("repository listing (--skills)", cliDll, skills, null, "--skills", skills);
        try
        {
            using var document = JsonDocument.Parse(listing.Out);
            var top = document.RootElement;
            Check(string.Join(",", EnumerateNames(top)) == "status,skills_path,packs", "the envelope members are status, skills_path, packs in that order", listing.Out);
            Check(top.GetProperty("status").GetString() == "succeeded" && top.GetProperty("skills_path").GetString() == skills, "status succeeded and skills_path is the repository skills directory", D(listing));
            var ids = top.GetProperty("packs").EnumerateArray().Select(pack => pack.GetProperty("id").GetString()!).ToArray();
            var directories = Directory.GetDirectories(skills).Select(directory => Path.GetFileName(directory)).Order(StringComparer.Ordinal).ToArray();
            Check(ids.SequenceEqual(directories), "pack ids equal the skills/ directory names in ordinal order", string.Join(",", ids) + " | " + string.Join(",", directories));
            var caba = top.GetProperty("packs").EnumerateArray().First(pack => pack.GetProperty("id").GetString() == Caba);
            Check(caba.GetProperty("kind").GetString() == "code" && !caba.GetProperty("stub").GetBoolean() && caba.GetProperty("source_confidence").GetProperty("rules").GetInt32() > 0,
                "CABA is a code pack, not a stub, with rules", caba.GetRawText());
            Check(string.Join(",", EnumerateNames(caba)) == "id,version,kind,stub,source_confidence"
                && string.Join(",", EnumerateNames(caba.GetProperty("source_confidence"))) == "rules,cited,owner-confirmed,to-verify,lowest",
                "pack members are id, version, kind, stub, source_confidence{rules, cited, owner-confirmed, to-verify, lowest}", caba.GetRawText());
            var stub = top.GetProperty("packs").EnumerateArray().First(pack => pack.GetProperty("id").GetString() == Stub);
            Check(stub.GetProperty("stub").GetBoolean(), "the xx.example pack is flagged stub", stub.GetRawText());
            Check(top.GetProperty("packs").EnumerateArray().Where(pack => pack.GetProperty("kind").GetString() != "code").All(pack => !pack.GetProperty("stub").GetBoolean()),
                "packs of other kinds are not stubs", listing.Out);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { Check(false, "the repository listing is a JSON envelope", D(listing) + " " + error.Message); }
        var again = Cli(cliDll);
        Check(again.Exit == listing.Exit && again.Out == listing.Out, "two runs print the same bytes (deterministic)", D(again));

        // ---- malformed skill.json ----
        var malformed = Path.Combine(scratch, "malformed");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(malformed, Caba));
        Directory.CreateDirectory(Path.Combine(malformed, "bim.broken.pack"));
        File.WriteAllText(Path.Combine(malformed, "bim.broken.pack", "skill.json"), "{ \"manifest_version\": 1, ", new UTF8Encoding(false));
        var malformedDigest = TreeDigest(malformed);
        Same("malformed skill.json", cliDll, malformed, SkillCodes.JsonInvalid, "--skills", malformed);
        var malformedResult = Cli(cliDll, "--skills", malformed);
        Check(malformedResult.Out.Contains("\"file\":\"bim.broken.pack/skill.json\"", StringComparison.Ordinal), "the malformed manifest is named by its root-relative path", D(malformedResult));
        Check(TreeDigest(malformed) == malformedDigest, "the malformed fixture is unchanged");

        // ---- malformed jurisdiction.json sidecar (the registry's refusal, after the loader accepted the manifests) ----
        var badSidecar = Path.Combine(scratch, "bad-sidecar");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(badSidecar, Caba));
        File.WriteAllText(Path.Combine(badSidecar, Caba, "jurisdiction.json"), "{ \"jurisdiction_version\": 1, ", new UTF8Encoding(false));
        var badSidecarDigest = TreeDigest(badSidecar);
        Same("malformed jurisdiction.json", cliDll, badSidecar, JurisdictionCodes.JsonInvalid, "--skills", badSidecar);
        var sidecarResult = Cli(cliDll, "--skills", badSidecar);
        string? sidecarSubject = null;
        var sidecarMembers = "";
        try
        {
            using var document = JsonDocument.Parse(sidecarResult.Out);
            sidecarMembers = string.Join(",", EnumerateNames(document.RootElement));
            sidecarSubject = document.RootElement.GetProperty("subject").GetString();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        Check(sidecarResult.Exit == 1 && sidecarMembers == "status,code,subject,detail" && sidecarResult.Out.StartsWith("{\"status\":\"failed\",\"code\":\"json_invalid\",", StringComparison.Ordinal)
            && sidecarSubject == Caba + "/jurisdiction.json",
            "a malformed sidecar prints {status,code,subject,detail} with code json_invalid and subject " + Caba + "/jurisdiction.json, exit 1", D(sidecarResult));
        Check(TreeDigest(badSidecar) == badSidecarDigest, "the malformed sidecar fixture is unchanged (nothing written)");

        // ---- a trailing directory separator prints the same skills_path as the default ----
        foreach (var separator in new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }.Distinct())
        {
            var trailing = Cli(cliDll, "--skills", skills + separator);
            Check(trailing.Exit == 0 && trailing.Out == listing.Out && trailing.Out.Contains("\"skills_path\":" + JsonSerializer.Serialize(skills) + ",", StringComparison.Ordinal),
                "--skills with a trailing '" + separator + "' prints the default listing (skills_path without the separator)", D(trailing));
        }

        // ---- missing directory ----
        var absent = Path.Combine(scratch, "absent-skills");
        Same("missing directory", cliDll, absent, SkillCodes.RootMissing, "--skills", absent);
        Check(!Directory.Exists(absent), "a missing skills directory is not created");

        // ---- relative --skills resolves against the working directory ----
        var relative = Cli(cliDll, "--skills", Path.GetRelativePath(Environment.CurrentDirectory, skills));
        Check(relative.Exit == repository.Exit && relative.Out == repository.Line, "a relative --skills path lists the same directory", D(relative));

        // ---- 3. package-first ----
        var package = Path.Combine(scratch, "pkg");
        var packageCli = Path.Combine(package, "cli");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var packageSkills = Path.Combine(package, "skills");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(packageSkills, Caba));
        CopyDirectory(Path.Combine(skills, Stub), Path.Combine(packageSkills, Stub));
        var packageDll = Path.Combine(packageCli, "Bim.Cli.dll");
        // The packaged binary directory may reach the CLI through a resolved path (macOS /var -> /private/var), so the
        // printed skills_path is checked to be the package's skills/ (same tree digest) and then used for the oracle.
        var packageListing = Cli(packageDll);
        string? packagePath = null;
        try { using var document = JsonDocument.Parse(packageListing.Out); packagePath = document.RootElement.GetProperty("skills_path").GetString(); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        var packageExpected = packagePath is null ? (Exit: -1, Line: "") : Expected(packagePath);
        Check(packagePath is not null && Path.GetFileName(packagePath) == "skills" && Path.GetFileName(Path.GetDirectoryName(packagePath)) == "pkg"
            && Directory.Exists(packagePath) && TreeDigest(packagePath) == TreeDigest(packageSkills),
            "the packaged CLI resolves the package's skills/ directory", D(packageListing));
        Check(packageListing.Exit == 0 && packageListing.Out == packageExpected.Line, "package listing: stdout equals the in-process loader result", D(packageListing) + " | expected " + packageExpected.Line);
        Check(packageListing.Out.Contains("\"id\":\"" + Caba + "\"", StringComparison.Ordinal) && packageListing.Out.Contains("\"id\":\"" + Stub + "\"", StringComparison.Ordinal)
            && !packageListing.Out.Contains("bim.style.", StringComparison.Ordinal),
            "the packaged CLI lists the package skills, not the repository's", D(packageListing));
        Same("package CLI with --skills over the repository", packageDll, skills, null, "--skills", skills);
        File.WriteAllText(Path.Combine(packageSkills, Caba, "skill.json"), "{ \"manifest_version\": 1, ", new UTF8Encoding(false));
        Same("malformed packaged skill.json", packageDll, packageSkills, SkillCodes.JsonInvalid);

        // ---- 4. usage ----
        var usageLine = JsonSerializer.Serialize(new { status = "failed", code = UsageCode, detail = Usage });
        foreach (var (label, arguments) in new (string, string[])[]
        {
            ("--skills without a value", new[] { "--skills" }),
            ("--skills followed by an option", new[] { "--skills", "--skills" }),
            ("--skills repeated", new[] { "--skills", skills, "--skills", skills }),
            ("blank --skills", new[] { "--skills", " " }),
            ("empty --skills", new[] { "--skills", "" }),
            ("unknown option", new[] { "--lock", "skills.lock.json" }),
            ("positional argument", new[] { skills }),
        })
        {
            var result = Cli(cliDll, arguments);
            Check(result.Exit == 1 && result.Out == usageLine, "usage refusal: " + label, D(result));
        }

        // ---- 8. P5-23: registry issues of a successful load are dropped ----
        var issues = Path.Combine(scratch, "claim-fixture");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(issues, Caba));
        var stubManifest = File.ReadAllText(Path.Combine(skills, Stub, "skill.json"));
        foreach (var id in new[] { "xx.dup.pack-a", "xx.dup.pack-b" })
        {
            Directory.CreateDirectory(Path.Combine(issues, id));
            File.WriteAllText(Path.Combine(issues, id, "skill.json"), stubManifest.Replace("\"id\": \"" + Stub + "\"", "\"id\": \"" + id + "\"", StringComparison.Ordinal), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(issues, id, "jurisdiction.json"),
                "{\"jurisdiction_version\":1,\"code\":\"xx.dup\",\"pack\":\"" + id + "\",\"country_names\":[\"Duplicate Country\"],\"municipality_names\":[\"Duplicate City\"],\"stub\":true,\"note\":\"fictitious test fixture\"}\n",
                new UTF8Encoding(false));
        }
        const string QaSource = "bim.qa.element-basics";
        const string QaClaimed = "xx.qa.element-basics";
        Directory.CreateDirectory(Path.Combine(issues, QaClaimed));
        File.WriteAllText(Path.Combine(issues, QaClaimed, "skill.json"),
            File.ReadAllText(Path.Combine(skills, QaSource, "skill.json")).Replace("\"" + QaSource + "\"", "\"" + QaClaimed + "\"", StringComparison.Ordinal), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(issues, QaClaimed, "jurisdiction.json"),
            "{\"jurisdiction_version\":1,\"code\":\"xx.qa\",\"pack\":\"" + QaClaimed + "\",\"country_names\":[\"Qa Country\"],\"municipality_names\":[\"Qa City\"],\"stub\":true,\"note\":\"fictitious test fixture\"}\n",
            new UTF8Encoding(false));
        var issuesDigest = TreeDigest(issues);
        var issuesLoad = JurisdictionRegistry.Load(issues);
        var issueCodes = issuesLoad.Registry?.Issues.Select(issue => issue.Code).ToArray() ?? Array.Empty<string>();
        Check(issuesLoad.Succeeded && issueCodes.Contains(JurisdictionCodes.ClaimDuplicate) && issueCodes.Contains(JurisdictionCodes.PackNotCode),
            "the registry-issues fixture loads in process with claim_duplicate and pack_not_code", issuesLoad.Refusal?.ToString() ?? string.Join(",", issueCodes));
        Same("registry issues dropped", cliDll, issues, null, "--skills", issues);
        var issuesResult = Cli(cliDll, "--skills", issues);
        Check(issuesResult.Exit == 0 && !issuesResult.Out.Contains("issue", StringComparison.Ordinal) && !issuesResult.Out.Contains(JurisdictionCodes.ClaimDuplicate, StringComparison.Ordinal)
            && !issuesResult.Out.Contains(JurisdictionCodes.PackNotCode, StringComparison.Ordinal) && !issuesResult.Out.Contains(JurisdictionCodes.PackMissing, StringComparison.Ordinal),
            "registry issues are dropped: the success envelope never names claim_duplicate, pack_not_code or pack_missing", D(issuesResult));
        try
        {
            using var document = JsonDocument.Parse(issuesResult.Out);
            Check(string.Join(",", EnumerateNames(document.RootElement)) == "status,skills_path,packs", "with registry issues the envelope members are still status, skills_path, packs", issuesResult.Out);
            var stubs = document.RootElement.GetProperty("packs").EnumerateArray().Where(pack => pack.GetProperty("stub").GetBoolean()).Select(pack => pack.GetProperty("id").GetString()).ToArray();
            Check(stubs.Length == 0, "duplicated and non-code stub claims flag no pack as stub (ClaimFor is null for both)", string.Join(",", stubs));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { Check(false, "the registry-issues listing is a JSON envelope", D(issuesResult) + " " + error.Message); }
        Check(TreeDigest(issues) == issuesDigest, "the registry-issues fixture is unchanged");

        // ---- 9. P5-23: a non-invariant culture prints the same bytes ----
        foreach (var name in new[] { "de-DE", "tr-TR" })
        {
            var available = false;
            try { available = CultureInfo.GetCultureInfo(name).NumberFormat.NumberDecimalSeparator == ","; }
            catch (CultureNotFoundException) { }
            if (!available)
            {
                if (OperatingSystem.IsLinux()) Check(false, name + " culture data with a comma decimal separator exists on Linux", "CultureInfo.GetCultureInfo(\"" + name + "\") unavailable or not comma-decimal");
                else Console.WriteLine("SKIP skill-list: " + name + " culture data unavailable (invariant globalization); in-process culture check not run");
                continue;
            }
            var previous = CultureInfo.CurrentCulture;
            var previousUi = CultureInfo.CurrentUICulture;
            (int Exit, string Line) invariantRepository = Expected(skills), invariantIssues = Expected(issues), culturedRepository, culturedIssuesExpected;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                culturedRepository = Expected(skills);
                culturedIssuesExpected = Expected(issues);
            }
            finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
            Check(culturedRepository == invariantRepository && culturedIssuesExpected == invariantIssues && invariantRepository.Line == listing.Out,
                "in process under " + name + " current culture the listing envelopes are byte-identical", culturedRepository.Line);
        }
        if (!OperatingSystem.IsLinux())
            Console.WriteLine("SKIP skill-list: child-process culture runs need Linux (LANG/LC_ALL do not select the .NET culture on Windows or macOS)");
        else
        {
            var runtimeConfig = Path.ChangeExtension(cliDll, ".runtimeconfig.json");
            var forcedInvariant = File.Exists(runtimeConfig) && File.ReadAllText(runtimeConfig).Contains("\"System.Globalization.Invariant\": true", StringComparison.Ordinal);
            Check(File.Exists(runtimeConfig) && !forcedInvariant, "the CLI runtimeconfig does not force invariant globalization", runtimeConfig);
            foreach (var (locale, name) in new[] { ("de_DE.UTF-8", "de-DE"), ("tr_TR.UTF-8", "tr-TR") })
            {
                var env = new Dictionary<string, string> { ["LANG"] = locale, ["LC_ALL"] = locale, ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "0" };
                // Sentinel: a .NET child (pwsh) under the same environment must really select the culture.
                string sentinel;
                try
                {
                    var probe = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "[System.Globalization.CultureInfo]::CurrentCulture.Name + '|' + (1.5).ToString([System.Globalization.CultureInfo]::CurrentCulture)" })
                        probe.ArgumentList.Add(argument);
                    foreach (var (key, value) in env) probe.Environment[key] = value;
                    using var process = Process.Start(probe) ?? throw new InvalidOperationException("pwsh did not start");
                    var probeOut = process.StandardOutput.ReadToEndAsync();
                    var probeErr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(120000)) { process.Kill(true); sentinel = "<timed out>"; }
                    else { process.WaitForExit(); sentinel = probeOut.Result.Trim() + (probeErr.Result.Trim().Length > 0 ? " stderr " + probeErr.Result.Trim() : ""); }
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { sentinel = "<pwsh unavailable: " + error.Message + ">"; }
                Check(sentinel == name + "|1,5", "sentinel: a .NET child under LANG/LC_ALL " + locale + " runs with CurrentCulture " + name + " (comma decimal)", sentinel);
                var cultured = CliWith(env, cliDll);
                Check(cultured.Exit == listing.Exit && cultured.Out == listing.Out, "under " + locale + " the repository listing prints the same bytes as under the default culture", D(cultured));
                var culturedIssues = CliWith(env, cliDll, "--skills", issues);
                Check(culturedIssues.Exit == issuesResult.Exit && culturedIssues.Out == issuesResult.Out, "under " + locale + " the claim fixture listing prints the same bytes", D(culturedIssues));
                var culturedRefusal = CliWith(env, cliDll, "--skills", malformed);
                Check(culturedRefusal.Exit == malformedResult.Exit && culturedRefusal.Out == malformedResult.Out, "under " + locale + " a loader refusal prints the same bytes", D(culturedRefusal));
            }
        }

        // ---- 5. read-only ----
        var skillsAfter = TreeDigest(skills);
        Check(skillsAfter == skillsDigest, "every run leaves the repository skills tree unchanged (paths and bytes)", skillsDigest + " -> " + skillsAfter);

        // ---- 6. static ----
        var sourcePath = Path.Combine(root, "interfaces", "cli", "SkillListCommand.cs");
        var source = File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : "";
        Check(source.Contains("JurisdictionRegistry.Load(", StringComparison.Ordinal)
            && source.Contains("SkillPackLookup.PackageRoot(", StringComparison.Ordinal) && source.Contains("IntentPlan.Internal(\"skill-list\"", StringComparison.Ordinal),
            "the command loads through JurisdictionRegistry.Load, finds the package with SkillPackLookup.PackageRoot and reports defects with IntentPlan.Internal");
        // 7. P5-23 one load: comments are stripped so only code is counted.
        var codeText = StripComments(source);
        static int Count(string text, string token) { var n = 0; for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal)) n++; return n; }
        var registryAt = codeText.IndexOf("JurisdictionRegistry.Load(", StringComparison.Ordinal);
        Check(Count(codeText, "JurisdictionRegistry.Load(") == 1 && registryAt >= 0 && codeText.Contains("registry.Catalog", StringComparison.Ordinal),
            "one JurisdictionRegistry.Load supplies catalogue and claims",
            "JurisdictionRegistry.Load x" + Count(codeText, "JurisdictionRegistry.Load(").ToString(CultureInfo.InvariantCulture) + " at " + registryAt.ToString(CultureInfo.InvariantCulture));
        var violations = ReadViolations(codeText);
        Check(violations.Count == 0,
            "P5-24: the skills directory is read once on every path: the command never reads the skills directory itself (no loader, lock, hash or file-system read API; no SkillLoader import or alias)",
            string.Join(", ", violations));
        // Checker self-tests: each bypass of the read ban in a crafted source is caught, and comments (but not string
        // literals that contain "//" or "/*") are stripped before the scan.
        const string Q = "\"";
        foreach (var (label, crafted, expected) in new (string, string, string?)[]
        {
            ("clean source with comments naming the loader", "var load = JurisdictionRegistry.Load(skills); var url = " + Q + "http://x" + Q + "; // SkillLoader.LoadDirectory(skills)\n/* File.ReadAllText(x); LoadLocked( */ var y = 1;", null),
            ("a call after a string holding //", "var url = " + Q + "http://x" + Q + "; var c = SkillLoader.LoadDirectory(skills);", "LoadDirectory("),
            ("a call after a string holding /*", "var glob = " + Q + "a/*b" + Q + "; var c = SkillLoader.LoadDirectory(skills); var d = " + Q + "*/" + Q + ";", "LoadDirectory("),
            ("a call after a verbatim string ending in a backslash", "var dir = @" + Q + "c:\\" + Q + "; var url = " + Q + "http://x" + Q + "; var c = Bim.Core.SkillLoader.LoadDirectory(skills);", "LoadDirectory("),
            ("a call after a verbatim string with a doubled quote", "var t = @" + Q + "a" + Q + Q + "//" + Q + "; var c = SkillLoader.LoadDirectory(skills);", "LoadDirectory("),
            ("a call after a raw string holding // and a quote", "var t = " + Q + Q + Q + "a " + Q + "// b" + Q + Q + Q + "; var c = SkillLoader.LoadDirectory(skills);", "LoadDirectory("),
            ("a call after a quote char literal", "var q = '" + Q + "'; var url = " + Q + "http://x" + Q + "; var c = SkillLoader.LoadDirectory(skills);", "LoadDirectory("),
            ("a call after an escaped quote in a string", "var t = " + Q + "a\\" + Q + "//" + Q + "; var c = SkillLoader.LoadDirectory(skills);", "LoadDirectory("),
            ("using static SkillLoader", "using static Bim.Core.SkillLoader;\nvar c = LoadDirectory (skills);", "using static SkillLoader"),
            ("using static SkillLoader with global::", "using static global::Bim.Core.SkillLoader;", "using static SkillLoader"),
            ("a SkillLoader alias", "using Reader = Bim.Core.SkillLoader;\nvar c = Reader.Load" + "Directory(skills);", "SkillLoader alias"),
            ("a global:: SkillLoader alias", "using Reader = global::Bim.Core.SkillLoader;", "SkillLoader alias"),
            ("any SkillLoader use", "var max = SkillLoader.MaxSkills;", "SkillLoader"),
            ("LoadLocked", "var load = JurisdictionRegistry.LoadLocked(skills, lockPath);", "LoadLocked("),
            ("SkillPackHash", "var snapshot = SkillPackHash.Snapshot(skills, id);", "SkillPackHash."),
            ("File read API", "var text = File.ReadAllText(path);", "File."),
            ("System.IO.File read API", "var bytes = System.IO.File.ReadAllBytes(path);", "File."),
            ("Directory read API", "var names = Directory.EnumerateDirectories(skills);", "Directory."),
            ("DirectoryInfo", "var info = new DirectoryInfo(skills);", "file-system info type"),
            ("FileInfo", "var info = new System.IO.FileInfo(path);", "file-system info type"),
            ("block comment does not hide a call", "/* note */ var c = SkillLoader.LoadDirectory(skills); /* end */", "LoadDirectory("),
        })
        {
            var found = ReadViolations(StripComments(crafted));
            Check(expected is null ? found.Count == 0 : found.Contains(expected),
                "read-ban checker self-test: " + label + (expected is null ? " passes" : " is caught as " + expected), string.Join(", ", found) + " | " + crafted);
        }
        Check(StripComments("var url = " + Q + "http://x" + Q + "; // tail") == "var url = " + Q + "http://x" + Q + "; ",
            "comment stripping keeps a string literal holding // and drops the trailing comment", StripComments("var url = " + Q + "http://x" + Q + "; // tail"));
        var refusalBlock = Block(codeText, "if (load.Refusal is { } refusal)");
        Check(refusalBlock.Contains("load.LoaderError", StringComparison.Ordinal) && !refusalBlock.Contains("registry.Catalog", StringComparison.Ordinal)
            && !refusalBlock.Contains("JurisdictionCodes.SkillsInvalid", StringComparison.Ordinal),
            "P5-24: the refusal block prints the loader's typed error from load.LoaderError, with no skills_invalid race branch", "refusal block: " + refusalBlock);
        Check(!codeText.Contains(".Issues", StringComparison.Ordinal), "registry issues are not read by the command (dropped)");
        Check(source.Contains("public static IntentEnvelope List(", StringComparison.Ordinal), "the handler is a public static IntentEnvelope List(...) for reuse by the MCP tool");
        Check(!source.Contains(" internal error: ", StringComparison.Ordinal), "the command hand-writes no internal-error stderr line");
        Check(new[] { "File.Write", "File.Create", "File.Move", "File.Copy", "File.Delete", "File.Open", "FileMode.", "FileStream", "Directory.Create", "Directory.Move", "Directory.Delete" }
            .All(api => !source.Contains(api, StringComparison.Ordinal)), "the command has no file write API (read-only)");
        var program = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs"));
        Check(program.Contains("case \"skill-list\": SkillListCommand.Run(args); break;", StringComparison.Ordinal) && program.Contains("| skill-list [--skills skills-directory] |", StringComparison.Ordinal),
            "Program.cs registers skill-list and lists it in the usage text");

        // ---- 10. P5-28 twice-read bytes pin ----
        TwiceRead(scratch, dotnet, cliDll, check);

        // ---- 11. P5-29 internal error forced through the ListingWorkProbe seam ----
        Seam(root, scratch, dotnet, cliDll, check);

        // ---- 12. P5-30 twice-read bytes under --lock ----
        LockTwiceRead(scratch, dotnet, cliDll, check);

        // ---- 13. P5-31 post-load seam and a throwing stderr ----
        PostLoad(root, scratch, dotnet, cliDll, check);

        // ---- 14. P5-32 twice-read for selection flags and --allow-stub under --lock ----
        SelectionTwiceRead(scratch, dotnet, cliDll, check);

        // ---- 15. P5-33 a throwing stdout in skill-list and a throwing stderr in jurisdiction-resolve ----
        StdoutFailure(root, scratch, dotnet, cliDll, check);

        // ---- 16. P5-34 ambiguous, conflict and drifted-lock twice-read under --lock ----
        AmbiguityTwiceRead(scratch, dotnet, cliDll, check);

        // ---- 17. P5-35 a throwing stdout in jurisdiction-resolve and WriteDiagnostic's pinned home ----
        ResolveStdoutFailure(root, dotnet, cliDll, check);
    }

    // Section 11 (P5-29): see oracle 11 in the header.
    static void Seam(string root, string scratch, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: P5-29 seam: " + name, detail);
        const string InternalLine = "{\"status\":\"failed\",\"code\":\"skill_list_internal_error\"}";
        var skills = Path.GetFullPath(Path.Combine(root, "skills"));
        var absent = Path.Combine(scratch, "p529-absent");
        var badSidecar = Path.Combine(scratch, "p529-bad-sidecar");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(badSidecar, Caba));
        File.WriteAllText(Path.Combine(badSidecar, Caba, "jurisdiction.json"), "{ \"jurisdiction_version\": 1, ", new UTF8Encoding(false));

        // The verb in process: raw stdout, stderr, exit and any escaping exception; globals restored in the finally.
        static (int Exit, string Out, string Err, string Thrown) Inline(string[] arguments)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var thrown = "";
            int exit;
            try
            {
                Environment.ExitCode = 0;
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                try { Bim.Cli.SkillListCommand.Run(arguments); }
                catch (Exception failure) { thrown = failure.GetType().FullName + ": " + failure.Message; }
            }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
            return (exit, capturedOut.ToString(), capturedError.ToString(), thrown);
        }
        (int Exit, string Out, string Err) Child(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            start.ArgumentList.Add("skill-list");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
        static string D((int Exit, string Out, string Err, string Thrown) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out) + " stderr " + JsonSerializer.Serialize(r.Err) + (r.Thrown.Length > 0 ? " threw " + r.Thrown : "");

        // Seam null: the in-process verb prints the real process bytes (listing and refusal).
        void Unchanged(string phase)
        {
            Check(Bim.Cli.SkillListCommand.ListingWorkProbe is null, phase + ": the seam is null");
            foreach (var (label, arguments) in new (string, string[])[] { ("repository listing", ["--skills", skills]), ("root_missing refusal", ["--skills", absent]), ("sidecar refusal", ["--skills", badSidecar]) })
            {
                var inline = Inline(["skill-list", .. arguments]);
                var child = Child(arguments);
                Check(inline.Exit == child.Exit && inline.Out == child.Out && inline.Err.Length == 0 && child.Err.Length == 0 && inline.Thrown.Length == 0 && inline.Out != InternalLine + Environment.NewLine,
                    phase + ": " + label + " in process equals the real process bytes", D(inline) + " | process exit " + child.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(child.Out));
            }
            Check(!Directory.Exists(absent), phase + ": the missing directory is not created");
        }
        Unchanged("before");

        var probes = 0;
        foreach (var failure in new Exception[] { new InvalidOperationException("boom skill-list P5-29"), new IOException("disk P5-29"), new NullReferenceException("null P5-29") })
        {
            var diagnostic = "skill-list internal error: " + failure.GetType().FullName + ": " + failure.Message;
            probes = 0;
            Bim.Cli.SkillListCommand.ListingWorkProbe = () => { probes++; throw failure; };
            try
            {
                foreach (var (label, arguments) in new (string, string[])[] { ("default directory", []), ("explicit --skills", ["--skills", skills]), ("missing directory", ["--skills", absent]), ("malformed sidecar", ["--skills", badSidecar]) })
                {
                    var before = probes;
                    var run = Inline(["skill-list", .. arguments]);
                    Check(run.Exit == 1 && run.Out == InternalLine + Environment.NewLine && run.Err == diagnostic + Environment.NewLine && run.Thrown.Length == 0 && probes == before + 1,
                        failure.GetType().Name + " " + label + ": the typed skill_list_internal_error line, exit 1, the diagnostic once", D(run) + " probes " + (probes - before).ToString(CultureInfo.InvariantCulture));
                }
                Bim.Cli.IntentEnvelope? envelope = null;
                var escaped = "";
                try { envelope = Bim.Cli.SkillListCommand.List(skills); }
                catch (Exception error) { escaped = error.GetType().Name; }
                Check(escaped.Length == 0 && envelope is { Line: InternalLine, Exit: 1 } && envelope.Diagnostic == diagnostic && !envelope.Succeeded,
                    failure.GetType().Name + ": List returns the typed envelope with the diagnostic and never throws", (envelope?.ToString() ?? "null") + " " + escaped);
                var usageBefore = probes;
                var usage = Inline(["skill-list", "--bogus"]);
                Check(usage.Exit == 1 && usage.Out == JsonSerializer.Serialize(new { status = "failed", code = UsageCode, detail = Usage }) + Environment.NewLine && usage.Err.Length == 0 && probes == usageBefore,
                    failure.GetType().Name + ": a usage refusal precedes the probe (never invoked) and is unchanged", D(usage));
            }
            finally { Bim.Cli.SkillListCommand.ListingWorkProbe = null; }
        }
        // A probe that does not throw changes nothing.
        Bim.Cli.SkillListCommand.ListingWorkProbe = () => probes++;
        try
        {
            probes = 0;
            var quiet = Inline(["skill-list", "--skills", skills]);
            var child = Child("--skills", skills);
            Check(probes == 1 && quiet.Exit == 0 && quiet.Out == child.Out && quiet.Err.Length == 0, "a non-throwing probe runs once and keeps the listing bytes", D(quiet));
        }
        finally { Bim.Cli.SkillListCommand.ListingWorkProbe = null; }
        Unchanged("after");

        // Static pin over interfaces/**/*.cs.
        var interfaces = Path.Combine(root, "interfaces");
        var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(interfaces, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal).Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path))).ToArray();
        var problems = SeamProblems(sources);
        Check(problems.Count == 0, "interfaces/**/*.cs names ListingWorkProbe only in its initializer-free declaration and one invoke inside Listing before JurisdictionRegistry.Load(, List guards Listing, never assigned",
            string.Join("; ", problems));
        const string SeamPath = "interfaces/cli/SkillListCommand.cs";
        const string Decl = "internal static Action? ListingWorkProbe { get; set; }";
        const string Guard = "public static IntentEnvelope List(string? skillsDirectory) { try { return Listing(skillsDirectory); } catch (Exception error) { return IntentPlan.Internal(\"skill-list\", error); } }";
        const string Good = "ListingWorkProbe?.Invoke(); if (skillsDirectory is null) { skillsDirectory = \"x\"; } var load = JurisdictionRegistry.Load(skills); return null;";
        static string Command(string listingBody, string declaration = Decl, string list = Guard, string extra = "") =>
            "internal static class SkillListCommand { " + declaration + " static IntentEnvelope Execute(string[] args) { return List(null); } " + list
            + " static IntentEnvelope Listing(string? skillsDirectory) { " + listingBody + " } " + extra + " }";
        Check(SeamProblems([(SeamPath, Command(Good))]).Count == 0
            && SeamProblems([(SeamPath, Command("ListingWorkProbe ?. Invoke ( ) ;\n // ListingWorkProbe = null;\n var s = \"ListingWorkProbe = x\"; var load = JurisdictionRegistry . Load (skills); return null;"))]).Count == 0
            && SeamProblems([(SeamPath, Command(Good)), ("interfaces/cli/JurisdictionResolveCommand.cs", "class J { internal static Action? WorkProbe { get; set; } void X() { WorkProbe?.Invoke(); } }")]).Count == 0,
            "self-test: the seam checker accepts the real shape at any spacing, ignoring comments, literals and other seams", string.Join("; ", SeamProblems([(SeamPath, Command(Good))])));
        foreach (var (label, files) in new (string, (string, string)[])[]
        {
            ("an initializer", [(SeamPath, Command(Good, "internal static Action? ListingWorkProbe { get; set; } = null;"))]),
            ("a field declaration", [(SeamPath, Command(Good, "internal static Action? ListingWorkProbe;"))]),
            ("a public declaration", [(SeamPath, Command(Good, "public static Action? ListingWorkProbe { get; set; }"))]),
            ("the declaration missing", [(SeamPath, Command(Good, ""))]),
            ("an assignment in the command", [(SeamPath, Command(Good, Decl, Guard, "static void Set() { ListingWorkProbe = () => throw null!; }"))]),
            ("an assignment in another interfaces file", [(SeamPath, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { SkillListCommand.ListingWorkProbe = null; } }")]),
            ("a += assignment", [(SeamPath, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { SkillListCommand.ListingWorkProbe += null; } }")]),
            ("a ??= assignment", [(SeamPath, Command(Good)), ("interfaces/mcp/SkillListTool.cs", "class T { void X() { SkillListCommand.ListingWorkProbe ??= null; } }")]),
            ("a use in the tool", [(SeamPath, Command(Good)), ("interfaces/mcp/SkillListTool.cs", "class T { void X() { SkillListCommand.ListingWorkProbe?.Invoke(); } }")]),
            ("the invoke after JurisdictionRegistry.Load(", [(SeamPath, Command("var load = JurisdictionRegistry.Load(skills); ListingWorkProbe?.Invoke(); return null;"))]),
            ("the invoke only in a comment", [(SeamPath, Command("// ListingWorkProbe?.Invoke();\n var load = JurisdictionRegistry.Load(skills); return null;"))]),
            ("the invoke missing", [(SeamPath, Command("var load = JurisdictionRegistry.Load(skills); return null;"))]),
            ("a second invoke", [(SeamPath, Command("ListingWorkProbe?.Invoke(); ListingWorkProbe?.Invoke(); var load = JurisdictionRegistry.Load(skills); return null;"))]),
            ("the invoke outside Listing (unguarded in List)", [(SeamPath, Command("var load = JurisdictionRegistry.Load(skills); return null;", Decl,
                "public static IntentEnvelope List(string? skillsDirectory) { ListingWorkProbe?.Invoke(); try { return Listing(skillsDirectory); } catch (Exception error) { return IntentPlan.Internal(\"skill-list\", error); } }"))]),
            ("the invoke in Execute", [(SeamPath, Command("var load = JurisdictionRegistry.Load(skills); return null;", Decl, Guard, "static void Other() { ListingWorkProbe?.Invoke(); }"))]),
            ("List not guarding Listing", [(SeamPath, Command(Good, Decl, "public static IntentEnvelope List(string? skillsDirectory) => Listing(skillsDirectory);"))]),
            ("List guarding with another catch", [(SeamPath, Command(Good, Decl, "public static IntentEnvelope List(string? skillsDirectory) { try { return Listing(skillsDirectory); } catch (IOException error) { return IntentPlan.Internal(\"skill-list\", error); } }"))]),
            ("the command file missing", [("interfaces/cli/Other.cs", "class O { }")]),
        })
            Check(SeamProblems(files).Count > 0, "self-test: the seam checker flags " + label);
    }

    // P5-29: over (relative path, source) pairs under interfaces/, comments removed and literals blanked
    // (ApplyIntentTests.CodeOf): ListingWorkProbe (whole word) is named exactly twice, both in
    // interfaces/cli/SkillListCommand.cs: `internal static Action? ListingWorkProbe { get; set; }` with no initializer and
    // one `ListingWorkProbe?.Invoke();` inside the brace-matched body of Listing before its JurisdictionRegistry.Load(;
    // List's body is exactly `try { return Listing(skillsDirectory); } catch (Exception error) { return IntentPlan.Internal(..., error); }`
    // (whitespace-insensitive); no assignment (=, +=, ??=, ...) anywhere.
    static List<string> SeamProblems(IEnumerable<(string Name, string Source)> files)
    {
        const string Command = "interfaces/cli/SkillListCommand.cs";
        var problems = new List<string>();
        var total = 0;
        var sawCommand = false;
        foreach (var (name, source) in files)
        {
            var code = ApplyIntentTests.CodeOf(source);
            total += System.Text.RegularExpressions.Regex.Matches(code, @"\bListingWorkProbe\b").Count;
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bListingWorkProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": ListingWorkProbe assigned");
            if (name != Command) continue;
            sawCommand = true;
            var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+static\s+Action\s*\?\s*ListingWorkProbe\s*\{\s*get\s*;\s*set\s*;\s*\}(?!\s*=)").Count;
            if (declarations != 1) problems.Add(name + ": the initializer-free declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
            var invokes = System.Text.RegularExpressions.Regex.Matches(code, @"\bListingWorkProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;").Count;
            if (invokes != 1) problems.Add(name + ": " + invokes.ToString(CultureInfo.InvariantCulture) + " ListingWorkProbe?.Invoke() calls");
            var listing = BodyAfter(code, @"\bstatic\s+IntentEnvelope\s+Listing\s*\(");
            var invoke = System.Text.RegularExpressions.Regex.Match(listing, @"\bListingWorkProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;");
            var load = System.Text.RegularExpressions.Regex.Match(listing, @"\bJurisdictionRegistry\s*\.\s*Load\s*\(");
            if (!invoke.Success || !load.Success || invoke.Index > load.Index) problems.Add(name + ": ListingWorkProbe?.Invoke() not inside Listing before its JurisdictionRegistry.Load(");
            var list = System.Text.RegularExpressions.Regex.Replace(BodyAfter(code, @"\bpublic\s+static\s+IntentEnvelope\s+List\s*\("), @"\s+", "");
            if (list != "try{returnListing(skillsDirectory);}catch(Exceptionerror){returnIntentPlan.Internal(\"\",error);}") problems.Add(name + ": List does not run Listing inside its try/catch (Exception) IntentPlan.Internal [" + list + "]");
        }
        if (!sawCommand) problems.Add(Command + " not found");
        if (total != 2) problems.Add("ListingWorkProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // The brace-matched body of the first block opened after the first match of pattern ("" when absent or when an
    // expression body (=>) or a ';' comes before the brace).
    static string BodyAfter(string code, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(code, pattern);
        if (!match.Success) return "";
        var open = code.IndexOf('{', match.Index + match.Length);
        if (open < 0) return "";
        var between = code[(match.Index + match.Length)..open];
        if (between.Contains("=>", StringComparison.Ordinal) || between.Contains(';')) return "";
        var depth = 0;
        for (var index = open; index < code.Length; index++)
        {
            if (code[index] == '{') depth++;
            else if (code[index] == '}' && --depth == 0) return code[(open + 1)..index];
        }
        return "";
    }

    // P5-28 twice-read bytes pin: one generated package (<pkg>/cli = a copy of the CLI beside VERSION, manifest.json and
    // skills/) whose skills/ hold one code pack xx.p528.pack with a jurisdiction.json sidecar (code xx.p528, not a stub).
    // skill-list --skills <pkg>/skills and jurisdiction-resolve --code xx.p528 (package-first, so it reads the same
    // directory) each run twice in process (the verb's Run with stdout/stderr/exit captured; jurisdiction-resolve with
    // AppContext.BaseDirectory pointed at <pkg>/cli, restored in a finally, a sentinel proving the package is read) and
    // twice as real processes of the packaged CLI. Phase "success" (valid sidecar) and phase "sidecar refusal" (the
    // sidecar rewritten malformed: json_invalid, subject xx.p528.pack/jurisdiction.json): within a phase all four runs of
    // a verb print byte-identical stdout (raw, newline included), the same exit and empty stderr, equal to the pinned
    // line. Behind them, two in-process JurisdictionRegistry.Load reads of the directory: on success they carry equal
    // content but are unequal registries (P5-27 reference identity) and unequal loads, so the identical bytes cannot
    // depend on registry identity; on the refusal there is no registry and the two loads compare equal (Refusal by value,
    // no LoaderError; P5-26). The skills tree is unchanged by every run.
    static void TwiceRead(string scratch, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: P5-28 twice-read: " + name, detail);
        const string Pack = "xx.p528.pack";
        const string Code = "xx.p528";
        var package = Path.Combine(scratch, "p528-pkg");
        var packageCli = Path.Combine(package, "cli");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var skills = Path.Combine(package, "skills");
        Directory.CreateDirectory(Path.Combine(skills, Pack));
        File.WriteAllText(Path.Combine(skills, Pack, "skill.json"),
            "{\"manifest_version\":1,\"id\":\"" + Pack + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fictitious test fixture\",\"priority\":10,\"rules\":[]}\n", new UTF8Encoding(false));
        var sidecar = Path.Combine(skills, Pack, "jurisdiction.json");
        File.WriteAllText(sidecar,
            "{\"jurisdiction_version\":1,\"code\":\"" + Code + "\",\"pack\":\"" + Pack + "\",\"country_names\":[\"Fixture Country\"],\"municipality_names\":[\"Fixture Town\"],\"stub\":false,\"note\":\"fictitious test fixture\"}\n",
            new UTF8Encoding(false));
        var packageDll = Path.Combine(packageCli, "Bim.Cli.dll");
        string[] listArguments = { "skill-list", "--skills", skills };
        string[] resolveArguments = { "jurisdiction-resolve", "--code", Code };

        // A real process of the packaged CLI: raw stdout (newline kept), stderr, exit.
        (int Exit, string Out, string Err) Child(string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(packageDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        // The verb run in process: stdout raw, stderr and Environment.ExitCode captured; with baseDirectory set,
        // AppContext.BaseDirectory reads it for the run (APP_CONTEXT_BASE_DIRECTORY). Every global is restored in the finally.
        (int Exit, string Out, string Err) Inline(Action run, string? baseDirectory)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var previousBase = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var exit = -1;
            var thrown = "";
            var seen = "";
            try
            {
                if (baseDirectory is not null) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", baseDirectory);
                seen = AppContext.BaseDirectory;
                Environment.ExitCode = 0;
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                run();
            }
            catch (Exception failure) { thrown = " threw " + failure.GetType().Name + ": " + failure.Message; }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                if (baseDirectory is not null) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", previousBase);
            }
            if (baseDirectory is not null)
                Check(seen == baseDirectory, "sentinel: override visible during the run (in process AppContext.BaseDirectory is the package cli directory)", seen + " != " + baseDirectory);
            return (exit, capturedOut.ToString(), (capturedError.ToString().Trim() + thrown).Trim());
        }
        static string D((int Exit, string Out, string Err) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");

        var originalBase = AppContext.BaseDirectory;
        var packageBase = packageCli + Path.DirectorySeparatorChar;

        // The four runs of each verb in one phase, all against the one directory.
        ((int Exit, string Out, string Err)[] List, (int Exit, string Out, string Err)[] Resolve) Runs() => (
            new[]
            {
                Inline(() => Bim.Cli.SkillListCommand.Run(listArguments), null),
                Inline(() => Bim.Cli.SkillListCommand.Run(listArguments), null),
                Child(listArguments),
                Child(listArguments),
            },
            new[]
            {
                Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(resolveArguments), packageBase),
                Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(resolveArguments), packageBase),
                Child(resolveArguments),
                Child(resolveArguments),
            });
        string[] runNames = { "in process #1", "in process #2", "process #1", "process #2" };
        void Identical(string phase, string verb, (int Exit, string Out, string Err)[] runs, int exit, string line)
        {
            for (var index = 0; index < runs.Length; index++)
                Check(runs[index].Exit == exit && runs[index].Out == line + Environment.NewLine && runs[index].Err.Length == 0,
                    phase + ": " + verb + " " + runNames[index] + " prints the pinned line (exit " + exit.ToString(CultureInfo.InvariantCulture) + ", nothing on stderr)",
                    D(runs[index]) + " | expected " + JsonSerializer.Serialize(line + Environment.NewLine));
            Check(runs.All(run => run.Exit == runs[0].Exit && run.Out == runs[0].Out && run.Err == runs[0].Err),
                phase + ": " + verb + " twice in process and twice as a real process prints byte-identical output", string.Join(" || ", runs.Select(D)));
        }
        static string Failed(JurisdictionIssue issue) => JsonSerializer.Serialize(new { status = "failed", code = issue.Code, subject = issue.Subject, detail = issue.Detail });

        // ---- success phase ----
        var digest = TreeDigest(skills);
        var first = JurisdictionRegistry.Load(skills);
        var second = JurisdictionRegistry.Load(skills);
        Check(first.Registry is { } a && second.Registry is { } b && a.Codes.SequenceEqual(new[] { Code }) && b.Codes.SequenceEqual(new[] { Code })
            && a.Claims.Select(claim => (claim.Code, claim.Pack, claim.Stub)).SequenceEqual(b.Claims.Select(claim => (claim.Code, claim.Pack, claim.Stub)))
            && a.Catalog.Skills.Select(skill => skill.Id).SequenceEqual(b.Catalog.Skills.Select(skill => skill.Id)),
            "success: two in-process loads of the generated directory succeed with the same claim (" + Code + " -> " + Pack + ") and catalogue",
            (first.Refusal?.ToString() ?? "") + " / " + (second.Refusal?.ToString() ?? ""));
        var firstRegistry = first.Registry;
        var secondRegistry = second.Registry;
        // P5-27 reference identity: two in-process loads of the same directory (stand-ins; the verbs' own loads are not
        // observable) give distinct, unequal registries (red phase run 44623782837 probed the opposite expectation and failed
        // with ReferenceEquals False, Equals False, loads equal False).
        Check(firstRegistry is not null && secondRegistry is not null && !ReferenceEquals(firstRegistry, secondRegistry) && !firstRegistry.Equals(secondRegistry)
            && !firstRegistry.Equals((object)secondRegistry) && !EqualityComparer<JurisdictionRegistry>.Default.Equals(firstRegistry, secondRegistry),
            "success: the two in-process registries are unequal (P5-27 reference identity), so the identical bytes below never depend on registry identity",
            "ReferenceEquals " + ReferenceEquals(firstRegistry, secondRegistry) + ", Equals " + (firstRegistry?.Equals(secondRegistry)).ToString() + ", loads equal " + (first == second));
        Check(first != second && !first.Equals(second), "success: the two in-process loads are unequal (JurisdictionLoad follows registry identity)", first + " / " + second);
        var expectedList = Expected(skills);
        Check(expectedList.Exit == 0 && expectedList.Line.Contains("\"id\":\"" + Pack + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"stub\":false", StringComparison.Ordinal),
            "success: the in-process oracle lists " + Pack + " as a code pack, not a stub", expectedList.Line);
        // The success line pinned literally (field order as SkillListCommand writes it), and equal to the in-process oracle.
        var listLine = "{\"status\":\"succeeded\",\"skills_path\":" + JsonSerializer.Serialize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(skills)))
            + ",\"packs\":[{\"id\":\"" + Pack + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"stub\":false,"
            + "\"source_confidence\":{\"rules\":0,\"cited\":0,\"owner-confirmed\":0,\"to-verify\":0,\"lowest\":\"none\"}}]}";
        Check(expectedList.Line == listLine, "success: the in-process skill-list oracle equals the literal pinned line", expectedList.Line + " | pinned " + listLine);
        var resolveLine = JsonSerializer.Serialize(new { status = "succeeded", code = Code, pack = Pack, version = "1.2.3", stub = false });
        // Negative control: without the base-directory override, in-process jurisdiction-resolve reads the repository
        // skills (no xx.p528 claim) and refuses with jurisdiction_unknown, so the override is load-bearing.
        var unoverridden = Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(resolveArguments), null);
        string? unoverriddenCode = null;
        try { using var document = JsonDocument.Parse(unoverridden.Out); unoverriddenCode = document.RootElement.GetProperty("code").GetString(); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        Check(unoverridden.Exit == 1 && unoverridden.Out != resolveLine + Environment.NewLine && unoverriddenCode == JurisdictionCodes.JurisdictionUnknown && unoverridden.Err.Length == 0,
            "negative control: in process with the original base directory, jurisdiction-resolve --code " + Code + " is jurisdiction_unknown (the override is load-bearing)", D(unoverridden));
        var success = Runs();
        Identical("success", "skill-list", success.List, 0, listLine);
        Identical("success", "jurisdiction-resolve", success.Resolve, 0, resolveLine);
        Check(TreeDigest(skills) == digest, "success: the generated skills tree is unchanged by every run");

        // ---- sidecar refusal phase: the one sidecar rewritten malformed ----
        File.WriteAllText(sidecar, "{ \"jurisdiction_version\": 1, ", new UTF8Encoding(false));
        digest = TreeDigest(skills);
        var firstRefused = JurisdictionRegistry.Load(skills);
        var secondRefused = JurisdictionRegistry.Load(skills);
        Check(firstRefused.Registry is null && secondRefused.Registry is null && firstRefused.LoaderError is null && secondRefused.LoaderError is null
            && firstRefused.Refusal is { Code: JurisdictionCodes.JsonInvalid, Subject: Pack + "/jurisdiction.json", Detail.Length: > 0 },
            "sidecar refusal: two in-process loads of the same directory (stand-ins; the verbs' own loads are not observable) refuse with json_invalid at " + Pack + "/jurisdiction.json and a non-empty detail (no registry, no loader error)",
            (firstRefused.Refusal?.ToString() ?? "<no refusal>") + " / " + (secondRefused.Refusal?.ToString() ?? "<no refusal>"));
        Check(firstRefused == secondRefused && firstRefused.GetHashCode() == secondRefused.GetHashCode(),
            "sidecar refusal: the two refusal loads compare equal with an equal hash (no registry identity involved)", firstRefused + " / " + secondRefused);
        var refusalLine = firstRefused.Refusal is { } refusal ? Failed(refusal) : "<no refusal>";
        var expectedRefusal = Expected(skills);
        Check(expectedRefusal.Exit == 1 && expectedRefusal.Line == refusalLine, "sidecar refusal: the skill-list oracle is the registry refusal line {status,code,subject,detail}",
            expectedRefusal.Line + " | " + refusalLine);
        var refused = Runs();
        Identical("sidecar refusal", "skill-list", refused.List, 1, refusalLine);
        Identical("sidecar refusal", "jurisdiction-resolve", refused.Resolve, 1, refusalLine);
        Check(TreeDigest(skills) == digest, "sidecar refusal: the generated skills tree is unchanged by every run");

        Check(AppContext.BaseDirectory == originalBase, "AppContext.BaseDirectory is restored after the in-process runs", AppContext.BaseDirectory + " != " + originalBase);
    }

    // Section 12 (P5-30) twice-read bytes for the lock path: one generated package (<pkg>/cli = a copy of the CLI beside
    // VERSION, manifest.json and skills/) whose skills/ hold one code pack xx.p530.pack with a jurisdiction.json sidecar
    // (code xx.p530, not a stub), and a skills.lock.json pinning it (SkillLocking.Pin, SkillLockFile.Serialize) outside the
    // package. jurisdiction-resolve --code xx.p530 --lock <lock> runs twice in process (Run with stdout/stderr/exit captured
    // and AppContext.BaseDirectory pointed at <pkg>/cli through APP_CONTEXT_BASE_DIRECTORY, restored in a finally; a
    // negative control without the override refuses) and twice as real processes of the packaged CLI. Three phases:
    // "success" (the pinned literal success line, exit 0), "sidecar drift" (the valid sidecar's note edited after pinning:
    // skill_lock_drift on the pack, line pinned literally) and "sidecar json_invalid" (the sidecar rewritten malformed and
    // the pack pinned again in a second lock: json_invalid at xx.p530.pack/jurisdiction.json, the line equal to the
    // in-process LoadLocked refusal). Within a phase all four runs print byte-identical stdout (raw, newline included), the
    // same exit and empty stderr. Behind them, two in-process LoadLocked reads of the same inputs: on success equal
    // claim/catalogue content but unequal registries (P5-27 reference identity) and unequal loads; on a refusal no registry
    // and the two loads compare equal. The skills tree and the lock bytes are unchanged by every run.
    static void LockTwiceRead(string scratch, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: P5-30 lock twice-read: " + name, detail);
        const string Pack = "xx.p530.pack";
        const string Code = "xx.p530";
        var package = Path.Combine(scratch, "p530-pkg");
        var packageCli = Path.Combine(package, "cli");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var skills = Path.Combine(package, "skills");
        Directory.CreateDirectory(Path.Combine(skills, Pack));
        File.WriteAllText(Path.Combine(skills, Pack, "skill.json"),
            "{\"manifest_version\":1,\"id\":\"" + Pack + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fictitious test fixture\",\"priority\":10,\"rules\":[]}\n", new UTF8Encoding(false));
        var sidecar = Path.Combine(skills, Pack, "jurisdiction.json");
        static string Sidecar(string note) =>
            "{\"jurisdiction_version\":1,\"code\":\"" + Code + "\",\"pack\":\"" + Pack + "\",\"country_names\":[\"Fixture Country\"],\"municipality_names\":[\"Fixture Town\"],\"stub\":false,\"note\":\"" + note + "\"}\n";
        File.WriteAllText(sidecar, Sidecar("fictitious test fixture"), new UTF8Encoding(false));
        var lockPath = Path.Combine(scratch, "p530.skills.lock.json");
        File.WriteAllBytes(lockPath, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Pack })));
        var packageDll = Path.Combine(packageCli, "Bim.Cli.dll");

        (int Exit, string Out, string Err) Child(string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(packageDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        // In process: stdout raw, stderr and Environment.ExitCode captured; with baseDirectory set AppContext.BaseDirectory
        // reads it for the run. Every global is restored in the finally.
        (int Exit, string Out, string Err) Inline(string[] arguments, string? baseDirectory)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var previousBase = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var exit = -1;
            var thrown = "";
            var seen = "";
            try
            {
                if (baseDirectory is not null) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", baseDirectory);
                seen = AppContext.BaseDirectory;
                Environment.ExitCode = 0;
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                Bim.Cli.JurisdictionResolveCommand.Run(arguments);
            }
            catch (Exception failure) { thrown = " threw " + failure.GetType().Name + ": " + failure.Message; }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                if (baseDirectory is not null) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", previousBase);
            }
            if (baseDirectory is not null)
                Check(seen == baseDirectory, "sentinel: override visible during the run (in process AppContext.BaseDirectory is the package cli directory)", seen + " != " + baseDirectory);
            return (exit, capturedOut.ToString(), (capturedError.ToString().Trim() + thrown).Trim());
        }
        static string D((int Exit, string Out, string Err) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        static string Failed(JurisdictionIssue issue) => JsonSerializer.Serialize(new { status = "failed", code = issue.Code, subject = issue.Subject, detail = issue.Detail });

        var originalBase = AppContext.BaseDirectory;
        var packageBase = packageCli + Path.DirectorySeparatorChar;
        string[] runNames = { "in process #1", "in process #2", "process #1", "process #2" };
        void Phase(string phase, string lockFile, int exit, string line)
        {
            var digest = TreeDigest(skills);
            var lockBytes = File.ReadAllBytes(lockFile);
            string[] arguments = { "jurisdiction-resolve", "--code", Code, "--lock", lockFile };
            var runs = new[] { Inline(arguments, packageBase), Inline(arguments, packageBase), Child(arguments), Child(arguments) };
            for (var index = 0; index < runs.Length; index++)
                Check(runs[index].Exit == exit && runs[index].Out == line + Environment.NewLine && runs[index].Err.Length == 0,
                    phase + ": jurisdiction-resolve --lock " + runNames[index] + " prints the pinned line (exit " + exit.ToString(CultureInfo.InvariantCulture) + ", nothing on stderr)",
                    D(runs[index]) + " | expected " + JsonSerializer.Serialize(line + Environment.NewLine));
            Check(runs.All(run => run.Exit == runs[0].Exit && run.Out == runs[0].Out && run.Err == runs[0].Err),
                phase + ": jurisdiction-resolve --lock twice in process and twice as a real process prints byte-identical output", string.Join(" || ", runs.Select(D)));
            Check(TreeDigest(skills) == digest && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(lockBytes), phase + ": the generated skills tree and the lock are unchanged by every run");
        }
        void RefusedTwice(string phase, string lockFile, string code, string subject)
        {
            var firstRefused = JurisdictionRegistry.LoadLocked(skills, lockFile);
            var secondRefused = JurisdictionRegistry.LoadLocked(skills, lockFile);
            Check(firstRefused.Registry is null && secondRefused.Registry is null && firstRefused.LoaderError is null && secondRefused.LoaderError is null
                && firstRefused.Refusal is { } refusal && refusal.Code == code && refusal.Subject == subject && refusal.Detail.Length > 0,
                phase + ": two in-process LoadLocked reads of the same inputs refuse with " + code + " at " + subject + " (no registry, no loader error)",
                (firstRefused.Refusal?.ToString() ?? "<no refusal>") + " / " + (secondRefused.Refusal?.ToString() ?? "<no refusal>"));
            Check(firstRefused == secondRefused && firstRefused.GetHashCode() == secondRefused.GetHashCode(),
                phase + ": the two refusal loads compare equal with an equal hash (no registry identity involved)", firstRefused + " / " + secondRefused);
        }

        // ---- success ----
        var first = JurisdictionRegistry.LoadLocked(skills, lockPath);
        var second = JurisdictionRegistry.LoadLocked(skills, lockPath);
        Check(first.Registry is { } a && second.Registry is { } b && a.Codes.SequenceEqual(new[] { Code }) && b.Codes.SequenceEqual(new[] { Code })
            && a.Claims.Select(claim => (claim.Code, claim.Pack, claim.Stub)).SequenceEqual(b.Claims.Select(claim => (claim.Code, claim.Pack, claim.Stub)))
            && a.Catalog.Skills.Select(skill => skill.Id + "@" + skill.Version).SequenceEqual(b.Catalog.Skills.Select(skill => skill.Id + "@" + skill.Version)),
            "success: two in-process LoadLocked reads of the same skills directory and lock succeed with the same claim (" + Code + " -> " + Pack + ") and catalogue",
            (first.Refusal?.ToString() ?? "") + " / " + (second.Refusal?.ToString() ?? ""));
        var firstRegistry = first.Registry;
        var secondRegistry = second.Registry;
        Check(firstRegistry is not null && secondRegistry is not null && !ReferenceEquals(firstRegistry, secondRegistry) && !firstRegistry.Equals(secondRegistry)
            && !firstRegistry.Equals((object)secondRegistry) && !EqualityComparer<JurisdictionRegistry>.Default.Equals(firstRegistry, secondRegistry)
            && new HashSet<JurisdictionRegistry> { firstRegistry, secondRegistry }.Count == 2,
            "success: the two LoadLocked registries are unequal (P5-27 reference identity), so the identical bytes below never depend on registry identity",
            "ReferenceEquals " + ReferenceEquals(firstRegistry, secondRegistry) + ", Equals " + (firstRegistry?.Equals(secondRegistry)).ToString() + ", loads equal " + (first == second));
        Check(first != second && !first.Equals(second), "success: the two LoadLocked results are unequal (JurisdictionLoad follows registry identity)", first + " / " + second);
        const string SuccessLine = "{\"status\":\"succeeded\",\"code\":\"" + Code + "\",\"pack\":\"" + Pack + "\",\"version\":\"1.2.3\",\"stub\":false}";
        var unoverridden = Inline(new[] { "jurisdiction-resolve", "--code", Code, "--lock", lockPath }, null);
        // The repository skills hold no xx.p530.pack directory, so the lock's pinned pack is skill_lock_missing on the pack.
        Check(unoverridden.Exit == 1 && unoverridden.Err.Length == 0
            && unoverridden.Out.StartsWith("{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.SkillLockMissing + "\",\"subject\":\"" + Pack + "\",\"detail\":\"", StringComparison.Ordinal),
            "negative control: in process with the original base directory, jurisdiction-resolve --code " + Code + " --lock is skill_lock_missing on " + Pack + " (the override is load-bearing)", D(unoverridden));
        Phase("success", lockPath, 0, SuccessLine);

        // ---- sidecar drift: the valid sidecar edited after pinning ----
        File.WriteAllText(sidecar, Sidecar("edited after pinning"), new UTF8Encoding(false));
        RefusedTwice("sidecar drift", lockPath, JurisdictionCodes.SkillLockDrift, Pack);
        var drifted = JurisdictionRegistry.LoadLocked(skills, lockPath);
        // Pinned literally (red phase run 44723785006 probed a placeholder and observed this line on every run): the hashes
        // are bim-skill-pack-sha256-v1 of the fixture bytes as pinned and as edited.
        const string DriftLine = "{\"status\":\"failed\",\"code\":\"skill_lock_drift\",\"subject\":\"" + Pack + "\",\"detail\":\"pinned 1.2.3 sha256 6d09875131642f8a7e7d65830c4cb5d0d1f662ad34bfa12c9283e4b83bf95d2b, found 1.2.3 sha256 b94f79539e06cff98e37479550687cad152aea361b65baa3ae17dc245f545295; an explicit lock update is required to accept it\"}";
        Check(drifted.Refusal is { } driftRefusal && Failed(driftRefusal) == DriftLine, "sidecar drift: the in-process LoadLocked refusal equals the literal pinned line",
            (drifted.Refusal is { } shown ? Failed(shown) : "<no refusal>") + " | pinned " + DriftLine);
        Phase("sidecar drift", lockPath, 1, DriftLine);

        // ---- sidecar json_invalid: the sidecar rewritten malformed and the pack pinned again ----
        File.WriteAllText(sidecar, "{ \"jurisdiction_version\": 1, ", new UTF8Encoding(false));
        var repinned = Path.Combine(scratch, "p530.repinned.skills.lock.json");
        File.WriteAllBytes(repinned, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Pack })));
        RefusedTwice("sidecar json_invalid", repinned, JurisdictionCodes.JsonInvalid, Pack + "/jurisdiction.json");
        var malformed = JurisdictionRegistry.LoadLocked(skills, repinned);
        var malformedLine = malformed.Refusal is { } malformedRefusal ? Failed(malformedRefusal) : "<no refusal>";
        // P5-34: tightened from a StartsWith prefix to the whole line; the detail is "$: " + the documented .NET 8
        // System.Text.Json message for this truncated text (the one section 14 checks against JsonDocument.Parse).
        const string MalformedPinned = "{\"status\":\"failed\",\"code\":\"json_invalid\",\"subject\":\"" + Pack + "/jurisdiction.json\",\"detail\":\"$: Expected start of a property name or value, but instead reached end of data. LineNumber: 0 | BytePositionInLine: 29.\"}";
        Check(malformedLine == MalformedPinned,
            "sidecar json_invalid: the in-process LoadLocked refusal line is the pinned json_invalid at " + Pack + "/jurisdiction.json with the documented System.Text.Json message", malformedLine + " | pinned " + MalformedPinned);
        Phase("sidecar json_invalid", repinned, 1, malformedLine);

        Check(AppContext.BaseDirectory == originalBase, "AppContext.BaseDirectory is restored after the in-process runs", AppContext.BaseDirectory + " != " + originalBase);
    }

    // Section 14 (P5-32): see oracle 14 in the header.
    static void SelectionTwiceRead(string scratch, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: P5-32 selection twice-read: " + name, detail);
        const string Pack = "xx.p532.pack";
        const string Code = "xx.p532";
        const string StubPack = "xx.p532s.pack";
        const string StubCode = "xx.p532s";
        const string Truncated = "{ \"jurisdiction_version\": 1, ";
        var package = Path.Combine(scratch, "p532-pkg");
        var packageCli = Path.Combine(package, "cli");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var skills = Path.Combine(package, "skills");
        static string Manifest(string id) =>
            "{\"manifest_version\":1,\"id\":\"" + id + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fictitious test fixture\",\"priority\":10,\"rules\":[]}\n";
        static string Sidecar(string code, string pack, string municipality, bool stub) =>
            "{\"jurisdiction_version\":1,\"code\":\"" + code + "\",\"pack\":\"" + pack + "\",\"country_names\":[\"Fixture Country\"],\"municipality_names\":[\"" + municipality + "\"],\"stub\":" + (stub ? "true" : "false") + ",\"note\":\"fictitious test fixture\"}\n";
        foreach (var (id, code, municipality, stub) in new[] { (Pack, Code, "Fixture Town", false), (StubPack, StubCode, "Stub Village", true) })
        {
            Directory.CreateDirectory(Path.Combine(skills, id));
            File.WriteAllText(Path.Combine(skills, id, "skill.json"), Manifest(id), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(skills, id, "jurisdiction.json"), Sidecar(code, id, municipality, stub), new UTF8Encoding(false));
        }
        var lockPath = Path.Combine(scratch, "p532.skills.lock.json");
        File.WriteAllBytes(lockPath, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Pack, StubPack })));
        var packageDll = Path.Combine(packageCli, "Bim.Cli.dll");

        (int Exit, string Out, string Err) Child(string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(packageDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        // In process with AppContext.BaseDirectory read from baseDirectory for the run (null: unchanged); globals restored.
        (int Exit, string Out, string Err) Inline(string[] arguments, string? baseDirectory)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var previousBase = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var exit = -1;
            var thrown = "";
            var seen = "";
            try
            {
                if (baseDirectory is not null) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", baseDirectory);
                seen = AppContext.BaseDirectory;
                Environment.ExitCode = 0;
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                Bim.Cli.JurisdictionResolveCommand.Run(arguments);
            }
            catch (Exception failure) { thrown = " threw " + failure.GetType().Name + ": " + failure.Message; }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                if (baseDirectory is not null) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", previousBase);
            }
            if (baseDirectory is not null)
                Check(seen == baseDirectory, "sentinel: override visible during the run", seen + " != " + baseDirectory);
            return (exit, capturedOut.ToString(), (capturedError.ToString().Trim() + thrown).Trim());
        }
        static string D((int Exit, string Out, string Err) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");

        var originalBase = AppContext.BaseDirectory;
        var packageBase = packageCli + Path.DirectorySeparatorChar;
        string[] runNames = { "in process #1", "in process #2", "process #1", "process #2" };
        void Case(string label, string lockFile, string[] selection, int exit, string line)
        {
            var digest = TreeDigest(skills);
            var lockBytes = File.ReadAllBytes(lockFile);
            var arguments = new[] { "jurisdiction-resolve" }.Concat(selection).Concat(new[] { "--lock", lockFile }).ToArray();
            var runs = new[] { Inline(arguments, packageBase), Inline(arguments, packageBase), Child(arguments), Child(arguments) };
            for (var index = 0; index < runs.Length; index++)
                Check(runs[index].Exit == exit && runs[index].Out == line + Environment.NewLine && runs[index].Err.Length == 0,
                    label + ": " + string.Join(" ", selection) + " --lock " + runNames[index] + " prints the pinned line (exit " + exit.ToString(CultureInfo.InvariantCulture) + ", nothing on stderr)",
                    D(runs[index]) + " | expected " + JsonSerializer.Serialize(line + Environment.NewLine));
            Check(runs.All(run => run.Exit == runs[0].Exit && run.Out == runs[0].Out && run.Err == runs[0].Err),
                label + ": twice in process and twice as a real process print byte-identical output", string.Join(" || ", runs.Select(D)));
            Check(TreeDigest(skills) == digest && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(lockBytes), label + ": the generated skills tree and the lock are unchanged by every run");
        }

        // Two LoadLocked reads of the same inputs: unequal registries holding the same two claims.
        var first = JurisdictionRegistry.LoadLocked(skills, lockPath);
        var second = JurisdictionRegistry.LoadLocked(skills, lockPath);
        Check(first.Registry is { } a && second.Registry is { } b && !ReferenceEquals(a, b) && !a.Equals(b) && first != second
            && a.Claims.Select(claim => (claim.Code, claim.Pack, claim.Stub)).SequenceEqual(b.Claims.Select(claim => (claim.Code, claim.Pack, claim.Stub)))
            && a.Codes.Order(StringComparer.Ordinal).SequenceEqual(new[] { Code, StubCode }),
            "two in-process LoadLocked reads of the lock pinning both packs succeed with the same claims and are unequal registries (P5-27)",
            (first.Refusal?.ToString() ?? "") + " / " + (second.Refusal?.ToString() ?? "") + " / " + string.Join(",", first.Registry?.Codes ?? Array.Empty<string>()));
        var unoverridden = Inline(new[] { "jurisdiction-resolve", "--country", "Fixture Country", "--municipality", "Fixture Town", "--lock", lockPath }, null);
        Check(unoverridden.Exit == 1 && unoverridden.Err.Length == 0
            && unoverridden.Out.StartsWith("{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.SkillLockMissing + "\",\"subject\":\"xx.p532", StringComparison.Ordinal),
            "negative control: in process with the original base directory the selection under the lock is skill_lock_missing (the override is load-bearing)", D(unoverridden));

        static string Succeeded(string code, string pack, bool stub) =>
            "{\"status\":\"succeeded\",\"code\":\"" + code + "\",\"pack\":\"" + pack + "\",\"version\":\"1.2.3\",\"stub\":" + (stub ? "true" : "false") + "}";
        // Pinned from red run 44841766427 (the MCP jurisdiction_unknown placeholder reported this line; registered codes in
        // ordinal order). The json_invalid message is the .NET 8 System.Text.Json text first observed in P5-30 run 44731149194.
        const string UnknownLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_unknown\",\"subject\":\"fixture country/nowhere else\",\"detail\":\"no registered jurisdiction matches; registered: xx.p532, xx.p532s\"}";
        const string StubRefusedLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_stub_refused\",\"subject\":\"xx.p532s\",\"detail\":\"jurisdiction xx.p532s is a stub example pack (xx.p532s.pack), not real law; pass allowStub to use it\"}";
        // P5-34: aliased on purpose, not a missing pin. JurisdictionRegistry.Select refuses a stub after either route has
        // matched it, with the claim's code as subject and no mention of the route, so the by-metadata refusal is the same
        // bytes as the by-code one.
        const string StubRefusedByMetadataLine = StubRefusedLine;
        Case("selection success", lockPath, new[] { "--country", "Fixture Country", "--municipality", "Fixture Town" }, 0, Succeeded(Code, Pack, false));
        Case("selection success (accents, case and spacing folded)", lockPath, new[] { "--country", "  FIXTURE  country ", "--municipality", "f\u00EDxture town" }, 0, Succeeded(Code, Pack, false));
        Case("selection refusal", lockPath, new[] { "--country", "Fixture Country", "--municipality", "Nowhere Else" }, 1, UnknownLine);
        Case("allow-stub success", lockPath, new[] { "--code", StubCode, "--allow-stub" }, 0, Succeeded(StubCode, StubPack, true));
        Case("allow-stub success by selection flags", lockPath, new[] { "--allow-stub", "--country", "Fixture Country", "--municipality", "Stub Village" }, 0, Succeeded(StubCode, StubPack, true));
        Case("allow-stub refusal", lockPath, new[] { "--code", StubCode }, 1, StubRefusedLine);
        Case("allow-stub refusal by selection flags", lockPath, new[] { "--country", "Fixture Country", "--municipality", "Stub Village" }, 1, StubRefusedByMetadataLine);
        Case("allow-stub on a real pack", lockPath, new[] { "--code", Code, "--allow-stub" }, 0, Succeeded(Code, Pack, false));

        // json_invalid: the stub sidecar truncated and both packs pinned again; the load refuses before any selection.
        File.WriteAllText(Path.Combine(skills, StubPack, "jurisdiction.json"), Truncated, new UTF8Encoding(false));
        var repinned = Path.Combine(scratch, "p532.repinned.skills.lock.json");
        File.WriteAllBytes(repinned, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Pack, StubPack })));
        // The detail is "$: " + the System.Text.Json JsonException message for the truncated text; pinned literally (the only
        // tolerated text), and must equal what JsonDocument.Parse reports in this runtime.
        const string JsonMessage = "Expected start of a property name or value, but instead reached end of data. LineNumber: 0 | BytePositionInLine: 29.";
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        string observed;
        try { using var parsed = JsonDocument.Parse(Truncated); observed = "<parsed>"; }
        catch (JsonException error) { observed = error.Message; }
        Check(runtime.StartsWith(".NET 8.", StringComparison.Ordinal) && observed == JsonMessage,
            "json_invalid: System.Text.Json reports the documented message for the truncated sidecar on " + runtime, JsonSerializer.Serialize(observed));
        var jsonInvalidLine = "{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.JsonInvalid + "\",\"subject\":\"" + StubPack + "/jurisdiction.json\",\"detail\":" + JsonSerializer.Serialize("$: " + JsonMessage) + "}";
        var malformed = JurisdictionRegistry.LoadLocked(skills, repinned);
        Check(malformed.Refusal is { } refusal && JsonSerializer.Serialize(new { status = "failed", code = refusal.Code, subject = refusal.Subject, detail = refusal.Detail }) == jsonInvalidLine,
            "json_invalid: the in-process LoadLocked refusal equals the pinned line", (malformed.Refusal?.ToString() ?? "<no refusal>") + " | pinned " + jsonInvalidLine);
        Case("json_invalid under selection flags", repinned, new[] { "--country", "Fixture Country", "--municipality", "Fixture Town" }, 1, jsonInvalidLine);
        Case("json_invalid under allow-stub", repinned, new[] { "--code", StubCode, "--allow-stub" }, 1, jsonInvalidLine);

        Check(AppContext.BaseDirectory == originalBase, "AppContext.BaseDirectory is restored after the in-process runs", AppContext.BaseDirectory + " != " + originalBase);
    }

    // Section 16 (P5-34): see oracle 16 in the header.
    static void AmbiguityTwiceRead(string scratch, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: P5-34 ambiguity twice-read: " + name, detail);
        const string PackA = "xx.p534a.pack";
        const string CodeA = "xx.p534a";
        const string PackB = "xx.p534b.pack";
        const string CodeB = "xx.p534b";
        const string DuplicateOne = "xx.p534d.one";
        const string DuplicateTwo = "xx.p534d.two";
        const string DuplicateCode = "xx.p534d";
        var package = Path.Combine(scratch, "p534-pkg");
        var packageCli = Path.Combine(package, "cli");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var skills = Path.Combine(package, "skills");
        static string Manifest(string id) =>
            "{\"manifest_version\":1,\"id\":\"" + id + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fictitious test fixture\",\"priority\":10,\"rules\":[]}\n";
        static string Sidecar(string code, string pack, string municipality, string note) =>
            "{\"jurisdiction_version\":1,\"code\":\"" + code + "\",\"pack\":\"" + pack + "\",\"country_names\":[\"Fixture Country\"],\"municipality_names\":[\"" + municipality + "\"],\"stub\":false,\"note\":\"" + note + "\"}\n";
        var packs = new[] { (PackA, CodeA, "Fixture Town"), (PackB, CodeB, "Fixture Town"), (DuplicateOne, DuplicateCode, "Twin Town"), (DuplicateTwo, DuplicateCode, "Twin Town") };
        foreach (var (id, code, municipality) in packs)
        {
            Directory.CreateDirectory(Path.Combine(skills, id));
            File.WriteAllText(Path.Combine(skills, id, "skill.json"), Manifest(id), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(skills, id, "jurisdiction.json"), Sidecar(code, id, municipality, "fictitious test fixture"), new UTF8Encoding(false));
        }
        var lockPath = Path.Combine(scratch, "p534.skills.lock.json");
        File.WriteAllBytes(lockPath, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { PackA, PackB, DuplicateOne, DuplicateTwo })));
        var packageDll = Path.Combine(packageCli, "Bim.Cli.dll");

        (int Exit, string Out, string Err) Child(string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(packageDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        // In process with AppContext.BaseDirectory read from baseDirectory for the run (null: unchanged); globals restored.
        (int Exit, string Out, string Err) Inline(string[] arguments, string? baseDirectory)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var previousBase = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var exit = -1;
            var thrown = "";
            var seen = "";
            try
            {
                if (baseDirectory is not null) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", baseDirectory);
                seen = AppContext.BaseDirectory;
                Environment.ExitCode = 0;
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                Bim.Cli.JurisdictionResolveCommand.Run(arguments);
            }
            catch (Exception failure) { thrown = " threw " + failure.GetType().Name + ": " + failure.Message; }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                if (baseDirectory is not null) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", previousBase);
            }
            if (baseDirectory is not null)
                Check(seen == baseDirectory, "sentinel: override visible during the run", seen + " != " + baseDirectory);
            return (exit, capturedOut.ToString(), (capturedError.ToString().Trim() + thrown).Trim());
        }
        static string D((int Exit, string Out, string Err) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        static string Failed(JurisdictionIssue issue) => JsonSerializer.Serialize(new { status = "failed", code = issue.Code, subject = issue.Subject, detail = issue.Detail });

        var originalBase = AppContext.BaseDirectory;
        var packageBase = packageCli + Path.DirectorySeparatorChar;
        string[] runNames = { "in process #1", "in process #2", "process #1", "process #2" };
        void Case(string label, string[] selection, int exit, string line)
        {
            var digest = TreeDigest(skills);
            var lockBytes = File.ReadAllBytes(lockPath);
            var arguments = new[] { "jurisdiction-resolve" }.Concat(selection).Concat(new[] { "--lock", lockPath }).ToArray();
            var runs = new[] { Inline(arguments, packageBase), Inline(arguments, packageBase), Child(arguments), Child(arguments) };
            for (var index = 0; index < runs.Length; index++)
                Check(runs[index].Exit == exit && runs[index].Out == line + Environment.NewLine && runs[index].Err.Length == 0,
                    label + ": " + string.Join(" ", selection) + " --lock " + runNames[index] + " prints the pinned line (exit " + exit.ToString(CultureInfo.InvariantCulture) + ", nothing on stderr)",
                    D(runs[index]) + " | expected " + JsonSerializer.Serialize(line + Environment.NewLine));
            Check(runs.All(run => run.Exit == runs[0].Exit && run.Out == runs[0].Out && run.Err == runs[0].Err),
                label + ": twice in process and twice as a real process print byte-identical output", string.Join(" || ", runs.Select(D)));
            Check(TreeDigest(skills) == digest && File.ReadAllBytes(lockPath).AsSpan().SequenceEqual(lockBytes), label + ": the generated skills tree and the lock are unchanged by every run");
        }

        // The registry behind the lines: four claims, the overlap and the duplicate reported as issues (dropped from every
        // success envelope), and two reads of the same inputs are unequal registries (P5-27).
        var first = JurisdictionRegistry.LoadLocked(skills, lockPath);
        var second = JurisdictionRegistry.LoadLocked(skills, lockPath);
        Check(first.Registry is { } a && second.Registry is { } b && !ReferenceEquals(a, b) && !a.Equals(b) && first != second
            && a.Claims.Select(claim => claim.Code + " " + claim.Pack).SequenceEqual(new[] { CodeA + " " + PackA, CodeB + " " + PackB, DuplicateCode + " " + DuplicateOne, DuplicateCode + " " + DuplicateTwo })
            && a.Claims.Select(claim => (claim.Code, claim.Pack)).SequenceEqual(b.Claims.Select(claim => (claim.Code, claim.Pack)))
            && a.Issues.Select(issue => issue.Code + " " + issue.Subject).SequenceEqual(new[] { JurisdictionCodes.AliasOverlap + " " + CodeA + " " + CodeB, JurisdictionCodes.ClaimDuplicate + " " + DuplicateCode }),
            "two in-process LoadLocked reads succeed with the four claims, report alias_overlap and claim_duplicate, and are unequal registries (P5-27)",
            (first.Refusal?.ToString() ?? "") + " / " + string.Join(", ", first.Registry?.Claims.Select(claim => claim.Code + " " + claim.Pack) ?? Array.Empty<string>())
            + " / " + string.Join(", ", first.Registry?.Issues.Select(issue => issue.Code + " " + issue.Subject) ?? Array.Empty<string>()));
        var unoverridden = Inline(new[] { "jurisdiction-resolve", "--country", "Fixture Country", "--municipality", "Fixture Town", "--lock", lockPath }, null);
        Check(unoverridden.Exit == 1 && unoverridden.Err.Length == 0
            && unoverridden.Out.StartsWith("{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.SkillLockMissing + "\",\"subject\":\"xx.p534", StringComparison.Ordinal),
            "negative control: in process with the original base directory the selection under the lock is skill_lock_missing (the override is load-bearing)", D(unoverridden));

        // Derived from JurisdictionRegistry.SelectAny: metadata matches list "code (pack)" in claim order (code, then pack,
        // ordinal) under the normalised "country/municipality" subject; a duplicated code lists its packs.
        const string AmbiguousByMetadataLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_ambiguous\",\"subject\":\"fixture country/fixture town\",\"detail\":\"matches xx.p534a (xx.p534a.pack), xx.p534b (xx.p534b.pack)\"}";
        const string AmbiguousByCodeLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_ambiguous\",\"subject\":\"xx.p534d\",\"detail\":\"claimed by xx.p534d.one, xx.p534d.two\"}";
        const string AmbiguousDuplicateByMetadataLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_ambiguous\",\"subject\":\"fixture country/twin town\",\"detail\":\"matches xx.p534d (xx.p534d.one), xx.p534d (xx.p534d.two)\"}";
        const string OverlapCodeSuccessLine = "{\"status\":\"succeeded\",\"code\":\"xx.p534a\",\"pack\":\"xx.p534a.pack\",\"version\":\"1.2.3\",\"stub\":false}";
        const string UsageLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_resolve_usage\",\"detail\":\"usage: jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]\"}";
        const string ConflictCountryLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_conflict\",\"subject\":\"xx.p534a\",\"detail\":\"country Other Country does not match jurisdiction xx.p534a\"}";
        const string ConflictMunicipalityLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_conflict\",\"subject\":\"xx.p534a\",\"detail\":\"municipality Other Town does not match jurisdiction xx.p534a\"}";
        Case("ambiguous by metadata (alias overlap)", new[] { "--country", "Fixture Country", "--municipality", "Fixture Town" }, 1, AmbiguousByMetadataLine);
        Case("ambiguous by code (claim duplicate)", new[] { "--code", DuplicateCode }, 1, AmbiguousByCodeLine);
        Case("ambiguous by metadata (claim duplicate)", new[] { "--country", "Fixture Country", "--municipality", "Twin Town" }, 1, AmbiguousDuplicateByMetadataLine);
        Case("code route success on an overlapping pack", new[] { "--code", CodeA }, 0, OverlapCodeSuccessLine);

        // jurisdiction_conflict: the registry refuses a code with a mismatched country or municipality, but the verb's
        // parser refuses --code together with --country or --municipality as usage before any load, so the CLI never
        // prints it; both facts are pinned.
        var registry = first.Registry;
        var conflictCountry = registry?.Select(new ProjectJurisdiction(CodeA, "Other Country", null));
        var conflictMunicipality = registry?.Select(new ProjectJurisdiction(CodeA, "Fixture Country", "Other Town"));
        Check(conflictCountry?.Refusal is { } countryRefusal && Failed(countryRefusal) == ConflictCountryLine
            && conflictMunicipality?.Refusal is { } municipalityRefusal && Failed(municipalityRefusal) == ConflictMunicipalityLine,
            "jurisdiction_conflict: in process, Select with code " + CodeA + " and a mismatched country, then a matching country and a mismatched municipality, refuses with the pinned lines",
            (conflictCountry?.Refusal is { } shownCountry ? Failed(shownCountry) : "<no refusal>") + " / " + (conflictMunicipality?.Refusal is { } shownMunicipality ? Failed(shownMunicipality) : "<no refusal>"));
        Case("conflict unreachable on the CLI: --code with --country is usage", new[] { "--code", CodeA, "--country", "Other Country" }, 1, UsageLine);
        Case("conflict unreachable on the CLI: --code with --country and --municipality is usage", new[] { "--code", CodeA, "--country", "Fixture Country", "--municipality", "Other Town" }, 1, UsageLine);

        // Drifted lock: xx.p534a.pack's sidecar note edited after pinning; the locked load refuses before any selection.
        File.WriteAllText(Path.Combine(skills, PackA, "jurisdiction.json"), Sidecar(CodeA, PackA, "Fixture Town", "edited after pinning"), new UTF8Encoding(false));
        // The hashes are bim-skill-pack-sha256-v1 of the xx.p534a.pack fixture bytes as pinned and as edited (the line and the
        // usage line confirmed by the P5-34 red probe run 44911652955 on macOS).
        const string DriftLine = "{\"status\":\"failed\",\"code\":\"skill_lock_drift\",\"subject\":\"xx.p534a.pack\",\"detail\":\"pinned 1.2.3 sha256 265f1320b449f7ad1f1f6d8b938c27b8fc26703a0e2dc80f7c617c0e3b387616, found 1.2.3 sha256 b1bc3d968f3b9d73675700431bd8e5c52f980af5b4cab8ba8de4adecdeeef637; an explicit lock update is required to accept it\"}";
        var drifted = JurisdictionRegistry.LoadLocked(skills, lockPath);
        Check(drifted.Registry is null && drifted.Refusal is { } driftRefusal && Failed(driftRefusal) == DriftLine,
            "drifted lock: the in-process LoadLocked refusal equals the pinned skill_lock_drift line", (drifted.Refusal is { } shownDrift ? Failed(shownDrift) : "<no refusal>") + " | pinned " + DriftLine);
        Case("selection over a drifted lock", new[] { "--country", "Fixture Country", "--municipality", "Fixture Town" }, 1, DriftLine);
        Case("code selection over a drifted lock", new[] { "--code", DuplicateCode }, 1, DriftLine);

        Check(AppContext.BaseDirectory == originalBase, "AppContext.BaseDirectory is restored after the in-process runs", AppContext.BaseDirectory + " != " + originalBase);
    }

    // Reads of the skills directory skill-list must never make itself (P5-24): only JurisdictionRegistry.Load reads it.
    static readonly (string Label, string Pattern)[] ForbiddenReads =
    {
        ("LoadDirectory(", @"\bLoadDirectory\s*\("),
        ("LoadLocked(", @"\bLoadLocked\s*\("),
        ("SkillPackHash.", @"\bSkillPackHash\s*\."),
        ("SkillLoader", @"\bSkillLoader\b"),
        ("using static SkillLoader", @"\busing\s+static\s+(global\s*::\s*)?([\w.]+\.)?SkillLoader\b"),
        ("SkillLoader alias", @"\busing\s+\w+\s*=\s*(global\s*::\s*)?([\w.]+\.)?SkillLoader\b"),
        ("File.", @"\bFile\s*\."),
        ("Directory.", @"\bDirectory\s*\."),
        ("file-system info type", @"\b(FileInfo|DirectoryInfo|FileSystemInfo|FileStream|StreamReader|FileSystemEnumerable)\b"),
    };

    static List<string> ReadViolations(string code) =>
        ForbiddenReads.Where(rule => System.Text.RegularExpressions.Regex.IsMatch(code, rule.Pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant)).Select(rule => rule.Label).ToList();

    // C# source with // and /* */ comments removed; string literals (regular, verbatim, raw) and char literals are kept
    // whole, so "//" or "/*" inside them never starts a comment. A block comment becomes one space.
    static string StripComments(string text)
    {
        var output = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (current == '/' && next == '/') { while (index < text.Length && text[index] != '\n') index++; continue; }
            if (current == '/' && next == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 2;
                output.Append(' ');
                continue;
            }
            if (current == '"' || current == '\'')
            {
                var start = index;
                var verbatim = current == '"' && ((index > 0 && text[index - 1] == '@') || (index > 1 && text[index - 1] == '$' && text[index - 2] == '@'));
                var quotes = 0;
                while (current == '"' && index + quotes < text.Length && text[index + quotes] == '"') quotes++;
                if (!verbatim && quotes >= 3)
                {
                    var close = text.IndexOf(new string('"', quotes), index + quotes, StringComparison.Ordinal);
                    index = close < 0 ? text.Length : close + quotes;
                }
                else
                {
                    index++;
                    while (index < text.Length)
                    {
                        var at = text[index];
                        if (verbatim)
                        {
                            if (at == '"' && index + 1 < text.Length && text[index + 1] == '"') { index += 2; continue; }
                            index++;
                            if (at == '"') break;
                        }
                        else
                        {
                            if (at == '\\') { index += 2; continue; }
                            index++;
                            if (at == current || at == '\n') break;
                        }
                    }
                    index = Math.Min(index, text.Length);
                }
                output.Append(text, start, index - start);
                continue;
            }
            output.Append(current);
            index++;
        }
        return output.ToString();
    }

    // The brace-matched body of the first block after header ("" when absent).
    static string Block(string text, string header)
    {
        var at = text.IndexOf(header, StringComparison.Ordinal);
        if (at < 0) return "";
        var open = text.IndexOf('{', at + header.Length);
        if (open < 0) return "";
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}' && --depth == 0) return text[(open + 1)..index];
        }
        return "";
    }

    static IEnumerable<string> EnumerateNames(JsonElement element) => element.EnumerateObject().Select(property => property.Name);

    // The envelope skill-list must print for a skills directory, built from the in-process loader.
    static (int Exit, string Line) Expected(string skillsDirectory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skillsDirectory));
        try { SkillLoader.LoadDirectory(full); }
        catch (SkillException error) { return (1, JsonSerializer.Serialize(new { status = "failed", code = error.Code, field = error.Field, detail = error.Detail, file = error.File })); }
        var load = JurisdictionRegistry.Load(full);
        if (load.Refusal is { } refusal) return (1, JsonSerializer.Serialize(new { status = "failed", code = refusal.Code, subject = refusal.Subject, detail = refusal.Detail }));
        var registry = load.Registry!;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "succeeded");
            writer.WriteString("skills_path", full);
            writer.WriteStartArray("packs");
            foreach (var pack in registry.Catalog.Skills.OrderBy(pack => pack.Id, StringComparer.Ordinal))
            {
                var confidence = JurisdictionConfidenceSummary.Of(pack);
                writer.WriteStartObject();
                writer.WriteString("id", pack.Id);
                writer.WriteString("version", pack.Version.ToString());
                writer.WriteString("kind", SkillParser.KindName(pack.Kind));
                writer.WriteBoolean("stub", registry.ClaimFor(pack.Id)?.Stub ?? false);
                writer.WriteStartObject("source_confidence");
                writer.WriteNumber("rules", confidence.Rules);
                writer.WriteNumber("cited", confidence.Cited);
                writer.WriteNumber("owner-confirmed", confidence.OwnerConfirmed);
                writer.WriteNumber("to-verify", confidence.ToVerify);
                writer.WriteString("lowest", confidence.Lowest);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return (0, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    // Section 17 (P5-35): see oracle 17 in the header.
    static void ResolveStdoutFailure(string root, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: P5-35: " + name, detail);
        const string ResolveInternal = "{\"status\":\"failed\",\"code\":\"jurisdiction_resolve_internal_error\"}";
        const string ResolveUsage = "{\"status\":\"failed\",\"code\":\"jurisdiction_resolve_usage\",\"detail\":\"usage: jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]\"}";
        var newline = Environment.NewLine;

        // run in process with stdout = outWriter and stderr = errorWriter (fresh StringWriters when null): the captured
        // stdout and stderr ("" for a writer passed in), the exit code and any escaping exception; globals restored.
        static (int Exit, string Out, string Err, string Thrown) Inline(Action run, TextWriter? outWriter = null, TextWriter? errorWriter = null)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var thrown = "";
            int exit;
            try
            {
                Environment.ExitCode = 0;
                Console.SetOut(outWriter ?? capturedOut);
                Console.SetError(errorWriter ?? capturedError);
                try { run(); }
                catch (Exception failure) { thrown = failure.GetType().FullName + ": " + failure.Message; }
            }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
            return (exit, capturedOut.ToString(), capturedError.ToString(), thrown);
        }
        // A real CLI process (arguments include the verb); with a shell redirection (Linux/macOS) the CLI is exec'd by
        // /bin/sh with that redirection applied to it alone, e.g. ">/dev/full" (stdout rejects every write).
        (int Exit, string Out, string Err) Child(string? redirect, params string[] arguments)
        {
            var start = new ProcessStartInfo(redirect is null ? dotnet : "/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (redirect is not null) { start.ArgumentList.Add("-c"); start.ArgumentList.Add("exec \"$@\" " + redirect); start.ArgumentList.Add("sh"); start.ArgumentList.Add(dotnet); }
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + start.FileName);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
        static string D((int Exit, string Out, string Err, string Thrown) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out) + " stderr " + JsonSerializer.Serialize(r.Err) + (r.Thrown.Length > 0 ? " threw " + r.Thrown : "");
        static string P((int Exit, string Out, string Err) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out) + " stderr " + JsonSerializer.Serialize(r.Err.Length > 400 ? r.Err[..400] + "..." : r.Err);

        Check(Bim.Cli.JurisdictionResolveCommand.WorkProbe is null, "the WorkProbe seam is null in production");
        var resolved = Child(null, "jurisdiction-resolve", "--code", "ar.caba");
        var unknown = Child(null, "jurisdiction-resolve", "--code", "zz.nowhere");
        var usage = Child(null, "jurisdiction-resolve");
        Check(resolved.Exit == 0 && resolved.Out.StartsWith("{\"status\":\"succeeded\",\"code\":\"ar.caba\"", StringComparison.Ordinal) && resolved.Err.Length == 0
            && unknown.Exit == 1 && unknown.Out.Contains("\"code\":\"jurisdiction_unknown\"", StringComparison.Ordinal) && unknown.Err.Length == 0
            && usage.Exit == 1 && usage.Out == ResolveUsage + newline && usage.Err.Length == 0,
            "fixture: real processes resolve ar.caba, refuse zz.nowhere (jurisdiction_unknown) and no selector (the usage line), stderr empty",
            P(resolved) + " | " + P(unknown) + " | " + P(usage));

        // 17a. A Console.Out whose every write throws: exit 1, nothing escapes Run, one stdout write attempt; stderr carries
        // only the internal error's diagnostic (once). Both streams rejecting: one attempt on each, exit 1.
        foreach (var rejection in new Exception[] { new IOException("stdout closed P5-35"), new ObjectDisposedException("stdout P5-35"), new InvalidOperationException("stdout rejected P5-35") })
        {
            var name = rejection.GetType().Name;
            foreach (var (label, arguments, probe, expectedErr) in new (string, string[], Action?, string)[]
            {
                ("success", ["--code", "ar.caba"], null, ""),
                ("jurisdiction_unknown refusal", ["--code", "zz.nowhere"], null, ""),
                ("usage refusal", [], null, ""),
                ("internal error", ["--code", "ar.caba"], () => throw new InvalidOperationException("resolve stdout P5-35"),
                    "jurisdiction-resolve internal error: System.InvalidOperationException: resolve stdout P5-35" + newline),
            })
            {
                var writer = new RejectingWriter(rejection);
                Bim.Cli.JurisdictionResolveCommand.WorkProbe = probe;
                (int Exit, string Out, string Err, string Thrown) run;
                try { run = Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(["jurisdiction-resolve", .. arguments]), writer); }
                finally { Bim.Cli.JurisdictionResolveCommand.WorkProbe = null; }
                Check(run.Exit == 1 && run.Thrown.Length == 0 && writer.Attempts == 1 && run.Err == expectedErr,
                    "stdout throws " + name + " (jurisdiction-resolve " + label + "): exit 1, nothing escapes Run, one stdout write attempt, stderr " + (expectedErr.Length == 0 ? "untouched" : "the diagnostic once"),
                    D(run) + " stdout attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture));
            }
            foreach (var (label, arguments, probe, errAttempts) in new (string, string[], Action?, int)[]
            {
                ("internal error", ["--code", "ar.caba"], () => throw new InvalidOperationException("both P5-35"), 1),
                ("success", ["--code", "ar.caba"], null, 0),
            })
            {
                var outWriter = new RejectingWriter(rejection);
                var errWriter = new RejectingWriter(rejection);
                Bim.Cli.JurisdictionResolveCommand.WorkProbe = probe;
                (int Exit, string Out, string Err, string Thrown) both;
                try { both = Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(["jurisdiction-resolve", .. arguments]), outWriter, errWriter); }
                finally { Bim.Cli.JurisdictionResolveCommand.WorkProbe = null; }
                Check(both.Exit == 1 && both.Thrown.Length == 0 && outWriter.Attempts == 1 && errWriter.Attempts == errAttempts,
                    "stdout and stderr both throw " + name + " (jurisdiction-resolve " + label + "): exit 1, nothing escapes Run, one stdout attempt, " + errAttempts.ToString(CultureInfo.InvariantCulture) + " stderr attempt(s)",
                    D(both) + " attempts out " + outWriter.Attempts.ToString(CultureInfo.InvariantCulture) + " err " + errWriter.Attempts.ToString(CultureInfo.InvariantCulture));
            }
        }

        // 17b. With working streams the in-process bytes are the real process bytes (success and refusals unchanged), and
        // the internal error is the typed line with the diagnostic once.
        foreach (var (label, arguments, child) in new (string, string[], (int Exit, string Out, string Err))[] { ("success", ["--code", "ar.caba"], resolved), ("jurisdiction_unknown", ["--code", "zz.nowhere"], unknown), ("usage", [], usage) })
        {
            var run = Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(["jurisdiction-resolve", .. arguments]));
            Check(run.Exit == child.Exit && run.Out == child.Out && run.Err.Length == 0 && run.Thrown.Length == 0, "jurisdiction-resolve " + label + ": in-process bytes equal the real process", D(run) + " | process " + P(child));
        }
        Bim.Cli.JurisdictionResolveCommand.WorkProbe = () => throw new InvalidOperationException("typed P5-35");
        try
        {
            var run = Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(["jurisdiction-resolve", "--code", "ar.caba"]));
            Check(run.Exit == 1 && run.Out == ResolveInternal + newline && run.Err == "jurisdiction-resolve internal error: System.InvalidOperationException: typed P5-35" + newline && run.Thrown.Length == 0,
                "jurisdiction-resolve internal error with working streams: the typed line, exit 1, the diagnostic once", D(run));
        }
        finally { Bim.Cli.JurisdictionResolveCommand.WorkProbe = null; }
        Check(Bim.Cli.JurisdictionResolveCommand.WorkProbe is null, "the WorkProbe seam is null again");

        // 17c. Real processes with stdout rejecting every write (>/dev/full, Linux only: macOS has no /dev/full, Windows no
        // /bin/sh): exit 1 and stderr empty on success and refusals (the stdout failure is contained in Run). The seam
        // cannot be set in a real process, so the internal-error path is covered in process only (17a, 17b).
        if (!OperatingSystem.IsLinux())
            Console.WriteLine("SKIP skill-list: P5-35 real-process >/dev/full needs Linux; covered in process elsewhere");
        else
        {
            Check(File.Exists("/dev/full"), "precondition: /dev/full exists on Linux");
            foreach (var (label, arguments) in new (string, string[])[] { ("success", ["--code", "ar.caba"]), ("jurisdiction_unknown", ["--code", "zz.nowhere"]), ("usage", []) })
            {
                var full = Child(">/dev/full", ["jurisdiction-resolve", .. arguments]);
                Check(full.Exit == 1 && full.Out.Length == 0 && full.Err.Length == 0,
                    "real process with >/dev/full (jurisdiction-resolve " + label + "): exit 1, stderr empty (no unhandled exception)", P(full));
            }
        }

        // 17d. Static pins. JurisdictionResolveCommand.Run's body is pinned; the documented delta ResolveDeltaP535 reversed
        // gives the P5-33 file byte for byte ("\n" line endings), and section 15 reads the file with it reversed. The
        // command has no Console access of its own, prints once and sets the exit code only in Run's catch.
        static string Squeeze(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "");
        static int Matches(string code, string pattern) => System.Text.RegularExpressions.Regex.Matches(code, pattern).Count;
        var resolvePath = Path.Combine(root, "interfaces", "cli", "JurisdictionResolveCommand.cs");
        var resolveSource = File.Exists(resolvePath) ? File.ReadAllText(resolvePath).ReplaceLineEndings("\n") : "";
        var resolveCode = ApplyIntentTests.CodeOf(resolveSource);
        var runBody = Squeeze(BodyAfter(resolveCode, @"\bpublic\s+static\s+void\s+Run\s*\(\s*string\s*\[\s*\]\s*args\s*\)"));
        Check(runBody == "try{varenvelope=Resolve(args);SkillListCommand.WriteDiagnostic(envelope);(envelopewith{Diagnostic=null}).Print();}catch(Exception){Environment.ExitCode=1;}",
            "JurisdictionResolveCommand.Run: the P5-33 statements inside a try whose catch (Exception) sets exit 1", runBody);
        Check(Matches(resolveCode, @"\bConsole\b") == 0 && Matches(resolveCode, @"\.\s*Print\s*\(") == 1 && Matches(resolveCode, @"\bEnvironment\s*\.\s*ExitCode\b") == 1,
            "JurisdictionResolveCommand.cs: no Console of its own, one Print, the exit code set only in Run's catch");
        var before = ReverseDelta(resolveSource, ResolveDeltaP535);
        Check(before is not null && Sha256(before) == ResolveBeforeP535Sha256 && before.Length == ResolveBeforeP535Length,
            "JurisdictionResolveCommand.cs with the P5-35 delta reversed is the P5-33 file",
            before is null ? "a delta text does not occur exactly once" : Sha256(before) + " " + before.Length.ToString(CultureInfo.InvariantCulture));

        // WriteDiagnostic's home is pinned in SkillListCommand.cs (not moved), for this reason: every project that compiles
        // either verb or either MCP tool already compiles SkillListCommand.cs (the CLI by its default glob, the others by a
        // link), so the shared guarded write needs no new file in any project; moving it would rewrite SkillListCommand.cs,
        // whose P5-31/P5-33 bytes are pinned by the documented-delta reversals of sections 13 and 15. Over interfaces/**/*.cs
        // (comments stripped, literals blanked) WriteDiagnostic is declared once, `internal static void
        // WriteDiagnostic(IntentEnvelope envelope)` in SkillListCommand.cs, and called exactly by SkillListCommand.Run and,
        // qualified as SkillListCommand.WriteDiagnostic(envelope), by SkillListTool, JurisdictionResolveCommand.Run and
        // JurisdictionResolveTool, once each.
        var interfaces = Path.Combine(root, "interfaces");
        var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(interfaces, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Select(path => (Name: Path.GetRelativePath(root, path).Replace('\\', '/'), Code: ApplyIntentTests.CodeOf(File.ReadAllText(path)))).ToArray();
        var declarations = sources.Select(s => (s.Name, Count: Matches(s.Code, @"\bvoid\s+WriteDiagnostic\s*\("))).Where(s => s.Count > 0).Select(s => s.Name + " " + s.Count.ToString(CultureInfo.InvariantCulture)).ToArray();
        var home = sources.FirstOrDefault(s => s.Name == "interfaces/cli/SkillListCommand.cs").Code ?? "";
        Check(declarations.SequenceEqual(new[] { "interfaces/cli/SkillListCommand.cs 1" }) && Matches(home, @"\binternal\s+static\s+void\s+WriteDiagnostic\s*\(\s*IntentEnvelope\s+envelope\s*\)") == 1,
            "WriteDiagnostic is declared once, internal static void WriteDiagnostic(IntentEnvelope envelope) in interfaces/cli/SkillListCommand.cs", string.Join(", ", declarations));
        var callers = sources.Select(s => (s.Name, Calls: Matches(s.Code, @"\bWriteDiagnostic\s*\(") - Matches(s.Code, @"\bvoid\s+WriteDiagnostic\s*\("), Qualified: Matches(s.Code, @"\bSkillListCommand\s*\.\s*WriteDiagnostic\s*\(\s*envelope\s*\)\s*;")))
            .Where(s => s.Calls > 0).Select(s => s.Name + " " + s.Calls.ToString(CultureInfo.InvariantCulture) + "/" + s.Qualified.ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal).ToArray();
        Check(callers.SequenceEqual(new[] { "interfaces/cli/JurisdictionResolveCommand.cs 1/1", "interfaces/cli/SkillListCommand.cs 1/0", "interfaces/mcp/JurisdictionResolveTool.cs 1/1", "interfaces/mcp/SkillListTool.cs 1/1" }),
            "WriteDiagnostic is called once each by SkillListCommand.Run and, as SkillListCommand.WriteDiagnostic(envelope), by JurisdictionResolveCommand, JurisdictionResolveTool and SkillListTool", string.Join(", ", callers));
        foreach (var project in new[] { "interfaces/mcp/Bim.Mcp.csproj", "tests/mcp/Bim.Mcp.Tests.csproj", "tests/cli/Bim.Cli.Tests.csproj", "tests/revit-commands/Bim.RevitCommands.Tests.csproj" })
        {
            var path = Path.Combine(root, project);
            var xml = File.Exists(path) ? File.ReadAllText(path) : "";
            bool Links(string file) => System.Text.RegularExpressions.Regex.IsMatch(xml, @"<Compile\s+Include=""[^""]*\bcli/" + file.Replace(".", @"\.") + @"""");
            bool LinksTool(string file) => System.Text.RegularExpressions.Regex.IsMatch(xml, @"<Compile\s+Include=""[^""]*\bmcp/" + file.Replace(".", @"\.") + @"""") || project.StartsWith("interfaces/mcp/", StringComparison.Ordinal);
            var users = Links("JurisdictionResolveCommand.cs") || Links("SkillListCommand.cs") || LinksTool("JurisdictionResolveTool.cs") || LinksTool("SkillListTool.cs");
            Check(xml.Length > 0 && users && Links("SkillListCommand.cs"), project + " compiles a WriteDiagnostic caller and links SkillListCommand.cs, its home");
        }
    }

    // P5-35 documented delta (new text, P5-33 text), "\n" line endings.
    static readonly (string New, string Old)[] ResolveDeltaP535 =
    {
        ("    // is ignored), then the envelope is printed without it: the typed line and exit 1 still follow, nothing escapes Run.\n"
            + "    // P5-35: the print is guarded too: a Console.Out that throws while the success, refusal or internal-error line is\n"
            + "    // written drops only that line: exit 1, nothing escapes Run (SkillListCommand.Run's P5-33 pattern). The try spans\n"
            + "    // the whole body (Resolve and WriteDiagnostic never throw), so the three P5-33 statements read unchanged inside it.\n"
            + "    public static void Run(string[] args)\n"
            + "    {\n"
            + "        try\n"
            + "        {\n"
            + "            var envelope = Resolve(args);\n"
            + "            SkillListCommand.WriteDiagnostic(envelope);\n"
            + "            (envelope with { Diagnostic = null }).Print();\n"
            + "        }\n"
            + "        catch (Exception) { Environment.ExitCode = 1; }\n"
            + "    }\n",
            "    // is ignored), then the envelope is printed without it: the typed line and exit 1 still follow, nothing escapes Run.\n"
            + "    public static void Run(string[] args)\n"
            + "    {\n"
            + "        var envelope = Resolve(args);\n"
            + "        SkillListCommand.WriteDiagnostic(envelope);\n"
            + "        (envelope with { Diagnostic = null }).Print();\n"
            + "    }\n"),
    };
    const string ResolveBeforeP535Sha256 = "aefc9eeeff1cf02a3ca4bf6cf95e879d558d1d310f1206987e1b0dd47ae8c25e";
    const int ResolveBeforeP535Length = 8171;

    // Section 15 (P5-33): see oracle 15 in the header.
    static void StdoutFailure(string root, string scratch, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: P5-33: " + name, detail);
        const string SkillInternal = "{\"status\":\"failed\",\"code\":\"skill_list_internal_error\"}";
        const string ResolveInternal = "{\"status\":\"failed\",\"code\":\"jurisdiction_resolve_internal_error\"}";
        var newline = Environment.NewLine;
        var skills = Path.GetFullPath(Path.Combine(root, "skills"));
        var absent = Path.Combine(scratch, "p533-absent");

        // run in process with stdout = outWriter and stderr = errorWriter (fresh StringWriters when null): the captured
        // stdout and stderr ("" for a writer passed in), the exit code and any escaping exception; globals restored.
        static (int Exit, string Out, string Err, string Thrown) Inline(Action run, TextWriter? outWriter = null, TextWriter? errorWriter = null)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var thrown = "";
            int exit;
            try
            {
                Environment.ExitCode = 0;
                Console.SetOut(outWriter ?? capturedOut);
                Console.SetError(errorWriter ?? capturedError);
                try { run(); }
                catch (Exception failure) { thrown = failure.GetType().FullName + ": " + failure.Message; }
            }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
            return (exit, capturedOut.ToString(), capturedError.ToString(), thrown);
        }
        // A real CLI process (arguments include the verb); with a shell redirection (Linux/macOS) the CLI is exec'd by
        // /bin/sh with that redirection applied to it alone, e.g. "2>&-" (stderr closed) or ">/dev/full" (stdout rejects).
        (int Exit, string Out, string Err) Child(string? redirect, params string[] arguments)
        {
            var start = new ProcessStartInfo(redirect is null ? dotnet : "/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (redirect is not null) { start.ArgumentList.Add("-c"); start.ArgumentList.Add("exec \"$@\" " + redirect); start.ArgumentList.Add("sh"); start.ArgumentList.Add(dotnet); }
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + start.FileName);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
        static string D((int Exit, string Out, string Err, string Thrown) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out) + " stderr " + JsonSerializer.Serialize(r.Err) + (r.Thrown.Length > 0 ? " threw " + r.Thrown : "");
        static string P((int Exit, string Out, string Err) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out) + " stderr " + JsonSerializer.Serialize(r.Err.Length > 400 ? r.Err[..400] + "..." : r.Err);

        Check(Bim.Cli.SkillListCommand.LoadedProbe is null && Bim.Cli.SkillListCommand.ListingWorkProbe is null && Bim.Cli.JurisdictionResolveCommand.WorkProbe is null, "every seam is null in production");
        var listing = Child(null, "skill-list", "--skills", skills);
        var missing = Child(null, "skill-list", "--skills", absent);
        var usage = Child(null, "skill-list", "--skills");
        var resolved = Child(null, "jurisdiction-resolve", "--code", "ar.caba");
        var unknown = Child(null, "jurisdiction-resolve", "--code", "zz.nowhere");
        Check(listing.Exit == 0 && listing.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && listing.Err.Length == 0
            && missing.Exit == 1 && missing.Out.Contains("\"code\":\"root_missing\"", StringComparison.Ordinal) && missing.Err.Length == 0
            && usage.Exit == 1 && usage.Out.Contains("\"code\":\"skill_list_usage\"", StringComparison.Ordinal) && usage.Err.Length == 0
            && resolved.Exit == 0 && resolved.Out.StartsWith("{\"status\":\"succeeded\",\"code\":\"ar.caba\"", StringComparison.Ordinal) && resolved.Err.Length == 0
            && unknown.Exit == 1 && unknown.Out.Contains("\"code\":\"jurisdiction_unknown\"", StringComparison.Ordinal) && unknown.Err.Length == 0,
            "fixture: real processes (stdout and stderr open) list, refuse root_missing and usage, resolve ar.caba and refuse zz.nowhere as jurisdiction_unknown, stderr empty",
            P(listing) + " | " + P(missing) + " | " + P(usage) + " | " + P(resolved) + " | " + P(unknown));

        // 15a. skill-list with a Console.Out whose every write throws: exit 1, nothing escapes Run, one stdout write attempt;
        // stderr carries only an internal error's diagnostic (once), never anything on a success or a refusal.
        foreach (var rejection in new Exception[] { new IOException("stdout closed P5-33"), new ObjectDisposedException("stdout P5-33"), new InvalidOperationException("stdout rejected P5-33") })
        {
            var name = rejection.GetType().Name;
            foreach (var (label, arguments, arm, expectedErr) in new (string, string[], Action, string)[]
            {
                ("success listing", ["--skills", skills], () => { }, ""),
                ("root_missing refusal", ["--skills", absent], () => { }, ""),
                ("usage refusal", ["--skills"], () => { }, ""),
                ("internal error (post-load probe)", ["--skills", skills], () => Bim.Cli.SkillListCommand.LoadedProbe = _ => throw new InvalidOperationException("post-load P5-33"),
                    "skill-list internal error: System.InvalidOperationException: post-load P5-33" + newline),
                ("internal error (listing probe)", [], () => Bim.Cli.SkillListCommand.ListingWorkProbe = () => throw new IOException("listing P5-33"),
                    "skill-list internal error: System.IO.IOException: listing P5-33" + newline),
            })
            {
                var writer = new RejectingWriter(rejection);
                arm();
                (int Exit, string Out, string Err, string Thrown) run;
                try { run = Inline(() => Bim.Cli.SkillListCommand.Run(["skill-list", .. arguments]), writer); }
                finally { Bim.Cli.SkillListCommand.LoadedProbe = null; Bim.Cli.SkillListCommand.ListingWorkProbe = null; }
                Check(run.Exit == 1 && run.Thrown.Length == 0 && writer.Attempts == 1 && run.Err == expectedErr,
                    "stdout throws " + name + " (skill-list " + label + "): exit 1, nothing escapes Run, one stdout write attempt, stderr " + (expectedErr.Length == 0 ? "untouched" : "the diagnostic once"),
                    D(run) + " stdout attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture));
            }
            // Both streams reject: the internal error still ends with exit 1 and nothing escapes; each stream tried once.
            var outWriter = new RejectingWriter(rejection);
            var errWriter = new RejectingWriter(rejection);
            Bim.Cli.SkillListCommand.LoadedProbe = _ => throw new InvalidOperationException("both P5-33");
            (int Exit, string Out, string Err, string Thrown) both;
            try { both = Inline(() => Bim.Cli.SkillListCommand.Run(["skill-list", "--skills", skills]), outWriter, errWriter); }
            finally { Bim.Cli.SkillListCommand.LoadedProbe = null; }
            Check(both.Exit == 1 && both.Thrown.Length == 0 && outWriter.Attempts == 1 && errWriter.Attempts == 1,
                "stdout and stderr both throw " + name + " (skill-list internal error): exit 1, nothing escapes Run, one attempt on each stream",
                D(both) + " attempts out " + outWriter.Attempts.ToString(CultureInfo.InvariantCulture) + " err " + errWriter.Attempts.ToString(CultureInfo.InvariantCulture));
        }

        // 15b. With a working stdout the in-process bytes are the real process bytes (success and refusals unchanged), and
        // the internal error is the typed line with the diagnostic once.
        foreach (var (label, arguments, child) in new (string, string[], (int Exit, string Out, string Err))[] { ("listing", ["--skills", skills], listing), ("root_missing", ["--skills", absent], missing), ("usage", ["--skills"], usage) })
        {
            var run = Inline(() => Bim.Cli.SkillListCommand.Run(["skill-list", .. arguments]));
            Check(run.Exit == child.Exit && run.Out == child.Out && run.Err.Length == 0 && run.Thrown.Length == 0, "skill-list " + label + ": in-process bytes equal the real process", D(run) + " | process " + P(child));
        }
        Bim.Cli.SkillListCommand.LoadedProbe = _ => throw new InvalidOperationException("typed P5-33");
        try
        {
            var run = Inline(() => Bim.Cli.SkillListCommand.Run(["skill-list", "--skills", skills]));
            Check(run.Exit == 1 && run.Out == SkillInternal + newline && run.Err == "skill-list internal error: System.InvalidOperationException: typed P5-33" + newline && run.Thrown.Length == 0,
                "skill-list internal error with working streams: the typed line, exit 1, the diagnostic once", D(run));
        }
        finally { Bim.Cli.SkillListCommand.LoadedProbe = null; }

        // 15c. jurisdiction-resolve with a Console.Error whose every write throws on its internal-error path (WorkProbe):
        // the typed line, exit 1, nothing escapes Run, one stderr write attempt; success and refusal never write stderr.
        foreach (var rejection in new Exception[] { new IOException("stderr closed P5-33"), new ObjectDisposedException("stderr P5-33"), new InvalidOperationException("stderr rejected P5-33") })
        {
            var name = rejection.GetType().Name;
            var writer = new RejectingWriter(rejection);
            Bim.Cli.JurisdictionResolveCommand.WorkProbe = () => throw new InvalidOperationException("resolve P5-33");
            (int Exit, string Out, string Err, string Thrown) run;
            try { run = Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(["jurisdiction-resolve", "--code", "ar.caba"]), null, writer); }
            finally { Bim.Cli.JurisdictionResolveCommand.WorkProbe = null; }
            Check(run.Exit == 1 && run.Out == ResolveInternal + newline && run.Thrown.Length == 0 && writer.Attempts == 1,
                "stderr throws " + name + " (jurisdiction-resolve internal error): the typed line, exit 1, nothing escapes Run, one stderr write attempt",
                D(run) + " stderr attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture));
            foreach (var (label, arguments, child) in new (string, string[], (int Exit, string Out, string Err))[] { ("success", ["--code", "ar.caba"], resolved), ("jurisdiction_unknown", ["--code", "zz.nowhere"], unknown) })
            {
                var quiet = new RejectingWriter(rejection);
                var result = Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(["jurisdiction-resolve", .. arguments]), null, quiet);
                Check(result.Exit == child.Exit && result.Out == child.Out && result.Thrown.Length == 0 && quiet.Attempts == 0,
                    "stderr throws " + name + " (jurisdiction-resolve " + label + "): bytes equal the real process, stderr never written", D(result) + " | process " + P(child));
            }
        }
        Bim.Cli.JurisdictionResolveCommand.WorkProbe = () => throw new InvalidOperationException("resolve typed P5-33");
        try
        {
            var run = Inline(() => Bim.Cli.JurisdictionResolveCommand.Run(["jurisdiction-resolve", "--code", "ar.caba"]));
            Check(run.Exit == 1 && run.Out == ResolveInternal + newline && run.Err == "jurisdiction-resolve internal error: System.InvalidOperationException: resolve typed P5-33" + newline && run.Thrown.Length == 0,
                "jurisdiction-resolve internal error with working streams: the typed line, exit 1, the diagnostic once (unchanged)", D(run));
        }
        finally { Bim.Cli.JurisdictionResolveCommand.WorkProbe = null; }
        Check(Bim.Cli.SkillListCommand.LoadedProbe is null && Bim.Cli.SkillListCommand.ListingWorkProbe is null && Bim.Cli.JurisdictionResolveCommand.WorkProbe is null, "every seam is null again");

        // 15d. Real processes with stderr closed (2>&-, Linux/macOS) or rejecting every write (2>/dev/full, Linux): the same
        // typed lines and exits as with stderr open. With stdout rejecting (>/dev/full, Linux) skill-list exits 1 with
        // stderr empty on success and refusals (no unhandled exception). The seams cannot be set in a real process, so its
        // internal-error path is covered in process only (15a-15c).
        if (OperatingSystem.IsWindows())
            Console.WriteLine("SKIP skill-list: P5-33 real-process stderr/stdout redirection needs /bin/sh (Linux/macOS); covered in process on Windows");
        else
        {
            var redirections = new List<string> { "2>&-" };
            if (OperatingSystem.IsLinux())
            {
                Check(File.Exists("/dev/full"), "precondition: /dev/full exists on Linux");
                redirections.Add("2>/dev/full");
            }
            foreach (var redirect in redirections)
                foreach (var (label, arguments, open) in new (string, string[], (int Exit, string Out, string Err))[]
                {
                    ("skill-list listing", ["skill-list", "--skills", skills], listing), ("skill-list root_missing", ["skill-list", "--skills", absent], missing),
                    ("skill-list usage", ["skill-list", "--skills"], usage), ("jurisdiction-resolve success", ["jurisdiction-resolve", "--code", "ar.caba"], resolved),
                    ("jurisdiction-resolve jurisdiction_unknown", ["jurisdiction-resolve", "--code", "zz.nowhere"], unknown),
                })
                {
                    var closed = Child(redirect, arguments);
                    Check(closed.Exit == open.Exit && closed.Out == open.Out && closed.Err.Length == 0,
                        "real process with " + redirect + " (" + label + "): the same typed line and exit as with stderr open", P(closed) + " | open " + P(open));
                }
            if (OperatingSystem.IsLinux())
                foreach (var (label, arguments) in new (string, string[])[] { ("listing", ["--skills", skills]), ("root_missing", ["--skills", absent]), ("usage", ["--skills"]) })
                {
                    var full = Child(">/dev/full", ["skill-list", .. arguments]);
                    Check(full.Exit == 1 && full.Err.Length == 0,
                        "real process with >/dev/full (skill-list " + label + "): exit 1, stderr empty (the stdout failure is contained in Run, no unhandled exception)", P(full));
                }
        }
        Check(!Directory.Exists(absent), "the missing directory is not created");

        // 15e. Static pins. Run of each verb is pinned; the documented deltas reversed give the pre-P5-33 files byte for
        // byte ("\n" line endings), so section 13 reads SkillListCommand.cs with CommandDeltaP533 reversed.
        static string Squeeze(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "");
        var commandPath = Path.Combine(root, "interfaces", "cli", "SkillListCommand.cs");
        var resolvePath = Path.Combine(root, "interfaces", "cli", "JurisdictionResolveCommand.cs");
        var commandSource = File.Exists(commandPath) ? File.ReadAllText(commandPath).ReplaceLineEndings("\n") : "";
        var resolveSource = File.Exists(resolvePath) ? File.ReadAllText(resolvePath).ReplaceLineEndings("\n") : "";
        var runBody = Squeeze(BodyAfter(ApplyIntentTests.CodeOf(commandSource), @"\bpublic\s+static\s+void\s+Run\s*\(\s*string\s*\[\s*\]\s*args\s*\)"));
        Check(runBody == "IntentEnvelopeenvelope;try{envelope=Execute(args);}catch(Exceptionerror){envelope=IntentPlan.Internal(\"\",error);}WriteDiagnostic(envelope);try{(envelopewith{Diagnostic=null}).Print();}catch(Exception){Environment.ExitCode=1;}",
            "SkillListCommand.Run: the P5-31 body with the print guarded (a throwing stdout sets exit 1)", runBody);
        var resolveBody = Squeeze(BodyAfter(ApplyIntentTests.CodeOf(resolveSource), @"\bpublic\s+static\s+void\s+Run\s*\(\s*string\s*\[\s*\]\s*args\s*\)"));
        Check(resolveBody == "varenvelope=Resolve(args);SkillListCommand.WriteDiagnostic(envelope);(envelopewith{Diagnostic=null}).Print();",
            "JurisdictionResolveCommand.Run: Resolve(args), the diagnostic through the shared guarded SkillListCommand.WriteDiagnostic, the envelope printed without it", resolveBody);
        var before = ReverseDelta(commandSource, CommandDeltaP533);
        Check(before is not null && Sha256(before) == CommandBeforeP533Sha256 && before.Length == CommandBeforeP533Length,
            "SkillListCommand.cs with the P5-33 delta reversed is the pre-P5-33 (P5-31) file",
            before is null ? "a delta text does not occur exactly once" : Sha256(before) + " " + before.Length.ToString(CultureInfo.InvariantCulture));
        var resolveBefore = ReverseDelta(resolveSource, ResolveDeltaP533);
        Check(resolveBefore is not null && Sha256(resolveBefore) == ResolveBeforeP533Sha256 && resolveBefore.Length == ResolveBeforeP533Length,
            "JurisdictionResolveCommand.cs with the P5-33 delta reversed is the pre-P5-33 file",
            resolveBefore is null ? "a delta text does not occur exactly once" : Sha256(resolveBefore) + " " + resolveBefore.Length.ToString(CultureInfo.InvariantCulture));

        // RejectingWriter is one shared class: declared only in tests/cli/RejectingWriter.cs and linked into tests/mcp.
        var tests = Path.Combine(root, "tests");
        var declarers = new[] { "cli", "mcp" }.SelectMany(part => Directory.GetFiles(Path.Combine(tests, part), "*.cs", SearchOption.AllDirectories))
            .Where(path => !Path.GetRelativePath(tests, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(ApplyIntentTests.CodeOf(File.ReadAllText(path)), @"\bclass\s+RejectingWriter\b"))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Check(declarers.SequenceEqual(new[] { "tests/cli/RejectingWriter.cs" }), "RejectingWriter is declared once, in tests/cli/RejectingWriter.cs", string.Join(", ", declarers));
        var mcpProject = Path.Combine(tests, "mcp", "Bim.Mcp.Tests.csproj");
        var mcpXml = File.Exists(mcpProject) ? File.ReadAllText(mcpProject) : "";
        Check(System.Text.RegularExpressions.Regex.Matches(mcpXml, @"<Compile\s+Include=""\.\./cli/RejectingWriter\.cs""").Count == 1 && !mcpXml.Contains("<!--", StringComparison.Ordinal),
            "tests/mcp/Bim.Mcp.Tests.csproj compiles the shared tests/cli/RejectingWriter.cs (one Compile item, no XML comment)");
    }

    // P5-33 documented deltas (new text, pre-P5-33 text), "\n" line endings.
    static readonly (string New, string Old)[] CommandDeltaP533 =
    {
        ("// ignores a Console.Error that throws while the line is written: the typed line and exit 1 still follow, nothing escapes.\n"
            + "// P5-33: Run prints the line guarded too: a Console.Out that throws while the typed internal-error, refusal or success line\n"
            + "// is written drops only that line: exit 1, nothing escapes Run (the GenerateWrite.cs P7-29 pattern).\n",
            "// ignores a Console.Error that throws while the line is written: the typed line and exit 1 still follow, nothing escapes.\n"),
        ("        try { (envelope with { Diagnostic = null }).Print(); }\n"
            + "        catch (Exception) { Environment.ExitCode = 1; }\n",
            "        (envelope with { Diagnostic = null }).Print();\n"),
    };
    const string CommandBeforeP533Sha256 = "a37b1b80fc3571c5ee7926e2bd7495cb90060c61ad20c30a1257e6d327dbbbbe";
    const int CommandBeforeP533Length = 8047;
    static readonly (string New, string Old)[] ResolveDeltaP533 =
    {
        ("    // P5-33: the diagnostic goes through the shared guarded SkillListCommand.WriteDiagnostic (a Console.Error that throws\n"
            + "    // is ignored), then the envelope is printed without it: the typed line and exit 1 still follow, nothing escapes Run.\n"
            + "    public static void Run(string[] args)\n"
            + "    {\n"
            + "        var envelope = Resolve(args);\n"
            + "        SkillListCommand.WriteDiagnostic(envelope);\n"
            + "        (envelope with { Diagnostic = null }).Print();\n"
            + "    }\n",
            "    public static void Run(string[] args) => Resolve(args).Print();\n"),
    };
    const string ResolveBeforeP533Sha256 = "d9a54bee67466a0801b45c277d3fa7720c814745477fe6605f0a57ede3a3d5a9";
    const int ResolveBeforeP533Length = 7795;

    // Section 13 (P5-31): see oracle 13 in the header.
    static void PostLoad(string root, string scratch, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "skill-list: P5-31: " + name, detail);
        const string InternalLine = "{\"status\":\"failed\",\"code\":\"skill_list_internal_error\"}";
        var newline = Environment.NewLine;
        var skills = Path.GetFullPath(Path.Combine(root, "skills"));
        var absent = Path.Combine(scratch, "p531-absent");
        var badSidecar = Path.Combine(scratch, "p531-bad-sidecar");
        var nul = Path.Combine(scratch, "p531\0nul");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(badSidecar, Caba));
        File.WriteAllText(Path.Combine(badSidecar, Caba, "jurisdiction.json"), "{ \"jurisdiction_version\": 1, ", new UTF8Encoding(false));
        var usageLine = JsonSerializer.Serialize(new { status = "failed", code = UsageCode, detail = Usage }) + newline;

        // The verb in process with stderr = errorWriter (a fresh StringWriter when null): raw stdout, the captured stderr
        // ("" for a writer passed in), exit and any escaping exception; globals restored in the finally.
        static (int Exit, string Out, string Err, string Thrown) Inline(string[] arguments, TextWriter? errorWriter = null)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var thrown = "";
            int exit;
            try
            {
                Environment.ExitCode = 0;
                Console.SetOut(capturedOut);
                Console.SetError(errorWriter ?? capturedError);
                try { Bim.Cli.SkillListCommand.Run(arguments); }
                catch (Exception failure) { thrown = failure.GetType().FullName + ": " + failure.Message; }
            }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
            return (exit, capturedOut.ToString(), capturedError.ToString(), thrown);
        }
        (int Exit, string Out, string Err) Child(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            start.ArgumentList.Add("skill-list");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
        static string D((int Exit, string Out, string Err, string Thrown) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + JsonSerializer.Serialize(r.Out) + " stderr " + JsonSerializer.Serialize(r.Err) + (r.Thrown.Length > 0 ? " threw " + r.Thrown : "");
        static string Loads(List<JurisdictionLoad> loads) => string.Join(" | ", loads.Select(load =>
            "registry " + (load.Registry is not null) + " refusal " + (load.Refusal?.Code ?? "-") + " loader " + (load.LoaderError?.Code ?? "-")));

        Check(Bim.Cli.SkillListCommand.LoadedProbe is null && Bim.Cli.SkillListCommand.ListingWorkProbe is null, "both seams are null in production");
        var listingChild = Child("--skills", skills);
        var missingChild = Child("--skills", absent);
        var sidecarChild = Child("--skills", badSidecar);
        Check(listingChild.Exit == 0 && missingChild.Exit == 1 && sidecarChild.Exit == 1 && listingChild.Err.Length == 0 && missingChild.Err.Length == 0 && sidecarChild.Err.Length == 0,
            "fixture: real processes list the repository and refuse the missing directory and the malformed sidecar, stderr empty",
            listingChild.Exit.ToString(CultureInfo.InvariantCulture) + missingChild.Out + sidecarChild.Out);

        // 13a. A probe thrown after JurisdictionRegistry.Load: the typed line, exit 1, the diagnostic once; the probe holds
        // the load of the one read (so it ran after Load): a registry, the loader's root_missing, the sidecar refusal.
        var seen = new List<JurisdictionLoad>();
        var cases = new (string Label, string[] Arguments, Func<JurisdictionLoad, bool> Load)[]
        {
            ("default directory", [], load => load.Registry is not null && load.Refusal is null),
            ("explicit --skills", ["--skills", skills], load => load.Registry is not null && load.Refusal is null),
            ("missing directory", ["--skills", absent], load => load.Registry is null && load.LoaderError?.Code == SkillCodes.RootMissing),
            ("malformed sidecar", ["--skills", badSidecar], load => load.Registry is null && load.LoaderError is null && load.Refusal?.Code == JurisdictionCodes.JsonInvalid),
        };
        foreach (var failure in new Exception[] { new InvalidOperationException("boom skill-list P5-31"), new IOException("disk P5-31"), new NullReferenceException("null P5-31") })
        {
            var name = failure.GetType().Name;
            var diagnostic = "skill-list internal error: " + failure.GetType().FullName + ": " + failure.Message;
            Bim.Cli.SkillListCommand.LoadedProbe = load => { seen.Add(load); throw failure; };
            try
            {
                foreach (var (label, arguments, expected) in cases)
                {
                    seen.Clear();
                    var run = Inline(["skill-list", .. arguments]);
                    Check(run.Exit == 1 && run.Out == InternalLine + newline && run.Err == diagnostic + newline && run.Thrown.Length == 0,
                        name + " after the load, " + label + ": the typed skill_list_internal_error line, exit 1, the diagnostic once", D(run));
                    Check(seen.Count == 1 && expected(seen[0]), name + " after the load, " + label + ": the probe ran once with the load of the read", Loads(seen));
                }
                seen.Clear();
                Bim.Cli.IntentEnvelope? envelope = null;
                var escaped = "";
                try { envelope = Bim.Cli.SkillListCommand.List(absent); }
                catch (Exception error) { escaped = error.GetType().Name; }
                Check(escaped.Length == 0 && envelope is { Line: InternalLine, Exit: 1 } && envelope.Diagnostic == diagnostic && seen.Count == 1,
                    name + " after the load: List returns the typed envelope with the diagnostic and never throws", (envelope?.ToString() ?? "null") + " " + escaped);
                seen.Clear();
                var usage = Inline(["skill-list", "--skills"]);
                Check(usage.Exit == 1 && usage.Out == usageLine && usage.Err.Length == 0 && seen.Count == 0, name + ": a usage refusal never reaches the post-load probe and is unchanged", D(usage));
            }
            finally { Bim.Cli.SkillListCommand.LoadedProbe = null; }
        }

        // 13b. The listing probe throws first: the post-load probe is never reached.
        Bim.Cli.SkillListCommand.ListingWorkProbe = () => throw new InvalidOperationException("first P5-31");
        Bim.Cli.SkillListCommand.LoadedProbe = load => seen.Add(load);
        try
        {
            seen.Clear();
            var first = Inline(["skill-list", "--skills", skills]);
            Check(first.Exit == 1 && first.Out == InternalLine + newline && first.Err == "skill-list internal error: System.InvalidOperationException: first P5-31" + newline && seen.Count == 0,
                "a listing probe that throws ends the verb before the load: the post-load probe never runs", D(first) + " loads " + seen.Count.ToString(CultureInfo.InvariantCulture));
        }
        finally { Bim.Cli.SkillListCommand.ListingWorkProbe = null; Bim.Cli.SkillListCommand.LoadedProbe = null; }

        // 13c. A post-load probe that does not throw changes no byte (listing and refusals equal the real processes).
        Bim.Cli.SkillListCommand.LoadedProbe = load => seen.Add(load);
        try
        {
            foreach (var (label, arguments, child) in new (string, string[], (int Exit, string Out, string Err))[] { ("listing", ["--skills", skills], listingChild), ("root_missing", ["--skills", absent], missingChild), ("sidecar refusal", ["--skills", badSidecar], sidecarChild) })
            {
                seen.Clear();
                var quiet = Inline(["skill-list", .. arguments]);
                Check(seen.Count == 1 && quiet.Exit == child.Exit && quiet.Out == child.Out && quiet.Err.Length == 0 && quiet.Thrown.Length == 0,
                    "a non-throwing post-load probe runs once and keeps the " + label + " bytes", D(quiet) + " | process " + JsonSerializer.Serialize(child.Out));
            }
        }
        finally { Bim.Cli.SkillListCommand.LoadedProbe = null; }

        // 13d. A Console.Error that throws while the diagnostic is written: the typed line still prints, exit 1, nothing
        // escapes Run, the write is attempted once; success and refusals never touch stderr and keep their bytes.
        foreach (var rejection in new Exception[] { new IOException("stderr closed P5-31"), new ObjectDisposedException("stderr P5-31"), new InvalidOperationException("stderr rejected P5-31") })
        {
            var name = rejection.GetType().Name;
            foreach (var (label, arguments, arm) in new (string, string[], Action)[]
            {
                ("post-load probe", ["--skills", skills], () => Bim.Cli.SkillListCommand.LoadedProbe = _ => throw new InvalidOperationException("post-load P5-31")),
                ("listing probe", ["--skills", absent], () => Bim.Cli.SkillListCommand.ListingWorkProbe = () => throw new IOException("listing P5-31")),
                ("NUL skills path", ["--skills", nul], () => { }),
            })
            {
                var writer = new RejectingWriter(rejection);
                arm();
                (int Exit, string Out, string Err, string Thrown) run;
                try { run = Inline(["skill-list", .. arguments], writer); }
                finally { Bim.Cli.SkillListCommand.LoadedProbe = null; Bim.Cli.SkillListCommand.ListingWorkProbe = null; }
                Check(run.Exit == 1 && run.Out == InternalLine + newline && run.Thrown.Length == 0 && writer.Attempts == 1,
                    "stderr throws " + name + " (" + label + "): the typed line, exit 1, nothing escapes Run, one write attempt",
                    D(run) + " attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture));
            }
            foreach (var (label, arguments, expectedOut, expectedExit) in new (string, string[], string, int)[]
            {
                ("listing", ["--skills", skills], listingChild.Out, 0), ("root_missing", ["--skills", absent], missingChild.Out, 1),
                ("sidecar refusal", ["--skills", badSidecar], sidecarChild.Out, 1), ("usage", ["--skills"], usageLine, 1),
            })
            {
                var writer = new RejectingWriter(rejection);
                var run = Inline(["skill-list", .. arguments], writer);
                Check(run.Exit == expectedExit && run.Out == expectedOut && run.Thrown.Length == 0 && writer.Attempts == 0,
                    "stderr throws " + name + " (" + label + "): bytes unchanged, stderr never written", D(run) + " attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture));
            }
        }
        Check(Bim.Cli.SkillListCommand.LoadedProbe is null && Bim.Cli.SkillListCommand.ListingWorkProbe is null, "both seams are null again");
        Check(!Directory.Exists(absent), "the missing directory is not created");

        // 13e. Static pins.
        var commandPath = Path.Combine(root, "interfaces", "cli", "SkillListCommand.cs");
        var toolPath = Path.Combine(root, "interfaces", "mcp", "SkillListTool.cs");
        // P5-33: the pins below read the command with the documented P5-33 delta reversed (the P5-31 file; "" if it does not reverse).
        var commandSource = ReverseDelta(File.Exists(commandPath) ? File.ReadAllText(commandPath).ReplaceLineEndings("\n") : "", CommandDeltaP533) ?? "";
        var toolSource = File.Exists(toolPath) ? File.ReadAllText(toolPath).ReplaceLineEndings("\n") : "";
        var interfaces = Path.Combine(root, "interfaces");
        var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(interfaces, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal).Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path))).ToArray();
        var problems = PostLoadProblems(sources);
        Check(problems.Count == 0, "interfaces/**/*.cs names LoadedProbe only in its initializer-free declaration and one LoadedProbe?.Invoke(load); right after Listing's JurisdictionRegistry.Load( and before its refusal check, never assigned",
            string.Join("; ", problems));
        const string CommandPath = "interfaces/cli/SkillListCommand.cs";
        const string Decl = "internal static Action<JurisdictionLoad>? LoadedProbe { get; set; }";
        const string Good = "var load = JurisdictionRegistry.Load(skills); LoadedProbe?.Invoke(load); if (load.Refusal is { } refusal) return null; return null;";
        static string Command(string listingBody, string declaration = Decl, string extra = "") =>
            "internal static class SkillListCommand { " + declaration + " static IntentEnvelope Listing(string? skillsDirectory) { ListingWorkProbe?.Invoke(); " + listingBody + " } " + extra + " }";
        Check(PostLoadProblems([(CommandPath, Command(Good))]).Count == 0
            && PostLoadProblems([(CommandPath, Command("var load = JurisdictionRegistry . Load ( skills ) ;\n // LoadedProbe = null;\n LoadedProbe ?. Invoke ( load ) ; if (load.Refusal is null) return null; var s = \"LoadedProbe = x\"; return null;"))]).Count == 0,
            "self-test: the post-load checker accepts the real shape at any spacing, ignoring comments and literals", string.Join("; ", PostLoadProblems([(CommandPath, Command(Good))])));
        foreach (var (label, files) in new (string, (string, string)[])[]
        {
            ("an initializer", [(CommandPath, Command(Good, Decl.Replace("set; }", "set; } = null;", StringComparison.Ordinal)))]),
            ("a field declaration", [(CommandPath, Command(Good, "internal static Action<JurisdictionLoad>? LoadedProbe;"))]),
            ("a public declaration", [(CommandPath, Command(Good, "public static Action<JurisdictionLoad>? LoadedProbe { get; set; }"))]),
            ("a parameterless Action", [(CommandPath, Command("var load = JurisdictionRegistry.Load(skills); LoadedProbe?.Invoke(); if (load.Refusal is { } r) return null; return null;", "internal static Action? LoadedProbe { get; set; }"))]),
            ("the declaration missing", [(CommandPath, Command(Good, ""))]),
            ("an assignment in the command", [(CommandPath, Command(Good, Decl, "static void Set() { LoadedProbe = _ => throw null!; }"))]),
            ("an assignment in another interfaces file", [(CommandPath, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { SkillListCommand.LoadedProbe = null; } }")]),
            ("a ??= assignment", [(CommandPath, Command(Good)), ("interfaces/mcp/SkillListTool.cs", "class T { void X() { SkillListCommand.LoadedProbe ??= null; } }")]),
            ("a use in the tool", [(CommandPath, Command(Good)), ("interfaces/mcp/SkillListTool.cs", "class T { void X(JurisdictionLoad l) { SkillListCommand.LoadedProbe?.Invoke(l); } }")]),
            ("the invoke before the load", [(CommandPath, Command("LoadedProbe?.Invoke(null!); var load = JurisdictionRegistry.Load(skills); if (load.Refusal is { } r) return null; return null;"))]),
            ("the invoke after the refusal check", [(CommandPath, Command("var load = JurisdictionRegistry.Load(skills); if (load.Refusal is { } r) return null; LoadedProbe?.Invoke(load); return null;"))]),
            ("the invoke only in a comment", [(CommandPath, Command("var load = JurisdictionRegistry.Load(skills); // LoadedProbe?.Invoke(load);\n if (load.Refusal is { } r) return null; return null;"))]),
            ("a second invoke", [(CommandPath, Command("var load = JurisdictionRegistry.Load(skills); LoadedProbe?.Invoke(load); LoadedProbe?.Invoke(load); if (load.Refusal is { } r) return null; return null;"))]),
            ("the invoke outside Listing", [(CommandPath, Command("var load = JurisdictionRegistry.Load(skills); if (load.Refusal is { } r) return null; return null;", Decl, "static void Other(JurisdictionLoad load) { LoadedProbe?.Invoke(load); }"))]),
            ("the command file missing", [("interfaces/cli/Other.cs", "class O { }")]),
        })
            Check(PostLoadProblems(files).Count > 0, "self-test: the post-load checker flags " + label);

        // Run writes the diagnostic through WriteDiagnostic, then prints the envelope without it; WriteDiagnostic is the one
        // guarded Console.Error write of the command, and the MCP tool calls it instead of writing stderr itself.
        var commandCode = ApplyIntentTests.CodeOf(commandSource);
        var toolCode = ApplyIntentTests.CodeOf(toolSource);
        static string Squeeze(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "");
        var runBody = Squeeze(BodyAfter(commandCode, @"\bpublic\s+static\s+void\s+Run\s*\(\s*string\s*\[\s*\]\s*args\s*\)"));
        Check(runBody == "IntentEnvelopeenvelope;try{envelope=Execute(args);}catch(Exceptionerror){envelope=IntentPlan.Internal(\"\",error);}WriteDiagnostic(envelope);(envelopewith{Diagnostic=null}).Print();",
            "Run: the catch-all envelope, then WriteDiagnostic(envelope), then the envelope printed without its diagnostic", runBody);
        var writeBody = Squeeze(BodyAfter(commandCode, @"\binternal\s+static\s+void\s+WriteDiagnostic\s*\(\s*IntentEnvelope\s+envelope\s*\)"));
        Check(writeBody == "if(envelope.Diagnosticisnot{}detail)return;try{Console.Error.WriteLine(detail);}catch(Exception){}",
            "WriteDiagnostic: the diagnostic, if any, written once inside a try whose catch (Exception) ignores a throwing Console.Error", writeBody);
        static int Matches(string code, string pattern) => System.Text.RegularExpressions.Regex.Matches(code, pattern).Count;
        Check(Matches(commandCode, @"\bConsole\s*\.\s*Error\b") == 1 && Matches(commandCode, @"\.\s*Print\s*\(") == 1 && Matches(commandCode, @"\bWriteDiagnostic\s*\(") == 2,
            "the command writes stderr only in WriteDiagnostic, prints once and calls WriteDiagnostic only from Run");
        Check(Matches(toolCode, @"\bSkillListCommand\s*\.\s*WriteDiagnostic\s*\(\s*envelope\s*\)\s*;") == 1 && Matches(toolCode, @"\bConsole\b") == 0 && Matches(toolCode, @"\bDiagnostic\b") == 0,
            "the MCP tool writes the diagnostic only through SkillListCommand.WriteDiagnostic(envelope) (no Console of its own)", toolCode);

        // Documented deltas: each P5-31 change, reversed, gives back the pre-P5-31 file byte for byte (line endings
        // normalized), so the P5-21/P5-29 code the earlier pins read is otherwise untouched.
        var before = ReverseDelta(commandSource, CommandDeltaP531);
        Check(before is not null && Sha256(before) == CommandBeforeP531Sha256 && before.Length == CommandBeforeP531Length,
            "SkillListCommand.cs with the P5-31 delta reversed is the pre-P5-31 file",
            before is null ? "a delta text does not occur exactly once" : Sha256(before) + " " + before.Length.ToString(CultureInfo.InvariantCulture));
        var toolBefore = ReverseDelta(toolSource, ToolDeltaP531);
        Check(toolBefore is not null && Sha256(toolBefore) == ToolBeforeP531Sha256 && toolBefore.Length == ToolBeforeP531Length,
            "SkillListTool.cs with the P5-31 delta reversed is the pre-P5-31 file",
            toolBefore is null ? "a delta text does not occur exactly once" : Sha256(toolBefore) + " " + toolBefore.Length.ToString(CultureInfo.InvariantCulture));
        Check(ReverseDelta("a b a", [("a", "x")]) is null && ReverseDelta("a b", [("c", "x")]) is null && ReverseDelta("a b", [("b", "x")]) == "a x",
            "self-test: a delta text missing or occurring twice fails the reversal");
    }

    // P5-31 documented deltas (new text, pre-P5-31 text), "\n" line endings.
    static readonly (string New, string Old)[] CommandDeltaP531 =
    {
        ("// List(skillsDirectory) is the shared handler (the MCP skill_list tool, P5-22): it never prints and never throws.\n"
            + "// P5-31: the internal-error diagnostic reaches stderr only through WriteDiagnostic (Run here, the MCP tool there), which\n"
            + "// ignores a Console.Error that throws while the line is written: the typed line and exit 1 still follow, nothing escapes.\n",
            "// List(skillsDirectory) is the shared handler (the MCP skill_list tool, P5-22): it never prints and never throws.\n"),
        ("    internal static Action? ListingWorkProbe { get; set; }\n\n"
            + "    // P5-31 second test seam (null in production, never assigned under interfaces/): invoked with the load right after the\n"
            + "    // one JurisdictionRegistry.Load inside Listing, so a defect after the read reaches the same typed internal error.\n"
            + "    internal static Action<JurisdictionLoad>? LoadedProbe { get; set; }\n",
            "    internal static Action? ListingWorkProbe { get; set; }\n"),
        ("        WriteDiagnostic(envelope);\n"
            + "        (envelope with { Diagnostic = null }).Print();\n"
            + "    }\n\n"
            + "    // P5-31: the envelope's internal-error diagnostic, if any, on stderr once; a Console.Error that throws is ignored.\n"
            + "    internal static void WriteDiagnostic(IntentEnvelope envelope)\n"
            + "    {\n"
            + "        if (envelope.Diagnostic is not { } detail) return;\n"
            + "        try { Console.Error.WriteLine(detail); }\n"
            + "        catch (Exception) { }\n"
            + "    }\n",
            "        envelope.Print();\n    }\n"),
        ("        var load = JurisdictionRegistry.Load(skills);\n        LoadedProbe?.Invoke(load);\n",
            "        var load = JurisdictionRegistry.Load(skills);\n"),
    };
    const string CommandBeforeP531Sha256 = "198e2b8af928645c8460a3d80cfebf5eb3b20bd957459c9c17e2427055dfbcaf";
    const int CommandBeforeP531Length = 7050;
    static readonly (string New, string Old)[] ToolDeltaP531 =
    {
        ("        SkillListCommand.WriteDiagnostic(envelope); // P5-31: stderr written guarded by the verb (a throwing Console.Error is ignored)\n",
            "        if (envelope.Diagnostic is { } detail) Console.Error.WriteLine(detail);\n"),
    };
    const string ToolBeforeP531Sha256 = "66285d98bc85e442c9e2a590e6402120e60b7885407c492a77eef14472ee2746";
    const int ToolBeforeP531Length = 2816;

    static string Sha256(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // The text with each (new, old) pair replaced back (new -> old), or null when a new text does not occur exactly once.
    static string? ReverseDelta(string text, IReadOnlyList<(string New, string Old)> delta)
    {
        foreach (var (newText, oldText) in delta)
        {
            var at = text.IndexOf(newText, StringComparison.Ordinal);
            if (at < 0 || text.IndexOf(newText, at + 1, StringComparison.Ordinal) >= 0) return null;
            text = text[..at] + oldText + text[(at + newText.Length)..];
        }
        return text;
    }

    // P5-31: over (relative path, source) pairs under interfaces/, comments removed and literals blanked
    // (ApplyIntentTests.CodeOf): LoadedProbe (whole word) is named exactly twice, both in interfaces/cli/SkillListCommand.cs:
    // `internal static Action<JurisdictionLoad>? LoadedProbe { get; set; }` with no initializer and one
    // `LoadedProbe?.Invoke(load);` inside the brace-matched body of Listing immediately after
    // `var load = JurisdictionRegistry.Load(skills);` and immediately before `if (load.Refusal`; no assignment anywhere.
    static List<string> PostLoadProblems(IEnumerable<(string Name, string Source)> files)
    {
        const string Command = "interfaces/cli/SkillListCommand.cs";
        var problems = new List<string>();
        var total = 0;
        var sawCommand = false;
        foreach (var (name, source) in files)
        {
            var code = ApplyIntentTests.CodeOf(source);
            total += System.Text.RegularExpressions.Regex.Matches(code, @"\bLoadedProbe\b").Count;
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bLoadedProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": LoadedProbe assigned");
            if (name != Command) continue;
            sawCommand = true;
            var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+static\s+Action\s*<\s*JurisdictionLoad\s*>\s*\?\s*LoadedProbe\s*\{\s*get\s*;\s*set\s*;\s*\}(?!\s*=)").Count;
            if (declarations != 1) problems.Add(name + ": the initializer-free declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
            var invokes = System.Text.RegularExpressions.Regex.Matches(code, @"\bLoadedProbe\s*\?\s*\.\s*Invoke\s*\(").Count;
            if (invokes != 1) problems.Add(name + ": " + invokes.ToString(CultureInfo.InvariantCulture) + " LoadedProbe?.Invoke( calls");
            var listing = BodyAfter(code, @"\bstatic\s+IntentEnvelope\s+Listing\s*\(");
            if (System.Text.RegularExpressions.Regex.Matches(listing, @"\bvar\s+load\s*=\s*JurisdictionRegistry\s*\.\s*Load\s*\(\s*skills\s*\)\s*;\s*LoadedProbe\s*\?\s*\.\s*Invoke\s*\(\s*load\s*\)\s*;\s*if\s*\(\s*load\s*\.\s*Refusal\b").Count != 1)
                problems.Add(name + ": LoadedProbe?.Invoke(load); is not inside Listing right after var load = JurisdictionRegistry.Load(skills); and before if (load.Refusal");
        }
        if (!sawCommand) problems.Add(Command + " not found");
        if (total != 2) problems.Add("LoadedProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // SHA-256 over the sorted relative paths, sizes and bytes of every file under a directory.
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
