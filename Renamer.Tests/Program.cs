using System.Text.Json;
using Renamer.Core;

record TestResult(string Name, string Status, string Details);
sealed class SkipTest(string reason) : Exception(reason);
sealed class InlineProgress(Action<OperationProgress> action) : IProgress<OperationProgress> { public void Report(OperationProgress value) => action(value); }
static partial class Program
{
    static readonly List<TestResult> results = new();
    static readonly List<string> roots = new();
    static string root = "";
    static int index;
    static readonly CancellationToken None = CancellationToken.None;
    static string Example(string u = "4.000", string sweep = "251") => $"dimer_2_j_1.000_U_{u}_L_200_ratio_0.150_filling_1010_nsweep_{sweep}_mps.h5";
    static RulePreset Rules() => new();
    static void Assert(bool ok, string message = "断言失败") { if (!ok) throw new Exception(message); }
    static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("应拒绝此操作：" + typeof(T).Name); }
    static void ThrowsSync<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("应拒绝此操作：" + typeof(T).Name); }
    static async Task Run(string name, Func<Task> action)
    {
        try { await action(); results.Add(new(name, "PASS", "实际执行通过")); Console.WriteLine("PASS  " + name); }
        catch (SkipTest ex) { results.Add(new(name, "SKIP", ex.Message)); Console.WriteLine("SKIP  " + name + "：" + ex.Message); }
        catch (Exception ex) { results.Add(new(name, "FAIL", ex.ToString())); Console.WriteLine("FAIL  " + name + "：" + ex.Message); }
    }
    static (string Source, string Target, BatchEngine Engine) Case()
    {
        var dir = Path.Combine(root, (++index).ToString("D3"));
        var source = Path.Combine(dir, "源目录"); var target = Path.Combine(dir, "目标目录");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        return (source, target, new BatchEngine(Path.Combine(dir, "logs")));
    }
    static string Write(string dir, string name, int size = 8193, int seed = 1)
    {
        Directory.CreateDirectory(dir); var path = Path.Combine(dir, name); var bytes = new byte[size]; new Random(seed).NextBytes(bytes); File.WriteAllBytes(path, bytes); return path;
    }
    static OperationSettings Settings(string source, string target = "", OperationKind kind = OperationKind.Rename, bool overwrite = false) => new() { SourceFolder = source, TargetFolder = target, Kind = kind, AllowOverwrite = overwrite };
    static List<PreviewRow> Scan(string source, RulePreset? p = null, OperationSettings? s = null) => PreviewScanner.Scan(p ?? Rules(), s ?? Settings(source), None).Rows;
    static Task<BatchJournal> Execute(BatchEngine e, List<PreviewRow> rows, OperationSettings s, CancellationToken token = default, IProgress<OperationProgress>? progress = null) => e.ExecuteAsync(rows.Where(r => r.Selected).ToArray(), s, BatchEngine.ValidateSelected(rows.Where(r => r.Selected).ToArray(), s), token, progress);
    static async Task<string> Hash(string path) => await BatchEngine.HashFile(path, None);
    static bool NoTemps(string directory) => !Directory.GetFiles(directory, ".__bra_*", SearchOption.AllDirectories).Any();

    static async Task<int> Main(string[] args)
    {
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "test-results.json");
        root = Path.Combine(Path.GetTempPath(), "BatchRenameAssistantTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); roots.Add(root);
        Console.WriteLine("仅测试新建模拟文件：" + root);
        await Run("U 范围包含 3.5 / 3.500 / 4.5 / 4.500，排除范围外与附件示例", () =>
        {
            var e = new RuleEngine(Rules()); foreach (var u in new[] { "3.5", "3.500", "4.5", "4.500", "4.000" }) Assert(e.Matches(Example(u), out _), u);
            foreach (var u in new[] { "1.000", "3.499999", "4.500001" }) Assert(!e.Matches(Example(u), out _), u); return Task.CompletedTask;
        });
        await Run("3.5 与 3.500 的字段等于 / 不等于按数值比较", () =>
        {
            var p = Rules(); p.Filters = new() { new() { Kind = FilterKind.FieldEqual, Field = "U", Value = "3.5" } }; Assert(new RuleEngine(p).Matches(Example("3.500"), out _));
            p.Filters[0].Kind = FilterKind.FieldNotEqual; Assert(!new RuleEngine(p).Matches(Example("3.500"), out _)); return Task.CompletedTask;
        });
        await Run("完整识别 dimer、j、U、L、ratio、filling、nsweep", () =>
        {
            var p = Rules(); p.Filters.Clear();
            foreach (var pair in new Dictionary<string, string> { ["dimer"] = "2", ["j"] = "1", ["U"] = "4", ["L"] = "200", ["ratio"] = "0.15", ["filling"] = "1010", ["nsweep"] = "251" }) p.Filters.Add(new() { Kind = FilterKind.FieldEqual, Field = pair.Key, Value = pair.Value });
            Assert(new RuleEngine(p).Matches(Example(), out _)); return Task.CompletedTask;
        });
        await Run("多条件并且：U 范围、L 数值、包含与排除", () =>
        {
            var p = Rules(); p.Filters.Add(new() { Kind = FilterKind.FieldEqual, Field = "L", Value = "200" }); p.Filters.Add(new() { Kind = FilterKind.Contains, Value = "dimer_2" }); p.Filters.Add(new() { Kind = FilterKind.NotContains, Value = "backup" });
            var e = new RuleEngine(p); Assert(e.Matches(Example(), out _)); Assert(!e.Matches(Example().Replace("L_200", "L_100"), out _)); Assert(!e.Matches("backup_" + Example(), out _)); return Task.CompletedTask;
        });
        await Run("字段_值、字段-值、字段=值筛选和替换", () =>
        {
            foreach (var name in new[] { "U_4.000_nsweep_251.h5", "U-4.000-nsweep-251.h5", "U=4.000_nsweep=251.h5" })
            { var e = new RuleEngine(Rules()); Assert(e.Matches(name, out _), name); var renamed = e.Rename(name, out var why); Assert(why == "" && renamed == name.Replace("251", "100"), name); }
            return Task.CompletedTask;
        });
        await Run("数值大小比较全部运算符与负数科学记数法", () =>
        {
            foreach (var kind in new[] { FilterKind.Greater, FilterKind.GreaterEqual, FilterKind.Less, FilterKind.LessEqual })
            {
                var p = Rules(); p.Filters = new() { new() { Kind = kind, Field = "U", Value = kind is FilterKind.Greater or FilterKind.GreaterEqual ? "3" : "5" } }; Assert(new RuleEngine(p).Matches(Example(), out _));
            }
            var n = Rules(); n.Filters = new() { new() { Kind = FilterKind.FieldEqual, Field = "U", Value = "-3.5" } }; Assert(new RuleEngine(n).Matches("U--3.5e0_nsweep-251.h5", out _)); return Task.CompletedTask;
        });
        await Run("字段缺失、重复、非数字与字段边界不会误选", () =>
        {
            var e = new RuleEngine(Rules()); foreach (var name in new[] { "nsweep_251.h5", "BU_4.0_nsweep_251.h5", "U_4_U_4_nsweep_251.h5", "U_abc_nsweep_251.h5" }) Assert(!e.Matches(name, out _), name);
            var p = Rules(); p.Filters = new() { new() { Kind = FilterKind.FieldNotEqual, Field = "U", Value = "3.5" } }; Assert(!new RuleEngine(p).Matches("missing.h5", out _)); return Task.CompletedTask;
        });
        await Run("包含、排除、通配符、大小写选项与 *.* 无扩展名", () =>
        {
            var p = Rules(); p.Wildcard = "*.*"; p.Filters = new() { new() { Kind = FilterKind.Contains, Value = "中文" }, new() { Kind = FilterKind.NotContains, Value = "backup" }, new() { Kind = FilterKind.Wildcard, Value = "*U_?.???*.h5" } };
            var e = new RuleEngine(p); Assert(e.MatchesFileType("无扩展名")); Assert(e.Matches("中文_u_4.000_nsweep_251.H5", out _)); Assert(!e.Matches("中文_backup_U_4.000.h5", out _)); p.CaseSensitive = true; Assert(!new RuleEngine(p).Matches("中文_u_4.000.h5", out _)); return Task.CompletedTask;
        });
        await Run("正则筛选与分组替换，扩展名保持原样", () =>
        {
            var p = Rules(); p.RegexEnabled = true; p.RegexPattern = @"^dimer_2_.*_nsweep_251_.*\.h5$"; p.Rename = new() { Kind = RenameKind.RegexReplace, Find = @"(nsweep_)\d+", Replacement = "${1}100" };
            var e = new RuleEngine(p); Assert(e.Matches(Example(), out _)); Assert(!e.Matches(Example(sweep: "250"), out _)); Assert(e.Rename(Example(), out _) == Example(sweep: "100"));
            p.Rename = new() { Kind = RenameKind.TextReplace, Find = ".h5", Replacement = ".txt" }; Assert(new RuleEngine(p).Rename(Example(), out _) == Example()); return Task.CompletedTask;
        });
        await Run("正则错误与真实执行超时会禁止继续", () =>
        {
            var p = Rules(); p.RegexEnabled = true; p.RegexPattern = "("; ThrowsSync<UserError>(() => new RuleEngine(p));
            p.Filters.Clear(); p.RegexPattern = "^(a+)+$"; var e = new RuleEngine(p); ThrowsSync<UserError>(() => e.Matches(new string('a', 10000) + "!", out _));
            p.RegexEnabled = false; p.Rename = new() { Kind = RenameKind.RegexReplace, Find = "^(a+)+$", Replacement = "x" }; e = new RuleEngine(p); ThrowsSync<UserError>(() => e.Rename(new string('a', 10000) + "!.h5", out _)); return Task.CompletedTask;
        });
        await Run("扫描预览不写文件；子文件夹开关及中文文件名", async () =>
        {
            var c = Case(); var f = Write(c.Source, "中文_" + Example()); var sub = Path.Combine(c.Source, "中文子目录"); var g = Write(sub, Example("3.500"));
            var h1 = await Hash(f); var h2 = await Hash(g); var before = Directory.GetFiles(c.Source, "*", SearchOption.AllDirectories).Order().ToArray();
            Assert(Scan(c.Source).Count == 1); var p = Rules(); p.Recursive = true; Assert(Scan(c.Source, p).Count == 2);
            Assert(before.SequenceEqual(Directory.GetFiles(c.Source, "*", SearchOption.AllDirectories).Order())); Assert(await Hash(f) == h1 && await Hash(g) == h2); Assert(NoTemps(c.Source));
        });
        await Run("原地改名示例正确、SHA-256 一致、日志及撤销", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example()); var original = await Hash(f); var s = Settings(c.Source); var rows = Scan(c.Source); Assert(rows[0].NewName == Example(sweep: "100"));
            var b = await Execute(c.Engine, rows, s); var target = rows[0].TargetPath; Assert(!File.Exists(f) && await Hash(target) == original); Assert(File.Exists(c.Engine.JournalPath(b)) && c.Engine.LastUndoable()?.Id == b.Id);
            await c.Engine.UndoAsync(b, None); Assert(File.Exists(f) && !File.Exists(target) && await Hash(f) == original && NoTemps(c.Source));
        });
        await Run("复制文件保留源文件、保留相对子目录、哈希一致及撤销", async () =>
        {
            var c = Case(); var f = Write(Path.Combine(c.Source, "实验一"), "中文_" + Example()); var original = await Hash(f); var p = Rules(); p.Recursive = true; var s = Settings(c.Source, c.Target, OperationKind.Copy); var rows = Scan(c.Source, p, s);
            var b = await Execute(c.Engine, rows, s); Assert(rows[0].TargetPath.Contains("实验一")); Assert(File.Exists(f) && await Hash(f) == original && await Hash(rows[0].TargetPath) == original);
            await c.Engine.UndoAsync(b, None); Assert(File.Exists(f) && !File.Exists(rows[0].TargetPath) && await Hash(f) == original);
        });
        await Run("移动原文件移除源、保留子目录、哈希一致及撤销", async () =>
        {
            var c = Case(); var f = Write(Path.Combine(c.Source, "实验二"), Example()); var h = await Hash(f); var p = Rules(); p.Recursive = true; var s = Settings(c.Source, c.Target, OperationKind.Move); var rows = Scan(c.Source, p, s);
            var b = await Execute(c.Engine, rows, s); Assert(!File.Exists(f) && await Hash(rows[0].TargetPath) == h); await c.Engine.UndoAsync(b, None); Assert(await Hash(f) == h && !File.Exists(rows[0].TargetPath));
        });
        await Run("批内重名无论覆盖开关都禁止执行", async () =>
        {
            var c = Case(); Write(c.Source, Example(sweep: "251")); Write(c.Source, Example(sweep: "252")); var p = Rules(); var s = Settings(c.Source, c.Target, OperationKind.Copy, true); var rows = Scan(c.Source, p, s); Assert(rows.All(r => r.Status == "批内重名" && !r.Selected));
            foreach (var r in rows) { r.Selected = true; r.Status = "正常"; } await Throws<UserError>(() => Execute(c.Engine, rows, s)); Assert(!Directory.GetFiles(c.Target).Any());
        });
        await Run("已有目标默认阻止；主动覆盖先备份并撤销恢复旧目标", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example(), seed: 1); var old = Write(c.Target, Example(sweep: "100"), seed: 2); var original = await Hash(f); var oldHash = await Hash(old); var s = Settings(c.Source, c.Target, OperationKind.Copy); var rows = Scan(c.Source, null, s); Assert(rows[0].Status == "已有目标" && !rows[0].Selected);
            s.AllowOverwrite = true; rows = Scan(c.Source, null, s); Assert(rows[0].Status == "已有目标（将备份）"); var b = await Execute(c.Engine, rows, s); var backup = b.Records[0].Backup; Assert(await Hash(backup) == oldHash && await Hash(old) == original && await Hash(f) == original);
            await c.Engine.UndoAsync(b, None); Assert(await Hash(old) == oldHash && await Hash(f) == original && Directory.Exists(Path.GetDirectoryName(backup)));
        });
        await Run("覆盖未经清单确认或确认后目标变化时拒绝", async () =>
        {
            var c = Case(); Write(c.Source, Example()); var s = Settings(c.Source, c.Target, OperationKind.Copy, true); var rows = Scan(c.Source, null, s); Write(c.Target, Example(sweep: "100"), seed: 2);
            await Throws<UserError>(() => c.Engine.ExecuteAsync(rows, s, Array.Empty<string>(), None)); Assert(File.Exists(rows[0].SourcePath));
        });
        await Run("原地循环改名通过临时名称分阶段完成并可撤销", async () =>
        {
            var c = Case(); var a = Write(c.Source, "a_b.h5", seed: 1); var bfile = Write(c.Source, "b_a.h5", seed: 2); var ah = await Hash(a); var bh = await Hash(bfile); var p = Rules(); p.Filters.Clear(); p.Rename = new() { Kind = RenameKind.RegexReplace, Find = @"^([^_]+)_([^_]+)$", Replacement = "$2_$1" };
            var s = Settings(c.Source); var rows = Scan(c.Source, p, s); Assert(rows.All(r => r.Executable)); var b = await Execute(c.Engine, rows, s); Assert(await Hash(a) == bh && await Hash(bfile) == ah); await c.Engine.UndoAsync(b, None); Assert(await Hash(a) == ah && await Hash(bfile) == bh && NoTemps(c.Source));
        });
        await Run("仅改变大小写的原地改名和撤销", async () =>
        {
            var c = Case(); var f = Write(c.Source, "Abc.h5"); var p = Rules(); p.Filters.Clear(); p.Rename = new() { Kind = RenameKind.TextReplace, Find = "Abc", Replacement = "abc" }; var s = Settings(c.Source); var rows = Scan(c.Source, p, s); Assert(rows[0].Executable); var b = await Execute(c.Engine, rows, s); Assert(Directory.GetFiles(c.Source).Select(Path.GetFileName).Contains("abc.h5")); await c.Engine.UndoAsync(b, None); Assert(Directory.GetFiles(c.Source).Select(Path.GetFileName).Contains("Abc.h5")); Assert(File.Exists(f));
        });
        await Run("Windows 非法字符、保留名称、尾点与过长路径", () =>
        {
            foreach (var name in new[] { "a:b.h5", "CON.h5", "COM1.h5", "LPT².txt", "NUL", "a.", "a ", "a/b.h5", "a\\b.h5" }) Assert(PathSafety.NameProblem(name).Length > 0, name);
            ThrowsSync<UserError>(() => PathSafety.CheckPath(Path.Combine(root, new string('x', 270) + ".h5")));
            var c = Case(); Write(c.Source, Example()); var p = Rules(); p.Rename.Value = "x/y"; Assert(Scan(c.Source, p)[0].Status == "非法名称", "非法路径分隔符"); p.Rename = new() { Kind = RenameKind.RegexReplace, Find = "^.*$", Replacement = "CON" }; Assert(Scan(c.Source, p)[0].Status == "非法名称", "保留名称预览"); return Task.CompletedTask;
        });
        await Run("无变化、无字段改名与复制到源文件不能执行", () =>
        {
            var c = Case(); Write(c.Source, Example(sweep: "100")); var rows = Scan(c.Source); Assert(rows[0].Status == "无变化"); var s = Settings(c.Source, c.Source, OperationKind.Copy); Assert(Scan(c.Source, null, s)[0].Status == "目标是源文件"); var p = Rules(); p.Rename.Field = "missing"; Assert(Scan(c.Source, p)[0].Status == "无法改名"); return Task.CompletedTask;
        });
        await Run("锁定文件导致执行前停止，源文件保持完整", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example()); var h = await Hash(f); var s = Settings(c.Source); var rows = Scan(c.Source);
            using (var locked = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) await Throws<UserError>(() => Execute(c.Engine, rows, s)); Assert(await Hash(f) == h && !File.Exists(rows[0].TargetPath));
        });
        await Run("只读源文件在移动或改名前阻止", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example()); var s = Settings(c.Source); var rows = Scan(c.Source); File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly);
            try { await Throws<UserError>(() => Execute(c.Engine, rows, s)); Assert(File.Exists(f)); } finally { File.SetAttributes(f, FileAttributes.Normal); }
        });
        await Run("实际目录权限失败时不提交任何目标", async () =>
        {
            if (OperatingSystem.IsWindows()) throw new SkipTest("需真实 Windows ACL；当前兼容层或自动测试不等于原生 ACL 验证");
            var c = Case(); var f = Write(c.Source, Example()); var h = await Hash(f); var s = Settings(c.Source, c.Target, OperationKind.Copy); var rows = Scan(c.Source, null, s); var mode = File.GetUnixFileMode(c.Target); File.SetUnixFileMode(c.Target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                try { PathSafety.ProbeDirectory(c.Target, false); } catch (UserError) { await Throws<UserError>(() => Execute(c.Engine, rows, s)); Assert(await Hash(f) == h && !File.Exists(rows[0].TargetPath)); return; }
                throw new SkipTest("当前执行账户可越过临时目录权限位，无法制造真实权限拒绝；Windows ACL 未验证");
            }
            finally { File.SetUnixFileMode(c.Target, mode); }
        });
        await Run("复制中途取消：无残缺最终目标，源哈希一致", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example(), 8 * 1024 * 1024 + 31); var h = await Hash(f); var s = Settings(c.Source, c.Target, OperationKind.Copy); var rows = Scan(c.Source, null, s); using var ct = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => Execute(c.Engine, rows, s, ct.Token, new InlineProgress(p => { if (p.Bytes > 0) ct.Cancel(); }))); Assert(await Hash(f) == h && !File.Exists(rows[0].TargetPath) && NoTemps(c.Target)); Assert(c.Engine.History()[0].Status == "Cancelled");
        });
        await Run("移动中途取消：源暂存回滚，源哈希一致", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example(), 8 * 1024 * 1024 + 11); var h = await Hash(f); var s = Settings(c.Source, c.Target, OperationKind.Move); var rows = Scan(c.Source, null, s); using var ct = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => Execute(c.Engine, rows, s, ct.Token, new InlineProgress(p => { if (p.Bytes > 0) ct.Cancel(); }))); Assert(await Hash(f) == h && !File.Exists(rows[0].TargetPath) && NoTemps(c.Source) && NoTemps(c.Target));
        });
        await Run("首个文件已完成后取消：整批回滚与覆盖旧目标恢复", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example("3.500")); var g = Write(c.Source, Example("4.500"), seed: 2); var old = Write(c.Target, Example("3.500", "100"), seed: 3); var fh = await Hash(f); var gh = await Hash(g); var oh = await Hash(old); var s = Settings(c.Source, c.Target, OperationKind.Move, true); var rows = Scan(c.Source, null, s); using var ct = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => Execute(c.Engine, rows, s, ct.Token, new InlineProgress(p => { if (p.Finished == 1 && p.Bytes == 0) ct.Cancel(); }))); Assert(await Hash(f) == fh && await Hash(g) == gh && await Hash(old) == oh && !File.Exists(rows[1].TargetPath) && NoTemps(c.Source) && NoTemps(c.Target));
        });
        await Run("撤销前原路径冲突时整批停止、不覆盖新文件", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example()); var s = Settings(c.Source, c.Target, OperationKind.Move); var rows = Scan(c.Source, null, s); var b = await Execute(c.Engine, rows, s); Write(c.Source, Example(), seed: 9); var occupied = await Hash(f); var target = await Hash(rows[0].TargetPath);
            await Throws<UserError>(() => c.Engine.UndoAsync(b, None)); Assert(await Hash(f) == occupied && await Hash(rows[0].TargetPath) == target && NoTemps(c.Target));
        });
        await Run("撤销前目标内容已改动时停止", async () =>
        {
            var c = Case(); Write(c.Source, Example()); var s = Settings(c.Source, c.Target, OperationKind.Copy); var rows = Scan(c.Source, null, s); var b = await Execute(c.Engine, rows, s); File.AppendAllText(rows[0].TargetPath, "externally changed"); var changed = await Hash(rows[0].TargetPath);
            await Throws<UserError>(() => c.Engine.UndoAsync(b, None)); Assert(await Hash(rows[0].TargetPath) == changed && NoTemps(c.Target));
        });
        await Run("撤销中取消后原批次完整恢复", async () =>
        {
            var c = Case(); Write(c.Source, Example("3.5")); Write(c.Source, Example("4.5"), seed: 2); var s = Settings(c.Source, c.Target, OperationKind.Copy); var rows = Scan(c.Source, null, s); var b = await Execute(c.Engine, rows, s); using var ct = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => c.Engine.UndoAsync(b, ct.Token, new InlineProgress(p => { if (p.Finished == 1) ct.Cancel(); }))); Assert(rows.All(r => File.Exists(r.TargetPath)) && NoTemps(c.Target)); Assert(c.Engine.LastUndoable()?.Id == b.Id); await c.Engine.UndoAsync(b, None); Assert(rows.All(r => !File.Exists(r.TargetPath)));
        });
        await Run("跨文件系统移动：完整复制核对后安全移出源及撤销", async () =>
        {
            if (OperatingSystem.IsWindows() || !Directory.Exists("/dev/shm")) throw new SkipTest("当前环境没有第二个可用临时文件系统；真实 Windows 跨盘未验证");
            var c = Case(); var other = "/dev/shm/BatchRenameAssistantTests_" + Guid.NewGuid().ToString("N"); roots.Add(other); Directory.CreateDirectory(other); var f = Write(c.Source, Example(), 2 * 1024 * 1024 + 17); var h = await Hash(f); var s = Settings(c.Source, other, OperationKind.Move); var rows = Scan(c.Source, null, s); var b = await Execute(c.Engine, rows, s); Assert(!File.Exists(f) && await Hash(rows[0].TargetPath) == h); await c.Engine.UndoAsync(b, None); Assert(await Hash(f) == h && !File.Exists(rows[0].TargetPath));
        });
        await Run("预览后源长度变化时执行拒绝", async () =>
        {
            var c = Case(); var f = Write(c.Source, Example()); var rows = Scan(c.Source); File.AppendAllText(f, "modified"); await Throws<UserError>(() => Execute(c.Engine, rows, Settings(c.Source))); Assert(!File.Exists(rows[0].TargetPath));
        });
        await Run("备份目录与源目录内的独立目标目录不参与扫描", () =>
        {
            var c = Case(); Write(c.Source, Example()); var destination = Path.Combine(c.Source, "输出"); Write(destination, Example("3.5")); Write(Path.Combine(c.Source, ".批量改名助手_备份_test"), Example("4.5")); var p = Rules(); p.Recursive = true; var s = Settings(c.Source, destination, OperationKind.Copy); Assert(Scan(c.Source, p, s).Count == 1); return Task.CompletedTask;
        });
        await Run("预设 JSON 保存规则且没有目录或覆盖字段", () =>
        {
            var c = Case(); var p = Rules(); p.Name = "中文预设"; p.Recursive = true; p.CaseSensitive = true; var path = Path.Combine(c.Source, "preset.json"); JsonStore.Save(path, p); var raw = File.ReadAllText(path); Assert(!raw.Contains("SourceFolder") && !raw.Contains("TargetFolder") && !raw.Contains("AllowOverwrite")); var q = JsonStore.Read<RulePreset>(path); Assert(q.Name == p.Name && q.Recursive && q.CaseSensitive && q.Rename.Value == "100"); return Task.CompletedTask;
        });
        await Run("模拟中断：源已暂存但未提交时可恢复", async () =>
        {
            var c = Case(); Write(c.Source, Example()); var s = Settings(c.Source); var rows = Scan(c.Source); var b = await Execute(c.Engine, rows, s); var r = b.Records[0]; File.Move(r.Target, r.Stage); b.Status = "Running"; r.Completed = false; r.CommitPending = false; r.Staged = false; JsonStore.Save(c.Engine.JournalPath(b), b);
            await c.Engine.RecoverAsync(c.Engine.Pending()!); Assert(File.Exists(r.Source) && !File.Exists(r.Target) && await Hash(r.Source) == r.Hash && NoTemps(c.Source));
        });
        await Run("模拟回滚中断：目标已移回暂存、日志仍标完成", async () =>
        {
            var c = Case(); Write(c.Source, Example()); var s = Settings(c.Source); var b = await Execute(c.Engine, Scan(c.Source), s); var r = b.Records[0]; File.Move(r.Target, r.Stage); b.Status = "Running"; JsonStore.Save(c.Engine.JournalPath(b), b); await c.Engine.RecoverAsync(c.Engine.Pending()!); Assert(await Hash(r.Source) == r.Hash && c.Engine.Pending() == null);
        });
        await Run("模拟复制提交中断：目标已提交、完成标记尚未写入", async () =>
        {
            var c = Case(); Write(c.Source, Example()); var s = Settings(c.Source, c.Target, OperationKind.Copy); var b = await Execute(c.Engine, Scan(c.Source, null, s), s); var r = b.Records[0]; b.Status = "Running"; r.Completed = false; r.CommitPending = true; JsonStore.Save(c.Engine.JournalPath(b), b); await c.Engine.RecoverAsync(c.Engine.Pending()!); Assert(File.Exists(r.Source) && !File.Exists(r.Target) && c.Engine.Pending() == null);
        });
        await Run("模拟撤销中断：目标已暂存、撤销日志尚未更新", async () =>
        {
            var c = Case(); Write(c.Source, Example()); var s = Settings(c.Source, c.Target, OperationKind.Copy); var b = await Execute(c.Engine, Scan(c.Source, null, s), s); var r = b.Records[0]; File.Move(r.Target, r.UndoTemp); b.Status = "Undoing"; JsonStore.Save(c.Engine.JournalPath(b), b); await c.Engine.RecoverAsync(c.Engine.Pending()!); Assert(await Hash(r.Target) == r.Hash && !File.Exists(r.UndoTemp) && c.Engine.LastUndoable()?.Id == b.Id);
        });
        await Run("复制循环目标指向本批源文件时预览和执行均阻止", async () =>
        {
            var c = Case(); Write(c.Source, "a_b.h5"); Write(c.Source, "b_a.h5", seed: 2); var p = Rules(); p.Filters.Clear(); p.Rename = new() { Kind = RenameKind.RegexReplace, Find = @"^([^_]+)_([^_]+)$", Replacement = "$2_$1" }; var s = Settings(c.Source, c.Source, OperationKind.Copy, true); var rows = Scan(c.Source, p, s); Assert(rows.All(r => r.Status == "目标是源文件" && !r.Selected));
            foreach (var r in rows) { r.Status = "正常"; r.Selected = true; } await Throws<UserError>(() => Execute(c.Engine, rows, s));
        });
        await Run("仅勾选合法文件才执行，未勾选源保持不变", async () =>
        {
            var c = Case(); var a = Write(c.Source, Example("3.5")); var bfile = Write(c.Source, Example("4.5"), seed: 2); var ah = await Hash(a); var bh = await Hash(bfile); var rows = Scan(c.Source); rows[1].Selected = false; var b = await Execute(c.Engine, rows, Settings(c.Source)); Assert(b.Records.Count == 1 && !File.Exists(a) && await Hash(bfile) == bh); await c.Engine.UndoAsync(b, None); Assert(await Hash(a) == ah && await Hash(bfile) == bh);
        });
        await Run("原地覆盖目标：旧目标先备份，撤销恢复两份原文件", async () =>
        {
            var c = Case(); var original = Write(c.Source, Example()); var existing = Write(c.Source, Example(sweep: "100"), seed: 2); var a = await Hash(original); var bHash = await Hash(existing); var s = Settings(c.Source, overwrite: true); var rows = Scan(c.Source, null, s); Assert(rows.Count(r => r.Selected) == 1); var b = await Execute(c.Engine, rows, s); Assert(await Hash(existing) == a && await Hash(b.Records[0].Backup) == bHash); await c.Engine.UndoAsync(b, None); Assert(await Hash(original) == a && await Hash(existing) == bHash);
        });
        await Run("控制字符非法新名只标记该行，不会中断整个扫描", () =>
        {
            var c = Case(); Write(c.Source, Example()); var p = Rules(); p.Rename.Value = "bad\0name"; Assert(Scan(c.Source, p)[0].Status == "非法名称"); return Task.CompletedTask;
        });
        await Run("预设格式缺失时显示错误并拒绝加载", () =>
        {
            var p = Rules(); p.Filters = null!; ThrowsSync<UserError>(() => new RuleEngine(p)); return Task.CompletedTask;
        });
        await Run("目录边界判断支持根目录且排除同名前缀目录", () =>
        {
            var c = Case(); var f = Write(c.Source, Example()); Assert(PathSafety.IsWithin(f, c.Source)); Assert(PathSafety.IsWithin(f, Path.GetPathRoot(f)!)); Assert(!PathSafety.IsWithin(c.Source + "_other" + Path.DirectorySeparatorChar + "a.h5", c.Source)); return Task.CompletedTask;
        });
        await RunUpgradeTests();
        await RunFinalAuditTests();
        var report = new { Utc = DateTime.UtcNow, OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, Root = root, Pass = results.Count(r => r.Status == "PASS"), Fail = results.Count(r => r.Status == "FAIL"), Skip = results.Count(r => r.Status == "SKIP"), Results = results };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, JsonSerializer.Serialize(report, JsonStore.Options));
        Console.WriteLine($"RESULT: PASS={report.Pass}, FAIL={report.Fail}, SKIP={report.Skip}");
        foreach (var p in roots) { try { Directory.Delete(p, true); } catch { /* Only disposable test files are retained on cleanup failure. */ } }
        return report.Fail == 0 ? 0 : 1;
    }
}
