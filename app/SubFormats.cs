// Подписки в формате Clash (YAML) и JSON (настройки sing-box или Xray): каждый сервер превращается в обычную ссылку
// (vless://, vmess://, trojan://, ss://, hysteria2://, tuic://) и дальше идёт тем же путём, что ключи из обычной подписки
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ZapretLauncher
{
    static class SubFormats
    {
        // null — текст не похож ни на Clash, ни на JSON (или не читается); иначе ссылки по одной в строке.
        // Пустая строка — формат узнан, но знакомых лаунчеру серверов в нём нет
        public static string ToLinks(string text)
        {
            string t = (text ?? "").Trim().TrimStart('﻿');
            List<string> links;
            if (t.StartsWith("{") || t.StartsWith("["))
            {
                object root;
                try { root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(t); } catch { return null; }
                links = FromJson(root);
            }
            else if (Regex.IsMatch(t, @"(?m)^proxies\s*:"))
            {
                Dictionary<string, object> root;
                try { root = Yaml.Parse(t) as Dictionary<string, object>; } catch { return null; }
                if (root == null || !(Get(root, "proxies") is IList)) return null;
                links = Items(Get(root, "proxies")).OfType<Dictionary<string, object>>().Select(FromClash).Where(l => l != null).ToList();
            }
            else return null;
            return string.Join("\n", links);
        }

        // ---------- JSON: массив полных настроек Xray («JSON-подписка») или одна настройка sing-box / Xray ----------
        static List<string> FromJson(object root)
        {
            List<string> links = new List<string>();
            IEnumerable<object> configs = root is Dictionary<string, object> ? new[] { root } : Items(root);
            foreach (Dictionary<string, object> cfg in configs.OfType<Dictionary<string, object>>())
            {
                string remarks = Str(cfg, "remarks");
                foreach (Dictionary<string, object> o in Items(Get(cfg, "outbounds")).OfType<Dictionary<string, object>>())
                {
                    string link = o.ContainsKey("protocol") ? FromXray(o, remarks) : FromSingBox(o);
                    if (link != null) links.Add(link);
                }
            }
            return links;
        }

        static string FromSingBox(Dictionary<string, object> o)
        {
            string type = Str(o, "type"), name = Str(o, "tag"), host = Str(o, "server");
            object port = Get(o, "server_port");
            Dictionary<string, object> tls = Map(o, "tls"), tr = Map(o, "transport");
            bool tlsOn = tls != null && Bool(tls, "enabled");
            Dictionary<string, string> q = Q();
            if (type == "hysteria2" || type == "tuic")
            {
                q["sni"] = Str(tls, "server_name");
                if (Bool(tls, "insecure")) q["insecure"] = "1";
                if (type == "tuic")
                {
                    q["alpn"] = Join(Get(tls, "alpn"));
                    q["congestion_control"] = Str(o, "congestion_control");
                    return Link("tuic", Str(o, "uuid") + ":" + Str(o, "password"), host, port, name, q);
                }
                Dictionary<string, object> obfs = Map(o, "obfs");
                if (Str(obfs, "type") == "salamander") { q["obfs"] = "salamander"; q["obfs-password"] = Str(obfs, "password"); }
                return Link("hysteria2", Str(o, "password"), host, port, name, q);
            }
            if (type == "shadowsocks")
                return Get(o, "plugin") != null ? null : Link("ss", Str(o, "method") + ":" + Str(o, "password"), host, port, name, Q());
            if (tlsOn)
            {
                Dictionary<string, object> reality = Map(tls, "reality");
                bool r = reality != null && Bool(reality, "enabled");
                q["security"] = r ? "reality" : "tls";
                q["sni"] = Str(tls, "server_name");
                q["alpn"] = Join(Get(tls, "alpn"));
                q["fp"] = Str(Map(tls, "utls"), "fingerprint");
                if (Bool(tls, "insecure")) q["allowInsecure"] = "1";
                if (r) { q["pbk"] = Str(reality, "public_key"); q["sid"] = Str(reality, "short_id"); }
            }
            if (tr != null)
            {
                string tt = Str(tr, "type");
                q["type"] = tt;
                q["path"] = Str(tr, "path");
                q["host"] = tt == "ws" ? Str(Map(tr, "headers"), "Host") : Join(Get(tr, "host"));
                q["serviceName"] = Str(tr, "service_name");
            }
            if (type == "vless") { q["flow"] = Str(o, "flow"); return Link("vless", Str(o, "uuid"), host, port, name, q); }
            if (type == "anytls")
            {
                if (Bool(tls, "insecure")) q["insecure"] = "1";
                return Link(type, Str(o, "password"), host, port, name, q);
            }
            if (type == "trojan") return Link(type, Str(o, "password"), host, port, name, q);
            if (type == "vmess") return Vmess(name, host, port, Str(o, "uuid"), Str(o, "alter_id"), Str(o, "security"), q);
            return null;   // selector, urltest, direct, block, dns — служебные
        }

        static string FromXray(Dictionary<string, object> o, string remarks)
        {
            string proto = Str(o, "protocol"), name = remarks.Length > 0 ? remarks : Str(o, "tag");
            Dictionary<string, object> set = Map(o, "settings"), st = Map(o, "streamSettings");
            Dictionary<string, string> q = Q();
            string net = Str(st, "network"), sec = Str(st, "security");
            q["type"] = net.Length > 0 ? net : "tcp";
            q["security"] = sec == "none" ? "" : sec;
            Dictionary<string, object> tls = Map(st, sec == "reality" ? "realitySettings" : "tlsSettings");
            q["sni"] = Str(tls, "serverName");
            q["fp"] = Str(tls, "fingerprint");
            q["alpn"] = Join(Get(tls, "alpn"));
            q["pbk"] = Str(tls, "publicKey");
            q["sid"] = Str(tls, "shortId");
            q["spx"] = Str(tls, "spiderX");
            if (Bool(tls, "allowInsecure")) q["allowInsecure"] = "1";
            Dictionary<string, object> xh = Map(st, "xhttpSettings") ?? Map(st, "splithttpSettings");
            Dictionary<string, object> t = Map(st, "wsSettings") ?? Map(st, "httpupgradeSettings") ?? xh;
            q["path"] = Str(t, "path");
            q["host"] = Str(t, "host").Length > 0 ? Str(t, "host") : Str(Map(t, "headers"), "Host");
            q["serviceName"] = Str(Map(st, "grpcSettings"), "serviceName");
            q["mode"] = Str(xh, "mode");
            if (proto == "vless" || proto == "vmess")
            {
                Dictionary<string, object> vnext = Items(Get(set, "vnext")).OfType<Dictionary<string, object>>().FirstOrDefault();
                Dictionary<string, object> user = Items(Get(vnext, "users")).OfType<Dictionary<string, object>>().FirstOrDefault();
                if (vnext == null || user == null) return null;
                if (proto == "vmess") return Vmess(name, Str(vnext, "address"), Get(vnext, "port"), Str(user, "id"), Str(user, "alterId"), Str(user, "security"), q);
                q["flow"] = Str(user, "flow");
                return Link("vless", Str(user, "id"), Str(vnext, "address"), Get(vnext, "port"), name, q);
            }
            if (proto == "trojan" || proto == "shadowsocks")
            {
                Dictionary<string, object> s = Items(Get(set, "servers")).OfType<Dictionary<string, object>>().FirstOrDefault();
                if (s == null) return null;
                return proto == "trojan" ? Link("trojan", Str(s, "password"), Str(s, "address"), Get(s, "port"), name, q)
                                         : Link("ss", Str(s, "method") + ":" + Str(s, "password"), Str(s, "address"), Get(s, "port"), name, Q());
            }
            return null;   // freedom, blackhole, dns — служебные
        }

        // ---------- Clash: список proxies ----------
        static string FromClash(Dictionary<string, object> p)
        {
            string type = Str(p, "type").ToLowerInvariant(), name = Str(p, "name"), host = Str(p, "server");
            object port = Get(p, "port");
            Dictionary<string, string> q = Q();
            if (type == "anytls")
            {
                q["sni"] = Str(p, "sni");
                if (Bool(p, "skip-cert-verify")) q["insecure"] = "1";
                return Link(type, Str(p, "password"), host, port, name, q);
            }
            if (type == "vless" || type == "vmess" || type == "trojan")
            {
                Dictionary<string, object> ws = Map(p, "ws-opts"), reality = Map(p, "reality-opts");
                string net = Str(p, "network");
                q["type"] = net.Length == 0 ? "tcp" : net == "ws" && Bool(ws, "v2ray-http-upgrade") ? "httpupgrade" : net;
                q["path"] = Str(ws, "path");
                q["host"] = Str(Map(ws, "headers"), "Host");
                q["serviceName"] = Str(Map(p, "grpc-opts"), "grpc-service-name");
                q["security"] = reality != null ? "reality" : type == "trojan" || Bool(p, "tls") ? "tls" : "";
                q["sni"] = Str(p, "servername").Length > 0 ? Str(p, "servername") : Str(p, "sni");
                q["fp"] = Str(p, "client-fingerprint");
                q["alpn"] = Join(Get(p, "alpn"));
                q["pbk"] = Str(reality, "public-key");
                q["sid"] = Str(reality, "short-id");
                q["flow"] = Str(p, "flow");
                if (Bool(p, "skip-cert-verify")) q["allowInsecure"] = "1";
                if (type == "vmess") return Vmess(name, host, port, Str(p, "uuid"), Str(p, "alterId"), Str(p, "cipher"), q);
                return Link(type, type == "trojan" ? Str(p, "password") : Str(p, "uuid"), host, port, name, q);
            }
            if (type == "ss")   // с плагином (obfs, v2ray-plugin) лаунчер подключиться не сможет — такой сервер не берём
                return Get(p, "plugin") != null ? null : Link("ss", Str(p, "cipher") + ":" + Str(p, "password"), host, port, name, q);
            if (type == "hysteria2" || type == "tuic")
            {
                q["sni"] = Str(p, "sni");
                if (Bool(p, "skip-cert-verify")) q["insecure"] = "1";
                if (type == "tuic")
                {
                    q["alpn"] = Join(Get(p, "alpn"));
                    q["congestion_control"] = Str(p, "congestion-controller");
                    return Link("tuic", Str(p, "uuid") + ":" + Str(p, "password"), host, port, name, q);
                }
                if (Str(p, "obfs") == "salamander") { q["obfs"] = "salamander"; q["obfs-password"] = Str(p, "obfs-password"); }
                return Link("hysteria2", Str(p, "password"), host, port, name, q);
            }
            return null;
        }

        // ---------- Сборка ссылок ----------
        static string Link(string scheme, string user, string host, object port, string name, Dictionary<string, string> q)
        {
            int p;
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || user.Trim(':').Length == 0) return null;
            if (!int.TryParse(Convert.ToString(port, CultureInfo.InvariantCulture), out p) || p <= 0 || p > 65535) return null;
            string query = string.Join("&", q.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
            return scheme + "://" + Uri.EscapeDataString(user) + "@" + (host.Contains(":") ? "[" + host + "]" : host) + ":" + p +
                   (query.Length > 0 ? "?" + query : "") + "#" + Uri.EscapeDataString(name.Length > 0 ? name : host);
        }

        static string Vmess(string name, string host, object port, string id, string aid, string cipher, Dictionary<string, string> q)
        {
            int p;
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(id)) return null;
            if (!int.TryParse(Convert.ToString(port, CultureInfo.InvariantCulture), out p) || p <= 0 || p > 65535) return null;
            string net = V(q, "type");
            Dictionary<string, object> j = new Dictionary<string, object>
            {
                { "v", "2" }, { "ps", name.Length > 0 ? name : host }, { "add", host }, { "port", p.ToString() }, { "id", id },
                { "aid", aid.Length > 0 ? aid : "0" }, { "scy", cipher.Length > 0 ? cipher : "auto" }, { "net", net.Length > 0 ? net : "tcp" }, { "type", "none" },
                { "host", V(q, "host") }, { "path", net == "grpc" ? V(q, "serviceName") : V(q, "path") }, { "tls", V(q, "security") == "tls" ? "tls" : "" },
                { "sni", V(q, "sni") }, { "fp", V(q, "fp") }, { "alpn", V(q, "alpn") },
                { "allowInsecure", V(q, "allowInsecure") },
            };
            return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(j)));
        }

        // ---------- Разбор словарей ----------
        static Dictionary<string, string> Q() { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        static string V(Dictionary<string, string> q, string k) { string v; return q.TryGetValue(k, out v) && v != null ? v : ""; }
        static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        static Dictionary<string, object> Map(Dictionary<string, object> d, string k) { return Get(d, k) as Dictionary<string, object>; }
        static IEnumerable<object> Items(object o) { IList l = o as IList; return l == null ? Enumerable.Empty<object>() : l.Cast<object>(); }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            return v == null || v is IDictionary || v is IList ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static bool Bool(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            return v is bool ? (bool)v : string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase);
        }

        // alpn бывает и списком, и строкой через запятую
        static string Join(object v)
        {
            if (v is IList) return string.Join(",", Items(v).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
            return v == null || v is IDictionary ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }

    // Крохотный разбор YAML — ровно то, что бывает в подписках Clash: вложенные словари и списки отступами,
    // словари и списки в скобках, строки в кавычках и без. Многострочные значения (| и >) пропускаются
    static class Yaml
    {
        class Line { public int Indent; public string Text; }

        public static object Parse(string text)
        {
            List<Line> lines = new List<Line>();
            foreach (string raw in text.Replace("\r", "").Replace('\t', ' ').Split('\n'))
            {
                string s = StripComment(raw).TrimEnd();
                if (s.Trim().Length == 0 || s == "---" || s == "...") continue;
                lines.Add(new Line { Indent = s.Length - s.TrimStart(' ').Length, Text = s.Trim() });
            }
            int i = 0;
            return lines.Count == 0 ? null : Block(lines, ref i, lines[0].Indent);
        }

        static bool IsItem(string s) { return s == "-" || s.StartsWith("- "); }

        static object Block(List<Line> lines, ref int i, int indent)
        {
            return IsItem(lines[i].Text) ? (object)ParseList(lines, ref i, indent) : ParseMap(lines, ref i, indent);
        }

        static List<object> ParseList(List<Line> lines, ref int i, int indent)
        {
            List<object> list = new List<object>();
            while (i < lines.Count && lines[i].Indent >= indent)
            {
                if (lines[i].Indent > indent || !IsItem(lines[i].Text)) { i++; continue; }   // строка не на месте — пропускаем
                string rest = lines[i].Text.Substring(1).TrimStart();
                if (rest.Length == 0)
                {
                    i++;
                    list.Add(i < lines.Count && lines[i].Indent > indent ? Block(lines, ref i, lines[i].Indent) : null);
                }
                else if (rest[0] == '{' || rest[0] == '[' || ColonAt(rest) < 0) { list.Add(Value(rest)); i++; }
                else
                {
                    // «- key: value» — словарь; следующие его ключи стоят под первым
                    int at = indent + lines[i].Text.Length - rest.Length;
                    lines[i] = new Line { Indent = at, Text = rest };
                    list.Add(ParseMap(lines, ref i, at));
                }
            }
            return list;
        }

        static Dictionary<string, object> ParseMap(List<Line> lines, ref int i, int indent)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            while (i < lines.Count && lines[i].Indent >= indent)
            {
                string text = lines[i].Text;
                if (lines[i].Indent == indent && IsItem(text)) break;
                int colon = ColonAt(text);
                if (lines[i].Indent > indent || colon < 0) { i++; continue; }
                string key = Convert.ToString(Scalar(text.Substring(0, colon)));
                string rest = text.Substring(colon + 1).Trim();
                i++;
                bool nested = i < lines.Count && (lines[i].Indent > indent || (lines[i].Indent == indent && IsItem(lines[i].Text)));
                map[key] = rest.Length > 0 ? Value(rest) : nested ? Block(lines, ref i, lines[i].Indent) : null;
            }
            return map;
        }

        // «: » вне кавычек — граница ключа; у строки, начатой скобкой, ключа нет
        static int ColonAt(string s)
        {
            char quote = '\0';
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if ((c == '"' || c == '\'') && k == 0) { quote = c; continue; }
                if (k == 0 && (c == '{' || c == '[')) return -1;
                if (c == ':' && (k + 1 == s.Length || s[k + 1] == ' ')) return k;
            }
            return -1;
        }

        static string StripComment(string s)
        {
            char quote = '\0';
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if ((c == '"' || c == '\'') && (k == 0 || " :,[{".IndexOf(s[k - 1]) >= 0)) { quote = c; continue; }
                if (c == '#' && (k == 0 || s[k - 1] == ' ')) return s.Substring(0, k);
            }
            return s;
        }

        static object Value(string s)
        {
            s = s.Trim();
            if (s.StartsWith("{") || s.StartsWith("[")) { int k = 0; return Flow(s, ref k); }
            return Scalar(s);
        }

        static object Scalar(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2).Replace("\\\\", "\u0001").Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\u0001", "\\");
            if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'') return s.Substring(1, s.Length - 2).Replace("''", "'");
            if (s == "true" || s == "True") return true;
            if (s == "false" || s == "False") return false;
            if (s == "~" || s == "null" || s.Length == 0) return null;
            return s;
        }

        // {a: 1, b: [x, y]} и [..] в одну строку
        static object Flow(string s, ref int k)
        {
            Skip(s, ref k);
            if (k < s.Length && (s[k] == '{' || s[k] == '['))
            {
                bool isMap = s[k] == '{';
                char close = isMap ? '}' : ']';
                Dictionary<string, object> map = new Dictionary<string, object>();
                List<object> list = new List<object>();
                k++;
                while (true)
                {
                    Skip(s, ref k);
                    if (k >= s.Length) break;
                    if (s[k] == close) { k++; break; }
                    if (s[k] == ',') { k++; continue; }
                    if (isMap)
                    {
                        string key = Convert.ToString(Scalar(Token(s, ref k, true)));
                        Skip(s, ref k);
                        if (k < s.Length && s[k] == ':') k++;
                        Skip(s, ref k);
                        map[key] = k < s.Length && (s[k] == ',' || s[k] == '}') ? null : Flow(s, ref k);
                    }
                    else list.Add(Flow(s, ref k));
                }
                return isMap ? (object)map : list;
            }
            return Scalar(Token(s, ref k, false));
        }

        // Строка до разделителя: в кавычках — целиком (с кавычками), без кавычек — до «,», «}», «]», у ключа — ещё до «:»
        static string Token(string s, ref int k, bool key)
        {
            Skip(s, ref k);
            int start = k;
            if (k < s.Length && (s[k] == '"' || s[k] == '\''))
            {
                char quote = s[k++];
                while (k < s.Length)
                {
                    if (quote == '"' && s[k] == '\\') { k += 2; continue; }
                    if (s[k] == quote) { if (quote == '\'' && k + 1 < s.Length && s[k + 1] == '\'') { k += 2; continue; } break; }
                    k++;
                }
                k = Math.Min(k + 1, s.Length);
                return s.Substring(start, k - start);
            }
            while (k < s.Length && s[k] != ',' && s[k] != '}' && s[k] != ']' && !(key && s[k] == ':')) k++;
            return s.Substring(start, k - start).Trim();
        }

        static void Skip(string s, ref int k) { while (k < s.Length && s[k] == ' ') k++; }
    }
}
