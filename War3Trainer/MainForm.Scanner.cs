using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace War3Trainer
{
    /// <summary>
    /// MainForm 的「内存搜索 / 地址收藏」扩展部分。
    ///
    /// 设计要点：
    ///  · 不改动原有 designer/resx：现有 splitMain 在构造时被移入第一个页签「功能修改」，
    ///    新增两个页签的控件全部用代码创建。
    ///  · 主区域用「固定定位 + Anchor」而不是 Dock，避免 WinForms 停靠顺序带来的布局不确定性。
    ///  · 地址收藏保存为「模块基址 + 偏移 + 指针链」，因此游戏重启后依然可用。
    /// </summary>
    public partial class MainForm
    {
        private const int MaxDisplayRows = 2000;   // 结果列表最多显示多少行（ListView 撑不住上百万行）

        private TabControl _tabsMain;
        private TabPage _pageTree;
        private TabPage _pageScan;
        private TabPage _pageBook;

        // ---- 内存搜索页 ----
        private ComboBox _cmbScanType;
        private TextBox _txtScanValue;
        private TextBox _txtScanScale;
        private TextBox _txtScanTolerance;
        private ComboBox _cmbScanCompare;
        private CheckBox _chkScanModuleOnly;
        private CheckBox _chkScanAutoRefresh;
        private Button _btnFirstScan;
        private Button _btnNextScan;
        private Button _btnScanClear;
        private Button _btnScanRefresh;
        private Button _btnScanAddToBook;
        private Button _btnScanPointer;
        private Button _btnScanKeyLocate;
        private Button _btnDump;
        private Button _btnScanWrite;
        private TextBox _txtScanWriteValue;
        private ListView _lvScan;
        private Label _labScanStatus;
        private Label _labScanHint;

        // ---- 地址收藏页 ----
        private ListView _lvBook;
        private TextBox _txtBookWriteValue;
        private Label _labBookStatus;
        private Button _btnBookAdd;
        private Button _btnBookEdit;
        private Button _btnBookDelete;
        private Button _btnBookWrite;
        private Button _btnBookSave;
        private Button _btnBookReload;
        private Button _btnBookPointer;
        private Button _btnBookRelocate;
        private Button _btnBookCleanup;
        private int _lastRelocatedPid;

        // ---- 定时重写（功能修改 + 地址收藏共用一套开关/间隔）----
        private RewriteEngine _rewrite;
        private Timer _timerRewrite;
        private ToolStripButton _btnRewriteToggle;
        private ToolStripComboBox _cmbRewriteInterval;
        private ToolStripLabel _labRewriteState;
        private bool _suppressRewrite;

        /// <summary>_mainTrainer 是按哪个进程算出来的地址；进程变了就不能再拿它写内存。</summary>
        private int _trainerPid;

        // ---- 运行状态 ----
        private MemoryScanner _scanner;
        private AddressBook _book;
        private readonly ModuleBaseCache _moduleCache = new ModuleBaseCache();
        private Timer _timerRefresh;
        private int _scannerProcessId;
        private bool _suppressTimer;

        /// <summary>扫描进行中：防止进度回调 pump 消息时又进来触发第二次扫描。</summary>
        private bool _scanBusy;

        /// <summary>上次画扫描进度的时刻（限流，避免每个 1MB 区块都刷新界面）。</summary>
        private DateTime _lastScanProgressPaint;

        //////////////////////////////////////////////////////////////////////
        // 页签搭建
        //////////////////////////////////////////////////////////////////////

        private void BuildMainTabs()
        {
            BuildMainTabs(0);
        }

        private void BuildMainTabs(int startupTabIndex)
        {
            // 窗口要装得下搜索结果列表，改成可调整大小
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.ClientSize = new Size(1000, 760);
            this.MinimumSize = new Size(880, 600);

            // 把原有的功能树区域移入“功能修改”页签
            this.Controls.Remove(this.splitMain);
            this.splitMain.Dock = DockStyle.Fill;

            _pageTree = new TabPage("功能修改");
            _pageTree.Padding = new Padding(0);
            _pageTree.Controls.Add(this.splitMain);
            BuildTrainerRewriteUi();

            _pageScan = new TabPage("内存搜索");
            BuildScannerTab(_pageScan);

            _pageBook = new TabPage("地址收藏");
            BuildBookTab(_pageBook);

            _tabsMain = new TabControl();
            _tabsMain.Font = new Font("微软雅黑", 9F);
            _tabsMain.TabPages.Add(_pageTree);
            _tabsMain.TabPages.Add(_pageScan);
            _tabsMain.TabPages.Add(_pageBook);
            _tabsMain.SelectedIndexChanged += delegate { OnTabChanged(); };

            this.Controls.Add(_tabsMain);

            // ---- 顶部区域改成普通停靠布局 ----
            // 原来工具栏被放在固定 27px 高的 ContentPanel 里，且 strip 宽度被设计器锁死在 686：
            // 一旦状态栏文字变长（识别到游戏时会显示“游戏已运行(PID)，版本：……（支持）”），
            // 工具栏放不下就会换行，溢出到菜单栏上造成重叠。
            // 现在把两个 strip 从 ToolStripContainer 里取出来，用标准 Dock 布局：
            //   先加填充控件，再加顶部控件（停靠按加入的逆序结算 → 菜单在最上、工具栏次之）。
            this.toolContainer.ContentPanel.Controls.Remove(this.toolStripMain);
            this.toolContainer.TopToolStripPanel.Controls.Remove(this.menuMain);
            this.Controls.Remove(this.toolContainer);
            this.toolContainer.Dispose();

            this.menuMain.GripStyle = ToolStripGripStyle.Hidden;
            this.menuMain.Dock = DockStyle.Top;
            this.toolStripMain.Dock = DockStyle.Top;
            _tabsMain.Dock = DockStyle.Fill;

            _tabsMain.Location = new Point(0, 0);
            this.Controls.Add(_tabsMain);
            this.Controls.Add(this.toolStripMain);
            this.Controls.Add(this.menuMain);
            this.MainMenuStrip = this.menuMain;

            // 在“其他”菜单里加入页签切换入口
            AddToolsMenu();

            // 定时重写：开关和间隔跟着收藏夹放在同一个目录，下次启动继续用
            bool rewriteEnabled;
            int rewriteInterval;
            RewriteSettings.Load(out rewriteEnabled, out rewriteInterval);
            _rewrite = new RewriteEngine();
            _rewrite.Enabled = rewriteEnabled;
            _rewrite.IntervalMs = rewriteInterval;
            BuildRewriteControls();
            StartRewriteTimer();

            // 收藏夹定时刷新（列表数值 + 状态提示；“定时重写”由 _timerRewrite 单独负责）
            _timerRefresh = new Timer();
            _timerRefresh.Interval = 1000;
            _timerRefresh.Tick += delegate { OnRefreshTick(); };
            _timerRefresh.Enabled = true;

            _book = new AddressBook();
            LoadAddressBook(false);

            if (startupTabIndex > 0 && startupTabIndex < _tabsMain.TabPages.Count)
                _tabsMain.SelectedIndex = startupTabIndex;
        }

        private void OnTabChanged()
        {
            if (_tabsMain.SelectedTab == _pageBook)
                RefreshBookValues(true);
            else if (_tabsMain.SelectedTab == _pageScan)
                RefreshScanValues();
        }

        private void AddToolsMenu()
        {
            ToolStripMenuItem menuTools = new ToolStripMenuItem("工具(&T)");

            ToolStripMenuItem scan = new ToolStripMenuItem("内存搜索(&S)");
            scan.Click += delegate { _tabsMain.SelectedTab = _pageScan; };
            menuTools.DropDownItems.Add(scan);

            ToolStripMenuItem book = new ToolStripMenuItem("地址收藏(&B)");
            book.Click += delegate { _tabsMain.SelectedTab = _pageBook; };
            menuTools.DropDownItems.Add(book);

            ToolStripMenuItem dump = new ToolStripMenuItem("导出内存(&D)…");
            dump.Click += delegate { RunMemoryDump(); };
            menuTools.DropDownItems.Add(dump);

            menuTools.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem openFolder = new ToolStripMenuItem("打开收藏文件位置(&O)");
            openFolder.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + AddressBook.DefaultPath() + "\""); }
                catch { }
            };
            menuTools.DropDownItems.Add(openFolder);

            this.menuMain.Items.Add(menuTools);
        }

        //////////////////////////////////////////////////////////////////////
        // 定时重写（开关 / 间隔 / 执行）
        //
        // 背景：地图脚本（触发器）每帧都在重算金币、属性、人口这些东西，
        // 「功能修改」页点一次“修改(F6)”只写一次，过几秒就被脚本改回去；
        // 「地址收藏」页原来靠每秒一次的“锁定”压住。
        // 现在两处共用工具栏上的这一个开关和间隔，统一由 _timerRewrite 驱动。
        //////////////////////////////////////////////////////////////////////

        private void BuildRewriteControls()
        {
            if (toolStripMain == null || _rewrite == null) return;

            // 本页新增的按钮/下拉都带 ToolTipText，默认 ToolStrip 不显示，这里打开
            toolStripMain.ShowItemToolTips = true;

            toolStripMain.Items.Add(new ToolStripSeparator());

            _btnRewriteToggle = new ToolStripButton("定时重写");
            _btnRewriteToggle.CheckOnClick = true;
            _btnRewriteToggle.Checked = _rewrite.Enabled;
            _btnRewriteToggle.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnRewriteToggle.ToolTipText =
                "勾选后按右边的间隔不断把数值重新写回内存：" + Environment.NewLine
                + "· 功能修改页：点「修改(F6)」写入过的行" + Environment.NewLine
                + "· 地址收藏页：勾了「定时重写」的条目" + Environment.NewLine
                + "地图脚本会重算数值，就靠它压住。";
            _btnRewriteToggle.CheckedChanged += delegate
            {
                _rewrite.Enabled = _btnRewriteToggle.Checked;
                RewriteSettings.Save(_rewrite.Enabled, _rewrite.IntervalMs);
                StartRewriteTimer();
                UpdateRewriteState();
            };
            toolStripMain.Items.Add(_btnRewriteToggle);

            toolStripMain.Items.Add(new ToolStripLabel("间隔"));
            _cmbRewriteInterval = new ToolStripComboBox();
            _cmbRewriteInterval.DropDownStyle = ComboBoxStyle.DropDown;
            _cmbRewriteInterval.AutoSize = false;
            _cmbRewriteInterval.Width = 80;
            _cmbRewriteInterval.Items.AddRange(RewriteInterval.Presets);
            _cmbRewriteInterval.Text = RewriteInterval.Format(_rewrite.IntervalMs);
            _cmbRewriteInterval.ToolTipText = "重写间隔，可直接手填：500 ms、0.5s、1 秒（"
                + RewriteInterval.MinMs + "~" + RewriteInterval.MaxMs + " 毫秒）";
            _cmbRewriteInterval.TextChanged += delegate { ApplyRewriteIntervalFromUi(); };
            toolStripMain.Items.Add(_cmbRewriteInterval);

            _labRewriteState = new ToolStripLabel("");
            _labRewriteState.ForeColor = Color.DimGray;
            toolStripMain.Items.Add(_labRewriteState);

            UpdateRewriteState();
        }

        /// <summary>界面上的间隔改动（下拉或手填）落到引擎 + 定时器 + 配置文件。</summary>
        private void ApplyRewriteIntervalFromUi()
        {
            if (_cmbRewriteInterval == null || _rewrite == null) return;

            int ms;
            if (!RewriteInterval.TryParse(_cmbRewriteInterval.Text, out ms))
            {
                if (_labRewriteState != null)
                    _labRewriteState.Text = "间隔看不懂：" + _cmbRewriteInterval.Text;
                return;
            }

            _rewrite.IntervalMs = ms;
            RewriteSettings.Save(_rewrite.Enabled, _rewrite.IntervalMs);
            StartRewriteTimer();
            UpdateRewriteState();
        }

        /// <summary>按当前开关/间隔启停重写定时器（关掉时一个字节都不写）。</summary>
        private void StartRewriteTimer()
        {
            if (_timerRewrite == null)
            {
                _timerRewrite = new Timer();
                _timerRewrite.Tick += delegate { OnRewriteTick(); };
            }

            _timerRewrite.Stop();
            if (_rewrite == null || !_rewrite.Enabled) return;

            int interval = _rewrite.IntervalMs;
            if (interval < RewriteInterval.MinMs) interval = RewriteInterval.MinMs;
            if (interval > RewriteInterval.MaxMs) interval = RewriteInterval.MaxMs;

            _timerRewrite.Interval = interval;
            _timerRewrite.Start();
        }

        /// <summary>一个重写周期：功能修改行的最新地址 + 收藏夹里勾了重写的条目。</summary>
        private void OnRewriteTick()
        {
            if (_suppressRewrite || _rewrite == null || !_rewrite.Enabled) return;

            int pid = CurrentPid();
            if (pid <= 0)
            {
                UpdateRewriteState();
                return;
            }

            try
            {
                _suppressRewrite = true;
                RewriteResult result = _rewrite.RunOnce(
                    pid, ResolveTrainerTaskAddress, _book.Items, ResolveBookAddress);
                UpdateRewriteState(result);

                // 「功能修改」页正显示时顺手刷新原值列，让用户看见数值确实被压住了
                if (_tabsMain != null && _tabsMain.SelectedTab == _pageTree)
                    RefreshTrainerValues();
            }
            catch
            {
                // 单轮失败不打扰用户（游戏可能刚好退出），下个周期再试
            }
            finally
            {
                _suppressRewrite = false;
            }
        }

        /// <summary>立刻重写一轮（右键“加入定时重写”后马上生效，不用等定时器）。</summary>
        private void ApplyRewriteNow()
        {
            if (_rewrite == null || !_rewrite.Enabled) return;

            int pid = CurrentPid();
            if (pid <= 0) return;

            try
            {
                RewriteResult result = _rewrite.RunOnce(
                    pid, ResolveTrainerTaskAddress, _book.Items, ResolveBookAddress);
                UpdateRewriteState(result);
            }
            catch
            {
            }
        }

        private void UpdateRewriteState()
        {
            UpdateRewriteState(null);
        }

        private void UpdateRewriteState(RewriteResult last)
        {
            if (_labRewriteState == null || _rewrite == null) return;

            if (!_rewrite.Enabled)
            {
                _labRewriteState.Text = "重写：关";
                _labRewriteState.ForeColor = Color.DimGray;
                _labRewriteState.ToolTipText = "定时重写已关闭：点左边的「定时重写」按钮打开";
                return;
            }

            int trainerCount = _rewrite.TrainerTasks.Count;
            int bookCount = _book == null ? 0 : _rewrite.CountBookTasks(_book.Items);
            int total = trainerCount + bookCount;

            string text = total == 0
                ? "重写：无条目"
                : string.Format("重写：{0} 条 @{1}", total, RewriteInterval.Format(_rewrite.IntervalMs));
            if (last != null && last.Failed > 0) text += string.Format("（失败 {0}）", last.Failed);

            _labRewriteState.Text = text;
            _labRewriteState.ForeColor = total == 0 ? Color.DimGray : Color.SeaGreen;

            string tip = string.Format(
                "定时重写：开" + Environment.NewLine
                + "间隔：{0}" + Environment.NewLine
                + "功能修改行：{1} 条" + Environment.NewLine
                + "收藏条目：{2} 条",
                RewriteInterval.Format(_rewrite.IntervalMs), trainerCount, bookCount);
            if (last != null && last.Failed > 0 && !string.IsNullOrEmpty(last.FirstError))
                tip += Environment.NewLine + "上轮失败：" + last.FirstError;
            _labRewriteState.ToolTipText = tip;
        }

        /// <summary>
        /// 功能修改行的地址解析：按「节点 + 标题 + 同名序号」回头看 GameTrainer 的最新地址。
        /// 这样游戏重开、重新“刷新(F5)”之后自动对上，不用重新登记。
        /// </summary>
        private uint ResolveTrainerTaskAddress(TrainerRewriteTask task)
        {
            if (task == null) return 0;

            task.LastError = null;
            if (_mainTrainer == null || _currentGameContext == null)
            {
                task.LastError = "未识别到游戏（点工具栏「查找游戏」）";
                return 0;
            }
            if (_trainerPid != _currentGameContext.ProcessId)
            {
                task.LastError = "游戏进程已变化，请点「查找游戏」重新识别后再「修改(F6)」";
                return 0;
            }

            int seen = 0;
            foreach (IAddressNode line in _mainTrainer.GetAddressList())
            {
                if (line == null) continue;
                if (line.ParentIndex != task.ParentIndex) continue;
                if (line.Caption != task.Caption) continue;
                if (seen == task.Occurrence) return line.Address;
                seen++;
            }

            task.LastError = "这一行在当前游戏里找不到了（换了一局请重新「修改(F6)」）";
            return 0;
        }

        private uint ResolveBookAddress(SavedAddress item)
        {
            if (item == null) return 0;

            string error;
            return AddressBook.Resolve(item, CurrentPid(), _moduleCache, out error);
        }

        //////////////////////////////////////////////////////////////////////
        // 定时重写：「功能修改」页的界面与登记
        //////////////////////////////////////////////////////////////////////

        private void BuildTrainerRewriteUi()
        {
            // 前三列（名称 / 数值 / 修改）在 Designer 里定义，这里补一列
            this.viewData.Columns.Add("定时重写", 110);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("把「修改」列的值加入定时重写(&R)", null,
                delegate { AddSelectedRowToRewrite(); });
            menu.Items.Add("把这一行移出定时重写(&T)", null,
                delegate { RemoveSelectedRowFromRewrite(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清除全部定时重写(&C)", null,
                delegate { ClearTrainerRewriteTasks(); });
            menu.Items.Add("刷新数值(&F)", null, delegate { RefreshTrainerValues(); });
            this.viewData.ContextMenuStrip = menu;
        }

        /// <summary>「修改(F6)」写成功后登记这一行；定时重写关着时不登记（保持原来的“只写一次”）。</summary>
        private bool RegisterTrainerRewrite(ListViewItem row, IAddressNode addressLine, string valueText)
        {
            if (_rewrite == null || !_rewrite.Enabled) return false;
            if (row == null || addressLine == null) return false;
            if (string.IsNullOrEmpty(valueText)) return false;

            ScanValueType type = ScanTypeOf(addressLine.ValueType);
            uint raw;
            if (!MemoryScanner.TryParseScaledValue(type, valueText, addressLine.ValueScale, out raw))
                return false;

            _rewrite.AddOrUpdate(
                addressLine.ParentIndex,
                OwnerNameOf(addressLine.ParentIndex),
                addressLine.Caption,
                CountRowOccurrence(addressLine),
                type, addressLine.ValueScale, raw, valueText.Trim());

            UpdateRewriteState();
            return true;
        }

        /// <summary>「功能修改」页的类型枚举换算成统一的数值类型（内存搜索/收藏页用的是后者）。</summary>
        private static ScanValueType ScanTypeOf(AddressListValueType type)
        {
            switch (type)
            {
                case AddressListValueType.Float: return ScanValueType.Float;
                case AddressListValueType.Char4: return ScanValueType.Char4;
                default: return ScanValueType.Int32;
            }
        }

        /// <summary>同一功能节点下同名行的序号（标题重复时用来区分到底是哪一行）。</summary>
        private int CountRowOccurrence(IAddressNode target)
        {
            if (target == null || _mainTrainer == null) return 0;

            int occurrence = 0;
            foreach (IAddressNode line in _mainTrainer.GetAddressList())
            {
                if (line == null) continue;
                if (line.ParentIndex != target.ParentIndex) continue;
                if (line.Caption != target.Caption) continue;
                if (ReferenceEquals(line, target)) return occurrence;
                occurrence++;
            }
            return occurrence;
        }

        private string OwnerNameOf(int parentIndex)
        {
            if (_mainTrainer == null) return "";

            foreach (ITrainerNode node in _mainTrainer.GetFunctionList())
            {
                if (node != null && node.NodeIndex == parentIndex) return node.NodeTypeName;
            }
            return "";
        }

        private TrainerRewriteTask FindTrainerTask(IAddressNode addressLine)
        {
            if (_rewrite == null || addressLine == null) return null;

            string key = addressLine.ParentIndex + "|" + addressLine.Caption + "|"
                + CountRowOccurrence(addressLine);
            return _rewrite.Find(key);
        }

        private void AddSelectedRowToRewrite()
        {
            if (_rewrite == null || viewData.SelectedItems.Count == 0) return;

            // 用户直接点了“加入”，说明就是想让它生效：开关关着就顺手打开
            if (!_rewrite.Enabled && _btnRewriteToggle != null)
                _btnRewriteToggle.Checked = true;

            ListViewItem row = viewData.SelectedItems[0];
            IAddressNode addressLine = row.Tag as IAddressNode;
            if (addressLine == null) return;

            // 优先用「修改值」列里填的，没填就用当前原值（等于“把当前数值按住不动”）
            string text = row.SubItems[2].Text;
            if (string.IsNullOrEmpty(text)) text = row.SubItems[1].Text;
            if (string.IsNullOrEmpty(text)) return;

            if (!RegisterTrainerRewrite(row, addressLine, text.Trim()))
            {
                MessageBox.Show(this,
                    "这个值没法按 " + addressLine.ValueType + " 解析：" + text,
                    "定时重写", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ApplyRewriteNow();
            RefreshTrainerValues();
        }

        private void RemoveSelectedRowFromRewrite()
        {
            if (_rewrite == null || viewData.SelectedItems.Count == 0) return;

            IAddressNode addressLine = viewData.SelectedItems[0].Tag as IAddressNode;
            TrainerRewriteTask task = FindTrainerTask(addressLine);
            if (task == null) return;

            _rewrite.Remove(task.Key);
            RefreshTrainerRewriteMarks();
            UpdateRewriteState();
        }

        private void ClearTrainerRewriteTasks()
        {
            if (_rewrite == null) return;

            _rewrite.ClearTrainerTasks();
            RefreshTrainerRewriteMarks();
            UpdateRewriteState();
        }

        /// <summary>刷新「功能修改」页的原值列 + 重写标记（不重建行，避免打断正在编辑的输入框）。</summary>
        private void RefreshTrainerValues()
        {
            if (viewData == null || _currentGameContext == null) return;
            if (txtInput != null && txtInput.Visible) return;

            int pid = CurrentPid();
            if (pid <= 0) return;

            try
            {
                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(pid))
                {
                    foreach (ListViewItem row in viewData.Items)
                    {
                        IAddressNode addressLine = row.Tag as IAddressNode;
                        if (addressLine == null) continue;

                        try
                        {
                            switch (addressLine.ValueType)
                            {
                                case AddressListValueType.Integer:
                                    row.SubItems[1].Text = (mem.ReadInt32((IntPtr)addressLine.Address)
                                        / addressLine.ValueScale).ToString();
                                    break;
                                case AddressListValueType.Float:
                                    row.SubItems[1].Text = (mem.ReadFloat((IntPtr)addressLine.Address)
                                        / addressLine.ValueScale).ToString();
                                    break;
                                case AddressListValueType.Char4:
                                    row.SubItems[1].Text = mem.ReadChar4((IntPtr)addressLine.Address);
                                    break;
                            }
                            row.ImageIndex = 2;
                        }
                        catch
                        {
                            // 单个地址读失败（列表刚换页 / 游戏退出）就跳过
                        }
                    }
                }
            }
            catch
            {
            }

            RefreshTrainerRewriteMarks();
        }

        /// <summary>给每一行标出「正在被定时重写」以及被压住的值。</summary>
        private void RefreshTrainerRewriteMarks()
        {
            if (viewData == null || _rewrite == null) return;

            foreach (ListViewItem row in viewData.Items)
            {
                if (row.SubItems.Count < 4) continue;

                IAddressNode addressLine = row.Tag as IAddressNode;
                TrainerRewriteTask task = FindTrainerTask(addressLine);
                if (task == null)
                {
                    row.SubItems[3].Text = "";
                    continue;
                }

                row.SubItems[3].Text = task.DisplayValue() + "（重写中）";
            }
        }

        //////////////////////////////////////////////////////////////////////
        // 内存搜索页 UI
        //////////////////////////////////////////////////////////////////////

        private void BuildScannerTab(TabPage page)
        {
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 168;
            page.Controls.Add(top);

            Label lab;

            lab = MakeLabel("数值类型", 12, 12);
            top.Controls.Add(lab);
            _cmbScanType = new ComboBox();
            _cmbScanType.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbScanType.Location = new Point(78, 9);
            _cmbScanType.Width = 100;
            _cmbScanType.Items.AddRange(new object[] { "Int32", "Float", "Char4", "UInt16" });
            _cmbScanType.SelectedIndex = 0;
            top.Controls.Add(_cmbScanType);

            lab = MakeLabel("倍数", 190, 12);
            top.Controls.Add(lab);
            _txtScanScale = new TextBox();
            _txtScanScale.Location = new Point(230, 9);
            _txtScanScale.Width = 45;
            _txtScanScale.Text = "1";
            top.Controls.Add(_txtScanScale);

            lab = MakeLabel("容差", 288, 12);
            top.Controls.Add(lab);
            _txtScanTolerance = new TextBox();
            _txtScanTolerance.Location = new Point(328, 9);
            _txtScanTolerance.Width = 45;
            top.Controls.Add(_txtScanTolerance);

            lab = MakeLabel("数值", 386, 12);
            top.Controls.Add(lab);
            _txtScanValue = new TextBox();
            _txtScanValue.Location = new Point(426, 9);
            _txtScanValue.Width = 150;
            top.Controls.Add(_txtScanValue);

            _btnFirstScan = new Button();
            _btnFirstScan.Text = "首次扫描";
            _btnFirstScan.Location = new Point(586, 7);
            _btnFirstScan.Size = new Size(92, 28);
            _btnFirstScan.Click += delegate { RunFirstScan(); };
            top.Controls.Add(_btnFirstScan);

            _btnScanClear = new Button();
            _btnScanClear.Text = "清空结果";
            _btnScanClear.Location = new Point(686, 7);
            _btnScanClear.Size = new Size(92, 28);
            _btnScanClear.Click += delegate
            {
                if (_scanner != null) _scanner.Reset();
                _lvScan.Items.Clear();
                _labScanStatus.Text = "已清空结果";
            };
            top.Controls.Add(_btnScanClear);

            lab = MakeLabel("筛选方式", 12, 52);
            top.Controls.Add(lab);
            _cmbScanCompare = new ComboBox();
            _cmbScanCompare.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbScanCompare.Location = new Point(78, 49);
            _cmbScanCompare.Width = 120;
            _cmbScanCompare.Items.AddRange(new object[] { "等于数值", "变化了", "未变化", "增大了", "减小了" });
            _cmbScanCompare.SelectedIndex = 0;
            top.Controls.Add(_cmbScanCompare);

            _btnNextScan = new Button();
            _btnNextScan.Text = "再次扫描";
            _btnNextScan.Location = new Point(212, 47);
            _btnNextScan.Size = new Size(92, 28);
            _btnNextScan.Click += delegate { RunNextScan(); };
            top.Controls.Add(_btnNextScan);

            _btnScanRefresh = new Button();
            _btnScanRefresh.Text = "刷新数值";
            _btnScanRefresh.Location = new Point(312, 47);
            _btnScanRefresh.Size = new Size(92, 28);
            _btnScanRefresh.Click += delegate { RefreshScanValues(); };
            top.Controls.Add(_btnScanRefresh);

            _chkScanModuleOnly = new CheckBox();
            _chkScanModuleOnly.Text = "只搜索 game.dll（推荐）";
            _chkScanModuleOnly.Location = new Point(420, 52);
            _chkScanModuleOnly.AutoSize = true;
            _chkScanModuleOnly.Checked = true;
            top.Controls.Add(_chkScanModuleOnly);

            _chkScanAutoRefresh = new CheckBox();
            _chkScanAutoRefresh.Text = "自动刷新数值";
            _chkScanAutoRefresh.Location = new Point(616, 52);
            _chkScanAutoRefresh.AutoSize = true;
            _chkScanAutoRefresh.Checked = true;
            top.Controls.Add(_chkScanAutoRefresh);

            _labScanHint = MakeLabel(
                "数值留空点“首次扫描”= 未知初始值搜索，再用“变化了/未变化/增大了/减小了”逐步筛选。",
                12, 80);
            _labScanHint.ForeColor = Color.Gray;
            _labScanHint.AutoSize = true;
            top.Controls.Add(_labScanHint);

            Label hint2 = MakeLabel(
                "界面显示会四舍五入的数值（如 113.0）请在“容差”填 0.05；搜不到就取消勾选「只搜索 game.dll」。",
                12, 100);
            hint2.ForeColor = Color.Gray;
            hint2.AutoSize = true;
            top.Controls.Add(hint2);

            Label hint3 = MakeLabel(
                "容差按内存里的原始值算：填了倍数就要一起乘 —— 倍数 10000 时，容差填 500 表示允许 ±0.05。",
                12, 118);
            hint3.ForeColor = Color.Gray;
            hint3.AutoSize = true;
            top.Controls.Add(hint3);

            _labScanStatus = MakeLabel("游戏未运行", 12, 142);
            _labScanStatus.AutoSize = true;
            _labScanStatus.ForeColor = Color.DimGray;
            top.Controls.Add(_labScanStatus);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 44;
            page.Controls.Add(bottom);

            lab = MakeLabel("写入选中地址的值", 12, 14);
            bottom.Controls.Add(lab);
            _txtScanWriteValue = new TextBox();
            _txtScanWriteValue.Location = new Point(136, 11);
            _txtScanWriteValue.Width = 120;
            bottom.Controls.Add(_txtScanWriteValue);

            _btnScanWrite = new Button();
            _btnScanWrite.Text = "写入";
            _btnScanWrite.Location = new Point(266, 9);
            _btnScanWrite.Size = new Size(76, 28);
            _btnScanWrite.Click += delegate { WriteSelectedScanValue(); };
            bottom.Controls.Add(_btnScanWrite);

            _btnScanAddToBook = new Button();
            _btnScanAddToBook.Text = "添加到收藏…";
            _btnScanAddToBook.Location = new Point(352, 9);
            _btnScanAddToBook.Size = new Size(120, 28);
            _btnScanAddToBook.Click += delegate { AddSelectedScanResultToBook(); };
            bottom.Controls.Add(_btnScanAddToBook);

            _btnScanPointer = new Button();
            _btnScanPointer.Text = "扫描指针链…";
            _btnScanPointer.Location = new Point(482, 9);
            _btnScanPointer.Size = new Size(120, 28);
            _btnScanPointer.Click += delegate { RunPointerScan(); };
            bottom.Controls.Add(_btnScanPointer);

            _btnScanKeyLocate = new Button();
            _btnScanKeyLocate.Text = "按键定位…";
            _btnScanKeyLocate.Location = new Point(612, 9);
            _btnScanKeyLocate.Size = new Size(120, 28);
            _btnScanKeyLocate.Click += delegate { RunKeyLocate(); };
            bottom.Controls.Add(_btnScanKeyLocate);

            _btnDump = new Button();
            _btnDump.Text = "导出内存…";
            _btnDump.Location = new Point(742, 9);
            _btnDump.Size = new Size(120, 28);
            _btnDump.Click += delegate { RunMemoryDump(); };
            bottom.Controls.Add(_btnDump);

            _lvScan = new ListView();
            _lvScan.Dock = DockStyle.Fill;
            _lvScan.View = View.Details;
            _lvScan.FullRowSelect = true;
            _lvScan.GridLines = true;
            _lvScan.MultiSelect = true;   // 支持多选
            _lvScan.HideSelection = false;
            _lvScan.Columns.Add("地址", 120);
            _lvScan.Columns.Add("当前值", 140);
            _lvScan.Columns.Add("类型", 80);
            _lvScan.DoubleClick += delegate { AddSelectedScanResultToBook(); };
            _lvScan.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.C) { CopyScanSelection(true); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.A)
                {
                    foreach (ListViewItem it in _lvScan.Items) it.Selected = true;
                    e.Handled = true;
                }
            };
            ContextMenuStrip scanMenu = new ContextMenuStrip();
            scanMenu.Items.Add("复制地址(&C)", null, delegate { CopyScanSelection(false); });
            scanMenu.Items.Add("复制地址+数值(&V)", null, delegate { CopyScanSelection(true); });
            scanMenu.Items.Add("全选(&A)", null, delegate { foreach (ListViewItem it in _lvScan.Items) it.Selected = true; });
            _lvScan.ContextMenuStrip = scanMenu;
            page.Controls.Add(_lvScan);
            _lvScan.BringToFront();

            page.Resize += delegate { };
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            Label lab = new Label();
            lab.Text = text;
            lab.Location = new Point(x, y);
            lab.AutoSize = true;
            return lab;
        }

        //////////////////////////////////////////////////////////////////////
        // 地址收藏页 UI
        //////////////////////////////////////////////////////////////////////

        private void BuildBookTab(TabPage page)
        {
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 86;
            page.Controls.Add(top);

            _btnBookAdd = MakeButton("新增(&N)", 12, top, delegate { AddBookItem(); });
            _btnBookEdit = MakeButton("编辑(&E)", 104, top, delegate { EditBookItem(); });
            _btnBookDelete = MakeButton("删除(&D)", 196, top, delegate { DeleteBookItem(); });
            _btnBookWrite = MakeButton("写入(&W)", 288, top, delegate { WriteBookValue(); });
            _btnBookSave = MakeButton("保存(&S)", 380, top, delegate { SaveAddressBook(); });
            _btnBookReload = MakeButton("重新载入(&R)", 472, top, delegate { LoadAddressBook(true); });

            _btnBookPointer = MakeButton("指针链(&P)", 564, top, delegate { RunPointerScanForBookItem(); });
            _btnBookRelocate = MakeButton("重新定位(&A)", 656, top, delegate { RelocateKeyItems(); });
            _btnBookCleanup = MakeButton("清理失效(&X)", 748, top, delegate { CleanupBrokenItems(); });

            // 第二行：要写入的值（放在这里，避免和按钮抢横向空间）
            Label writeLabel = MakeLabel("要写入的值", 12, 54);
            top.Controls.Add(writeLabel);
            _txtBookWriteValue = new TextBox();
            _txtBookWriteValue.Location = new Point(96, 50);
            _txtBookWriteValue.Width = 160;
            top.Controls.Add(_txtBookWriteValue);

            Label hint = MakeLabel(
                "按键定位条目重启后点「重新定位」即可恢复；指针链条目以“重启后还能读出数值”为准。",
                280, 56);
            hint.ForeColor = Color.Gray;
            top.Controls.Add(hint);

            _labBookStatus = MakeLabel("", 12, 0);
            _labBookStatus.AutoSize = true;
            _labBookStatus.ForeColor = Color.DimGray;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 30;
            page.Controls.Add(bottom);
            _labBookStatus.Location = new Point(12, 6);
            bottom.Controls.Add(_labBookStatus);

            _lvBook = new ListView();
            _lvBook.Dock = DockStyle.Fill;
            _lvBook.View = View.Details;
            _lvBook.FullRowSelect = true;
            _lvBook.GridLines = true;
            _lvBook.MultiSelect = false;
            _lvBook.HideSelection = false;
            _lvBook.Columns.Add("名称", 150);
            _lvBook.Columns.Add("地址表达式", 210);
            _lvBook.Columns.Add("当前地址", 100);
            _lvBook.Columns.Add("当前值", 110);
            _lvBook.Columns.Add("类型", 70);
            _lvBook.Columns.Add("定时重写", 80);
            _lvBook.Columns.Add("备注", 160);
            _lvBook.DoubleClick += delegate { EditBookItem(); };
            page.Controls.Add(_lvBook);
            _lvBook.BringToFront();
        }

        private static Button MakeButton(string text, int x, Panel parent, EventHandler onClick)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = new Point(x, 10);
            btn.Size = new Size(86, 28);
            btn.Click += onClick;
            parent.Controls.Add(btn);
            return btn;
        }

        //////////////////////////////////////////////////////////////////////
        // 扫描逻辑
        //////////////////////////////////////////////////////////////////////

        private ScanValueType SelectedScanType()
        {
            return (ScanValueType)Math.Max(0, _cmbScanType.SelectedIndex);
        }

        private int SelectedScanScale()
        {
            int scale;
            if (!int.TryParse(_txtScanScale.Text.Trim(), out scale) || scale < 1) scale = 1;
            return scale;
        }

        /// <summary>匹配容差：界面显示会四舍五入的数值必须靠它（如显示 113.0、真实 113.04）。</summary>
        private double SelectedScanTolerance()
        {
            double tolerance;
            if (!double.TryParse(_txtScanTolerance.Text.Trim(), out tolerance) || tolerance < 0)
                tolerance = 0;
            return tolerance;
        }

        /// <summary>确保扫描器可用；游戏未运行或进程变化时重建。</summary>
        private bool EnsureScanner()
        {
            if (_currentGameContext == null)
            {
                _labScanStatus.Text = "游戏未运行：请先启动魔兽并点工具栏「查找游戏」";
                _scanner = null;
                return false;
            }

            int pid = _currentGameContext.ProcessId;
            if (_scanner == null || _scannerProcessId != pid)
            {
                bool processChanged = _scanner != null && _scannerProcessId != pid;
                if (_scanner != null) _scanner.Dispose();
                try
                {
                    _scanner = new MemoryScanner(pid);
                }
                catch (Exception ex)
                {
                    _labScanStatus.Text = "无法打开游戏进程：" + ex.Message;
                    _scanner = null;
                    return false;
                }
                _scannerProcessId = pid;
                _scanner.Progress = delegate(string s)
                {
                    // 扫描在 UI 线程上跑。这里只负责把进度画出来，
                    // 顺便让窗口能重绘/拖动；真正的重入防护在 _scanBusy 上。
                    _labScanStatus.Text = s;
                    if ((DateTime.Now - _lastScanProgressPaint).TotalMilliseconds < 120) return;
                    _lastScanProgressPaint = DateTime.Now;
                    try { _labScanStatus.Refresh(); } catch { }
                    Application.DoEvents();
                };
                _moduleCache.Reset(pid);

                // 进程变了：旧结果的地址对新进程毫无意义，绝不能留着让「写入」去写
                if (processChanged)
                {
                    _lvScan.Items.Clear();
                    _labScanStatus.Text = "游戏进程已变化，上次的搜索结果已清空（请重新「首次扫描」）";
                }
            }

            _scanner.Tolerance = SelectedScanTolerance();
            return true;
        }

        private void RunFirstScan()
        {
            if (_scanBusy) return;
            if (!EnsureScanner()) return;

            ScanValueType type = SelectedScanType();
            int scale = SelectedScanScale();

            string valueText = _txtScanValue.Text.Trim();
            if (valueText.Length > 0)
            {
                uint ignored;
                if (!MemoryScanner.TryParseScaledValue(type, valueText, scale, out ignored))
                {
                    MessageBox.Show(this, "数值格式不对，无法解析：" + valueText,
                        "内存搜索", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            bool moduleOnly = _chkScanModuleOnly.Checked;
            uint moduleBase = 0;
            int moduleSize = 0;
            if (moduleOnly)
            {
                if (!_moduleCache.TryGetModule(_currentGameContext.ProcessId, "game.dll", out moduleBase, out moduleSize))
                {
                    _labScanStatus.Text = "找不到 game.dll 模块，改为搜索全部可写内存";
                    moduleOnly = false;
                }
            }

            SetScanButtonsEnabled(false);
            _scanBusy = true;
            _lvScan.Items.Clear();
            _labScanStatus.Text = "首次扫描中…";

            DateTime started = DateTime.Now;
            try
            {
                bool truncated;

                // “未知初始值”把每个 4 字节槽都当成候选，非常吃内存：
                // 800 万条 ≈ 64MB 内存、只够覆盖 32MB 地址空间；已知值扫描用不着这么大。
                _scanner.MaxResults = valueText.Length == 0 ? 8000000 : 1000000;

                // 倍数交给扫描器自己乘：以前是这里先乘好再转字符串给它重新解析，浮点容易掉精度
                int count = _scanner.FirstScan(type, valueText, scale, moduleOnly, moduleBase, moduleSize, out truncated);
                TimeSpan elapsed = DateTime.Now - started;

                // 扫描期间用户可能已经关掉窗口（进度回调里 pump 过消息），别再碰界面
                if (IsDisposed || Disposing) return;

                double scannedMb = _scanner.ScannedBytes / 1048576.0;
                double totalMb = _scanner.TotalWritableBytes / 1048576.0;

                string status = string.Format(
                    "首次扫描完成：命中 {0} 个地址（{1}{2}），扫了 {3:0.0} MB / {4:0.0} MB，用时 {5:0.0} 秒",
                    count, type, scale > 1 ? " ×" + scale : "", scannedMb, totalMb, elapsed.TotalSeconds);
                if (truncated)
                    status += string.Format(
                        "；结果已达上限（{0} 万条），只扫到前 {1:0.0} MB，后面的内存没搜到 —— 请用「等于数值」缩小范围再扫",
                        _scanner.MaxResults / 10000, scannedMb);
                if (count == 0 && valueText.Length > 0)
                    status += moduleOnly
                        ? "；试试取消「只搜索 game.dll」，或者把数值类型改成 Float"
                        : "；数值本身是 float 时请把类型改成 Float，四舍五入过的数值请填「容差」";
                _labScanStatus.Text = status;
                ShowScanResults();
            }
            catch (Exception ex)
            {
                _labScanStatus.Text = "扫描失败：" + ex.Message;
            }
            finally
            {
                _scanBusy = false;
                SetScanButtonsEnabled(true);
            }
        }

        private void RunNextScan()
        {
            if (_scanBusy) return;
            if (!EnsureScanner()) return;
            if (_scanner.ResultCount == 0)
            {
                _labScanStatus.Text = "还没有扫描结果，请先「首次扫描」";
                return;
            }

            ScanCompare compare = (ScanCompare)Math.Max(0, _cmbScanCompare.SelectedIndex);
            string valueText = _txtScanValue.Text.Trim();

            // 类型/倍数沿用首次扫描（扫描器内部记着），这里不再读界面上的下拉框
            if (compare == ScanCompare.ExactValue && valueText.Length == 0)
            {
                MessageBox.Show(this, "“等于数值”需要在“数值”里填一个值。",
                    "再次扫描", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetScanButtonsEnabled(false);
            _scanBusy = true;
            _labScanStatus.Text = "再次扫描中…";
            try
            {
                int count = _scanner.NextScan(compare, compare == ScanCompare.ExactValue ? valueText : "");
                string note = SelectedScanType() != _scanner.Type
                    ? "；注意：这次沿用首次扫描的 " + _scanner.Type + "，要换类型请重新「首次扫描」"
                    : "";
                _labScanStatus.Text = string.Format("再次扫描完成：剩余 {0} 个地址（{1}{2}）{3}",
                    count, _scanner.Type, _scanner.Scale > 1 ? " ×" + _scanner.Scale : "", note);
                ShowScanResults();
            }
            catch (Exception ex)
            {
                _labScanStatus.Text = "再次扫描失败：" + ex.Message;
            }
            finally
            {
                _scanBusy = false;
                SetScanButtonsEnabled(true);
            }
        }

        private void SetScanButtonsEnabled(bool enabled)
        {
            _btnFirstScan.Enabled = enabled;
            _btnNextScan.Enabled = enabled;
            _btnScanClear.Enabled = enabled;
            _btnScanRefresh.Enabled = enabled;
            _btnScanWrite.Enabled = enabled;
            _btnScanAddToBook.Enabled = enabled;

            // 这两个也要一起锁：扫描期间它们会另开进程句柄做重活，
            // 而且“扫描指针链”用的是结果列表里的地址，扫一半点它会拿到半截数据
            _btnScanPointer.Enabled = enabled;
            _btnScanKeyLocate.Enabled = enabled;
            _btnDump.Enabled = enabled;

            // 扫描中途重新识别游戏会把 _currentGameContext/_mainTrainer 换掉
            if (cmdScanGame != null) cmdScanGame.Enabled = enabled && _currentGameContext != null;

            Cursor = enabled ? Cursors.Default : Cursors.WaitCursor;
        }

        private void ShowScanResults()
        {
            _lvScan.BeginUpdate();
            try
            {
                _lvScan.Items.Clear();
                if (_scanner == null) return;

                // 用结果集自己的类型/倍数（不是界面上当前选中的那个）
                ScanValueType type = _scanner.Type;
                uint[] all = _scanner.GetAddresses();
                int show = Math.Min(all.Length, MaxDisplayRows);
                uint[] slice = new uint[show];
                Array.Copy(all, slice, show);

                uint[] values = _scanner.ReadResultValues(slice);

                for (int i = 0; i < show; i++)
                {
                    ListViewItem row = new ListViewItem("0x" + slice[i].ToString("X8"));
                    row.SubItems.Add(_scanner.FormatResult(values[i]));
                    row.SubItems.Add(type.ToString() + (_scanner.Scale > 1 ? " ×" + _scanner.Scale : ""));
                    row.Tag = slice[i];
                    _lvScan.Items.Add(row);
                }

                if (all.Length > show)
                {
                    ListViewItem row = new ListViewItem("…");
                    row.SubItems.Add(string.Format("还有 {0} 条未显示，请用“再次扫描”继续筛选", all.Length - show));
                    row.SubItems.Add("");
                    _lvScan.Items.Add(row);
                }
            }
            finally
            {
                _lvScan.EndUpdate();
            }
        }

        private void RefreshScanValues()
        {
            if (_scanner == null || _lvScan.Items.Count == 0) return;

            List<uint> addresses = new List<uint>();
            List<ListViewItem> rows = new List<ListViewItem>();
            foreach (ListViewItem row in _lvScan.Items)
            {
                if (row.Tag is uint)
                {
                    addresses.Add((uint)row.Tag);
                    rows.Add(row);
                }
            }
            if (addresses.Count == 0) return;

            uint[] values = _scanner.ReadResultValues(addresses.ToArray());
            for (int i = 0; i < rows.Count; i++)
                rows[i].SubItems[1].Text = _scanner.FormatResult(values[i]);
        }

        /// <summary>复制选中的地址（withValue=true 时连当前值一起），可直接粘到记事本/收藏对话框。</summary>
        private void CopyScanSelection(bool withValue)
        {
            if (_lvScan.SelectedItems.Count == 0)
            {
                _labScanStatus.Text = "请先选中要复制的行";
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (ListViewItem row in _lvScan.SelectedItems)
            {
                if (!(row.Tag is uint)) continue;
                string addr = "0x" + ((uint)row.Tag).ToString("X8");
                sb.AppendLine(withValue ? addr + "\t" + row.SubItems[1].Text : addr);
            }

            if (sb.Length == 0) { _labScanStatus.Text = "选中的行里没有有效地址"; return; }
            try
            {
                Clipboard.SetText(sb.ToString());
                _labScanStatus.Text = string.Format("已复制 {0} 行到剪贴板", _lvScan.SelectedItems.Count);
            }
            catch (Exception ex)
            {
                _labScanStatus.Text = "复制失败：" + ex.Message;
            }
        }

        private void WriteSelectedScanValue()
        {
            if (_scanBusy) return;
            if (!EnsureScanner()) return;
            if (_lvScan.SelectedItems.Count == 0)
            {
                _labScanStatus.Text = "请先在结果列表里选中一行";
                return;
            }

            string wtext = _txtScanWriteValue.Text.Trim();
            if (wtext.Length == 0)
            {
                _labScanStatus.Text = "请先在左下角「写入选中地址的值」里填一个数值";
                return;
            }

            // 写入的类型/倍数必须和这批结果一致，否则写下去的是另一种解释的字节
            ScanValueType wtype = _scanner.Type;
            int wscale = _scanner.Scale;
            int done = 0;
            try
            {
                foreach (ListViewItem row in _lvScan.SelectedItems)
                {
                    if (!(row.Tag is uint)) continue;
                    _scanner.WriteValue((uint)row.Tag, wtype, wtext, wscale);
                    done++;
                }
                RefreshScanValues();
                _labScanStatus.Text = string.Format("已写入 {0} 个地址（{1}{2}）",
                    done, wtype, wscale > 1 ? " ×" + wscale : "");
            }
            catch (Exception ex)
            {
                _labScanStatus.Text = "写入失败：" + ex.Message;
            }
        }

        private void AddSelectedScanResultToBook()
        {
            if (_scanBusy) return;
            if (_lvScan.SelectedItems.Count == 0)
            {
                _labScanStatus.Text = "请先在结果列表里选中一行，再添加到收藏";
                return;
            }

            ListViewItem row = _lvScan.SelectedItems[0];
            if (!(row.Tag is uint))
            {
                _labScanStatus.Text = "该行不是有效地址";
                return;
            }

            uint address = (uint)row.Tag;

            // 收藏的类型/倍数也要跟着结果集走（原来读界面下拉框，
            // 用户中途改过类型就会把 float 地址存成 Int32）
            ScanValueType type = _scanner != null ? _scanner.Type : SelectedScanType();
            int scale = _scanner != null ? _scanner.Scale : SelectedScanScale();

            // 换算成模块相对偏移，游戏重启后依然有效
            string moduleName = "";
            uint moduleBase = 0;
            if (_currentGameContext != null)
            {
                int pid = _currentGameContext.ProcessId;
                uint baseAddr;
                int size;
                if (_moduleCache.TryGetModule(pid, "game.dll", out baseAddr, out size)
                    && address >= baseAddr && address < baseAddr + (uint)size)
                {
                    moduleName = "game.dll";
                    moduleBase = baseAddr;
                }
                else if (_moduleCache.TryGetModule(pid, "war3.exe", out baseAddr, out size)
                    && address >= baseAddr && address < baseAddr + (uint)size)
                {
                    moduleName = "war3.exe";
                    moduleBase = baseAddr;
                }
            }

            SavedAddress item = AddressBook.FromAbsoluteAddress(
                "新地址 " + DateTime.Now.ToString("HH:mm:ss"), address, moduleName, moduleBase, type, scale);

            using (SavedAddressDialog dialog = new SavedAddressDialog(item, CurrentPid(), _moduleCache))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _book.Items.Add(dialog.Result);
                SaveAddressBook();
                RefreshBookValues(true);
                _tabsMain.SelectedTab = _pageBook;
                _labBookStatus.Text = "已添加：" + dialog.Result.Name
                    + "（" + dialog.Result.Describe() + "）";
            }
        }

        //////////////////////////////////////////////////////////////////////
        // 指针链扫描
        //////////////////////////////////////////////////////////////////////

        /// <summary>从结果列表选中的地址出发，向上找 game.dll+偏移 形式的指针链。</summary>
        private void RunPointerScan()
        {
            if (_scanBusy) return;
            if (_lvScan.SelectedItems.Count == 0)
            {
                _labScanStatus.Text = "请先在结果列表里选中一行（也就是你要长期跟踪的那个地址）";
                return;
            }

            ListViewItem row = _lvScan.SelectedItems[0];
            if (!(row.Tag is uint))
            {
                _labScanStatus.Text = "该行不是有效地址";
                return;
            }

            if (_currentGameContext == null)
            {
                _labScanStatus.Text = "游戏未运行，无法扫描指针链";
                return;
            }

            int pid = _currentGameContext.ProcessId;
            uint target = (uint)row.Tag;
            ScanValueType type = _scanner != null ? _scanner.Type : SelectedScanType();
            int scale = _scanner != null ? _scanner.Scale : SelectedScanScale();

            uint moduleBase = 0;
            int moduleSize = 0;
            bool hasModule = _moduleCache.TryGetModule(pid, "game.dll", out moduleBase, out moduleSize);

            using (PointerScanDialog dialog = new PointerScanDialog(
                pid, target, type, scale, moduleBase, moduleSize,
                hasModule ? "game.dll" : null, _moduleCache))
            {
                dialog.ShowDialog(this);

                if (dialog.Selected.Count > 0)
                {
                    foreach (SavedAddress item in dialog.Selected)
                        _book.Items.Add(item);

                    SaveAddressBook();
                    RefreshBookValues(true);
                    _tabsMain.SelectedTab = _pageBook;
                    _labBookStatus.Text = string.Format("已加入 {0} 条指针链，"
                        + "请重启游戏后回到本页确认哪条还能读出数值。", dialog.Selected.Count);
                }
            }
        }

        //////////////////////////////////////////////////////////////////////
        // 按键定位（指针链的保底方案）
        //////////////////////////////////////////////////////////////////////

        /// <summary>导出内存十六进制（用于"买科技前后对比"定位等级数组）。</summary>
        private void RunMemoryDump()
        {
            using (MemoryDumpDialog dialog = new MemoryDumpDialog(_currentGameContext))
            {
                dialog.ShowDialog(this);
            }
        }

        private void RunKeyLocate()
        {
            if (_currentGameContext == null)
            {
                _labScanStatus.Text = "游戏未运行，无法按键定位";
                return;
            }

            using (KeyLocatorDialog dialog = new KeyLocatorDialog(_currentGameContext.ProcessId))
            {
                // 把当前搜索框里的类型/数值带进去，少填两项
                dialog.ShowDialog(this);

                if (dialog.Selected.Count > 0)
                {
                    foreach (SavedAddress item in dialog.Selected)
                        _book.Items.Add(item);

                    SaveAddressBook();
                    RefreshBookValues(true);
                    _tabsMain.SelectedTab = _pageBook;
                    _labBookStatus.Text = "已把按键定位的结果加入收藏（堆地址，重启后需重新定位）。";
                }
            }
        }

        /// <summary>对地址收藏里选中的条目发起指针链扫描（用于把“按键定位”得到的地址变成可保存的链）。</summary>
        private void RunPointerScanForBookItem()
        {
            if (_lvBook.SelectedItems.Count == 0)
            {
                _labBookStatus.Text = "请先在收藏里选中一条（例如按键定位存进来的那条）";
                return;
            }

            if (_currentGameContext == null)
            {
                _labBookStatus.Text = "游戏未运行，无法扫描";
                return;
            }

            SavedAddress item = _lvBook.SelectedItems[0].Tag as SavedAddress;
            if (item == null) return;

            int pid = _currentGameContext.ProcessId;
            string error;
            uint target = AddressBook.Resolve(item, pid, _moduleCache, out error);
            if (target == 0)
            {
                _labBookStatus.Text = "解析地址失败：" + (error ?? "地址为 0");
                return;
            }

            uint moduleBase = 0;
            int moduleSize = 0;
            bool hasModule = _moduleCache.TryGetModule(pid, "game.dll", out moduleBase, out moduleSize);

            using (PointerScanDialog dialog = new PointerScanDialog(
                pid, target, item.ValueType, item.ValueScale,
                moduleBase, moduleSize, hasModule ? "game.dll" : null, _moduleCache))
            {
                dialog.ShowDialog(this);

                if (dialog.Selected.Count > 0)
                {
                    foreach (SavedAddress chain in dialog.Selected)
                        _book.Items.Add(chain);

                    SaveAddressBook();
                    RefreshBookValues(true);
                    _labBookStatus.Text = string.Format("已加入 {0} 条指针链，重启游戏后回来确认哪条仍能读出数值。",
                        dialog.Selected.Count);
                }
            }
        }

        /// <summary>重新定位所有「按键定位」条目（重启游戏后点一下即可恢复）。</summary>
        private void RelocateKeyItems()
        {
            int pid = CurrentPid();
            if (pid <= 0)
            {
                _labBookStatus.Text = "游戏未运行，无法重新定位";
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                _labBookStatus.Text = "正在重新定位…";
                Application.DoEvents();

                string summary = AddressBook.RelocateKeyItems(_book.Items, pid,
                    delegate(string s) { _labBookStatus.Text = s; Application.DoEvents(); });

                _labBookStatus.Text = summary;
                _lastRelocatedPid = pid;
                RefreshBookValues(true);
            }
            catch (Exception ex)
            {
                _labBookStatus.Text = "重新定位失败：" + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// 识别到游戏后自动重新定位按键定位条目（同一次游戏进程只做一次，
        /// 避免重复扫内存）。
        /// </summary>
        internal void AutoRelocateIfNeeded()
        {
            if (_book == null || _currentGameContext == null) return;

            int pid = _currentGameContext.ProcessId;
            if (pid == _lastRelocatedPid) return;

            bool hasKeyItem = false;
            foreach (SavedAddress item in _book.Items)
            {
                if (item.IsKeyItem) { hasKeyItem = true; break; }
            }
            if (!hasKeyItem) { _lastRelocatedPid = pid; return; }

            RelocateKeyItems();
        }

        /// <summary>清理解析不到的条目（重启后失效的指针链等）。</summary>
        private void CleanupBrokenItems()
        {
            int pid = CurrentPid();
            if (pid <= 0)
            {
                _labBookStatus.Text = "游戏未运行，判断不了哪些条目失效（请进图后再清理）";
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;

                // 先恢复按键定位条目，免得把“只是缓存过期”的条目误判为失效
                try
                {
                    AddressBook.RelocateKeyItems(_book.Items, pid,
                        delegate(string s) { _labBookStatus.Text = s; Application.DoEvents(); });
                }
                catch
                {
                }

                List<SavedAddress> broken = new List<SavedAddress>();
                int keySkipped = 0;
                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(pid))
                {
                    foreach (SavedAddress item in _book.Items)
                    {
                        // 按键定位条目绝不能因为“这一刻定位不到”就被删：
                        // 它本来就是“每次开局重新定位”的设计，定位失败只是暂时状态。
                        if (item.IsKeyItem) { keySkipped++; continue; }

                        string error;
                        uint address = AddressBook.Resolve(item, pid, _moduleCache, out error);
                        int size = MemoryScanner.SizeOf(item.ValueType);
                        bool ok = false;
                        if (address != 0)
                        {
                            int read;
                            mem.ReadBytes((IntPtr)address, size, out read);
                            ok = read == size;
                        }
                        if (!ok) broken.Add(item);
                    }
                }

                if (broken.Count == 0)
                {
                    _labBookStatus.Text = string.Format("没有失效条目（全部都能解析）；按键定位条目 {0} 条已跳过保护", keySkipped);
                    return;
                }

                System.Text.StringBuilder names = new System.Text.StringBuilder();
                for (int i = 0; i < broken.Count && i < 8; i++)
                    names.AppendLine("  · " + broken[i].Name + "   " + broken[i].Describe());
                if (broken.Count > 8) names.AppendLine("  · …");

                if (MessageBox.Show(this,
                        string.Format("有 {0} 条解析不到（通常是重启后失效的指针链）：{1}{1}{2}{1}删除它们？",
                            broken.Count, Environment.NewLine, names.ToString()),
                        "清理失效条目", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
                    != DialogResult.OK)
                    return;

                foreach (SavedAddress item in broken)
                    _book.Items.Remove(item);

                SaveAddressBook();
                RefreshBookValues(true);
                _labBookStatus.Text = string.Format("已删除 {0} 条失效条目", broken.Count);
            }
            catch (Exception ex)
            {
                _labBookStatus.Text = "清理失败：" + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        //////////////////////////////////////////////////////////////////////
        // 地址收藏逻辑
        //////////////////////////////////////////////////////////////////////

        private int CurrentPid()
        {
            return _currentGameContext == null ? 0 : _currentGameContext.ProcessId;
        }

        private void LoadAddressBook(bool showMessage)
        {
            try
            {
                int count = _book.Load(null);
                if (showMessage)
                    _labBookStatus.Text = string.Format("已从 {0} 载入 {1} 条收藏", AddressBook.DefaultPath(), count);
                else
                    _labBookStatus.Text = string.Format("收藏文件：{0}（{1} 条）", AddressBook.DefaultPath(), count);
                RefreshBookValues(true);
                UpdateRewriteState();
            }
            catch (Exception ex)
            {
                if (showMessage)
                    MessageBox.Show(this, "载入收藏失败：" + ex.Message, "地址收藏",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    _labBookStatus.Text = "载入收藏失败：" + ex.Message;
            }
        }

        private void SaveAddressBook()
        {
            try
            {
                _book.Save(null);
                _labBookStatus.Text = "已保存到 " + AddressBook.DefaultPath();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存收藏失败：" + ex.Message, "地址收藏",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RefreshBookValues(bool rebuildRows)
        {
            if (rebuildRows)
            {
                _lvBook.BeginUpdate();
                try
                {
                    _lvBook.Items.Clear();
                    foreach (SavedAddress item in _book.Items)
                    {
                        ListViewItem row = new ListViewItem(item.Name);
                        row.SubItems.Add(item.Describe());
                        row.SubItems.Add("");
                        row.SubItems.Add("");
                        row.SubItems.Add(item.ValueType.ToString());
                        row.SubItems.Add(item.Frozen ? "重写中" : "");
                        row.SubItems.Add(item.Note);
                        row.Tag = item;
                        _lvBook.Items.Add(row);
                    }
                }
                finally
                {
                    _lvBook.EndUpdate();
                }
            }

            int pid = CurrentPid();
            if (pid <= 0)
            {
                foreach (ListViewItem row in _lvBook.Items)
                {
                    row.SubItems[2].Text = "";
                    row.SubItems[3].Text = "游戏未运行";
                }
                return;
            }

            foreach (ListViewItem row in _lvBook.Items)
            {
                SavedAddress item = row.Tag as SavedAddress;
                if (item == null) continue;

                string error;
                uint address = AddressBook.Resolve(item, pid, _moduleCache, out error);
                if (address == 0)
                {
                    row.SubItems[2].Text = "解析失败";
                    row.SubItems[3].Text = error ?? "";
                    continue;
                }

                row.SubItems[2].Text = "0x" + address.ToString("X8");
                try
                {
                    using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(pid))
                    {
                        uint raw = item.ValueType == ScanValueType.UInt16
                            ? mem.ReadUInt16((IntPtr)address)
                            : mem.ReadUInt32((IntPtr)address);
                        row.SubItems[3].Text = MemoryScanner.FormatValue(item.ValueType, raw, item.ValueScale);
                    }
                }
                catch (Exception ex)
                {
                    row.SubItems[3].Text = "读取失败：" + ex.Message;
                }
            }
        }

        private void AddBookItem()
        {
            SavedAddress item = new SavedAddress();
            item.Name = "新收藏 " + (_book.Items.Count + 1);
            item.ModuleName = "game.dll";   // 绝大多数可改数值都在 Game.dll 的静态区
            using (SavedAddressDialog dialog = new SavedAddressDialog(item, CurrentPid(), _moduleCache))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _book.Items.Add(dialog.Result);
                SaveAddressBook();
                RefreshBookValues(true);
            }
        }

        private void EditBookItem()
        {
            if (_lvBook.SelectedItems.Count == 0) return;
            SavedAddress original = _lvBook.SelectedItems[0].Tag as SavedAddress;
            if (original == null) return;

            using (SavedAddressDialog dialog = new SavedAddressDialog(original, CurrentPid(), _moduleCache))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                int index = _book.Items.IndexOf(original);
                if (index >= 0) _book.Items[index] = dialog.Result;
                SaveAddressBook();
                RefreshBookValues(true);
            }
        }

        private void DeleteBookItem()
        {
            if (_lvBook.SelectedItems.Count == 0) return;
            SavedAddress item = _lvBook.SelectedItems[0].Tag as SavedAddress;
            if (item == null) return;

            if (MessageBox.Show(this, "确定删除收藏「" + item.Name + "」？", "地址收藏",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            _book.Items.Remove(item);
            SaveAddressBook();
            RefreshBookValues(true);
        }

        private void WriteBookValue()
        {
            if (_lvBook.SelectedItems.Count == 0)
            {
                _labBookStatus.Text = "请先选中一条收藏";
                return;
            }

            int pid = CurrentPid();
            if (pid <= 0)
            {
                _labBookStatus.Text = "游戏未运行，无法写入";
                return;
            }

            SavedAddress item = _lvBook.SelectedItems[0].Tag as SavedAddress;
            if (item == null) return;

            string error;
            uint address = AddressBook.Resolve(item, pid, _moduleCache, out error);
            if (address == 0)
            {
                _labBookStatus.Text = "解析地址失败：" + (error ?? "地址为 0");
                return;
            }

            string text = _txtBookWriteValue.Text.Trim();
            if (text.Length == 0)
            {
                _labBookStatus.Text = "请先在右侧填写要写入的值";
                return;
            }

            try
            {
                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(pid))
                {
                    uint raw;
                    if (!MemoryScanner.TryParseScaledValue(item.ValueType, text, item.ValueScale, out raw))
                        throw new ArgumentException("数值格式不对：" + text);

                    if (item.ValueType == ScanValueType.UInt16)
                        mem.WriteUInt16((IntPtr)address, (ushort)raw);
                    else
                        mem.WriteUInt32((IntPtr)address, raw);
                }

                // 记下重写值：勾了「定时重写」的话，工具栏那个开关会按间隔持续回写
                item.FrozenValue = text;
                SaveAddressBook();
                RefreshBookValues(false);
                UpdateRewriteState();
                _labBookStatus.Text = string.Format("已把「{0}」写入 0x{1:X8}", item.Name, address)
                    + (item.Frozen ? "，已纳入定时重写" : "");
            }
            catch (Exception ex)
            {
                _labBookStatus.Text = "写入失败：" + ex.Message;
            }
        }

        //////////////////////////////////////////////////////////////////////
        // 定时刷新（列表数值；重写见上面的 _timerRewrite）
        //////////////////////////////////////////////////////////////////////

        private void OnRefreshTick()
        {
            if (_suppressTimer || _tabsMain == null) return;

            try
            {
                _suppressTimer = true;   // 避免重入

                // 列表刷新只对可见页签做，省开销
                if (_tabsMain.SelectedTab == _pageBook)
                    RefreshBookValues(false);
                else if (_tabsMain.SelectedTab == _pageScan && _chkScanAutoRefresh != null
                         && _chkScanAutoRefresh.Checked)
                    RefreshScanValues();
            }
            catch
            {
                // 定时刷新失败不打扰用户（游戏可能刚好退出）
            }
            finally
            {
                _suppressTimer = false;
            }
        }
    }
}
