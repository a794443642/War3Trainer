using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace War3Trainer
{
    /// <summary>
    /// 一条「功能修改」页的定时重写任务。
    ///
    /// 功能修改页的地址是「打开游戏那一刻」算出来的绝对地址，游戏重启后会变；
    /// 因此这里不存地址，只存「哪个功能节点(nodeIndex) 下的哪一行(caption)」，
    /// 每次重写时再向 GameTrainer 要一次地址，重启后自动对上新的地址。
    /// </summary>
    internal sealed class TrainerRewriteTask
    {
        /// <summary>所属功能节点（GameTrainer 的 NodeIndex）。</summary>
        public int ParentIndex = -1;

        /// <summary>功能节点名，仅用于界面提示。</summary>
        public string OwnerName = "";

        /// <summary>行标题（如「力量」「金币」）。</summary>
        public string Caption = "";

        /// <summary>同一节点内同名行的序号，避免标题重复时写错行。</summary>
        public int Occurrence;

        public ScanValueType Type = ScanValueType.Int32;
        public int Scale = 1;

        /// <summary>要写回的原始值（已按倍数放大）。</summary>
        public uint Raw;

        /// <summary>界面显示值（用户填的那个数）。</summary>
        public string ValueText = "";

        /// <summary>已写入次数 / 最近一次失败原因，供界面提示。</summary>
        public int WriteCount;
        public string LastError;

        /// <summary>唯一键：节点 + 标题 + 同名序号。</summary>
        public string Key
        {
            get { return ParentIndex.ToString(CultureInfo.InvariantCulture) + "|" + Caption + "|" + Occurrence; }
        }

        /// <summary>按类型/倍数还原的显示值。</summary>
        public string DisplayValue()
        {
            return MemoryScanner.FormatValue(Type, Raw, Scale);
        }
    }

    /// <summary>把原始值写进目标进程（UInt16 只写 2 字节，其余按 4 字节）。</summary>
    internal static class RewriteWriter
    {
        public static void Write(WindowsApi.ProcessMemory mem, uint address, ScanValueType type, uint raw)
        {
            if (type == ScanValueType.UInt16)
                mem.WriteUInt16((IntPtr)address, (ushort)raw);
            else
                mem.WriteUInt32((IntPtr)address, raw);
        }
    }

    /// <summary>重写间隔的解析 / 格式化（界面允许手填，写错也不会崩）。</summary>
    internal static class RewriteInterval
    {
        public const int MinMs = 50;
        public const int MaxMs = 60000;
        public const int DefaultMs = 500;

        /// <summary>接受 "500"、"500ms"、"500 毫秒"、"0.5s"、"1 秒" 等写法。</summary>
        public static bool TryParse(string text, out int ms)
        {
            ms = DefaultMs;
            if (text == null) return false;
            text = text.Trim().ToLowerInvariant().Replace(" ", "");
            if (text.Length == 0) return false;

            bool seconds = false;
            if (text.EndsWith("ms") || text.EndsWith("毫秒"))
                text = text.Substring(0, text.Length - 2);
            else if (text.EndsWith("s") || text.EndsWith("秒"))
            {
                seconds = true;
                text = text.Substring(0, text.Length - 1);
            }

            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.TryParse(text, out value))
                return false;

            if (value <= 0) return false;

            double total = seconds ? value * 1000.0 : value;
            if (total < MinMs) total = MinMs;
            if (total > MaxMs) total = MaxMs;
            ms = (int)Math.Round(total);
            return true;
        }

        /// <summary>把毫秒格式化成界面文本（整秒用「秒」，其余用「ms」）。</summary>
        public static string Format(int ms)
        {
            if (ms <= 0) ms = DefaultMs;
            if (ms % 1000 == 0 && ms >= 1000)
                return (ms / 1000).ToString(CultureInfo.InvariantCulture) + " 秒";
            return ms.ToString(CultureInfo.InvariantCulture) + " ms";
        }

        /// <summary>界面下拉框里的候选项。</summary>
        public static readonly string[] Presets = new string[]
        {
            "100 ms", "250 ms", "500 ms", "1 秒", "2 秒", "5 秒"
        };
    }

    /// <summary>
    /// 定时重写的开关与间隔，保存成一个小文本文件，
    /// 位置跟着收藏夹走（便携模式 --book= 时也落在程序目录旁边）。
    /// </summary>
    internal static class RewriteSettings
    {
        public const string FileName = "rewrite.txt";

        public static string SettingsPath()
        {
            try
            {
                string bookPath = AddressBook.DefaultPath();
                string dir = System.IO.Path.GetDirectoryName(bookPath);
                if (!string.IsNullOrEmpty(dir)) return System.IO.Path.Combine(dir, FileName);
            }
            catch
            {
            }
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
        }

        /// <summary>读取开关/间隔；文件不存在或损坏时用默认值（开 + 500ms）。</summary>
        public static void Load(out bool enabled, out int intervalMs)
        {
            enabled = true;
            intervalMs = RewriteInterval.DefaultMs;

            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;

                foreach (string line in File.ReadAllLines(path))
                {
                    if (line == null) continue;
                    string text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("#") || text.StartsWith(";")) continue;

                    int eq = text.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = text.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = text.Substring(eq + 1).Trim();

                    if (key == "enabled")
                    {
                        enabled = value == "1"
                            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (key == "interval")
                    {
                        int parsed;
                        if (RewriteInterval.TryParse(value, out parsed)) intervalMs = parsed;
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>保存开关/间隔；失败静默（下次启动回到默认值，不影响修改功能）。</summary>
        public static void Save(bool enabled, int intervalMs)
        {
            try
            {
                string path = SettingsPath();
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(path,
                    "# War3Trainer 定时重写设置" + Environment.NewLine
                    + "enabled=" + (enabled ? "1" : "0") + Environment.NewLine
                    + "interval=" + intervalMs.ToString(CultureInfo.InvariantCulture) + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    /// <summary>一轮重写的统计结果（用于状态栏提示）。</summary>
    internal sealed class RewriteResult
    {
        public int TrainerWritten;
        public int BookWritten;
        public int Failed;
        public string FirstError = "";

        public int Written { get { return TrainerWritten + BookWritten; } }

        public string Describe()
        {
            string text = string.Format("已重写 {0} 条（功能修改 {1}，收藏 {2}）",
                Written, TrainerWritten, BookWritten);
            if (Failed > 0)
                text += string.Format("，失败 {0} 条", Failed);
            if (!string.IsNullOrEmpty(FirstError))
                text += "：" + FirstError;
            return text;
        }
    }

    /// <summary>
    /// 定时重写引擎：按固定间隔把「功能修改」页登记的值（以及收藏夹里勾了
    /// 「定时重写」的条目）重新写回内存，抵抗地图脚本对数值的重新计算。
    ///
    /// 只负责「要写什么」和「怎么写」，地址解析由调用方通过委托提供：
    /// 功能修改行要回头看 GameTrainer 的最新地址，收藏夹条目走 AddressBook 解析。
    /// </summary>
    internal sealed class RewriteEngine
    {
        private readonly List<TrainerRewriteTask> _trainerTasks = new List<TrainerRewriteTask>();

        /// <summary>全局开关；关掉时一个字节都不写（收藏夹的锁定也一起停）。</summary>
        public bool Enabled = true;

        /// <summary>重写间隔（毫秒）。</summary>
        public int IntervalMs = RewriteInterval.DefaultMs;

        /// <summary>「功能修改」页登记的重写任务。</summary>
        public List<TrainerRewriteTask> TrainerTasks { get { return _trainerTasks; } }

        public TrainerRewriteTask Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (TrainerRewriteTask task in _trainerTasks)
            {
                if (task != null && task.Key == key) return task;
            }
            return null;
        }

        /// <summary>按「节点 + 标题 + 同名序号」登记（已存在则更新数值）。返回该任务。</summary>
        public TrainerRewriteTask AddOrUpdate(
            int parentIndex, string ownerName, string caption, int occurrence,
            ScanValueType type, int scale, uint raw, string valueText)
        {
            string key = parentIndex.ToString(CultureInfo.InvariantCulture) + "|" + caption + "|"
                + occurrence.ToString(CultureInfo.InvariantCulture);

            TrainerRewriteTask task = Find(key);
            bool isNew = task == null;
            if (isNew)
            {
                task = new TrainerRewriteTask();
                task.ParentIndex = parentIndex;
                task.Caption = caption;
                task.Occurrence = occurrence;
            }

            task.OwnerName = ownerName;
            task.Type = type;
            task.Scale = scale;
            task.Raw = raw;
            task.ValueText = valueText;
            task.LastError = null;

            if (isNew) _trainerTasks.Add(task);
            return task;
        }

        public bool Remove(string key)
        {
            TrainerRewriteTask task = Find(key);
            if (task == null) return false;
            _trainerTasks.Remove(task);
            return true;
        }

        public int ClearTrainerTasks()
        {
            int count = _trainerTasks.Count;
            _trainerTasks.Clear();
            return count;
        }

        /// <summary>
        /// 执行一轮重写。resolveTrainer / resolveBook 返回 0 表示这条暂时解析不到地址
        /// （例如键定位条目还没重新定位），跳过即可，不算致命错误。
        /// </summary>
        public RewriteResult RunOnce(
            int pid,
            Func<TrainerRewriteTask, uint> resolveTrainer,
            IEnumerable<SavedAddress> bookItems,
            Func<SavedAddress, uint> resolveBook)
        {
            RewriteResult result = new RewriteResult();
            if (pid <= 0) return result;

            WindowsApi.ProcessMemory mem = null;
            try
            {
                if (resolveTrainer != null)
                {
                    foreach (TrainerRewriteTask task in _trainerTasks.ToArray())
                    {
                        if (task == null) continue;
                        task.LastError = null;

                        uint address = resolveTrainer(task);
                        if (address == 0)
                        {
                            result.Failed++;
                            if (result.FirstError.Length == 0)
                                result.FirstError = task.Caption + "：" + (task.LastError ?? "地址解析失败");
                            continue;
                        }

                        try
                        {
                            if (mem == null) mem = new WindowsApi.ProcessMemory(pid);
                            RewriteWriter.Write(mem, address, task.Type, task.Raw);
                            task.WriteCount++;
                            result.TrainerWritten++;
                        }
                        catch (Exception ex)
                        {
                            if (mem != null) { mem.Dispose(); mem = null; }
                            task.LastError = ex.Message;
                            result.Failed++;
                            if (result.FirstError.Length == 0) result.FirstError = task.Caption + "：" + ex.Message;
                        }
                    }
                }

                if (bookItems != null && resolveBook != null)
                {
                    foreach (SavedAddress item in bookItems)
                    {
                        if (item == null || !item.Frozen) continue;
                        if (string.IsNullOrEmpty(item.FrozenValue)) continue;

                        uint raw;
                        if (!MemoryScanner.TryParseScaledValue(item.ValueType, item.FrozenValue, item.ValueScale, out raw))
                        {
                            result.Failed++;
                            if (result.FirstError.Length == 0)
                                result.FirstError = item.Name + "：数值格式不对（" + item.FrozenValue + "）";
                            continue;
                        }

                        uint address = resolveBook(item);
                        if (address == 0)
                        {
                            result.Failed++;
                            if (result.FirstError.Length == 0) result.FirstError = item.Name + "：地址解析失败";
                            continue;
                        }

                        try
                        {
                            if (mem == null) mem = new WindowsApi.ProcessMemory(pid);
                            RewriteWriter.Write(mem, address, item.ValueType, raw);
                            result.BookWritten++;
                        }
                        catch (Exception ex)
                        {
                            if (mem != null) { mem.Dispose(); mem = null; }
                            result.Failed++;
                            if (result.FirstError.Length == 0) result.FirstError = item.Name + "：" + ex.Message;
                        }
                    }
                }
            }
            finally
            {
                if (mem != null) mem.Dispose();
            }

            return result;
        }

        /// <summary>当前登记了多少条重写任务（含收藏夹里勾了锁定的条目）。</summary>
        public int CountBookTasks(IEnumerable<SavedAddress> bookItems)
        {
            int count = 0;
            if (bookItems == null) return 0;
            foreach (SavedAddress item in bookItems)
            {
                if (item != null && item.Frozen && !string.IsNullOrEmpty(item.FrozenValue)) count++;
            }
            return count;
        }
    }
}
