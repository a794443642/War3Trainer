using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace War3Trainer
{
    /// <summary>可扫描/修改的数值类型。</summary>
    public enum ScanValueType
    {
        Int32 = 0,
        Float = 1,
        Char4 = 2,
        UInt16 = 3,
    }

    /// <summary>“再次扫描”的过滤方式。</summary>
    public enum ScanCompare
    {
        /// <summary>等于输入的数值</summary>
        ExactValue = 0,
        /// <summary>与上次扫描相比发生了变化</summary>
        Changed = 1,
        /// <summary>与上次扫描相比没有变化</summary>
        Unchanged = 2,
        /// <summary>比上次扫描时增大</summary>
        Increased = 3,
        /// <summary>比上次扫描时减小</summary>
        Decreased = 4,
    }

    /// <summary>4 字节原始数据的联合视图（避免 BitConverter 分配）。</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct RawUnion
    {
        [FieldOffset(0)] public uint U;
        [FieldOffset(0)] public int I;
        [FieldOffset(0)] public float F;
        [FieldOffset(0)] public ushort U16;
    }

    /// <summary>
    /// 通用内存扫描器（Cheat Engine 式“搜值”功能的核心）。
    ///
    /// 数据流：首次扫描 → 候选地址列表（+ 每个地址上次的值）；
    ///        再次扫描 → 用“等于/变化/未变化/增大/减小”过滤并更新上次的值。
    ///
    /// 只读取“已提交且可写”的内存区域；可选用 exeModuleOnly 把范围限制在
    /// Game.dll 模块内，这样结果里基本都是游戏自己的数据结构，噪声最小。
    /// </summary>
    internal sealed class MemoryScanner : IDisposable
    {
        // ---- Win32 常量 ----
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_IMAGE = 0x1000000;
        private const uint PAGE_NOACCESS = 0x01;
        private const uint PAGE_GUARD = 0x100;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_WRITECOPY = 0x08;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_EXECUTE_WRITECOPY = 0x80;

        private const int ChunkSize = 1 << 20;   // 每次 ReadProcessMemory 1MB
        private const int ReadWindow = 1 << 16;  // 按地址散读时的合并窗口 64KB

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(
            WindowsApi.SafeProcessHandle hProcess,
            IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer,
            IntPtr dwLength);

        private readonly WindowsApi.ProcessMemory _mem;
        private readonly int _pid;
        private readonly List<uint> _addresses = new List<uint>();
        private readonly List<uint> _previous = new List<uint>();
        private bool _disposed;

        /// <summary>结果数量上限，避免“未知初始值”扫描把内存吃光。</summary>
        public int MaxResults { get; set; }

        /// <summary>
        /// 匹配容差。界面显示会四舍五入的数值必须用它：
        /// 例如界面显示 113.0，真实值可能是 113.04，此时容差填 0.05 才能搜到。
        /// 0 表示精确匹配（默认）。
        /// </summary>
        public double Tolerance { get; set; }

        /// <summary>进度回调（同步扫描期间调用，UI 里用 Application.DoEvents 刷新）。</summary>
        public Action<string> Progress { get; set; }

        public MemoryScanner(int processId)
        {
            _pid = processId;
            _mem = new WindowsApi.ProcessMemory(processId);
            MaxResults = 1000000;
            Type = ScanValueType.Int32;
            Scale = 1;
        }

        public int ProcessId { get { return _pid; } }

        public int ResultCount { get { return _addresses.Count; } }

        /// <summary>
        /// 本次结果集是用「什么类型 + 什么倍数」扫出来的。
        ///
        /// 显示、再次扫描、写入必须都用这一对值，而不是界面上当前选中的下拉项：
        /// 否则用户扫完 Int32 后顺手把类型改成 Float，同一批地址就会被当成 float
        /// 重新解释 —— 列表显示成乱码，写入更是把错的 4 个字节直接写进游戏内存。
        /// </summary>
        public ScanValueType Type { get; private set; }

        /// <summary>本次扫描的倍数（显示值 = 原始值 / 倍数）。</summary>
        public int Scale { get; private set; }

        /// <summary>结果集里每个数值占几个字节。</summary>
        public int ItemSize { get { return SizeOf(Type); } }

        /// <summary>本次扫描实际读过的字节数 / 可写内存总量，用于如实告诉用户“到底扫了多少”。</summary>
        public long ScannedBytes { get; private set; }
        public long TotalWritableBytes { get; private set; }

        /// <summary>按结果集的类型/倍数把一个原始值格式化成界面文本。</summary>
        public string FormatResult(uint raw)
        {
            return FormatValue(Type, raw, Scale);
        }

        /// <summary>按结果集的类型批量读取这些地址的当前值。</summary>
        public uint[] ReadResultValues(uint[] addresses)
        {
            return ReadRawValues(addresses, SizeOf(Type));
        }

        public uint[] GetAddresses() { return _addresses.ToArray(); }

        public void Reset()
        {
            _addresses.Clear();
            _previous.Clear();
            ScannedBytes = 0;
            TotalWritableBytes = 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _mem.Dispose();
            }
        }

        //////////////////////////////////////////////////////////////////////
        // 数值解析 / 格式化
        //////////////////////////////////////////////////////////////////////

        /// <summary>把用户输入的文本解析成 4 字节原始值。</summary>
        public static bool TryParseValue(ScanValueType type, string text, out uint raw)
        {
            raw = 0;
            if (text == null) return false;
            text = text.Trim();
            if (text.Length == 0) return false;

            RawUnion u = new RawUnion();
            switch (type)
            {
                case ScanValueType.Int32:
                    {
                        int v;
                        if (!TryParseInt32(text, out v)) return false;
                        u.I = v;
                        raw = u.U;
                        return true;
                    }
                case ScanValueType.Float:
                    {
                        float v;
                        if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out v))
                        {
                            // 兼容中文区域设置里的小数点
                            if (!float.TryParse(text, out v)) return false;
                        }
                        u.F = v;
                        raw = u.U;
                        return true;
                    }
                case ScanValueType.UInt16:
                    {
                        ushort v;
                        if (!ushort.TryParse(text, out v)) return false;
                        raw = v;
                        return true;
                    }
                case ScanValueType.Char4:
                    {
                        byte[] b = Char4ToRawBytes(text);
                        raw = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
                        return true;
                    }
            }
            return false;
        }

        private static bool TryParseInt32(string text, out int value)
        {
            value = 0;
            text = text.Trim();
            bool negative = false;
            if (text.StartsWith("-"))
            {
                negative = true;
                text = text.Substring(1);
            }

            if (text.StartsWith("0x") || text.StartsWith("0X"))
            {
                int hex;
                if (!int.TryParse(text.Substring(2),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.NumberFormatInfo.InvariantInfo, out hex))
                    return false;
                value = negative ? -hex : hex;
                return true;
            }

            long dec;
            if (!long.TryParse(text, out dec)) return false;
            value = unchecked((int)(negative ? -dec : dec));
            return true;
        }

        /// <summary>按游戏内部约定把 4 个 ASCII 字符转成写入内存的字节序（倒序）。</summary>
        internal static byte[] Char4ToRawBytes(string text)
        {
            if (text.Length < 4) text = text + new string('\0', 4 - text.Length);
            byte[] buffer = Encoding.ASCII.GetBytes(text.Substring(0, 4));
            return new byte[] { buffer[3], buffer[2], buffer[1], buffer[0] };
        }

        /// <summary>把原始值格式化成界面显示文本（Char4 按游戏约定倒序还原）。</summary>
        public static string FormatValue(ScanValueType type, uint raw, int scale)
        {
            if (scale < 1) scale = 1;
            RawUnion u = new RawUnion();
            u.U = raw;

            switch (type)
            {
                case ScanValueType.Int32:
                    return (u.I / scale).ToString();
                case ScanValueType.Float:
                    {
                        float f = u.F / scale;
                        return f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                    }
                case ScanValueType.UInt16:
                    return u.U16.ToString();
                case ScanValueType.Char4:
                    {
                        byte[] bytes = BitConverter.GetBytes(raw);
                        return Encoding.ASCII.GetString(new byte[] { bytes[3], bytes[2], bytes[1], bytes[0] });
                    }
            }
            return raw.ToString();
        }

        /// <summary>把显示文本解析成原始值（会乘以 scale）。</summary>
        public static bool TryParseScaledValue(ScanValueType type, string text, int scale, out uint raw)
        {
            if (scale < 1) scale = 1;
            if (!TryParseValue(type, text, out raw)) return false;
            if (scale == 1) return true;

            RawUnion u = new RawUnion();
            u.U = raw;
            switch (type)
            {
                case ScanValueType.Int32:
                    u.I = u.I * scale;
                    raw = u.U;
                    return true;
                case ScanValueType.Float:
                    u.F = u.F * scale;
                    raw = u.U;
                    return true;
            }
            return true;
        }

        public static int SizeOf(ScanValueType type)
        {
            return type == ScanValueType.UInt16 ? 2 : 4;
        }

        //////////////////////////////////////////////////////////////////////
        // 扫描
        //////////////////////////////////////////////////////////////////////

        /// <summary>统计待扫描的内存区域（用于界面提示“本次将扫描多少 MB”）。</summary>
        public void GetRegionStats(bool moduleOnly, uint moduleBase, int moduleSize,
            out int regionCount, out long totalBytes)
        {
            regionCount = 0;
            totalBytes = 0;
            foreach (Region region in EnumRegions(moduleOnly, moduleBase, moduleSize))
            {
                regionCount++;
                totalBytes += region.Size;
            }
        }

        /// <summary>导出可写区域列表（指针链扫描需要遍历同样的区域）。</summary>
        public void GetRegions(out uint[] bases, out uint[] sizes, out uint[] types,
            bool moduleOnly = false, uint moduleBase = 0, int moduleSize = 0)
        {
            List<uint> bl = new List<uint>();
            List<uint> sl = new List<uint>();
            List<uint> tl = new List<uint>();
            foreach (Region region in EnumRegions(moduleOnly, moduleBase, moduleSize))
            {
                bl.Add(region.Base);
                sl.Add(region.Size);
                tl.Add(region.Type);
            }
            bases = bl.ToArray();
            sizes = sl.ToArray();
            types = tl.ToArray();
        }

        /// <summary>
        /// 首次扫描。valueText 为 null/空 时表示“未知初始值”，只把候选地址和当前值快照下来，
        /// 之后用“变化了 / 未变化 / 增大 / 减小”逐步缩小范围。
        /// </summary>
        /// <param name="moduleOnly">只扫描 Game.dll 所属模块范围。</param>
        /// <param name="moduleBase">moduleOnly 时使用的模块基址。</param>
        /// <param name="moduleSize">moduleOnly 时使用的模块大小。</param>
        /// <param name="truncated">结果是否因超过上限而被截断。</param>
        public int FirstScan(
            ScanValueType type,
            string valueText,
            bool moduleOnly,
            uint moduleBase,
            int moduleSize,
            out bool truncated)
        {
            return FirstScan(type, valueText, 1, moduleOnly, moduleBase, moduleSize, out truncated);
        }

        /// <summary>
        /// 首次扫描（带倍数）。valueText 是**界面上的值**，这里负责乘上 scale，
        /// 因此调用方不用自己换算成原始值再转回字符串（那一步是浮点精度和符号出错的源头）。
        /// </summary>
        public int FirstScan(
            ScanValueType type,
            string valueText,
            int scale,
            bool moduleOnly,
            uint moduleBase,
            int moduleSize,
            out bool truncated)
        {
            Reset();
            truncated = false;

            if (scale < 1) scale = 1;
            Type = type;
            Scale = scale;

            bool unknownInitial = string.IsNullOrEmpty(valueText);
            uint target = 0;
            if (!unknownInitial && !TryParseScaledValue(type, valueText, scale, out target))
                throw new ArgumentException("无法解析要搜索的数值：" + valueText);

            int itemSize = SizeOf(type);
            int regionIndex = 0;
            int regionTotal = 0;

            foreach (Region region in EnumRegions(moduleOnly, moduleBase, moduleSize))
            {
                regionTotal++;
                TotalWritableBytes += region.Size;
            }

            foreach (Region region in EnumRegions(moduleOnly, moduleBase, moduleSize))
            {
                // 扫描期间界面还在转（进度回调里会 DoEvents），窗体可能已经关掉、
                // 句柄已经释放：这里必须收手，否则会用已释放的进程句柄读到一堆 0。
                if (_disposed) return _addresses.Count;

                regionIndex++;
                if (Progress != null)
                    Progress(string.Format("扫描中 {0}/{1}  已找到 {2}", regionIndex, regionTotal, _addresses.Count));

                ulong regionStart = (ulong)region.Base;
                ulong regionEnd = regionStart + (ulong)region.Size;

                for (ulong pos = regionStart; pos < regionEnd; pos += (ulong)ChunkSize)
                {
                    int want = (int)Math.Min((ulong)ChunkSize, regionEnd - pos);
                    int got;
                    byte[] data = _mem.ReadBytes((IntPtr)(uint)pos, want, out got);
                    ScannedBytes += got;
                    if (got < itemSize) continue;

                    if (unknownInitial)
                    {
                        for (int i = 0; i + itemSize <= got; i += itemSize)
                        {
                            _addresses.Add((uint)(pos + (ulong)i));
                            _previous.Add(itemSize == 2
                                ? (uint)BitConverter.ToUInt16(data, i)
                                : BitConverter.ToUInt32(data, i));
                            if (_addresses.Count >= MaxResults) { truncated = true; return _addresses.Count; }
                        }
                    }
                    else
                    {
                        for (int i = 0; i + itemSize <= got; i += itemSize)
                        {
                            uint raw = itemSize == 2
                                ? (uint)BitConverter.ToUInt16(data, i)
                                : BitConverter.ToUInt32(data, i);

                            if (Matches(type, raw, target, Tolerance))
                            {
                                _addresses.Add((uint)(pos + (ulong)i));
                                _previous.Add(raw);
                                if (_addresses.Count >= MaxResults) { truncated = true; return _addresses.Count; }
                            }
                        }
                    }
                }
            }

            return _addresses.Count;
        }

        /// <summary>
        /// 再次扫描：用过滤条件缩小上次的结果集。
        ///
        /// 类型由首次扫描固定下来。传进来的 type 只用于拦“扫完改了类型”的误用，
        /// 不再参与计算 —— 否则同一批地址会被按另一种类型重新解释。
        /// </summary>
        public int NextScan(ScanCompare compare, ScanValueType type, string valueText)
        {
            if (type != Type)
                throw new InvalidOperationException(string.Format(
                    "再次扫描必须和首次扫描用同一种类型（首次是 {0}，现在是 {1}）。"
                    + "要换类型请重新「首次扫描」。", Type, type));

            return NextScan(compare, valueText);
        }

        /// <summary>再次扫描（类型/倍数沿用首次扫描）。</summary>
        public int NextScan(ScanCompare compare, string valueText)
        {
            if (_addresses.Count == 0) return 0;

            ScanValueType type = Type;
            int itemSize = SizeOf(type);
            uint target = 0;
            if (compare == ScanCompare.ExactValue)
            {
                if (!TryParseScaledValue(type, valueText, Scale, out target))
                    throw new ArgumentException("无法解析要搜索的数值：" + valueText);
            }

            uint[] addresses = _addresses.ToArray();
            uint[] previous = _previous.ToArray();
            uint[] current = ReadRawValues(addresses, itemSize);

            _addresses.Clear();
            _previous.Clear();

            for (int i = 0; i < addresses.Length; i++)
            {
                bool keep;
                switch (compare)
                {
                    case ScanCompare.ExactValue:
                        keep = Matches(type, current[i], target, Tolerance);
                        break;
                    case ScanCompare.Changed:
                        keep = !SameValue(type, current[i], previous[i]);
                        break;
                    case ScanCompare.Unchanged:
                        keep = SameValue(type, current[i], previous[i]);
                        break;
                    case ScanCompare.Increased:
                        keep = CompareValue(type, current[i], previous[i]) > 0;
                        break;
                    case ScanCompare.Decreased:
                        keep = CompareValue(type, current[i], previous[i]) < 0;
                        break;
                    default:
                        keep = false;
                        break;
                }

                if (keep)
                {
                    _addresses.Add(addresses[i]);
                    _previous.Add(current[i]);
                }
            }

            if (Progress != null)
                Progress(string.Format("筛选后剩余 {0} 个地址", _addresses.Count));

            return _addresses.Count;
        }

        /// <summary>批量读取指定地址的原始值（按 64KB 窗口合并读，避免逐个系统调用）。</summary>
        public uint[] ReadRawValues(uint[] addresses, int itemSize)
        {
            uint[] result = new uint[addresses.Length];
            if (addresses.Length == 0) return result;

            int i = 0;
            while (i < addresses.Length)
            {
                uint windowStart = addresses[i];

                // 窗口不能越过“当前这块已提交内存”的末尾：
                // 从命中地址往后一次读 64KB，只要跨进未提交/不可读的页，
                // ReadProcessMemory 就会整次失败、返回 0 字节 —— 表现就是
                // “扫描命中了一个地址，但列表里的当前值永远是 0”，
                // 而且再次扫描用“未变化/等于数值”还会把这个地址误筛掉。
                ulong span;
                if (!TryGetReadableSpan(windowStart, out span) || span < (ulong)itemSize)
                {
                    i++;                     // 这个地址本身确实读不到
                    continue;
                }

                ulong spaceLeft = 0x100000000UL - (ulong)windowStart;
                ulong limit = Math.Min((ulong)ReadWindow, Math.Min(span, spaceLeft));
                int want = (int)limit;
                if (want < itemSize) { i++; continue; }

                int read;
                byte[] data = _mem.ReadBytes((IntPtr)windowStart, want, out read);
                if (read < itemSize) { i++; continue; }   // 该窗口读不到（保护属性变化），跳过

                int consumed = 0;
                while (i < addresses.Length)
                {
                    long delta = (long)addresses[i] - (long)windowStart;
                    if (delta < 0) { i++; consumed++; continue; }          // 地址倒退，直接跳过
                    if (delta + itemSize > read) break;                    // 超出本窗口，开新窗口
                    result[i] = itemSize == 2
                        ? (uint)BitConverter.ToUInt16(data, (int)delta)
                        : BitConverter.ToUInt32(data, (int)delta);
                    i++;
                    consumed++;
                }

                if (consumed == 0) i++;   // 兜底：确保每轮至少前进一个地址，避免死循环
            }

            return result;
        }

        /// <summary>
        /// 从 address 起，在“同一块已提交且可读”的内存里还能读多少字节。
        /// 用来把批量读取的窗口限制在区域内部（见 ReadRawValues）。
        /// </summary>
        private bool TryGetReadableSpan(uint address, out ulong span)
        {
            span = 0;

            MEMORY_BASIC_INFORMATION mbi;
            int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            if (VirtualQueryEx(_mem.ProcessHandle, (IntPtr)address, out mbi, (IntPtr)mbiSize) == IntPtr.Zero)
                return false;

            if (mbi.State != MEM_COMMIT) return false;
            if ((mbi.Protect & PAGE_GUARD) != 0) return false;
            if (!IsReadable(mbi.Protect)) return false;

            ulong baseAddr = (ulong)mbi.BaseAddress;
            ulong end = baseAddr + (ulong)mbi.RegionSize;
            ulong start = (ulong)address;
            if (end <= start) return false;

            span = end - start;
            return true;
        }

        /// <summary>写入一个数值（自动按类型与倍数处理）。</summary>
        public void WriteValue(uint address, ScanValueType type, string text, int scale)
        {
            uint raw;
            if (!TryParseScaledValue(type, text, scale, out raw))
                throw new ArgumentException("无法解析要写入的数值：" + text);

            switch (type)
            {
                case ScanValueType.UInt16:
                    _mem.WriteUInt16((IntPtr)address, (ushort)raw);
                    break;
                case ScanValueType.Char4:
                    {
                        RawUnion u = new RawUnion();
                        u.U = raw;
                        byte[] bytes = BitConverter.GetBytes(raw);
                        _mem.WriteChar4((IntPtr)address,
                            Encoding.ASCII.GetString(new byte[] { bytes[3], bytes[2], bytes[1], bytes[0] }));
                        break;
                    }
                default:
                    _mem.WriteUInt32((IntPtr)address, raw);
                    break;
            }
        }

        public void WriteRaw(uint address, uint raw, ScanValueType type)
        {
            if (type == ScanValueType.UInt16)
                _mem.WriteUInt16((IntPtr)address, (ushort)raw);
            else
                _mem.WriteUInt32((IntPtr)address, raw);
        }

        /// <summary>读取单个地址的原始值。</summary>
        public uint ReadRaw(uint address, ScanValueType type)
        {
            int read;
            byte[] data = _mem.ReadBytes((IntPtr)address, SizeOf(type), out read);
            if (read < SizeOf(type)) return 0;
            return type == ScanValueType.UInt16
                ? (uint)BitConverter.ToUInt16(data, 0)
                : BitConverter.ToUInt32(data, 0);
        }

        public uint ReadUInt32(uint address)
        {
            return _mem.ReadUInt32((IntPtr)address);
        }

        //////////////////////////////////////////////////////////////////////
        // 比较辅助
        //////////////////////////////////////////////////////////////////////

        private static bool Matches(ScanValueType type, uint raw, uint target, double tolerance)
        {
            if (type == ScanValueType.Float)
            {
                RawUnion a = new RawUnion(); a.U = raw;
                RawUnion b = new RawUnion(); b.U = target;
                if (float.IsNaN(a.F) || float.IsNaN(b.F)) return false;

                double diff = Math.Abs((double)a.F - (double)b.F);
                // 没有指定容差时也留一点相对误差，避免 0.1 这类十进制表示带来的漏配
                double allowed = tolerance > 0
                    ? tolerance
                    : Math.Max(1f, Math.Abs(b.F)) * 1e-6;
                return diff <= allowed;
            }
            if (type == ScanValueType.UInt16)
            {
                int diff = (int)(raw & 0xFFFF) - (int)(target & 0xFFFF);
                return tolerance > 0 ? Math.Abs(diff) <= tolerance : diff == 0;
            }
            if (tolerance > 0)
                return Math.Abs((double)(int)raw - (double)(int)target) <= tolerance;

            return raw == target;
        }

        private static bool Matches(ScanValueType type, uint raw, uint target)
        {
            return Matches(type, raw, target, 0);
        }

        private static bool SameValue(ScanValueType type, uint a, uint b)
        {
            if (type == ScanValueType.Float)
            {
                RawUnion x = new RawUnion(); x.U = a;
                RawUnion y = new RawUnion(); y.U = b;
                if (float.IsNaN(x.F) && float.IsNaN(y.F)) return true;
                return x.F == y.F;
            }
            if (type == ScanValueType.UInt16)
                return (a & 0xFFFF) == (b & 0xFFFF);
            return a == b;
        }

        private static int CompareValue(ScanValueType type, uint a, uint b)
        {
            if (type == ScanValueType.Float)
            {
                RawUnion x = new RawUnion(); x.U = a;
                RawUnion y = new RawUnion(); y.U = b;
                return x.F.CompareTo(y.F);
            }
            if (type == ScanValueType.UInt16)
                return ((ushort)a).CompareTo((ushort)b);
            return ((int)a).CompareTo((int)b);
        }

        //////////////////////////////////////////////////////////////////////
        // 内存区域枚举
        //////////////////////////////////////////////////////////////////////

        private struct Region
        {
            public uint Base;
            public uint Size;
            public uint Type;
        }

        private IEnumerable<Region> EnumRegions(bool moduleOnly, uint moduleBase, int moduleSize)
        {
            if (moduleOnly && moduleSize > 0)
            {
                ulong start = moduleBase;
                ulong end = (ulong)moduleBase + (ulong)moduleSize;
                for (ulong pos = start; pos < end; pos += (ulong)ChunkSize)
                {
                    Region r = new Region();
                    r.Base = (uint)pos;
                    r.Size = (uint)Math.Min((ulong)ChunkSize, end - pos);
                    r.Type = MEM_IMAGE;
                    yield return r;
                }
                yield break;
            }

            MEMORY_BASIC_INFORMATION mbi;
            ulong address = 0;
            int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

            // 扫到 4GB（32 位目标的完整地址空间）。以前只扫到 0x80000000，
            // 打了 /LARGEADDRESSAWARE 的游戏（大内存 RPG 地图很常见）把数据放在
            // 2GB 以上时就会“什么都搜不到”。
            const ulong UserSpaceEnd = 0x100000000UL;

            while (address < UserSpaceEnd)
            {
                if (VirtualQueryEx(_mem.ProcessHandle, (IntPtr)(uint)address, out mbi, (IntPtr)mbiSize) == IntPtr.Zero)
                    break;

                ulong baseAddr = (ulong)mbi.BaseAddress;
                if (baseAddr >= UserSpaceEnd) break;   // 64 位目标进程，本工具不支持

                ulong regionSize = (ulong)mbi.RegionSize;
                if (regionSize == 0) break;

                if (mbi.State == MEM_COMMIT
                    && (mbi.Protect & PAGE_GUARD) == 0
                    && (mbi.Protect & PAGE_NOACCESS) == 0
                    && IsWritable(mbi.Protect))
                {
                    Region r = new Region();
                    r.Base = (uint)baseAddr;
                    r.Size = (uint)Math.Min(regionSize, UserSpaceEnd - baseAddr);
                    r.Type = mbi.Type;
                    if (r.Size > 0) yield return r;
                }

                address = baseAddr + regionSize;
                if (address <= baseAddr) break;
            }
        }

        private static bool IsWritable(uint protect)
        {
            uint p = protect & 0xFF;
            return p == PAGE_READWRITE || p == PAGE_WRITECOPY
                || p == PAGE_EXECUTE_READWRITE || p == PAGE_EXECUTE_WRITECOPY;
        }

        /// <summary>这个保护属性下能不能读（PAGE_EXECUTE 这类“只可执行”的不算）。</summary>
        private static bool IsReadable(uint protect)
        {
            uint p = protect & 0xFF;
            if (p == 0 || p == PAGE_NOACCESS || p == 0x10 /* PAGE_EXECUTE */) return false;
            return true;
        }
    }
}
