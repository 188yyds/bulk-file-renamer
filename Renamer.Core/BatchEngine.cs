using System.Security.Cryptography;

namespace Renamer.Core;

public sealed record OperationProgress(string Message, int Finished, int Total, long Bytes = 0, long TotalBytes = 0);
public sealed class BatchRecord
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Stage { get; set; } = "";
    public string CopyTemp { get; set; } = "";
    public string UndoTemp { get; set; } = "";
    public string Backup { get; set; } = "";
    public string Hash { get; set; } = "";
    public string BackupHash { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public bool Staged { get; set; }
    public bool BackedUp { get; set; }
    public bool CopyStarted { get; set; }
    public bool CommitPending { get; set; }
    public bool Completed { get; set; }
    public bool UndoStaged { get; set; }
    public bool UndoSourceRestored { get; set; }
    public bool UndoBackupRestored { get; set; }
    public string RestoreTemp { get; set; } = "";
    public bool UndoRestorePending { get; set; }
    public bool UndoBackupPending { get; set; }
    public bool UndoCopyDeleted { get; set; }
}
public sealed class BatchJournal
{
    public int FormatVersion { get; set; } = 2;
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public OperationKind Kind { get; set; }
    public string Status { get; set; } = "Running";
    public string Error { get; set; } = "";
    public List<BatchRecord> Records { get; set; } = new();
    public bool UndoInProgress { get; set; }
    public List<RetirementRecord> Retirements { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Events { get; set; } = new();
}
public sealed class BatchEngine
{
    public string LogDirectory { get; }
    readonly SemaphoreSlim gate = new(1, 1);
    readonly IRecycleBin recycleBin;
    readonly string protectedDirectory;
    public BatchEngine(string logDirectory, IRecycleBin? recycleBin = null, string? protectedDirectory = null) { LogDirectory = Path.GetFullPath(logDirectory); this.recycleBin = recycleBin ?? new RetainOnlyRecycleBin(); this.protectedDirectory = Path.GetFullPath(protectedDirectory ?? logDirectory); }
    void Warn(BatchJournal batch, string message) { batch.Warnings.Add(message); Save(batch, message); }
    void Retire(BatchJournal batch, string path, string reason, bool keep = false)
    {
        if (!File.Exists(path)) return;
        PathSafety.NoLinks(path);
        var retirement = new RetirementRecord { OriginalPath = path, Reason = reason,
            RetainedPath = Path.Combine(Path.GetDirectoryName(path)!, ".批量改名助手_保留_" + batch.Id[^8..], Guid.NewGuid().ToString("N")[..8] + "_" + (Path.GetFileName(path).StartsWith(".__bra_", StringComparison.Ordinal) ? "暂存文件.bin" : Path.GetFileName(path))) };
        // Near MAX_PATH, a short sibling name keeps failed cleanup recoverable.
        if (retirement.RetainedPath.Length >= 260 || PathSafety.NameProblem(Path.GetFileName(retirement.RetainedPath)).Length > 0)
            retirement.RetainedPath = Path.Combine(Path.GetDirectoryName(path)!, ".__bra_k_" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        PathSafety.CheckPath(retirement.RetainedPath); PathSafety.NoLinks(retirement.RetainedPath);
        batch.Retirements.Add(retirement); Save(batch, "准备安全移除：" + path + "（" + reason + "）");
        Directory.CreateDirectory(Path.GetDirectoryName(retirement.RetainedPath)!);
        File.Move(path, retirement.RetainedPath); retirement.State = "Retained"; Save(batch);
        if (keep) { Warn(batch, "发现异常内容，已隔离保留，不送回收站：" + retirement.RetainedPath); return; }
        try
        {
            if (recycleBin.TryRecycle(retirement.RetainedPath, out var why) && !File.Exists(retirement.RetainedPath))
            { retirement.State = "Recycled"; Save(batch, "已送入系统回收站：" + path); }
            else if (File.Exists(retirement.RetainedPath)) Warn(batch, "未送入回收站（" + why + "），文件安全保留于：" + retirement.RetainedPath);
            else { retirement.State = "Unconfirmed"; Warn(batch, "系统未确认回收结果且保留位置已无文件；请检查回收站。原位置：" + path + "；回收前位置：" + retirement.RetainedPath); }
        }
        catch (Exception ex)
        {
            if (!File.Exists(retirement.RetainedPath)) retirement.State = "Unconfirmed";
            Warn(batch, (File.Exists(retirement.RetainedPath) ? "回收站操作未完成，文件仍保留于：" : "回收结果未确认，请检查系统回收站；回收前位置：") + retirement.RetainedPath + "；" + ChineseError(ex));
        }
    }
    public string RecyclePreset(string path)
    {
        var batch = new BatchJournal { Id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..8], Status = "PresetRemoval" };
        Retire(batch, path, "删除规则预设"); return string.Join("\n", batch.Warnings);
    }
    void CleanupCommitted(BatchJournal batch)
    {
        foreach (var r in batch.Records)
        {
            var path = batch.Status == "Completed" && batch.Kind == OperationKind.Move ? r.Stage : batch.Status == "Undone" && batch.Kind != OperationKind.Rename ? r.UndoTemp : "";
            if (path.Length == 0 || !File.Exists(path)) continue;
            try { Retire(batch, path, batch.Status == "Completed" ? "移动整批核验成功后回收源暂存" : "撤销成功后回收目标副本"); }
            catch (Exception ex) { Warn(batch, "操作已提交；清理停止，完整暂存文件保留于：" + path + "；" + ChineseError(ex)); }
        }
    }
    public string JournalPath(BatchJournal batch) => Path.Combine(LogDirectory, batch.Id + ".json");
    void Save(BatchJournal batch, string? message = null)
    {
        if (message != null) batch.Events.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message);
        JsonStore.Save(JournalPath(batch), batch);
        File.WriteAllLines(Path.Combine(LogDirectory, batch.Id + ".txt"), new[]
        {
            "批量文件改名助手操作日志", "批次：" + batch.Id, "操作：" + batch.Kind, "状态：" + batch.Status,
            "错误：" + batch.Error, ""
        }.Concat(batch.Records.Select(r => r.Source + "  →  " + r.Target + (r.Backup.Length > 0 ? "\n旧目标备份：" + r.Backup : ""))).Concat(new[] { "", "提示：" }).Concat(batch.Warnings).Concat(batch.Retirements.Select(r => r.State + "：" + r.OriginalPath + " → " + r.RetainedPath)).Concat(new[] { "", "过程记录：" }).Concat(batch.Events), new System.Text.UTF8Encoding(true));
    }
    public IReadOnlyList<BatchJournal> History()
    {
        if (!Directory.Exists(LogDirectory)) return Array.Empty<BatchJournal>();
        var list = new List<BatchJournal>();
        foreach (var p in Directory.GetFiles(LogDirectory, "*.json"))
        {
            try { var j = JsonStore.Read<BatchJournal>(p); ValidateJournal(j, p); list.Add(j); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UserError) { throw new UserError("有操作日志损坏，已停止自动撤销。请保留日志并查看：" + p, ex); }
        }
        return list.OrderByDescending(j => j.CreatedUtc).ToArray();
    }
    static void ValidateJournal(BatchJournal j, string path)
    {
        if (j.FormatVersion is not (1 or 2) || j.Id != Path.GetFileNameWithoutExtension(path) || j.Id.Length != 28 ||
            !System.Text.RegularExpressions.Regex.IsMatch(j.Id, @"\A[0-9]{8}_[0-9]{6}_[0-9]{3}_[0-9a-fA-F]{8}\z") ||
            !Enum.IsDefined(j.Kind) || j.Records == null || j.Events == null || j.Warnings == null || j.Retirements == null ||
            j.Status is not ("Running" or "RecoveryNeeded" or "Undoing" or "Completed" or "Undone" or "Cancelled" or "RolledBack" or "PreflightFailed" or "PresetRemoval"))
            throw new UserError("日志结构或版本不合法，禁止继续操作。");
        foreach (var r in j.Records)
        {
            if (r == null || r.Backup == null || r.RestoreTemp == null || r.BackupHash == null || r.Hash == null || r.Length < 0 ||
                r.Hash.Length != 64 || !r.Hash.All(Uri.IsHexDigit) ||
                new[] { r.Source, r.Target, r.Stage, r.CopyTemp, r.UndoTemp }.Any(p => string.IsNullOrEmpty(p) || !Path.IsPathFullyQualified(p)))
                throw new UserError("日志文件记录不完整，已停止恢复。");
            if (!PathSafety.Same(Path.GetDirectoryName(r.Source)!, Path.GetDirectoryName(r.Stage)!) ||
                !PathSafety.Same(Path.GetDirectoryName(r.Target)!, Path.GetDirectoryName(r.CopyTemp)!) ||
                !PathSafety.Same(Path.GetDirectoryName(r.Target)!, Path.GetDirectoryName(r.UndoTemp)!) ||
                !Path.GetFileName(r.Stage).StartsWith(".__bra_s_", StringComparison.Ordinal) ||
                !Path.GetFileName(r.CopyTemp).StartsWith(".__bra_c_", StringComparison.Ordinal) ||
                !Path.GetFileName(r.UndoTemp).StartsWith(".__bra_u_", StringComparison.Ordinal))
                throw new UserError("日志暂存路径不一致，已停止恢复。");
        }
    }
    public BatchJournal? LastUndoable() => History().FirstOrDefault(j => j.Status == "Completed");
    public BatchJournal? Pending() => History().FirstOrDefault(j => j.Status is "Running" or "RecoveryNeeded" or "Undoing");

