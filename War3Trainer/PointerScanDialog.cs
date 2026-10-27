using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace War3Trainer
{
    /// <summary>
    /// 指针链扫描对话框：从一个已知地址出发，向上找出「game.dll+偏移 +[多级偏移]」形式的路径。
    /// 找到的链可以保存进地址收藏，游戏重启后依然可用。
    /// </summary>
    internal sealed class PointerScanDialog : Form
    {
        private readonly int _pid;
        private readonly uint _target;
        private readonly ScanValueType _type;
        private readonly int _scale;
        private readonly ModuleBaseCache _cache;
        private readonly uint _moduleBase;
        private readonly int _moduleSize;
        private readonly string _moduleName;

        private TextBox _txtDepth;
        private TextBox _txtMaxOffset;
        private TextBox _txtParents;
        private TextBox _txtMaxChains;
        private Button _btnScan;
        private Label _labStatus;
        private ListView _lvChains;
        private Button _btnSave;
        private Button _btnClose;
        private CheckBox _chkOnlyResolvable;

        private readonly List<PointerChain> _chains = new List<PointerChain>();
        private volatile bool _cancelRequested;

        /// <summary>用户选择要保存的收藏项。</summary>
        public List<SavedAddress> Selected { get; private set; }

        public PointerScanDialog(
            int processId, uint target, ScanValueType type, int scale,
            uint moduleBase, int moduleSize, string moduleName,
            ModuleBaseCache cache)
        {
            _pid = processId;
            _target = target;
            _type = type;
            _scale = scale;
            _moduleBase = moduleBase;
            _moduleSize = moduleSize;
            _moduleName = moduleName;
            _cache = cache;
            Selected = new List<SavedAddress>();

            BuildUi();
        }

        private void BuildUi()
        {
            Text = "指针链扫描";
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 520);
            MinimumSize = new Size(700, 420);

            Label lab;

            lab = new Label();
            lab.Text = string.Format("目标地址：0x{0:X8}   类型：{1}", _target, _type);
            lab.Location = new Point(12, 10);
            lab.AutoSize = true;
            Controls.Add(lab);

            lab = new Label(); lab.Text = "最大级数"; lab.Location = new Point(12, 44); lab.AutoSize = true;
            Controls.Add(lab);
            _txtDepth = new TextBox(); _txtDepth.SetBounds(76, 40, 42, 25); _txtDepth.Text = "3";
            Controls.Add(_txtDepth);

            lab = new Label(); lab.Text = "每级最大偏移"; lab.Location = new Point(132, 44); lab.AutoSize = true;
            Controls.Add(lab);
            _txtMaxOffset = new TextBox(); _txtMaxOffset.SetBounds(224, 40, 64, 25); _txtMaxOffset.Text = "400";
            Controls.Add(_txtMaxOffset);

            lab = new Label(); lab.Text = "每级候选上限"; lab.Location = new Point(300, 44); lab.AutoSize = true;
            Controls.Add(lab);
            _txtParents = new TextBox(); _txtParents.SetBounds(392, 40, 56, 25); _txtParents.Text = "16";
            Controls.Add(_txtParents);

            lab = new Label(); lab.Text = "最多返回"; lab.Location = new Point(460, 44); lab.AutoSize = true;
            Controls.Add(lab);
            _txtMaxChains = new TextBox(); _txtMaxChains.SetBounds(528, 40, 56, 25); _txtMaxChains.Text = "500";
            Controls.Add(_txtMaxChains);

            _btnScan = new Button();
            _btnScan.Text = "开始扫描(&S)";
            _btnScan.SetBounds(600, 38, 104, 30);
            _btnScan.Click += delegate { StartScan(); };
            Controls.Add(_btnScan);

            Button btnCancel = new Button();
            btnCancel.Text = "中止";
            btnCancel.SetBounds(712, 38, 92, 30);
            btnCancel.Click += delegate { _cancelRequested = true; _labStatus.Text = "正在中止…"; };
            Controls.Add(btnCancel);

            lab = new Label();
            lab.Text = "提示：偏移单位是十六进制；级数越大越慢；结果需要用“重启游戏后还能否读出数值”来筛选。";
            lab.Location = new Point(12, 74);
            lab.AutoSize = true;
            lab.ForeColor = Color.Gray;
            Controls.Add(lab);

            _labStatus = new Label();
            _labStatus.Text = "未开始";
            _labStatus.SetBounds(12, 96, 796, 20);
            _labStatus.AutoSize = false;
            _labStatus.ForeColor = Color.DimGray;
            Controls.Add(_labStatus);

            _lvChains = new ListView();
            _lvChains.SetBounds(12, 120, 792, 350);
            _lvChains.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _lvChains.View = View.Details;
            _lvChains.FullRowSelect = true;
            _lvChains.GridLines = true;
            _lvChains.MultiSelect = true;
            _lvChains.Columns.Add("指针链", 300);
            _lvChains.Columns.Add("解析地址", 110);
            _lvChains.Columns.Add("当前值", 120);
            _lvChains.Columns.Add("层级", 60);
            Controls.Add(_lvChains);

            _chkOnlyResolvable = new CheckBox();
            _chkOnlyResolvable.Text = "只显示能解析出数值的链";
            _chkOnlyResolvable.SetBounds(12, 480, 220, 24);
            _chkOnlyResolvable.AutoSize = true;
            _chkOnlyResolvable.Checked = true;
            _chkOnlyResolvable.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(_chkOnlyResolvable);

            _btnSave = new Button();
            _btnSave.Text = "保存选中到收藏(&A)";
            _btnSave.SetBounds(430, 476, 160, 30);
            _btnSave.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnSave.Enabled = false;
            _btnSave.Click += delegate { SaveSelected(); };
            Controls.Add(_btnSave);

            _btnClose = new Button();
            _btnClose.Text = "完成(&C)";
            _btnClose.SetBounds(600, 476, 92, 30);
            _btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnClose.Click += delegate { Close(); };
            Controls.Add(_btnClose);

            Button btnAll = new Button();
            btnAll.Text = "全选(&L)";
            btnAll.SetBounds(330, 476, 92, 30);
            btnAll.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnAll.Click += delegate { _lvChains.Items.Clear(); ShowChains(_chains); };
            Controls.Add(btnAll);
        }

        private int ReadInt(TextBox box, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(box.Text.Trim(), out value)) value = fallback;
            if (value < min) value = min;
            if (value > max) value = max;
            return value;
        }

        private void StartScan()
        {
            if (!WarnIfGameRunning()) return;

            PointerScanOptions options = new PointerScanOptions();
            options.MaxDepth = ReadInt(_txtDepth, 3, 1, 6);
            options.MaxOffset = ReadInt(_txtMaxOffset, 0x400, 0x10, 0x100000);
            options.MaxParentsPerNode = ReadInt(_txtParents, 16, 1, 100000);
            options.MaxChains = ReadInt(_txtMaxChains, 500, 1, 100000);
            // 收敛窗口不要开太大：窗口越宽，“巧合成立”的链越多，
            // 那些链本局内解析得到、重启就废。这里跟每级偏移保持一致。
            options.RootMaxOffset = options.MaxOffset;

            _chains.Clear();
            _lvChains.Items.Clear();
            _btnScan.Enabled = false;
            _btnSave.Enabled = false;
            _cancelRequested = false;

            try
            {
                using (PointerScanner scanner = new PointerScanner(_pid, _moduleBase, _moduleSize, _moduleName))
                {
                    scanner.Progress = delegate(string message)
                    {
                        _labStatus.Text = message;
                        Application.DoEvents();
                    };
                    scanner.IsCancelled = delegate { return _cancelRequested; };

                    List<PointerChain> found = scanner.FindChains(_target, options);
                    _chains.AddRange(found);
                }

                _labStatus.Text = "扫描完成，候选链 " + _chains.Count + " 条（正在解析…）";
                Application.DoEvents();
                ShowChains(_chains);
                _labStatus.Text = string.Format("扫描完成：候选 {0} 条，其中可解析 {1} 条。"
                    + "重启游戏后再打开“地址收藏”，还能读出数值的那条才是长期有效的。",
                    _chains.Count, CountResolvable());
                _btnSave.Enabled = _chains.Count > 0;
            }
            catch (Exception ex)
            {
                _labStatus.Text = "扫描失败：" + ex.Message;
            }
            finally
            {
                _btnScan.Enabled = true;
                Application.DoEvents();
            }
        }

        private bool WarnIfGameRunning()
        {
            if (_pid > 0) return true;
            MessageBox.Show(this, "游戏未运行，无法扫描。", "指针链扫描",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private int CountResolvable()
        {
            int n = 0;
            foreach (PointerChain c in _chains)
            {
                if (TryResolve(c, out uint address, out string valueText)) n++;
            }
            return n;
        }

        private bool TryResolve(PointerChain chain, out uint address, out string valueText)
        {
            address = 0;
            valueText = "";
            try
            {
                SavedAddress item = chain.ToSavedAddress("tmp", _type, _scale);
                string error;
                address = AddressBook.Resolve(item, _pid, _cache, out error);
                if (address == 0) { valueText = error ?? "解析失败"; return false; }

                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(_pid))
                {
                    uint raw = _type == ScanValueType.UInt16
                        ? mem.ReadUInt16((IntPtr)address)
                        : mem.ReadUInt32((IntPtr)address);
                    valueText = MemoryScanner.FormatValue(_type, raw, _scale);
                }
                return true;
            }
            catch (Exception ex)
            {
                valueText = ex.Message;
                return false;
            }
        }

        private void ShowChains(List<PointerChain> chains)
        {
            _lvChains.BeginUpdate();
            try
            {
                _lvChains.Items.Clear();
                foreach (PointerChain c in chains)
                {
                    uint address;
                    string valueText;
                    bool ok = TryResolve(c, out address, out valueText);
                    if (_chkOnlyResolvable.Checked && !ok) continue;

                    ListViewItem row = new ListViewItem(c.Describe());
                    row.SubItems.Add(ok ? "0x" + address.ToString("X8") : "失败");
                    row.SubItems.Add(valueText);
                    row.SubItems.Add(c.Depth.ToString());
                    row.Tag = c;
                    _lvChains.Items.Add(row);
                }
            }
            finally
            {
                _lvChains.EndUpdate();
            }
        }

        private void SaveSelected()
        {
            ListView.SelectedListViewItemCollection rows = _lvChains.SelectedItems;
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "请先在列表里选中要保存的链（可多选）。", "指针链扫描",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            foreach (ListViewItem row in rows)
            {
                PointerChain chain = row.Tag as PointerChain;
                if (chain == null) continue;

                SavedAddress item = chain.ToSavedAddress(
                    "指针链 " + DateTime.Now.ToString("HH:mm:ss") + " (" + chain.Depth + "级)",
                    _type, _scale);
                Selected.Add(item);
            }

            _labStatus.Text = string.Format("已选择 {0} 条，点“完成”写入地址收藏。", Selected.Count);
        }
    }
}
