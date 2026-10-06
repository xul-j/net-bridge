// Minimal JSON for the bridge: no dependencies, so it builds on .NET Framework 4.x, Mono and .NET.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XulJ.Bridge
{
    public static class Json
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { WriteString(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is int || v is long || v is short || v is byte) { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, e.Value);
                }
                sb.Append('}');
                return;
            }
            if (v is IEnumerable list)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // Parses into Dictionary<string, object>, List<object>, string, double, bool or null.
        public static object Parse(string text)
        {
            int i = 0;
            var v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing characters");
            return v;
        }

        static void SkipWs(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }

        static object ParseValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) throw new FormatException("unexpected end");
            char c = t[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++;
                SkipWs(t, ref i);
                if (t[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs(t, ref i);
                    string key = ParseString(t, ref i);
                    SkipWs(t, ref i);
                    if (t[i++] != ':') throw new FormatException("expected ':'");
                    d[key] = ParseValue(t, ref i);
                    SkipWs(t, ref i);
                    if (t[i] == ',') { i++; continue; }
                    if (t[i] == '}') { i++; return d; }
                    throw new FormatException("expected ',' or '}'");
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                SkipWs(t, ref i);
                if (t[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(ParseValue(t, ref i));
                    SkipWs(t, ref i);
                    if (t[i] == ',') { i++; continue; }
                    if (t[i] == ']') { i++; return l; }
                    throw new FormatException("expected ',' or ']'");
                }
            }
            if (c == '"') return ParseString(t, ref i);
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (t.Length - i >= 5 && string.CompareOrdinal(t, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < t.Length && "+-0123456789.eE".IndexOf(t[i]) >= 0) i++;
            if (start == i) throw new FormatException("unexpected character");
            return double.Parse(t.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        static string ParseString(string t, ref int i)
        {
            if (t[i] != '"') throw new FormatException("expected string");
            i++;
            var sb = new StringBuilder();
            while (t[i] != '"')
            {
                char c = t[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = t[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(t.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }
    }
}
