namespace Renamer.Core;

public interface IRecycleBin
{
    // Implementations MUST refuse an operation which could permanently delete the file.
    bool TryRecycle(string path, out string reason);
}
public sealed class RetainOnlyRecycleBin : IRecycleBin
{
    public bool TryRecycle(string path, out string reason) { reason = "当前环境没有可用的系统回收站"; return false; }
}
public sealed class RetirementRecord
{
    public string OriginalPath { get; set; } = "";
    public string RetainedPath { get; set; } = "";
    public string Reason { get; set; } = "";
    public string State { get; set; } = "Planned";
}
