using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace AIMeter
{
    /// <summary>Indented JSON for files people read and edit, and for tool text that models read.</summary>
    internal static class JsonText
    {
        public static string Indented(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, int depth)
        {
            var map = value as IDictionary<string, object>;
            var list = value as IList;
            if (map != null)
            {
                if (map.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                int i = 0;
                foreach (var kv in map)
                {
                    sb.Append(i++ == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2).Append(Json.Serialize(kv.Key)).Append(": ");
                    Write(sb, kv.Value, depth + 1);
                }
                sb.Append('\n').Append(' ', depth * 2).Append('}');
            }
            else if (list != null && !(value is string))
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2);
                    Write(sb, list[i], depth + 1);
                }
                sb.Append('\n').Append(' ', depth * 2).Append(']');
            }
            else sb.Append(Json.Serialize(value));
        }
    }
}
