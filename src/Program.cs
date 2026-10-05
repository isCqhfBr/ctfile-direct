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
                Console.WriteLine("   · " + s.Label.PadRight(30) + s.Threads + " 线程  " + s.Host);
            foreach (var w in r.Warnings) Console.WriteLine("  ! " + w);

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
    }
}
