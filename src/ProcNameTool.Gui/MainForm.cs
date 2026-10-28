using System.Diagnostics;
using System.Security.Principal;

namespace ProcNameTool.Gui;

public sealed class MainForm : Form
{
    // ── 控件 ────────────────────────────────────────────────
    private readonly TextBox _filter = new();
    private readonly CheckBox _onlyOperable = new();
    private readonly Button _btnRefresh = new();
    private readonly Button _btnElevate = new();
    private readonly ListView _list = new();
    private readonly GroupBox _listGroup = new();

    private readonly TextBox _newName = new();
    private readonly TextBox _newDesc = new();
    private readonly CheckBox _overwrite = new();
    private readonly CheckBox _keepAlive = new();
    private readonly Button _btnApply = new();
    private readonly Button _btnCopyOnly = new();
    private readonly Button _btnRestore = new();
    private readonly Button _btnOpenDir = new();

    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _watch = new();

    // ── 状态 ────────────────────────────────────────────────
    private List<ProcEntry> _all = new();
    private bool _rebuilding;
    private Process? _child;
    private ProcessStartInfo? _childPsi;
    private DateTime _lastRestart = DateTime.MinValue;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public MainForm()
    {
        Text = "进程名修改工具";
        // 以 96 DPI 为设计基准，交给 WinForms 按当前 DPI 等比放大，
        // 否则在 125% 缩放的高分屏上尺寸会算错（实测会缩到 0.8 倍）。
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        ClientSize = new Size(980, 720);
        MinimumSize = new Size(880, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        KeyPreview = true;

        BuildUi();

        _watch.Interval = 1000;
        _watch.Tick += OnWatchTick;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; RefreshProcessList(); }
        };

        Load += (_, _) =>
        {
            FitColumns();
            Log("就绪。在列表里选中要改名的进程 → 填新名字 → 点“生成副本并启动”。");
            Log($"当前权限：{(IsAdmin() ? "管理员" : "普通用户")}（改系统目录下的程序需要管理员）");
            RefreshProcessList();
        };

