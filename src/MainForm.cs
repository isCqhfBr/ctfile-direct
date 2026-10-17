using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CtfileDownloader
{
    public class MainForm : Form
    {
        static readonly Color C_BG = Color.FromArgb(14, 17, 22);
        static readonly Color C_CARD = Color.FromArgb(29, 36, 49);
        static readonly Color C_FG = Color.FromArgb(230, 237, 246);
        static readonly Color C_DIM = Color.FromArgb(139, 152, 171);
        static readonly Color C_ACCENT = Color.FromArgb(56, 189, 248);
        static readonly Color C_GREEN = Color.FromArgb(74, 222, 128);
        static readonly Color C_RED = Color.FromArgb(248, 113, 113);

        TextBox txtLink, txtOutDir, txtEmail, txtPass;
        Button btnResolve, btnDl, btnCancel, btnBrowse, btnOpenDir, btnAddAcc, btnDelAcc, btnAll, btnNone, btnInvert;
        Label lblFile, lblStats, lblStatus, lblFiles;
        ListView lvSources, lvAccounts, lvFiles;
        ProgressBar bar;
        CheckBox chkAnon;

        Resolved _resolved;
        Ctfile.FolderResult _folder;
        Ctfile.Parsed _parsed;
        CancellationTokenSource _cts;
        bool _busy;
        string _cfgPath;

        public MainForm(string initialLink = null)
        {
            Text = "城通网盘 多账号加速下载";
            ClientSize = new Size(960, 880);
            MinimumSize = new Size(880, 840);            BackColor = C_BG;
            ForeColor = C_FG;
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            _cfgPath = Path.Combine(ExeDir(), "config.json");
            BuildUi();
            LoadConfig();
            if (!string.IsNullOrEmpty(initialLink))
            {
                txtLink.Text = initialLink;
                Shown += async (a, b) => await DoResolve();
            }
        }

        static string ExeDir()
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(p);
            return AppContext.BaseDirectory;
        }

        static string DefaultDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        Label MkLabel(string text, int x, int y, Color c, float size = 9F, bool bold = false)
        {
            var l = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = c,
                BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            };
            Controls.Add(l);
            return l;
        }

        TextBox MkText(int x, int y, int w)
        {
            var t = new TextBox
            {
                Location = new Point(x, y),
                Width = w,
                BackColor = C_CARD,
                ForeColor = C_FG,
                BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(t);
            return t;
        }

        Button MkButton(string text, int x, int y, int w, int h, bool primary = true)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Width = w,
                Height = h,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? C_ACCENT : C_CARD,
                ForeColor = primary ? Color.FromArgb(4, 18, 28) : C_FG,
                Font = new Font("Microsoft YaHei UI", 9F, primary ? FontStyle.Bold : FontStyle.Regular),
            };
            b.FlatAppearance.BorderColor = primary ? C_ACCENT : Color.FromArgb(40, 49, 63);
            b.FlatAppearance.BorderSize = 1;
            Controls.Add(b);
            return b;
        }

        void BuildUi()
        {
            // 布局总原则：各区块 y 坐标严格递增，绝不重叠。
            //  标题            12
            //  ① 分享链接       72
            //  文件名/预估       126
            //  ② 下载来源       154 (高 92，可放 3 行)
            //  ③ 文件列表       262 (高 140，默认隐藏)
            //  保存目录         436
            //  进度条/状态       492
            //  开始/取消         516
            //  ④ 账号池         566
            //  账号输入         708
            //  匿名勾选/提示     748
            //  状态行           792
            const int PAD = 16, W = 928;

            MkLabel("城通网盘 多账号加速下载", PAD, 12, C_FG, 15F, true);
            MkLabel("粘贴分享链接 → 用所有账号各开一路并发下载", PAD, 42, C_DIM, 9F);

            // ① 分享链接
            MkLabel("① 分享链接（单文件 / 文件夹都支持）", PAD, 72, C_DIM, 9F, true);
            txtLink = MkText(PAD, 92, 776);
            btnResolve = MkButton("解析", 800, 90, 144, 28);
            btnResolve.Click += async (a, b) => await DoResolve();

            lblFile = MkLabel("", PAD, 126, C_FG, 9.5F);

            // ② 下载来源
            MkLabel("② 下载来源", PAD, 154, C_DIM, 9F, true);
            lvSources = new ListView
            {
                Location = new Point(PAD, 174),
                Size = new Size(W, 92),
                View = View.Details,
                FullRowSelect = true,
                BackColor = C_CARD,
                ForeColor = C_FG,
                BorderStyle = BorderStyle.FixedSingle,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            lvSources.Columns.Add("来源", 400);
            lvSources.Columns.Add("线程", 70);
            lvSources.Columns.Add("主机", 300);
            lvSources.Columns.Add("限速", 130);
            lvSources.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lvSources);

            // ③ 文件列表（文件夹分享时才显示）
            lblFiles = MkLabel("③ 文件列表（默认全选，按需取消勾选）", PAD, 262, C_DIM, 9F, true);
            lblFiles.Visible = false;
            lvFiles = new ListView
            {
                Location = new Point(PAD, 282),
                Size = new Size(W, 140),
                View = View.Details,
                FullRowSelect = true,
                CheckBoxes = true,
                BackColor = C_CARD,
                ForeColor = C_FG,
                BorderStyle = BorderStyle.FixedSingle,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Visible = false,
            };
            lvFiles.Columns.Add("文件", 400);
            lvFiles.Columns.Add("大小", 90);
            lvFiles.Columns.Add("修改日期", 110);
            lvFiles.Columns.Add("所在目录", 328);
            lvFiles.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lvFiles.ItemChecked += (a, b) => UpdateSelectedStats();
            Controls.Add(lvFiles);

            btnAll = MkButton("全选", PAD, 430, 70, 26, false);
            btnAll.Visible = false;
            btnAll.Click += (a, b) => { foreach (ListViewItem i in lvFiles.Items) i.Checked = true; UpdateSelectedStats(); };
            btnNone = MkButton("全不选", 92, 430, 70, 26, false);
            btnNone.Visible = false;
            btnNone.Click += (a, b) => { foreach (ListViewItem i in lvFiles.Items) i.Checked = false; UpdateSelectedStats(); };
            btnInvert = MkButton("反选", 168, 430, 70, 26, false);
            btnInvert.Visible = false;
            btnInvert.Click += (a, b) => { foreach (ListViewItem i in lvFiles.Items) i.Checked = !i.Checked; UpdateSelectedStats(); };

            // 保存目录
            MkLabel("保存目录", PAD, 466, C_DIM, 9F, true);
            txtOutDir = MkText(PAD, 486, 700);
            txtOutDir.Text = DefaultDir();
            btnBrowse = MkButton("浏览…", 724, 484, 100, 28, false);
            btnBrowse.Click += (a, b) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.SelectedPath = txtOutDir.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) txtOutDir.Text = d.SelectedPath;
                }
            };
            btnOpenDir = MkButton("打开目录", 832, 484, 112, 28, false);
            btnOpenDir.Click += (a, b) =>
            {
                try { Process.Start("explorer", "\"" + txtOutDir.Text + "\""); } catch { }
            };

            bar = new ProgressBar { Location = new Point(PAD, 524), Size = new Size(W, 18), Minimum = 0, Maximum = 1000 };
            bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(bar);
            lblStats = MkLabel("等待开始", PAD, 546, C_DIM, 9F);

            btnDl = MkButton("开始下载", PAD, 570, 150, 36);
            btnDl.Enabled = false;
            btnDl.Click += async (a, b) => await DoDownload();
            btnCancel = MkButton("取消", 176, 570, 110, 36, false);
            btnCancel.Visible = false;
            btnCancel.Click += (a, b) => { if (_cts != null) _cts.Cancel(); };

            // ④ 账号池
            MkLabel("④ 账号池（每个账号能提供几条并发由城通服务端决定）", PAD, 620, C_DIM, 9F, true);
            lvAccounts = new ListView
            {
                Location = new Point(PAD, 640),
                Size = new Size(W, 92),
                View = View.Details,
                FullRowSelect = true,
                BackColor = C_CARD,
                ForeColor = C_FG,
                BorderStyle = BorderStyle.FixedSingle,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            lvAccounts.Columns.Add("账号", 560);
            lvAccounts.Columns.Add("状态", 340);
            lvAccounts.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lvAccounts);

            txtEmail = MkText(PAD, 740, 280);
            txtEmail.PlaceholderText = "城通账号（邮箱）";
            txtPass = MkText(306, 740, 240);
            txtPass.PlaceholderText = "密码";
            txtPass.UseSystemPasswordChar = true;
            btnAddAcc = MkButton("添加账号", 556, 738, 110, 28, false);
            btnAddAcc.Click += (a, b) => AddAccount();
            btnDelAcc = MkButton("删除选中", 676, 738, 110, 28, false);
            btnDelAcc.Click += (a, b) => DelAccount();

            chkAnon = new CheckBox
            {
                Text = "额外使用匿名通道（不需要账号）",
                Location = new Point(PAD, 778),
                AutoSize = true,
                ForeColor = C_DIM,
                FlatStyle = FlatStyle.Flat,
            };
            chkAnon.CheckedChanged += (a, b) => SaveConfig();
            Controls.Add(chkAnon);

            MkLabel("⚠ 城通风控会针对「同一 IP 多账号下载同一文件」，被判定多用户登录的账号会被锁定。账号越多、下载越频繁，风险越高。",
                PAD, 802, Color.FromArgb(200, 160, 90), 8.5F);
            MkLabel("密码以明文保存在 config.json（仅本程序读取）；密码错误的账号会自动跳过，不影响其他账号。",
                PAD, 820, Color.FromArgb(120, 132, 150), 8.5F);

            lblStatus = MkLabel("", PAD, 842, C_DIM, 9F);
        }

        // ---------------- 配置 ----------------
        bool _loading = true;

        void LoadConfig()
        {
            AppConfig cfg = new AppConfig();
            try
            {
                if (File.Exists(_cfgPath))
                    cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_cfgPath)) ?? new AppConfig();
            }
            catch { }
            _cfg = cfg;                     // 必须先赋值：下面设置 chkAnon.Checked 会触发 CheckedChanged
            chkAnon.Checked = cfg.useAnonymous;
            RefreshAccounts(_cfg);
            lblStatus.Text = "配置：" + _cfgPath;
            _loading = false;               // 加载完成后才允许写盘
        }

        AppConfig _cfg = new AppConfig();

        void RefreshAccounts(AppConfig cfg)
        {
            _cfg = cfg ?? new AppConfig();
            lvAccounts.Items.Clear();
            foreach (var a in _cfg.accounts)
            {
                var it = new ListViewItem(a.email);
                it.SubItems.Add("就绪（解析时自动登录）");
                lvAccounts.Items.Add(it);
            }
            if (_cfg.accounts.Count == 0)
            {
                var it = new ListViewItem("（无账号）");
                it.SubItems.Add("只能匿名单线程下载");
                lvAccounts.Items.Add(it);
            }
        }

        void SaveConfig()
        {
            if (_loading) return;           // 加载过程中不要写盘，否则会用空配置覆盖掉账号
            try
            {
                File.WriteAllText(_cfgPath, JsonSerializer.Serialize(new
                {
                    _说明 = "城通网盘账号池：每个账号 +2 线程，N 个账号 = 2N 线程，匿名 +1。密码明文存储。",
                    useAnonymous = chkAnon.Checked,
                    accounts = _cfg.accounts.Select(x => new { x.email, x.password }).ToList(),
                }, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            }
            catch (Exception ex) { lblStatus.Text = "保存配置失败：" + ex.Message; }
        }

        void AddAccount()
        {
            var em = txtEmail.Text.Trim();
            var pw = txtPass.Text;
            if (em.Length == 0 || pw.Length == 0) { MessageBox.Show(this, "账号和密码都要填", "提示"); return; }
            if (_cfg.accounts.Any(x => x.email == em)) { MessageBox.Show(this, "这个账号已经在列表里了", "提示"); return; }
            _cfg.accounts.Add(new Account { email = em, password = pw });
            txtEmail.Clear(); txtPass.Clear();
            RefreshAccounts(_cfg);
            SaveConfig();
            lblStatus.Text = "已添加账号：" + em;
        }

        void DelAccount()
        {
            if (lvAccounts.SelectedIndices.Count == 0) { MessageBox.Show(this, "先在列表里选中一行", "提示"); return; }
            int i = lvAccounts.SelectedIndices[0];
            if (i >= _cfg.accounts.Count) return;
            var em = _cfg.accounts[i].email;
            _cfg.accounts.RemoveAt(i);
            RefreshAccounts(_cfg);
            SaveConfig();
            lblStatus.Text = "已删除账号：" + em;
        }

        // ---------------- 解析 ----------------
        async Task DoResolve()
        {
            if (_busy) return;
            _resolved = null;
            _folder = null;
            btnDl.Enabled = false;
            lvSources.Items.Clear();
            lvFiles.Items.Clear();
            lblFile.Text = "解析中…";
            lblStats.Text = "正在读取分享信息…";
            btnResolve.Enabled = false;
            ShowFolderUi(false);
            try
            {
                _cfg.useAnonymous = chkAnon.Checked;
                var parsed = Ctfile.Parse(txtLink.Text);
                _parsed = parsed;

                if (parsed.Kind == 'd')
                {
                    // 文件夹分享
                    var fr = await Ctfile.ListFolderAsync(parsed, SetStatus, _cts == null ? default(CancellationToken) : _cts.Token);                    _folder = fr;
                    lblFile.Text = fr.FolderName + "    " + fr.Files.Count + " 个文件    " + Fmt(fr.TotalBytes);
                    foreach (var f in fr.Files)
                    {
                        var it = new ListViewItem(f.Name);
                        it.SubItems.Add(string.IsNullOrEmpty(f.SizeText) ? "-" : f.SizeText);
                        it.SubItems.Add(f.Date ?? "");
                        it.SubItems.Add(Path.GetDirectoryName(f.Path) ?? "");
                        it.Checked = true;
                        it.Tag = f;
                        lvFiles.Items.Add(it);
                    }
                    ShowFolderUi(true);
                    UpdateSelectedStats();
                    // 文件夹模式：用匿名槽给每个文件单独取链，来源表只显示匿名
                    var anon = new Source { Url = "", Threads = 1, Label = "逐个文件取链（匿名）", Kind = "anonymous", Host = "-", Spd = 0 };
                    var it2 = new ListViewItem(anon.Label);
                    it2.SubItems.Add("1");
                    it2.SubItems.Add("-");
                    it2.SubItems.Add("-");
                    lvSources.Items.Add(it2);
                    btnDl.Enabled = true;
                    lblStatus.Text = "解析成功";
                }
                else
                {
                    var r = await Ctfile.ResolveAsync(txtLink.Text, _cfg, SetStatus);
                    _resolved = r;
                    string est = Ctfile.EstimateTime(r.Sources, r.FileSize);
                    lblFile.Text = r.FileName + "    " + Fmt(r.FileSize) + "    合计 " + r.TotalThreads + " 线程并发"
                                 + (string.IsNullOrEmpty(est) ? "" : "    预计 " + est);
                    foreach (var s in r.Sources)
                    {
                        var it = new ListViewItem(s.Label);
                        it.SubItems.Add(s.Threads.ToString());
                        it.SubItems.Add(s.Host);
                        it.SubItems.Add(s.Spd > 0 ? (s.Spd / 1024) + " KB/s" : (s.Kind == "account" ? "账号" : "匿名"));
                        lvSources.Items.Add(it);
                    }
                    btnDl.Enabled = true;
                    lblStats.Text = r.Warnings.Count > 0 ? "⚠ " + string.Join("；", r.Warnings) : "就绪，点「开始下载」";
                    lblStatus.Text = "解析成功";
                }
            }
            catch (Exception ex)
            {
                lblFile.Text = "";
                lblStats.Text = "解析失败";
                // 把完整异常写到日志，方便定位（GUI 里只显示一行）
                try
                {
                    string logPath = Path.Combine(ExeDir(), "debug.log");
                    File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 解析失败\r\n" + ex.ToString() + "\r\n\r\n");
                    MessageBox.Show(this, ex.Message + "\n\n（完整堆栈已写入 " + logPath + "）", "解析失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch
                {
                    MessageBox.Show(this, ex.Message, "解析失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally { btnResolve.Enabled = true; }
        }

        void ShowFolderUi(bool show)
        {
            lblFiles.Visible = show;
            lvFiles.Visible = show;
            btnAll.Visible = show;
            btnNone.Visible = show;
            btnInvert.Visible = show;
            // 窗口尺寸固定，绝不在运行中改（会把控件顶出可见区域）
        }

        void UpdateSelectedStats()
        {
            if (_folder == null) return;
            int n = 0; long bytes = 0;
            foreach (ListViewItem it in lvFiles.Items)
            {
                if (!it.Checked) continue;
                var f = it.Tag as Ctfile.FolderFile;
                if (f == null) continue;
                n++; bytes += f.Size;
            }
            lblStats.Text = string.Format("已选 {0} / {1} 个文件，共 {2}", n, _folder.Files.Count, Fmt(bytes));
        }

        // ---------------- 下载 ----------------
        async Task DoDownload()
        {
            if (_busy) return;
            string dir = txtOutDir.Text.Trim();
            if (dir.Length == 0) { dir = DefaultDir(); txtOutDir.Text = dir; }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { MessageBox.Show(this, "保存目录不可用：" + ex.Message, "错误"); return; }

            if (_folder != null) { await DoDownloadFolder(dir); return; }
            if (_resolved == null) return;

            string name = SanitizeFileName(_resolved.FileName);
            string outPath = Path.Combine(dir, name);
            _cts = new CancellationTokenSource();
            _busy = true;
            btnDl.Enabled = false; btnResolve.Enabled = false; btnCancel.Visible = true;
            bar.Value = 0;
            var sw = Stopwatch.StartNew();

            var prog = new Progress<Tuple<long, long, double>>(t =>
            {
                long d = t.Item1, total = t.Item2; double sp = t.Item3;
                bar.Value = total > 0 ? (int)Math.Min(1000, d * 1000 / total) : 0;
                double pct = total > 0 ? d * 100.0 / total : 0;
                string eta = sp > 0.1 ? TimeSpan.FromSeconds((total - d) / sp).ToString(@"mm\:ss") : "--";
                lblStats.Text = string.Format("进度 {0:F1}%   {1} / {2}   {3}/s   剩余 {4}", pct, Fmt(d), Fmt(total), Fmt((long)sp), eta);
            });

            try
            {
                int th = await Ctfile.DownloadAsync(_resolved.Sources, outPath,
                    (d, total, sp) => ((IProgress<Tuple<long, long, double>>)prog).Report(Tuple.Create(d, total, sp)),
                    _cts.Token, SetStatus);
                bar.Value = 1000;
                var fi = new FileInfo(outPath);
                double secs = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                lblStats.Text = string.Format("✅ 完成：{0}  用时 {1:F1} 秒  平均 {2}/s  ({3} 线程)", Fmt(fi.Length), secs, Fmt((long)(fi.Length / secs)), th);
                if (MessageBox.Show(this, "下载完成：\n" + outPath + "\n\n要打开所在文件夹吗？", "完成",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    try { Process.Start("explorer", "/select,\"" + outPath + "\""); } catch { }
            }
            catch (OperationCanceledException)
            {
                lblStats.Text = "已取消";
            }
            catch (Exception ex)
            {
                lblStats.Text = "下载失败";
                MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                btnDl.Enabled = true; btnResolve.Enabled = true; btnCancel.Visible = false;
            }
        }

        /// <summary>文件夹批量下载：逐个取链 + 多来源并发，保留目录结构</summary>
        async Task DoDownloadFolder(string rootDir)
        {
            var picked = new List<Ctfile.FolderFile>();
            foreach (ListViewItem it in lvFiles.Items)
            {
                if (!it.Checked) continue;
                var f = it.Tag as Ctfile.FolderFile;
                if (f != null) picked.Add(f);
            }
            if (picked.Count == 0) { MessageBox.Show(this, "没有勾选任何文件", "提示"); return; }

            _cts = new CancellationTokenSource();
            _busy = true;
            btnDl.Enabled = false; btnResolve.Enabled = false; btnCancel.Visible = true;
            var sw = Stopwatch.StartNew();
            long totalBytes = 0;
            foreach (var f in picked) totalBytes += f.Size;
            long doneBytes = 0;
            int okCount = 0, failCount = 0;
            var failures = new List<string>();

            try
            {
                foreach (var f in picked)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    string rel = f.Path;
                    // 去掉首层文件夹名（分享根目录名），直接存到用户选的目录下
                    int slash = rel.IndexOf('/');
                    if (slash > 0) rel = rel.Substring(slash + 1);
                    string outPath = Path.Combine(rootDir, SanitizeFileName(rel));
                    try { Directory.CreateDirectory(Path.GetDirectoryName(outPath)); } catch { }

                    lblStats.Text = string.Format("[{0}/{1}] {2}", okCount + failCount + 1, picked.Count, f.Name);
                    bar.Value = totalBytes > 0 ? (int)Math.Min(1000, doneBytes * 1000 / totalBytes) : 0;

                    Source src;
                    try
                    {
                        src = await Ctfile.GetFileLinkAsync(f.FileCode, _parsed.PageUrl, SetStatus);
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        failures.Add(f.Name + "（取链失败：" + ex.Message + "）");
                        continue;
                    }

                    try
                    {
                        await Ctfile.DownloadAsync(new List<Source> { src }, outPath, (d, t2, sp) =>
                        {
                            long abs = doneBytes + d;
                            bar.Value = totalBytes > 0 ? (int)Math.Min(1000, abs * 1000 / totalBytes) : 0;
                            double pct = totalBytes > 0 ? abs * 100.0 / totalBytes : 0;
                            lblStats.Text = string.Format("[{0}/{1}] {2}   {3:F1}%   {4}/s", okCount + failCount + 1, picked.Count, f.Name, pct, Fmt((long)sp));
                        }, _cts.Token, SetStatus);
                        okCount++;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        failures.Add(f.Name + "（下载失败：" + ex.Message + "）");
                    }
                    doneBytes += f.Size;
                    bar.Value = totalBytes > 0 ? (int)Math.Min(1000, doneBytes * 1000 / totalBytes) : 0;
                }

                bar.Value = 1000;
                double secs = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                lblStats.Text = string.Format("✅ 完成：{0} 个成功，{1} 个失败   用时 {2:F1} 秒", okCount, failCount, secs);
                string msg = string.Format("成功 {0} 个，失败 {1} 个\n保存目录：{2}", okCount, failCount, rootDir);
                if (failures.Count > 0) msg += "\n\n失败列表：\n" + string.Join("\n", failures.GetRange(0, Math.Min(10, failures.Count)));
                if (MessageBox.Show(this, msg + "\n\n要打开所在文件夹吗？", "完成",
                        MessageBoxButtons.YesNo, failCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information) == DialogResult.Yes)
                    try { Process.Start("explorer", "\"" + rootDir + "\""); } catch { }
            }
            catch (OperationCanceledException)
            {
                lblStats.Text = "已取消";
            }
            catch (Exception ex)
            {
                lblStats.Text = "下载失败";
                MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                btnDl.Enabled = true; btnResolve.Enabled = true; btnCancel.Visible = false;
            }
        }

        // ---------------- 工具 ----------------
        // 解析在后台线程跑，回调要切回 UI 线程
        void SetStatus(string s)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(() => { if (!IsDisposed) lblStats.Text = s; }));
                else lblStats.Text = s;
            }
            catch { }
        }

        static string SanitizeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "ctfile-download.bin";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        static string Fmt(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1048576) return (n / 1024.0).ToString("F0") + " KB";
            if (n < 1073741824L) return (n / 1048576.0).ToString("F2") + " MB";
            return (n / 1073741824.0).ToString("F2") + " GB";
        }
    }
}
