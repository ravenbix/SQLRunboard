using System.Text.RegularExpressions;

namespace SqlRunboard.Core;

/// <summary>One GO part: the text to send, how many times to run it (GO n), and its first line in the file (1-based).</summary>
public sealed record ScriptPart(string Text, int Repeat, int Line);

/// <summary>The script reader and GO splitter.</summary>
public static partial class SqlScript
{
    /// <summary>
    /// Splits on lines that hold only GO or GO n (any case, spaces and a -- comment after it), as SSMS does.
    /// GO inside a /* */ comment (nested too) or a '', "" or [] literal that continues across lines does not split.
    /// </summary>
    public static IReadOnlyList<ScriptPart> Split(string text)
    {
        var parts = new List<ScriptPart>();
        int start = 0, startLine = 1, line = 1, depth = 0; // depth: nested /* */ comments open at this point
        var close = '\0'; // the quote that ends the open literal, or \0
        for (var pos = 0; pos <= text.Length; line++)
        {
            var end = text.IndexOf('\n', pos);
            if (end < 0) end = text.Length;
            var go = depth == 0 && close == '\0' ? GoLine().Match(text, pos, end - pos) : Match.Empty;
            var repeat = 1;
            if (go.Success && (!go.Groups[1].Success || int.TryParse(go.Groups[1].Value, out repeat) && repeat > 0))
            {
                Add(text[start..pos], repeat);
                start = Math.Min(end + 1, text.Length);
                startLine = line + 1;
            }
            else Scan(text, pos, end, ref depth, ref close);
            pos = end + 1;
        }
        Add(text[start..], 1);
        return parts;

        void Add(string part, int repeat)
        {
            part = part.TrimEnd();
            if (part.Trim().Length > 0) parts.Add(new ScriptPart(part, repeat, startLine));
        }
    }

    // Moves the comment and literal state over text[pos..end] (one line).
    static void Scan(string text, int pos, int end, ref int depth, ref char close)
    {
        for (var i = pos; i < end; i++)
        {
            var c = text[i];
            var n = i + 1 < end ? text[i + 1] : '\0';
            if (close != '\0')
            {
                if (c != close) continue;
                if (n == close) i++; // '' or ]] or "" is an escaped quote
                else close = '\0';
            }
            else if (c == '/' && n == '*') { depth++; i++; }
            else if (depth > 0) { if (c == '*' && n == '/') { depth--; i++; } }
            else if (c == '-' && n == '-') return;
            else if (c is '\'' or '"') close = c;
            else if (c == '[') close = ']';
        }
    }

    [GeneratedRegex(@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