        FormClosing += (_, _) =>
        {
            _watch.Stop();
            _child?.Dispose();
        };
    }

    // ══════════════════════════════════════════════════════════
    //  界面搭建
    // ══════════════════════════════════════════════════════════

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 360));

        root.Controls.Add(BuildTopBar(), 0, 0);
        root.Controls.Add(BuildListGroup(), 0, 1);
        root.Controls.Add(BuildBottom(), 0, 2);

        Controls.Add(root);
    }

    private Control BuildTopBar()
    {
        // 行高交给内容自适应：写死高度在 125%/150% DPI 下会挤掉控件
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(0, 2, 0, 8)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lblFilter = new Label
        {
            Text = "筛选进程：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 4, 6)
        };

        _filter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _filter.PlaceholderText = "进程名 / 描述 / 路径关键字";
        _filter.Margin = new Padding(0, 4, 8, 4);
        _filter.TextChanged += (_, _) => ApplyFilter();

        _btnRefresh.Text = "刷新 (F5)";
        _btnRefresh.AutoSize = true;
        _btnRefresh.Anchor = AnchorStyles.Left;
        _btnRefresh.Padding = new Padding(8, 3, 8, 3);
        _btnRefresh.Margin = new Padding(0, 2, 14, 0);
        _btnRefresh.Click += (_, _) => RefreshProcessList();

        _onlyOperable.Text = "只显示能拿到路径的进程";
        _onlyOperable.Checked = true;
        _onlyOperable.AutoSize = true;
        _onlyOperable.Anchor = AnchorStyles.Left;
        _onlyOperable.Margin = new Padding(0, 6, 0, 0);
        _onlyOperable.CheckedChanged += (_, _) => ApplyFilter();

        _btnElevate.Text = IsAdmin() ? "已是管理员" : "以管理员身份重启";
        _btnElevate.AutoSize = true;
        _btnElevate.Enabled = !IsAdmin();
        _btnElevate.Padding = new Padding(8, 3, 8, 3);
        _btnElevate.Anchor = AnchorStyles.Right;
        _btnElevate.Margin = new Padding(0, 2, 0, 0);
        _btnElevate.Click += (_, _) => RestartElevated();

        bar.Controls.Add(lblFilter, 0, 0);
        bar.Controls.Add(_filter, 1, 0);
        bar.Controls.Add(_btnRefresh, 2, 0);
        bar.Controls.Add(_onlyOperable, 3, 0);
        bar.Controls.Add(_btnElevate, 4, 0);

        return bar;
    }

    private Control BuildListGroup()
    {
        _listGroup.Text = "目标进程";
        _listGroup.Dock = DockStyle.Fill;
        _listGroup.Padding = new Padding(8);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.GridLines = false;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Columns.Add("进程名", 190);
        _list.Columns.Add("PID", 70, HorizontalAlignment.Right);
        _list.Columns.Add("描述（任务管理器“名称”列）", 240);
        _list.Columns.Add("可执行文件路径", 430);
        _list.ClientSizeChanged += (_, _) => FitColumns();
        _list.SelectedIndexChanged += OnSelectionChanged;
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0) { _newName.Focus(); _newName.SelectAll(); }
        };

        _listGroup.Controls.Add(_list);
        return _listGroup;
    }

    private Control BuildBottom()
    {
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 8, 0, 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        bottom.Controls.Add(BuildSettings(), 0, 0);
        bottom.Controls.Add(BuildLogGroup(), 0, 1);
        return bottom;
    }

    private Control BuildSettings()
    {
        var group = new GroupBox
        {
            Text = "改名设置",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8)
        };

        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 4
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lbl1 = new Label { Text = "新进程名", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };
        var lbl2 = new Label { Text = "新显示描述", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };

        _newName.Dock = DockStyle.Fill;
        _newName.PlaceholderText = "不填 .exe，例如 AgentProbe（这就是新文件名 / 映像名）";
        _newName.Margin = new Padding(0, 3, 0, 3);

        _newDesc.Dock = DockStyle.Fill;
        _newDesc.PlaceholderText = "任务管理器“名称”列显示的文字，留空则与新进程名相同";
        _newDesc.Margin = new Padding(0, 3, 0, 3);

        var flags = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        _overwrite.Text = "允许覆盖同名文件";
        _overwrite.AutoSize = true;
        _overwrite.Margin = new Padding(0, 0, 18, 0);
        _keepAlive.Text = "保活：程序退出后自动重启";
        _keepAlive.AutoSize = true;
        flags.Controls.Add(_overwrite);
        flags.Controls.Add(_keepAlive);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };

        StylePrimary(_btnApply, "生成副本并启动");
        _btnApply.Click += async (_, _) => await RunAsync(startAfter: true);

        Style(_btnCopyOnly, "只生成副本（不启动）");
        _btnCopyOnly.Click += async (_, _) => await RunAsync(startAfter: false);

        Style(_btnRestore, "从 .bak 恢复");
        _btnRestore.Click += (_, _) => RestoreFromBackup();

        Style(_btnOpenDir, "打开所在目录");
        _btnOpenDir.Click += (_, _) => OpenSelectedFolder();

        buttons.Controls.AddRange(new Control[] { _btnApply, _btnCopyOnly, _btnRestore, _btnOpenDir });

        t.Controls.Add(lbl1, 0, 0);
        t.Controls.Add(_newName, 1, 0);
        t.Controls.Add(lbl2, 0, 1);
        t.Controls.Add(_newDesc, 1, 1);
        t.Controls.Add(flags, 0, 2);
        t.SetColumnSpan(flags, 2);
        t.Controls.Add(buttons, 0, 3);
        t.SetColumnSpan(buttons, 2);

        group.Controls.Add(t);
        return group;
    }

    private Control BuildLogGroup()
    {
        var group = new GroupBox
        {
            Text = "执行日志",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.HideSelection = true;
        _log.BackColor = Color.White;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9F);
        _log.WordWrap = true;
        _log.ScrollBars = ScrollBars.Vertical;

        group.Controls.Add(_log);
        return group;
    }

    private static void Style(Button b, string text)
    {
        b.Text = text;
        b.AutoSize = true;
        b.Padding = new Padding(12, 5, 12, 5);
        b.Margin = new Padding(0, 0, 8, 0);
        b.FlatStyle = FlatStyle.System;
    }

    private static void StylePrimary(Button b, string text)
    {
        Style(b, text);
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Color.FromArgb(0, 120, 215);
        b.ForeColor = Color.White;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(0, 90, 170);
        b.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
    }

    // ══════════════════════════════════════════════════════════
    //  进程列表
    // ══════════════════════════════════════════════════════════

    private void RefreshProcessList()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            _all = ProcessScanner.Scan();
        }
        catch (Exception ex)
        {
            Log($"[失败] 枚举进程：{ex.Message}");
            return;
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        ApplyFilter();
        Log($"已刷新，捕获 {_all.Count} 个进程。");
    }

    private void ApplyFilter()
    {
        string q = _filter.Text.Trim();
        bool onlyOps = _onlyOperable.Checked;
        int? keepPid = SelectedRow()?.Pid;

        _rebuilding = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            int shown = 0;
            ListViewItem? reselect = null;

            foreach (var r in _all)
            {
                if (onlyOps && r.Path == null) continue;
                if (q.Length > 0 && !Matches(r, q)) continue;

                var item = new ListViewItem(r.Name) { Tag = r };
                item.SubItems.Add(r.Pid.ToString());
                item.SubItems.Add(r.Description);
                item.SubItems.Add(r.Path ?? "（无法访问，需要管理员权限）");
                _list.Items.Add(item);
                shown++;

                if (keepPid.HasValue && r.Pid == keepPid.Value) reselect = item;
            }

            // 刷新后尽量保持原来选中的那一行，避免用户重新找
            if (reselect != null)
            {
                reselect.Selected = true;
                reselect.Focused = true;
                _list.EnsureVisible(reselect.Index);
            }

            _listGroup.Text = $"目标进程（共 {_all.Count} 个，当前显示 {shown} 个；选中一行后填写新名字）";
        }
        finally
        {
            _list.EndUpdate();
            _rebuilding = false;
        }
    }

    /// <summary>按列表当前宽度分配列宽，避免路径列被截断或出现横向滚动条。</summary>
    private void FitColumns()
    {
        // 预留垂直滚动条宽度，否则列宽刚好等于可视宽度时会多出一条横向滚动条
        int total = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
        if (total < 200) return;

        int pid = 64;
        int name = Math.Max(120, (int)(total * 0.22));
        int desc = Math.Max(150, (int)(total * 0.25));
        int path = total - pid - name - desc;
        if (path < 140)
        {
            path = 140;
            desc = Math.Max(120, total - pid - name - path);
        }

        _list.Columns[0].Width = name;
        _list.Columns[1].Width = pid;
        _list.Columns[2].Width = desc;
        _list.Columns[3].Width = path;
    }

    private static bool Matches(ProcEntry r, string q) =>        r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        (r.Path?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
        r.Pid.ToString() == q;

    private ProcEntry? SelectedRow() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ProcEntry : null;

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_rebuilding) return;

        var row = SelectedRow();
        if (row == null) return;

        _newName.Text = row.Name;
        _newDesc.Text = row.Description;
    }

    // ══════════════════════════════════════════════════════════
    //  核心流程
    // ══════════════════════════════════════════════════════════

    private async Task RunAsync(bool startAfter)
    {
        var row = SelectedRow();
        if (row == null)
        {
            MessageBox.Show(this, "请先在上面的列表里选中要改名的进程。", "还没选进程",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (row.Path == null)
        {
            AskElevate($"拿不到 {row.Name} (PID {row.Pid}) 的 exe 路径，通常是该进程以更高权限运行。\n\n要以管理员身份重新启动本工具吗？");
            return;
        }

        string newName = _newName.Text.Trim();
        if (!ValidateName(newName, out string error))
        {
            MessageBox.Show(this, error, "新进程名不合法", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _newName.Focus();
            return;
        }

        string originalBase = Path.GetFileNameWithoutExtension(row.Path);
        bool sameName = string.Equals(newName, originalBase, StringComparison.OrdinalIgnoreCase);
        string dir = Path.GetDirectoryName(row.Path)!;
        string dest = sameName ? row.Path : Path.Combine(dir, newName + ".exe");

        string newDesc = _newDesc.Text.Trim();
        if (newDesc.Length == 0) newDesc = newName;

        if (sameName)
        {
            var r = MessageBox.Show(this,
                $"新进程名与原名相同，将直接改写原文件：\n{dest}\n\n" +
                "影响范围：\n" +
                "  · 该文件的数字签名会失效（若有）\n" +
                "  · 只改不改名，原文件必须没在运行，否则会被占用\n" +
                "  · 只能靠自动生成的 .bak 回滚\n\n" +
                "如果只是想换个显示名，建议把“新进程名”也改掉（那样会生成副本，原文件不动）。\n\n确定继续吗？",
                "风险确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.OK) return;
        }
        else if (File.Exists(dest) && !_overwrite.Checked)
        {
            MessageBox.Show(this,
                $"目标文件已存在：\n{dest}\n\n如果确认覆盖，请勾选“允许覆盖同名文件”。",
                "目标已存在", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool killOriginal = false;
        if (startAfter)
        {
            var ans = MessageBox.Show(this,
                $"是否结束原进程 {row.Name} (PID {row.Pid})？\n\n" +
                "【是】先结束原进程，再用新名字启动 —— 推荐，避免双实例抢端口 / 抢文件锁\n" +
                "【否】保留原进程，另外启动一份新名字的实例\n" +
                "【取消】撤回，什么都不做\n\n" +
                "注意：强制结束进程可能导致未保存数据丢失；若这是生产服务，请确认当前处于变更窗口内。",
                "结束原进程？", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (ans == DialogResult.Cancel) return;
            killOriginal = ans == DialogResult.Yes;
        }

        bool overwrite = _overwrite.Checked;
        SetBusy(true);
        try
        {
            await Task.Run(() =>
            {
                if (sameName)
                {
                    string bak = dest + ".bak";
                    if (!File.Exists(bak))
                    {
                        File.Copy(dest, bak);
                        Log($"已备份：{bak}");
                    }
                    else
                    {
                        Log($"备份已存在，保留不动：{bak}");
                    }
                }
                else
                {
                    Log($"复制：{row.Path}");
                    Log($"   ->  {dest}");
                    File.Copy(row.Path, dest, overwrite);
                }

                Log($"改写版本信息（FileDescription = \"{newDesc}\"）");
                var changes = VersionResource.Patch(
                    dest,
                    new Dictionary<string, string> { ["FileDescription"] = newDesc },
                    dryRun: false);

                if (changes.Count == 0)
                    Log("   （描述与原值一致，未发生改写）");
                foreach (var c in changes)
                    Log($"   [{c.Table}] {c.Key}: \"{c.Old}\" -> \"{c.New}\"");
            });

            ProcessScanner.ForgetCached(dest);

            if (!startAfter)
            {
                Log("完成：副本已生成，未启动。");
                return;
            }

            if (killOriginal) KillProcess(row.Pid, row.Name);

            StartChild(dest, dir, newDesc);
        }
        catch (UnauthorizedAccessException)
        {
            Log("[失败] 目标目录无写入权限。");
            AskElevate("写入被拒绝，需要管理员权限。\n\n要重启本工具并以管理员身份运行吗？");
        }
        catch (IOException ex)
        {
            Log($"[失败] {ex.Message}");
            MessageBox.Show(this,
                $"{ex.Message}\n\n常见原因：目标文件正在被占用（先结束进程再试），或磁盘只读。",
                "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            Log($"[失败] {ex.Message}");
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshProcessList();   // 重新扫描：原进程已结束、新进程已起来，列表要跟上
        }
    }

    private void KillProcess(int pid, string name)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();

            if (p.WaitForExit(5000))
                Log($"已结束原进程 {name} (PID {pid})。");
            else
                Log($"[警告] PID {pid} 在 5 秒内未退出，仍尝试启动新实例。");
        }
        catch (ArgumentException)
        {
            Log("原进程已不存在，跳过结束步骤。");
        }
        catch (Exception ex)
        {
            Log($"[警告] 结束原进程失败：{ex.Message}（可能需要管理员权限）");
        }
    }

    private void StartChild(string exe, string workDir, string displayName)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = true      // 交给系统决定：控制台程序会得到控制台窗口，GUI 程序正常显示
        };

        try
        {
            _child?.Dispose();
            _child = Process.Start(psi);
            _childPsi = psi;
            _lastRestart = DateTime.MinValue;

            Log($"已启动：{exe}");
            if (_child != null) Log($"   PID = {_child.Id}");
            Log($"   任务管理器“详细信息”页 名称 = {Path.GetFileName(exe)}");
            Log($"   任务管理器“进程”页     名称 = {displayName}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            Log("[失败] 目标程序要求管理员权限，请以管理员身份重启本工具后再试。");
            AskElevate("目标程序要求以管理员身份运行（错误 740）。\n\n要重启本工具并以管理员身份运行吗？");
        }
        catch (Exception ex)
        {
            Log($"[失败] 启动新进程：{ex.Message}");
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnWatchTick(object? sender, EventArgs e)
    {
        if (_child == null || _childPsi == null) return;

        bool exited;
        try { exited = _child.HasExited; }
        catch { exited = true; }

        if (!exited) return;

        if (!_keepAlive.Checked)
        {
            _child.Dispose();
            _child = null;
            return;
        }

        if ((DateTime.Now - _lastRestart).TotalSeconds < 3) return;
        _lastRestart = DateTime.Now;

        try
        {
            _child.Dispose();
            _child = Process.Start(_childPsi);
            Log($"[保活] 子进程已退出，已重启 PID={_child?.Id}");
        }
        catch (Exception ex)
        {
            Log($"[保活] 重启失败：{ex.Message}");
            _child = null;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  辅助操作
    // ══════════════════════════════════════════════════════════

    private void RestoreFromBackup()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择要恢复的 exe（需要存在同名 .bak 备份）",
            Filter = "可执行文件|*.exe|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string file = dlg.FileName;
        string bak = file + ".bak";

        if (!File.Exists(bak))
        {
            MessageBox.Show(this, $"找不到备份文件：\n{bak}\n\n本工具只恢复由它自己生成的 .bak。",
                "没有备份", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"将用备份覆盖：\n{file}\n\n来源：{bak}\n\n覆盖前请确认该程序没有在运行。继续吗？",
            "确认恢复", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK) return;

        try
        {
            File.Copy(bak, file, overwrite: true);
            ProcessScanner.ForgetCached(file);
            Log($"已从备份恢复：{file}");
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log($"[失败] 恢复：{ex.Message}");
            MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedFolder()
    {
        var row = SelectedRow();
        if (row?.Path == null)
        {
            MessageBox.Show(this, "请先选中一个能拿到路径的进程。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start("explorer.exe", $"/select,\"{row.Path}\"");
        }
        catch (Exception ex)
        {
            Log($"[失败] 打开目录：{ex.Message}");
        }
    }

    private bool ValidateName(string name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "新进程名不能为空。";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('\\') || name.Contains('/'))
        {
            error = "新进程名不能包含 \\ / : * ? \" < > | 等字符。";
            return false;
        }

        if (name.Contains('.'))
        {
            error = "只填不带扩展名的基名，例如 AgentProbe，不要写成 AgentProbe.exe。";
            return false;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "文件名不能以空格或点结尾。";
            return false;
        }

        if (name.Length > 120)
        {
            error = "名字太长（超过 120 字符）。";
            return false;
        }

        if (ReservedNames.Contains(name))
        {
            error = $"“{name}” 是 Windows 保留设备名，不能用作文件名。";
            return false;
        }

        return true;
    }

    private void SetBusy(bool busy)
    {
        _btnApply.Enabled = !busy;
        _btnCopyOnly.Enabled = !busy;
        _btnRestore.Enabled = !busy;
        _btnRefresh.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.ScrollToCaret();
    }

    private static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void AskElevate(string message)
    {
        if (IsAdmin())
        {
            MessageBox.Show(this, message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var r = MessageBox.Show(this, message, "需要管理员权限",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) RestartElevated();
    }

    private void RestartElevated()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
        }
        catch
        {
            return;   // 用户在 UAC 弹窗点了取消
        }

        Application.Exit();
    }
}
