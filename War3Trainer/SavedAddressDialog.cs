using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace War3Trainer
{
    /// <summary>
    /// 收藏项编辑对话框：名称 / 模块 + 偏移 / 指针链 / 类型 / 倍数 / 定时重写 / 备注，
    /// 并可“测试读取”立即验证地址表达式是否解析得到数值。
    /// </summary>
    internal sealed class SavedAddressDialog : Form
    {
        private const string AbsoluteModuleText = "(绝对地址)";

        private readonly SavedAddress _item;
        private readonly int _processId;
        private readonly ModuleBaseCache _cache;

        private TextBox _txtName;
        private ComboBox _cmbModule;
        private TextBox _txtOffset;
        private TextBox _txtPointers;
        private ComboBox _cmbType;
        private TextBox _txtScale;
        private CheckBox _chkFrozen;
        private TextBox _txtKeyHash;
        private TextBox _txtKeyOffset;
        private TextBox _txtNote;
        private Label _labPreview;
        private Button _btnOk;

        public SavedAddress Result { get; private set; }

        public SavedAddressDialog(SavedAddress item, int processId, ModuleBaseCache cache)
        {
            _item = item ?? new SavedAddress();
            _processId = processId;
            _cache = cache;

            BuildUi();
            LoadItem(_item);
        }

        private void BuildUi()
        {
            const int left = 14;          // 标签列
            const int field = 104;        // 输入列起点
            const int rightEdge = 546;    // 输入列右边界
            int fieldWidth = rightEdge - field;

            Text = "收藏地址";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 418);
            Font = new Font("微软雅黑", 9F);

            // ---- 名称 ----
            Controls.Add(MakeLabel("名称", left, 20));
            _txtName = new TextBox();
            _txtName.SetBounds(field, 16, fieldWidth, 25);
            Controls.Add(_txtName);

            // ---- 模块 + 基址偏移 ----
            Controls.Add(MakeLabel("模块", left, 54));
            _cmbModule = new ComboBox();
            _cmbModule.DropDownStyle = ComboBoxStyle.DropDown;
            _cmbModule.SetBounds(field, 50, 180, 25);
            _cmbModule.Items.AddRange(new object[] { "game.dll", "war3.exe", AbsoluteModuleText });
            Controls.Add(_cmbModule);

            Controls.Add(MakeLabel("基址偏移", 300, 54));
            _txtOffset = new TextBox();
            _txtOffset.SetBounds(380, 50, 166, 25);
            Controls.Add(_txtOffset);

            // ---- 指针链 ----
            Controls.Add(MakeLabel("指针链", left, 88));
            _txtPointers = new TextBox();
            _txtPointers.SetBounds(field, 84, fieldWidth, 25);
            Controls.Add(_txtPointers);

            Label hint = MakeLabel("多级偏移，逗号分隔，如 1C,70（留空表示直接寻址）", field, 112);
            hint.ForeColor = Color.Gray;
            Controls.Add(hint);

            // ---- 类型 + 倍数 ----
            Controls.Add(MakeLabel("类型", left, 146));
            _cmbType = new ComboBox();
            _cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbType.SetBounds(field, 142, 120, 25);
            _cmbType.Items.AddRange(new object[] { "Int32", "Float", "Char4", "UInt16" });
            Controls.Add(_cmbType);

            Controls.Add(MakeLabel("倍数", 240, 146));
            _txtScale = new TextBox();
            _txtScale.SetBounds(284, 142, 60, 25);
            Controls.Add(_txtScale);

            // ---- 定时重写 ----
            _chkFrozen = new CheckBox();
            _chkFrozen.Text = "定时重写（按工具栏间隔回写）";
            _chkFrozen.SetBounds(field, 176, 260, 24);
            Controls.Add(_chkFrozen);

            Label lockHint = MakeLabel("勾选后由工具栏的「定时重写」开关按间隔持续回写「写入」时填的值，抵抗地图脚本重新计算",
                field, 200);
            lockHint.ForeColor = Color.Gray;
            Controls.Add(lockHint);

            // ---- 按键定位（hashtable 键 + 偏移）：留空表示不用这个模式 ----
            Controls.Add(MakeLabel("键值(hex)", left, 232));
            _txtKeyHash = new TextBox();
            _txtKeyHash.SetBounds(field, 228, 180, 25);
            Controls.Add(_txtKeyHash);

            Controls.Add(MakeLabel("键偏移", 300, 232));
            _txtKeyOffset = new TextBox();
            _txtKeyOffset.SetBounds(380, 228, 166, 25);
            Controls.Add(_txtKeyOffset);

            Label keyHint = MakeLabel(
                "填了键值即按键定位；重启后点收藏页「重新定位」恢复。"
                + "",
                field, 256);
            keyHint.ForeColor = Color.Gray;
            Controls.Add(keyHint);

            // ---- 备注 ----
            Controls.Add(MakeLabel("备注", left, 284));
            _txtNote = new TextBox();
            _txtNote.SetBounds(field, 280, fieldWidth, 25);
            Controls.Add(_txtNote);

            // ---- 预览 ----
            _labPreview = new Label();
            _labPreview.SetBounds(left, 312, 532, 40);
            _labPreview.Text = "";
            Controls.Add(_labPreview);

            // ---- 按钮 ----
            Button btnTest = new Button();
            btnTest.Text = "测试读取";
            btnTest.SetBounds(left, 364, 96, 30);
            btnTest.Click += delegate { TestRead(); };
            Controls.Add(btnTest);

            _btnOk = new Button();
            _btnOk.Text = "确定";
            _btnOk.SetBounds(346, 364, 90, 30);
            _btnOk.Click += delegate { OnOk(); };
            Controls.Add(_btnOk);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(442, 364, 90, 30);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = _btnOk;
            CancelButton = btnCancel;
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            Label lab = new Label();
            lab.Text = text;
            lab.Location = new Point(x, y);
            lab.AutoSize = true;
            return lab;
        }

        private void LoadItem(SavedAddress item)
        {
            _txtName.Text = item.Name;
            _cmbModule.Text = string.IsNullOrEmpty(item.ModuleName) ? AbsoluteModuleText : item.ModuleName;
            _txtOffset.Text = "0x" + item.ModuleOffset.ToString("X");
            _txtPointers.Text = FormatOffsets(item.Offsets);
            _cmbType.SelectedIndex = (int)item.ValueType;
            _txtScale.Text = item.ValueScale.ToString();
            _chkFrozen.Checked = item.Frozen;
            _txtNote.Text = item.Note;
            _txtKeyHash.Text = item.IsKeyItem ? item.KeyHash.ToString("X") : "";
            _txtKeyOffset.Text = item.IsKeyItem
                ? (item.KeyOffset < 0 ? "-0x" : "0x") + Math.Abs(item.KeyOffset).ToString("X")
                : "";
        }

        private static string FormatOffsets(uint[] offsets)
        {
            if (offsets == null || offsets.Length == 0) return "";
            string[] parts = new string[offsets.Length];
            for (int i = 0; i < offsets.Length; i++)
                parts[i] = offsets[i].ToString("X");
            return string.Join(",", parts);
        }

        public static bool TryParseHex(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return true;   // 空串视为 0
            text = text.Trim();
            if (text.StartsWith("0x") || text.StartsWith("0X")) text = text.Substring(2);
            if (text.Length == 0) return true;
            return uint.TryParse(text, NumberStyles.HexNumber, NumberFormatInfo.InvariantInfo, out value);
        }

        private bool BuildFromUi(out SavedAddress item, out string error)
        {
            item = null;
            error = null;

            if (string.IsNullOrEmpty(_txtName.Text.Trim()))
            {
                error = "请填写名称";
                return false;
            }

            uint offset;
            if (!TryParseHex(_txtOffset.Text, out offset))
            {
                error = "基址偏移不是合法的十六进制数";
                return false;
            }

            uint[] offsets;
            string pointerText = _txtPointers.Text.Trim();
            if (pointerText.Length == 0)
            {
                offsets = new uint[0];
            }
            else
            {
                string[] parts = pointerText.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                offsets = new uint[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!TryParseHex(parts[i], out offsets[i]))
                    {
                        error = "指针链第 " + (i + 1) + " 项不是合法的十六进制数";
                        return false;
                    }
                }
            }

            int scale;
            if (!int.TryParse(_txtScale.Text.Trim(), out scale) || scale < 1) scale = 1;

            string module = _cmbModule.Text.Trim();
            if (module == AbsoluteModuleText) module = "";

            SavedAddress result = _item.Clone();
            result.Name = _txtName.Text.Trim();
            result.ModuleName = module;
            result.ModuleOffset = offset;
            result.Offsets = offsets;
            result.ValueType = (ScanValueType)Math.Max(0, _cmbType.SelectedIndex);
            result.ValueScale = scale;
            result.Frozen = _chkFrozen.Checked;
            result.Note = _txtNote.Text.Trim();

            // 按键定位模式：填了「键值」就按“键 + 偏移”定位，忽略模块/指针链
            string keyText = _txtKeyHash.Text.Trim();
            if (keyText.Length > 0)
            {
                uint keyHash;
                if (!TryParseHex(keyText, out keyHash) || keyHash == 0)
                {
                    error = "键值不是合法的十六进制数（例如 E59561E4）";
                    return false;
                }

                string offText = _txtKeyOffset.Text.Trim();
                bool negative = offText.StartsWith("-");
                if (negative || offText.StartsWith("+")) offText = offText.Substring(1);

                uint absOffset;
                if (!TryParseHex(offText, out absOffset))
                {
                    error = "键偏移不是合法的十六进制数（例如 18 或 -0x10）";
                    return false;
                }

                result.KeyHash = keyHash;
                result.KeyOffset = negative ? -(int)absOffset : (int)absOffset;
                result.ModuleName = "";
                result.ModuleOffset = 0;
                result.Offsets = new uint[0];
            }
            else
            {
                result.KeyHash = 0;
                result.KeyOffset = 0;
            }

            item = result;
            return true;
        }

        private void TestRead()
        {
            SavedAddress item;
            string error;
            if (!BuildFromUi(out item, out error))
            {
                ShowPreviewError(error);
                return;
            }

            if (_processId <= 0)
            {
                ShowPreviewError("游戏未运行，无法测试。表达式：" + item.Describe());
                return;
            }

            string resolveError;
            uint address = AddressBook.Resolve(item, _processId, _cache, out resolveError);
            if (address == 0)
            {
                ShowPreviewError("解析失败：" + (resolveError ?? "地址为 0"));
                return;
            }

            try
            {
                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(_processId))
                {
                    uint raw = item.ValueType == ScanValueType.UInt16
                        ? mem.ReadUInt16((IntPtr)address)
                        : mem.ReadUInt32((IntPtr)address);

                    _labPreview.ForeColor = Color.ForestGreen;
                    string warning = "";
                    if (string.IsNullOrEmpty(item.ModuleName))
                    {
                        warning = Environment.NewLine
                            + "提示：这是绝对地址，游戏重启后会变；要长期有效请填「模块 + 偏移」或指针链。";
                    }
                    _labPreview.Text = string.Format("解析成功：0x{0:X8}（{1}）{2}当前值 = {3}",
                        address, item.Describe(), Environment.NewLine,
                        MemoryScanner.FormatValue(item.ValueType, raw, item.ValueScale)) + warning;
                }
            }
            catch (Exception ex)
            {
                ShowPreviewError("读取失败：" + ex.Message);
            }
        }

        private void ShowPreviewError(string message)
        {
            _labPreview.ForeColor = Color.Firebrick;
            _labPreview.Text = message;
        }

        private void OnOk()
        {
            SavedAddress item;
            string error;
            if (!BuildFromUi(out item, out error))
            {
                MessageBox.Show(this, error, "收藏地址", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Result = item;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
