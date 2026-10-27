using System;
using System.Collections.Generic;
using System.Text;

namespace War3Trainer
{
    /// <summary>
    /// PointerScanner 的路径枚举实现。
    ///
    /// 为什么不是“每个节点保留一个父亲”：一个堆地址往往有几百个位置存着指向它附近的指针，
    /// 正确的那条只是其中之一。只留一条会把正确答案丢掉，所以这里枚举**路径**：
    /// 每条路径记住「从目标地址一路向上的偏移序列」，遇到模块（静态）地址就产出一条可保存的指针链。
    /// </summary>
    internal sealed partial class PointerScanner
    {
        private sealed class ChainPath
        {
            public uint Current;    // 当前需要“找父亲”的地址
            public uint[] Up;       // 从目标向上的偏移序列（第 i 项对应第 i+1 级）
        }

        public List<PointerChain> FindChains(uint target, PointerScanOptions options)
        {
            if (options == null) options = new PointerScanOptions();
            _extraStatic = options.ExtraStaticRanges;
            _ignoreModules = options.IgnoreModules;

            List<PointerChain> chains = new List<PointerChain>();
            HashSet<string> chainSeen = new HashSet<string>();

            Report(string.Format("目标 0x{0:X8}；模块 {1} 个，可写区域 {2} 个",
                target, _modules.Count, _regionBases.Length));

            ChainPath start = new ChainPath();
            start.Current = target;
            start.Up = new uint[0];

            List<ChainPath> frontier = new List<ChainPath>();
            frontier.Add(start);

            const int chunk = 1 << 20;
            const int CandidateLimit = 3000000;

            for (int level = 1; level <= options.MaxDepth; level++)
            {
                if (chains.Count >= options.MaxChains) break;

                // 按地址把路径分组：同一个地址可能有多条不同历史的路径
                Dictionary<uint, List<ChainPath>> byAddr = new Dictionary<uint, List<ChainPath>>();
                foreach (ChainPath p in frontier)
                {
                    List<ChainPath> list;
                    if (!byAddr.TryGetValue(p.Current, out list))
                    {
                        list = new List<ChainPath>();
                        byAddr[p.Current] = list;
                    }
                    list.Add(p);
                }

                uint[] addrs = new uint[byAddr.Count];
                byAddr.Keys.CopyTo(addrs, 0);
                Array.Sort(addrs);

                List<uint> cLoc = new List<uint>();
                List<int> cIdx = new List<int>();
                List<uint> cOff = new List<uint>();
                bool overflow = false;
                long scanned = 0;

                for (int ri = 0; ri < _regionBases.Length; ri++)
                {
                    if (IsCancelled != null && IsCancelled()) { Report("已取消"); break; }

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

                            uint location = (uint)(pos + (ulong)i);

                            int allowed = options.MaxOffset;
                            string modName;
                            uint modBase;
                            if (IsInModule(location, out modName, out modBase))
                                allowed = options.RootMaxOffset;

                            uint hiLimit = v + (uint)allowed;
                            int attributed = 0;
                            for (int t = idx; t < addrs.Length; t++)
                            {
                                if (addrs[t] > hiLimit) break;
                                if (attributed >= options.MaxAttributionsPerValue) break;

                                cLoc.Add(location);
                                cIdx.Add(t);
                                cOff.Add(addrs[t] - v);
                                attributed++;

                                if (cLoc.Count >= CandidateLimit) { overflow = true; break; }
                            }

                            if (overflow) break;
                        }

                        if (overflow) break;
                    }

                    if (overflow) break;
                    if (IsCancelled != null && IsCancelled()) break;
                }

                if (overflow) Report("候选达到上限，结果可能不完整");
                Report(string.Format("第 {0} 级：扫描 {1:0.0} MB，候选 {2} 个（输入路径 {3} 条）",
                    level, scanned / 1048576.0, cLoc.Count, frontier.Count));

                // 展开：每个候选给“目标地址相同的所有路径”各生成一个后继
                List<ChainPath> next = new List<ChainPath>();
                HashSet<string> pathSeen = new HashSet<string>();
                int staticHits = 0;

                for (int i = 0; i < cLoc.Count; i++)
                {
                    if (chains.Count >= options.MaxChains) break;

                    uint location = cLoc[i];
                    uint off = cOff[i];
                    uint addr = addrs[cIdx[i]];

                    string modName;
                    uint modBase;
                    bool isStatic = IsInModule(location, out modName, out modBase);

                    List<ChainPath> paths;
                    if (!byAddr.TryGetValue(addr, out paths)) continue;

                    foreach (ChainPath p in paths)
                    {
                        uint[] up = Append(p.Up, off);

                        if (isStatic)
                        {
                            staticHits++;
                            PointerChain chain = new PointerChain();
                            chain.RootModule = modName ?? "";
                            chain.RootOffset = location - modBase;
                            chain.Offsets = BuildOffsets(up);
                            chain.Target = target;

                            string sig = chain.Signature();
                            if (chainSeen.Add(sig))
                            {
                                chains.Add(chain);
                                if (chains.Count >= options.MaxChains) break;
                            }
                        }
                        else
                        {
                            string sig = location.ToString("X") + "|" + Join(up);
                            if (!pathSeen.Add(sig)) continue;
                            if (next.Count >= options.MaxFrontier) continue;

                            ChainPath np = new ChainPath();
                            np.Current = location;
                            np.Up = up;
                            next.Add(np);
                        }
                    }
                }

                Report(string.Format("第 {0} 级完成：静态命中 {1}，累计链 {2}，下一层路径 {3}",
                    level, staticHits, chains.Count, next.Count));

                frontier = next;
                if (frontier.Count == 0)
                {
                    Report("没有更多可追踪的指针，停止搜索");
                    break;
                }
            }

            Report(string.Format("共找到 {0} 条指针链", chains.Count));
            return chains;
        }

        //////////////////////////////////////////////////////////////////////
        // 小工具
        //////////////////////////////////////////////////////////////////////

        private static uint[] Append(uint[] source, uint value)
        {
            uint[] result = new uint[source.Length + 1];
            Array.Copy(source, result, source.Length);
            result[source.Length] = value;
            return result;
        }

        private static uint[] Reverse(uint[] source)
        {
            uint[] result = new uint[source.Length];
            for (int i = 0; i < source.Length; i++)
                result[i] = source[source.Length - 1 - i];
            return result;
        }

        /// <summary>
        /// 把“从目标向上累积的偏移”翻译成 AddressBook 的偏移序列。
        ///
        /// AddressBook 的解析规则是：最后一段偏移**直接相加（不解引用）**，其余每段都是
        /// “先解引用再加偏移”。而指针链的最后一跳是「*(A1) + o1 == 目标」，
        /// 也就是必须对 A1 解引用一次。因此要在最后一段偏移之前插入一个 0：
        ///     [o_k, ..., o_2, 0, o_1]
        ///     最后一段 o_1 落在目标字段上，前面那个 0 负责把 A1 解引用出来。
        /// </summary>
        private static uint[] BuildOffsets(uint[] up)
        {
            uint[] rev = Reverse(up);
            uint[] result = new uint[rev.Length + 1];
            for (int i = 0; i < rev.Length - 1; i++)
                result[i] = rev[i];
            result[rev.Length - 1] = 0;
            result[rev.Length] = rev[rev.Length - 1];
            return result;
        }

        private static string Join(uint[] values)
        {
            StringBuilder sb = new StringBuilder();
            foreach (uint v in values) sb.Append(v.ToString("X")).Append('.');
            return sb.ToString();
        }
    }
}
