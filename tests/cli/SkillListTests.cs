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
