using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace War3Trainer
{
    static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            InstallCrashLogging();
            ApplyCommandLine(args);

            Application.EnableVisualStyles();
            //Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(StartupTabIndex(args)));
        }

        /// <summary>
        /// 命令行参数：
        ///   --tab=N      启动时选中页签（0 功能修改 / 1 内存搜索 / 2 地址收藏）
        ///   --book=路径  指定收藏文件位置（便携模式：把收藏夹放在程序旁边）
        /// </summary>
        private static void ApplyCommandLine(string[] args)
        {
            if (args == null) return;
            foreach (string arg in args)
            {
                if (arg == null) continue;
                string text = arg.Trim();
                if (!text.StartsWith("--book=", StringComparison.OrdinalIgnoreCase)) continue;

                string path = text.Substring(7).Trim().Trim('"');
                if (path.Length > 0) AddressBook.OverridePath = path;
            }
        }

        /// <summary>支持 --tab=N 直接打开某个页签（0 功能修改 / 1 内存搜索 / 2 地址收藏）。</summary>
        private static int StartupTabIndex(string[] args)
        {
            if (args == null) return 0;
            foreach (string arg in args)
            {
                if (arg == null) continue;
                string text = arg.Trim();
                if (!text.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase)) continue;

                int index;
                if (int.TryParse(text.Substring(6), out index) && index >= 0 && index <= 2)
                    return index;
            }
            return 0;
        }

        /// <summary>
        /// 记录未处理异常。界面上出问题时，日志文件是唯一的线索
        /// （程序带 requireAdministrator 清单，控制台看不到任何输出）。
        /// </summary>
        private static void InstallCrashLogging()
        {
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                WriteCrashLog("UI 线程未处理异常", e.Exception);
                MessageBox.Show(
                    "程序遇到错误：" + Environment.NewLine + Environment.NewLine + e.Exception.Message
                    + Environment.NewLine + Environment.NewLine + "详细信息已写入：" + Environment.NewLine + CrashLogPath(),
                    "War3Trainer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                WriteCrashLog("非 UI 线程未处理异常", e.ExceptionObject as Exception);
            };

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        }

        internal static string CrashLogPath()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "War3Trainer");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "crash.log");
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            }
        }

        private static void WriteCrashLog(string title, Exception ex)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("========================================");
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + title);
                sb.AppendLine("版本: " + Application.ProductVersion);
                sb.AppendLine("OS: " + Environment.OSVersion.VersionString);
                if (ex != null)
                {
                    sb.AppendLine(ex.GetType().FullName + ": " + ex.Message);
                    sb.AppendLine(ex.StackTrace);
                }
                else
                {
                    sb.AppendLine("(没有异常对象)");
                }
                sb.AppendLine();

                File.AppendAllText(CrashLogPath(), sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 记日志失败就算了，不能因此再抛异常
            }
        }
    }
}
