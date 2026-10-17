using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CtfileDownloader
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--cli" || args[0] == "-c"))
            {
                AttachConsole(-1);
                int rc = RunCli(args.Skip(1).ToArray()).GetAwaiter().GetResult();
                Environment.Exit(rc);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
            // 支持把分享链接直接拖到 exe 上 / 作为参数传入：界面会预填并自动解析
            string initial = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;
            Application.Run(new MainForm(initial));
        }

        static string ExeDir()
        {
            var p = Environment.ProcessPath;
            return string.IsNullOrEmpty(p) ? AppContext.BaseDirectory : Path.GetDirectoryName(p);
        }

        static string Fmt(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1048576) return (n / 1024.0).ToString("F0") + " KB";
            if (n < 1073741824L) return (n / 1048576.0).ToString("F2") + " MB";
            return (n / 1073741824.0).ToString("F2") + " GB";
        }

        static async Task<int> RunCli(string[] a)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            string link = a.FirstOrDefault(x => !x.StartsWith("--"));
            string outDir = null;
            bool noDownload = a.Contains("--no-download");
            for (int i = 0; i < a.Length; i++)
                if (a[i] == "--out" && i + 1 < a.Length) outDir = a[i + 1];

            if (string.IsNullOrEmpty(link))
            {
                Console.WriteLine("用法: 城通网盘下载器.exe --cli \"<分享链接>\" [--out <目录>] [--no-download]");
                return 2;
            }

            var cfgPath = Path.Combine(ExeDir(), "config.json");
            AppConfig cfg = new AppConfig();
            try { if (File.Exists(cfgPath)) cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath)) ?? cfg; } catch { }

            // 文件夹分享
            var parsed = Ctfile.Parse(link);
            if (parsed.Kind == 'd')
            {
                Ctfile.FolderResult fr;
                try { fr = await Ctfile.ListFolderAsync(parsed, s => Console.WriteLine("  " + s)); }
                catch (Exception ex) { Console.WriteLine("解析失败: " + ex.Message); return 1; }

                Console.WriteLine();
                Console.WriteLine("文件夹 : " + fr.FolderName);
                Console.WriteLine("文件数 : " + fr.Files.Count + " 个，共 " + Fmt(fr.TotalBytes) + "（" + fr.DirCount + " 个目录）");
                foreach (var w in fr.Warnings) Console.WriteLine("  ! " + w);
                int show = Math.Min(fr.Files.Count, 30);
                for (int i = 0; i < show; i++)
                {
                    var f = fr.Files[i];
                    Console.WriteLine("   · " + f.Path + "  " + (string.IsNullOrEmpty(f.SizeText) ? "-" : f.SizeText));
                }
                if (fr.Files.Count > show) Console.WriteLine("   … 还有 " + (fr.Files.Count - show) + " 个文件");

                if (noDownload) return 0;
                if (string.IsNullOrEmpty(outDir))
                    outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(outDir);

                var ctsF = new CancellationTokenSource();
                int ok = 0, fail = 0;
                var fails = new List<string>();
                var swF = System.Diagnostics.Stopwatch.StartNew();
                long doneF = 0;
                foreach (var f in fr.Files)
                {
                    string rel = f.Path;
                    int slash = rel.IndexOf('/');
                    if (slash > 0) rel = rel.Substring(slash + 1);
                    string fOut = Path.Combine(outDir, Sanitize(rel));
                    try { Directory.CreateDirectory(Path.GetDirectoryName(fOut)); } catch { }
                    Console.WriteLine(string.Format("[{0}/{1}] {2}", ok + fail + 1, fr.Files.Count, f.Path));
                    try
                    {
                        var src = await Ctfile.GetFileLinkAsync(f.FileCode, parsed.PageUrl);
                        await Ctfile.DownloadAsync(new List<Source> { src }, fOut, (d, t2, sp) =>
                        {
                            long abs = doneF + d;
                            int pct = fr.TotalBytes > 0 ? (int)(abs * 100 / fr.TotalBytes) : 0;
                            Console.WriteLine(string.Format("      {0}%  {1}  {2}/s", pct, Fmt(abs), Fmt((long)sp)));
                        }, ctsF.Token);
                        ok++;
                    }
                    catch (Exception ex) { fail++; fails.Add(f.Path + "：" + ex.Message); }
                    doneF += f.Size;
                }
                Console.WriteLine();
                Console.WriteLine(string.Format("完成: {0} 个成功，{1} 个失败，用时 {2:F1} 秒", ok, fail, swF.Elapsed.TotalSeconds));
                foreach (var x in fails) Console.WriteLine("  ! " + x);
                Console.WriteLine("保存到 : " + outDir);
                return fail > 0 ? 1 : 0;
            }

            Resolved r;
            try
            {
                r = await Ctfile.ResolveAsync(link, cfg, s => Console.WriteLine("  " + s));
            }
            catch (Exception ex)
            {
                Console.WriteLine("解析失败: " + ex.Message);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("文件名 : " + r.FileName);
            Console.WriteLine("大小   : " + Fmt(r.FileSize));
            Console.WriteLine("并发   : " + r.TotalThreads + " 线程 / " + r.Sources.Count + " 条直链");
            foreach (var s in r.Sources)
            {
                string spd = s.Spd > 0 ? "  " + (s.Spd / 1024) + " KB/s" : "";
                Console.WriteLine("   · " + s.Label.PadRight(30) + s.Threads + " 线程  " + s.Host + spd);
            }
            foreach (var w in r.Warnings) Console.WriteLine("  ! " + w);

            string est = Ctfile.EstimateTime(r.Sources, r.FileSize);
            if (!string.IsNullOrEmpty(est)) Console.WriteLine("预计耗时 : " + est);

            if (noDownload) return 0;

            if (string.IsNullOrEmpty(outDir))
                outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(outDir);
            foreach (var c in Path.GetInvalidFileNameChars()) r.FileName = r.FileName.Replace(c, '_');
            string outPath = Path.Combine(outDir, r.FileName);

            Console.WriteLine();
            Console.WriteLine("保存到 : " + outPath);
            var cts = new CancellationTokenSource();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int lastPct = -1;
            try
            {
                int th = await Ctfile.DownloadAsync(r.Sources, outPath, (done, total, speed) =>
                {
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        Console.WriteLine(string.Format("  {0,3}%  {1} / {2}  {3}/s", pct, Fmt(done), Fmt(total), Fmt((long)speed)));
                    }
                }, cts.Token, (s) => Console.WriteLine("  " + s));
                var len = new FileInfo(outPath).Length;
                Console.WriteLine(string.Format("完成: {0}  用时 {1:F1}s  平均 {2}/s  ({3} 线程)", Fmt(len), sw.Elapsed.TotalSeconds, Fmt((long)(len / Math.Max(0.1, sw.Elapsed.TotalSeconds))), th));
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("下载失败: " + ex.Message);
                return 1;
            }
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unnamed.bin";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            foreach (var c in Path.GetInvalidPathChars()) s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}
