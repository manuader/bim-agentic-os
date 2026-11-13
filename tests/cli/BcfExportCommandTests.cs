using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P10-20 `bcf-export issues.json out.bcfzip|out.bcf`: the P10-02 BCF 3.0 export (core/Bim.Core/Bcf.cs, BcfZip.cs) on the
// CLI. Oracles, all against the real Release CLI process:
//   1. The output file is byte-identical to BcfExport.WriteZip(issues, options) computed in process for the same issues;
//      for the tests/bcf fixture it hashes to the committed golden SHA-256 of tests/bcf/Program.cs. Member order, issue
//      order and an equivalent timestamp offset do not change a byte. stdout is one line
//      {"status":"succeeded","output","issues","entries","bytes","sha256"}.
//   2. Never-overwrite: an existing output file or directory is refused (bcf_export_output_exists) and left untouched.
//   3. Every refusal prints one {"status":"failed","code"[,"detail"]} envelope, exits 1 and writes no file:
//      bcf_export_usage, bcf_export_output_invalid, bcf_export_input_missing, bcf_export_input_too_large,
//      bcf_export_input_unreadable, bcf_export_input_invalid (detail the JSON path), and every library refusal with
//      exactly the in-process BcfExportException's code and detail.
internal static class BcfExportCommandTests
{
    // Same constant as tests/bcf/Program.cs GoldenSha256 (SHA-256 of WriteZip over the fixture below).
    const string GoldenSha256 = "f787db0d5858cee76066d8e537e2fa6fe4a28227d5938e2a0bb7fe3c5c4cf7e8";
    const string Usage = "usage: bcf-export issues.json output.bcfzip|output.bcf (output: a new file, never overwritten)";

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "bcf-export");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "bcf-export: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
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
        var counter = 0;
        string Input(string name, string content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        string Bytes(string name, byte[] content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllBytes(path, content);
            return path;
        }
        string Output(string name = "issues.bcfzip") => Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
        static string Envelope(string code, string? detail) =>
            detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
        void ExpectFailure(string name, string code, string? detail, string output, params string[] arguments)
        {
            var inputs = arguments.Where(File.Exists).Where(path => path != output).Distinct().ToArray();
            var before = inputs.Select(File.ReadAllBytes).ToArray();
            var result = Cli(arguments);
            Check(result.Exit == 1 && result.Out == Envelope(code, detail), name + " -> " + Envelope(code, detail), D(result));
            Check(!File.Exists(output) && !Directory.Exists(output), name + ": no output file", D(result));
            Check(inputs.Select((path, i) => File.ReadAllBytes(path).AsSpan().SequenceEqual(before[i])).All(same => same), name + ": inputs unchanged", D(result));
        }

        // ---- the tests/bcf fixture, in process and as the CLI's neutral issues JSON ----
        var stamp = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var options = new BcfExportOptions("tower-project", "Tower & Annex <A>", "bim-agentic-os@example.invalid", stamp, "AuthoringTool");
        var wallGuid = new Guid("0f3c1d2e-4b5a-6978-8a9b-0c1d2e3f4a5b");
        const string IfcCompressed = "1hZ8Hk$JfB$Ru5lYGCvzV8";
        var issues = new List<BcfIssue>
        {
            new("room-area-living", "Living room below minimum area", "Measured 7.5 m2 < required 9 m2.\nSee article 4.2.", BcfSeverity.Error,
                "es-ar/room.min_area", new[] { wallGuid.ToString(), IfcCompressed, "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b", IfcCompressed },
                BcfCamera.Perspective(new BcfVector(10, -5.5, 1.6), new BcfVector(0, 1, 0), new BcfVector(0, 0, 1), 60, 1.5)),
            new("door-width-entry", "Entry door narrower than 0.9 m", "Escaping test: <tag> & \"quotes\" 'apos' ]]> done", BcfSeverity.Warning,
                "es-ar/door.min_width", Array.Empty<string>(),
                BcfCamera.Orthogonal(new BcfVector(0, 0, 50), new BcfVector(0, 0, -1), new BcfVector(0, 1, 0), 0.02, 1.0)),
            new("brief-note", "Brief adjacency stated twice", null, BcfSeverity.Info),
        };
        static object V(double x, double y, double z) => new { x, y, z };
        object Camera(BcfCamera c) => new
        {
            kind = c.Kind == BcfCameraKind.Perspective ? "perspective" : "orthogonal",
            view_point = V(c.ViewPoint.X, c.ViewPoint.Y, c.ViewPoint.Z),
            direction = V(c.Direction.X, c.Direction.Y, c.Direction.Z),
            up_vector = V(c.UpVector.X, c.UpVector.Y, c.UpVector.Z),
            value = c.Value,
            aspect_ratio = c.AspectRatio,
        };
        Dictionary<string, object?> IssueJson(BcfIssue i)
        {
            var row = new Dictionary<string, object?>
            {
                ["id"] = i.Id, ["title"] = i.Title, ["severity"] = i.Severity.ToString().ToLowerInvariant(),
            };
            if (i.Description is not null) row["description"] = i.Description;
            if (i.RuleId is not null) row["rule_id"] = i.RuleId;
            if (i.ElementIds is not null) row["element_ids"] = i.ElementIds;
            if (i.Camera is not null) row["camera"] = Camera(i.Camera);
            return row;
        }
        Dictionary<string, object?> ProjectJson(string timestamp = "2026-11-02T10:00:00Z") => new()
        {
            ["id"] = options.ProjectId, ["name"] = options.ProjectName, ["author"] = options.Author, ["timestamp"] = timestamp, ["originating_system"] = options.OriginatingSystem,
        };
        string Document(IEnumerable<object> rows, Dictionary<string, object?>? project = null) =>
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["kind"] = "bcf-issues", ["version"] = 1, ["project"] = project ?? ProjectJson(), ["issues"] = rows.ToArray() });
        string Lower(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var expected = BcfExport.WriteZip(issues, options);
        var entryCount = BcfExport.Entries(issues, options).Count;
        Check(Lower(expected) == GoldenSha256, "fixture: in-process WriteZip still matches the tests/bcf golden SHA-256", Lower(expected));
        var issuesPath = Input("issues.json", Document(issues.Select(IssueJson)));

        // ---- 1. byte equality with the in-process export and the golden hash ----
        {
            var output = Output();
            var before = File.ReadAllBytes(issuesPath);
            var run = Cli("bcf-export", issuesPath, output);
            var written = File.Exists(output) ? File.ReadAllBytes(output) : Array.Empty<byte>();
            Check(run.Exit == 0 && run.Err.Length == 0 && written.AsSpan().SequenceEqual(expected), "writes the archive byte-identical to BcfExport.WriteZip(issues, options)", D(run));
            Check(Lower(written) == GoldenSha256, "output hashes to the tests/bcf golden SHA-256", Lower(written));
            var summary = JsonSerializer.Serialize(new { status = "succeeded", output, issues = issues.Count, entries = entryCount, bytes = (long)expected.Length, sha256 = Convert.ToHexString(SHA256.HashData(expected)) });
            Check(run.Out == summary, "prints the one-line success summary (output, issues, entries, bytes, sha256)", D(run) + " expected " + summary);
            Check(File.ReadAllBytes(issuesPath).AsSpan().SequenceEqual(before), "leaves the input file unchanged");

            // Reversed issue order, reversed members, an equivalent +02:00 timestamp, a UTF-8 BOM and a .bcf output: same bytes.
            var shuffled = issues.AsEnumerable().Reverse().Select(i => (object)IssueJson(i).Reverse().ToDictionary(p => p.Key, p => p.Value)).ToArray();
            var project = ProjectJson("2026-11-02T12:00:00+02:00").Reverse().ToDictionary(p => p.Key, p => p.Value);
            var reorderedText = JsonSerializer.Serialize(new Dictionary<string, object?> { ["issues"] = shuffled, ["project"] = project, ["version"] = 1, ["kind"] = "bcf-issues" });
            var reordered = Bytes("reordered.json", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(reorderedText)).ToArray());
            var second = Output("issues.bcf");
            var again = Cli("bcf-export", reordered, second);
            Check(again.Exit == 0 && File.Exists(second) && File.ReadAllBytes(second).AsSpan().SequenceEqual(expected),
                "issue order, member order, timestamp offset, BOM and .bcf extension do not change a byte", D(again));

            // Null optional members equal absent ones; a single issue without camera or elements matches the library.
            var nulls = Input("nulls.json", Document(new object[] { new Dictionary<string, object?> { ["id"] = "only", ["title"] = "Only issue", ["severity"] = "warning",
                ["description"] = null, ["rule_id"] = null, ["element_ids"] = null, ["camera"] = null } },
                new Dictionary<string, object?> { ["id"] = "p", ["name"] = "P", ["author"] = "a", ["timestamp"] = "2026-11-02T10:00:01Z", ["originating_system"] = null }));
            var nullsExpected = BcfExport.WriteZip(new[] { new BcfIssue("only", "Only issue", null, BcfSeverity.Warning) }, new BcfExportOptions("p", "P", "a", stamp.AddSeconds(1)));
            var third = Output();
            var nullRun = Cli("bcf-export", nulls, third);
            Check(nullRun.Exit == 0 && File.Exists(third) && File.ReadAllBytes(third).AsSpan().SequenceEqual(nullsExpected), "null optional members are absent members", D(nullRun));

            // ---- 2. never overwrite ----
            ExpectFailureExisting("existing output file", issuesPath, output);
            var directory = Output("dir.bcfzip");
            Directory.CreateDirectory(directory);
            var dirRun = Cli("bcf-export", issuesPath, directory);
            Check(dirRun.Exit == 1 && dirRun.Out == Envelope("bcf_export_output_exists", null) && Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0,
                "existing output directory -> bcf_export_output_exists, left untouched", D(dirRun));
        }

        void ExpectFailureExisting(string name, string input, string existing)
        {
            var before = File.ReadAllBytes(existing);
            var run = Cli("bcf-export", input, existing);
            Check(run.Exit == 1 && run.Out == Envelope("bcf_export_output_exists", null), name + " -> bcf_export_output_exists", D(run));
            Check(File.ReadAllBytes(existing).AsSpan().SequenceEqual(before), name + ": existing file untouched");
        }

        // ---- 3. refusals ----
        ExpectFailure("no arguments", "bcf_export_usage", Usage, "", "bcf-export");
        var usageOut = Output();
        ExpectFailure("one argument", "bcf_export_usage", Usage, usageOut, "bcf-export", issuesPath);
        ExpectFailure("three arguments", "bcf_export_usage", Usage, usageOut, "bcf-export", issuesPath, usageOut, "extra");
        ExpectFailure("flag-shaped input", "bcf_export_usage", Usage, usageOut, "bcf-export", "--issues", usageOut);
        ExpectFailure("empty output argument", "bcf_export_usage", Usage, "", "bcf-export", issuesPath, "");
        var badExtension = Output("issues.zip");
        ExpectFailure("unsupported output extension", "bcf_export_output_invalid", "output_extension_unsupported", badExtension, "bcf-export", issuesPath, badExtension);
        var noParent = Path.Combine(scratch, "missing-dir", "x.bcfzip");
        ExpectFailure("missing output directory", "bcf_export_output_invalid", "output_directory_not_found", noParent, "bcf-export", issuesPath, noParent);
        // Output checked before input: a bad output with a missing input reports the output.
        ExpectFailure("output before input", "bcf_export_output_invalid", "output_extension_unsupported", badExtension, "bcf-export", Path.Combine(scratch, "absent.json"), badExtension);

        var missingOut = Output();
        ExpectFailure("missing input", "bcf_export_input_missing", null, missingOut, "bcf-export", Path.Combine(scratch, "absent.json"), missingOut);
        var tooLarge = Bytes("too-large.json", overCap);
        var tooLargeOut = Output();
        ExpectFailure("input of 8 MiB + 1 byte", "bcf_export_input_too_large", null, tooLargeOut, "bcf-export", tooLarge, tooLargeOut);
        var inputDirectory = Path.Combine(scratch, "input-dir.json");
        Directory.CreateDirectory(inputDirectory);
        var dirOut = Output();
        ExpectFailure("input is a directory", "bcf_export_input_unreadable", "input_not_regular_file", dirOut, "bcf-export", inputDirectory, dirOut);

        // Strict input shape: detail is the JSON path of the first problem.
        void Invalid(string name, string text, string detail)
        {
            var path = Input("invalid.json", text);
            var output = Output();
            ExpectFailure("input invalid: " + name, "bcf_export_input_invalid", detail, output, "bcf-export", path, output);
        }
        var good = JsonSerializer.Serialize(IssueJson(issues[2]));
        var proj = JsonSerializer.Serialize(ProjectJson());
        string Root(string issuesArray, string? project = null) => "{\"kind\":\"bcf-issues\",\"version\":1,\"project\":" + (project ?? proj) + ",\"issues\":" + issuesArray + "}";
        Invalid("not JSON", "{\"kind\":", "json");
        Invalid("trailing comma", Root("[" + good + ",]"), "json");
        Invalid("comment", "/*c*/" + Root("[" + good + "]"), "json");
        Invalid("root is an array", "[]", "$");
        Invalid("wrong kind", Root("[]").Replace("\"bcf-issues\"", "\"bcf\"", StringComparison.Ordinal), "$.kind");
        Invalid("wrong version", Root("[]").Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal), "$.version");
        Invalid("missing project", "{\"kind\":\"bcf-issues\",\"version\":1,\"issues\":[]}", "$.project");
        Invalid("unknown root member", Root("[]")[..^1] + ",\"extra\":0}", "$.extra");
        Invalid("duplicate root member", Root("[]")[..^1] + ",\"issues\":[]}", "$.issues");
        Invalid("issues not an array", Root("{}"), "$.issues");
        Invalid("issue not an object", Root("[1]"), "$.issues[0]");
        Invalid("issue missing title", Root("[{\"id\":\"a\",\"severity\":\"info\"}]"), "$.issues[0].title");
        Invalid("issue id not a string", Root("[{\"id\":1,\"title\":\"t\",\"severity\":\"info\"}]"), "$.issues[0].id");
        Invalid("unknown issue member", Root("[" + good[..^1] + ",\"status\":\"open\"}]"), "$.issues[0].status");
        Invalid("duplicate issue member", Root("[" + good[..^1] + ",\"title\":\"again\"}]"), "$.issues[0].title");
        Invalid("unknown severity", Root("[{\"id\":\"a\",\"title\":\"t\",\"severity\":\"Error\"}]"), "$.issues[0].severity");
        Invalid("element id not a string", Root("[{\"id\":\"a\",\"title\":\"t\",\"severity\":\"info\",\"element_ids\":[\"x\",2]}]"), "$.issues[0].element_ids[1]");
        Invalid("camera kind unknown", Root("[{\"id\":\"a\",\"title\":\"t\",\"severity\":\"info\",\"camera\":{\"kind\":\"fisheye\",\"view_point\":{\"x\":0,\"y\":0,\"z\":0},\"direction\":{\"x\":0,\"y\":1,\"z\":0},\"up_vector\":{\"x\":0,\"y\":0,\"z\":1},\"value\":50,\"aspect_ratio\":1}}]"), "$.issues[0].camera.kind");
        Invalid("camera coordinate not a number", Root("[{\"id\":\"a\",\"title\":\"t\",\"severity\":\"info\",\"camera\":{\"kind\":\"perspective\",\"view_point\":{\"x\":0,\"y\":\"1\",\"z\":0},\"direction\":{\"x\":0,\"y\":1,\"z\":0},\"up_vector\":{\"x\":0,\"y\":0,\"z\":1},\"value\":50,\"aspect_ratio\":1}}]"), "$.issues[0].camera.view_point.y");
        Invalid("camera number overflows", Root("[{\"id\":\"a\",\"title\":\"t\",\"severity\":\"info\",\"camera\":{\"kind\":\"perspective\",\"view_point\":{\"x\":0,\"y\":0,\"z\":0},\"direction\":{\"x\":0,\"y\":1,\"z\":0},\"up_vector\":{\"x\":0,\"y\":0,\"z\":1},\"value\":1e999,\"aspect_ratio\":1}}]"), "$.issues[0].camera.value");
        Invalid("timestamp without offset", Root("[]", proj.Replace("2026-11-02T10:00:00Z", "2026-11-02T10:00:00", StringComparison.Ordinal)), "$.project.timestamp");
        Invalid("timestamp not ISO", Root("[]", proj.Replace("2026-11-02T10:00:00Z", "02/11/2026 10:00", StringComparison.Ordinal)), "$.project.timestamp");
        Invalid("unknown project member", Root("[]", proj[..^1] + ",\"guid\":\"x\"}"), "$.project.guid");
        Invalid("lone surrogate in a string", Root("[{\"id\":\"a\",\"title\":\"\\ud800\",\"severity\":\"info\"}]"), "$.issues[0].title");

        // Library refusals: exactly the in-process BcfExportException code and detail.
        void Library(string name, IReadOnlyList<BcfIssue> rows, Dictionary<string, object?>? project = null, BcfExportOptions? libraryOptions = null)
        {
            string? code = null, detail = null;
            try { BcfExport.WriteZip(rows, libraryOptions ?? options); }
            catch (BcfExportException error) { code = error.Code; detail = error.Detail; }
            Check(code is not null, "fixture: the library refuses " + name);
            if (code is null) return;
            var path = Input("library.json", Document(rows.Select(IssueJson), project));
            var output = Output();
            ExpectFailure("library refusal: " + name, code, detail, output, "bcf-export", path, output);
        }
        Library("duplicate issue id", new[] { issues[2], issues[2] with { Title = "Other" } });
        Library("blank title", new[] { issues[2] with { Title = "   " } });
        Library("field of view out of range", new[] { issues[0] with { Camera = issues[0].Camera! with { Value = 30 } } });
        Library("parallel camera vectors", new[] { issues[0] with { Camera = issues[0].Camera! with { UpVector = new BcfVector(0, 2, 0) } } });
        Library("element id with surrounding whitespace", new[] { issues[2] with { ElementIds = new[] { " x" } } });
        Library("project id blank", new[] { issues[2] }, new Dictionary<string, object?> { ["id"] = " ", ["name"] = "P", ["author"] = "a", ["timestamp"] = "2026-11-02T10:00:00Z" },
            new BcfExportOptions(" ", "P", "a", stamp));
        Library("timestamp before 1980", new[] { issues[2] }, new Dictionary<string, object?> { ["id"] = "p", ["name"] = "P", ["author"] = "a", ["timestamp"] = "1970-01-01T00:00:00Z" },
            new BcfExportOptions("p", "P", "a", new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Library("too many issues", Enumerable.Range(0, BcfLimits.MaxIssues + 1).Select(i => new BcfIssue("i" + i.ToString(CultureInfo.InvariantCulture), "t", null, BcfSeverity.Info)).ToArray());
    }
}
