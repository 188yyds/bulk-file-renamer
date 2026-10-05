using System.Text;

namespace Renamer.Core;

public static class PreviewExport
{
    public static void WriteNew(string path, IReadOnlyList<PreviewRow> rows)
    {
        if (rows.Count == 0) throw new UserError("请先扫描生成清单。");
        PathSafety.CheckPath(path); PathSafety.NoLinks(path);
        // Reports must never overwrite a user's data, even if the dialog selected an existing name.
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(output, new UTF8Encoding(true));
        writer.WriteLine("批量文件改名助手 — 预览 / 结果清单");
        writer.WriteLine("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        writer.WriteLine($"共 {rows.Count} 行，当前勾选 {rows.Count(r => r.Selected)} 行。此文本不执行任何操作；规则变化后须重新扫描。");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; writer.WriteLine(); writer.WriteLine($"[{i + 1}] 勾选：{(r.Selected ? "是" : "否")}；状态：{r.Status}");
            writer.WriteLine("原始路径：" + r.SourcePath); writer.WriteLine("原文件名：" + r.OriginalName); writer.WriteLine("新文件名：" + r.NewName);
            writer.WriteLine("目标路径：" + r.TargetPath); writer.WriteLine("原因：" + r.Reason); writer.WriteLine("预览时源 SHA-256：" + r.SourceHash);
        }
        writer.Flush(); output.Flush(true);
    }
}
