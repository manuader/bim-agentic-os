using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// P1-44: the one copy of the deadline static pins' source helpers, compiled by tests/cli (Bim.Cli.Tests, default items)
// and linked by tests/mcp (Bim.Mcp.Tests.csproj): StripCode and MemberBody (P1-39, P1-41, P1-43; moved unchanged from
// tests/cli/RevitCommandDeadlineTests.cs), CommentsBlanked (P1-44), and the parsed argument list of a
// `new RevitDeadlineTransport(` construction (P1-40, P1-42): split at depth 0 and mapped onto the constructor's
// parameters as C# does. RevitCommandDeadlineTests (sections 2g, 2i, 3a) and the tests/mcp RevitCallAll pin call it.
// P1-45: CodeMask (comments and literals blanked at their positions) and MemberBody(raw: true), for a pin that reads
// the source as written instead of StripCode output. P1-47: the raw path skips a signature with any character (not only
// the first) in a comment or literal.
internal static class DeadlineSourceScan
{
    // P1-39: the one comment and literal strip of the static pins (2g, 2h, 2i; it replaces P1-36's StripComments and
    // P1-37's StripCommentsAndLiterals). Comments are blanked and every literal is reduced to its quotes, newlines kept:
    // regular, verbatim (@"..", @$, $@), raw ("""..""", any quote run and $ count) and char literals. The holes of an
    // interpolated literal are code and are kept, with any nested literal, comment or brace read the same way, so a
    // // or a quote inside a hole neither starts a comment nor ends the literal; a format text (':' at hole depth 0) is
    // literal text. P1-41: '::' (the global:: alias qualifier) is code, never a format colon.
    internal static string StripCode(string source)
    {
        var text = new StringBuilder(source.Length);
        var i = 0;
        StripCodeInto(source, ref i, text, 0, null, null);
        return text.ToString();
    }

    // P1-44: source with only its comments blanked (each comment character a space, newlines kept), literals and code
    // unchanged at their positions: for pins that read literal text or need to tell a comment line from literal text.
    internal static string CommentsBlanked(string source) => Blanked(source, comments: true, literals: false);

    // P1-45: the source with its comments AND every literal — its delimiters, its text, an interpolation hole's braces
    // and the format text after a hole's ':' — blanked the same way, every other character left at its own position,
    // the code inside a hole included, so the result has the source's length. MemberBody(raw: true) counts brackets on
    // it, where a bracket inside a string or char literal (or a comment) is not one of the code's.
    internal static string CodeMask(string source) => Blanked(source, comments: true, literals: true);

    static string Blanked(string source, bool comments, bool literals)
    {
        var marks = new bool[source.Length];
        var i = 0;
        StripCodeInto(source, ref i, new StringBuilder(source.Length), 0, comments ? marks : null, literals ? marks : null);
        var text = source.ToCharArray();
        for (var k = 0; k < text.Length; k++) if (marks[k] && text[k] != '\n') text[k] = ' ';
        return new string(text);
    }

