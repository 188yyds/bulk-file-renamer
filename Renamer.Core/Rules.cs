using System.Globalization;
using System.Text.RegularExpressions;

namespace Renamer.Core;

public sealed record NameContext(long Sequence, string Parent, DateTime Modified, DateTime Created);
public sealed class RuleEngine
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    readonly RulePreset preset;
    readonly RegexOptions options;
    readonly StringComparison comparison;
    readonly Regex[] wildcards;
    readonly Regex? filterRegex;
    readonly Regex? renameRegex;
    readonly Dictionary<string, Regex> fieldRegexes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Regex> fieldMarkers = new(StringComparer.Ordinal);
    readonly Dictionary<string, Regex> fieldNames = new(StringComparer.Ordinal);
    readonly Dictionary<string, Regex[]> wildcardFilters = new(StringComparer.Ordinal);
    readonly string[] extensions;
    public static bool IsField(FilterKind k) => k is FilterKind.FieldEqual or FilterKind.FieldNotEqual or FilterKind.Greater or FilterKind.GreaterEqual or FilterKind.Less or FilterKind.LessEqual or FilterKind.Range or FilterKind.FieldExists or FilterKind.FieldMissing or FilterKind.FieldContains or FilterKind.FieldNotContains;
    public static bool IsNumeric(FilterKind k) => k is FilterKind.Greater or FilterKind.GreaterEqual or FilterKind.Less or FilterKind.LessEqual or FilterKind.Range;
    public RuleEngine(RulePreset preset)
    {
        if (preset.Filters == null || preset.Rename == null || preset.Wildcard == null || preset.RegexPattern == null || preset.FieldPattern == null || preset.CompoundExtensions == null) throw new UserError("规则预设格式不完整，请重新保存预设。");
        if (preset.Filters.Any(f => f == null || f.Field == null || f.Value == null || f.Upper == null) || preset.Rename.Field == null || preset.Rename.Value == null || preset.Rename.Find == null || preset.Rename.Replacement == null) throw new UserError("规则预设含空字段，无法加载。");
        this.preset = preset;
        options = RegexOptions.CultureInvariant | (preset.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        comparison = preset.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        wildcards = WildcardSet(preset.Wildcard);
        extensions = preset.CompoundExtensions.Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length).ToArray();
        if (extensions.Any(e => !e.StartsWith('.') || e.Length < 2 || PathSafety.NameProblem("file" + e).Length > 0)) throw new UserError("复合扩展名请填写 .tar.gz;.nii.gz 这样的列表。");
        if (preset.FieldPattern.Length > 0)
        {
            if (!preset.FieldPattern.Contains("{field}", StringComparison.Ordinal)) throw new UserError("自定义字段提取规则必须包含 {field}，表示要查找的字段名。");
            var check = MakeRegex(preset.FieldPattern.Replace("{field}", "sample", StringComparison.Ordinal));
            if (!check.GetGroupNames().Contains("value")) throw new UserError("自定义字段提取规则必须用 (?<value>...) 标记字段值。");
        }
        foreach (var f in preset.Filters)
        {
            if (!Enum.IsDefined(f.Kind) || !Enum.IsDefined(f.Comparison)) throw new UserError("预设包含未知筛选方式。");
            if (f.Kind is not (FilterKind.FieldExists or FilterKind.FieldMissing) && f.Value.Length == 0) throw new UserError("筛选条件的值不能为空。");
            if (IsField(f.Kind)) { ValidateField(f.Field); _ = FieldRegex(f.Field); }
            if (IsNumeric(f.Kind) || ((f.Kind is FilterKind.FieldEqual or FilterKind.FieldNotEqual) && f.Comparison == ValueComparison.Numeric))
            {
                if (!ExactNumber.TryParse(f.Value, out var lower)) throw new UserError("数值条件需要有效数字，小数点用 .，科学计数法指数绝对值不超过 100000。");
                if (f.Kind == FilterKind.Range && (!ExactNumber.TryParse(f.Upper, out var upper) || upper.CompareTo(lower) < 0)) throw new UserError("范围上限必须是数字，并且不能小于下限。");
            }
            if (f.Kind == FilterKind.Wildcard) wildcardFilters[f.Value] = WildcardSet(f.Value);
        }
        if (preset.RegexEnabled)
        {
            if (preset.RegexPattern.Length == 0) throw new UserError("请填写正则筛选表达式。");
            filterRegex = MakeRegex(preset.RegexPattern);
        }
        var rename = preset.Rename;
        if (!Enum.IsDefined(rename.Kind)) throw new UserError("预设包含未知改名方式。");
        if (rename.SequenceStart < 0 || rename.SequenceStep <= 0) throw new UserError("序号起点不能小于 0，步长必须大于 0。");
        switch (rename.Kind)
        {
            case RenameKind.SetField:
                ValidateField(rename.Field); _ = FieldRegex(rename.Field);
                if (rename.Value.Length == 0) throw new UserError("字段新值不能为空。");
                if (rename.AppendSeparator is not ("_" or "-" or "=")) throw new UserError("追加字段分隔符只支持 _、-、=。");
                break;
            case RenameKind.TextReplace:
            case RenameKind.RegexReplace:
                if (rename.Find.Length == 0) throw new UserError("要替换的文本或正则不能为空。");
                if (rename.Kind == RenameKind.RegexReplace) renameRegex = MakeRegex(rename.Find);
                break;
            case RenameKind.Prefix:
            case RenameKind.Suffix:
            case RenameKind.Template:
                if (rename.Value.Length == 0) throw new UserError("前缀、后缀或命名模板不能为空。");
                if (rename.Kind == RenameKind.Template) _ = ExpandTemplate("sample", new(1, "folder", new(2026, 1, 2), new(2026, 1, 2)), true);
                break;
        }
    }
    Regex MakeRegex(string pattern)
    {
        if (pattern.Length > 4096) throw new UserError("表达式过长，最多 4096 个字符。");
        try { return new Regex(pattern, options, RegexTimeout); }
        catch (ArgumentException ex) { throw new UserError("正则表达式格式有误：" + ex.Message, ex); }
    }
    Regex[] WildcardSet(string patterns)
    {
        var list = patterns.Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length == 0 || list.Length > 100) throw new UserError("请填写 1–100 个文件通配符；多个模式用分号隔开，例如 *.h5;*.txt。");
        if (list.Any(p => p.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)) throw new UserError("通配符只填写文件名模式，不填写路径。");
        return list.Select(p => MakeRegex("\\A" + Regex.Escape(p == "*.*" ? "*" : p).Replace("\\*", ".*").Replace("\\?", ".") + "\\z")).ToArray();
    }
    public bool MatchesFileType(string name)
    {
        try { return wildcards.Any(w => w.IsMatch(name)); }
        catch (RegexMatchTimeoutException ex) { throw new UserError("通配符匹配超时，请减少连续的 * 并重新扫描。", ex); }
    }
    public static bool Number(string value, out decimal result) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    public static void ValidateField(string field)
    {
        if (!Regex.IsMatch(field, @"^[\p{L}][\p{L}\p{N}_.-]{0,79}$", RegexOptions.CultureInvariant, RegexTimeout)) throw new UserError("字段名以字母或汉字开头，可含字母、数字、下划线、点和短横线，最多 80 个字符。");
    }
    public (string Stem, string Extension) SplitName(string name)
    {
        var compound = extensions.FirstOrDefault(e => name.Length > e.Length && name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        if (compound != null) return (name[..^compound.Length], name[^compound.Length..]);
        var lastDot = name.LastIndexOf('.');
        return lastDot <= 0 ? (name, "") : (name[..lastDot], name[lastDot..]);
    }
    Regex FieldRegex(string key)
    {
        if (fieldRegexes.TryGetValue(key, out var regex)) return regex;
        var pattern = preset.FieldPattern.Length > 0 ? preset.FieldPattern.Replace("{field}", Regex.Escape(key), StringComparison.Ordinal) :
            @"(?<![\p{L}\p{N}])" + Regex.Escape(key) + @"\s*(?<sep>[_=\-])\s*(?<value>[+\-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+\-]?[0-9]+)?|[^_=\-\s\[\](){};,]+)(?=$|[_=\-\s\[\](){};,])";
        return fieldRegexes[key] = MakeRegex(pattern);
    }
    Regex FieldMarker(string key, bool nameOnly)
    {
        var cache = nameOnly ? fieldNames : fieldMarkers;
        if (!cache.TryGetValue(key, out var result)) cache[key] = result = MakeRegex(@"(?<![\p{L}\p{N}])" + Regex.Escape(key) + (nameOnly ? @"(?=$|[^\p{L}\p{N}])" : @"\s*[_=\-]"));
        return result;
    }
    public Match? GetField(string stem, string key, out string reason)
    {
        if (preset.FieldPattern.Length == 0 && FieldMarker(key, false).Matches(stem).Count > 1)
        { reason = "字段 " + key + " 出现多次（包括不完整字段），无法确定使用哪一个"; return null; }
        var matches = FieldRegex(key).Matches(stem);
        if (matches.Count == 0)
        {
            var exists = FieldMarker(key, true).IsMatch(stem);
            reason = exists ? "字段 " + key + " 已出现，但值为空或格式无法识别" : "缺少字段 " + key;
            return null;
        }
        if (matches.Count > 1) { reason = "字段 " + key + " 出现多次，无法确定使用哪一个"; return null; }
        if (!matches[0].Groups["value"].Success || matches[0].Groups["value"].Length == 0 || matches[0].Groups["value"].Captures.Count != 1) { reason = "字段 " + key + " 的值为空或重复捕获"; return null; }
        reason = ""; return matches[0];
    }
    public bool Matches(string filename, out string reason)
    {
        try
        {
            var (stem, _) = SplitName(filename);
            foreach (var f in preset.Filters)
            {
                bool ok;
                if (!IsField(f.Kind))
                {
                    ok = f.Kind switch
                    {
                        FilterKind.Contains => filename.Contains(f.Value, comparison), FilterKind.NotContains => !filename.Contains(f.Value, comparison),
                        FilterKind.StartsWith => filename.StartsWith(f.Value, comparison), FilterKind.EndsWith => filename.EndsWith(f.Value, comparison),
                        FilterKind.Wildcard => wildcardFilters[f.Value].Any(w => w.IsMatch(filename)), _ => false
                    };
                }
                else
                {
                    var match = GetField(stem, f.Field, out reason);
                    if (f.Kind == FilterKind.FieldMissing)
                    {
                        if (match != null) { reason = "字段 " + f.Field + " 实际存在"; return false; }
                        if (!reason.StartsWith("缺少字段", StringComparison.Ordinal)) return false;
                        continue;
                    }
                    if (match == null) return false;
                    if (f.Kind == FilterKind.FieldExists) continue;
                    var text = match.Groups["value"].Value;
                    if (f.Kind is FilterKind.FieldContains or FilterKind.FieldNotContains) { ok = text.Contains(f.Value, comparison); if (f.Kind == FilterKind.FieldNotContains) ok = !ok; }
                    else
                    {
                        var aNumber = ExactNumber.TryParse(text, out var a); var bNumber = ExactNumber.TryParse(f.Value, out var b);
                        if (f.Kind is FilterKind.FieldEqual or FilterKind.FieldNotEqual)
                        {
                            if (f.Comparison == ValueComparison.Numeric && !aNumber) { reason = "字段 " + f.Field + " 不是有效数字"; return false; }
                            ok = f.Comparison != ValueComparison.Text && aNumber && bNumber ? a.CompareTo(b) == 0 : string.Equals(text, f.Value, comparison);
                            if (f.Kind == FilterKind.FieldNotEqual) ok = !ok;
                        }
                        else
                        {
                            if (!aNumber) { reason = "字段 " + f.Field + " 不是有效数字：" + text; return false; }
                            ExactNumber.TryParse(f.Upper, out var upper); var c = a.CompareTo(b);
                            ok = f.Kind switch { FilterKind.Greater => c > 0, FilterKind.GreaterEqual => c >= 0, FilterKind.Less => c < 0, FilterKind.LessEqual => c <= 0, FilterKind.Range => c >= 0 && a.CompareTo(upper) <= 0, _ => false };
                        }
                    }
                }
                if (!ok) { reason = "未满足条件：" + Describe(f); return false; }
            }
            if (filterRegex != null && !filterRegex.IsMatch(filename)) { reason = "未匹配正则筛选"; return false; }
            reason = "满足全部筛选条件"; return true;
        }
        catch (RegexMatchTimeoutException ex) { throw new UserError("正则执行超过 200 毫秒，已停止扫描；请简化表达式。", ex); }
    }
    public string Rename(string filename, out string reason, NameContext? context = null)
    {
        try
        {
            var (stem, extension) = SplitName(filename); var rule = preset.Rename; string changed;
            switch (rule.Kind)
            {
                case RenameKind.SetField:
                    var match = GetField(stem, rule.Field, out reason);
                    if (match == null)
                    {
                        if (!rule.AppendMissingField || !reason.StartsWith("缺少字段", StringComparison.Ordinal)) return filename;
                        if (MakeRegex(@"(?<![\p{L}\p{N}])" + Regex.Escape(rule.Field) + @"(?=$|[^\p{L}\p{N}])").IsMatch(stem)) { reason = "字段名已出现但格式不明确，禁止重复追加"; return filename; }
                        changed = stem + (stem.Length > 0 ? "_" : "") + rule.Field + rule.AppendSeparator + rule.Value;
                    }
                    else { var g = match.Groups["value"]; changed = stem[..g.Index] + rule.Value + stem[(g.Index + g.Length)..]; }
                    break;
                case RenameKind.TextReplace:
                    var index = stem.IndexOf(rule.Find, comparison);
                    changed = !rule.FirstOnly ? stem.Replace(rule.Find, rule.Replacement, comparison) : index < 0 ? stem : stem[..index] + rule.Replacement + stem[(index + rule.Find.Length)..]; break;
                case RenameKind.RegexReplace: changed = rule.FirstOnly ? renameRegex!.Replace(stem, rule.Replacement, 1) : renameRegex!.Replace(stem, rule.Replacement); break;
                case RenameKind.Prefix: changed = rule.Value + stem; break;
                case RenameKind.Suffix: changed = stem + rule.Value; break;
                case RenameKind.Template:
                    try { changed = ExpandTemplate(stem, context); }
                    catch (MissingField ex) { reason = ex.Message; return filename; }
                    break;
                case RenameKind.LowerCase: changed = stem.ToLowerInvariant(); break;
                case RenameKind.UpperCase: changed = stem.ToUpperInvariant(); break;
                default: changed = stem.Trim(); break;
            }
            reason = ""; return changed + extension;
        }
        catch (RegexMatchTimeoutException ex) { throw new UserError("改名正则执行超过 200 毫秒，已停止扫描。", ex); }
        catch (ArgumentException ex) { throw new UserError("替换内容或模板有错误：" + ex.Message, ex); }
    }
    sealed class MissingField(string message) : Exception(message);
    string ExpandTemplate(string stem, NameContext? context, bool validateOnly = false)
    {
        var result = new System.Text.StringBuilder(); var template = preset.Rename.Value;
        for (var i = 0; i < template.Length;)
        {
            if (template[i] == '{' && i + 1 < template.Length && template[i + 1] == '{') { result.Append('{'); i += 2; continue; }
            if (template[i] == '}' && i + 1 < template.Length && template[i + 1] == '}') { result.Append('}'); i += 2; continue; }
            if (template[i] == '}') throw new UserError("模板中的 } 没有对应的 {；输出大括号请写 {{ 或 }}。");
            if (template[i] != '{') { result.Append(template[i++]); continue; }
            var end = template.IndexOf('}', i + 1); if (end < 0) throw new UserError("模板中的 { 没有对应的 }。");
            var token = template[(i + 1)..end]; var parts = token.Split(':', 2); var key = parts[0]; var format = parts.Length > 1 ? parts[1] : ""; string value;
            switch (key)
            {
                case "name": if (format.Length != 0) throw new UserError("{name} 不接受格式参数。"); value = stem; break;
                case "parent": if (format.Length != 0) throw new UserError("{parent} 不接受格式参数。"); value = context?.Parent ?? ""; break;
                case "index":
                    if (format.Length > 12 || format.Any(c => c != '0')) throw new UserError("序号格式只支持 1–12 个 0，例如 {index:0000}。");
                    value = (context?.Sequence ?? preset.Rename.SequenceStart).ToString(format.Length == 0 ? "0" : format, CultureInfo.InvariantCulture); break;
                case "field":
                    ValidateField(format); if (validateOnly) { value = "value"; break; }
                    var match = GetField(stem, format, out var reason); if (match == null) throw new MissingField(reason);
                    value = match.Groups["value"].Value; break;
                case "modified":
                case "created":
                    if (format.Length == 0 || format.Length > 80 || format.Any(c => !"yMdHhmsft_-. ".Contains(c))) throw new UserError("日期格式支持 y M d H h m s f t、空格及 _-.；例如 yyyyMMdd_HHmmss。");
                    if (context == null) throw new MissingField("此模板需要文件日期，请通过扫描生成预览");
                    value = (key == "modified" ? context.Modified : context.Created).ToString(format, CultureInfo.InvariantCulture); break;
                default: throw new UserError("未知模板变量 {" + token + "}；支持 name、parent、index、field、modified、created。扩展名自动保留。");
            }
            result.Append(value); i = end + 1;
        }
        return result.ToString();
    }
    public static string Describe(FilterRule f) => f.Kind switch
    {
        FilterKind.Contains => "文件名包含 " + f.Value, FilterKind.NotContains => "文件名不包含 " + f.Value, FilterKind.Wildcard => "文件名通配符 " + f.Value,
        FilterKind.StartsWith => "文件名开头为 " + f.Value, FilterKind.EndsWith => "文件名结尾为 " + f.Value, FilterKind.FieldExists => "字段存在：" + f.Field, FilterKind.FieldMissing => "字段不存在：" + f.Field,
        FilterKind.FieldContains => f.Field + " 包含 " + f.Value, FilterKind.FieldNotContains => f.Field + " 不包含 " + f.Value,
        FilterKind.FieldEqual => f.Field + " = " + f.Value + "（" + ComparisonLabel(f.Comparison) + "）", FilterKind.FieldNotEqual => f.Field + " ≠ " + f.Value + "（" + ComparisonLabel(f.Comparison) + "）",
        FilterKind.Greater => f.Field + " > " + f.Value, FilterKind.GreaterEqual => f.Field + " ≥ " + f.Value, FilterKind.Less => f.Field + " < " + f.Value, FilterKind.LessEqual => f.Field + " ≤ " + f.Value,
        _ => f.Field + " 在 [" + f.Value + ", " + f.Upper + "]"
    };
    static string ComparisonLabel(ValueComparison c) => c switch { ValueComparison.Numeric => "数值", ValueComparison.Text => "文本", _ => "自动数值/文本" };
    public static string DescribeRename(RenameRule r) => r.Kind switch
    {
        RenameKind.SetField => "将字段 " + r.Field + " 设置为 " + r.Value + (r.AppendMissingField ? "；缺失时追加" : "；缺失时不处理"),
        RenameKind.TextReplace => "文本替换：" + r.Find + " → " + r.Replacement + (r.FirstOnly ? "（首次）" : "（全部）"),
        RenameKind.RegexReplace => "正则替换：" + r.Find + " → " + r.Replacement + (r.FirstOnly ? "（首次）" : "（全部）"),
        RenameKind.Prefix => "添加前缀：" + r.Value, RenameKind.Suffix => "添加后缀：" + r.Value, RenameKind.Template => "模板：" + r.Value + $"；序号起点 {r.SequenceStart}，步长 {r.SequenceStep}",
        RenameKind.LowerCase => "名称转小写", RenameKind.UpperCase => "名称转大写", _ => "去除名称首尾空格"
    };
}
