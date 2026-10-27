using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace War3Trainer
{
    /// <summary>
    /// 一条收藏的地址。为了在游戏重启后依然有效，地址用「模块基址 + 偏移 + 多级指针链」表达，
    /// 而不是保存会变的绝对地址。
    ///
    /// 解析规则（与 Cheat Engine 的习惯一致）：
    ///   起点 = 模块基址 + ModuleOffset          （ModuleName 为空时，起点 = ModuleOffset，即绝对地址）
    ///   对 Offsets 里除最后一个以外的每个 o：起点 = [起点] + o
    ///   最终地址 = 起点 + Offsets[最后一个]
    ///   Offsets 为空时，最终地址 = 起点。
    /// </summary>
    public class SavedAddress
    {
        public string Name = "";
        public string ModuleName = "";
        public uint ModuleOffset = 0;
        public uint[] Offsets = new uint[0];
        public ScanValueType ValueType = ScanValueType.Int32;
        public int ValueScale = 1;
        public string Note = "";

        /// <summary>
        /// 定时重写：勾选后由工具栏的「定时重写」开关按设定间隔回写指定值，
        /// 抵抗地图脚本对数值的重新计算。（XML 属性名仍叫 frozen，兼容旧收藏文件。）
        /// </summary>
        public bool Frozen = false;

        /// <summary>定时重写时要回写的显示值（文本形式，与类型/倍数配合）。</summary>
        public string FrozenValue = "";

        /// <summary>
        /// 按键定位模式：hashtable 子键常量（0 表示不用这个模式）。
        /// 「键 + 固定偏移 = 值」这个关系在同一张图里跨重启恒定，
        /// 所以每次开局重新搜一次键即可定位，比重启即失效的指针链可靠得多。
        /// </summary>
        public uint KeyHash = 0;

        /// <summary>值相对键地址的偏移（按键定位得到，可为负）。</summary>
        public int KeyOffset = 0;

        /// <summary>上次已知的显示值，用于在多个键命中里挑出正确的那一个。</summary>
        public string KeyValueHint = "";

        /// <summary>本次会话解析出的地址缓存（不持久化）。</summary>
        public uint CachedAddress = 0;
        public int CachedPid = 0;

        public bool IsKeyItem { get { return KeyHash != 0; } }

        public void InvalidateCache()
        {
            CachedAddress = 0;
            CachedPid = 0;
        }

        public SavedAddress Clone()
        {
            SavedAddress c = new SavedAddress();
            c.Name = Name;
            c.ModuleName = ModuleName;
            c.ModuleOffset = ModuleOffset;
            c.Offsets = (uint[])Offsets.Clone();
            c.ValueType = ValueType;
            c.ValueScale = ValueScale;
            c.Note = Note;
            c.Frozen = Frozen;
            c.FrozenValue = FrozenValue;
            c.KeyHash = KeyHash;
            c.KeyOffset = KeyOffset;
            c.KeyValueHint = KeyValueHint;
            return c;
        }

        /// <summary>可读的地址表达式，例如 game.dll+1E8 +[1C,70]，或 键E59561E4 +0x40。</summary>
        public string Describe()
        {
            if (IsKeyItem)
            {
                return "键" + KeyHash.ToString("X8")
                    + (KeyOffset >= 0 ? " +0x" : " -0x") + Math.Abs(KeyOffset).ToString("X");
            }

            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(ModuleName))
                sb.Append(ModuleName).Append('+');
            sb.Append("0x").Append(ModuleOffset.ToString("X"));

            if (Offsets != null && Offsets.Length > 0)
            {
                sb.Append(" +[");
                for (int i = 0; i < Offsets.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("0x").Append(Offsets[i].ToString("X"));
                }
                sb.Append(']');
            }
            return sb.ToString();
        }
    }

    /// <summary>模块基址缓存（进程 + 模块名 -> 基址）。</summary>
    internal sealed class ModuleBaseCache
    {
        private readonly Dictionary<string, uint> _cache = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private int _pid;

        public void Reset(int processId)
        {
            _pid = processId;
            _cache.Clear();
        }

        public uint GetBase(int processId, string moduleName)
        {
            if (processId != _pid) Reset(processId);
            if (string.IsNullOrEmpty(moduleName)) return 0;

            uint cached;
            if (_cache.TryGetValue(moduleName, out cached)) return cached;

            WindowsApi.ProcessModule module = new WindowsApi.ProcessModule(processId, moduleName);
            uint baseAddress = (uint)module.BaseAddress;
            _cache[moduleName] = baseAddress;
            return baseAddress;
        }

        /// <summary>取模块基址与大小（找不到时返回 false，不抛异常）。</summary>
        public bool TryGetModule(int processId, string moduleName, out uint baseAddress, out int size)
        {
            baseAddress = 0;
            size = 0;
            if (string.IsNullOrEmpty(moduleName)) return false;
            try
            {
                WindowsApi.ProcessModule module = new WindowsApi.ProcessModule(processId, moduleName);
                baseAddress = (uint)module.BaseAddress;
                size = module.SizeOfImage;
                if (processId != _pid) Reset(processId);
                _cache[moduleName] = baseAddress;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>地址收藏夹：一组 SavedAddress，可存成 XML 反复使用。</summary>
    internal sealed class AddressBook
    {
        public const string FileName = "addresses.xml";

        private readonly List<SavedAddress> _items = new List<SavedAddress>();

        /// <summary>
        /// 收藏文件路径覆盖（命令行 --book= 指定）。为空时用 %APPDATA%\War3Trainer\addresses.xml，
        /// 不可写则退回程序目录。指定后可用于“便携模式”（收藏夹跟着程序走）。
        /// </summary>
        public static string OverridePath;

        public List<SavedAddress> Items { get { return _items; } }

        public void Clear() { _items.Clear(); }

        /// <summary>默认保存位置：%APPDATA%\War3Trainer\addresses.xml，不可写时退回程序目录。</summary>
        public static string DefaultPath()
        {
            if (!string.IsNullOrEmpty(OverridePath))
                return OverridePath;

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "War3Trainer");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, FileName);
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            }
        }

        /// <summary>把「绝对地址 + 模块基址」换算成模块相对偏移，便于下次启动复用。</summary>
        public static SavedAddress FromAbsoluteAddress(
            string name, uint absoluteAddress, string moduleName, uint moduleBase,
            ScanValueType type, int scale)
        {
            SavedAddress item = new SavedAddress();
            item.Name = name;
            item.ValueType = type;
            item.ValueScale = scale;

            if (!string.IsNullOrEmpty(moduleName) && moduleBase > 0 && absoluteAddress >= moduleBase)
            {
                item.ModuleName = moduleName;
                item.ModuleOffset = absoluteAddress - moduleBase;
            }
            else
            {
                item.ModuleName = "";
                item.ModuleOffset = absoluteAddress;
            }
            item.Offsets = new uint[0];
            return item;
        }

        //////////////////////////////////////////////////////////////////////
        // 解析
        //////////////////////////////////////////////////////////////////////

        /// <summary>解析出最终地址；失败返回 0。</summary>
        public static uint Resolve(SavedAddress item, int processId, ModuleBaseCache cache, out string error)
        {
            error = null;
            if (item == null) { error = "空条目"; return 0; }

            // 按键定位条目：键 + 偏移。缓存有效就直接用；无效则提示需要重新定位
            // （重新搜键要扫一遍内存，不能塞在每秒刷新的路径里做）。
            if (item.IsKeyItem)
            {
                if (item.CachedPid == processId && item.CachedAddress != 0
                    && ProbeReadable(processId, item.CachedAddress))
                    return item.CachedAddress;

                error = "需要重新定位（点“重新定位”按钮）";
                return 0;
            }

            try
            {
                uint pointer;
                if (string.IsNullOrEmpty(item.ModuleName))
                {
                    pointer = item.ModuleOffset;
                }
                else
                {
                    uint moduleBase = cache.GetBase(processId, item.ModuleName);
                    if (moduleBase == 0) { error = "找不到模块 " + item.ModuleName; return 0; }
                    pointer = unchecked(moduleBase + item.ModuleOffset);
                }

                if (item.Offsets == null || item.Offsets.Length == 0)
                    return pointer;

                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(processId))
                {
                    for (int i = 0; i < item.Offsets.Length - 1; i++)
                    {
                        uint next = mem.ReadUInt32((IntPtr)pointer);
                        if (next == 0) { error = "指针链在第 " + (i + 1) + " 级为 0"; return 0; }
                        pointer = unchecked(next + item.Offsets[i]);
                    }
                    return unchecked(pointer + item.Offsets[item.Offsets.Length - 1]);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return 0;
            }
        }

        public static uint Resolve(SavedAddress item, int processId, ModuleBaseCache cache)
        {
            string error;
            return Resolve(item, processId, cache, out error);
        }

        //////////////////////////////////////////////////////////////////////
        // 按键定位（跨重启依然有效的定位方式）
        //////////////////////////////////////////////////////////////////////

        /// <summary>地址是否可读（用于判断缓存是否还有效）。</summary>
        private static bool ProbeReadable(int processId, uint address)
        {
            try
            {
                using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(processId))
                {
                    int read;
                    mem.ReadBytes((IntPtr)address, 4, out read);
                    return read == 4;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 重新定位所有「按键定位」条目：搜键 → 用偏移算出值地址 → 写进缓存。
        /// 多个键命中时，用 KeyValueHint（上次已知的显示值）挑最接近的那个。
        /// </summary>
        public static string RelocateKeyItems(
            IEnumerable<SavedAddress> items, int processId, Action<string> progress)
        {
            if (processId <= 0) return "游戏未运行";

            List<SavedAddress> targets = new List<SavedAddress>();
            foreach (SavedAddress item in items)
            {
                if (item != null && item.IsKeyItem) targets.Add(item);
            }
            if (targets.Count == 0) return "没有按键定位条目";

            int ok = 0;
            List<string> failures = new List<string>();

            using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(processId))
            {
                for (int t = 0; t < targets.Count; t++)
                {
                    SavedAddress item = targets[t];
                    if (progress != null)
                        progress(string.Format("正在定位「{0}」（{1}/{2}）…", item.Name, t + 1, targets.Count));

                    item.InvalidateCache();
                    try
                    {
                        List<uint> keys = KeyLocator.FindKeyAddresses(processId, item.KeyHash, 20000, null);
                        if (keys.Count == 0)
                        {
                            failures.Add(item.Name + "：没找到键 " + item.KeyHash.ToString("X8"));
                            continue;
                        }

                        double hint;
                        bool hasHint = double.TryParse(item.KeyValueHint,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out hint);

                        uint best = 0;
                        double bestScore = double.MaxValue;
                        int size = MemoryScanner.SizeOf(item.ValueType);

                        foreach (uint keyAddr in keys)
                        {
                            uint addr = unchecked(keyAddr + (uint)item.KeyOffset);
                            int read;
                            byte[] data = mem.ReadBytes((IntPtr)addr, Math.Max(4, size), out read);
                            if (read < size) continue;

                            uint raw = size == 2
                                ? (uint)BitConverter.ToUInt16(data, 0)
                                : BitConverter.ToUInt32(data, 0);

                            if (!hasHint)
                            {
                                best = addr;
                                break;
                            }

                            double value = ToDouble(item.ValueType, raw);
                            double score = Math.Abs(value - hint);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = addr;
                            }
                        }

                        if (best == 0)
                        {
                            failures.Add(item.Name + "：键命中处读不到值（偏移可能不对）");
                            continue;
                        }

                        item.CachedAddress = best;
                        item.CachedPid = processId;
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(item.Name + "：" + ex.Message);
                    }
                }
            }

            string summary = string.Format("重新定位完成：成功 {0}/{1}", ok, targets.Count);
            if (failures.Count > 0)
                summary += "；失败：" + string.Join("；", failures.ToArray());
            return summary;
        }

        internal static double ToDouble(ScanValueType type, uint raw)
        {
            byte[] bytes = BitConverter.GetBytes(raw);
            switch (type)
            {
                case ScanValueType.Float: return BitConverter.ToSingle(bytes, 0);
                case ScanValueType.UInt16: return raw & 0xFFFF;
                default: return unchecked((int)raw);
            }
        }

        //////////////////////////////////////////////////////////////////////
        // 读写
        //////////////////////////////////////////////////////////////////////

        public int Load(string path)
        {
            _items.Clear();
            if (string.IsNullOrEmpty(path)) path = DefaultPath();
            if (!File.Exists(path)) return 0;

            XmlDocument doc = new XmlDocument();
            doc.Load(path);

            XmlNode root = doc.SelectSingleNode("addressBook");
            if (root == null) return 0;

            foreach (XmlNode node in root.SelectNodes("address"))
            {
                SavedAddress item = new SavedAddress();
                item.Name = Attr(node, "name");

                // 兼容两种写法：module="game.dll" offset="1E8"  或  modules 更复杂的形式
                item.ModuleName = Attr(node, "module");
                item.ModuleOffset = ParseHex(Attr(node, "offset"));

                string typeText = Attr(node, "type");
                item.ValueType = ParseType(typeText);

                int scale;
                if (!int.TryParse(Attr(node, "scale"), out scale) || scale < 1) scale = 1;
                item.ValueScale = scale;

                item.Note = Attr(node, "note");
                item.Frozen = string.Equals(Attr(node, "frozen"), "true", StringComparison.OrdinalIgnoreCase);
                item.FrozenValue = Attr(node, "frozenValue");

                item.KeyHash = ParseHex(Attr(node, "keyHash"));
                item.KeyOffset = ParseSignedHex(Attr(node, "keyOffset"));
                item.KeyValueHint = Attr(node, "keyHint");

                string offsetsText = Attr(node, "pointers");
                item.Offsets = ParseOffsets(offsetsText);

                _items.Add(item);
            }

            return _items.Count;
        }

        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) path = DefaultPath();

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            XmlDocument doc = new XmlDocument();
            XmlDeclaration decl = doc.CreateXmlDeclaration("1.0", "utf-8", null);
            doc.AppendChild(decl);

            XmlElement root = doc.CreateElement("addressBook");
            doc.AppendChild(root);

            foreach (SavedAddress item in _items)
            {
                XmlElement node = doc.CreateElement("address");
                node.SetAttribute("name", item.Name ?? "");
                node.SetAttribute("module", item.ModuleName ?? "");
                node.SetAttribute("offset", "0x" + item.ModuleOffset.ToString("X"));
                node.SetAttribute("type", item.ValueType.ToString());
                node.SetAttribute("scale", item.ValueScale.ToString());
                node.SetAttribute("pointers", FormatOffsets(item.Offsets));
                node.SetAttribute("note", item.Note ?? "");
                node.SetAttribute("frozen", item.Frozen ? "true" : "false");
                node.SetAttribute("frozenValue", item.FrozenValue ?? "");
                if (item.IsKeyItem)
                {
                    node.SetAttribute("keyHash", "0x" + item.KeyHash.ToString("X"));
                    node.SetAttribute("keyOffset",
                        (item.KeyOffset < 0 ? "-0x" : "0x") + Math.Abs(item.KeyOffset).ToString("X"));
                    node.SetAttribute("keyHint", item.KeyValueHint ?? "");
                }
                root.AppendChild(node);
            }

            // 先写临时文件再替换，避免写一半损坏收藏夹；
            // 同时留一份上一次的备份，万一误删还能找回。
            string temp = path + ".tmp";
            doc.Save(temp);
            if (File.Exists(path))
            {
                try
                {
                    File.Copy(path, path + ".bak", true);
                }
                catch
                {
                }
                File.Delete(path);
            }
            File.Move(temp, path);
        }

        //////////////////////////////////////////////////////////////////////
        // 辅助
        //////////////////////////////////////////////////////////////////////

        private static string Attr(XmlNode node, string name)
        {
            if (node == null || node.Attributes == null) return "";
            XmlAttribute a = node.Attributes[name];
            return a == null ? "" : a.Value;
        }

        private static uint ParseHex(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            text = text.Trim();
            if (text.StartsWith("0x") || text.StartsWith("0X")) text = text.Substring(2);

            uint value;
            if (uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.NumberFormatInfo.InvariantInfo, out value))
                return value;

            // 也接受十进制
            if (uint.TryParse(text, out value)) return value;
            return 0;
        }

        /// <summary>解析带符号的十六进制偏移，例如 0x40 / -0x10。</summary>
        private static int ParseSignedHex(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            text = text.Trim();
            bool negative = text.StartsWith("-");
            if (negative || text.StartsWith("+")) text = text.Substring(1);
            if (text.StartsWith("0x") || text.StartsWith("0X")) text = text.Substring(2);

            int value;
            if (!int.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.NumberFormatInfo.InvariantInfo, out value))
                return 0;
            return negative ? -value : value;
        }

        private static ScanValueType ParseType(string text)
        {
            try
            {
                return (ScanValueType)Enum.Parse(typeof(ScanValueType), text, true);
            }
            catch
            {
                return ScanValueType.Int32;
            }
        }

        private static uint[] ParseOffsets(string text)
        {
            if (string.IsNullOrEmpty(text)) return new uint[0];

            string[] parts = text.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            List<uint> list = new List<uint>();
            foreach (string part in parts)
                list.Add(ParseHex(part));
            return list.ToArray();
        }

        private static string FormatOffsets(uint[] offsets)
        {
            if (offsets == null || offsets.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < offsets.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("0x").Append(offsets[i].ToString("X"));
            }
            return sb.ToString();
        }
    }
}
