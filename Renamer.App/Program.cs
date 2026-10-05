using System.Text;
using Renamer.Core;

namespace Renamer.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10) || !Environment.Is64BitProcess)
                throw new UserError("此版本适用于 Windows 10/11 64 位，请使用对应系统运行。");
            using var mutex = new Mutex(true, "Local\\BatchRenameAssistant_1", out var first);
            if (!first) { MessageBox.Show("软件已经运行，请使用已打开的窗口。", "批量文件改名助手", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ReportFailure(e.Exception, "界面操作未完成");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => SaveFailure(e.ExceptionObject as Exception ?? new Exception("未知异常"));
            using var form = new MainForm();
            if (args.Length == 2 && args[0] == "--ui-smoke")
            {
                var folder = args[1];
                form.Shown += async (_, _) =>
                {
                    try { await form.RunSmokeAsync(folder); }
                    catch (Exception ex) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "ui-smoke.txt"), "FAIL\n" + ex); Environment.ExitCode = 1; }
                    finally { form.Close(); }
                };
            }
            Application.Run(form);
        }
        catch (Exception ex) { Environment.ExitCode = 1; ReportFailure(ex, "软件启动或运行失败"); }
    }
    static void ReportFailure(Exception ex, string title)
    {
        var log = SaveFailure(ex);
        MessageBox.Show(BatchEngine.ChineseError(ex) + "\n\n本程序已包含运行时，无需安装 Python、Node 或开发环境。请保留原文件及操作日志。\n" +
            (log.Length > 0 ? "诊断信息已保存：\n" + log : "诊断日志也无法写入，请检查用户目录和临时目录的访问权限。"), title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    static string SaveFailure(Exception ex)
    {
        foreach (var folder in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "批量文件改名助手", "诊断日志"), Path.GetTempPath() })
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "改名助手诊断_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                writer.WriteLine("批量文件改名助手 1.2"); writer.WriteLine(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
                writer.WriteLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription); writer.WriteLine(ex); return path;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return "";
    }
}
