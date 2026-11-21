using System.Text.Json.Nodes;

// P10-29: every MCP tool path check under a deleted working directory (the P10-28 environment case, there for
// skills_lock_verify). On Linux and macOS .NET 8 Path.GetFullPath of a relative path throws FileNotFoundException once the
// process working directory is deleted. Oracle, per tool and per path parameter, through McpServer tools/call: one
// parameter relative ("p10-29.json"), every other path absolute under the harness scratch directory; the response is
// isError with exactly that tool's typed refusal {"status":"failed","code":"<tool>_path_invalid","detail":"<parameter>"}
// (pinned bytes; never McpServer's generic {"status":"failed","code":<exception message>} nor an *_internal_error),
// nothing on stderr, the precondition (GetFullPath throws FileNotFoundException) asserted, and the working directory
// restored in a finally. Windows cannot delete a process's current directory: the cases print SKIP (counted).
internal static class DeletedCwdPathToolTests
{
    const string Relative = "p10-29.json";

    public static void Run(string work, Func<string, JsonObject> call, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P10-29 deleted working directory: " + name);
        var absolute = Path.GetFullPath(Path.Combine(work, "p10-29-absent.json"));
        // (tool, path parameter, detail, arguments with that parameter relative and every other path absolute)
        var cases = new List<(string Tool, string Detail, JsonObject Arguments)>();
        JsonObject With(IEnumerable<string> paths, string relative, JsonObject? extra = null)
        {
            var arguments = new JsonObject();
            foreach (var name in paths) arguments[name] = name == relative ? Relative : absolute;
            if (extra is not null) foreach (var (key, value) in extra) arguments[key] = value?.DeepClone();
            return arguments;
        }
        void Add(string tool, string code, string[] paths, (string Parameter, string Detail)[] details, Func<JsonObject>? extra = null)
        {
            foreach (var (parameter, detail) in details)
                cases.Add((tool, "{\"status\":\"failed\",\"code\":\"" + code + "\",\"detail\":\"" + detail + "\"}", With(paths, parameter, extra?.Invoke())));
        }
        Add("audit_verify", "audit_verify_path_invalid", ["log_path", "anchor_path"], [("log_path", "log"), ("anchor_path", "anchor")]);
        Add("diff_intent", "diff_intent_path_invalid", ["project_path", "intent_path"], [("project_path", "project"), ("intent_path", "intent")]);
        Add("doc_realized", "doc_realized_path_invalid", ["record", "fixture", "assignments"], [("record", "record"), ("fixture", "fixture"), ("assignments", "assignments")],
            () => new JsonObject { ["document"] = "00000000-0000-0000-0000-000000000000-00000001" });
        Add("doc_update", "doc_update_path_invalid", ["before", "after", "realized", "bindings", "dependents"],
            [("before", "before"), ("after", "after"), ("realized", "realized"), ("bindings", "bindings"), ("dependents", "dependents")]);
        Add("generate_prototypes", "generate_prototypes_path_invalid", ["request"], [("request", "request")]);
        Add("generate_typology", "generate_typology_path_invalid", ["request_path"], [("request_path", "request")]);
        Add("plan_layouts", "plan_layouts_path_invalid", ["brief"], [("brief", "brief")]);
        Check(cases.Count == 15 && cases.Select(c => c.Tool).Distinct().Count() == 7, "fixture: 15 cases over the seven tools");

        if (OperatingSystem.IsWindows())
        {
            foreach (var (tool, expected, _) in cases)
                Console.WriteLine("SKIP P10-29 deleted working directory: " + tool + " " + expected + ": Windows cannot delete a directory that is a process's current directory");
            Console.WriteLine("SKIP P10-29 deleted working directory: " + cases.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " cases skipped on Windows");
            Unsearchable(work, call, check);
            return;
        }

        var original = Environment.CurrentDirectory;
        var results = new List<(string Label, bool Deleted, bool Precondition, bool Typed, bool Restored, string Detail)>();
        foreach (var (tool, expected, arguments) in cases)
        {
            var gone = Path.Combine(work, "p10-29-cwd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(gone);
            var previousError = Console.Error;
            var capturedError = new StringWriter();
            var probe = "";
            var deleted = false;
            var thrown = "";
            JsonObject? response = null;
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 29, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() } }.ToJsonString();
            try
            {
                Directory.SetCurrentDirectory(gone);
                Directory.Delete(gone);
                deleted = !Directory.Exists(gone);
                try { probe = "GetFullPath accepted: " + Path.GetFullPath(Relative); }
                catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName + ": " + failure.Message; }
                Console.SetError(capturedError);
                response = call(request);
            }
            catch (Exception failure) { thrown = "threw " + failure.GetType().FullName + ": " + failure.Message; }
            finally
            {
                Console.SetError(previousError);
                Directory.SetCurrentDirectory(original);
            }
            var stderr = capturedError.ToString().Trim();
            string text = "", isError = "";
            try { text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? ""; isError = response?["result"]?["isError"]?.ToJsonString() ?? ""; }
            catch (Exception failure) { thrown += " response " + failure.Message; }
            var typed = text == expected && isError == "true" && stderr.Length == 0 && thrown.Length == 0 && !text.Contains("internal_error", StringComparison.Ordinal);
            results.Add((tool + " " + expected, deleted, probe.StartsWith("GetFullPath threw System.IO.FileNotFoundException", StringComparison.Ordinal), typed,
                Environment.CurrentDirectory == original,
                "text " + text + " isError " + isError + (stderr.Length > 0 ? " stderr " + stderr : "") + (thrown.Length > 0 ? " " + thrown : "") + " probe " + probe));
        }
        // Every case runs before the first check, so a failing check names all observed outcomes (one CI annotation).
        var failing = string.Join(" || ", results.Where(r => !(r.Deleted && r.Precondition && r.Typed && r.Restored)).Select(r => r.Label + " => " + r.Detail));
        var observed = failing.Length == 0 ? "" : " | failing cases: " + failing;
        foreach (var r in results)
        {
            Check(r.Deleted, r.Label + ": the working directory was deleted" + (r.Deleted ? "" : observed));
            Check(r.Precondition, r.Label + ": precondition: Path.GetFullPath of a relative path under the deleted working directory throws FileNotFoundException" + (r.Precondition ? "" : observed));
            Check(r.Typed, r.Label + ": a relative path under a deleted working directory is the typed refusal, isError, nothing on stderr" + (r.Typed ? "" : observed));
            Check(r.Restored, r.Label + ": the working directory is restored (finally)" + (r.Restored ? "" : observed));
        }
        Check(Environment.CurrentDirectory == original, "the working directory is restored after all cases");
        Unsearchable(work, call, check);
    }

    // ---- P10-35: the PathProblem copies under an unsearchable working directory ----
    // Each MCP tool path parameter (the seven P10-29 tools and skills_lock_verify) is called through McpServer tools/call
    // with that one parameter relative ("p10-35.json") and every other path an existing absolute file ("{}") or directory,
    // while the working directory exists but is not searchable: "cwd" mode chmods the working directory itself to 000,
    // "parent" mode its parent. On Linux Path.GetFullPath accepts the relative path (PathProblem passes it) and the tool's
    // own read refuses it, so the response is not <tool>_path_invalid there (that needs GetFullPath to throw: macOS); it must
    // be isError with a typed snake_case code, never McpServer's generic {"status":"failed","code":<exception message>} nor
    // an *_internal_error, nothing on stderr, and is pinned as measured (Pins35).
    // Linux only (macOS does not run tests/mcp; Windows has no Unix permission bits): counted SKIP lines elsewhere, and
    // when a mode-000 directory stays listable (root). Modes and the working directory are restored in a finally.
    const string Relative35 = "p10-35.json";

    // The text per tool and relative parameter, as measured (run 44892086261, Linux, both modes alike): PathProblem passes the
    // path and the tool's bounded read refuses it, input_access_denied (detail = the parameter for doc_realized/doc_update).
    // skills_lock_verify reads the lock first: with a relative skills_path the absolute "{}" lock is refused (member_missing);
    // with a relative lock_path Core's lock reader reports the unreadable lock as lock_missing (not an access refusal).
    const string Denied = """{"status":"failed","code":"input_access_denied"}""";
    static string DeniedDetail(string detail) => "{\"status\":\"failed\",\"code\":\"input_access_denied\",\"detail\":\"" + detail + "\"}";
    static readonly Dictionary<string, string> Pins35 = new(StringComparer.Ordinal)
    {
        ["audit_verify log_path"] = Denied,
        ["audit_verify anchor_path"] = Denied,
        ["diff_intent project_path"] = Denied,
        ["diff_intent intent_path"] = Denied,
        ["doc_realized record"] = DeniedDetail("record"),
        ["doc_realized fixture"] = DeniedDetail("fixture"),
        ["doc_realized assignments"] = DeniedDetail("assignments"),
        ["doc_update before"] = DeniedDetail("before"),
        ["doc_update after"] = DeniedDetail("after"),
        ["doc_update realized"] = DeniedDetail("realized"),
        ["doc_update bindings"] = DeniedDetail("bindings"),
        ["doc_update dependents"] = DeniedDetail("dependents"),
        ["generate_prototypes request"] = Denied,
        ["generate_typology request_path"] = Denied,
        ["plan_layouts brief"] = Denied,
        ["skills_lock_verify skills_path"] = """{"status":"failed","code":"member_missing","subject":"$.lock_version","detail":"required member","mode":"verify","verified":false,"packs":[],"issues":[{"code":"member_missing","subject":"$.lock_version","detail":"required member"}]}""",
        ["skills_lock_verify lock_path"] = """{"status":"failed","code":"lock_missing","subject":"p10-35.json","detail":"lock file not found","mode":"verify","verified":false,"packs":[],"issues":[{"code":"lock_missing","subject":"p10-35.json","detail":"lock file not found"}]}""",
    };

    static void Chmod(string path, UnixFileMode mode) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode); }

    static void Unsearchable(string work, Func<string, JsonObject> call, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P10-35 unsearchable working directory: " + name);
        var dummy = Path.GetFullPath(Path.Combine(work, "p10-35-dummy.json"));
        File.WriteAllText(dummy, "{}\n");
        var skills = Path.GetFullPath(Path.Combine(work, "p10-35-skills"));
        Directory.CreateDirectory(skills);
        var cases = new List<(string Tool, string Parameter, JsonObject Arguments)>();
        void Add(string tool, string[] paths, JsonObject? extra = null)
        {
            foreach (var parameter in paths)
            {
                var arguments = new JsonObject();
                foreach (var name in paths) arguments[name] = name == parameter ? Relative35 : name == "skills_path" ? skills : dummy;
                if (extra is not null) foreach (var (key, value) in extra) arguments[key] = value?.DeepClone();
                cases.Add((tool, parameter, arguments));
            }
        }
        Add("audit_verify", ["log_path", "anchor_path"]);
        Add("diff_intent", ["project_path", "intent_path"]);
        Add("doc_realized", ["record", "fixture", "assignments"], new JsonObject { ["document"] = "00000000-0000-0000-0000-000000000000-00000001" });
        Add("doc_update", ["before", "after", "realized", "bindings", "dependents"]);
        Add("generate_prototypes", ["request"]);
        Add("generate_typology", ["request_path"]);
        Add("plan_layouts", ["brief"]);
        Add("skills_lock_verify", ["skills_path", "lock_path"]);
        Check(cases.Count == 17 && cases.Select(c => c.Tool).Distinct().Count() == 8, "fixture: 17 cases over eight tools");

        foreach (var mode in new[] { "cwd", "parent" })
        {
            if (!OperatingSystem.IsLinux())
            {
                foreach (var (tool, parameter, _) in cases)
                    Console.WriteLine("SKIP P10-35 unsearchable working directory: " + mode + " " + tool + " " + parameter + ": Linux only (macOS does not run tests/mcp; Windows has no Unix permission bits)");
                continue;
            }
            var parent = Path.Combine(work, "p10-35-locked-" + mode + "-" + Guid.NewGuid().ToString("N"));
            var cwd = Path.Combine(parent, "cwd");
            Directory.CreateDirectory(cwd);
            var locked = mode == "cwd" ? cwd : parent;
            const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            bool denied;
            Chmod(locked, UnixFileMode.None);
            try { Directory.GetFileSystemEntries(locked); denied = false; }
            catch (UnauthorizedAccessException) { denied = true; }
            finally { Chmod(locked, Owner); }
            if (!denied)
            {
                foreach (var (tool, parameter, _) in cases)
                    Console.WriteLine("SKIP P10-35 unsearchable working directory: " + mode + " " + tool + " " + parameter + ": a mode-000 directory is still listable (privileged runner)");
                continue;
            }

            var original = Environment.CurrentDirectory;
            var measured = new List<(string Label, string Expected, bool Ok, string Detail)>();
            foreach (var (tool, parameter, arguments) in cases)
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 35, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() } }.ToJsonString();
                var previousError = Console.Error;
                var capturedError = new StringWriter();
                var probe = "";
                var thrown = "";
                JsonObject? response = null;
                try
                {
                    Directory.SetCurrentDirectory(cwd);
                    Chmod(locked, UnixFileMode.None);
                    try { Path.GetFullPath(Relative35); probe = "GetFullPath accepted"; }
                    catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName; }
                    Console.SetError(capturedError);
                    response = call(request);
                }
                catch (Exception failure) { thrown = "threw " + failure.GetType().FullName + ": " + failure.Message; }
                finally
                {
                    Console.SetError(previousError);
                    Chmod(locked, Owner);
                    Directory.SetCurrentDirectory(original);
                }
                var stderr = capturedError.ToString().Trim();
                string text = "", isError = "";
                try { text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? ""; isError = response?["result"]?["isError"]?.ToJsonString() ?? ""; }
                catch (Exception failure) { thrown += " response " + failure.Message; }
                text = text.Replace(work, "<work>", StringComparison.Ordinal);
                var expected = Pins35[tool + " " + parameter];
                string? code = null;
                try { code = JsonNode.Parse(text)?["code"]?.GetValue<string>(); }
                catch (Exception failure) when (failure is System.Text.Json.JsonException or InvalidOperationException) { }
                var ok = probe == "GetFullPath accepted" && isError == "true" && text == expected && stderr.Length == 0 && thrown.Length == 0
                    && code is { Length: > 0 } && code.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') && !code.Contains("internal_error", StringComparison.Ordinal)
                    && Environment.CurrentDirectory == original;
                measured.Add((tool + " " + parameter, expected, ok, probe + "; isError " + isError + " " + text + (stderr.Length > 0 ? " stderr " + stderr : "") + (thrown.Length > 0 ? " " + thrown : "")));
            }
            // Every case runs before the first check, so a failing check names all observed outcomes (one CI annotation).
            var failing = string.Join(" || ", measured.Where(m => !m.Ok).Select(m => m.Label + " => " + m.Detail));
            var observed = failing.Length == 0 ? "" : " | failing cases: " + failing;
            foreach (var m in measured)
                Check(m.Ok, mode + " " + m.Label + ": GetFullPath accepted (Linux), isError, exactly " + m.Expected + ", a typed code (never McpServer's generic failure nor an internal error), nothing on stderr, working directory restored" + (m.Ok ? "" : observed));
            Check(Environment.CurrentDirectory == original, mode + ": the working directory is restored after all cases");
        }
    }
}
