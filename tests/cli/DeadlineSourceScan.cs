using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// P1-44: the one copy of the deadline static pins' source helpers, compiled by tests/cli (Bim.Cli.Tests, default items)
// and linked by tests/mcp (Bim.Mcp.Tests.csproj): StripCode and MemberBody (P1-39, P1-41, P1-43; moved unchanged from
// tests/cli/RevitCommandDeadlineTests.cs), CommentsBlanked (P1-44), and the parsed argument list of a
// `new RevitDeadlineTransport(` construction (P1-40, P1-42): split at depth 0 and mapped onto the constructor's
// parameters as C# does. RevitCommandDeadlineTests (sections 2g, 2i, 3a) and the tests/mcp RevitCallAll pin call it.
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
        StripCodeInto(source, ref i, text, 0, null);
        return text.ToString();
    }

    // P1-44: source with only its comments blanked (each comment character a space, newlines kept), literals and code
    // unchanged at their positions: for pins that read literal text or need to tell a comment line from literal text.
    internal static string CommentsBlanked(string source)
    {
        var comments = new bool[source.Length];
        var i = 0;
        StripCodeInto(source, ref i, new StringBuilder(source.Length), 0, comments);
        var text = source.ToCharArray();
        for (var k = 0; k < text.Length; k++) if (comments[k] && text[k] != '\n') text[k] = ' ';
        return new string(text);
    }

    // Copies code from s[i..] into text. holeBraces > 0: inside an interpolation hole, returns past the run of holeBraces
    // '}' that closes it at depth 0 (the caller writes the hole's braces). P1-44: comments, when given, marks the source
    // positions of every comment (CommentsBlanked).
    static void StripCodeInto(string s, ref int i, StringBuilder text, int holeBraces, bool[]? comments)
    {
        var depth = 0;
        void Blank(int from, int to) { for (var k = from; k < to && k < s.Length; k++) if (s[k] == '\n') text.Append('\n'); }
        void Mark(int from, int to) { if (comments is not null) for (var k = from; k < to && k < s.Length; k++) comments[k] = true; }
        void Hole(ref int pos, int braces)
        {
            text.Append('{');
            StripCodeInto(s, ref pos, text, braces, comments);
            text.Append('}');
        }
        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && next == '/') { var from = i; while (i < s.Length && s[i] != '\n') i++; Mark(from, i); continue; }
            if (c == '/' && next == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? s.Length : end + 2;
                Blank(i, stop);
                Mark(i, stop);
                text.Append(' ');
                i = stop;
                continue;
            }
            if (c == '\'')
            {
                var p = i + 1;
                while (p < s.Length && s[p] != '\'' && s[p] != '\n') p += s[p] == '\\' ? 2 : 1;
                text.Append("''");
                i = Math.Min(s.Length, p + 1);
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
                var p = at + 1;
                if (quotes == 2) p = at + 2;
                else if (quotes >= 3)
                {
                    p = at + quotes;
                    while (p < s.Length)
                    {
                        if (s[p] == '"')
                        {
                            var run = 0;
                            while (p + run < s.Length && s[p + run] == '"') run++;
                            p += run;
                            if (run >= quotes) break;
                            continue;
                        }
                        if (dollars > 0 && s[p] == '{')
                        {
                            var run = 0;
                            while (p + run < s.Length && s[p + run] == '{') run++;
                            p += run;
                            if (run >= dollars) Hole(ref p, dollars);
                            continue;
                        }
                        if (s[p] == '\n') text.Append('\n');
                        p++;
                    }
                }
                else
                {
                    while (p < s.Length)
                    {
                        var d = s[p];
                        var after = p + 1 < s.Length ? s[p + 1] : '\0';
                        if (d == '"')
                        {
                            if (verbatim && after == '"') { p += 2; continue; }
                            p++;
                            break;
                        }
                        if (!verbatim && d == '\\') { p += 2; continue; }
                        if (!verbatim && d == '\n') break;
                        if (dollars > 0 && (d == '{' || d == '}') && after == d) { p += 2; continue; }
                        if (dollars > 0 && d == '{') { p++; Hole(ref p, 1); continue; }
                        if (d == '\n') text.Append('\n');
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
                    if (depth == 0) { i = Math.Min(s.Length, i + holeBraces); return; }
                    depth--;
                }
                else if (c == ':' && depth == 0 && next != ':' && (i == 0 || s[i - 1] != ':'))
                {
                    while (i < s.Length && s[i] != '}' && s[i] != '\n') i++;
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
    internal static string? MemberBody(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        var nested = 0;
        for (var k = start; k < code.Length; k++)
        {
            var c = code[k];
            if (c is '(' or '[') nested++;
            else if (c is ')' or ']') nested--;
            else if (nested > 0) continue;
            else if (c == ';' || (c == '=' && k + 1 < code.Length && code[k + 1] == '>')) return null;
            else if (c == '{')
            {
                var depth = 0;
                for (var end = k; end < code.Length; end++)
                {
                    if (code[end] == '{') depth++;
                    else if (code[end] == '}' && --depth == 0) return code[start..(end + 1)];
                }
                return null;
            }
        }
        return null;
    }

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
