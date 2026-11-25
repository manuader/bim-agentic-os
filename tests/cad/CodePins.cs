using System.Text;

// P6-49: the static-check helpers shared by the cad-brief source pins (the PDF reader-option and builder pins in the P6-39
// block of PdfType3GlyphListTests and the DXF reader-option pin in MinsertEzdxfTests), called directly (no reflection).
// StripCommentsAndLiterals keeps code only; Squeeze removes whitespace except one space between two identifier
// characters, so a reformatted line matches its pin while "public const int" stays three tokens; Count and IndexOf
// match a squeezed token only at identifier boundaries, so xpdf=... does not count as pdf=... and
// OtherMaxInputBytes= does not count as MaxInputBytes=.
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
}
