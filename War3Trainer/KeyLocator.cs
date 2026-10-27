using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace War3Trainer
{
    /// <summary>一次「按键定位」的结果。</summary>
    internal sealed class KeyHit
    {
        public uint KeyAddress;      // 键常量所在地址
        public int Delta;            // 值相对键的偏移（可能是负数）
        public uint ValueAddress;    // 值所在地址
        public string ValueText = "";

        public string DeltaText
        {
            get { return (Delta >= 0 ? "+0x" : "-0x") + Math.Abs(Delta).ToString("X"); }
        }
    }

    /// <summary>
    /// 按键定位（指针链的保底方案）。
    ///
    /// 背景：地图把「声望」存进 hashtable 时，是
    ///     SaveReal(YDHT, GetHandleId(玩家), 0xE59561E4, 值)
    /// 于是内存里存在一个「键常量 + 值」的结构。键 0xE59561E4 是个很稀有的 32 位常量，
    /// 直接搜它就能定位到结构；再在它周围找出数值等于界面上显示的那个位置，
    /// 就得到「值 = 键地址 + 偏移」这条确定性关系。
    ///
    /// 与指针链的分工：指针链能保存下来一劳永逸；这个方法每次开局点一下，
    /// 但**不依赖**任何结构布局假设，因此更稳、必中（只要你知道界面显示的数字）。
    /// </summary>
    internal static class KeyLocator
    {
        /// <summary>搜索键常量，返回它出现的所有地址。</summary>
        public static List<uint> FindKeyAddresses(int processId, uint key, int maxResults, Action<string> progress)
        {
            using (MemoryScanner scanner = new MemoryScanner(processId))
            {
                scanner.MaxResults = maxResults;
                scanner.Progress = progress;
                bool truncated;
                // 键是 32 位整数，用十六进制文本精确搜索（搜索框支持 0x 前缀）
                scanner.FirstScan(ScanValueType.Int32, "0x" + key.ToString("X"),
                    false, 0, 0, out truncated);
                return new List<uint>(scanner.GetAddresses());
            }
        }

        /// <summary>
        /// 在键地址周围寻找数值匹配的位置。
        /// expectedText 为空时不筛值，只把窗口内的浮点候选列出来（可能很多）。
        /// </summary>
        public static List<KeyHit> Locate(
            int processId, uint key,
            ScanValueType valueType, string expectedText, double tolerance,
            int windowBefore, int windowAfter, int maxHits,
            Action<string> progress)
        {
            List<KeyHit> hits = new List<KeyHit>();

            uint expected = 0;
            bool hasExpected = false;
            if (!string.IsNullOrEmpty(expectedText))
                hasExpected = MemoryScanner.TryParseValue(valueType, expectedText, out expected);

            if (progress != null) progress("正在搜索键 0x" + key.ToString("X") + " …");
            List<uint> keyAddresses = FindKeyAddresses(processId, key, 50000, progress);
            if (progress != null) progress(string.Format("键命中 {0} 处，正在检查各自邻近内存…", keyAddresses.Count));
            if (keyAddresses.Count == 0) return hits;

            int size = Math.Max(8, windowBefore + windowAfter);

            using (WindowsApi.ProcessMemory mem = new WindowsApi.ProcessMemory(processId))
            {
                foreach (uint keyAddr in keyAddresses)
                {
                    uint start = keyAddr;
                    if (windowBefore > 0)
                        start = (keyAddr >= (uint)windowBefore) ? keyAddr - (uint)windowBefore : 0;
                    start &= 0xFFFFFFFCu;

                    int read;
                    byte[] buffer = mem.ReadBytes((IntPtr)start, size, out read);
                    if (read < 4) continue;

                    for (int i = 0; i + 4 <= read; i += 4)
                    {
                        uint raw = BitConverter.ToUInt32(buffer, i);
                        if (hasExpected && !MatchValue(valueType, raw, expected, tolerance)) continue;
                        if (!hasExpected && valueType != ScanValueType.Float && valueType != ScanValueType.Int32) continue;

                        KeyHit hit = new KeyHit();
                        hit.KeyAddress = keyAddr;
                        hit.ValueAddress = unchecked(start + (uint)i);
                        hit.Delta = unchecked((int)(hit.ValueAddress - keyAddr));
                        hit.ValueText = MemoryScanner.FormatValue(valueType, raw, 1);
                        hits.Add(hit);

                        if (hits.Count >= maxHits) return hits;
                    }
                }
            }

            return hits;
        }

        /// <summary>数值匹配：浮点带容差，整数与 16 位精确比较，Char4 按字节。</summary>
        internal static bool MatchValue(ScanValueType type, uint raw, uint expected, double tolerance)
        {
            switch (type)
            {
                case ScanValueType.Float:
                    {
                        float a = ToFloat(raw);
                        float b = ToFloat(expected);
                        if (float.IsNaN(a) || float.IsNaN(b)) return false;
                        double diff = Math.Abs((double)a - (double)b);
                        double allowed = tolerance > 0
                            ? tolerance
                            : Math.Max(1f, Math.Abs(b)) * 1e-6;
                        return diff <= allowed;
                    }
                case ScanValueType.Int32:
                    if (tolerance > 0)
                        return Math.Abs((double)(int)raw - (double)(int)expected) <= tolerance;
                    return raw == expected;
                case ScanValueType.UInt16:
                    return (raw & 0xFFFF) == (expected & 0xFFFF);
                default:
                    return raw == expected;
            }
        }

        private static float ToFloat(uint raw)
        {
            byte[] bytes = BitConverter.GetBytes(raw);
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}
