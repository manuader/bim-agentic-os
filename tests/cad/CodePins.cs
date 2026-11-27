using System.Text;

// P6-49: the static-check helpers shared by the cad-brief source pins (the PDF reader-option and builder pins in the P6-39
// block of PdfType3GlyphListTests and the DXF reader-option pin in MinsertEzdxfTests), called directly (no reflection).
// StripCommentsAndLiterals keeps code only; Squeeze removes whitespace except one space between two identifier
// characters, so a reformatted line matches its pin while "public const int" stays three tokens; Count and IndexOf
// match a squeezed token only at identifier boundaries, so xpdf=... does not count as pdf=... and
// OtherMaxInputBytes= does not count as MaxInputBytes=. P6-51: CountAnywhere is the plain substring count the zero-count
// (banned-name) pins use, so a longer identifier such as BriefSerializerX still holds the banned BriefSerializer; and
// Assignments counts simple and compound assignments (=, +=, -=, ??= and the other compound operators) to an identifier,
// not comparisons (==, !=, <=, >=) or an expression body (=>). P6-53: Assignments and CountAnywhere have direct self-tests
// (and the one-stripper aggregate a planted-second-definition self-test) in the P6-39 block of PdfType3GlyphListTests.
// P6-55: that aggregate counts StripCommentsAndLiterals definitions per file, so a second definition inside this file is
// red too (self-test). P6-57: the planted self-test plants into every scanned file other than this one, one at a time,
// and a definition also counts when generic, returning string?, with a second parameter or as a non-static local
// function (self-test; the forms not counted are stated there).
internal static class CodePins
{
    // P6-27: C# source with // and /* */ comments removed and every string, verbatim, interpolated, raw and char literal
    // replaced by an empty one ("" or ''), so a static check matches code only.
    internal static string StripCommentsAndLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        var n = source.Length;
        var i = 0;
        while (i < n)
        {
            var c = source[i];
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                output.Append(' ');
                continue;
            }
            if (c == '\'')
            {
                var k = i + 1;
                while (k < n && source[k] != '\'' && source[k] != '\n')
                {
                    if (source[k] == '\\') k++;
                    k++;
                }
                output.Append("''");
                i = Math.Min(n, k + 1);
                continue;
            }
            if (c is '$' or '@' or '"')
            {
                var k = i;
                var verbatim = false;
                while (k < n && source[k] is '$' or '@')
                {
                    if (source[k] == '@') verbatim = true;
                    k++;
                }
                if (k < n && source[k] == '"')
                {
                    var quotes = 0;
                    while (k + quotes < n && source[k + quotes] == '"') quotes++;
                    // A verbatim string is never raw: @"""" is one escaped quote, not the start of a raw literal.
                    if (quotes >= 3 && !verbatim)
                    {
                        var close = source.IndexOf(new string('"', quotes), k + quotes, StringComparison.Ordinal);
                        i = close < 0 ? n : close + quotes;
                    }
                    else if (quotes == 2 && !verbatim) i = k + 2;
                    else if (verbatim)
                    {
                        k++;
                        while (k < n)
                        {
                            if (source[k] == '"')
                            {
                                if (k + 1 < n && source[k + 1] == '"') { k += 2; continue; }
                                break;
                            }
                            k++;
                        }
                        i = k + 1;
                    }
                    else
                    {
                        k++;
                        while (k < n && source[k] != '"' && source[k] != '\n')
                        {
                            if (source[k] == '\\') k++;
                            k++;
                        }
                        i = k + 1;
                    }
                    output.Append("\"\"");
                    continue;
                }
                output.Append(source, i, Math.Max(k - i, 1));
                i = Math.Max(k, i + 1);
                continue;
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Whitespace removed, except one space kept where both neighbours are identifier characters.
    internal static string Squeeze(string text)
    {
        var output = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = output.Length > 0; continue; }
            if (pendingSpace && IsIdentifier(c) && IsIdentifier(output[^1])) output.Append(' ');
            pendingSpace = false;
            output.Append(c);
        }
        return output.ToString();
    }

    // The first index at or after from of token (squeezed by the caller, as the text) whose ends do not run into an
    // adjacent identifier character; -1 when there is none.
    internal static int IndexOf(string squeezed, string token, int from = 0)
    {
        if (token.Length == 0) return -1;
        for (var at = squeezed.IndexOf(token, from, StringComparison.Ordinal); at >= 0; at = squeezed.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            var after = at + token.Length;
            var openStart = !IsIdentifier(token[0]) || at == 0 || !IsIdentifier(squeezed[at - 1]);
            var openEnd = !IsIdentifier(token[^1]) || after >= squeezed.Length || !IsIdentifier(squeezed[after]);
            if (openStart && openEnd) return at;
        }
        return -1;
    }

    // Non-overlapping occurrences of token at identifier boundaries.
    internal static int Count(string squeezed, string token)
    {
        var count = 0;
        for (var at = IndexOf(squeezed, token); at >= 0; at = IndexOf(squeezed, token, at + token.Length)) count++;
        return count;
    }

    // P6-51: non-overlapping occurrences of token anywhere (no identifier boundary), for pins whose count is 0.
    internal static int CountAnywhere(string squeezed, string token)
    {
        if (token.Length == 0) return 0;
        var count = 0;
        for (var at = squeezed.IndexOf(token, StringComparison.Ordinal); at >= 0; at = squeezed.IndexOf(token, at + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // Longest first, so >>= is not read as >= and ??= is not read as a bare =.
    private static readonly string[] AssignmentOperators = { ">>>=", "<<=", ">>=", "??=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "=" };

    // P6-51: occurrences of identifier (at identifier boundaries) directly followed by an assignment operator in the
    // squeezed code: = (not == or =>) and the compound operators. A comparison (==, !=, <=, >=) or an expression body
    // (=>) is not an assignment. ++ and -- are not counted (squeezed, x - -1 reads as x--1).
    internal static int Assignments(string squeezed, string identifier)
    {
        var count = 0;
        for (var at = IndexOf(squeezed, identifier); at >= 0; at = IndexOf(squeezed, identifier, at + identifier.Length))
        {
            var after = at + identifier.Length;
            var op = AssignmentOperators.FirstOrDefault(o => squeezed.AsSpan(after).StartsWith(o, StringComparison.Ordinal));
            if (op is null) continue;
            var next = after + op.Length < squeezed.Length ? squeezed[after + op.Length] : '\0';
            if (op == "=" && next is '=' or '>') continue; // == compares, => is an expression body
            count++;
        }
        return count;
    }
}
