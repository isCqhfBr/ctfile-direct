using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CtfileDownloader
{
    /// <summary>CDN 软限流（HTTP 429）。区别于硬错误：等一会儿通常能恢复，应当折进退避而非立刻甩手。</summary>
    internal sealed class ThrottleException : Exception
    {
        public ThrottleException() : base("下载被城通限流（HTTP 429），等几分钟再试") { }
    }

    public class Source
    {
        public string Url { get; set; }
        public int Threads { get; set; }
        public string Label { get; set; }
        public string Host { get; set; }
        public string Kind { get; set; }   // account / anonymous
        public int Spd { get; set; }        // 单连接限速（字节/秒），服务端下发；0 = 未知

        // 续取一条新直链。直链带 ctt 有效期（约 6 小时），长任务跑过 ctt 后旧链会开始 503，
        // 重试时不该拿同一条死链硬撞。匿名源取不到账号信息，Refetch 留 null；账号源由
        // ResolveAsync 注入。
        public Func<CancellationToken, Task<Source>> Refetch { get; set; }
    }

    public class Resolved
    {
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public List<Source> Sources { get; set; } = new List<Source>();
        public List<string> Warnings { get; set; } = new List<string>();
        public int TotalThreads { get { int n = 0; foreach (var s in Sources) n += s.Threads; return n; } }
    }

    public class Account
    {
        public string email { get; set; }
        public string password { get; set; }
    }

    public class AppConfig
    {
        public bool useAnonymous { get; set; } = true;
        public List<Account> accounts { get; set; } = new List<Account>();
        public string outDir { get; set; }   // 记住上次用的保存目录
    }

    public static class Ctfile
    {
        // 退避抖动用一个即可：.NET 的 Random 线程安全，new Random() 每请求造一个更糟。
        static readonly System.Random rnd = new System.Random();

        public const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        const string OkHttp = "okhttp/4.9.2";
        const string WebApi = "https://webapi.ctfile.com";
        const string Rest = "https://rest.ctfile.com";
        const string Api = "https://api.ctfile.com";

        static HttpClient NewClient(string ua = null)
        {
            // 不走系统代理：城通是境内站点，直连更稳（代理挂了也能用）
            var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua ?? Ua);
            return c;
        }

        public class Parsed
        {
            public string Code, Passcode, PageUrl;
            public char Kind = 'f';    // f = 单文件，d = 文件夹
        }

        public static Parsed Parse(string input)
        {
            var s = (input ?? "").Trim();
            if (s.Length == 0) throw new Exception("请先粘贴城通分享链接");
            var rx = new System.Text.RegularExpressions.Regex(@"/([fd]l?)/([0-9]+-[0-9]+-[0-9a-fA-F]+)");
            var rx2 = new System.Text.RegularExpressions.Regex(@"^([0-9]+-[0-9]+-[0-9a-fA-F]+)$");

            if (s.StartsWith("http://") || s.StartsWith("https://"))
            {
                Uri u;
                if (!Uri.TryCreate(s, UriKind.Absolute, out u)) throw new Exception("链接格式不对，请检查是否复制完整");
                var m = rx.Match(u.AbsolutePath);
                if (!m.Success) throw new Exception("看不懂这个链接，需要形如 https://url91.ctfile.com/f/37476991-1447877437-0eb71b 的分享链接");
                string pc = "";
                var q = u.Query;
                var mp = System.Text.RegularExpressions.Regex.Match(q, @"[?&](?:p|passcode)=([^&]*)");
                if (mp.Success) pc = Uri.UnescapeDataString(mp.Groups[1].Value);
                return new Parsed { Code = m.Groups[2].Value, Passcode = pc, PageUrl = s, Kind = m.Groups[1].Value[0] };
            }
            var m2 = rx2.Match(s);
            if (!m2.Success) throw new Exception("看不懂这个分享码，应为 37476991-1447877437-0eb71b 这种格式");
            return new Parsed { Code = m2.Groups[1].Value, Passcode = "", PageUrl = "https://url91.ctfile.com/f/" + m2.Groups[1].Value, Kind = 'f' };
        }

        // 城通会按接口主机限流（短时间请求太多就返 429）。这里统一做退避重试，
        // 重试仍失败就给一句人话，而不是把 HTTP 状态码糊到用户脸上。
        static Task<JsonElement> SendJson(HttpClient c, Func<HttpRequestMessage> makeReq, string what, Action<string> log = null)
            => SendJson(c, makeReq, what, log, CancellationToken.None);

        static async Task<JsonElement> SendJson(HttpClient c, Func<HttpRequestMessage> makeReq, string what, Action<string> log, CancellationToken ct)
        {
            // 429 = 限流，503/504 = 城通服务端偶发网关错误（实测深层目录请求常发），都重试
            int[] waits = { 2000, 5000, 10000 };
            for (int attempt = 0; ; attempt++)
            {
                using (var req = makeReq())
                using (var res = await c.SendAsync(req, ct).ConfigureAwait(false))
                {
                    int code = (int)res.StatusCode;
                    var txt = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (code == 429 || code == 503 || code == 504)
                    {
                        if (attempt < waits.Length)
                        {
                            if (log != null) log($"服务端返回 {code}，{waits[attempt] / 1000} 秒后重试…");
                            await Task.Delay(waits[attempt], ct).ConfigureAwait(false);
                            continue;
                        }
                        if (code == 429)
                            throw new Exception(
                                "城通把这个 IP 限流了（HTTP 429，请求过于频繁）。\n\n" +
                                "这不是链接失效，等 10~30 分钟再试就行。\n" +
                                "如果是刚批量解析过很多链接，属于正常现象。");
                        throw new Exception(
                            $"城通服务端持续返回 {code}（重试 {waits.Length} 次仍失败）。\n" +
                            "通常是对方网关繁忙，稍后再试。");
                    }
                    if (code == 403 && string.IsNullOrWhiteSpace(txt))
                        throw new Exception("城通拒绝了请求（HTTP 403）。通常是 IP 被临时限制，稍后再试。");
                    if (string.IsNullOrWhiteSpace(txt))
                        throw new Exception($"{what} 返回了空响应（HTTP {code}），可能是被限流，稍后再试。");
                    try { return JsonDocument.Parse(txt).RootElement.Clone(); }
                    catch
                    {
                        throw new Exception($"{what} 返回了非 JSON 内容（HTTP {code}）。\n内容开头：{txt.Substring(0, Math.Min(80, txt.Length))}");
                    }
                }
            }
        }

        static Task<JsonElement> GetJson(HttpClient c, string url, string referer = null, Action<string> log = null)
        {
            return SendJson(c, () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (referer != null)
                {
                    req.Headers.TryAddWithoutValidation("Referer", referer);
                    req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                }
                return req;
            }, "取文件信息接口", log, CancellationToken.None);
        }

        static Task<JsonElement> GetJson(HttpClient c, string url, string referer, Action<string> log, CancellationToken ct)
        {
            return SendJson(c, () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (referer != null)
                {
                    req.Headers.TryAddWithoutValidation("Referer", referer);
                    req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                }
                return req;
            }, "取文件信息接口", log, ct);
        }

        static Task<JsonElement> PostJson(HttpClient c, string url, string json, string referer = null, string bearer = null, Action<string> log = null)
            => PostJson(c, url, json, referer, bearer, log, CancellationToken.None);

        static Task<JsonElement> PostJson(HttpClient c, string url, string json, string referer, string bearer, Action<string> log, CancellationToken ct)
        {
            return SendJson(c, () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                if (referer != null) req.Headers.TryAddWithoutValidation("Origin", "https://my.ctfile.com");
                req.Headers.TryAddWithoutValidation("Referer", referer ?? "https://my.ctfile.com/");
                if (bearer != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                return req;
            }, "城通接口", log, ct);
        }

        public static async Task<Resolved> ResolveAsync(string input, AppConfig cfg, Action<string> log = null)
        {
            void L(string s) { if (log != null) log(s); }
            var p = Parse(input);
            var http = NewClient();

            // 1) 元数据（匿名，顺便拿 xtredirect）
            var metaUrl = WebApi + "/getfile.php?path=f&f=" + Uri.EscapeDataString(p.Code)
                        + "&passcode=" + Uri.EscapeDataString(p.Passcode) + "&r=" + new Random().NextDouble()
                        + "&ref=&url=" + Uri.EscapeDataString(p.PageUrl);
            var metaRoot = await GetJson(http, metaUrl, p.PageUrl, L).ConfigureAwait(false);
            int code = metaRoot.TryGetProperty("code", out var ce) ? ce.GetInt32() : 0;
            if (code == 423) throw new Exception("这个分享需要提取码，请在链接后面加 ?p=提取码");
            if (code != 200 || !metaRoot.TryGetProperty("file", out var fileEl))
                throw new Exception("取文件信息失败：" + (metaRoot.TryGetProperty("message", out var me) ? me.GetString() : "code=" + code));

            var result = new Resolved();
            result.FileName = fileEl.TryGetProperty("file_name", out var fn) ? fn.GetString() : "ctfile-download.bin";
            string xtredirect = fileEl.TryGetProperty("xtredirect", out var xt) ? xt.GetString() : null;

            // 2) 每个账号各取一条直链
            if (cfg.accounts != null && cfg.accounts.Count > 0 && !string.IsNullOrEmpty(xtredirect))
            {
                string xtlink = "ctfile://" + xtredirect;
                string listJson = null;

                // 续取一条直链：登录 → file/list → fetch_url。同一账号重取会拿到同一条
                // 直链（账号接口目前只发一条），所以价值不在"换一条更快的"，而在于
                // 旧链的 ctt 过期后能拿到一条新的、把 ctt 重新延后 360 分钟。
                Func<CancellationToken, Task<Source>> makeRefetch(Account acct)
                {
                    return async (ct) =>
                    {
                        var lg2 = await PostJson(http, Api + "/v4/user/auth/login",
                            JsonSerializer.Serialize(new { email = acct.email, password = acct.password }), null, null, L, ct).ConfigureAwait(false);
                        int lc2 = lg2.TryGetProperty("code", out var lce2) ? lce2.GetInt32() : 0;
                        if (lc2 != 200 || !lg2.TryGetProperty("data", out var d2) || !d2.TryGetProperty("token", out var tk2))
                            return null;
                        string tok2 = tk2.GetString();
                        if (listJson == null) return null;
                        var l2 = JsonDocument.Parse(listJson).RootElement;
                        string fk = null;
                        foreach (var it in l2.GetProperty("results").EnumerateArray())
                        {
                            if ((it.TryGetProperty("icon", out var ic2) ? ic2.GetString() : "") == "folder") continue;
                            fk = it.GetProperty("key").GetString(); break;
                        }
                        if (fk == null) return null;
                        var d3 = await PostJson(http, Rest + "/p2/browser/file/fetch_url",
                            JsonSerializer.Serialize(new { xtlink, file_id = fk, token = tok2 }), null, null, L, ct).ConfigureAwait(false);
                        string u2 = d3.TryGetProperty("download_url", out var du2) ? du2.GetString() : null;
                        if (string.IsNullOrEmpty(u2)) return null;
                        return new Source { Url = u2, Threads = 1, Label = acct.email, Kind = "account", Host = SafeHost(u2), Spd = ReadSpd(u2) };
                    };
                }

                foreach (var acc in cfg.accounts)
                {
                    try
                    {
                        var lg = await PostJson(http, Api + "/v4/user/auth/login",
                            JsonSerializer.Serialize(new { email = acc.email, password = acc.password }), null, null, L).ConfigureAwait(false);
                        int lc = lg.TryGetProperty("code", out var lce) ? lce.GetInt32() : 0;
                        if (lc != 200 || !lg.TryGetProperty("data", out var dataEl) || !dataEl.TryGetProperty("token", out var tkEl))
                        {
                            var m = lg.TryGetProperty("message", out var mme) ? mme.GetString() : "code=" + lc;
                            result.Warnings.Add(acc.email + " 登录失败：" + m);
                            continue;
                        }
                        string token = tkEl.GetString();

                        if (listJson == null)
                        {
                            var lr = await PostJson(http, Rest + "/p2/browser/file/list",
                                JsonSerializer.Serialize(new { xtlink, token, reload = false }), null, null, L).ConfigureAwait(false);
                            int rc = lr.TryGetProperty("code", out var rce) ? rce.GetInt32() : 0;
                            if (rc != 200 || !lr.TryGetProperty("results", out _))
                            {
                                result.Warnings.Add("客户端接口返回 " + rc + "：" + (lr.TryGetProperty("message", out var rm) ? rm.GetString() : ""));
                                break;
                            }
                            listJson = lr.GetRawText();
                        }
                        var list = JsonDocument.Parse(listJson).RootElement;
                        string fileKey = null; string itemName = null; long itemSize = 0;
                        foreach (var it in list.GetProperty("results").EnumerateArray())
                        {
                            var icon = it.TryGetProperty("icon", out var ic) ? ic.GetString() : "";
                            if (icon == "folder") continue;
                            fileKey = it.GetProperty("key").GetString();
                            itemName = it.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                            itemSize = it.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                            break;
                        }
                        if (fileKey == null) { result.Warnings.Add("分享里没有可下载的文件"); break; }

                        var dl = await PostJson(http, Rest + "/p2/browser/file/fetch_url",
                            JsonSerializer.Serialize(new { xtlink, file_id = fileKey, token }), null, null, L).ConfigureAwait(false);
                        string url = dl.TryGetProperty("download_url", out var du) ? du.GetString() : null;
                        if (!string.IsNullOrEmpty(url))
                        {
                            int th = 2;
                            try { var q2 = new Uri(url).Query; var mt = System.Text.RegularExpressions.Regex.Match(q2, @"[?&]limit=(\d+)"); if (mt.Success) th = int.Parse(mt.Groups[1].Value); } catch { }
                            result.Sources.Add(new Source
                            {
                                Url = url, Threads = th, Label = acc.email, Kind = "account",
                                Host = SafeHost(url), Spd = ReadSpd(url),
                                Refetch = makeRefetch(acc),
                            });
                            if (!string.IsNullOrEmpty(itemName)) result.FileName = itemName;
                            if (itemSize > 0) result.FileSize = itemSize;
                            L(acc.email + " → " + th + " 线程");
                        }
                        else
                        {
                            result.Warnings.Add(acc.email + " 取直链失败：" + (dl.TryGetProperty("message", out var dm) ? dm.GetString() : "code=" + (dl.TryGetProperty("code", out var dc) ? dc.GetInt32() : 0)));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add(acc.email + "：" + ex.Message);
                    }
                }
            }

            // 3) 匿名槽
            if (cfg.useAnonymous)
            {
                try
                {
                    string q = "uid=" + Str(fileEl, "userid") + "&fid=" + Str(fileEl, "file_id")
                             + "&file_chk=" + Str(fileEl, "file_chk") + "&start_time=" + Str(fileEl, "start_time")
                             + "&wait_seconds=" + Str(fileEl, "wait_seconds") + "&rd=" + new Random().NextDouble();
                    var du = await GetJson(http, WebApi + "/get_down_url.php?" + q, p.PageUrl, L).ConfigureAwait(false);
                    if (du.TryGetProperty("downurl", out var durl) && !string.IsNullOrEmpty(durl.GetString()))
                    {
                        string u = durl.GetString();
                        int th = 1;
                        try { var mt = System.Text.RegularExpressions.Regex.Match(new Uri(u).Query, @"[?&]limit=(\d+)"); if (mt.Success) th = int.Parse(mt.Groups[1].Value); } catch { }
                        result.Sources.Add(new Source { Url = u, Threads = th, Label = "匿名", Kind = "anonymous", Host = SafeHost(u), Spd = ReadSpd(u) });
                        if (result.FileSize == 0 && du.TryGetProperty("file_size", out var fs) && fs.ValueKind == JsonValueKind.Number) result.FileSize = fs.GetInt64();
                        L("匿名 → " + th + " 线程");
                    }
                }
                catch (Exception ex) { result.Warnings.Add("匿名直链获取失败：" + ex.Message); }
            }

            if (result.Sources.Count == 0)
                throw new Exception(result.Warnings.Count > 0 ? string.Join("；", result.Warnings) : "没有拿到任何可用直链");
            return result;
        }

        static string Str(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return "0";
            return v.ValueKind == JsonValueKind.Number ? v.GetRawText() : (v.GetString() ?? "0");
        }

        static string SafeHost(string url)
        {
            try { return new Uri(url).Host; } catch { return "?"; }
        }

        /// <summary>
        /// 从直链的 ctt 参数读过期时间（Unix 秒）。DateTime.MinValue = 读不到。
        /// 实测 ctt 就是精确的失效时刻（当前时间 + 6 小时）。
        /// </summary>
        public static DateTime ReadExpiry(string url)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(new Uri(url).Query, @"[?&]ctt=(\d+)");
                if (m.Success)
                {
                    long ts = long.Parse(m.Groups[1].Value);
                    if (ts > 1700000000 && ts < 4102444800)   // 2023~2100 合理性检查
                        return DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        /// <summary>取一批直链里最早的过期时间</summary>
        public static DateTime EarliestExpiry(IEnumerable<Source> sources)
        {
            DateTime earliest = DateTime.MaxValue;
            foreach (var s in sources)
            {
                if (string.IsNullOrEmpty(s.Url)) continue;
                var e = ReadExpiry(s.Url);
                if (e != DateTime.MinValue && e < earliest) earliest = e;
            }
            return earliest == DateTime.MaxValue ? DateTime.MinValue : earliest;
        }

        /// <summary>从直链里读单连接限速（字节/秒）。0 = 读不到</summary>
        public static int ReadSpd(string url)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(new Uri(url).Query, @"[?&]spd=(\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 估算下载耗时（保守下限）。
        /// 实测标定（2026-10-16，13.5MB 文件）：
        ///   4 路（3 条 spd=30000 + 1 条 spd=100000）实际 933 KB/s —— 远超 spd 之和，
        ///   说明 spd 是「软限速」，实际能跑多快取决于 CDN 当时的负载，不可预测。
        ///   另一次 7 路慢档实测聚合 109 KB/s（≈ 单连接的 3.3 倍）。
        /// 所以这里按「每条连接的 spd 之和」给一个下限，实际通常更快；只用于提示量级，
        /// 不承诺准确。真正慢的文件（几百 MB 的慢档）用这个估是准的。
        /// </summary>
        public static string EstimateTime(List<Source> sources, long totalBytes)
        {
            if (totalBytes <= 0) return "";
            double sum = 0; int conns = 0;
            foreach (var s in sources)
            {
                int t = Math.Max(1, s.Threads);
                conns += t;
                // 读不到 spd 时按 100KB/s 兜底
                sum += (s.Spd > 0 ? s.Spd : 100000) * t;
            }
            if (conns == 0 || sum < 1024) return "";
            double secs = totalBytes / sum;
            var ts = TimeSpan.FromSeconds(secs);
            return secs < 60 ? $"至少 {Math.Max(1, (int)Math.Round(secs))} 秒"
                 : secs < 3600 ? $"至少 {ts.Minutes} 分 {ts.Seconds} 秒"
                 : $"至少 {ts.Hours} 小时 {ts.Minutes} 分";
        }

        /// <summary>多来源并发下载，返回实际线程数</summary>
        public static async Task<int> DownloadAsync(List<Source> sources, string outPath,
            Action<long, long, double> onProgress, CancellationToken ct, Action<string> log = null)
        {
            var http = NewClient(OkHttp);

            // 预检：城通有的存储节点会整个挂掉（账号直链全 503），但匿名那条还能用。
            // 不剔掉死链的话，那些分块会一直失败，最后整个下载报错。
            async Task<(Source src, bool ok, long total, int code)> Probe(Source s)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, s.Url);
                    req.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
                    using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        if (res.StatusCode != HttpStatusCode.PartialContent && res.StatusCode != HttpStatusCode.OK)
                            return (s, false, 0L, (int)res.StatusCode);
                        long total = 0;
                        var cr = res.Content.Headers.ContentRange;
                        if (cr != null && cr.Length.HasValue) total = cr.Length.Value;
                        else if (res.Content.Headers.ContentLength.HasValue) total = res.Content.Headers.ContentLength.Value;
                        using (var st = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                        {
                            var buf = new byte[1024];
                            await st.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                        }
                        return (s, true, total, 200);
                    }
                }
                catch { return (s, false, 0L, 0); }
            }

            var probeResults = await Task.WhenAll(sources.Select(Probe)).ConfigureAwait(false);
            var alive = new List<Source>();
            var dead = new List<string>();
            long size = 0;
            foreach (var pr in probeResults)
            {
                if (pr.ok) { alive.Add(pr.src); if (pr.total > size) size = pr.total; }
                else dead.Add($"{pr.src.Label}（HTTP {pr.code}）");
            }
            if (log != null && dead.Count > 0) log($"跳过 {dead.Count} 条不可用直链：{string.Join("、", dead)}");
            if (alive.Count == 0)
                throw new Exception("所有直链都不可用（节点返回 503，或链接已过期）。\n请重新点「解析」再试。");
            if (size == 0) throw new Exception("拿不到文件大小，直链可能已过期，请重新解析");

            var workers = new List<Source>();
            foreach (var s in alive)
            {
                int t = Math.Max(1, Math.Min(s.Threads <= 0 ? 1 : s.Threads, 8));
                for (int i = 0; i < t; i++) workers.Add(s);
            }
            int n = workers.Count;
            long chunk = (long)Math.Ceiling((double)size / n);

            long done = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double lastSpeed = 0;
            long lastDone = 0;
            long lastTick = sw.ElapsedTicks;
            var reportLock = new object();
            Action<bool> report = (force) =>
            {
                lock (reportLock)
                {
                    long d = Interlocked.Read(ref done);
                    long now = sw.ElapsedTicks;
                    double secs = (now - lastTick) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (!force && secs < 0.35) return;
                    if (secs > 0.05) { lastSpeed = (d - lastDone) / secs; lastDone = d; lastTick = now; }
                    onProgress(d, size, lastSpeed);
                }
            };

            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    var handle = fs.SafeFileHandle;
                    var tasks = new List<Task>();
                    for (int i = 0; i < n; i++)
                    {
                        long start = i * chunk;
                        long end = Math.Min(start + chunk - 1, size - 1);
                        if (start > end) continue;
                        // C# 的 for 循环变量是闭包共享的：回调异步执行时 i 已变成 n-1。
                        // 所有要用的值都必须在循环体内同步取出。
                        string url = workers[i].Url;
                        var refetch = workers[i].Refetch;
                        tasks.Add(Task.Run(async () =>
                        {
                            // url 只作首跳。重试一律重新取链，理由见 Source.Refetch 注释。
                            string cur = url;
                            int attempt = 0;
                            while (true)
                            {
                                try
                                {
                                    var req = new HttpRequestMessage(HttpMethod.Get, cur);
                                    req.Headers.TryAddWithoutValidation("Range", "bytes=" + start + "-" + end);
                                    using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                                    {
                                        // 429 是软限流：IP 被打上标记，等一会儿通常能恢复，
                                        // 直接甩手会把整盘下载判死。折进退避，退避时长取 3 秒 × 重试次数。
                                        if (res.StatusCode == (HttpStatusCode)429)
                                            throw new ThrottleException();
                                        if (res.StatusCode != HttpStatusCode.PartialContent && res.StatusCode != HttpStatusCode.OK)
                                            throw new Exception("HTTP " + (int)res.StatusCode);
                                        using (var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                                        {
                                            var buf = new byte[1 << 16];
                                            long pos = start;
                                            int read;
                                            while ((read = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                                            {
                                                await RandomAccess.WriteAsync(handle, new ReadOnlyMemory<byte>(buf, 0, read), pos, ct).ConfigureAwait(false);
                                                pos += read;
                                                Interlocked.Add(ref done, read);
                                                report(false);
                                            }
                                            if (pos != end + 1) throw new Exception("分块不完整 " + (pos - start) + "/" + (end + 1 - start));
                                        }
                                    }
                                    return;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    if (++attempt >= 4) throw;
                                    // 退避加随机抖动：多分块同时失败时错开重试时刻，
                                    // 否则会在同一秒集体打向刚被限流的节点（上游模块的做法）。
                                    double ms = ex is ThrottleException ? 3000 * attempt : 800 * attempt;
                                    try { await Task.Delay((int)(ms * (1 + rnd.NextDouble() * 0.5)), ct).ConfigureAwait(false); }
                                    catch (OperationCanceledException) { throw; }

                                    // 换链。只有账号源有 Refetch；匿名源在取链失败时退回原链。
                                    if (refetch != null)
                                    {
                                        try
                                        {
                                            var fresh = await refetch(ct).ConfigureAwait(false);
                                            if (fresh != null && !string.IsNullOrEmpty(fresh.Url)) cur = fresh.Url;
                                        }
                                        catch (OperationCanceledException) { throw; }
                                        catch (Exception e2)
                                        {
                                            // 续取失败不致命：旧链可能还没过期，继续用它试。
                                            if (log != null) log("续取直链失败（继续用旧链）：" + e2.Message);
                                        }
                                    }
                                }
                            }
                        }, ct));
                    }
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            report(true);
            return n;
        }

        // ==================== 文件夹分享 ====================

        /// <summary>文件夹里的一個文件项</summary>
        public class FolderFile
        {
            public string Path { get; set; }      // 含子目录的完整路径
            public string Name { get; set; }
            public string FileCode { get; set; }  // 用于取直链
            public long Size { get; set; }
            public string SizeText { get; set; }
            public string Date { get; set; }
            public bool Checked { get; set; } = true;   // 默认勾选
        }

        public class FolderResult
        {
            public string FolderName { get; set; }
            public List<FolderFile> Files { get; set; } = new List<FolderFile>();
            public List<string> Warnings { get; set; } = new List<string>();
            public int DirCount { get; set; }
            public long TotalBytes { get { long n = 0; foreach (var f in Files) n += f.Size; return n; } }
        }

        // 从列1 的 HTML 里提取信息
        static readonly System.Text.RegularExpressions.Regex RxFileName =
            new System.Text.RegularExpressions.Regex(@"filename-text"">.*?<a [^>]*>([^<]+)</a>", System.Text.RegularExpressions.RegexOptions.Singleline);
        static readonly System.Text.RegularExpressions.Regex RxSubDir =
            new System.Text.RegularExpressions.Regex(@"load_subdir\((\d+),\s*'([0-9a-f]+)'\)");
        static readonly System.Text.RegularExpressions.Regex RxHref =
            new System.Text.RegularExpressions.Regex(@"href=""([^""]*)""");
        // 列0 复选框里的 file_ids[] value 才是服务端认的文件 id（形如 f8462389031）。
        // 列1 的 href 现在是 href="#/f/tempdir-<base64>"：前端 SPA 路由占位，
        // 不是 <uid>-<fid>-<chk> 的三段式分享码，直接拿去调 getfile.php 会一路 404。
        static readonly System.Text.RegularExpressions.Regex RxFileId =
            new System.Text.RegularExpressions.Regex(@"name=""file_ids\[\]""[^>]*value=""([^""]*)""");
        static readonly System.Text.RegularExpressions.Regex RxShareCode =
            new System.Text.RegularExpressions.Regex(@"(?:^|/)([0-9]+-[0-9]+-[0-9a-fA-F]+)");

        static long ParseSizeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(s.Trim(), @"^([\d.]+)\s*(GB|MB|KB|B)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            double v;
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return 0;
            switch (m.Groups[2].Value.ToUpper())
            {
                case "GB": return (long)(v * 1073741824);
                case "MB": return (long)(v * 1048576);
                case "KB": return (long)(v * 1024);
                default: return (long)v;
            }
        }

        /// <summary>
        /// 递归遍历文件夹分享，返回全部文件。
        /// 关键点：folder_id 必须传数字 fid（load_subdir 的第一参数），传 hash 会 504。
        /// </summary>
        public static async Task<FolderResult> ListFolderAsync(Parsed p, Action<string> log = null, CancellationToken ct = default(CancellationToken))
        {
            void L(string s) { if (log != null) log(s); }
            var result = new FolderResult();

            // 提取码用 cookie 传（文件夹接口对 ?p= 不总是认）
            string shareId = p.Code.Split('-').Length > 1 ? p.Code.Split('-')[1] : "";
            var http = NewClient();
            string origin = null;
            try { origin = new Uri(p.PageUrl).Scheme + "://" + new Uri(p.PageUrl).Host; } catch { }
            if (origin != null) http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
            if (!string.IsNullOrEmpty(p.Passcode) && shareId.Length > 0)
                http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "pass_d" + shareId + "=" + p.Passcode);

            var dirs = new System.Collections.Generic.HashSet<string>();

            async Task Walk(string folderId, string prefix, int depth)
            {
                if (depth > 6) { result.Warnings.Add("目录层级超过 6 层，已停止深挖"); return; }
                var url = WebApi + "/getdir.php?path=d&d=" + Uri.EscapeDataString(p.Code) + "&folder_id=" + (folderId ?? "");
                var dirInfo = await GetJson(http, url, p.PageUrl, L).ConfigureAwait(false);
                int dc = dirInfo.TryGetProperty("code", out var dce) ? dce.GetInt32() : 0;
                if (dc != 200)
                {
                    string msg = dirInfo.TryGetProperty("message", out var dme) ? dme.GetString() : "";
                    if (dc == 503) throw new Exception("分享已失效或被限制（503）" + (string.IsNullOrEmpty(msg) ? "" : "：" + msg));
                    if (dc == 423) throw new Exception("这个分享需要提取码，请在链接后面加 ?p=提取码");
                    throw new Exception("读取目录失败（code=" + dc + "）" + (string.IsNullOrEmpty(msg) ? "" : "：" + msg));
                }
                if (!dirInfo.TryGetProperty("file", out var fileEl)) return;
                string folderName = fileEl.TryGetProperty("folder_name", out var fne) ? fne.GetString() : "(未命名目录)";
                string listUrl = fileEl.TryGetProperty("url", out var ue) ? ue.GetString() : null;
                if (string.IsNullOrEmpty(listUrl)) return;
                if (result.FolderName == null) result.FolderName = folderName;

                string curPath = string.IsNullOrEmpty(prefix) ? folderName : prefix + "/" + folderName;
                dirs.Add(curPath);

                var listJson = await GetJson(http, WebApi + listUrl, p.PageUrl, L).ConfigureAwait(false);
                if (!listJson.TryGetProperty("aaData", out var aa) || aa.ValueKind != JsonValueKind.Array) return;

                foreach (var it in aa.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    string html = it.ValueKind == JsonValueKind.Array && it.GetArrayLength() > 1 ? it[1].GetString() : null;
                    if (string.IsNullOrEmpty(html)) continue;

                    var nm = RxFileName.Match(html);
                    string name = nm.Success ? nm.Groups[1].Value.Trim() : "(无名)";
                    var sub = RxSubDir.Match(html);
                    if (sub.Success)
                    {
                        // 子目录：用第一参数（数字 fid）
                        await Walk(sub.Groups[1].Value, curPath, depth + 1).ConfigureAwait(false);
                        continue;
                    }
                    // 文件 id 有两个来源：列1 href 里的三段式分享码（旧版），
                    // 列0 file_ids[] 的 value（新版 SPA）。两者都拿不到就记一条 warning，
                    // 不能静默丢掉——否则用户只看到「没有解析出任何文件」，无从判断原因。
                    string fileCode = null;
                    var href = RxHref.Match(html);
                    if (href.Success)
                    {
                        var sc = RxShareCode.Match(href.Groups[1].Value);
                        if (sc.Success) fileCode = sc.Groups[1].Value;
                    }
                    if (fileCode == null)
                    {
                        var col0 = it.GetArrayLength() > 0 && it[0].ValueKind == JsonValueKind.String ? it[0].GetString() : null;
                        if (col0 != null)
                        {
                            var idm = RxFileId.Match(col0);
                            if (idm.Success) fileCode = idm.Groups[1].Value;
                        }
                    }

                    string sizeText = it.GetArrayLength() > 2 && it[2].ValueKind == JsonValueKind.String ? it[2].GetString() : "";
                    string date = it.GetArrayLength() > 3 && it[3].ValueKind == JsonValueKind.String ? it[3].GetString() : "";

                    if (fileCode == null)
                    {
                        result.Warnings.Add("跳过「" + name + "」：这个分享的页面结构变了，没能从中取出文件标识。" +
                            "（单文件链接、代码里写死的 fileCode 不受影响。）");
                        continue;
                    }

                    result.Files.Add(new FolderFile
                    {
                        Path = curPath + "/" + name,
                        Name = name,
                        FileCode = fileCode,
                        Size = ParseSizeText(sizeText),
                        SizeText = sizeText,
                        Date = date,
                    });
                }
            }

            await Walk("", "", 0).ConfigureAwait(false);
            result.DirCount = dirs.Count;
            if (result.Files.Count == 0)
            {
                if (result.DirCount > 0) throw new Exception("这个分享里只有空目录，没有可下载的文件");
                throw new Exception("没有解析出任何文件");
            }
            L("共 " + result.DirCount + " 个目录、" + result.Files.Count + " 个文件");
            return result;
        }

        /// <summary>给文件夹里的单个文件取一条匿名直链</summary>
        public static async Task<Source> GetFileLinkAsync(string fileCode, string pageUrl, Action<string> log = null)
        {
            var http = NewClient();
            var url = WebApi + "/getfile.php?path=f&f=" + Uri.EscapeDataString(fileCode) + "&r=" + new Random().NextDouble() + "&ref=&url=" + Uri.EscapeDataString(pageUrl ?? "");
            var j = await GetJson(http, url, pageUrl, log).ConfigureAwait(false);
            int code = j.TryGetProperty("code", out var ce) ? ce.GetInt32() : 0;
            string msg = j.TryGetProperty("message", out var me2) ? me2.GetString() : "";
            if (code != 200 || !j.TryGetProperty("file", out var f))
            {
                // code=404 + fileCode 不是三段式：多半是文件夹解析出的 id 形态变了
                // （服务端把列1 href 改成 SPA 路由占位后，从 file_ids[] 取到的 fid 不完整）。
                if (code == 404 && !RxShareCode.IsMatch(fileCode))
                    throw new Exception("取文件信息失败：文件标识 " + fileCode + " 不是完整分享码，" +
                        "很可能这个文件夹分享改用了新的页面结构。请改用单个文件的分享链接下载。");
                throw new Exception("取文件信息失败（code=" + code + "）" + msg);
            }

            // 元数据缺字段时报错带上实际值（上游 ctfile_free.py:227-231 的做法）：
            // 光说"没拿到"没法区分是分享失效、字段改名还是链接格式不对。
            string uid = Str(f, "userid"), fid = Str(f, "file_id"), chk = Str(f, "file_chk");
            if (uid.Length == 0 || fid.Length == 0 || chk.Length == 0)
                throw new Exception("分享页元数据不完整：userid=" + uid + " file_id=" + fid + " file_chk=" + chk +
                                    "（通常是该文件已被分享者删除或取消分享）");

            string q = "uid=" + uid + "&fid=" + fid
                     + "&file_chk=" + chk + "&start_time=" + Str(f, "start_time")
                     + "&wait_seconds=" + Str(f, "wait_seconds") + "&rd=" + new Random().NextDouble();
            var du = await GetJson(http, WebApi + "/get_down_url.php?" + q, pageUrl, log).ConfigureAwait(false);
            if (!du.TryGetProperty("downurl", out var durl) || string.IsNullOrEmpty(durl.GetString()))
                throw new Exception("没拿到下载地址（uid=" + uid + " fid=" + fid + "，可能是等待未结束或直链已过期）");
            string u = durl.GetString();
            return new Source
            {
                Url = u,
                Threads = 1,
                Label = "匿名",
                Kind = "anonymous",
                Host = SafeHost(u),
                Spd = ReadSpd(u),
            };
        }
    }
}
