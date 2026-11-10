using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P10-11 `audit-verify log.jsonl [--anchor head.json] [--actors a,b] [--summary] [--write-anchor head.json]`: the P10-09
// verifier (core/Bim.Core/AuditVerify.cs) on the CLI, with an external head anchor file. Oracles, all against the real
// Release CLI process:
//   1. stdout is byte-identical to AuditVerifier.VerifyBytes(bytes, options).ToJson() computed in process (with
//      --summary: AuditSummary.ToJson(report.VerifiedEntries)); exit 0 exactly when the report has no finding.
//   2. Every finding class the P10-09 vocabulary names (except the file codes, which bounded input refuses first) is
//      produced by a concrete tampering of a log written by the real AuditLog, and exits 1 with that code.
//   3. --write-anchor writes {"kind":"audit-anchor","version":1,"sequence":n,"hash":h} + LF for a clean log only, never
//      overwrites, and a later --anchor run detects tail truncation and a full rehash that the file alone cannot show.
//   4. Usage, option, anchor and bounded-input refusals print {"status":"failed","code":...[,"detail"]}, exit 1 and
//      write nothing; the log is never modified.
internal static class AuditVerifyTests
{
    const long MaxLogBytes = 64L * 1024 * 1024;

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "audit-verify");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "audit-verify: " + name, detail);

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
        static JsonElement? Parse(string text)
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        static string? Str(JsonElement? element, string name) =>
            element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        var counter = 0;
        string Path1(string name) => Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
        string Bytes(string name, byte[] content) { var path = Path1(name); File.WriteAllBytes(path, content); return path; }
        string Text(string name, string content) => Bytes(name, Utf8(content));

        // A typed refusal: exit 1, exactly one envelope with status failed and the code, and no anchor file written.
        void ExpectFailure(string name, string code, string? neverWritten, params string[] arguments)
        {
            var result = Cli(arguments);
            var envelope = Parse(result.Out);
            var members = envelope is { ValueKind: JsonValueKind.Object } o ? o.EnumerateObject().Select(p => p.Name).ToArray() : Array.Empty<string>();
            Check(result.Exit == 1 && Str(envelope, "status") == "failed" && Str(envelope, "code") == code
                && members.Length is 2 or 3 && members[0] == "status" && members[1] == "code" && (members.Length == 2 || members[2] == "detail")
                && (neverWritten is null || (!File.Exists(neverWritten) && !Directory.Exists(neverWritten))),
                name + " -> " + code + ", exit 1" + (neverWritten is null ? "" : ", no anchor file"), D(result));
        }

        // ---- fixture: a real AuditLog with three entries ----
        var clockTicks = new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero);
        var logPath = Path1("audit.jsonl");
        var log = AuditLog.Open(logPath, () => { clockTicks = clockTicks.AddHours(13); return clockTicks; });
        log.Append("alice", "op-1", "wall.create", "C:/models/a.rvt", true, "granted");
        log.Append("bob", "op-2", "door.delete", "C:/models/a.rvt", false, "read-only actor");
        log.Append("alice", "op-3", "wall.create", "C:/models/b.rvt", true, "granted");
        var logBytes = File.ReadAllBytes(logPath);
        var logText = Encoding.UTF8.GetString(logBytes);
        var lines = logText.Split('\n')[..^1];
        var entries = AuditLogFormat.VerifyBytes(logBytes).Entries;
        Check(lines.Length == 3 && entries.Count == 3, "fixture log has three verified entries", lines.Length.ToString(CultureInfo.InvariantCulture));
        string Report(byte[] bytes, AuditVerifyOptions? options = null) => AuditVerifier.VerifyBytes(bytes, options).ToJson();

        // ---- registration and usage ----
        var program = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs"));
        Check(program.Contains("case \"audit-verify\": AuditVerifyCommand.Run(args); break;", StringComparison.Ordinal), "Program.cs registers audit-verify");
        var defaultUsage = Cli("no-such-verb");
        Check(defaultUsage.Out.Contains("audit-verify log.jsonl [--anchor head.json] [--actors a,b] [--summary] [--write-anchor head.json]", StringComparison.Ordinal), "the default usage lists audit-verify", D(defaultUsage));

        // ---- success: canonical report, byte-identical to the library, deterministic, log untouched ----
        var mtime = File.GetLastWriteTimeUtc(logPath);
        var ok = Cli("audit-verify", logPath);
        var expectedOk = Report(logBytes);
        Check(ok.Exit == 0 && ok.Out == expectedOk && Parse(ok.Out) is { } okJson && okJson.GetProperty("ok").GetBoolean() && Str(okJson, "code") == "ok",
            "a clean log prints AuditVerifier.VerifyBytes(...).ToJson() byte for byte and exits 0", D(ok));
        var okAgain = Cli("audit-verify", logPath);
        Check(okAgain.Exit == 0 && okAgain.Out == ok.Out, "a second run prints the same bytes", D(okAgain));
        Check(File.ReadAllBytes(logPath).SequenceEqual(logBytes) && File.GetLastWriteTimeUtc(logPath) == mtime, "the log keeps its bytes and mtime");
        var empty = Bytes("empty.jsonl", Array.Empty<byte>());
        var emptyRun = Cli("audit-verify", empty);
        Check(emptyRun.Exit == 0 && emptyRun.Out == Report(Array.Empty<byte>()), "an empty log is a clean report with no entries", D(emptyRun));
        var bom = Bytes("bom.jsonl", [0xEF, 0xBB, 0xBF, .. logBytes]);
        var bomRun = Cli("audit-verify", bom);
        Check(bomRun.Exit == 1 && bomRun.Out == Report([0xEF, 0xBB, 0xBF, .. logBytes]) && Str(Parse(bomRun.Out), "code") == "audit_entry_not_canonical",
            "the log's raw bytes are verified (a byte order mark is not stripped: audit_entry_not_canonical)", D(bomRun));

        // ---- --summary ----
        var summary = Cli("audit-verify", logPath, "--summary");
        Check(summary.Exit == 0 && summary.Out == AuditSummary.ToJson(entries), "--summary prints AuditSummary.ToJson(verified entries) byte for byte", D(summary));
        var tornBytes = logBytes[..^1];
        var torn = Bytes("torn.jsonl", tornBytes);
        var tornSummary = Cli("audit-verify", "--summary", torn);
        Check(tornSummary.Exit == 1 && tornSummary.Out == AuditSummary.ToJson(AuditVerifier.VerifyBytes(tornBytes).VerifiedEntries),
            "--summary on a log with a finding prints only the verified prefix and exits 1", D(tornSummary));

        // ---- --actors ----
        var actorsOk = Cli("audit-verify", logPath, "--actors", "alice,bob");
        Check(actorsOk.Exit == 0 && actorsOk.Out == Report(logBytes, new AuditVerifyOptions(KnownActors: new[] { "alice", "bob" })), "--actors alice,bob: every actor known, exit 0", D(actorsOk));
        var actorsDup = Cli("audit-verify", "--actors", "bob,alice,bob", logPath);
        Check(actorsDup.Exit == 0 && actorsDup.Out == actorsOk.Out, "--actors is a set: order and repeats do not matter, the option may precede the log", D(actorsDup));
        var actorsUnknown = Cli("audit-verify", logPath, "--actors", "alice");
        Check(actorsUnknown.Exit == 1 && actorsUnknown.Out == Report(logBytes, new AuditVerifyOptions(KnownActors: new[] { "alice" })) && Str(Parse(actorsUnknown.Out), "code") == "audit_actor_unknown",
            "--actors alice: bob is audit_actor_unknown, exit 1", D(actorsUnknown));
        var actorsSpaced = Cli("audit-verify", logPath, "--actors", "alice, bob");
        Check(actorsSpaced.Exit == 1 && actorsSpaced.Out == Report(logBytes, new AuditVerifyOptions(KnownActors: new[] { "alice", " bob" })),
            "--actors items are taken verbatim (\" bob\" is not bob)", D(actorsSpaced));
        foreach (var bad in new[] { "", ",", "alice,", ",alice", "alice,,bob", " ", new string('a', 129) })
            ExpectFailure("--actors \"" + (bad.Length > 20 ? bad[..20] + "..." : bad) + "\"", "audit_verify_actors_invalid", null, "audit-verify", logPath, "--actors", bad);

        // ---- every finding class, exit 1, byte-identical to the library ----
        void Finding(string name, string code, byte[] bytes, AuditVerifyOptions? options = null, params string[] extra)
        {
            var path = Bytes(name + ".jsonl", bytes);
            var result = Cli(["audit-verify", path, .. extra]);
            var expected = Report(bytes, options);
            Check(result.Exit == 1 && result.Out == expected && Str(Parse(result.Out), "code") == code, name + " -> " + code + ", exit 1, report equals the library", D(result) + " expected " + expected);
        }
        string Rehash(AuditEntry entry) => AuditLogFormat.Line(entry with { Hash = AuditLogFormat.ComputeHash(entry) });
        Finding("malformed", "audit_entry_malformed", Utf8(lines[0] + "\n" + "not json\n" + lines[2] + "\n"));
        Finding("edited", "audit_entry_edited", Utf8(lines[0] + "\n" + lines[1].Replace("read-only actor", "allowed by owner", StringComparison.Ordinal) + "\n" + lines[2] + "\n"));
        Finding("chain-broken", "audit_chain_broken", Utf8(lines[0] + "\n" + Rehash(entries[1] with { Reason = "allowed by owner" }) + "\n" + lines[2] + "\n"));
        Finding("torn", "audit_truncated", tornBytes);
        Finding("entries-missing", "audit_entries_missing", Utf8(lines[0] + "\n" + lines[2] + "\n"));
        Finding("reordered", "audit_reordered", Utf8(lines[0] + "\n" + lines[2] + "\n" + lines[1] + "\n"));
        Finding("duplicated", "audit_sequence_broken", Utf8(lines[0] + "\n" + lines[1] + "\n" + lines[1] + "\n" + lines[2] + "\n"));
        Finding("crlf", "audit_entry_not_canonical", Utf8(lines[0] + "\r\n" + lines[1] + "\n" + lines[2] + "\n"));
        Finding("actor", "audit_actor_unknown", logBytes, new AuditVerifyOptions(KnownActors: new[] { "bob" }), "--actors", "bob");

        // ---- anchors: --write-anchor, then truncation and rehash detection with --anchor ----
        string AnchorText(long seq, string hash) => "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"hash\":\"" + hash + "\"}\n";
        var anchorPath = Path1("head.json");
        var write = Cli("audit-verify", logPath, "--write-anchor", anchorPath);
        Check(write.Exit == 0 && write.Out == expectedOk, "--write-anchor on a clean log prints the same report and exits 0", D(write));
        var anchorBytes = File.Exists(anchorPath) ? File.ReadAllBytes(anchorPath) : Array.Empty<byte>();
        Check(anchorBytes.SequenceEqual(Utf8(AnchorText(3, entries[2].Hash))), "the anchor file is {\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":3,\"hash\":<head hash>} + LF, UTF-8 without BOM",
            Encoding.UTF8.GetString(anchorBytes));
        var anchorMtime = File.Exists(anchorPath) ? File.GetLastWriteTimeUtc(anchorPath) : default;
        ExpectFailure("--write-anchor onto an existing anchor", "output_exists", null, "audit-verify", logPath, "--write-anchor", anchorPath);
        Check(File.Exists(anchorPath) && File.ReadAllBytes(anchorPath).SequenceEqual(anchorBytes) && File.GetLastWriteTimeUtc(anchorPath) == anchorMtime, "an existing anchor is never overwritten (bytes and mtime kept)");
        var anchor = new AuditAnchor(3, entries[2].Hash);

        var anchored = Cli("audit-verify", logPath, "--anchor", anchorPath);
        Check(anchored.Exit == 0 && anchored.Out == Report(logBytes, new AuditVerifyOptions(Anchor: anchor)), "--anchor on the anchored log: clean, exit 0", D(anchored));
        // Growing the log keeps the anchor valid.
        var grownPath = Path1("grown.jsonl");
        File.Copy(logPath, grownPath);
        var grown = AuditLog.Open(grownPath, () => new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), anchor);
        grown.Append("bob", "op-4", "door.delete", "C:/models/b.rvt", true, "granted");
        var grownRun = Cli("audit-verify", grownPath, "--anchor", anchorPath);
        Check(grownRun.Exit == 0 && grownRun.Out == Report(File.ReadAllBytes(grownPath), new AuditVerifyOptions(Anchor: anchor)), "an appended log still matches the earlier anchor", D(grownRun));
        // Tail truncation at an entry boundary: invisible from the file alone, audit_truncated against the anchor.
        var cutBytes = Utf8(lines[0] + "\n" + lines[1] + "\n");
        var cutPath = Bytes("cut.jsonl", cutBytes);
        var cutAlone = Cli("audit-verify", cutPath);
        Check(cutAlone.Exit == 0, "a log cut at an entry boundary verifies clean without the anchor (the P10-03 limit)", D(cutAlone));
        var cut = Cli("audit-verify", cutPath, "--anchor", anchorPath);
        Check(cut.Exit == 1 && cut.Out == Report(cutBytes, new AuditVerifyOptions(Anchor: anchor)) && Str(Parse(cut.Out), "code") == "audit_truncated",
            "the same cut log against the written anchor is audit_truncated, exit 1", D(cut));
        // Whole-file rewrite with recomputed hashes: audit_anchor_mismatch, and no entry is summarised.
        var rewritten = new List<string>();
        var previous = AuditLogFormat.GenesisHash;
        foreach (var entry in entries)
        {
            var changed = entry with { Reason = entry.Reason + " (rewritten)", PrevHash = previous, Hash = "" };
            changed = changed with { Hash = AuditLogFormat.ComputeHash(changed) };
            rewritten.Add(AuditLogFormat.Line(changed));
            previous = changed.Hash;
        }
        var rehashedBytes = Utf8(string.Concat(rewritten.Select(l => l + "\n")));
        var rehashedPath = Bytes("rehashed.jsonl", rehashedBytes);
        Check(Cli("audit-verify", rehashedPath).Exit == 0, "a fully rehashed log verifies clean without the anchor");
        var rehashed = Cli("audit-verify", rehashedPath, "--anchor", anchorPath);
        Check(rehashed.Exit == 1 && rehashed.Out == Report(rehashedBytes, new AuditVerifyOptions(Anchor: anchor)) && Str(Parse(rehashed.Out), "code") == "audit_anchor_mismatch",
            "a fully rehashed log against the anchor is audit_anchor_mismatch, exit 1", D(rehashed));
        var rehashedSummary = Cli("audit-verify", rehashedPath, "--anchor", anchorPath, "--summary");
        Check(rehashedSummary.Exit == 1 && rehashedSummary.Out == AuditSummary.ToJson(Array.Empty<AuditEntry>()), "--summary on an anchor mismatch counts no entry", D(rehashedSummary));
        var wrongHash = Text("wrong-hash.json", AnchorText(2, new string('a', 64)));
        Finding("anchor-mismatch", "audit_anchor_mismatch", logBytes, new AuditVerifyOptions(Anchor: new AuditAnchor(2, new string('a', 64))), "--anchor", wrongHash);
        var genesisWrong = Text("genesis-wrong.json", AnchorText(0, new string('b', 64)));
        Finding("anchor-zero-not-genesis", "audit_anchor_mismatch", logBytes, new AuditVerifyOptions(Anchor: new AuditAnchor(0, new string('b', 64))), "--anchor", genesisWrong);
        // The anchor parser tolerates insignificant whitespace, member order and a BOM; other shapes are refused.
        var pretty = Bytes("pretty.json", [0xEF, 0xBB, 0xBF, .. Utf8("{\r\n  \"hash\": \"" + entries[2].Hash + "\",\r\n  \"sequence\": 3,\r\n  \"version\": 1,\r\n  \"kind\": \"audit-anchor\"\r\n}\r\n")]);
        var prettyRun = Cli("audit-verify", logPath, "--anchor", pretty);
        Check(prettyRun.Exit == 0 && prettyRun.Out == anchored.Out, "an anchor with a BOM, CRLF, indentation and any member order reads the same", D(prettyRun));
        // Anchor plus write-anchor: a clean anchored log writes its new head.
        var grownHead = Path1("grown-head.json");
        var regrown = Cli("audit-verify", grownPath, "--anchor", anchorPath, "--write-anchor", grownHead);
        var grownEntries = AuditLogFormat.VerifyBytes(File.ReadAllBytes(grownPath)).Entries;
        Check(regrown.Exit == 0 && File.Exists(grownHead) && File.ReadAllText(grownHead) == AnchorText(4, grownEntries[3].Hash), "--anchor with --write-anchor writes the new head after a clean anchored check", D(regrown));
        var emptyHead = Path1("empty-head.json");
        var emptyWrite = Cli("audit-verify", empty, "--write-anchor", emptyHead);
        Check(emptyWrite.Exit == 0 && File.Exists(emptyHead) && File.ReadAllText(emptyHead) == AnchorText(0, AuditLogFormat.GenesisHash), "an empty log's anchor is sequence 0 with the genesis hash", D(emptyWrite));
        // A log with any finding writes no anchor.
        foreach (var (name, path, extra) in new[] { ("torn log", torn, Array.Empty<string>()), ("unknown actor", logPath, new[] { "--actors", "alice" }), ("cut log against its anchor", cutPath, new[] { "--anchor", anchorPath }) })
        {
            var target = Path1("never.json");
            var result = Cli(["audit-verify", path, "--write-anchor", target, .. extra]);
            Check(result.Exit == 1 && Parse(result.Out) is { ValueKind: JsonValueKind.Object } report && report.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.False && !File.Exists(target), "--write-anchor on a " + name + ": report printed, exit 1, no anchor written", D(result));
        }
        var badExtension = Path1("head.txt");
        ExpectFailure("--write-anchor without .json", "output_extension_unsupported", badExtension, "audit-verify", logPath, "--write-anchor", badExtension);
        var noDirectory = Path.Combine(scratch, "no-such-directory", "head.json");
        ExpectFailure("--write-anchor into a missing directory", "output_directory_not_found", noDirectory, "audit-verify", logPath, "--write-anchor", noDirectory);
        var missingLogHead = Path1("head.json");
        ExpectFailure("--write-anchor with a missing log", "input_not_found", missingLogHead, "audit-verify", Path1("missing.jsonl"), "--write-anchor", missingLogHead);

        // ---- anchor file refusals ----
        var h = entries[2].Hash;
        foreach (var (name, content) in new[]
        {
            ("not JSON", "not json"),
            ("an array", "[]"),
            ("missing hash", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":3}"),
            ("unknown member", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":3,\"hash\":\"" + h + "\",\"extra\":1}"),
            ("duplicate member", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":3,\"sequence\":3,\"hash\":\"" + h + "\"}"),
            ("wrong kind", "{\"kind\":\"audit-head\",\"version\":1,\"sequence\":3,\"hash\":\"" + h + "\"}"),
            ("version 2", "{\"kind\":\"audit-anchor\",\"version\":2,\"sequence\":3,\"hash\":\"" + h + "\"}"),
            ("negative sequence", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":-1,\"hash\":\"" + h + "\"}"),
            ("fractional sequence", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":3.5,\"hash\":\"" + h + "\"}"),
            ("string sequence", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":\"3\",\"hash\":\"" + h + "\"}"),
            ("upper-case hash", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":3,\"hash\":\"" + h.ToUpperInvariant() + "\"}"),
            ("short hash", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":3,\"hash\":\"" + h[..63] + "\"}"),
            ("trailing content", "{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":3,\"hash\":\"" + h + "\"} {}"),
        })
        {
            var target = Path1("never.json");
            ExpectFailure("anchor " + name, "audit_verify_anchor_invalid", target, "audit-verify", logPath, "--anchor", Text("anchor.json", content), "--write-anchor", target);
        }

        // ---- usage ----
        foreach (var bad in new[]
        {
            new[] { "audit-verify" },
            new[] { "audit-verify", "--summary" },
            new[] { "audit-verify", logPath, logPath },
            new[] { "audit-verify", logPath, "--anchor" },
            new[] { "audit-verify", logPath, "--actors" },
            new[] { "audit-verify", logPath, "--write-anchor" },
            new[] { "audit-verify", logPath, "--anchor", "--summary" },
            new[] { "audit-verify", logPath, "--anchor", "" },
            new[] { "audit-verify", "", "--summary" },
            new[] { "audit-verify", logPath, "--summary", "--summary" },
            new[] { "audit-verify", logPath, "--anchor", anchorPath, "--anchor", anchorPath },
            new[] { "audit-verify", logPath, "--actors", "alice", "--actors", "bob" },
            new[] { "audit-verify", logPath, "--unknown" },
            new[] { "audit-verify", logPath, "-summary" },
        })
        {
            var result = Cli(bad);
            var envelope = Parse(result.Out);
            Check(result.Exit == 1 && Str(envelope, "status") == "failed" && Str(envelope, "code") == "audit_verify_usage" && (Str(envelope, "detail") ?? "").StartsWith("usage: audit-verify log.jsonl", StringComparison.Ordinal),
                "usage refusal for: " + string.Join(" ", bad.Skip(1).Select(a => a == logPath ? "<log>" : a == anchorPath ? "<anchor>" : a.Length == 0 ? "\"\"" : a)), D(result));
        }

        // ---- bounded input ----
        ExpectFailure("missing log", "input_not_found", null, "audit-verify", Path1("missing.jsonl"));
        ExpectFailure("log is a directory", "input_not_regular_file", null, "audit-verify", scratch);
        ExpectFailure("missing anchor", "input_not_found", null, "audit-verify", logPath, "--anchor", Path1("missing.json"));
        ExpectFailure("anchor over 8 MiB", "input_too_large", null, "audit-verify", logPath, "--anchor", Bytes("over-cap.json", overCap));
        var atLogCap = Path1("at-cap.jsonl");
        using (var file = new FileStream(atLogCap, FileMode.CreateNew)) file.SetLength(MaxLogBytes + 1);
        ExpectFailure("log over 64 MiB", "input_too_large", null, "audit-verify", atLogCap);
        File.Delete(atLogCap);
        if (!OperatingSystem.IsWindows())
        {
            var link = Path1("link.jsonl");
            var linked = true;
            try { File.CreateSymbolicLink(link, logPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { linked = false; }
            if (linked) ExpectFailure("log through a symbolic link", "input_link_rejected", null, "audit-verify", link);
        }
        Check(File.ReadAllBytes(logPath).SequenceEqual(logBytes) && File.GetLastWriteTimeUtc(logPath) == mtime, "after every run the log keeps its bytes and mtime");
    }
}
