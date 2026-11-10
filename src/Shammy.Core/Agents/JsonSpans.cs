using System.Globalization;
using System.Text;

namespace Shammy.Core.Agents;

internal enum SpanKind { Object, Array, String, Other }

/// <summary>A JSON value and where it sits in the text: [<see cref="Start"/>, <see cref="End"/>).</summary>
internal sealed class SpanNode
{
    public SpanKind Kind;
    public int Start, End;

    /// <summary>An object's members, in file order.</summary>
    public List<SpanMember> Members = new();

    /// <summary>An array's elements.</summary>
    public List<SpanNode> Items = new();

    /// <summary>A string's value, unescaped.</summary>
    public string Text = "";

    /// <summary>The last member with that name (what a JSON reader would keep), or null.</summary>
    public SpanMember? Find(string key)
    {
        for (int i = Members.Count - 1; i >= 0; i--)
            if (Members[i].Key == key) return Members[i];
        return null;
    }

    /// <summary>How many children: members of an object, elements of an array.</summary>
    public int Count => Kind == SpanKind.Object ? Members.Count : Items.Count;

    /// <summary>Where child <paramref name="i"/> starts (a member starts at its name's quote).</summary>
    public int ChildStart(int i) => Kind == SpanKind.Object ? Members[i].Start : Items[i].Start;

    public int ChildEnd(int i) => Kind == SpanKind.Object ? Members[i].Value.End : Items[i].End;
}

internal sealed class SpanMember
{
    public string Key = "";
    public int Start;
    public SpanNode Value = null!;
}

/// <summary>
/// Reads strict JSON into a tree that remembers where every value sits in the text, so a file can be changed in
/// one place and left exactly as it was everywhere else. Throws <see cref="FormatException"/> on anything that
/// is not plain JSON (comments, trailing commas, text after the value).
/// </summary>
internal static class JsonSpans
{
    const int MaxDepth = 64;

    public static SpanNode Parse(string text)
    {
        int pos = 0;
        var node = Value(text, ref pos, 0);
        Skip(text, ref pos);
        if (pos != text.Length) throw new FormatException("Text after the JSON value");
        return node;
    }

    static void Skip(string s, ref int pos)
    {
        while (pos < s.Length && s[pos] is ' ' or '\t' or '\r' or '\n') pos++;
    }

    static SpanNode Value(string s, ref int pos, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("Nested too deeply");
        Skip(s, ref pos);
        if (pos >= s.Length) throw new FormatException("Ended early");
        switch (s[pos])
        {
            case '{': return Object(s, ref pos, depth);
            case '[': return Array(s, ref pos, depth);
            case '"':
            {
                int start = pos;
                string value = String(s, ref pos);
                return new SpanNode { Kind = SpanKind.String, Start = start, End = pos, Text = value };
            }
            default:
            {
                int start = pos;
                while (pos < s.Length && s[pos] is not (',' or '}' or ']' or ' ' or '\t' or '\r' or '\n')) pos++;
                string word = s[start..pos];
                bool ok = word is "true" or "false" or "null"
                    || double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                if (!ok) throw new FormatException($"Unexpected '{(word.Length > 12 ? word[..12] : word)}'");
                return new SpanNode { Kind = SpanKind.Other, Start = start, End = pos };
            }
        }
    }

    static SpanNode Object(string s, ref int pos, int depth)
    {
        var node = new SpanNode { Kind = SpanKind.Object, Start = pos++ };
        Skip(s, ref pos);
        if (pos < s.Length && s[pos] == '}')
        {
            node.End = ++pos;
            return node;
        }
        while (true)
        {
            Skip(s, ref pos);
            if (pos >= s.Length || s[pos] != '"') throw new FormatException("Expected a name");
            var member = new SpanMember { Start = pos };
            member.Key = String(s, ref pos);
            Skip(s, ref pos);
            if (pos >= s.Length || s[pos] != ':') throw new FormatException("Expected ':'");
            pos++;
            member.Value = Value(s, ref pos, depth + 1);
            node.Members.Add(member);
            Skip(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Ended early");
            if (s[pos] == ',')
            {
                pos++;
                continue;
            }
            if (s[pos] != '}') throw new FormatException("Expected ',' or '}'");
            node.End = ++pos;
            return node;
        }
    }

    static SpanNode Array(string s, ref int pos, int depth)
    {
        var node = new SpanNode { Kind = SpanKind.Array, Start = pos++ };
        Skip(s, ref pos);
        if (pos < s.Length && s[pos] == ']')
        {
            node.End = ++pos;
            return node;
        }
        while (true)
        {
            node.Items.Add(Value(s, ref pos, depth + 1));
            Skip(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Ended early");
            if (s[pos] == ',')
            {
                pos++;
                continue;
            }
            if (s[pos] != ']') throw new FormatException("Expected ',' or ']'");
            node.End = ++pos;
            return node;
        }
    }

    static string String(string s, ref int pos)
    {
        var sb = new StringBuilder();
        pos++;
        while (true)
        {
            if (pos >= s.Length) throw new FormatException("A string never ends");
            char c = s[pos++];
            if (c == '"') return sb.ToString();
            if (c < ' ') throw new FormatException("A control character inside a string");
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (pos >= s.Length) throw new FormatException("A string never ends");
            char e = s[pos++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (pos + 4 > s.Length || !ushort.TryParse(s.AsSpan(pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        throw new FormatException("A bad \\u escape");
                    sb.Append((char)code);
                    pos += 4;
                    break;
                default: throw new FormatException("A bad escape");
            }
        }
    }

    /// <summary>A string as JSON text, escaping only what JSON requires (so "&amp;&amp;" and accents stay readable).</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
