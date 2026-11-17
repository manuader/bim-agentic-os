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
//   7. P5-23 one load: the skills directory is read once. The source calls JurisdictionRegistry.Load( exactly once and
//      takes the catalogue from registry.Catalog; SkillLoader.LoadDirectory( appears exactly once, after the registry
//      load, only to restate a skills_invalid refusal as the loader's typed SkillException (failure path only).
//   8. P5-23 registry issues are dropped: a directory whose registry loads with claim_duplicate (two packs claiming xx.dup)
//      and pack_not_code (a qa pack claiming xx.qa) lists exactly the in-process envelope, which never carries them; the
//      duplicated and the non-code claims flag no pack as stub. (pack_missing cannot arise from a directory load: a
//      sidecar's pack must equal its own directory, which the catalogue holds.)
//   9. P5-23 culture: under LANG/LC_ALL de_DE.UTF-8 and tr_TR.UTF-8 the repository listing and a loader refusal print the
//      same bytes as under the default culture (skipped with a SKIP line when culture data is unavailable).
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
        foreach (var (locale, name) in new[] { ("de_DE.UTF-8", "de-DE"), ("tr_TR.UTF-8", "tr-TR") })
        {
            var available = false;
            try { available = !CultureInfo.GetCultureInfo(name).NumberFormat.NumberDecimalSeparator.Equals(".", StringComparison.Ordinal); }
            catch (CultureNotFoundException) { }
            if (!available) { Console.WriteLine("SKIP skill-list: " + name + " culture data unavailable (invariant globalization); culture check not run"); continue; }
            var env = new Dictionary<string, string> { ["LANG"] = locale, ["LC_ALL"] = locale };
            var cultured = CliWith(env, cliDll);
            Check(cultured.Exit == listing.Exit && cultured.Out == listing.Out, "under " + locale + " the repository listing prints the same bytes as under the default culture", D(cultured));
            var culturedIssues = CliWith(env, cliDll, "--skills", issues);
            Check(culturedIssues.Exit == issuesResult.Exit && culturedIssues.Out == issuesResult.Out, "under " + locale + " the registry-issues listing prints the same bytes", D(culturedIssues));
            var culturedRefusal = CliWith(env, cliDll, "--skills", malformed);
            Check(culturedRefusal.Exit == malformedResult.Exit && culturedRefusal.Out == malformedResult.Out, "under " + locale + " a loader refusal prints the same bytes", D(culturedRefusal));
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
        var codeText = string.Join("\n", source.Split('\n').Select(line => { var at = line.IndexOf("//", StringComparison.Ordinal); return at < 0 ? line : line[..at]; }));
        static int Count(string text, string token) { var n = 0; for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal)) n++; return n; }
        var registryAt = codeText.IndexOf("JurisdictionRegistry.Load(", StringComparison.Ordinal);
        var loaderAt = codeText.IndexOf("SkillLoader.LoadDirectory(", StringComparison.Ordinal);
        Check(Count(codeText, "JurisdictionRegistry.Load(") == 1 && Count(codeText, "SkillLoader.LoadDirectory(") == 1 && registryAt >= 0 && loaderAt > registryAt
            && codeText.Contains("JurisdictionCodes.SkillsInvalid", StringComparison.Ordinal) && codeText.Contains("registry.Catalog", StringComparison.Ordinal),
            "the skills directory is read once: one JurisdictionRegistry.Load supplies catalogue and claims; SkillLoader.LoadDirectory runs only after a skills_invalid refusal",
            "JurisdictionRegistry.Load x" + Count(codeText, "JurisdictionRegistry.Load(").ToString(CultureInfo.InvariantCulture) + " at " + registryAt.ToString(CultureInfo.InvariantCulture)
            + ", SkillLoader.LoadDirectory x" + Count(codeText, "SkillLoader.LoadDirectory(").ToString(CultureInfo.InvariantCulture) + " at " + loaderAt.ToString(CultureInfo.InvariantCulture));
        Check(!codeText.Contains(".Issues", StringComparison.Ordinal), "registry issues are not read by the command (dropped)");
        Check(source.Contains("public static IntentEnvelope List(", StringComparison.Ordinal), "the handler is a public static IntentEnvelope List(...) for reuse by the MCP tool");
        Check(!source.Contains(" internal error: ", StringComparison.Ordinal), "the command hand-writes no internal-error stderr line");
        Check(new[] { "File.Write", "File.Create", "File.Move", "File.Copy", "File.Delete", "File.Open", "FileMode.", "FileStream", "Directory.Create", "Directory.Move", "Directory.Delete" }
            .All(api => !source.Contains(api, StringComparison.Ordinal)), "the command has no file write API (read-only)");
        var program = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs"));
        Check(program.Contains("case \"skill-list\": SkillListCommand.Run(args); break;", StringComparison.Ordinal) && program.Contains("| skill-list [--skills skills-directory] |", StringComparison.Ordinal),
            "Program.cs registers skill-list and lists it in the usage text");
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
