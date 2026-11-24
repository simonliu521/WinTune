using System;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace WinTune
{
    /// <summary>极简 JSON 构建器，避免额外依赖。</summary>
    public class JB
    {
        private readonly StringBuilder sb = new StringBuilder(256);
        private bool first = true;

        public JB() { sb.Append('{'); }

        public JB Add(string k, string v)
        {
            Sep();
            sb.Append(Json.Quote(k)).Append(':').Append(Json.Quote(v));
            return this;
        }

        public JB Add(string k, long v)
        {
            Sep();
            sb.Append(Json.Quote(k)).Append(':').Append(v.ToString());
            return this;
        }

        /// <summary>数字字段；NaN/Infinity 一律写成 null，避免前端解析失败。</summary>
        public JB Add(string k, double v)
        {
            Sep();
            sb.Append(Json.Quote(k)).Append(':');
            if (double.IsNaN(v) || double.IsInfinity(v)) sb.Append("null");
            else sb.Append(v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            return this;
        }

        public JB Add(string k, bool v)
        {
            Sep();
            sb.Append(Json.Quote(k)).Append(':').Append(v ? "true" : "false");
            return this;
        }

        public JB Null(string k)
        {
            Sep();
            sb.Append(Json.Quote(k)).Append(":null");
            return this;
        }

        /// <summary>value 必须是合法 JSON 片段（对象/数组/字面量）。</summary>
        public JB Raw(string k, string json)
        {
            Sep();
            sb.Append(Json.Quote(k)).Append(':').Append(json);
            return this;
        }

        private void Sep()
        {
            if (first) { first = false; } else { sb.Append(','); }
        }

        public string End()
        {
            sb.Append('}');
            return sb.ToString();
        }

        public override string ToString() { return End(); }
    }

    public static class Json
    {
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        static Json()
        {
            Ser.MaxJsonLength = int.MaxValue;
            Ser.RecursionLimit = 100;
        }

        public static Dictionary<string, object> Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return new Dictionary<string, object>();
            object o = Ser.DeserializeObject(s);
            if (o is Dictionary<string, object>) return (Dictionary<string, object>)o;
            return new Dictionary<string, object>();
        }

        public static string Quote(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder b = new StringBuilder(s.Length + 8);
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
            return b.ToString();
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return "";
            return d[key].ToString();
        }

        public static int Int(Dictionary<string, object> d, string key, int def)
        {
            try
            {
                if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
                return Convert.ToInt32(d[key]);
            }
            catch { return def; }
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return false;
            object o = d[key];
            if (o is bool) return (bool)o;
            string s = o.ToString().ToLowerInvariant();
            return s == "true" || s == "1";
        }

        public static List<string> StrList(Dictionary<string, object> d, string key)
        {
            List<string> result = new List<string>();
            if (d == null || !d.ContainsKey(key) || d[key] == null) return result;
            object o = d[key];
            if (!(o is System.Collections.ArrayList || o is object[])) return result;
            object[] arr = o is object[] ? (object[])o : ((System.Collections.ArrayList)o).ToArray();
            foreach (object x in arr)
            {
                if (x != null) result.Add(x.ToString());
            }
            return result;
        }
    }
}
