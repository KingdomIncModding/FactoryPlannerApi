using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace KingdomFactoryApi
{
    // Only internal, already-serialized game/catalog data may bypass encoding.
    public sealed class RawJson
    {
        public readonly string Text;
        public RawJson(string text) { Text = text ?? "null"; }
    }

    public static class Json
    {
        public static Dictionary<string, object> Obj(params object[] pairs)
        {
            if (pairs.Length % 2 != 0) throw new ArgumentException("Expected key/value pairs");
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
        public static string Encode(object value)
        {
            var output = new StringBuilder(); Write(output, value, 0); return output.ToString();
        }
        static void Write(StringBuilder output, object value, int depth)
        {
            if (depth > 128) throw new ArgumentException("JSON depth exceeded");
            if (value == null) { output.Append("null"); return; }
            if (value is RawJson raw) { output.Append(raw.Text); return; }
            if (value is string text) { Quote(output, text); return; }
            if (value is bool flag) { output.Append(flag ? "true" : "false"); return; }
            if (value is IDictionary<string, object> map)
            {
                output.Append('{'); bool first = true;
                foreach (string key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) output.Append(','); first = false;
                    Quote(output, key); output.Append(':'); Write(output, map[key], depth + 1);
                }
                output.Append('}'); return;
            }
            if (value is IEnumerable sequence)
            {
                output.Append('['); bool first = true;
                foreach (object item in sequence)
                {
                    if (!first) output.Append(','); first = false; Write(output, item, depth + 1);
                }
                output.Append(']'); return;
            }
            if (value is float || value is double)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) { output.Append("null"); return; }
                output.Append(number.ToString("R", CultureInfo.InvariantCulture)); return;
            }
            if (value is byte || value is short || value is int || value is long || value is uint || value is ulong || value is decimal)
            { output.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            throw new ArgumentException("Unsupported JSON type: " + value.GetType().Name);
        }
        static void Quote(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (c < 32 || char.IsSurrogate(c) || c == '\u2028' || c == '\u2029')
                            output.Append("\\u").Append(((int)c).ToString("x4"));
                        else output.Append(c);
                        break;
                }
            }
            output.Append('"');
        }
    }
}
