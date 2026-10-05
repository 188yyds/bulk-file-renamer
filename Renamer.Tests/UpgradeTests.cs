using Renamer.Core;
using Renamer.App;

sealed class SimulatedRecycleBin : IRecycleBin
{
    public List<string> Recycled { get; } = new();
    public bool Fail { get; set; }
    public bool RaiseError { get; set; }
    public bool TryRecycle(string path, out string reason)
    {
        if (RaiseError) throw new IOException("模拟系统回收站异常");
        if (Fail) { reason = "模拟没有可用回收站"; return false; }
        // A test double, NOT the Windows system recycle bin. All files belong to the test fixture.
        var folder = Path.Combine(Path.GetDirectoryName(path)!, "模拟回收站"); Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".bin");
        File.Move(path, destination); Recycled.Add(destination); reason = ""; return true;
    }
}
static partial class Program
{
    static RulePreset General(RenameKind kind = RenameKind.Prefix, string value = "新_") => new() { Filters = new(), Wildcard = "*.*", Rename = new() { Kind = kind, Value = value } };
    static async Task RunUpgradeTests()
    {
        await Run("回收站回调防线：否决永久删除标志，失败回调不报成功", () =>
        {
            var denied = new RecycleOnlySink(); Assert(denied.PreDeleteItem(0, IntPtr.Zero) < 0 && denied.RefusedPermanentDelete);
            denied.PostDeleteItem(0, IntPtr.Zero, 0, IntPtr.Zero); Assert(!denied.Completed);
            var accepted = new RecycleOnlySink(); Assert(accepted.PreDeleteItem(0x80, IntPtr.Zero) == 0);
            accepted.PostDeleteItem(0x80, IntPtr.Zero, unchecked((int)0x80004005), IntPtr.Zero); Assert(!accepted.Completed);
            accepted.PostDeleteItem(0x80, IntPtr.Zero, 0, IntPtr.Zero); Assert(accepted.Completed); return Task.CompletedTask;
        });
        await Run("原生 Windows 系统回收站集成（仅临时模拟文件）", () =>
        {
            if (!OperatingSystem.IsWindows()) throw new SkipTest("Linux 环境不能执行 Windows Shell COM 回收站，模拟提供器和回调防线通过不代表原生集成通过");
            var c = Case(); var f = Write(c.Source, "Windows回收站测试文件.tmp");
            var success = new WindowsRecycleBin().TryRecycle(f, out var reason); Assert(success && !File.Exists(f), reason); return Task.CompletedTask;
        });
        await Run("原生 Windows NTFS 附加数据流拦截（仅临时模拟文件）", () =>
        {
            if (!OperatingSystem.IsWindows()) throw new SkipTest("当前 Linux 环境没有 NTFS FindFirstStreamW，尚未验证真实 Windows 附加数据流");
            var c = Case(); var f = Write(c.Source, "数据流测试.tmp");
            File.WriteAllText(f + ":模拟附加流", "必须保留的模拟内容");
            if (!File.Exists(f + ":模拟附加流")) throw new SkipTest("临时文件系统不支持 NTFS 附加数据流");
            ThrowsSync<UserError>(() => WindowsContentSafety.EnsureSimpleStream(f)); Assert(File.ReadAllText(f + ":模拟附加流") == "必须保留的模拟内容"); return Task.CompletedTask;
        });
        await Run("精确数字：极小数不下溢、大整数不舍入、科学计数等价", () =>
        {
            foreach (var (a, b, expected) in new[] { ("1e-100", "0", 1), ("-1e-100", "0", -1), ("1234567890123456789012345678901", "1234567890123456789012345678900", 1), ("35e-1", "3.500", 0), ("-0", "0e10000", 0), ("1e100000", "9e99999", 1), ("1.0001", "1", 1) })
            { Assert(ExactNumber.TryParse(a, out var x) && ExactNumber.TryParse(b, out _)); ExactNumber.TryParse(b, out var y); Assert(Math.Sign(x.CompareTo(y)) == expected, a + " / " + b); }
            Assert(!ExactNumber.TryParse("1e100001", out _) && !ExactNumber.TryParse("NaN", out _) && !ExactNumber.TryParse(".", out _)); return Task.CompletedTask;
        });
        await Run("字段比较可明确区分文本 001 与数值 1", () =>
        {
            var p = General(); p.Filters.Add(new() { Kind = FilterKind.FieldEqual, Field = "id", Value = "1", Comparison = ValueComparison.Text }); Assert(!new RuleEngine(p).Matches("id_001.txt", out _));
            p.Filters[0].Comparison = ValueComparison.Numeric; Assert(new RuleEngine(p).Matches("id_001.txt", out _)); Assert(!new RuleEngine(p).Matches("id_text.txt", out _));
            p.Filters[0].Kind = FilterKind.FieldNotEqual; Assert(!new RuleEngine(p).Matches("id_text.txt", out _)); return Task.CompletedTask;
        });
        await Run("多个文件通配符用分号组合，条件之间仍为并且", () =>
        {
            var p = General(); p.Wildcard = "*.h5;*.txt；*.csv"; p.Filters.Add(new() { Kind = FilterKind.Wildcard, Value = "实验*;sample*" }); p.Filters.Add(new() { Kind = FilterKind.NotContains, Value = "backup" }); var e = new RuleEngine(p);
            foreach (var n in new[] { "实验.h5", "sample.TXT", "sample.csv" }) Assert(e.MatchesFileType(n) && e.Matches(n, out _)); Assert(!e.MatchesFileType("sample.exe")); Assert(!e.Matches("sample_backup.txt", out _)); return Task.CompletedTask;
        });
        await Run("新增开头、结尾、字段存在、缺失及值包含条件", () =>
        {
            var p = General(); p.Filters = new() { new() { Kind = FilterKind.StartsWith, Value = "实验" }, new() { Kind = FilterKind.EndsWith, Value = ".h5" }, new() { Kind = FilterKind.FieldExists, Field = "组别", Value = "" }, new() { Kind = FilterKind.FieldContains, Field = "组别", Value = "对照" }, new() { Kind = FilterKind.FieldNotContains, Field = "组别", Value = "备用" }, new() { Kind = FilterKind.FieldMissing, Field = "U", Value = "" } };
            var e = new RuleEngine(p); Assert(e.Matches("实验_组别=对照一.h5", out _)); Assert(!e.Matches("实验_组别=备用对照.h5", out _)); Assert(!e.Matches("实验_组别=对照一_U=4.h5", out _)); Assert(!e.Matches("实验_组别=对照一_U=4_U=4.h5", out _)); return Task.CompletedTask;
        });
        await Run("带空格括号、中文及下划线字段名识别与精确替换", () =>
        {
            var p = General(RenameKind.SetField, "8"); p.Rename.Field = "sample_id"; p.Filters.Add(new() { Kind = FilterKind.FieldEqual, Field = "U", Value = "4" });
            var e = new RuleEngine(p); const string n = "实验 (U = 4.000);sample_id = 007.TXT"; Assert(e.Matches(n, out _)); Assert(e.Rename(n, out var why) == "实验 (U = 4.000);sample_id = 8.TXT" && why == ""); return Task.CompletedTask;
        });
        await Run("自定义字段 U[值] 共享筛选与改名，保持外围字符", () =>
        {
            var p = General(RenameKind.SetField, "100"); p.Rename.Field = "nsweep"; p.FieldPattern = @"(?<![\p{L}\p{N}]){field}\[(?<value>[^\]]+)\]"; p.Filters.Add(new() { Kind = FilterKind.Range, Field = "U", Value = "3.5", Upper = "4.5" });
            var e = new RuleEngine(p); Assert(e.Matches("中文_U[4.000]_nsweep[251].h5", out _)); Assert(e.Rename("中文_U[4.000]_nsweep[251].h5", out _) == "中文_U[4.000]_nsweep[100].h5");
            Assert(!e.Matches("U[4]_U[4]_nsweep[251].h5", out _)); p.FieldPattern = "{field}["; ThrowsSync<UserError>(() => new RuleEngine(p)); p.FieldPattern = "(?<value>.*)"; ThrowsSync<UserError>(() => new RuleEngine(p)); return Task.CompletedTask;
        });
        await Run("复合扩展名、大小写扩展名、点文件与无扩展名保护", () =>
        {
            var e = new RuleEngine(General(RenameKind.Suffix, "_归档"));
            foreach (var (a,b) in new[] { ("data.TAR.GZ", "data_归档.TAR.GZ"), ("中文.NII.GZ", "中文_归档.NII.GZ"), ("README", "README_归档"), (".env", ".env_归档"), ("中文.H5", "中文_归档.H5") }) Assert(e.Rename(a, out _) == b, a);
            var p = General(RenameKind.Suffix,"x"); p.CompoundExtensions = ".custom.zip"; Assert(new RuleEngine(p).Rename("a.custom.zip", out _) == "ax.custom.zip"); return Task.CompletedTask;
        });
        await Run("前后缀、大小写转换、首尾空格与首处替换", () =>
        {
            Assert(new RuleEngine(General(RenameKind.Prefix, "前_")).Rename("Abc.H5", out _) == "前_Abc.H5");
            Assert(new RuleEngine(General(RenameKind.LowerCase)).Rename("ABC.H5", out _) == "abc.H5"); Assert(new RuleEngine(General(RenameKind.UpperCase)).Rename("abc.h5", out _) == "ABC.h5"); Assert(new RuleEngine(General(RenameKind.Trim)).Rename(" abc .H5", out _) == "abc.H5");
            var p = General(); p.Rename = new() { Kind = RenameKind.TextReplace, Find = "a", Replacement = "", FirstOnly = true }; Assert(new RuleEngine(p).Rename("AaAa.TXT", out _) == "aAa.TXT"); p.Rename.Kind = RenameKind.RegexReplace; p.Rename.Find = "[aA]"; Assert(new RuleEngine(p).Rename("AaAa.TXT", out _) == "aAa.TXT"); return Task.CompletedTask;
        });
        await Run("缺失字段只在主动启用时追加，模糊或重复字段不追加", () =>
        {
            var p = General(RenameKind.SetField,"100"); p.Rename.Field = "nsweep"; var e = new RuleEngine(p); Assert(e.Rename("实验.h5", out var why) == "实验.h5" && why.Length > 0);
            p.Rename.AppendMissingField = true; p.Rename.AppendSeparator = "="; e = new RuleEngine(p); Assert(e.Rename("实验.h5", out why) == "实验_nsweep=100.h5" && why == "");
            Assert(e.Rename("实验_nsweep=.h5", out why) == "实验_nsweep=.h5" && why.Length > 0); Assert(e.Rename("nsweep_2_nsweep_3.h5", out why) == "nsweep_2_nsweep_3.h5" && why.Length > 0); return Task.CompletedTask;
        });
        await Run("模板字段、父目录、时间、编号及大括号试算", () =>
        {
            var p = General(RenameKind.Template,"{{归档}}_{parent}_{field:U}_{index:0000}_{modified:yyyyMMdd}_{created:HHmm}_{name}");
            var result = new RuleEngine(p).Rename("U_4.000.H5", out var why, new(12, "实验一", new DateTime(2025, 3, 4), new DateTime(2024, 2, 3, 12, 30, 0)));
            Assert(result == "{归档}_实验一_4.000_0012_20250304_1230_U_4.000.H5" && why == "", result);
            p.Rename.Value = "{unknown}"; ThrowsSync<UserError>(() => new RuleEngine(p)); p.Rename.Value = "{index:D4}"; ThrowsSync<UserError>(() => new RuleEngine(p)); p.Rename.Value = "{field:missing}"; Assert(new RuleEngine(p).Rename("U_4.h5", out why) == "U_4.h5" && why.Length > 0); return Task.CompletedTask;
        });
        await Run("全局相对路径排序编号可重复，取消勾选不重排", async () =>
        {
            var c = Case(); Write(c.Source, "z.txt"); Write(Path.Combine(c.Source,"a"), "b.txt"); Write(c.Source, "A.txt"); var p = General(RenameKind.Template, "{index:000}_{name}"); p.Recursive = true; p.Rename.SequenceStart = 10; p.Rename.SequenceStep = 2;
            var s = Settings(c.Source,c.Target,OperationKind.Copy); var rows = Scan(c.Source,p,s); Assert(rows.Select(r=>r.Sequence).SequenceEqual(new long[]{10,12,14})); Assert(rows.Select(r=>r.NewName).SequenceEqual(Scan(c.Source,p,s).Select(r=>r.NewName)));
            rows[1].Selected = false; var b = await Execute(c.Engine,rows,s); Assert(b.Records.Count==2 && File.Exists(rows[2].TargetPath) && rows[2].NewName.StartsWith("014_"));
        });
        await Run("预览源内容指纹可识别相同长度及相同修改时间的篡改", async () =>
        {
            var c=Case(); var f=Write(c.Source,Example()); var rows=Scan(c.Source); var time=File.GetLastWriteTimeUtc(f); var data=File.ReadAllBytes(f); data[0]^=255; File.WriteAllBytes(f,data); File.SetLastWriteTimeUtc(f,time);
            await Throws<UserError>(()=>Execute(c.Engine,rows,Settings(c.Source))); Assert(File.Exists(f) && !File.Exists(rows[0].TargetPath));
        });
        await Run("覆盖确认前后相同长度时间的目标内容改变也会拒绝", async () =>
        {
            var c=Case(); Write(c.Source,Example()); var old=Write(c.Target,Example(sweep:"100"),seed:8); var s=Settings(c.Source,c.Target,OperationKind.Copy,true); var rows=Scan(c.Source,null,s); var time=File.GetLastWriteTimeUtc(old); var bytes=File.ReadAllBytes(old); bytes[3]^=255; File.WriteAllBytes(old,bytes); File.SetLastWriteTimeUtc(old,time); var h=await Hash(old);
            await Throws<UserError>(()=>Execute(c.Engine,rows,s)); Assert(await Hash(old)==h && !Directory.GetDirectories(c.Target).Any());
        });
        await Run("临时复制内容损坏时拒绝提交，并安全保留源文件", async () =>
        {
            var c=Case(); var f=Write(c.Source,Example(),1048593); var h=await Hash(f); var s=Settings(c.Source,c.Target,OperationKind.Copy); var rows=Scan(c.Source,null,s); bool damaged=false;
            await Throws<UserError>(()=>Execute(c.Engine,rows,s,None,new InlineProgress(p=> { if(p.Message.StartsWith("临时副本已落盘：") && !damaged) { var path=p.Message["临时副本已落盘：".Length..]; var bytes=File.ReadAllBytes(path); bytes[0]^=255; File.WriteAllBytes(path,bytes); damaged=true; } })));
            Assert(damaged && await Hash(f)==h && !File.Exists(rows[0].TargetPath)); var batch=c.Engine.History()[0]; Assert(batch.Retirements.Count==1 && File.Exists(batch.Retirements[0].RetainedPath));
        });
        await Run("最终目标被外部替换时整批核验发现，移动源完整恢复", async () =>
        {
            var c=Case(); var f=Write(c.Source,Example()); var h=await Hash(f); var s=Settings(c.Source,c.Target,OperationKind.Move); var rows=Scan(c.Source,null,s); bool replaced=false;
            await Throws<UserError>(()=>Execute(c.Engine,rows,s,None,new InlineProgress(p=> { if(p.Message.StartsWith("完成：") && !replaced) { File.Move(rows[0].TargetPath,Path.Combine(c.Target,"外部移走.bin")); Write(c.Target,rows[0].NewName,seed:9); replaced=true; } })));
            Assert(replaced && await Hash(f)==h && !File.Exists(rows[0].TargetPath)); var batch=c.Engine.History()[0]; Assert(batch.Status=="RolledBack" && batch.Retirements.Single().State=="Retained" && batch.Warnings.Any(w=>w.Contains("隔离")));
        });
        await Run("移动整批提交前所有源暂存仍在，取消后全量恢复", async () =>
        {
            var c=Case(); var a=Write(c.Source,Example("3.5")); var b=Write(c.Source,Example("4.5"),seed:2); var ah=await Hash(a); var bh=await Hash(b); var s=Settings(c.Source,c.Target,OperationKind.Move); var rows=Scan(c.Source,null,s); using var ct=new CancellationTokenSource(); bool saw=false;
            await Throws<OperationCanceledException>(()=>Execute(c.Engine,rows,s,ct.Token,new InlineProgress(p=> { if(p.Message.StartsWith("完成：") && p.Finished==1) { var pending=c.Engine.Pending()!; Assert(pending.Records.All(r=>File.Exists(r.Stage))); saw=true; ct.Cancel(); } })));
            Assert(saw && await Hash(a)==ah && await Hash(b)==bh && rows.All(r=>!File.Exists(r.TargetPath)));
        });
        await Run("模拟回收站：移动源与撤销目标均完整送交，无硬删除", async () =>
        {
            var c=Case(); var bin=new SimulatedRecycleBin(); var engine=new BatchEngine(c.Engine.LogDirectory,bin); var f=Write(c.Source,Example()); var h=await Hash(f); var s=Settings(c.Source,c.Target,OperationKind.Move); var rows=Scan(c.Source,null,s);
            var batch=await Execute(engine,rows,s); Assert(bin.Recycled.Count==1 && await Hash(bin.Recycled[0])==h && !File.Exists(f)); await engine.UndoAsync(batch,None); Assert(bin.Recycled.Count==2 && await Hash(bin.Recycled[1])==h && await Hash(f)==h && !File.Exists(rows[0].TargetPath)); Assert(batch.Retirements.All(r=>r.State=="Recycled"));
        });
        await Run("模拟回收站失败或异常时保留完整文件并记录路径", async () =>
        {
            foreach(var raises in new[]{false,true})
            {
                var c=Case(); var bin=new SimulatedRecycleBin { Fail=!raises, RaiseError=raises }; var engine=new BatchEngine(c.Engine.LogDirectory,bin); var f=Write(c.Source,Example()); var h=await Hash(f); var s=Settings(c.Source,c.Target,OperationKind.Copy); var rows=Scan(c.Source,null,s); var batch=await Execute(engine,rows,s); await engine.UndoAsync(batch,None);
                Assert(await Hash(f)==h && !File.Exists(rows[0].TargetPath) && batch.Warnings.Count>0); Assert(await Hash(batch.Retirements.Single().RetainedPath)==h && batch.Retirements[0].State=="Retained");
            }
        });
        await Run("取消复制的残缺临时文件送交模拟回收站，不占最终名称", async () =>
        {
            var c=Case(); var bin=new SimulatedRecycleBin(); var e=new BatchEngine(c.Engine.LogDirectory,bin); var f=Write(c.Source,Example(),4*1024*1024+3); var h=await Hash(f); var s=Settings(c.Source,c.Target,OperationKind.Copy); var rows=Scan(c.Source,null,s); using var ct=new CancellationTokenSource();
            await Throws<OperationCanceledException>(()=>Execute(e,rows,s,ct.Token,new InlineProgress(p=> { if(p.Bytes>0) ct.Cancel(); }))); Assert(bin.Recycled.Count==1 && new FileInfo(bin.Recycled[0]).Length<new FileInfo(f).Length && await Hash(f)==h && !File.Exists(rows[0].TargetPath));
        });
        await Run("删除预设使用模拟回收站，失败时保留 JSON 完整字节", async () =>
        {
            foreach(var fail in new[]{false,true})
            {
                var c=Case(); var bin=new SimulatedRecycleBin{Fail=fail}; var engine=new BatchEngine(c.Engine.LogDirectory,bin); var preset=Path.Combine(c.Source,"规则.json"); JsonStore.Save(preset,General()); var h=await Hash(preset); var warning=engine.RecyclePreset(preset); var batch=engine.History()[0]; var retained=fail?batch.Retirements[0].RetainedPath:bin.Recycled[0]; Assert(!File.Exists(preset) && await Hash(retained)==h && (warning.Length>0)==fail);
            }
        });
        await Run("移动撤销中途取消：原批次和覆盖备份保持完整", async () =>
        {
            var c=Case(); Write(c.Source,Example("3.5")); Write(c.Source,Example("4.5"),seed:2); var old=Write(c.Target,Example("3.5","100"),seed:3); var oh=await Hash(old); var s=Settings(c.Source,c.Target,OperationKind.Move,true); var rows=Scan(c.Source,null,s); var batch=await Execute(c.Engine,rows,s); using var ct=new CancellationTokenSource();
            await Throws<OperationCanceledException>(()=>c.Engine.UndoAsync(batch,ct.Token,new InlineProgress(p=> { if(p.Message.StartsWith("正在撤销") && p.Finished==1) ct.Cancel(); })));
            foreach(var r in batch.Records) Assert(!File.Exists(r.Source) && await Hash(r.Target)==r.Hash); Assert(await Hash(batch.Records.Single(r=>r.Backup.Length>0).Backup)==oh); await c.Engine.UndoAsync(batch,None); Assert(await Hash(old)==oh);
        });
        await Run("模拟移动撤销崩溃：恢复副本已提交但日志标记滞后", async () =>
        {
            var c=Case(); Write(c.Source,Example()); var s=Settings(c.Source,c.Target,OperationKind.Move); var batch=await Execute(c.Engine,Scan(c.Source,null,s),s); var r=batch.Records[0]; File.Move(r.Target,r.UndoTemp); File.Copy(r.UndoTemp,r.Source); batch.Status="Undoing"; batch.UndoInProgress=true; r.UndoStaged=true; r.UndoRestorePending=true; r.UndoSourceRestored=false; JsonStore.Save(c.Engine.JournalPath(batch),batch);
            await c.Engine.RecoverAsync(c.Engine.Pending()!); Assert(!File.Exists(r.Source) && await Hash(r.Target)==r.Hash && c.Engine.LastUndoable()!=null);
        });
        await Run("新规则设置 JSON 往返保存且仍不含操作路径或覆盖状态", () =>
        {
            var c=Case(); var p=General(RenameKind.Template,"{index:000}_{name}"); p.FieldPattern=@"{field}\[(?<value>[^\]]+)\]"; p.Rename.SequenceStart=12; p.Rename.FirstOnly=true; p.Filters.Add(new(){Kind=FilterKind.FieldEqual,Field="id",Value="001",Comparison=ValueComparison.Text}); var path=Path.Combine(c.Source,"new.json"); JsonStore.Save(path,p); var q=JsonStore.Read<RulePreset>(path); _=new RuleEngine(q);
            Assert(q.FieldPattern==p.FieldPattern && q.Rename.SequenceStart==12 && q.Filters[0].Comparison==ValueComparison.Text && !File.ReadAllText(path).Contains("SourceFolder") && !File.ReadAllText(path).Contains("AllowOverwrite")); return Task.CompletedTask;
        });
        await Run("保留目录不参与递归扫描，避免再次误操作", () =>
        {
            var c=Case(); Write(c.Source,Example()); Write(Path.Combine(c.Source,".批量改名助手_保留_abcd"),Example("3.5")); var p=Rules(); p.Recursive=true; Assert(Scan(c.Source,p).Count==1); return Task.CompletedTask;
        });
        await Run("覆盖备份被改动时撤销整批拒绝且不碰目标", async () =>
        {
            var c=Case(); Write(c.Source,Example()); Write(c.Target,Example(sweep:"100"),seed:2); var s=Settings(c.Source,c.Target,OperationKind.Copy,true); var batch=await Execute(c.Engine,Scan(c.Source,null,s),s); var r=batch.Records[0]; File.AppendAllText(r.Backup,"changed"); var target=await Hash(r.Target); await Throws<UserError>(()=>c.Engine.UndoAsync(batch,None)); Assert(await Hash(r.Target)==target && !File.Exists(r.UndoTemp));
        });
        await Run("空文件与大模拟二进制跨目录复制、撤销保持 SHA-256", async () =>
        {
            var c=Case(); var a=Write(c.Source,"空文件.h5",0); var b=Write(c.Source,"二进制.h5",16*1024*1024+19); var ah=await Hash(a); var bh=await Hash(b); var p=General(RenameKind.Suffix,"_副本"); var s=Settings(c.Source,c.Target,OperationKind.Copy); var rows=Scan(c.Source,p,s); var batch=await Execute(c.Engine,rows,s); foreach(var r in batch.Records) Assert(await Hash(r.Target)==r.Hash); await c.Engine.UndoAsync(batch,None); Assert(await Hash(a)==ah && await Hash(b)==bh);
        });
    }
}
