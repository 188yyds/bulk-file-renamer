using System.Text.Json;
using System.Text.Json.Serialization;

namespace Renamer.Core;

public enum FilterKind { Contains, NotContains, Wildcard, FieldEqual, FieldNotEqual, Greater, GreaterEqual, Less, LessEqual, Range, StartsWith, EndsWith, FieldExists, FieldMissing, FieldContains, FieldNotContains }
public enum ValueComparison { Auto, Numeric, Text }
public enum RenameKind { SetField, TextReplace, RegexReplace, Prefix, Suffix, Template, LowerCase, UpperCase, Trim }
public enum OperationKind { Rename, Move, Copy }

public sealed class FilterRule
{
    public FilterKind Kind { get; set; }
    public string Field { get; set; } = "U";
    public string Value { get; set; } = "3.5";
    public string Upper { get; set; } = "4.5";
    public ValueComparison Comparison { get; set; }
}
public sealed class RenameRule
{
    public RenameKind Kind { get; set; }
    public string Field { get; set; } = "nsweep";
    public string Value { get; set; } = "100";
    public string Find { get; set; } = "";
    public string Replacement { get; set; } = "";
    public bool FirstOnly { get; set; }
    public bool AppendMissingField { get; set; }
    public string AppendSeparator { get; set; } = "_";
    public long SequenceStart { get; set; } = 1;
    public long SequenceStep { get; set; } = 1;
}
// Only these rule fields are persisted in presets. Paths and overwrite are intentionally absent.
public sealed class RulePreset
{
    public string Name { get; set; } = "示例：U 范围与 nsweep";
    public string Wildcard { get; set; } = "*.h5";
    public bool Recursive { get; set; }
    public bool CaseSensitive { get; set; }
    public List<FilterRule> Filters { get; set; } = new() { new() { Kind = FilterKind.Range } };
    public bool RegexEnabled { get; set; }
    public string RegexPattern { get; set; } = "";
    public string FieldPattern { get; set; } = "";
    public string CompoundExtensions { get; set; } = ".tar.gz;.tar.bz2;.tar.xz;.nii.gz;.fits.gz;.csv.gz;.h5.gz;.fastq.gz;.fq.gz;.json.gz";
    public RenameRule Rename { get; set; } = new();
}
public sealed class OperationSettings
{
    public string SourceFolder { get; set; } = "";
    public string TargetFolder { get; set; } = "";
    public OperationKind Kind { get; set; }
    public bool AllowOverwrite { get; set; }
}
public sealed class PreviewRow
{
    public bool Selected { get; set; }
    public string SourcePath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string NewName { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "";
    [JsonIgnore] public bool Matched { get; set; }
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string SourceHash { get; set; } = "";
    public string ExistingTargetHash { get; set; } = "";
    public long Sequence { get; set; }
    [JsonIgnore] public bool Executable => Matched && (Status == "正常" || Status == "已有目标（将备份）");
}
public sealed class UserError : Exception
{
    public UserError(string message) : base(message) { }
    public UserError(string message, Exception inner) : base(message, inner) { }
}
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".writing";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, value, Options);
            fs.Flush(true);
        }
        File.Move(temp, path, true);
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new UserError("JSON 文件内容为空。");
}