    public static List<string> ValidateSelected(IReadOnlyList<PreviewRow> rows, OperationSettings settings)
    {
        if (!Enum.IsDefined(settings.Kind)) throw new UserError("未知文件操作方式，禁止执行。");
        if (rows.Count == 0) throw new UserError("请至少勾选一个可执行文件。");
        if (rows.Any(r => !r.Executable)) throw new UserError("勾选的文件中存在非法或不可执行状态，请重新预览。");
        if (rows.GroupBy(r => r.TargetPath, PathSafety.Comparer).Any(g => g.Count() > 1)) throw new UserError("批内重名：多个文件生成同一个目标，禁止执行。");
        if (rows.GroupBy(r => r.SourcePath, PathSafety.Comparer).Any(g => g.Count() > 1)) throw new UserError("同一源文件被重复选择，禁止执行。");
        var sourceSet = rows.Select(r => r.SourcePath).ToHashSet(PathSafety.Comparer);
        var overwrite = new List<string>();
        foreach (var row in rows)
        {
            var nameProblem = PathSafety.NameProblem(row.NewName);
            if (nameProblem.Length > 0) throw new UserError("非法名称：" + nameProblem);
            if (!PathSafety.IsWithin(row.SourcePath, settings.SourceFolder)) throw new UserError("源文件不在所选源目录内，禁止执行：" + row.SourcePath);
            var expectedDirectory = settings.Kind == OperationKind.Rename ? Path.GetDirectoryName(row.SourcePath)! : Path.Combine(settings.TargetFolder, Path.GetRelativePath(settings.SourceFolder, Path.GetDirectoryName(row.SourcePath)!));
            if (!string.Equals(Path.GetFileName(row.SourcePath), row.OriginalName, StringComparison.Ordinal) || !PathSafety.Same(row.TargetPath, Path.Combine(expectedDirectory, row.NewName))) throw new UserError("预览路径与当前操作不一致，请重新扫描。");
            PathSafety.CheckPath(row.SourcePath); PathSafety.CheckPath(row.TargetPath);
            PathSafety.NoLinks(row.TargetPath);
            if (Directory.Exists(row.TargetPath)) throw new UserError("目标位置已存在同名文件夹：" + row.TargetPath);
            if (settings.Kind == OperationKind.Copy && sourceSet.Contains(row.TargetPath)) throw new UserError("复制目标不能指向本批任一源文件：" + row.TargetPath);
            if (string.Equals(row.SourcePath, row.TargetPath, StringComparison.Ordinal)) throw new UserError("路径没有变化：" + row.SourcePath);
            if (File.Exists(row.TargetPath) && !(settings.Kind != OperationKind.Copy && sourceSet.Contains(row.TargetPath)))
            {
                if (!settings.AllowOverwrite) throw new UserError("目标已存在，未开启覆盖：" + row.TargetPath);
                overwrite.Add(row.TargetPath);
            }
        }
        return overwrite;
    }
    public static async Task<string> HashFile(string path, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer.AsMemory(), token)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, n); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    static void SnapshotCheck(BatchRecord r, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != r.Length || info.LastWriteTimeUtc.Ticks != r.LastWriteUtcTicks) throw new UserError("文件在预览后发生变化，请重新扫描：" + r.Source);
    }
    static async Task Verify(string path, string expectedHash, CancellationToken token)
    {
        if (!File.Exists(path) || await HashFile(path, token) != expectedHash) throw new UserError("文件内容已变化或文件缺失，无法安全继续：" + path);
    }
    static async Task CopyVerified(string source, string temporary, string expectedHash, long length, CancellationToken token, IProgress<OperationProgress>? progress, int finished, int total)
    {
        if (File.Exists(temporary) || Directory.Exists(temporary)) throw new UserError("临时路径已被占用，已停止：" + temporary);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (input.Length != length) throw new UserError("复制前源文件长度发生变化：" + source);
            var buffer = new byte[1024 * 1024]; long bytes = 0; int n;
            while ((n = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                token.ThrowIfCancellationRequested();
                await output.WriteAsync(buffer.AsMemory(0, n), token);
                hash.AppendData(buffer, 0, n); bytes += n;
                progress?.Report(new("正在复制并核对：" + Path.GetFileName(source), finished, total, bytes, length));
            }
            await output.FlushAsync(token); output.Flush(true);
            if (bytes != length || output.Length != length || Convert.ToHexString(hash.GetHashAndReset()) != expectedHash) throw new UserError("复制长度或源内容核对失败，目标不会提交：" + source);
        }
        progress?.Report(new("临时副本已落盘：" + temporary, finished, total));
        if (new FileInfo(temporary).Length != length || await HashFile(temporary, token) != expectedHash) throw new UserError("临时副本 SHA-256 核对失败：" + temporary);
        File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
        if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(temporary, File.GetCreationTimeUtc(source));
    }
    async Task RestoreCopy(BatchJournal batch, BatchRecord r, CancellationToken token)
    {
        if (File.Exists(r.Source) || Directory.Exists(r.Source)) throw new UserError("恢复路径发生冲突：" + r.Source);
        if (r.RestoreTemp.Length == 0) r.RestoreTemp = Path.Combine(Path.GetDirectoryName(r.Source)!, ".__bra_r_" + Guid.NewGuid().ToString("N")[..12]);
        PathSafety.CheckPath(r.RestoreTemp); r.UndoRestorePending = true; Save(batch);
        await CopyVerified(r.UndoTemp, r.RestoreTemp, r.Hash, r.Length, token, null, 0, 1);
        token.ThrowIfCancellationRequested(); File.Move(r.RestoreTemp, r.Source);
        r.UndoSourceRestored = true; Save(batch);
        await Verify(r.Source, r.Hash, token);
    }

    public async Task<BatchJournal> ExecuteAsync(IReadOnlyList<PreviewRow> rows, OperationSettings settings, IReadOnlyList<string> approvedOverwrites, CancellationToken token, IProgress<OperationProgress>? progress = null)
    {
        await gate.WaitAsync(token);
        BatchJournal? batch = null;
        var leases = new List<FileStream>();
        void ReleaseLeases() { foreach (var stream in leases) stream.Dispose(); leases.Clear(); }
        try
        {
            if (Pending() != null) throw new UserError("有中断批次需要恢复，请先点击“恢复中断批次”。");
            if (rows.Any(r => PathSafety.IsWithin(r.SourcePath, protectedDirectory) || PathSafety.IsWithin(r.TargetPath, protectedDirectory) || PathSafety.Same(r.TargetPath, protectedDirectory)))
                throw new UserError("不能批量处理软件自身的日志或预设目录，避免破坏撤销记录。请调整源目录、目标目录或取消相关文件的勾选。");
            var overwrite = ValidateSelected(rows, settings);
            if (!overwrite.ToHashSet(PathSafety.Comparer).SetEquals(approvedOverwrites)) throw new UserError("覆盖清单在确认后发生变化，请重新预览并确认。");
            var id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..8];
            batch = new() { Id = id, Kind = settings.Kind };
            for (var i = 0; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = rows[i];
                progress?.Report(new("检查文件和 SHA-256：" + row.OriginalName, i, rows.Count));
                PathSafety.ProbeFile(row.SourcePath, settings.Kind != OperationKind.Copy);
                if (settings.Kind != OperationKind.Rename) WindowsContentSafety.EnsureSimpleStream(row.SourcePath);
                var r = new BatchRecord
                {
                    Source = row.SourcePath, Target = row.TargetPath, Length = row.Length, LastWriteUtcTicks = row.LastWriteUtcTicks,
                    Stage = Path.Combine(Path.GetDirectoryName(row.SourcePath)!, ".__bra_s_" + id[^8..] + "_" + i),
                    CopyTemp = Path.Combine(Path.GetDirectoryName(row.TargetPath)!, ".__bra_c_" + id[^8..] + "_" + i),
                    UndoTemp = Path.Combine(Path.GetDirectoryName(row.TargetPath)!, ".__bra_u_" + id[^8..] + "_" + i)
                };
                SnapshotCheck(r, r.Source); r.Hash = await HashFile(r.Source, token); SnapshotCheck(r, r.Source);
                if (row.SourceHash.Length != 64 || row.SourceHash != r.Hash) throw new UserError("源内容在预览后发生变化，请重新扫描：" + r.Source);
                if (overwrite.Contains(r.Target, PathSafety.Comparer))
                {
                    PathSafety.ProbeFile(r.Target, true);
                    r.Backup = Path.Combine(Path.GetDirectoryName(r.Target)!, ".批量改名助手_备份_" + id, Path.GetFileName(r.Target));
                    PathSafety.CheckPath(r.Backup);
                    r.BackupHash = await HashFile(r.Target, token);
                    if (row.ExistingTargetHash.Length != 64 || row.ExistingTargetHash != r.BackupHash) throw new UserError("已有目标内容在预览后发生变化，请重新扫描并确认：" + r.Target);
                }
                foreach (var p in new[] { r.Stage, r.CopyTemp, r.UndoTemp })
                {
                    PathSafety.CheckPath(p);
                    if (File.Exists(p) || Directory.Exists(p)) throw new UserError("临时路径被占用：" + p);
                }
                batch.Records.Add(r);
            }
            token.ThrowIfCancellationRequested();
            // Revalidate the complete batch before any source is staged.
            if (!ValidateSelected(rows, settings).ToHashSet(PathSafety.Comparer).SetEquals(overwrite)) throw new UserError("目标状态已变化，请重新预览。");
            foreach (var d in batch.Records.Select(r => Path.GetDirectoryName(r.Target)!).Distinct(PathSafety.Comparer)) PathSafety.ProbeDirectory(d, true);
            if (settings.Kind != OperationKind.Copy)
                foreach (var d in batch.Records.Select(r => Path.GetDirectoryName(r.Source)!).Distinct(PathSafety.Comparer)) PathSafety.ProbeDirectory(d, false);
            foreach (var r in batch.Records)
            {
                leases.Add(new FileStream(r.Source, FileMode.Open, FileAccess.Read, settings.Kind == OperationKind.Copy ? FileShare.Read : FileShare.Read | FileShare.Delete));
                await Verify(r.Source, r.Hash, token);
                if (r.Backup.Length > 0) { leases.Add(new FileStream(r.Target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)); await Verify(r.Target, r.BackupHash, token); }
            }
            Save(batch, "执行前检查通过，共 " + rows.Count + " 个文件，覆盖 " + overwrite.Count + " 个目标。");
            if (settings.Kind != OperationKind.Copy)
            {
                foreach (var r in batch.Records)
                {
                    token.ThrowIfCancellationRequested(); SnapshotCheck(r, r.Source);
                    File.Move(r.Source, r.Stage); r.Staged = true;
                    Save(batch, "源文件暂存：" + r.Source);
                    progress?.Report(new("暂存源文件：" + Path.GetFileName(r.Source), 0, rows.Count));
                }
            }
            foreach (var r in batch.Records.Where(r => r.Backup.Length > 0))
            {
                token.ThrowIfCancellationRequested();
                await Verify(r.Target, r.BackupHash, token);
                PathSafety.NoLinks(r.Backup);
                Directory.CreateDirectory(Path.GetDirectoryName(r.Backup)!);
                File.Move(r.Target, r.Backup); r.BackedUp = true;
                Save(batch, "旧目标已备份：" + r.Backup);
            }
            for (var i = 0; i < batch.Records.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var r = batch.Records[i];
                var input = settings.Kind == OperationKind.Copy ? r.Source : r.Stage;
                await Verify(input, r.Hash, token);
                if (File.Exists(r.Target) || Directory.Exists(r.Target)) throw new UserError("提交时目标被其他程序创建，已停止：" + r.Target);
                if (settings.Kind == OperationKind.Rename)
                {
                    r.CommitPending = true; Save(batch);
                    File.Move(r.Stage, r.Target); r.Completed = true;
                    Save(batch, "改名完成：" + r.Target);
                }
                else
                {
                    // A verified temporary copy also handles cross-volume moves without an unsafe overwrite.
                    r.CopyStarted = true; Save(batch);
                    await CopyVerified(input, r.CopyTemp, r.Hash, r.Length, token, progress, i, rows.Count);
                    token.ThrowIfCancellationRequested(); r.CommitPending = true; Save(batch);
                    File.Move(r.CopyTemp, r.Target); r.Completed = true;
                    Save(batch, "完整目标已提交：" + r.Target);
                }
                await Verify(r.Target, r.Hash, token);
                leases.Add(new FileStream(r.Target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
                progress?.Report(new("完成：" + Path.GetFileName(r.Target), i + 1, rows.Count));
            }
            token.ThrowIfCancellationRequested();
            foreach (var r in batch.Records)
            {
                progress?.Report(new("整批最终校验：" + Path.GetFileName(r.Target), rows.Count, rows.Count));
                await Verify(r.Target, r.Hash, token);
                if (settings.Kind != OperationKind.Rename)
                {
                    var original = settings.Kind == OperationKind.Copy ? r.Source : r.Stage;
                    WindowsContentSafety.EnsureSimpleStream(original); WindowsContentSafety.EnsureSimpleStream(r.Target);
                    await Verify(original, r.Hash, token);
                }
            }
            token.ThrowIfCancellationRequested();
            batch.Status = "Completed"; Save(batch, "整批 SHA-256 核验通过，可撤销上一批。");
            ReleaseLeases(); CleanupCommitted(batch);
            return batch;
        }
        catch (Exception ex)
        {
            ReleaseLeases();
            if (batch?.Status == "Completed") throw new UserError("文件操作已经完成，但后续日志或清理未完成。请保留暂存文件并查看日志：" + JournalPath(batch), ex);
            if (batch != null && File.Exists(JournalPath(batch)))
            {
                batch.Error = ChineseError(ex);
                progress?.Report(new("正在安全回滚，请勿关闭程序…", 0, batch.Records.Count));
                var errors = await Rollback(batch);
                batch.Status = errors.Count == 0 ? (ex is OperationCanceledException ? "Cancelled" : "RolledBack") : "RecoveryNeeded";
                batch.Error += errors.Count == 0 ? "；本批已回滚。" : "；回滚未完成：" + string.Join("；", errors);
                Save(batch, batch.Error);
                if (errors.Count > 0) throw new UserError(batch.Error + "\n日志：" + JournalPath(batch), ex);
            }
            else if (batch != null)
            {
                batch.Status = ex is OperationCanceledException ? "Cancelled" : "PreflightFailed";
                batch.Error = ChineseError(ex);
                try { Save(batch, "执行前检查停止，未改动源文件：" + batch.Error); } catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { /* Preserve the original error if logging itself is unavailable. */ }
            }
            if (ex is OperationCanceledException) throw;
            if (ex is UserError) throw;
            throw new UserError(ChineseError(ex), ex);
        }
        finally { ReleaseLeases(); gate.Release(); }
    }

    async Task<List<string>> Rollback(BatchJournal batch)
    {
        var errors = new List<string>();
        foreach (var r in batch.Records.AsEnumerable().Reverse())
        {
            try
            {
                if (r.Completed)
                {
                    PathSafety.ProbeFile(r.Target, true);
                    if (batch.Kind != OperationKind.Rename && (batch.Kind == OperationKind.Copy || File.Exists(r.Stage)))
                    {
                        await Verify(batch.Kind == OperationKind.Copy ? r.Source : r.Stage, r.Hash, CancellationToken.None);
                        var changed = await HashFile(r.Target, CancellationToken.None) != r.Hash;
                        Retire(batch, r.Target, "执行回滚：移除已生成副本", changed);
                    }
                    else
                    {
                        await Verify(r.Target, r.Hash, CancellationToken.None);
                        if (File.Exists(r.Stage)) throw new UserError("暂存路径被占用：" + r.Stage);
                        if (PathSafety.Same(Path.GetDirectoryName(r.Target)!, Path.GetDirectoryName(r.Stage)!)) File.Move(r.Target, r.Stage);
                        else
                        {
                            // Legacy move journals may no longer have their original stage.
                            // Never rely on File.Move's implicit cross-volume copy/delete fallback.
                            if (r.RestoreTemp.Length == 0) r.RestoreTemp = Path.Combine(Path.GetDirectoryName(r.Stage)!, ".__bra_r_" + Guid.NewGuid().ToString("N")[..12]);
                            PathSafety.CheckPath(r.RestoreTemp); Save(batch);
                            if (File.Exists(r.RestoreTemp)) Retire(batch, r.RestoreTemp, "恢复上次中断留下的临时副本");
                            await CopyVerified(r.Target, r.RestoreTemp, r.Hash, r.Length, CancellationToken.None, null, 0, 1);
                            File.Move(r.RestoreTemp, r.Stage); await Verify(r.Stage, r.Hash, CancellationToken.None);
                            Retire(batch, r.Target, "跨目录恢复核验成功后回收目标");
                        }
                    }
                    r.Completed = false; r.CommitPending = false;
                    Save(batch);
                }
                if (r.CopyStarted && File.Exists(r.CopyTemp)) Retire(batch, r.CopyTemp, "取消或失败后的不完整临时副本");
                if (r.RestoreTemp.Length > 0 && File.Exists(r.RestoreTemp)) Retire(batch, r.RestoreTemp, "失败后的恢复临时副本");
            }
            catch (Exception ex) { errors.Add(ChineseError(ex)); }
        }
        foreach (var r in batch.Records)
        {
            try
            {
                if (batch.Kind != OperationKind.Copy && File.Exists(r.Stage))
                {
                    await Verify(r.Stage, r.Hash, CancellationToken.None);
                    if (File.Exists(r.Source) || Directory.Exists(r.Source)) throw new UserError("源位置发生冲突，暂存文件已保留：" + r.Source);
                    File.Move(r.Stage, r.Source); r.Staged = false; Save(batch);
                }
            }
            catch (Exception ex) { errors.Add(ChineseError(ex)); }
        }
        foreach (var r in batch.Records.Where(r => r.BackedUp))
        {
            try
            {
                await Verify(r.Backup, r.BackupHash, CancellationToken.None);
                if (File.Exists(r.Target) || Directory.Exists(r.Target)) throw new UserError("旧目标恢复位置发生冲突，备份已保留：" + r.Backup);
                File.Move(r.Backup, r.Target); r.BackedUp = false; Save(batch);
            }
            catch (Exception ex) { errors.Add(ChineseError(ex)); }
        }
        return errors;
    }

    public async Task UndoAsync(BatchJournal batch, CancellationToken token, IProgress<OperationProgress>? progress = null)
    {
        await gate.WaitAsync(token);
        var leases = new List<FileStream>();
        void ReleaseLeases() { foreach (var stream in leases) stream.Dispose(); leases.Clear(); }
        try
        {
            if (Pending() != null) throw new UserError("请先恢复中断批次。");
            var latest = LastUndoable();
            if (batch.Status != "Completed" || latest?.Id != batch.Id) throw new UserError("只能撤销最近一批尚未撤销的成功操作。");
            var targets = batch.Records.Select(r => r.Target).ToHashSet(PathSafety.Comparer);
            foreach (var r in batch.Records)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("撤销前核对：" + Path.GetFileName(r.Target), 0, batch.Records.Count));
                PathSafety.ProbeFile(r.Target, true);
                if (batch.Kind != OperationKind.Rename) WindowsContentSafety.EnsureSimpleStream(r.Target);
                await Verify(r.Target, r.Hash, token);
                if (batch.Kind != OperationKind.Copy && (File.Exists(r.Source) || Directory.Exists(r.Source)) && !targets.Contains(r.Source)) throw new UserError("撤销冲突：原位置已被其他文件占用，未作任何撤销：" + r.Source);
                if (r.Backup.Length > 0) { PathSafety.ProbeFile(r.Backup, true); await Verify(r.Backup, r.BackupHash, token); }
                if (File.Exists(r.UndoTemp) || Directory.Exists(r.UndoTemp)) throw new UserError("撤销临时路径被占用：" + r.UndoTemp);
            }
            foreach (var d in batch.Records.Select(r => Path.GetDirectoryName(r.Target)!).Distinct(PathSafety.Comparer)) PathSafety.ProbeDirectory(d, false);
            if (batch.Kind != OperationKind.Copy)
                foreach (var d in batch.Records.Select(r => Path.GetDirectoryName(r.Source)!).Distinct(PathSafety.Comparer)) PathSafety.ProbeDirectory(d, true);
            foreach (var r in batch.Records)
            {
                leases.Add(new FileStream(r.Target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
                await Verify(r.Target, r.Hash, token);
                if (r.Backup.Length > 0) { leases.Add(new FileStream(r.Backup, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)); await Verify(r.Backup, r.BackupHash, token); }
                r.UndoStaged = r.UndoSourceRestored = r.UndoRestorePending = r.UndoBackupRestored = r.UndoBackupPending = r.UndoCopyDeleted = false;
            }
            batch.Status = "Undoing"; batch.UndoInProgress = true; Save(batch, "撤销前检查通过。");
            try
            {
                foreach (var r in batch.Records)
                {
                    token.ThrowIfCancellationRequested(); await Verify(r.Target, r.Hash, token);
                    File.Move(r.Target, r.UndoTemp); r.UndoStaged = true; Save(batch);
                }
                for (var i = 0; i < batch.Records.Count; i++)
                {
                    token.ThrowIfCancellationRequested(); var r = batch.Records[i];
                    if (batch.Kind != OperationKind.Copy)
                    {
                        r.UndoRestorePending = true; Save(batch);
                        if (batch.Kind == OperationKind.Rename) { File.Move(r.UndoTemp, r.Source); r.UndoSourceRestored = true; Save(batch); }
                        else { await RestoreCopy(batch, r, token); leases.Add(new FileStream(r.Source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)); }
                    }
                    progress?.Report(new("正在撤销：" + Path.GetFileName(r.Source), i + 1, batch.Records.Count));
                }
                foreach (var r in batch.Records.Where(r => r.Backup.Length > 0))
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(r.Target) || Directory.Exists(r.Target)) throw new UserError("旧目标恢复路径发生冲突：" + r.Target);
                    await Verify(r.Backup, r.BackupHash, token);
                    r.UndoBackupPending = true; Save(batch);
                    File.Move(r.Backup, r.Target); r.UndoBackupRestored = true; Save(batch);
                }
                token.ThrowIfCancellationRequested();
                foreach (var r in batch.Records)
                {
                    if (batch.Kind != OperationKind.Copy) await Verify(r.Source, r.Hash, token);
                    if (r.Backup.Length > 0) await Verify(r.Target, r.BackupHash, token);
                    if (batch.Kind != OperationKind.Rename) await Verify(r.UndoTemp, r.Hash, token);
                }
                token.ThrowIfCancellationRequested();
                batch.Status = "Undone"; batch.UndoInProgress = false; Save(batch, "整批撤销核验成功，旧目标备份已恢复；备份文件夹不会自动删除。");
                ReleaseLeases(); CleanupCommitted(batch);
            }
            catch (Exception ex)
            {
                ReleaseLeases();
                if (batch.Status == "Undone") throw new UserError("撤销已经完成，但清理或日志未完成。请保留暂存文件并查看日志。", ex);
                var errors = await RollbackUndo(batch);
                batch.Status = errors.Count == 0 ? "Completed" : "RecoveryNeeded"; batch.UndoInProgress = errors.Count != 0;
                batch.Error = ChineseError(ex) + (errors.Count == 0 ? "；撤销操作已回滚，原批次仍可撤销。" : "；撤销回滚未完成：" + string.Join("；", errors));
                Save(batch, batch.Error);
                if (ex is OperationCanceledException && errors.Count == 0) throw;
                throw new UserError(batch.Error, ex);
            }
        }
        finally { ReleaseLeases(); gate.Release(); }
    }
    async Task<List<string>> RollbackUndo(BatchJournal batch)
    {
        var errors = new List<string>();
        foreach (var r in batch.Records.Where(r => r.UndoBackupRestored))
        {
            try { await Verify(r.Target, r.BackupHash, CancellationToken.None); if (File.Exists(r.Backup)) throw new UserError("备份位置被占用：" + r.Backup); File.Move(r.Target, r.Backup); r.UndoBackupRestored = false; r.UndoBackupPending = false; Save(batch); }
            catch (Exception ex) { errors.Add(ChineseError(ex)); }
        }
        foreach (var r in batch.Records.Where(r => r.UndoSourceRestored))
        {
            try
            {
                await Verify(r.Source, r.Hash, CancellationToken.None);
                if (batch.Kind == OperationKind.Move && File.Exists(r.UndoTemp)) { await Verify(r.UndoTemp, r.Hash, CancellationToken.None); Retire(batch, r.Source, "撤销回滚：回收恢复的副本"); }
                else { if (File.Exists(r.UndoTemp)) throw new UserError("撤销暂存冲突：" + r.UndoTemp); File.Move(r.Source, r.UndoTemp); }
                r.UndoSourceRestored = false; r.UndoRestorePending = false; Save(batch);
            }
            catch (Exception ex) { errors.Add(ChineseError(ex)); }
        }
        foreach (var r in batch.Records.Where(r => r.RestoreTemp.Length > 0 && File.Exists(r.RestoreTemp)))
        {
            try { Retire(batch, r.RestoreTemp, "撤销回滚：不完整恢复副本"); } catch (Exception ex) { errors.Add(ChineseError(ex)); }
        }
        foreach (var r in batch.Records.Where(r => r.UndoStaged))
        {
            try
            {
                await Verify(r.UndoTemp, r.Hash, CancellationToken.None);
                if (File.Exists(r.Target) || Directory.Exists(r.Target)) throw new UserError("撤销回滚路径冲突，完整文件已保留：" + r.UndoTemp);
                File.Move(r.UndoTemp, r.Target); r.UndoStaged = false; Save(batch);
            }
            catch (Exception ex) { errors.Add(ChineseError(ex)); }
        }
        return errors;
    }
    public async Task RecoverAsync(BatchJournal batch, IProgress<OperationProgress>? progress = null)
    {
        await gate.WaitAsync();
        try
        {
            if (batch.Status is not ("Running" or "RecoveryNeeded" or "Undoing")) throw new UserError("此批次无需恢复。");
            progress?.Report(new("正在核对中断批次…", 0, batch.Records.Count));
            // Reconcile paths for a crash between a filesystem operation and the next journal write.
            var undo = batch.UndoInProgress || batch.Status == "Undoing" || batch.Records.Any(r => r.UndoStaged || r.UndoSourceRestored || r.UndoBackupRestored || r.UndoCopyDeleted);
            if (undo)
            {
                foreach (var r in batch.Records)
                {
                    if (File.Exists(r.UndoTemp)) { await Verify(r.UndoTemp, r.Hash, CancellationToken.None); r.UndoStaged = true; if (batch.Kind != OperationKind.Move) r.UndoSourceRestored = false; }
                    if (batch.Kind == OperationKind.Move && !File.Exists(r.Source)) r.UndoSourceRestored = false;
                    if (batch.Kind == OperationKind.Move && r.UndoRestorePending && File.Exists(r.Source)) { await Verify(r.Source, r.Hash, CancellationToken.None); r.UndoSourceRestored = true; }
                    if (batch.Kind != OperationKind.Copy && !File.Exists(r.UndoTemp) && File.Exists(r.Source) && r.UndoStaged) { await Verify(r.Source, r.Hash, CancellationToken.None); r.UndoSourceRestored = true; }
                    if (r.Backup.Length > 0 && !File.Exists(r.Backup) && File.Exists(r.Target)) { await Verify(r.Target, r.BackupHash, CancellationToken.None); r.UndoBackupRestored = true; }
                    if (r.Backup.Length > 0 && File.Exists(r.Backup)) r.UndoBackupRestored = false;
                    if (r.UndoStaged && !File.Exists(r.UndoTemp) && !r.UndoSourceRestored && File.Exists(r.Target) && await HashFile(r.Target, CancellationToken.None) == r.Hash) r.UndoStaged = false;
                }
                if (batch.Kind == OperationKind.Copy && batch.Records.Any(r => r.UndoStaged && !File.Exists(r.UndoTemp)))
                {
                    foreach (var r in batch.Records)
                    {
                        if (r.Backup.Length > 0) await Verify(r.Target, r.BackupHash, CancellationToken.None);
                        else if (File.Exists(r.Target)) throw new UserError("恢复撤销遇到目标冲突：" + r.Target);
                    }
                    foreach (var r in batch.Records.Where(r => File.Exists(r.UndoTemp))) { await Verify(r.UndoTemp, r.Hash, CancellationToken.None); Retire(batch, r.UndoTemp, "完成中断的复制撤销"); }
                    batch.Status = "Undone"; Save(batch, "已完成中断的复制撤销。"); return;
                }
                var errors = await RollbackUndo(batch);
                batch.Status = errors.Count == 0 ? "Completed" : "RecoveryNeeded"; batch.UndoInProgress = errors.Count != 0;
                batch.Error = string.Join("；", errors); Save(batch, "恢复中断的撤销：" + batch.Status);
                if (errors.Count > 0) throw new UserError("无法安全恢复：" + batch.Error);
            }
            else
            {
                foreach (var r in batch.Records)
                {
                    if (File.Exists(r.Stage)) { await Verify(r.Stage, r.Hash, CancellationToken.None); r.Staged = true; }
                    if (r.Backup.Length > 0 && File.Exists(r.Backup)) { await Verify(r.Backup, r.BackupHash, CancellationToken.None); r.BackedUp = true; }
                    if (r.Completed && !File.Exists(r.Target)) { r.Completed = false; r.CommitPending = false; }
                    if (r.CommitPending && !r.Completed && File.Exists(r.Target) && !File.Exists(r.CopyTemp) && (batch.Kind != OperationKind.Rename || !File.Exists(r.Stage)))
                    {
                        await Verify(r.Target, r.Hash, CancellationToken.None); r.Completed = true;
                    }
                }
                var errors = await Rollback(batch);
                batch.Status = errors.Count == 0 ? "RolledBack" : "RecoveryNeeded";
                batch.Error = string.Join("；", errors); Save(batch, "恢复中断的执行：" + batch.Status);
                if (errors.Count > 0) throw new UserError("无法安全恢复：" + batch.Error);
            }
        }
        finally { gate.Release(); }
    }
    public static string ChineseError(Exception ex) => ex switch
    {
        OperationCanceledException => "操作已取消",
        UserError => ex.Message,
        UnauthorizedAccessException => "权限不足或文件只读：" + ex.Message,
        IOException => "文件被占用、路径冲突或磁盘不可用：" + ex.Message,
        OverflowException => "序号超过可用范围，请减小起点、步长或文件数量。",
        _ => "操作失败：" + ex.Message
    };
}
