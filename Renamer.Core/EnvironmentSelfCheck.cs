using System.Runtime.InteropServices;
using System.Text;

namespace Renamer.Core;

public sealed record SelfCheckItem(string Name, string Status, string Details);
public sealed class SelfCheckReport
{
    public string WorkingDirectory { get; set; } = "";
    public string ReportPath => Path.Combine(WorkingDirectory, "自检报告.txt");
    public List<SelfCheckItem> Items { get; } = new();
    public string ToText() => "批量文件改名助手 — 本机环境与安全自检\r\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
        "\r\n系统：" + RuntimeInformation.OSDescription + "\r\n进程：" + RuntimeInformation.ProcessArchitecture + "；运行时：" + RuntimeInformation.FrameworkDescription +
        "\r\n自检目录：" + WorkingDirectory + "\r\n仅使用这里新建的模拟文件，没有读取所选源 / 目标目录。模拟文件及日志保留供检查。\r\n" +
        string.Join("\r\n\r\n", Items.Select(i => "[" + i.Status + "] " + i.Name + "\r\n" + i.Details)) +
        "\r\n\r\n本次自检只验证当前账户临时目录，不代表所有磁盘、网络盘、权限或断电情形。";
}
public static class EnvironmentSelfCheck
{
    public static async Task<SelfCheckReport> RunAsync(IRecycleBin recycleBin, CancellationToken token, IProgress<OperationProgress>? progress = null)
    {
        var report = new SelfCheckReport { WorkingDirectory = Path.Combine(Path.GetTempPath(), "BRA_Check_" + Guid.NewGuid().ToString("N")[..8]) };
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(report.WorkingDirectory);
        void Add(string name, string state, string message) => report.Items.Add(new(name, state, message));
        try
        {
            Add("运行环境", OperatingSystem.IsWindows() && Environment.Is64BitProcess ? "通过" : "提示", "应用运行时已加载。" + (OperatingSystem.IsWindows() ? "Windows x64 本机检查。" : "当前不是 Windows；不代表 Windows 原生验证。"));
            var source = Path.Combine(report.WorkingDirectory, "源"); var target = Path.Combine(report.WorkingDirectory, "目标"); Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            var original = Path.Combine(source, "U_4.000_nsweep_251.h5"); var bytes = new byte[65553]; new Random(1729).NextBytes(bytes);
            await using (var stream = new FileStream(original, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
            var expected = await BatchEngine.HashFile(original, token);
            Add("模拟文件读写", "通过", "已创建、落盘并读取 65553 字节，SHA-256：" + expected);
            var preset = new RulePreset(); var rules = new RuleEngine(preset);
            if (!rules.Matches("U_3.500_nsweep_251.h5", out _) || !rules.Matches("U_4.500_nsweep_251.h5", out _) || rules.Matches("U_1.000_nsweep_251.h5", out _) || rules.Rename("U_4.000_nsweep_251.h5", out _) != "U_4.000_nsweep_100.h5") throw new UserError("规则示例自检失败。");
            Add("筛选与命名规则", "通过", "包含 3.500 / 4.500，排除 1.000；只将 nsweep 改为 100。");
            var engine = new BatchEngine(Path.Combine(report.WorkingDirectory, "日志"), recycleBin);
            foreach (var kind in new[] { OperationKind.Copy, OperationKind.Rename, OperationKind.Move })
            {
                token.ThrowIfCancellationRequested(); progress?.Report(new("自检：" + kind, report.Items.Count, 8));
                var settings = new OperationSettings { SourceFolder = source, TargetFolder = target, Kind = kind };
                var rows = PreviewScanner.Scan(preset, settings, token).Rows.Where(r => r.Selected).ToArray();
                if (rows.Length != 1) throw new UserError("自检预览选择数量错误。");
                var batch = await engine.ExecuteAsync(rows, settings, Array.Empty<string>(), token, progress);
                if (await BatchEngine.HashFile(rows[0].TargetPath, token) != expected) throw new UserError("自检目标内容校验失败。");
                if ((kind == OperationKind.Copy) != File.Exists(original)) throw new UserError("自检源文件保留状态错误。");
                await engine.UndoAsync(batch, token, progress);
                if (await BatchEngine.HashFile(original, token) != expected || File.Exists(rows[0].TargetPath)) throw new UserError("自检撤销结果错误。");
                Add(kind switch { OperationKind.Copy => "复制及撤销", OperationKind.Move => "移动及撤销", _ => "原地改名及撤销" }, "通过", "源 / 目标 SHA-256 核验一致，撤销后恢复原名。");
                if (batch.Warnings.Count > 0) Add("回收站与保留文件", "提示", string.Join("\r\n", batch.Warnings));
            }
            var probe = Path.Combine(report.WorkingDirectory, "仅用于回收站自检.tmp");
            await File.WriteAllTextAsync(probe, "由本次自检新建的模拟文件，可送入回收站。", token);
            token.ThrowIfCancellationRequested();
            var recycled = recycleBin.TryRecycle(probe, out var reason);
            Add("系统回收站接入", recycled && !File.Exists(probe) ? "通过" : "提示", recycled && !File.Exists(probe) ? "提供器确认回收本次模拟文件。" : "回收未确认；不会永久删除。" + reason + "；检查位置：" + probe);
            var preview = PreviewScanner.Scan(preset, new() { SourceFolder = source }, token).Rows;
            PreviewExport.WriteNew(Path.Combine(report.WorkingDirectory, "模拟预览.txt"), preview);
            Add("预览导出", "通过", "以新文件生成中文清单，未覆盖任何现有文件。");
        }
        catch (OperationCanceledException) { Add("自检取消", "已取消", "自检已停止。批次日志和模拟文件保留在上面的自检目录，不影响真实数据。"); }
        catch (Exception ex) { Add("自检停止", "失败", BatchEngine.ChineseError(ex)); }
        await File.WriteAllTextAsync(report.ReportPath, report.ToText(), new UTF8Encoding(true), CancellationToken.None);
        return report;
    }
}
