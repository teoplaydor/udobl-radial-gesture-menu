using System.Text;
using System.Web.Script.Serialization;

namespace Udobl.Core
{
    /// <summary>
    /// Thin wrapper over the framework's JavaScriptSerializer (System.Web.Extensions,
    /// already in the GAC) plus a quote-aware pretty-printer so config files stay
    /// hand-editable. No external dependencies.
    /// </summary>
    public static class Json
    {
        public static string Serialize(object value)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return Indent(ser.Serialize(value));
        }

        public static T Deserialize<T>(string json)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return ser.Deserialize<T>(json);
        }

        /// <summary>Pretty-prints compact JSON. String contents are left untouched.</summary>
        private static string Indent(string json)
        {
            var sb = new StringBuilder(json.Length + json.Length / 4);
            int depth = 0;
            bool inString = false;
            bool escape = false;
            const string pad = "  ";

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    sb.Append(c);
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        // Don't add a newline for empty containers.
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                        {
                            sb.Append(json[++i]);
                        }
                        else
                        {
                            depth++;
                            NewLine(sb, depth, pad);
                        }
                        break;
                    case '}':
                    case ']':
                        depth--;
                        NewLine(sb, depth, pad);
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        NewLine(sb, depth, pad);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, int depth, string pad)
        {
            sb.Append('\n');
            for (int i = 0; i < depth; i++) sb.Append(pad);
        }
    }
}