    // Copies code from s[i..] into text. holeBraces > 0: inside an interpolation hole, returns past the run of holeBraces
    // '}' that closes it at depth 0 (the caller writes the hole's braces). P1-44: comments, when given, marks the source
    // positions of every comment (CommentsBlanked). P1-45: literals, when given, marks the positions of every literal —
    // delimiters, text, a hole's braces and a hole's format text — and never those of the code inside a hole (CodeMask).
    static void StripCodeInto(string s, ref int i, StringBuilder text, int holeBraces, bool[]? comments, bool[]? literals)
    {
        var depth = 0;
        void Blank(int from, int to) { for (var k = from; k < to && k < s.Length; k++) if (s[k] == '\n') text.Append('\n'); }
        void Mark(bool[]? marks, int from, int to) { if (marks is not null) for (var k = from; k < to && k < s.Length; k++) marks[k] = true; }
        void Hole(ref int pos, int braces)
        {
            text.Append('{');
            StripCodeInto(s, ref pos, text, braces, comments, literals);
            text.Append('}');
        }
        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && next == '/') { var from = i; while (i < s.Length && s[i] != '\n') i++; Mark(comments, from, i); continue; }
            if (c == '/' && next == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? s.Length : end + 2;
                Blank(i, stop);
                Mark(comments, i, stop);
                text.Append(' ');
                i = stop;
                continue;
            }
            if (c == '\'')
            {
                var p = i + 1;
                while (p < s.Length && s[p] != '\'' && s[p] != '\n') p += s[p] == '\\' ? 2 : 1;
                text.Append("''");
                var stop = Math.Min(s.Length, p + 1);
                Mark(literals, i, stop);
                i = stop;
                continue;
            }
            var at = i;
            var dollars = 0;
            var verbatim = false;
            while (at < s.Length && (s[at] == '$' || s[at] == '@')) { if (s[at] == '$') dollars++; else verbatim = true; at++; }
            if (at < s.Length && s[at] == '"')
            {
                var quotes = 0;
                while (!verbatim && at + quotes < s.Length && s[at + quotes] == '"') quotes++;
                text.Append('"');
                var p = quotes >= 3 ? at + quotes : quotes == 2 ? at + 2 : at + 1;
                Mark(literals, i, p);
                if (quotes >= 3)
                {
                    while (p < s.Length)
                    {
                        if (s[p] == '"')
                        {
                            var run = 0;
                            while (p + run < s.Length && s[p + run] == '"') run++;
                            Mark(literals, p, p + run);
                            p += run;
                            if (run >= quotes) break;
                            continue;
                        }
                        if (dollars > 0 && s[p] == '{')
                        {
                            var run = 0;
                            while (p + run < s.Length && s[p + run] == '{') run++;
                            Mark(literals, p, p + run);
                            p += run;
                            if (run >= dollars) Hole(ref p, dollars);
                            continue;
                        }
                        if (s[p] == '\n') text.Append('\n');
                        Mark(literals, p, p + 1);
                        p++;
                    }
                }
                else if (quotes <= 1)
                {
                    while (p < s.Length)
                    {
                        var d = s[p];
                        var after = p + 1 < s.Length ? s[p + 1] : '\0';
                        if (d == '"')
                        {
                            if (verbatim && after == '"') { Mark(literals, p, p + 2); p += 2; continue; }
                            Mark(literals, p, p + 1);
                            p++;
                            break;
                        }
                        if (!verbatim && d == '\\') { Mark(literals, p, p + 2); p += 2; continue; }
                        if (!verbatim && d == '\n') break;
                        if (dollars > 0 && (d == '{' || d == '}') && after == d) { Mark(literals, p, p + 2); p += 2; continue; }
                        if (dollars > 0 && d == '{') { Mark(literals, p, p + 1); p++; Hole(ref p, 1); continue; }
                        if (d == '\n') text.Append('\n');
                        Mark(literals, p, p + 1);
                        p++;
                    }
                }
                text.Append('"');
                i = Math.Min(s.Length, p);
                continue;
            }
            if (holeBraces > 0)
            {
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == '}')
                {
                    if (depth == 0) { Mark(literals, i, i + holeBraces); i = Math.Min(s.Length, i + holeBraces); return; }
                    depth--;
                }
                else if (c == ':' && depth == 0 && next != ':' && (i == 0 || s[i - 1] != ':'))
                {
                    var from = i;
                    while (i < s.Length && s[i] != '}' && s[i] != '\n') i++;
                    Mark(literals, from, i);
                    continue;
                }
            }
            text.Append(c);
            i++;
        }
    }

    // P1-39: the body of the first member whose code (StripCode output) starts with signature, from the signature through
    // its matching closing brace (brace depth, not indentation); null if absent or unclosed. P1-41: null too when a ';' or
    // '=>' comes before the first '{' (an abstract or expression-bodied member has no block of its own).
    // P1-43: the head is read at bracket depth 0 only: a ';', '=>' or '{' inside the parameter list or an attribute
    // (inside '(' .. ')' or '[' .. ']') is not the member's own end, arrow or block.
    // P1-45: `code` is StripCode output by default — there every literal is "" or an interpolated literal's
    // "{hole code}", whose brackets are that code's and are counted. `raw: true` reads the source as written: the
    // brackets are counted on CodeMask(code), so one inside a string or char literal (a parameter attribute like
    // [Note(")")], a default value) or inside a comment is not the code's, and a signature named in a comment or a
    // literal is not the member. The two cannot be one path: StripCode drops the '$' of an interpolated literal, so
    // its hole would read as literal text. A stray ')' or ']' never takes the bracket depth below 0.
    internal static string? MemberBody(string code, string signature, bool raw = false)
    {
        var scan = raw ? CodeMask(code) : code;
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        // P1-47: every character of the signature must be code (unmasked), not only the first.
        while (raw && start >= 0 && string.CompareOrdinal(scan, start, code, start, signature.Length) != 0) start = code.IndexOf(signature, start + 1, StringComparison.Ordinal);
        if (start < 0) return null;
        var nested = 0;
        for (var k = start; k < scan.Length; k++)
        {
            var c = scan[k];
            if (c is '(' or '[') nested++;
            else if (c is ')' or ']') { if (nested > 0) nested--; }
            else if (nested > 0) continue;
            else if (c == ';' || (c == '=' && k + 1 < scan.Length && scan[k + 1] == '>')) return null;
            else if (c == '{')
            {
                var depth = 0;
                for (var end = k; end < scan.Length; end++)
                {
                    if (scan[end] == '{') depth++;
                    else if (scan[end] == '}' && --depth == 0) return code[start..(end + 1)];
                }
                return null;
            }
        }
        return null;
    }

    // P1-48: the source lines of the member MemberBody(StripCode(source), signature) finds, read from
    // CommentsBlanked(source): comments blanked, literal text and code kept, line endings made "\n". StripCode keeps the
    // source's newlines, so the stripped member's lines are the source's lines (from its signature's line through its
    // closing brace's line, whole lines). "" when there is no block body. The one line mapping of the tests/mcp p122 and
    // p123 pins.
    internal static string MemberLines(string source, string signature) => "";

    // P1-40/P1-42: a `new [global::][Bim.Cli.]RevitDeadlineTransport(` construction in stripped code.
    internal const string ConstructionPattern = @"\bnew\s+(?:global\s*::\s*)?(?:Bim\s*\.\s*Cli\s*\.\s*)?RevitDeadlineTransport\s*\(";

    // The top-level arguments of the call whose '(' ends just before code[from]; null if it is not closed.
    internal static string[]? ArgumentsAt(string code, int from)
    {
        var arguments = new List<string>();
        var depth = 0;
        for (var i = from; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth-- > 0) continue;
                arguments.Add(code[from..i].Trim());
                return arguments.ToArray();
            }
            else if (c == ',' && depth == 0) { arguments.Add(code[from..i].Trim()); from = i + 1; }
        }
        return null;
    }

    // P1-40: every construction in stripped code (StripCode output: a '//' inside a literal is no comment), its arguments
    // by parameter name (positional by position, named by name), whitespace removed; Map null with the Reason for a call
    // that cannot be mapped.
    internal static List<(Dictionary<string, string>? Map, string Reason)> Calls(string code, string[] parameters)
    {
        var calls = new List<(Dictionary<string, string>? Map, string Reason)>();
        foreach (Match match in Regex.Matches(code, ConstructionPattern))
            calls.Add(MapArguments(ArgumentsAt(code, match.Index + match.Length), parameters));
        return calls;
    }

    // P1-42: maps one argument list onto parameters as C# does. `()` maps to no argument (checked before the loop); an
    // empty argument is refused; a named argument maps by name; a positional one by its position, which C# allows after
    // named arguments only while each of them is in its own position (else CS8323, refused by name).
    internal static (Dictionary<string, string>? Map, string Reason) MapArguments(string[]? arguments, string[] parameters)
    {
        if (arguments is null) return (null, "the argument list is not closed");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.Length == 1 && arguments[0].Length == 0) return (map, "");
        string? outOfPosition = null;
        for (var k = 0; k < arguments.Length; k++)
        {
            if (arguments[k].Length == 0) return (null, "argument " + N(k + 1) + " is empty");
            var name = Regex.Match(arguments[k], @"^([A-Za-z_][A-Za-z0-9_]*)\s*:(?!:)");
            string key;
            if (name.Success)
            {
                key = name.Groups[1].Value;
                if (Array.IndexOf(parameters, key) != k) outOfPosition ??= key;
            }
            else if (outOfPosition is not null)
                return (null, "positional argument " + N(k + 1) + " follows the out-of-position named argument '" + outOfPosition + "' (C# CS8323)");
            else if (k >= parameters.Length)
                return (null, "positional argument " + N(k + 1) + " is past the " + N(parameters.Length) + " parameters");
            else key = parameters[k];
            if (!parameters.Contains(key)) return (null, "no parameter named '" + key + "'");
            if (map.ContainsKey(key)) return (null, "parameter '" + key + "' given twice");
            map[key] = Regex.Replace(name.Success ? arguments[k][name.Length..] : arguments[k], @"\s+", "");
        }
        return (map, "");
    }

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
