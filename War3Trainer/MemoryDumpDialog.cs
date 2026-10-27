using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace War3Trainer
{
    /// <summary>
    /// 导出玩家结构体的十六进制内容，用于定位"随某个操作变化"的字段
    /// （例如：买一级科技前后各导一次，对比出科技等级数组的位置）。
    ///
    /// 玩家结构基址的算法与「游戏资源」节点完全一致：
    ///     upper = War3Common.ReadFromGameMemory(mem, ctx, args, 1) & 0xFFFF0000
    ///     玩家基址 = upper + 玩家基址表[玩家号]
    /// </summary>
    internal sealed class MemoryDumpDialog : Form
    {
        private static readonly uint[] PlayerBaseOffset = new uint[]
        {
            0,        // 占位
            0x0190, 0x1410, 0x26A0, 0x3920, 0x4BB0, 0x5E30,
            0x70C0, 0x8350, 0x95D0, 0xA860, 0xBAE0, 0xCD70
        };

        private readonly GameContext _ctx;

        private ComboBox _cmbPlayer;
        private TextBox _txtStart;
        private TextBox _txtLength;
        private TextBox _txtDump;
        private Label _labStatus;

        public MemoryDumpDialog(GameContext ctx)
        {
            _ctx = ctx;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "导出内存（用于对比定位）";
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 540);
            MinimumSize = new Size(640, 420);

            Label lab = new Label();
            lab.Text = "玩家";
            lab.Location = new Point(12, 16);
            lab.AutoSize = true;
            Controls.Add(lab);

            _cmbPlayer = new ComboBox();
            _cmbPlayer.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbPlayer.SetBounds(52, 12, 60, 25);
            for (int i = 1; i <= 12; i++) _cmbPlayer.Items.Add("玩家" + i);
            _cmbPlayer.SelectedIndex = 0;
            Controls.Add(_cmbPlayer);

            lab = new Label();
            lab.Text = "起始地址(hex，留空=该玩家结构基址)";
            lab.Location = new Point(126, 16);
            lab.AutoSize = true;
            Controls.Add(lab);

            _txtStart = new TextBox();
            _txtStart.SetBounds(320, 12, 110, 25);
            Controls.Add(_txtStart);

            lab = new Label();
            lab.Text = "长度(hex)";
            lab.Location = new Point(442, 16);
            lab.AutoSize = true;
            Controls.Add(lab);

            _txtLength = new TextBox();
            _txtLength.SetBounds(510, 12, 70, 25);
            _txtLength.Text = "800";
            Controls.Add(_txtLength);

            Button btnRead = new Button();
            btnRead.Text = "读取(&R)";
            btnRead.SetBounds(592, 10, 76, 28);
            btnRead.Click += delegate { DoDump(); };
            Controls.Add(btnRead);

            Button btnCopy = new Button();
            btnCopy.Text = "复制(&C)";
            btnCopy.SetBounds(672, 10, 76, 28);
            btnCopy.Click += delegate
            {
                try
                {
                    if (_txtDump.TextLength == 0) { _labStatus.Text = "还没有内容"; return; }
                    Clipboard.SetText(_txtDump.Text);
                    _labStatus.Text = "已复制到剪贴板";
                }
                catch (Exception ex) { _labStatus.Text = "复制失败：" + ex.Message; }
            };
            Controls.Add(btnCopy);

            _labStatus = new Label();
            _labStatus.SetBounds(12, 44, 736, 20);
            _labStatus.Text = "用法：买一级科技前点「读取」并「复制」；买完再读一次，两份对比，变化处就是等级数组。";
            _labStatus.ForeColor = Color.DimGray;
            Controls.Add(_labStatus);

            _txtDump = new TextBox();
            _txtDump.SetBounds(12, 68, 736, 460);
            _txtDump.Multiline = true;
            _txtDump.ReadOnly = true;
            _txtDump.ScrollBars = ScrollBars.Both;
            _txtDump.WordWrap = false;
            _txtDump.Font = new Font("Consolas", 8.5F);
            _txtDump.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_txtDump);
        }

        private static bool TryHex(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();
            if (text.StartsWith("0x") || text.StartsWith("0X")) text = text.Substring(2);
            if (text.Length == 0) return false;
            return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.NumberFormatInfo.InvariantInfo, out value);
        }

        private void DoDump()
        {
            if (_ctx == null)
            {
                _labStatus.Text = "游戏未运行";
                return;
            }

            try
            {
                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(_ctx.ProcessId))
                {
                    uint start;
                    if (!TryHex(_txtStart.Text, out start))
                    {
                        // 没填起始地址：按玩家结构基址算
                        NewChildrenEventArgs args = new NewChildrenEventArgs();
                        War3Common.GetGameMemory(_ctx, ref args);
                        if (args.ThisGameMemoryAddress == 0)
                        {
                            _labStatus.Text = "拿不到游戏内存基址（可能不在图里）";
                            return;
                        }

                        uint upper = War3Common.ReadFromGameMemory(mem, _ctx, args, 1) & 0xFFFF0000;
                        if (upper == 0)
                        {
                            _labStatus.Text = "玩家数据基址为 0（可能还没进游戏）";
                            return;
                        }

                        int player = Math.Max(0, _cmbPlayer.SelectedIndex);
                        start = unchecked(upper + PlayerBaseOffset[player + 1]);
                    }

                    uint len;
                    if (!TryHex(_txtLength.Text, out len) || len == 0) len = 0x800;
                    if (len > 0x10000) len = 0x10000;

                    // 一次读一大段时，只要中间有一页没提交，ReadProcessMemory 就整次失败。
                    // 这里失败就折半重试，尽量把能读的前半段显示出来。
                    int want = (int)len;
                    int read = 0;
                    byte[] data = null;
                    while (want >= 1)
                    {
                        data = mem.ReadBytes((IntPtr)start, want, out read);
                        if (read > 0) break;
                        want /= 2;
                    }

                    if (data == null || read <= 0)
                    {
                        _labStatus.Text = "读取失败（地址不可读）";
                        return;
                    }

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("起始 0x" + start.ToString("X8") + "   长度 0x" + read.ToString("X")
                        + (read < (int)len ? "（后面 0x" + ((int)len - read).ToString("X") + " 字节不可读，已截断）" : "")
                        + "   玩家 " + (Math.Max(0, _cmbPlayer.SelectedIndex) + 1));
                    sb.AppendLine();

                    for (int off = 0; off < read; off += 16)
                    {
                        sb.Append((start + (uint)off).ToString("X8")).Append("  ");
                        for (int j = 0; j < 16; j++)
                        {
                            if (off + j < read) sb.Append(data[off + j].ToString("X2")).Append(' ');
                            else sb.Append("   ");
                        }
                        sb.Append(' ');
                        for (int j = 0; j < 16 && off + j < read; j++)
                        {
                            byte b = data[off + j];
                            sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                        }
                        sb.AppendLine();
                    }

                    _txtDump.Text = sb.ToString();
                    _labStatus.Text = string.Format("已读取 0x{0:X} 字节（起始 0x{1:X8}）", read, start);
                }
            }
            catch (Exception ex)
            {
                _labStatus.Text = "读取失败：" + ex.Message;
            }
        }
    }
}
