using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CtfileDownloader
{
    public class Source
    {
        public string Url { get; set; }
        public int Threads { get; set; }
        public string Label { get; set; }
        public string Host { get; set; }
        public string Kind { get; set; }   // account / anonymous
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
    }

    public static class Ctfile
    {
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
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua ?? Ua);
            return c;
        }

        public class Parsed
        {
            public string Code, Passcode, PageUrl;
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
                if (m.Groups[1].Value[0] != 'f') throw new Exception("目前只支持单文件分享（/f/...），文件夹分享暂未支持");
                string pc = "";
                var q = u.Query;
                var mp = System.Text.RegularExpressions.Regex.Match(q, @"[?&](?:p|passcode)=([^&]*)");
                if (mp.Success) pc = Uri.UnescapeDataString(mp.Groups[1].Value);
                return new Parsed { Code = m.Groups[2].Value, Passcode = pc, PageUrl = s };
            }
            var m2 = rx2.Match(s);
            if (!m2.Success) throw new Exception("看不懂这个分享码，应为 37476991-1447877437-0eb71b 这种格式");
            return new Parsed { Code = m2.Groups[1].Value, Passcode = "", PageUrl = "https://url91.ctfile.com/f/" + m2.Groups[1].Value };
        }

        // 城通会按接口主机限流（短时间请求太多就返 429）。这里统一做退避重试，
        // 重试仍失败就给一句人话，而不是把 HTTP 状态码糊到用户脸上。
        static async Task<JsonElement> SendJson(HttpClient c, Func<HttpRequestMessage> makeReq, string what, Action<string> log = null)
        {
            int[] waits = { 2000, 5000 };
            for (int attempt = 0; ; attempt++)
            {
                using (var req = makeReq())
                using (var res = await c.SendAsync(req).ConfigureAwait(false))
                {
                    int code = (int)res.StatusCode;
                    var txt = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (code == 429)
                    {
                        if (attempt < waits.Length)
                        {
                            if (log != null) log($"被限流，{waits[attempt] / 1000} 秒后重试…");
                            await Task.Delay(waits[attempt]).ConfigureAwait(false);
                            continue;
                        }
                        throw new Exception(
                            "城通把这个 IP 限流了（HTTP 429，请求过于频繁）。\n\n" +
                            "这不是链接失效，等 10~30 分钟再试就行。\n" +
                            "如果是刚批量解析过很多链接，属于正常现象。");
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
            }, "取文件信息接口", log);
        }

        static Task<JsonElement> PostJson(HttpClient c, string url, string json, string referer = null, string bearer = null, Action<string> log = null)
        {
            return SendJson(c, () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                if (referer != null) req.Headers.TryAddWithoutValidation("Origin", "https://my.ctfile.com");
                req.Headers.TryAddWithoutValidation("Referer", referer ?? "https://my.ctfile.com/");
                if (bearer != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                return req;
            }, "城通接口", log);
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
                            result.Sources.Add(new Source { Url = url, Threads = th, Label = acc.email, Kind = "account", Host = SafeHost(url) });
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
                        result.Sources.Add(new Source { Url = u, Threads = th, Label = "匿名", Kind = "anonymous", Host = SafeHost(u) });
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
                        string url = workers[i].Url;
                        tasks.Add(Task.Run(async () =>
                        {
                            int attempt = 0;
                            while (true)
                            {
                                try
                                {
                                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                                    req.Headers.TryAddWithoutValidation("Range", "bytes=" + start + "-" + end);
                                    using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                                    {
                                        if (res.StatusCode == (HttpStatusCode)429)
                                            throw new Exception("下载被城通限流（HTTP 429），等几分钟再试");
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
                                catch (Exception)
                                {
                                    if (++attempt >= 4) throw;
                                    await Task.Delay(800 * attempt, ct).ConfigureAwait(false);
                                }
                            }
                        }, ct));
                    }
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            report(true);
            return n;
        }
    }
}
