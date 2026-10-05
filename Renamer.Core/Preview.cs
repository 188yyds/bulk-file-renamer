namespace Renamer.Core;

public static class PathSafety
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    public static bool Same(string a, string b) => Comparer.Equals(Path.GetFullPath(a), Path.GetFullPath(b));
    public static bool IsWithin(string path, string root)
    {
        var prefix = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    public static string NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return "名称为空或为特殊目录名";
        if (name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))) return "名称含 Windows 非法字符";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "名称不能以点或空格结尾";
        if (name.Length > 255) return "单个文件名超过 255 个字符";
        var first = name.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (first.Length == 4 && (first.StartsWith("COM", StringComparison.Ordinal) || first.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(first[3]))) return "名称是 Windows 保留设备名";
        return "";
    }
    public static void CheckPath(string path)
    {
        // A conservative MAX_PATH limit works without requiring a Windows registry change.
        if (Path.GetFullPath(path).Length >= 260) throw new UserError("路径过长（须少于 260 个字符），请缩短目录或文件名：" + path);
        var nameProblem = NameProblem(Path.GetFileName(path));
        if (nameProblem.Length != 0) throw new UserError(nameProblem + "：" + path);
    }
    public static void NoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new UserError("为避免意外操作，不能使用符号链接或目录联接：" + p);
        }
    }
    public static void ProbeFile(string path, bool writable)
    {
        NoLinks(path);
        if (!File.Exists(path)) throw new UserError("文件不存在或无法访问：" + path);
        if (writable && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) throw new UserError("文件为只读，无法安全移动或改名：" + path);
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (UnauthorizedAccessException ex) { throw new UserError("权限不足，无法访问文件：" + path, ex); }
        catch (IOException ex) { throw new UserError("文件被占用或无法独占访问，请关闭相关程序：" + path, ex); }
    }
    public static void ProbeDirectory(string directory, bool create)
    {
        NoLinks(directory);
        try
        {
            if (create) Directory.CreateDirectory(directory);
            var name = Path.Combine(directory, ".__bra_probe_" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(name, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { /* Only an empty, exclusively created application-owned permission probe. */ }
        }
        catch (UnauthorizedAccessException ex) { throw new UserError("文件夹权限不足：" + directory, ex); }
        catch (IOException ex) { throw new UserError("文件夹不可写或不可用：" + directory, ex); }
    }
}

public sealed class PreviewResult
{
    public List<PreviewRow> Rows { get; set; } = new();
    public int SkippedLinks { get; set; }
}
public static class PreviewScanner
{
    public static PreviewResult Scan(RulePreset preset, OperationSettings settings, CancellationToken token, IProgress<string>? progress = null)
    {
        var rules = new RuleEngine(preset);
        if (!Directory.Exists(settings.SourceFolder)) throw new UserError("请先选择存在且可访问的源文件夹。");
        PathSafety.NoLinks(settings.SourceFolder);
        if (settings.Kind != OperationKind.Rename && string.IsNullOrWhiteSpace(settings.TargetFolder)) throw new UserError("移动或复制需要选择目标文件夹。");
        if (settings.Kind != OperationKind.Rename) PathSafety.NoLinks(settings.TargetFolder);
        var sourceRoot = Path.GetFullPath(settings.SourceFolder);
        var targetRoot = settings.Kind == OperationKind.Rename ? sourceRoot : Path.GetFullPath(settings.TargetFolder);
        var result = new PreviewResult();
        var stack = new Stack<string>(); stack.Push(sourceRoot);
        var candidates = new List<string>();
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            progress?.Report("扫描：" + dir);
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { throw new UserError("无法完整扫描文件夹，已停止：" + dir + "\n" + ex.Message, ex); }
            foreach (var file in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.StartsWith(".__bra_", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) { result.SkippedLinks++; continue; }
                if (!rules.MatchesFileType(name)) continue;
                candidates.Add(file);
            }
            if (preset.Recursive)
            {
                foreach (var sub in Directory.GetDirectories(dir).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (Path.GetFileName(sub).StartsWith(".批量改名助手_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint)) { result.SkippedLinks++; continue; }
                    if (settings.Kind != OperationKind.Rename && !PathSafety.Same(targetRoot, sourceRoot) && PathSafety.Same(sub, targetRoot)) continue;
                    stack.Push(sub);
                }
            }
        }
        long matchedIndex = 0;
        foreach (var file in candidates.OrderBy(p => Path.GetRelativePath(sourceRoot, p), StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal))
        {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var row = new PreviewRow { SourcePath = Path.GetFullPath(file), OriginalName = name, NewName = name };
                row.Matched = rules.Matches(name, out var reason);
                row.Reason = reason;
                var info = new FileInfo(file);
                row.Length = info.Length; row.LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
                if (!row.Matched) { row.Status = "未匹配"; result.Rows.Add(row); continue; }
                row.Sequence = preset.Rename.Kind == RenameKind.Template ? checked(preset.Rename.SequenceStart + matchedIndex * preset.Rename.SequenceStep) : 0;
                matchedIndex++;
                row.NewName = rules.Rename(name, out var renameReason, new NameContext(row.Sequence, Path.GetFileName(Path.GetDirectoryName(file)) ?? "", info.LastWriteTime, info.CreationTime));
                var relative = Path.GetRelativePath(sourceRoot, Path.GetDirectoryName(file)!);
                var nameProblem = PathSafety.NameProblem(row.NewName);
                var targetDirectory = settings.Kind == OperationKind.Rename ? Path.GetDirectoryName(file)! : Path.Combine(targetRoot, relative);
                row.TargetPath = nameProblem.Length == 0 ? Path.GetFullPath(Path.Combine(targetDirectory, row.NewName)) : targetDirectory + Path.DirectorySeparatorChar + row.NewName;
                if (renameReason.Length > 0) { row.Status = "无法改名"; row.Reason += "；" + renameReason; }
                else if (nameProblem.Length > 0) { row.Status = "非法名称"; row.Reason += "；" + nameProblem; }
                else if (row.SourcePath.Length >= 260 || row.TargetPath.Length >= 260) { row.Status = "路径过长"; row.Reason += "；路径须少于 260 个字符"; }
                else if (settings.Kind == OperationKind.Copy && PathSafety.Same(file, row.TargetPath)) { row.Status = "目标是源文件"; }
                else if (string.Equals(row.SourcePath, row.TargetPath, StringComparison.Ordinal)) { row.Status = "无变化"; }
                else row.Status = "正常";
                result.Rows.Add(row);
        }
        foreach (var group in result.Rows.Where(r => r.Matched && r.Status == "正常").GroupBy(r => r.TargetPath, PathSafety.Comparer).Where(g => g.Count() > 1))
            foreach (var row in group) { row.Status = "批内重名"; row.Reason += "；多个文件生成同一目标，请修改规则"; }
        var sources = result.Rows.Where(r => r.Status == "正常").Select(r => r.SourcePath).ToHashSet(PathSafety.Comparer);
        foreach (var row in result.Rows.Where(r => r.Status == "正常"))
        {
            if (settings.Kind == OperationKind.Copy && sources.Contains(row.TargetPath)) { row.Status = "目标是源文件"; row.Reason += "；复制不能改动本批源文件"; continue; }
            if (Directory.Exists(row.TargetPath)) { row.Status = "已有目标目录"; continue; }
            if (!File.Exists(row.TargetPath) || (settings.Kind != OperationKind.Copy && sources.Contains(row.TargetPath))) continue;
            row.Status = settings.AllowOverwrite ? "已有目标（将备份）" : "已有目标";
            row.Reason += settings.AllowOverwrite ? "；执行前会再次确认并备份旧目标" : "；默认不覆盖";
        }
        // A source with an illegal target cannot participate in a staged cycle.
        // Propagate that fact through chains so the initial preview stays conservative.
        bool changed;
        do
        {
            changed = false;
            var participating = result.Rows.Where(r => r.Executable).Select(r => r.SourcePath).ToHashSet(PathSafety.Comparer);
            foreach (var row in result.Rows.Where(r => r.Status == "正常"))
            {
                if (settings.Kind == OperationKind.Copy || !File.Exists(row.TargetPath) || participating.Contains(row.TargetPath)) continue;
                row.Status = settings.AllowOverwrite ? "已有目标（将备份）" : "已有目标";
                row.Reason += "；目标源文件不能参与本批暂存"; changed = true;
            }
        } while (changed);
        foreach (var row in result.Rows.Where(r => r.Executable))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("只读校验 SHA-256：" + row.OriginalName);
            try
            {
                row.SourceHash = BatchEngine.HashFile(row.SourcePath, token).GetAwaiter().GetResult();
                var current = new FileInfo(row.SourcePath);
                if (current.Length != row.Length || current.LastWriteTimeUtc.Ticks != row.LastWriteUtcTicks)
                    throw new UserError("扫描时文件发生变化，请重新扫描");
                if (File.Exists(row.TargetPath)) row.ExistingTargetHash = BatchEngine.HashFile(row.TargetPath, token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserError)
            { row.Status = "无法安全读取"; row.Reason += "；" + BatchEngine.ChineseError(ex); }
        }
        foreach (var row in result.Rows) row.Selected = row.Executable;
        return result;
    }
}
