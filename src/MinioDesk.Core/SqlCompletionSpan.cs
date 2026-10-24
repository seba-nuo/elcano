namespace MinioDesk.Core;

public sealed record SqlCompletionSpan(int Start, int End, string Prefix, bool CanComplete = true)
{
    public static SqlCompletionSpan At(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        for (var i = 0; i < text.Length;)
        {
            var start = i;
            if (text[i] == '"')
            {
                i++; var content = i; var closed = false;
                while (i < text.Length)
                {
                    if (text[i] != '"') { i++; continue; }
                    if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                    i++; closed = true; break;
                }
                if (caret > start && caret <= i)
                {
                    var prefixEnd = Math.Min(caret, closed ? i - 1 : i);
                    return new(start, i, text[content..prefixEnd].Replace("\"\"", "\""));
                }
                continue;
            }
            if (text[i] == '\'' || (i + 1 < text.Length && text.AsSpan(i, 2).SequenceEqual("--")))
            {
                var quoted = text[i] == '\''; var closed = false; i++;
                while (i < text.Length)
                {
                    if (!quoted && text[i] == '\n') break;
                    if (quoted && text[i] == '\'')
                    { if (i + 1 < text.Length && text[i + 1] == '\'') { i += 2; continue; } i++; closed = true; break; }
                    i++;
                }
                if (caret > start && (caret < i || caret == i && (!quoted || !closed))) return new(caret, caret, "", false);
                continue;
            }
            if (i + 1 < text.Length && text.AsSpan(i, 2).SequenceEqual("/*"))
            {
                var depth = 1; i += 2;
                while (i < text.Length && depth > 0)
                {
                    if (i + 1 < text.Length && text.AsSpan(i, 2).SequenceEqual("/*")) { depth++; i += 2; }
                    else if (i + 1 < text.Length && text.AsSpan(i, 2).SequenceEqual("*/")) { depth--; i += 2; }
                    else i++;
                }
                if (caret > start && (caret < i || depth > 0 && caret == i)) return new(caret, caret, "", false);
                continue;
            }
            if (text[i] == '$')
            {
                var tagEnd = i + 1; while (tagEnd < text.Length && (char.IsLetterOrDigit(text[tagEnd]) || text[tagEnd] == '_')) tagEnd++;
                if (tagEnd < text.Length && text[tagEnd] == '$')
                {
                    var tag = text[i..(tagEnd + 1)]; var closing = text.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                    i = closing < 0 ? text.Length : closing + tag.Length;
                    if (caret > start && (caret < i || closing < 0 && caret == i)) return new(caret, caret, "", false);
                    continue;
                }
            }
            i++;
        }
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        var left = caret; var right = caret;
        while (left > 0 && Word(text[left - 1])) left--;
        while (right < text.Length && Word(text[right])) right++;
        return new(left, right, text[left..caret]);
    }
}
