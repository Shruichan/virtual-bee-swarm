using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// A small strict JSON reader for the editor tools (JsonUtility can't read top-level arrays or keyed objects).
    /// Objects keep their key order and reject duplicate keys. Values: Obj, List&lt;object&gt;, string, double, bool, null.
    /// </summary>
    static class MiniJson
    {
        public sealed class Obj
        {
            public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();

            public bool TryGet(string key, out object value)
            {
                foreach (var kv in Items)
                {
                    if (kv.Key != key) continue;
                    value = kv.Value;
                    return true;
                }
                value = null;
                return false;
            }

            public string Str(string key) => TryGet(key, out var v) ? v as string : null;
            public Obj O(string key) => TryGet(key, out var v) ? v as Obj : null;
            public List<object> A(string key) => TryGet(key, out var v) ? v as List<object> : null;
        }

        public static object Parse(string s)
        {
            int i = 0;
            if (s.Length > 0 && s[0] == '﻿') i = 1;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw Err(s, i, "unexpected text after the value");
            return v;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw Err(s, i, "unexpected end");
            char c = s[i];
            if (c == '{') return Object(s, ref i);
            if (c == '[') return Array(s, ref i);
            if (c == '"') return String(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            if (c == '-' || (c >= '0' && c <= '9')) return Number(s, ref i);
            throw Err(s, i, "unexpected '" + c + "'");
        }

        static Obj Object(string s, ref int i)
        {
            var o = new Obj();
            i++; // {
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                Ws(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Err(s, i, "expected a key");
                string key = String(s, ref i);
                if (o.TryGet(key, out _)) throw Err(s, i, "duplicate key \"" + key + "\"");
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Err(s, i, "expected ':'");
                i++;
                o.Items.Add(new KeyValuePair<string, object>(key, Value(s, ref i)));
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw Err(s, i, "expected ',' or '}'");
            }
        }

        static List<object> Array(string s, ref int i)
        {
            var a = new List<object>();
            i++; // [
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value(s, ref i));
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw Err(s, i, "expected ',' or ']'");
            }
        }

        static string String(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw Err(s, i - 1, "control character in a string");
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
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
                        if (i + 4 > s.Length) throw Err(s, i, "bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw Err(s, i - 1, "bad escape \\" + e);
                }
            }
            throw Err(s, i, "unterminated string");
        }

        static double Number(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw Err(s, start, "bad number");
            return d;
        }

        static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static FormatException Err(string s, int i, string what)
        {
            int line = 1, col = 1;
            for (int k = 0; k < i && k < s.Length; k++)
            {
                if (s[k] == '\n') { line++; col = 1; }
                else col++;
            }
            return new FormatException("JSON " + what + " at line " + line + ", column " + col);
        }
    }
}
