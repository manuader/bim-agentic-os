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
// the first) in a comment or literal. P1-49: read from the mask marks (CodeMarks), so a masked space or newline counts.
// P1-51: CodeMask's blanked text and those marks come from one StripCodeInto pass (MaskPass), counted by MaskPasses.
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

    // P1-51: every comment/literal marking pass over a source (MaskPass), counted per thread, so a self-test can pin how
    // many passes one MemberBody(raw: true) costs (one).
    [ThreadStatic] static int maskPasses;
    internal static int MaskPasses => maskPasses;

    static string Blanked(string source, bool comments, bool literals) => MaskPass(source, comments, literals).Text;

    // P1-51: the one StripCodeInto pass behind Blanked and the mask marks: the marked positions (P1-49 CodeMarks when both
    // comments and literals are marked) and the source with each marked character but a newline blanked to a space.
    static (string Text, bool[] Marks) MaskPass(string source, bool comments, bool literals)
    {
        maskPasses++;
        var marks = new bool[source.Length];
        var i = 0;
        StripCodeInto(source, ref i, new StringBuilder(source.Length), 0, comments ? marks : null, literals ? marks : null);
        var text = source.ToCharArray();
        for (var k = 0; k < text.Length; k++) if (marks[k] && text[k] != '\n') text[k] = ' ';
        return (new string(text), marks);
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
        // P1-51: the mask and its marks (the P1-49 CodeMarks: the positions CodeMask blanks, newlines included) come from
        // one MaskPass, not one StripCodeInto pass each.
        (string Text, bool[]? Marks) mask = raw ? MaskPass(code, comments: true, literals: true) : (code, null);
        var (scan, marks) = mask;
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        // P1-47: every character of the signature must be code (unmasked), not only the first. P1-49: read from the mask
        // marks, not the blanked text, where a masked space or newline is the same character as the code's.
        bool Masked(int from) { for (var k = from; k < from + signature.Length; k++) if (marks![k]) return true; return false; }
        while (raw && start >= 0 && Masked(start)) start = code.IndexOf(signature, start + 1, StringComparison.Ordinal);
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
    internal static string MemberLines(string source, string signature)
    {
        var raw = source.ReplaceLineEndings("\n");
        var stripped = StripCode(raw);
        var member = MemberBody(stripped, signature);
        if (member is null) return "";
        var first = stripped[..stripped.IndexOf(signature, StringComparison.Ordinal)].Count(c => c == '\n');
        return string.Join("\n", CommentsBlanked(raw).Split('\n').Skip(first).Take(member.Count(c => c == '\n') + 1));
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

    // P1-52: the call-chain reader of the P1-50 tests/mirror-journal warning bound (SourceBlocks, ContextAt, CallRuns and
    // their helpers), moved unchanged from tests/cli/RevitCommandDeadlineTests.cs and made internal so other pins can
    // share it. It reads CodeMask output (comments and literals blanked in place). A brace block's header is the code
    // since the previous '{', '}' or ';' outside parentheses ("if (x)", "static void M(int a)", "static class C").
    // It is a heuristic reader, not a C# parser. Limits (what it does not see; a count can be wrong there, unnamed):
    //   - delegates: a call through a delegate, an event, a Func/Action field or an interface is not followed; only a
    //     method group written next to the member's own name is named a problem;
    //   - goto: a goto and its label are not read, so a jump back over a call is not seen as a loop;
    //   - partial types: a type is the name in its own block header; the members of a partial type declared in another
    //     block or file are not joined, and a qualified call is matched on its last qualifier name only (no alias,
    //     namespace or nested-type resolution); overloads of one name are one member;
    //   - object initialisers: a '{' after `new T(..)`, `new[]`, `= ` or `switch` opens a block like any other, so a
    //     call in an object or collection initialiser, an anonymous type or a switch expression arm reads as one in a
    //     nested block (counted once, not a loop) whose header is the expression before it;
    //   - also unread: yield iterators and async methods (a call there is counted as in a plain method).
    internal readonly record struct SourceBlock(int Open, int Close, string Header);
    internal readonly record struct SourceContext(string? Type, string? Member, SourceBlock? MemberBlock, List<SourceBlock> Inner, string Problem);
    static readonly System.Text.RegularExpressions.Regex TypeHeader = new(@"^(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|file|unsafe|new|ref)\s+)*(?:class|struct|interface|record(?:\s+class|\s+struct)?)\s+(\w+)");
    static readonly System.Text.RegularExpressions.Regex ControlHeader = new(@"^(?:else\s+)?(?:if|for|foreach|while|switch|catch|using|lock|fixed|do|try|finally|else|checked|unchecked|unsafe|get|set|init)\b");
    static readonly System.Text.RegularExpressions.Regex LoopOrLambda = new(@"\b(?:for|foreach|while|do)\b|=>");
    static readonly System.Text.RegularExpressions.Regex IfHeader = new(@"^(?:else\s+)?if\b|^else\b");

    internal static string Collapsed(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    internal static List<SourceBlock> SourceBlocks(string mask)
    {
        var blocks = new List<SourceBlock>();
        var open = new Stack<(int At, string Header, int Parens)>();
        var last = -1;
        var parens = 0;
        for (var k = 0; k < mask.Length; k++)
        {
            var c = mask[k];
            if (c == '(') parens++;
            else if (c == ')') { if (parens > 0) parens--; }
            else if (c == ';') { if (parens == 0) last = k; }
            else if (c == '{') { open.Push((k, Collapsed(mask[(last + 1)..k]), parens)); parens = 0; last = k; }
            else if (c == '}')
            {
                if (open.Count > 0) { var (at, header, outer) = open.Pop(); blocks.Add(new SourceBlock(at, k, header)); parens = outer; }
                if (parens == 0) last = k;
            }
        }
        blocks.Sort((a, b) => a.Open.CompareTo(b.Open));
        return blocks;
    }

    // The name of a method or local function whose header this is (ends with its parameter list, no control keyword,
    // no '=' before the list, no '=>'); null otherwise.
    static string? FunctionName(string header)
    {
        if (header.Length == 0 || header[^1] != ')' || ControlHeader.IsMatch(header) || header.Contains("=>", StringComparison.Ordinal)) return null;
        var depth = 0;
        var k = header.Length - 1;
        for (; k >= 0; k--)
        {
            if (header[k] == ')') depth++;
            else if (header[k] == '(' && --depth == 0) break;
        }
        if (k <= 0) return null;
        var head = header[..k].TrimEnd();
        if (head.Contains('=')) return null;
        var name = System.Text.RegularExpressions.Regex.Match(head, @"(\w+)\s*(?:<[^<>]*>)?$");
        return name.Success ? name.Groups[1].Value : null;
    }

    // The statement text from the last '{', '}' or ';' (outside parentheses) after position from up to position at.
    static string StatementBefore(string mask, int from, int at)
    {
        var last = from;
        var depth = 0;
        var parens = 0;
        for (var k = from + 1; k < at; k++)
        {
            var c = mask[k];
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0 && parens == 0) last = k; }
            else if (depth > 0) continue;
            else if (c == '(') parens++;
            else if (c == ')') { if (parens > 0) parens--; }
            else if (c == ';' && parens == 0) last = k;
        }
        return Collapsed(mask[(last + 1)..at]);
    }

    // The last statement of the block open..close ("{block}" when a nested block ends it).
    static string LastStatement(string mask, int open, int close)
    {
        var last = open;
        var depth = 0;
        var parens = 0;
        var statement = "";
        for (var k = open + 1; k < close; k++)
        {
            var c = mask[k];
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0 && parens == 0) { statement = "{block}"; last = k; } }
            else if (depth > 0) continue;
            else if (c == '(') parens++;
            else if (c == ')') { if (parens > 0) parens--; }
            else if (c == ';' && parens == 0) { statement = Collapsed(mask[(last + 1)..k]); last = k; }
        }
        var rest = Collapsed(mask[(last + 1)..close]);
        return rest.Length > 0 ? rest : statement;
    }

    // The type and method a position sits in (no type: top-level statements) and the blocks inside that method.
    internal static SourceContext ContextAt(List<SourceBlock> blocks, int at)
    {
        var enclosing = blocks.Where(b => b.Open < at && at < b.Close).ToList();
        var typeIndex = enclosing.FindLastIndex(b => TypeHeader.IsMatch(b.Header));
        if (typeIndex < 0) return new SourceContext(null, null, null, enclosing, "");
        var type = TypeHeader.Match(enclosing[typeIndex].Header).Groups[1].Value;
        if (typeIndex + 1 >= enclosing.Count) return new SourceContext(type, null, null, new List<SourceBlock>(), "outside a method body");
        var member = enclosing[typeIndex + 1];
        var name = FunctionName(member.Header);
        if (name is null) return new SourceContext(type, null, null, new List<SourceBlock>(), "in a member that is not a method (" + member.Header + ")");
        return new SourceContext(type, name, member, enclosing.Skip(typeIndex + 2).ToList(), "");
    }

    // "" when the position runs at most once per run of its method: no loop, lambda or local function around it there.
    internal static string ChainProblem(string path, string mask, SourceContext context, int at, string what)
    {
        var where = what + " at " + path + ":" + N(mask[..at].Count(c => c == '\n') + 1);
        if (context.Problem.Length > 0) return where + " is " + context.Problem;
        foreach (var block in context.Inner)
        {
            if (FunctionName(block.Header) is not null) return where + " is inside a local function";
            if (LoopOrLambda.IsMatch(block.Header)) return where + " is in a loop or lambda (" + block.Header + ")";
        }
        var from = context.Inner.Count > 0 ? context.Inner[^1].Open : context.MemberBlock?.Open ?? -1;
        var statement = StatementBefore(mask, from, at);
        return LoopOrLambda.IsMatch(statement) ? where + " is in a loop or lambda (" + statement + ")" : "";
    }

    // How many times type.member can run in one run: the sum over its callers (qualified type.member( anywhere, or an
    // unqualified member( inside type that is no local function's) of the calls per run of the caller times the caller's
    // own runs (top-level statements run once). Per caller run the calls are the most of: those outside a branch that
    // ends with return, or for such a branch, those before its end that are not in an earlier such branch. A call in a
    // loop, lambda or local function, a method group use and a recursive chain are problems.
    internal static long CallRuns(Dictionary<string, (string Mask, List<SourceBlock> Blocks)> files, string type, string member, HashSet<string> visiting, List<string> problems)
    {
        var key = type + "." + member;
        if (!visiting.Add(key)) { problems.Add("a recursive call chain through " + key); return 0; }
        var callers = new Dictionary<(string Path, int Open), (string? Type, string? Member, List<(int At, List<SourceBlock> Returning)> Sites)>();
        foreach (var file in files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var path = file.Key;
            var (mask, blocks) = file.Value;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(mask, @"\b" + member + @"\b"))
            {
                var at = match.Index;
                var k = at - 1;
                while (k >= 0 && char.IsWhiteSpace(mask[k])) k--;
                var qualified = k >= 0 && mask[k] == '.';
                if (qualified)
                {
                    var q = k - 1;
                    while (q >= 0 && char.IsWhiteSpace(mask[q])) q--;
                    var qualifier = System.Text.RegularExpressions.Regex.Match(mask[..(q + 1)], @"(\w+)$");
                    if (!qualifier.Success || qualifier.Groups[1].Value != type) continue;
                }
                var context = ContextAt(blocks, at);
                if (!qualified && context.Type != type) continue;
                var line = N(mask[..at].Count(c => c == '\n') + 1);
                var p = at + match.Length;
                while (p < mask.Length && char.IsWhiteSpace(mask[p])) p++;
                if (p >= mask.Length || mask[p] != '(')
                {
                    var before = k >= 0 ? mask[k] : '\0';
                    if (p < mask.Length && (mask[p] is ';' or ',' or ')') && (qualified || before is '=' or '(' or ',' or '+' or '?' or ':' || System.Text.RegularExpressions.Regex.IsMatch(mask[..(k + 1)], @"\breturn$")))
                        problems.Add(key + " is used without a call at " + path + ":" + line + " (a method group is not counted)");
                    continue;
                }
                var depth = 0;
                var close = p;
                for (; close < mask.Length; close++)
                {
                    if (mask[close] == '(') depth++;
                    else if (mask[close] == ')' && --depth == 0) break;
                }
                var r = close + 1;
                while (r < mask.Length && char.IsWhiteSpace(mask[r])) r++;
                if (r >= mask.Length || mask[r] == '{' || mask.AsSpan(r).StartsWith("=>", StringComparison.Ordinal) || mask.AsSpan(r).StartsWith("where", StringComparison.Ordinal)) continue; // a declaration
                if (!qualified && context.MemberBlock is { } own && blocks.Any(b => own.Open < b.Open && b.Open < own.Close && FunctionName(b.Header) == member)) continue;
                var problem = ChainProblem(path, mask, context, at, "a call of " + key);
                if (problem.Length > 0) { problems.Add(problem); continue; }
                var returning = context.Inner.Where(b => IfHeader.IsMatch(b.Header) && System.Text.RegularExpressions.Regex.IsMatch(LastStatement(mask, b.Open, b.Close), @"^return\b")).ToList();
                var slot = (path, context.MemberBlock?.Open ?? -1);
                if (!callers.TryGetValue(slot, out var caller)) callers[slot] = caller = (context.Type, context.Member, new List<(int At, List<SourceBlock> Returning)>());
                caller.Sites.Add((at, returning));
            }
        }
        long total = 0;
        foreach (var caller in callers.Values)
        {
            var sites = caller.Sites;
            var count = sites.Count(site => site.Returning.Count == 0);
            foreach (var branch in sites.SelectMany(site => site.Returning).Distinct())
                count = Math.Max(count, sites.Count(site => site.At < branch.Close && !site.Returning.Any(earlier => earlier.Close < branch.Open)));
            var runs = caller.Type is null || caller.Member is null ? 1 : CallRuns(files, caller.Type, caller.Member, visiting, problems);
            total += count * runs;
        }
        visiting.Remove(key);
        return total;
    }


    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
