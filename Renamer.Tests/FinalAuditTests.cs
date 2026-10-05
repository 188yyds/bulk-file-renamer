using Renamer.Core;

static partial class Program
{
    static async Task RunFinalAuditTests()
    {
        await Run("执行拒绝触碰软件日志目录，避免改坏撤销记录", async () =>
        {
            var c = Case(); var f = Write(c.Engine.LogDirectory, "软件模拟记录.txt"); var h = await Hash(f); var p = General(); var s = Settings(c.Engine.LogDirectory, c.Target, OperationKind.Copy); var rows = Scan(c.Engine.LogDirectory, p, s);
            await Throws<UserError>(() => Execute(c.Engine, rows, s)); Assert(await Hash(f) == h && !File.Exists(rows[0].TargetPath));
        });
        await Run("执行拒绝输出到软件日志目录", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example()); var s = Settings(c.Source, c.Engine.LogDirectory, OperationKind.Copy); var rows = Scan(c.Source, null, s); var h = await Hash(f);
            await Throws<UserError>(() => Execute(c.Engine, rows, s)); Assert(await Hash(f) == h && !File.Exists(rows[0].TargetPath));
        });
        await Run("字段缺失条件不误选字段名已出现但值为空的文件", () =>
        {
            var p = General(); p.Filters.Add(new() { Kind = FilterKind.FieldMissing, Field = "U", Value = "" }); var e = new RuleEngine(p);
            Assert(e.Matches("普通名字.txt", out _)); Assert(!e.Matches("U=.txt", out _)); Assert(!e.Matches("U_4_U_.txt", out _)); return Task.CompletedTask;
        });
        await Run("完整字段与不完整重复字段混合时拒绝改名和数值筛选", () =>
        {
            var p = Rules(); var e = new RuleEngine(p); Assert(!e.Matches("U_4_U__nsweep_251.h5", out _));
            Assert(e.Rename("U_4_nsweep_251_nsweep_.h5", out var reason) == "U_4_nsweep_251_nsweep_.h5" && reason.Contains("多次")); return Task.CompletedTask;
        });
        await Run("非模板改名忽略未使用的超大序号，模板溢出则拒绝扫描", () =>
        {
            var c = Case(); Write(c.Source, "a.txt"); Write(c.Source, "b.txt"); var p = General(); p.Rename.SequenceStart = long.MaxValue; p.Rename.SequenceStep = long.MaxValue;
            Assert(Scan(c.Source, p).All(r => r.Executable)); p.Rename.Kind = RenameKind.Template; p.Rename.Value = "{index}_{name}"; ThrowsSync<OverflowException>(() => Scan(c.Source, p)); return Task.CompletedTask;
        });
        await Run("导出中文预览清单包含完整路径、状态和哈希且不改源文件", async () =>
        {
            var c = Case(); var f = Write(c.Source, "中文_" + Example()); var h = await Hash(f); var rows = Scan(c.Source); var path = Path.Combine(c.Target, "预览.txt"); PreviewExport.WriteNew(path, rows);
            var text = File.ReadAllText(path); Assert(text.Contains(f) && text.Contains(rows[0].NewName) && text.Contains(h) && text.Contains("正常") && await Hash(f) == h);
        });
        await Run("导出清单拒绝覆盖已有文件，即使该文件是源数据", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example()); var h = await Hash(f); ThrowsSync<IOException>(() => PreviewExport.WriteNew(f, Scan(c.Source))); Assert(await Hash(f) == h);
        });
        await Run("本机自检实际运行改名复制移动撤销，仅写随机模拟目录", async () =>
        {
            var report = await EnvironmentSelfCheck.RunAsync(new SimulatedRecycleBin(), None); roots.Add(report.WorkingDirectory);
            Assert(!report.Items.Any(i => i.Status == "失败") && report.Items.Count(i => i.Status == "通过") >= 7, report.ToText()); Assert(File.Exists(report.ReportPath));
            Assert(report.WorkingDirectory.StartsWith(Path.Combine(Path.GetTempPath(), "BRA_Check_"), StringComparison.Ordinal));
        });
        await Run("本机自检遇到回收站失败会报告提示并保留模拟文件", async () =>
        {
            var report = await EnvironmentSelfCheck.RunAsync(new SimulatedRecycleBin { Fail = true }, None); roots.Add(report.WorkingDirectory);
            Assert(!report.Items.Any(i => i.Status == "失败"), report.ToText()); Assert(report.Items.Any(i => i.Name.Contains("回收站") && i.Status == "提示")); Assert(Directory.GetFiles(report.WorkingDirectory, "*", SearchOption.AllDirectories).Length > 1);
        });
        await Run("自检中取消仍保存取消报告，不操作真实源或目标目录", async () =>
        {
            using var cancel = new CancellationTokenSource(); var report = await EnvironmentSelfCheck.RunAsync(new RetainOnlyRecycleBin(), cancel.Token, new InlineProgress(p => cancel.Cancel())); roots.Add(report.WorkingDirectory);
            Assert(report.Items.Any(i => i.Status == "已取消") && File.Exists(report.ReportPath));
        });
        await Run("损坏日志的空记录、错批次号、未知状态会阻止执行", async () =>
        {
            foreach (var kind in new[] { 0, 1, 2 })
            {
                var c = Case(); Write(c.Source, Example()); var s = Settings(c.Source); var b = await Execute(c.Engine, Scan(c.Source), s); var r = b.Records[0]; var h = await Hash(r.Target); var path = c.Engine.JournalPath(b);
                if (kind == 0) b.Records = null!; else if (kind == 1) b.Id = "../其他文件"; else b.Status = "Unknown";
                JsonStore.Save(path, b); ThrowsSync<UserError>(() => c.Engine.History()); Assert(await Hash(r.Target) == h);
            }
        });
        await Run("损坏日志中的临时路径不一致会在恢复前阻止", async () =>
        {
            var c = Case(); Write(c.Source, Example()); var b = await Execute(c.Engine, Scan(c.Source), Settings(c.Source)); b.Status = "Running"; b.Records[0].Stage = Path.Combine(c.Target, "other.h5"); JsonStore.Save(c.Engine.JournalPath(b), b);
            ThrowsSync<UserError>(() => c.Engine.Pending()); Assert(File.Exists(b.Records[0].Target));
        });
        await Run("接近 260 字符路径时回收失败可退到短保留名并完成撤销", async () =>
        {
            var c = Case(); var directory = c.Target;
            while (directory.Length < 231) { var length = Math.Min(35, 232 - directory.Length - 1); if (length <= 0) break; directory = Path.Combine(directory, new string('d', length)); Directory.CreateDirectory(directory); }
            var source = Write(c.Source, "a.h5"); var h = await Hash(source); var p = General(RenameKind.Suffix, "x"); var s = Settings(c.Source, directory, OperationKind.Copy); var rows = Scan(c.Source, p, s); var batch = await Execute(c.Engine, rows, s);
            await c.Engine.UndoAsync(batch, None); var held = batch.Retirements.Single().RetainedPath; Assert(Path.GetFileName(held).StartsWith(".__bra_k_") && held.Length < 260 && await Hash(held) == h && await Hash(source) == h && !File.Exists(rows[0].TargetPath));
        });
        await Run("旧版跨文件系统移动中断恢复使用哈希校验复制及安全回收", async () =>
        {
            if (OperatingSystem.IsWindows() || !Directory.Exists("/dev/shm")) throw new SkipTest("需要独立的第二个临时文件系统；当前无法执行此 Linux 跨文件系统模拟");
            var c = Case(); var target = Path.Combine("/dev/shm", "BRA_Legacy_" + Guid.NewGuid().ToString("N")); roots.Add(target); Directory.CreateDirectory(target);
            var f = Write(c.Source, Example()); var h = await Hash(f); var s = Settings(c.Source, target, OperationKind.Move); var b = await Execute(c.Engine, Scan(c.Source, null, s), s); var r = b.Records[0]; Assert(!File.Exists(r.Stage));
            b.FormatVersion = 1; b.Status = "Running"; JsonStore.Save(c.Engine.JournalPath(b), b); await c.Engine.RecoverAsync(c.Engine.Pending()!);
            Assert(await Hash(f) == h && !File.Exists(r.Target) && c.Engine.Pending() == null); Assert(c.Engine.History()[0].Retirements.Any(t => t.Reason.Contains("跨目录恢复")));
        });
    }
}
