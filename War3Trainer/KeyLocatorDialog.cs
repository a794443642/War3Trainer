using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace War3Trainer
{
    /// <summary>
    /// 「按键定位」对话框：用 hashtable 的子键常量（例：0xE59561E4）定位一个数值。
    ///
    /// 流程：搜键 → 在键周围找数值等于「界面显示值」的位置 → 得到 偏移 → 直接写入。
    /// 适合指针链没扫出来、或者不想每次都搜值的场景（每次开局点一下即可）。
    /// </summary>
    internal sealed class KeyLocatorDialog : Form
    {
        private readonly int _pid;

        private TextBox _txtKey;
        private TextBox _txtExpected;
        private ComboBox _cmbType;
        private TextBox _txtTolerance;
        private TextBox _txtWindow;
        private Button _btnLocate;
        private Label _labStatus;
        private ListView _lvHits;
        private TextBox _txtWriteValue;
        private Button _btnWrite;
        private Button _btnSaveBook;

        private readonly List<KeyHit> _hits = new List<KeyHit>();

        /// <summary>用户选择要存进地址收藏的项（绝对地址，仅本次有效）。</summary>
        public List<SavedAddress> Selected { get; private set; }

        public KeyLocatorDialog(int processId)
        {
            _pid = processId;
            Selected = new List<SavedAddress>();
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "按键定位（hashtable 键）";
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(780, 460);
            MinimumSize = new Size(680, 380);

            Label lab;

            lab = new Label(); lab.Text = "键值(hex)"; lab.Location = new Point(12, 16); lab.AutoSize = true;
            Controls.Add(lab);
            _txtKey = new TextBox();
            _txtKey.SetBounds(88, 12, 110, 25);
            _txtKey.Text = "E59561E4";
            Controls.Add(_txtKey);

            lab = new Label(); lab.Text = "界面显示值"; lab.Location = new Point(212, 16); lab.AutoSize = true;
            Controls.Add(lab);
            _txtExpected = new TextBox();
            _txtExpected.SetBounds(292, 12, 90, 25);
            Controls.Add(_txtExpected);

            lab = new Label(); lab.Text = "类型"; lab.Location = new Point(394, 16); lab.AutoSize = true;
            Controls.Add(lab);
            _cmbType = new ComboBox();
            _cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbType.SetBounds(432, 12, 90, 25);
            _cmbType.Items.AddRange(new object[] { "Float", "Int32", "UInt16" });
            _cmbType.SelectedIndex = 0;
            Controls.Add(_cmbType);

            lab = new Label(); lab.Text = "容差"; lab.Location = new Point(534, 16); lab.AutoSize = true;
            Controls.Add(lab);
            _txtTolerance = new TextBox();
            _txtTolerance.SetBounds(570, 12, 60, 25);
            _txtTolerance.Text = "0.05";
            Controls.Add(_txtTolerance);

            lab = new Label(); lab.Text = "窗口±"; lab.Location = new Point(640, 16); lab.AutoSize = true;
            Controls.Add(lab);
            _txtWindow = new TextBox();
            _txtWindow.SetBounds(692, 12, 72, 25);
            _txtWindow.Text = "100";
            Controls.Add(_txtWindow);

            _btnLocate = new Button();
            _btnLocate.Text = "定位(&L)";
            _btnLocate.SetBounds(12, 48, 110, 30);
            _btnLocate.Click += delegate { RunLocate(); };
            Controls.Add(_btnLocate);

            lab = new Label();
            lab.Text = "填“界面显示值”是为了在键周围认出哪个是目标；偏移会列在结果里。";
            lab.Location = new Point(134, 54);
            lab.AutoSize = true;
            lab.ForeColor = Color.Gray;
            Controls.Add(lab);

            _labStatus = new Label();
            _labStatus.SetBounds(12, 86, 756, 20);
            _labStatus.Text = "未开始";
            _labStatus.ForeColor = Color.DimGray;
            Controls.Add(_labStatus);

            _lvHits = new ListView();
            _lvHits.SetBounds(12, 110, 756, 280);
            _lvHits.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _lvHits.View = View.Details;
            _lvHits.FullRowSelect = true;
            _lvHits.GridLines = true;
            _lvHits.MultiSelect = false;
            _lvHits.Columns.Add("键地址", 110);
            _lvHits.Columns.Add("偏移", 90);
            _lvHits.Columns.Add("值地址", 110);
            _lvHits.Columns.Add("当前值", 140);
            _lvHits.Columns.Add("说明", 280);
            Controls.Add(_lvHits);

            lab = new Label(); lab.Text = "要写入的值"; lab.Location = new Point(12, 406); lab.AutoSize = true;
            lab.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(lab);

            _txtWriteValue = new TextBox();
            _txtWriteValue.SetBounds(96, 402, 110, 25);
            _txtWriteValue.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(_txtWriteValue);

            _btnWrite = new Button();
            _btnWrite.Text = "写入(&W)";
            _btnWrite.SetBounds(216, 400, 92, 30);
            _btnWrite.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnWrite.Click += delegate { WriteValue(); };
            Controls.Add(_btnWrite);

            _btnSaveBook = new Button();
            _btnSaveBook.Text = "存入收藏(&S)";
            _btnSaveBook.SetBounds(318, 400, 120, 30);
            _btnSaveBook.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnSaveBook.Click += delegate { SaveToBook(); };
            Controls.Add(_btnSaveBook);

            Button btnClose = new Button();
            btnClose.Text = "完成(&C)";
            btnClose.SetBounds(676, 400, 92, 30);
            btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);
        }

        private ScanValueType SelectedType()
        {
            return (ScanValueType)_cmbType.SelectedIndex;
        }

        private static int ParseHexInt(string text, int fallback)
        {
            int value;
            text = (text ?? "").Trim();
            if (text.StartsWith("0x") || text.StartsWith("0X")) text = text.Substring(2);
            if (int.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.NumberFormatInfo.InvariantInfo, out value))
                return value;
            return fallback;
        }

        private void RunLocate()
        {
            if (_pid <= 0)
            {
                _labStatus.Text = "游戏未运行，无法定位。";
                return;
            }

            uint key = unchecked((uint)ParseHexInt(_txtKey.Text, unchecked((int)0xE59561E4)));
            int window = Math.Max(0x10, ParseHexInt(_txtWindow.Text, 0x100));
            double tolerance;
            if (!double.TryParse(_txtTolerance.Text.Trim(), out tolerance) || tolerance < 0) tolerance = 0;

            ScanValueType type = SelectedType();

            _hits.Clear();
            _lvHits.Items.Clear();
            _btnLocate.Enabled = false;
            _labStatus.Text = "定位中…";

            try
            {
                List<KeyHit> hits = KeyLocator.Locate(
                    _pid, key, type, _txtExpected.Text.Trim(), tolerance,
                    window, window, 500,
                    delegate(string s) { _labStatus.Text = s; Application.DoEvents(); });

                _hits.AddRange(hits);

                _lvHits.BeginUpdate();
                try
                {
                    foreach (KeyHit hit in _hits)
                    {
                        ListViewItem row = new ListViewItem("0x" + hit.KeyAddress.ToString("X8"));
                        row.SubItems.Add(hit.DeltaText);
                        row.SubItems.Add("0x" + hit.ValueAddress.ToString("X8"));
                        row.SubItems.Add(hit.ValueText);
                        row.SubItems.Add("值 = 键地址 " + hit.DeltaText);
                        row.Tag = hit;
                        _lvHits.Items.Add(row);
                    }
                }
                finally
                {
                    _lvHits.EndUpdate();
                }

                if (_hits.Count == 0)
                {
                    _labStatus.Text = "没找到匹配。请确认：①“界面显示值”填得和游戏里一致 ②类型选对（带小数点是 Float）"
                        + " ③把“窗口±”调大（如 200）再试。";
                }
                else
                {
                    _labStatus.Text = string.Format("找到 {0} 处候选。选一行 → 填“要写入的值” → 点“写入”。"
                        + "记下偏移，以后每次开局重新定位即可。", _hits.Count);
                }
            }
            catch (Exception ex)
            {
                _labStatus.Text = "定位失败：" + ex.Message;
            }
            finally
            {
                _btnLocate.Enabled = true;
                Application.DoEvents();
            }
        }

        private KeyHit SelectedHit()
        {
            if (_lvHits.SelectedItems.Count == 0) return null;
            return _lvHits.SelectedItems[0].Tag as KeyHit;
        }

        private void WriteValue()
        {
            KeyHit hit = SelectedHit();
            if (hit == null)
            {
                _labStatus.Text = "请先在结果里选中一行。";
                return;
            }

            string text = _txtWriteValue.Text.Trim();
            if (text.Length == 0)
            {
                _labStatus.Text = "请填“要写入的值”。";
                return;
            }

            try
            {
                ScanValueType type = SelectedType();
                uint raw;
                if (!MemoryScanner.TryParseValue(type, text, out raw))
                    throw new ArgumentException("数值格式不对：" + text);

                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(_pid))
                {
                    if (type == ScanValueType.UInt16)
                        mem.WriteUInt16((IntPtr)hit.ValueAddress, (ushort)raw);
                    else
                        mem.WriteUInt32((IntPtr)hit.ValueAddress, raw);

                    uint check = type == ScanValueType.UInt16
                        ? mem.ReadUInt16((IntPtr)hit.ValueAddress)
                        : mem.ReadUInt32((IntPtr)hit.ValueAddress);
                    hit.ValueText = MemoryScanner.FormatValue(type, check, 1);
                }

                if (_lvHits.SelectedItems.Count > 0)
                    _lvHits.SelectedItems[0].SubItems[3].Text = hit.ValueText;

                _labStatus.Text = string.Format("已写入 0x{0:X8}，回游戏看界面是否变化。",
                    hit.ValueAddress);
            }
            catch (Exception ex)
            {
                _labStatus.Text = "写入失败：" + ex.Message;
            }
        }

        private void SaveToBook()
        {
            KeyHit hit = SelectedHit();
            if (hit == null)
            {
                _labStatus.Text = "请先在结果里选中一行。";
                return;
            }

            SavedAddress item = new SavedAddress();
            item.Name = "声望/键定位 " + _txtKey.Text.Trim() + " " + hit.DeltaText;
            item.ValueType = SelectedType();
            item.ValueScale = 1;
            // 按键定位模式：存「键 + 偏移」，重启游戏后点“重新定位”即可恢复
            item.KeyHash = unchecked((uint)ParseHexInt(_txtKey.Text, unchecked((int)0xE59561E4)));
            item.KeyOffset = hit.Delta;
            item.KeyValueHint = _txtExpected.Text.Trim();
            item.Note = "按键定位；重启后点收藏页的“重新定位”按钮即可恢复";
            Selected.Add(item);

            _labStatus.Text = "已加入待保存列表（按键定位模式），点“完成”后写入地址收藏。";
        }
    }
}
