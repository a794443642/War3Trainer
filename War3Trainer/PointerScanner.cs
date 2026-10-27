using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace War3Trainer
{
    /// <summary>指针链扫描参数。</summary>
    internal sealed class PointerScanOptions
    {
        /// <summary>额外的“静态范围”。正常运行时只需识别模块（game.dll 等）；
        /// 自测时用它把一块已知内存当作静态根，从而让结果可断言。</summary>
        public sealed class StaticRange
        {
            public string Name;
            public uint Base;
            public uint Size;
        }

        public List<StaticRange> ExtraStaticRanges = new List<StaticRange>();

        /// <summary>最大级数（链里最多几段偏移）。</summary>
        public int MaxDepth = 4;

        /// <summary>堆内每一级的最大偏移。结构体字段偏移一般很小，默认 4KB。</summary>
        public int MaxOffset = 0x1000;

        /// <summary>收敛到模块（静态地址）时允许的最大偏移。
        /// 句柄表之类会用「基址 + 句柄号*4」寻址，偏移可能很大，所以这里放宽。</summary>
        public int RootMaxOffset = 0x400000;

        /// <summary>同一个地址最多保留几条“谁指向我”的候选，防止组合爆炸。</summary>
        public int MaxParentsPerNode = 8;

        /// <summary>最多返回多少条链。</summary>
        public int MaxChains = 40;

        /// <summary>每一层最多保留多少个候选地址。</summary>
        public int MaxFrontier = 200000;

        /// <summary>只接受 4 字节对齐的指针值。堆上的结构体指针几乎都对齐，
        /// 这条能直接砍掉大部分噪声（默认开启）。</summary>
        public bool RequireAlignedPointers = true;

        /// <summary>一个指针值最多归属到几个目标地址（按偏移从小到大）。</summary>
        public int MaxAttributionsPerValue = 4;

        /// <summary>只认 ExtraStaticRanges 里的静态范围，不把模块当静态根。
        /// 仅用于自测（排除模块内噪声），生产环境保持 false。</summary>
        public bool IgnoreModules = false;
    }

    /// <summary>一条指针链（可保存进地址收藏，游戏重启后依然可用）。</summary>
    internal sealed class PointerChain
    {
        public string RootModule = "";
        public uint RootOffset;
        public uint[] Offsets = new uint[0];
        public uint Target;

        public int Depth { get { return Offsets == null ? 0 : Offsets.Length; } }

        public string Signature()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(RootModule).Append('+').Append(RootOffset.ToString("X"));
            foreach (uint o in Offsets) sb.Append(',').Append(o.ToString("X"));
            return sb.ToString();
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(RootModule)) sb.Append(RootModule).Append('+');
            sb.Append("0x").Append(RootOffset.ToString("X"));
            if (Offsets.Length > 0)
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

        public SavedAddress ToSavedAddress(string name, ScanValueType type, int scale)
        {
            SavedAddress item = new SavedAddress();
            item.Name = name;
            item.ModuleName = RootModule;
            item.ModuleOffset = RootOffset;
            item.Offsets = (uint[])Offsets.Clone();
            item.ValueType = type;
            item.ValueScale = scale;
            return item;
        }
    }

    /// <summary>
    /// 指针链扫描（Cheat Engine 的 Pointer Scan 思路，但只需要一个已知地址，不必反复重启）：
    ///
    ///   反复做一件事：在可写内存里找「哪个位置存着一个指针，指到目标地址附近」，
    ///   把那个位置当成新的目标继续往上找，直到某个位置落在模块（game.dll 等）里。
    ///   落在模块里的位置是静态的，于是整条路径可以写成
    ///       game.dll+0xAAAA  →  +[0x1C,0x70]
    ///   这种形式，游戏重启后依然有效。
    ///
    /// 之所以能这么找：结构体字段偏移通常很小，所以「指针值 ≤ 目标 ≤ 指针值+偏移上限」
    /// 这一条件足以把绝大多数无关指针排除掉（偏移上限越小，噪声越少）。
    /// </summary>
    internal sealed partial class PointerScanner : IDisposable
    {
        private sealed class Node
        {
            public uint Address;
            public uint Offset;          // *(Parent.Address) + Offset == Address
            public Node Parent;
            public bool IsStatic;
            public string ModuleName;
            public uint ModuleOffset;
        }

        private readonly WindowsApi.ProcessMemory _mem;
        private readonly int _pid;
        private readonly List<ModuleRange> _modules = new List<ModuleRange>();
        private uint[] _regionBases = new uint[0];
        private uint[] _regionSizes = new uint[0];
        private bool _disposed;

        public Action<string> Progress { get; set; }
        public Func<bool> IsCancelled { get; set; }

        private struct ModuleRange
        {
            public uint Base;
            public uint Size;
            public string Name;
        }

        public PointerScanner(int processId, uint moduleBase, int moduleSize, string moduleName)
        {
            _pid = processId;
            _mem = new WindowsApi.ProcessMemory(processId);
            LoadModules(moduleBase, moduleSize, moduleName);

            using (MemoryScanner ms = new MemoryScanner(processId))
            {
                uint[] bases, sizes, types;
                ms.GetRegions(out bases, out sizes, out types);
                _regionBases = bases;
                _regionSizes = sizes;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _mem.Dispose();
            }
        }

        public int ProcessId { get { return _pid; } }

        //////////////////////////////////////////////////////////////////////
        // 模块范围
        //////////////////////////////////////////////////////////////////////

        private void LoadModules(uint moduleBase, int moduleSize, string moduleName)
        {
            // 先登记调用方给的模块（找不到其它模块时至少还有它可用）
            if (moduleBase != 0 && moduleSize > 0)
            {
                ModuleRange r;
                r.Base = moduleBase;
                r.Size = (uint)moduleSize;
                r.Name = string.IsNullOrEmpty(moduleName) ? "game.dll" : moduleName;
                _modules.Add(r);
            }

            try
            {
                using (WindowsApi.SafeProcessHandle h = WindowsApi.NativeMethods.OpenProcessForReadWrite(_pid))
                {
                    if (h == null || h.IsInvalid) return;

                    int needed = 0;
                    IntPtr[] handles = new IntPtr[0];
                    WindowsApi.NativeMethods.EnumProcessModulesEx(
                        h, handles, 0, out needed, WindowsApi.NativeMethods.LIST_MODULES_ALL);
                    handles = new IntPtr[needed / IntPtr.Size];
                    if (!WindowsApi.NativeMethods.EnumProcessModulesEx(
                            h, handles, handles.Length * IntPtr.Size, out needed,
                            WindowsApi.NativeMethods.LIST_MODULES_ALL))
                        return;

                    foreach (IntPtr mh in handles)
                    {
                        System.Text.StringBuilder name = new System.Text.StringBuilder(260);
                        if (WindowsApi.NativeMethods.GetModuleBaseName(h, mh, name, name.Capacity) == 0)
                            continue;

                        WindowsApi.NativeMethods.NtModuleInfo info;
                        try
                        {
                            info = WindowsApi.ProcessModule.GetModuleInformationInfo(h, mh);
                        }
                        catch
                        {
                            continue;
                        }

                        ModuleRange r;
                        r.Base = (uint)info.BaseOfDll;
                        r.Size = (uint)info.SizeOfImage;
                        r.Name = name.ToString();

                        bool dup = false;
                        foreach (ModuleRange e in _modules)
                        {
                            if (e.Base == r.Base) { dup = true; break; }
                        }
                        if (!dup && r.Size > 0) _modules.Add(r);
                    }
                }
            }
            catch
            {
                // 枚举模块失败不影响主流程：仍然可以用调用方给的模块
            }
        }

        private bool IsInModule(uint address, out string moduleName, out uint moduleBase)
        {
            if (_extraStatic != null)
            {
                foreach (PointerScanOptions.StaticRange r in _extraStatic)
                {
                    if (r.Size == 0) continue;
                    if (address >= r.Base && address < r.Base + r.Size)
                    {
                        moduleName = r.Name;
                        moduleBase = r.Base;
                        return true;
                    }
                }
            }

            foreach (ModuleRange r in _modules)
            {
                if (_ignoreModules) break;
                if (address >= r.Base && address < r.Base + r.Size)
                {
                    moduleName = r.Name;
                    moduleBase = r.Base;
                    return true;
                }
            }
            moduleName = null;
            moduleBase = 0;
            return false;
        }

        private List<PointerScanOptions.StaticRange> _extraStatic;
        private bool _ignoreModules;

        private string DescribeModules()
        {
            StringBuilder sb = new StringBuilder();
            foreach (ModuleRange r in _modules)
                sb.Append(r.Name).Append('@').Append(r.Base.ToString("X")).Append(' ');
            return sb.ToString();
        }

        //////////////////////////////////////////////////////////////////////
        // 扫描
        //////////////////////////////////////////////////////////////////////

        // 说明：真正在用的实现见 PointerScannerChains.cs（路径枚举版）。
        // 下面这版是“每个节点只保留一个父亲”的早期实现，已被取代，保留仅作对照。
        private List<PointerChain> FindChainsLegacy(uint target, PointerScanOptions options)
        {
            if (options == null) options = new PointerScanOptions();
            _extraStatic = options.ExtraStaticRanges;

            List<PointerChain> chains = new List<PointerChain>();
            HashSet<string> seen = new HashSet<string>();

            Report(string.Format("模块: {0}", DescribeModules()));
            Report(string.Format("目标: 0x{0:X8}  可写区域 {1} 个", target, _regionBases.Length));

            Dictionary<uint, Node> nodeByAddr = new Dictionary<uint, Node>();
            Node targetNode = new Node();
            targetNode.Address = target;
            nodeByAddr[target] = targetNode;

            List<Node> frontier = new List<Node>();
            frontier.Add(targetNode);

            int chunk = 1 << 20;
            byte[] buffer = new byte[chunk];

            for (int level = 1; level <= options.MaxDepth; level++)
            {
                // 本层要“找父亲”的地址（还没有父亲、且不是静态的）
                List<Node> pending = new List<Node>();
                foreach (Node n in frontier)
                {
                    if (!n.IsStatic && n.Parent == null) pending.Add(n);
                }
                if (pending.Count == 0) break;

                pending.Sort(delegate(Node a, Node b) { return a.Address.CompareTo(b.Address); });
                uint[] addrs = new uint[pending.Count];
                for (int i = 0; i < pending.Count; i++) addrs[i] = pending[i].Address;

                // 每个地址当前已接受的候选数量
                Dictionary<uint, int> accepted = new Dictionary<uint, int>();
                // 本层收集到的全部候选：位置、指向的节点、偏移
                List<uint> candLoc = new List<uint>();
                List<uint> candChild = new List<uint>();
                List<uint> candOff = new List<uint>();
                const int CandidateLimit = 3000000;
                bool candidateOverflow = false;

                long scanned = 0;
                for (int ri = 0; ri < _regionBases.Length; ri++)
                {
                    if (IsCancelled != null && IsCancelled())
                    {
                        Report("已取消");
                        break;
                    }

                    ulong regionStart = _regionBases[ri];
                    ulong regionEnd = regionStart + _regionSizes[ri];

                    for (ulong pos = regionStart; pos < regionEnd; pos += (ulong)chunk)
                    {
                        int want = (int)Math.Min((ulong)chunk, regionEnd - pos);
                        int got;
                        byte[] data = _mem.ReadBytes((IntPtr)(uint)pos, want, out got);
                        if (got < 4) continue;

                        scanned += got;

                        for (int i = 0; i + 4 <= got; i += 4)
                        {
                            uint v = BitConverter.ToUInt32(data, i);
                            if (v == 0) continue;
                            if (options.RequireAlignedPointers && (v & 3) != 0) continue;

                            int idx = LowerBound(addrs, v);
                            if (idx >= addrs.Length) continue;

                            uint off = addrs[idx] - v;
                            uint location = (uint)(pos + (ulong)i);

                            int allowed = options.MaxOffset;
                            string modName;
                            uint modBase;
                            if (IsInModule(location, out modName, out modBase))
                                allowed = options.RootMaxOffset;

                            // 允许一个指针值同时归属到窗口内的多个目标地址（取最近的若干个）。
                            // 只归给“最小的那个 ≥ 它的目标”会把正确目标的候选抢给别的地址。
                            uint hiLimit = v + (uint)allowed;
                            int attributed = 0;
                            for (int t = idx; t < addrs.Length; t++)
                            {
                                if (addrs[t] > hiLimit) break;
                                if (attributed >= options.MaxAttributionsPerValue) break;

                                candLoc.Add(location);
                                candChild.Add(addrs[t]);
                                candOff.Add(addrs[t] - v);
                                attributed++;

                                if (candLoc.Count >= CandidateLimit)
                                {
                                    candidateOverflow = true;
                                    break;
                                }
                            }

                            if (candidateOverflow) break;
                        }

                        if (candidateOverflow) break;
                    }

                    if (candidateOverflow) break;
                    if (IsCancelled != null && IsCancelled()) break;
                    if ((ri & 0x0F) == 0)
                        Report(string.Format("第 {0} 级：已扫描 {1:0.0} MB，候选 {2} 个",
                            level, scanned / 1048576.0, candLoc.Count));
                }

                if (candidateOverflow)
                    Report("候选数量达到上限，结果可能不完整");

                // 同一个节点最多保留 MaxParentsPerNode 个“谁指向我”，按偏移从小到大取
                // （字段偏移通常很小，所以小偏移优先能显著提高命中率）
                Dictionary<uint, List<int>> byChild = new Dictionary<uint, List<int>>();
                for (int i = 0; i < candChild.Count; i++)
                {
                    List<int> list;
                    if (!byChild.TryGetValue(candChild[i], out list))
                    {
                        list = new List<int>();
                        byChild[candChild[i]] = list;
                    }
                    list.Add(i);
                }

                List<KeyValuePair<Node, uint>> selected = new List<KeyValuePair<Node, uint>>();
                List<uint> selectedLoc = new List<uint>();
                foreach (KeyValuePair<uint, List<int>> kv in byChild)
                {
                    List<int> idxs = kv.Value;
                    idxs.Sort(delegate(int a, int b) { return candOff[a].CompareTo(candOff[b]); });

                    int take = Math.Min(options.MaxParentsPerNode, idxs.Count);
                    for (int k = 0; k < take; k++)
                    {
                        int i = idxs[k];
                        selectedLoc.Add(candLoc[i]);
                        selected.Add(new KeyValuePair<Node, uint>(nodeByAddr[kv.Key], candOff[i]));
                    }
                }

                Report(string.Format("第 {0} 级：候选 {1} 个地址，保留 {2} 条走向",
                    level, candLoc.Count, selected.Count));

                // 建立父子关系
                List<Node> next = new List<Node>();
                int staticHits = 0;
                for (int si = 0; si < selected.Count; si++)
                {
                    uint location = selectedLoc[si];
                    Node child = selected[si].Key;
                    uint off = selected[si].Value;

                    if (child.Parent != null) continue;   // 已有父亲，保留偏移最小的那条
                    child.Offset = off;

                    Node parent;
                    if (!nodeByAddr.TryGetValue(location, out parent))
                    {
                        parent = new Node();
                        parent.Address = location;
                        nodeByAddr[location] = parent;
                    }

                    child.Parent = parent;

                    string modName;
                    uint modBase;
                    if (IsInModule(location, out modName, out modBase))
                    {
                        parent.IsStatic = true;
                        parent.ModuleName = modName;
                        parent.ModuleOffset = location - modBase;
                        staticHits++;
                    }
                    else if (next.Count < options.MaxFrontier)
                    {
                        next.Add(parent);
                    }
                }

                Report(string.Format("第 {0} 级完成：其中落在模块内 {1} 个", level, staticHits));

                TryEmitLegacy(targetNode, chains, seen, options.MaxChains);
                if (chains.Count >= options.MaxChains)
                {
                    Report("已达到链数量上限，停止搜索");
                    break;
                }

                frontier = next;
                if (frontier.Count == 0)
                {
                    Report("没有更多可追踪的指针，停止搜索");
                    break;
                }
            }

            TryEmitLegacy(targetNode, chains, seen, options.MaxChains);
            Report(string.Format("共找到 {0} 条可用指针链", chains.Count));
            return chains;
        }

        /// <summary>从目标节点向上走，若到达静态节点则产出一条链。</summary>
        private void TryEmitLegacy(Node targetNode, List<PointerChain> chains, HashSet<string> seen, int maxChains)
        {
            if (targetNode == null || targetNode.Parent == null) return;

            List<uint> offsets = new List<uint>();
            Node cur = targetNode;
            int guard = 0;
            while (cur != null && !cur.IsStatic && guard++ < 64)
            {
                offsets.Add(cur.Offset);
                cur = cur.Parent;
            }

            if (cur == null || !cur.IsStatic) return;   // 没走到静态地址，不算数
            if (offsets.Count == 0) return;

            offsets.Reverse();

            PointerChain chain = new PointerChain();
            chain.RootModule = cur.ModuleName ?? "";
            chain.RootOffset = cur.ModuleOffset;
            chain.Offsets = offsets.ToArray();
            chain.Target = targetNode.Address;

            string sig = chain.Signature();
            if (seen.Contains(sig)) return;
            seen.Add(sig);
            chains.Add(chain);
        }

        /// <summary>在升序数组里找第一个 ≥ v 的下标。</summary>
        private static int LowerBound(uint[] sorted, uint v)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] < v) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private void Report(string message)
        {
            if (Progress != null) Progress(message);
        }
    }
}
