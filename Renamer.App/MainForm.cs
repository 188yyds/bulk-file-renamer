using System.ComponentModel;
using System.Diagnostics;
using Renamer.Core;

namespace Renamer.App;

public sealed class MainForm : Form
{
    static readonly string[] FilterLabels = { "文件名包含", "文件名不包含", "通配符匹配", "字段等于", "字段不等于", "数值大于", "数值大于等于", "数值小于", "数值小于等于", "数值范围（包含边界）", "文件名开头是", "文件名结尾是", "字段存在", "字段缺失", "字段值包含", "字段值不包含" };
    static readonly string[] RenameLabels = { "设置字段值", "普通文本替换", "正则表达式替换", "添加前缀", "添加后缀", "模板与编号", "名称转小写", "名称转大写", "去除名称首尾空格" };
    static readonly string[] OperationLabels = { "原地改名", "移动原文件", "复制文件" };
    readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "批量文件改名助手");
    readonly BatchEngine engine;
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly TabPage rulesPage = new("文件与规则");
    readonly TabPage previewPage = new("预览与执行");
    readonly TextBox source = new() { Dock = DockStyle.Fill };
    readonly TextBox target = new() { Dock = DockStyle.Fill };
    readonly TextBox wildcard = new() { Text = "*.h5", Width = 180 };
    readonly CheckBox recursive = new() { Text = "包含子文件夹", AutoSize = true };
    readonly CheckBox sensitive = new() { Text = "区分英文字母大小写", AutoSize = true };
    readonly ListBox filters = new() { Dock = DockStyle.Fill, Height = 112 };
    readonly List<FilterRule> filterRules = new();
    readonly ComboBox filterKind = Combo(FilterLabels);
    readonly TextBox field = new() { Text = "U", Width = 90 };
    readonly TextBox value = new() { Text = "3.5", Width = 150 };
    readonly TextBox upper = new() { Text = "4.5", Width = 110 };
    readonly ComboBox valueComparison = Combo(new[] { "自动识别数字", "强制数值比较", "按文本精确比较" });
    readonly TextBox fieldPattern = new() { Dock = DockStyle.Fill };
    readonly TextBox compoundExtensions = new() { Dock = DockStyle.Fill };
    readonly CheckBox firstOnly = new() { Text = "仅替换第一处", AutoSize = true };
    readonly CheckBox appendMissing = new() { Text = "字段缺失时追加（默认关闭）", AutoSize = true };
    readonly ComboBox appendSeparator = Combo(new[] { "_", "-", "=" });
    readonly NumericUpDown sequenceStart = new() { Minimum = 0, Maximum = long.MaxValue, Value = 1, Width = 125 };
    readonly NumericUpDown sequenceStep = new() { Minimum = 1, Maximum = long.MaxValue, Value = 1, Width = 125 };
    readonly CheckBox regexEnabled = new() { Text = "同时启用正则筛选（可选）", AutoSize = true };
    readonly TextBox regex = new() { Dock = DockStyle.Fill };
    readonly ComboBox renameKind = Combo(RenameLabels);
    readonly Label renameFirstLabel = LabelOf("字段名");
    readonly Label renameSecondLabel = LabelOf("新值");
    readonly TextBox renameFirst = new() { Text = "nsweep", Width = 260 };
    readonly TextBox renameSecond = new() { Text = "100", Width = 260 };
    readonly ComboBox operation = Combo(OperationLabels);
    readonly CheckBox overwrite = new() { Text = "允许覆盖已有目标（默认关闭；执行前确认，先备份旧目标）", ForeColor = Color.FromArgb(160, 64, 16), AutoSize = true };
    readonly ComboBox presetNames = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    readonly TextBox presetName = new() { Text = "U范围改nsweep", Width = 200 };
    readonly Button selfCheck = ButtonOf("环境与安全自检");
    readonly Button exportPreview = ButtonOf("导出预览清单 TXT");
    readonly Button recentBatch = ButtonOf("最近批次详情");
    readonly Button scan = ButtonOf("扫描并生成预览");
    readonly Button execute = ButtonOf("执行勾选文件");
    readonly Button undo = ButtonOf("撤销上一批");
    readonly Button recover = ButtonOf("恢复中断批次");
    readonly Button cancel = ButtonOf("取消操作");
    readonly Button selectAll = ButtonOf("全选合法");
    readonly Button selectNone = ButtonOf("取消选择");
    readonly CheckBox onlyMatched = new() { Text = "仅显示匹配文件", AutoSize = true };
    readonly Label summary = new() { Text = "请选择源文件夹，然后扫描。预览不会修改任何文件。", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4, 8, 4, 8) };
    readonly Label status = new() { Text = "就绪 · 离线运行 · 只更改名称和位置", Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 20 };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, BackgroundColor = Color.White, BorderStyle = BorderStyle.None };
    List<PreviewRow> rows = new();
    OperationSettings? scannedSettings;
    CancellationTokenSource? cancellation;
    bool busy;
    bool initializing = true;
    bool settingSelection;
    public MainForm()
    {
        engine = new BatchEngine(Path.Combine(dataDirectory, "操作日志"), new WindowsRecycleBin(), dataDirectory);
        Text = "批量文件改名助手 1.2";
        KeyPreview = true; KeyDown += (_, e) => { if (e.KeyCode == Keys.F1) { TryAction(ShowNameRules); e.Handled = true; } };
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 650);
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        Size = new Size(Math.Min(1180, working.Width - 30), Math.Min(920, working.Height - 40));
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(10) };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        var alwaysHelp = ButtonOf("命名规则与试算（F1）"); alwaysHelp.Click += (_, _) => TryAction(ShowNameRules);
        selfCheck.Click += async (_, _) => await RunSelfCheckAsync(); exportPreview.Click += (_, _) => TryAction(ExportPreview); recentBatch.Click += (_, _) => TryAction(ShowRecentBatch);
        outer.Controls.Add(Flow(alwaysHelp, selfCheck, exportPreview, recentBatch), 0, 0);
        outer.Controls.Add(tabs, 0, 1); outer.Controls.Add(status, 0, 2); outer.Controls.Add(progress, 0, 3); Controls.Add(outer);
        tabs.TabPages.Add(rulesPage); tabs.TabPages.Add(previewPage); tabs.TabPages.Add(BuildHelp());
        BuildRules(); BuildPreview();
        LoadRules(new RulePreset()); TryAction(RefreshPresetNames);
        HookChanges(rulesPage);
        initializing = false;
        execute.Enabled = false; cancel.Enabled = false;
        Shown += (_, _) => RefreshHistory();
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            if (!cancel.Enabled) { MessageBox.Show(this, "正在恢复或回滚，请等待完成后再关闭窗口。", "正在恢复", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (MessageBox.Show(this, "操作正在进行。是否取消并等待安全回滚？回滚完成后再关闭窗口。", "正在操作", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) cancellation?.Cancel();
        };
    }
    static Label LabelOf(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(880, 0), Margin = new Padding(5, 7, 5, 4) };
    static Button ButtonOf(string text) => new() { Text = text, AutoSize = true, Height = 34, MinimumSize = new Size(90, 34), Padding = new Padding(5), Margin = new Padding(5) };
    static ComboBox Combo(string[] labels)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        c.Items.AddRange(labels); c.SelectedIndex = 0; return c;
    }
    static FlowLayoutPanel Flow(params Control[] children)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Padding = new Padding(4) };
        flow.Controls.AddRange(children); return flow;
    }
    static TableLayoutPanel Stack()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8), GrowStyle = TableLayoutPanelGrowStyle.AddRows };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return panel;
    }
    static GroupBox Group(string title, Control content)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), Margin = new Padding(3, 5, 3, 8) };
        group.Controls.Add(content); return group;
    }
    Control FolderRow(string label, TextBox box)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        var choose = ButtonOf("选择文件夹");
        choose.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择" + label, UseDescriptionForTitle = true, ShowNewFolderButton = true }; if (Directory.Exists(box.Text)) dialog.SelectedPath = box.Text; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath; };
        row.Controls.Add(LabelOf(label), 0, 0); row.Controls.Add(box, 1, 0); row.Controls.Add(choose, 2, 0); return row;
    }
    void BuildRules()
    {
        rulesPage.AutoScroll = true;
        var body = Stack(); rulesPage.Controls.Add(body);
        var scanSettings = Stack(); scanSettings.Controls.Add(FolderRow("源文件夹", source));
        scanSettings.Controls.Add(Flow(LabelOf("文件通配符"), wildcard, recursive, sensitive, LabelOf("默认 *.h5；多个格式用分号，例如 *.h5;*.txt")));
        body.Controls.Add(Group("1. 选择要扫描的文件", scanSettings));

        var filterBox = Stack(); filterBox.Controls.Add(filters);
        filterBox.Controls.Add(Flow(LabelOf("条件方式"), filterKind, LabelOf("字段"), field, LabelOf("值 / 下限"), value, LabelOf("范围上限"), upper));
        var add = ButtonOf("添加条件"); var edit = ButtonOf("更新选中条件"); var remove = ButtonOf("删除选中条件");
        add.Click += (_, _) => TryAction(() => { filterRules.Add(ReadFilter()); RefreshFilterList(); InvalidatePreview(); });
        edit.Click += (_, _) => TryAction(() => { var i = filters.SelectedIndex; if (i < 0) throw new UserError("先选择要更新的条件。"); filterRules[i] = ReadFilter(); RefreshFilterList(i); InvalidatePreview(); });
        remove.Click += (_, _) => { if (filters.SelectedIndex >= 0) { filterRules.RemoveAt(filters.SelectedIndex); RefreshFilterList(); InvalidatePreview(); } };
        filters.SelectedIndexChanged += (_, _) => { if (filters.SelectedIndex < 0) return; var f = filterRules[filters.SelectedIndex]; filterKind.SelectedIndex = (int)f.Kind; field.Text = f.Field; value.Text = f.Value; upper.Text = f.Upper; valueComparison.SelectedIndex = (int)f.Comparison; };
        filterBox.Controls.Add(Flow(LabelOf("字段等于 / 不等于的比较方式"), valueComparison));
        filterKind.SelectedIndexChanged += (_, _) => UpdateFilterEditor();
        filterBox.Controls.Add(Flow(add, edit, remove, LabelOf("所有已添加条件必须同时满足；编辑后请点“添加”或“更新”。")));
        filterBox.Controls.Add(Flow(regexEnabled, LabelOf("提示：^ 开头，$ 结尾，.* 任意文本；正则针对完整文件名")));
        filterBox.Controls.Add(regex);
        filterBox.Controls.Add(LabelOf(@"正则示例：^dimer_2_.*_nsweep_251_.*\.h5$  （更多示例见“找文件规则说明”）"));
        filterBox.Controls.Add(LabelOf("高级字段提取（留空自动识别）：{field} 表示字段名，(?<value>...) 标记值。"));
        filterBox.Controls.Add(fieldPattern);
        filterBox.Controls.Add(LabelOf(@"例如识别 U[4.000]：(?<![\p{L}\p{N}]){field}\[(?<value>[^\]]+)\]"));
        body.Controls.Add(Group("2. 筛选条件（并且关系）", filterBox));

        var renameBox = Stack();
        renameBox.Controls.Add(Flow(LabelOf("改名方式"), renameKind, renameFirstLabel, renameFirst, renameSecondLabel, renameSecond));
        renameBox.Controls.Add(LabelOf("每批只用一条规则。只处理扩展名前的名称；扩展名保持不变。正则替换可用 $1 引用第一个分组。"));
        renameBox.Controls.Add(Flow(firstOnly, appendMissing, LabelOf("追加分隔符"), appendSeparator));
        appendSeparator.Width = 65;
        renameBox.Controls.Add(Flow(LabelOf("模板编号起点"), sequenceStart, LabelOf("步长"), sequenceStep, LabelOf("按匹配文件相对路径排序编号；取消勾选不重排编号")));
        renameBox.Controls.Add(LabelOf("复合扩展名保护（分号分隔，扩展名含点，原始大小写不变）"));
        renameBox.Controls.Add(compoundExtensions);
        var namesHelp = ButtonOf("命名规则与试算（F1）"); namesHelp.Click += (_, _) => TryAction(ShowNameRules);
        renameBox.Controls.Add(Flow(namesHelp, LabelOf("模板示例：实验_{field:U}_{index:0000}_{name}。扩展名自动保留，无需写入模板。")));
        renameKind.SelectedIndexChanged += (_, _) => UpdateRenameEditor();
        body.Controls.Add(Group("3. 设置改名规则", renameBox));

        var actions = Stack(); actions.Controls.Add(Flow(LabelOf("文件操作"), operation));
        var targetRow = FolderRow("目标文件夹", target); actions.Controls.Add(targetRow);
        operation.SelectedIndexChanged += (_, _) => { targetRow.Enabled = operation.SelectedIndex != 0; target.Enabled = operation.SelectedIndex != 0; };
        targetRow.Enabled = false;
        actions.Controls.Add(Flow(overwrite));
        actions.Controls.Add(LabelOf("移动、复制时保留相对目录结构；扫描子文件夹时跳过目标目录、备份目录与目录联接。"));
        body.Controls.Add(Group("4. 选择操作方式", actions));

        var presets = Stack(); var save = ButtonOf("保存预设"); var load = ButtonOf("加载预设"); var delete = ButtonOf("删除预设"); var export = ButtonOf("导出 JSON"); var import = ButtonOf("导入 JSON");
        save.Click += (_, _) => TryAction(SavePreset); load.Click += (_, _) => TryAction(LoadPreset); delete.Click += (_, _) => TryAction(DeletePreset);
        export.Click += (_, _) => TryAction(() => { var p = ReadRules(); using var d = new SaveFileDialog { Filter = "JSON 规则文件|*.json", FileName = "改名规则.json" }; if (d.ShowDialog(this) == DialogResult.OK) JsonStore.Save(d.FileName, p); });
        import.Click += (_, _) => TryAction(() => { using var d = new OpenFileDialog { Filter = "JSON 规则文件|*.json" }; if (d.ShowDialog(this) == DialogResult.OK) { var p = JsonStore.Read<RulePreset>(d.FileName); _ = new RuleEngine(p); LoadRules(p); overwrite.Checked = false; InvalidatePreview(); } });
        presets.Controls.Add(Flow(LabelOf("预设名称"), presetName, save, export, import));
        presets.Controls.Add(Flow(LabelOf("已保存预设"), presetNames, load, delete));
        presets.Controls.Add(LabelOf("预设只保存筛选和改名规则；不保存源目录、目标目录及允许覆盖状态。"));
        body.Controls.Add(Group("规则预设", presets));
        var bottomScan = ButtonOf("扫描并进入预览"); bottomScan.Click += async (_, _) => await ScanAsync();
        var example = ButtonOf("填写 U 范围示例"); example.Click += (_, _) => { LoadRules(new RulePreset()); overwrite.Checked = false; InvalidatePreview(); };
        var helpButton = ButtonOf("找文件规则说明"); helpButton.Click += (_, _) => tabs.SelectedIndex = 2;
        body.Controls.Add(Flow(bottomScan, example, helpButton));
    }
    void BuildPreview()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logs = ButtonOf("查看日志文件夹");
        logs.Click += (_, _) => TryAction(() => { Directory.CreateDirectory(engine.LogDirectory); Process.Start(new ProcessStartInfo(engine.LogDirectory) { UseShellExecute = true }); });
        var rulesHelp = ButtonOf("命名规则与试算"); rulesHelp.Click += (_, _) => TryAction(ShowNameRules);
        var toolbar = Flow(rulesHelp, scan, execute, undo, recover, cancel, logs, selectAll, selectNone, onlyMatched);
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(summary, 0, 1); layout.Controls.Add(grid, 0, 2); previewPage.Controls.Add(layout);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "执行", DataPropertyName = nameof(PreviewRow.Selected), Width = 54 });
        AddColumn("状态", nameof(PreviewRow.Status), 145); AddColumn("原始完整路径", nameof(PreviewRow.SourcePath), 360); AddColumn("原文件名", nameof(PreviewRow.OriginalName), 290); AddColumn("新文件名", nameof(PreviewRow.NewName), 290); AddColumn("目标路径", nameof(PreviewRow.TargetPath), 360); AddColumn("匹配或未匹配原因", nameof(PreviewRow.Reason), 330);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 236, 250); grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 242, 248);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 253);
        grid.CellFormatting += (_, e) => { if (e.RowIndex < 0 || grid.Rows[e.RowIndex].DataBoundItem is not PreviewRow r) return; e.CellStyle!.ForeColor = r.Executable ? Color.FromArgb(20, 80, 46) : r.Status is "未匹配" or "无变化" ? Color.Gray : Color.FromArgb(158, 50, 35); };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (settingSelection || e.ColumnIndex != 0 || e.RowIndex < 0 || grid.Rows[e.RowIndex].DataBoundItem is not PreviewRow r) return;
            if (!r.Executable && r.Selected) { settingSelection = true; r.Selected = false; grid.Rows[e.RowIndex].Cells[0].Value = false; settingSelection = false; }
            UpdateSummary();
        };
        scan.Click += async (_, _) => await ScanAsync(); execute.Click += async (_, _) => await ExecuteAsync(); undo.Click += async (_, _) => await UndoAsync(); recover.Click += async (_, _) => await RecoverAsync();
        cancel.Click += (_, _) => { cancellation?.Cancel(); cancel.Enabled = false; status.Text = "正在取消并安全回滚，请勿关闭窗口…"; };
        selectAll.Click += (_, _) => { foreach (var r in rows) r.Selected = r.Executable; BindRows(); };
        selectNone.Click += (_, _) => { foreach (var r in rows) r.Selected = false; BindRows(); };
        onlyMatched.CheckedChanged += (_, _) => BindRows();
    }
    void AddColumn(string title, string property, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, DataPropertyName = property, Width = width, ReadOnly = true });
    void HookChanges(Control root)
    {
        foreach (Control c in root.Controls)
        {
            // The condition editor is not a rule until Add/Update is clicked, but invalidating here is conservative.
            if (c is TextBox t && t != presetName) t.TextChanged += (_, _) => InvalidatePreview();
            else if (c is NumericUpDown n) n.ValueChanged += (_, _) => InvalidatePreview();
            else if (c is CheckBox ck) ck.CheckedChanged += (_, _) => InvalidatePreview();
            else if (c is ComboBox cb && cb != presetNames) cb.SelectedIndexChanged += (_, _) => InvalidatePreview();
            HookChanges(c);
        }
    }
    void InvalidatePreview()
    {
        if (initializing || busy) return;
        scannedSettings = null; execute.Enabled = false;
        if (rows.Count > 0) summary.Text = "规则或路径已修改。请重新扫描预览后执行。";
    }
    void UpdateFilterEditor()
    {
        var k = (FilterKind)filterKind.SelectedIndex; field.Enabled = RuleEngine.IsField(k); upper.Enabled = k == FilterKind.Range;
        value.Enabled = k is not (FilterKind.FieldExists or FilterKind.FieldMissing); valueComparison.Enabled = k is FilterKind.FieldEqual or FilterKind.FieldNotEqual;
    }
    void UpdateRenameEditor()
    {
        var k = (RenameKind)renameKind.SelectedIndex;
        renameFirstLabel.Text = k == RenameKind.SetField ? "字段名" : k == RenameKind.TextReplace ? "要替换的文本" : "正则表达式";
        renameSecondLabel.Text = k == RenameKind.SetField ? "新值" : k == RenameKind.Template ? "命名模板" : k == RenameKind.Prefix ? "前缀" : k == RenameKind.Suffix ? "后缀" : "替换为";
        renameFirst.Visible = renameFirstLabel.Visible = k is RenameKind.SetField or RenameKind.TextReplace or RenameKind.RegexReplace;
        renameSecond.Visible = renameSecondLabel.Visible = k is not (RenameKind.LowerCase or RenameKind.UpperCase or RenameKind.Trim);
        firstOnly.Enabled = k is RenameKind.TextReplace or RenameKind.RegexReplace;
        appendMissing.Enabled = appendSeparator.Enabled = k == RenameKind.SetField;
        sequenceStart.Enabled = sequenceStep.Enabled = k == RenameKind.Template;
    }
    FilterRule ReadFilter()
    {
        var f = new FilterRule { Kind = (FilterKind)filterKind.SelectedIndex, Field = field.Text.Trim(), Value = value.Text, Upper = upper.Text, Comparison = (ValueComparison)valueComparison.SelectedIndex };
        var probe = new RulePreset { Filters = new() { f }, FieldPattern = fieldPattern.Text }; _ = new RuleEngine(probe); return f;
    }
    void RefreshFilterList(int selected = -1)
    {
        filters.Items.Clear(); foreach (var f in filterRules) filters.Items.Add(RuleEngine.Describe(f));
        if (filters.Items.Count > 0) filters.SelectedIndex = selected >= 0 ? selected : filters.Items.Count - 1;
        UpdateFilterEditor();
    }
    RulePreset ReadRules()
    {
        var p = new RulePreset
        {
            Name = presetName.Text.Trim(), Wildcard = wildcard.Text.Trim(), Recursive = recursive.Checked, CaseSensitive = sensitive.Checked,
            Filters = filterRules.Select(f => new FilterRule { Kind = f.Kind, Field = f.Field, Value = f.Value, Upper = f.Upper, Comparison = f.Comparison }).ToList(),
            RegexEnabled = regexEnabled.Checked, RegexPattern = regex.Text, FieldPattern = fieldPattern.Text, CompoundExtensions = compoundExtensions.Text,
            Rename = new RenameRule { Kind = (RenameKind)renameKind.SelectedIndex, FirstOnly = firstOnly.Checked, AppendMissingField = appendMissing.Checked, AppendSeparator = appendSeparator.Text, SequenceStart = (long)sequenceStart.Value, SequenceStep = (long)sequenceStep.Value }
        };
        if (p.Rename.Kind == RenameKind.SetField) { p.Rename.Field = renameFirst.Text.Trim(); p.Rename.Value = renameSecond.Text; }
        else if (p.Rename.Kind is RenameKind.TextReplace or RenameKind.RegexReplace) { p.Rename.Find = renameFirst.Text; p.Rename.Replacement = renameSecond.Text; }
        else p.Rename.Value = renameSecond.Text;
        _ = new RuleEngine(p); return p;
    }
    OperationSettings ReadSettings() => new() { SourceFolder = source.Text.Trim(), TargetFolder = target.Text.Trim(), Kind = (OperationKind)operation.SelectedIndex, AllowOverwrite = overwrite.Checked };
    void LoadRules(RulePreset p)
    {
        _ = new RuleEngine(p);
        wildcard.Text = p.Wildcard; recursive.Checked = p.Recursive; sensitive.Checked = p.CaseSensitive;
        filterRules.Clear(); filterRules.AddRange(p.Filters); RefreshFilterList();
        regexEnabled.Checked = p.RegexEnabled; regex.Text = p.RegexPattern; renameKind.SelectedIndex = (int)p.Rename.Kind;
        renameFirst.Text = p.Rename.Kind == RenameKind.SetField ? p.Rename.Field : p.Rename.Find;
        renameSecond.Text = p.Rename.Kind is RenameKind.TextReplace or RenameKind.RegexReplace ? p.Rename.Replacement : p.Rename.Value;
        fieldPattern.Text = p.FieldPattern; compoundExtensions.Text = p.CompoundExtensions;
        firstOnly.Checked = p.Rename.FirstOnly; appendMissing.Checked = p.Rename.AppendMissingField; appendSeparator.SelectedItem = p.Rename.AppendSeparator;
        sequenceStart.Value = Math.Min(sequenceStart.Maximum, p.Rename.SequenceStart); sequenceStep.Value = Math.Min(sequenceStep.Maximum, p.Rename.SequenceStep);
        presetName.Text = p.Name; UpdateRenameEditor();
    }
    string PresetDirectory => Path.Combine(dataDirectory, "规则预设");
    void RefreshPresetNames()
    {
        presetNames.Items.Clear(); if (!Directory.Exists(PresetDirectory)) return;
        foreach (var p in Directory.GetFiles(PresetDirectory, "*.json").OrderBy(p => p)) presetNames.Items.Add(Path.GetFileNameWithoutExtension(p));
        if (presetNames.Items.Count > 0) presetNames.SelectedIndex = 0;
    }
    void SavePreset()
    {
        var p = ReadRules(); if (PathSafety.NameProblem(p.Name).Length > 0 || string.IsNullOrWhiteSpace(p.Name)) throw new UserError("请填写合法的预设名称。");
        var path = Path.Combine(PresetDirectory, p.Name + ".json");
        if (File.Exists(path) && MessageBox.Show(this, "已存在同名预设，是否更新？", "保存预设", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        JsonStore.Save(path, p); RefreshPresetNames(); presetNames.SelectedItem = p.Name; status.Text = "规则预设已保存。";
    }
    void LoadPreset()
    {
        if (presetNames.SelectedItem is not string name) throw new UserError("请先选择已保存的预设。");
        LoadRules(JsonStore.Read<RulePreset>(Path.Combine(PresetDirectory, name + ".json"))); overwrite.Checked = false; InvalidatePreview(); status.Text = "预设已加载，覆盖已关闭。请检查路径并重新预览。";
    }
    void DeletePreset()
    {
        if (presetNames.SelectedItem is not string name) return;
        if (MessageBox.Show(this, "删除规则预设“" + name + "”？", "删除预设", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var warning = engine.RecyclePreset(Path.Combine(PresetDirectory, name + ".json")); RefreshPresetNames();
        if (warning.Length > 0) ShowText("预设已移出，文件仍保留", warning);
    }
    void ShowText(string title, string content)
    {
        using var dialog = new Form { Text = title, Size = new Size(880, 600), StartPosition = FormStartPosition.CenterParent, Font = Font };
        dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = content });
        dialog.ShowDialog(this);
    }
    void ShowNameRules()
    {
        var p = ReadRules(); var rule = new RuleEngine(p);
        using var dialog = new Form { Text = "命名规则与试算 · 不操作任何文件", Size = new Size(940, 690), MinimumSize = new Size(650, 500), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        var instructions = "当前改名规则：" + RuleEngine.DescribeRename(p.Rename) + "\r\n当前筛选：" + (p.Filters.Count == 0 ? "无普通条件" : string.Join("；", p.Filters.Select(RuleEngine.Describe))) +
            "\r\n\r\n所有改名方式都只处理扩展名前的名称。复合扩展名按设置保护。\r\n字段格式：U_4.000、U-4.000、U=4.000；重复字段会拒绝猜测。\r\n设置字段值只替换该字段的值；缺失时默认不改。主动开启追加后才会增加字段。\r\n文本与正则可选只替换第一处。正则替换引用分组可用 ${1} 或 ${组名}。\r\n前缀、后缀直接加在扩展名前；大小写转换也不动扩展名。\r\n\r\n模板符号：\r\n{name} 原名称（不含扩展名）\r\n{parent} 直接父文件夹名称\r\n{field:U} 名称中 U 的原始值\r\n{index} 序号；{index:0000} 至少四位，不截断较大的序号\r\n{modified:yyyyMMdd_HHmmss} 修改时间；{created:yyyyMMdd} 创建时间\r\n{{ 和 }} 输出普通大括号。未知符号会报错。\r\n示例：实验_{field:U}_{index:0000}_{name}\r\n序号按匹配文件相对路径排序分配；未勾选会留空号，不自动重排。\r\n\r\n试算只用下面的名字，目录示例为“示例目录”，时间使用当前本机时间。实际结果以扫描预览为准。\r\n筛选编辑框中的修改，必须先“添加”或“更新”才会生效。";
        layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = instructions }, 0, 0);
        var input = new TextBox { Dock = DockStyle.Fill, Text = "dimer_2_j_1.000_U_4.000_L_200_ratio_0.150_filling_1010_nsweep_251_mps.h5" };
        var result = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        var tryName = ButtonOf("试算这个文件名"); var help = ButtonOf("完整找文件规则说明"); help.Click += (_, _) => { dialog.Close(); tabs.SelectedIndex = 2; };
        void Calculate()
        {
            try
            {
                var match = rule.MatchesFileType(input.Text) && rule.Matches(input.Text, out _);
                var renamed = rule.Rename(input.Text, out var why, new(p.Rename.SequenceStart, "示例目录", DateTime.Now, DateTime.Now));
                var problem = PathSafety.NameProblem(renamed);
                result.Text = (match ? "满足筛选" : "不满足筛选（实际不会执行）") + "\r\n新名：" + renamed + "\r\n" + (why.Length > 0 ? why : problem.Length > 0 ? "非法名称：" + problem : renamed == input.Text ? "名称无变化" : "名称合法；路径与重名仍需扫描检查");
            }
            catch (Exception ex) { result.Text = BatchEngine.ChineseError(ex); }
        }
        tryName.Click += (_, _) => Calculate(); layout.Controls.Add(input, 0, 1); layout.Controls.Add(Flow(tryName, help), 0, 2); layout.Controls.Add(result, 0, 3);
        dialog.Controls.Add(layout); Calculate(); dialog.ShowDialog(this);
    }
    void TryAction(Action action) { try { action(); } catch (Exception ex) { ShowError(ex); } }
    void ShowError(Exception ex) => MessageBox.Show(this, BatchEngine.ChineseError(ex), "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
    void BindRows()
    {
        grid.DataSource = new BindingList<PreviewRow>((onlyMatched.Checked ? rows.Where(r => r.Matched) : rows).ToList()); UpdateSummary();
    }
    void UpdateSummary()
    {
        if (scannedSettings == null && rows.Count > 0) { execute.Enabled = false; return; }
        summary.Text = $"共 {rows.Count} 个文件 · 匹配 {rows.Count(r => r.Matched)} · 合法 {rows.Count(r => r.Executable)} · 已勾选 {rows.Count(r => r.Selected && r.Executable)} · 覆盖候选 {rows.Count(r => r.Selected && r.Status == "已有目标（将备份）")}\n预览不会修改文件；红色状态禁止执行。";
        execute.Enabled = !busy && scannedSettings != null && rows.Any(r => r.Selected && r.Executable);
    }
    void SetBusy(bool valueBusy)
    {
        busy = valueBusy; selfCheck.Enabled = exportPreview.Enabled = recentBatch.Enabled = !busy; rulesPage.Enabled = !busy; grid.Enabled = !busy; scan.Enabled = !busy; onlyMatched.Enabled = !busy; selectAll.Enabled = !busy; selectNone.Enabled = !busy; execute.Enabled = !busy && scannedSettings != null && rows.Any(r => r.Selected && r.Executable);
        undo.Enabled = !busy; recover.Enabled = !busy; cancel.Enabled = busy;
        if (busy) { cancellation = new(); progress.Value = 0; } else { cancellation?.Dispose(); cancellation = null; RefreshHistory(); }
    }
    IProgress<OperationProgress> OperationReporter() => new ThrottledProgress<OperationProgress>(p =>
    {
        status.Text = p.Message + (p.TotalBytes > 0 ? $" · {p.Bytes / 1048576.0:F1} / {p.TotalBytes / 1048576.0:F1} MB" : "");
        progress.Value = p.Total == 0 ? 0 : Math.Clamp((int)(100.0 * (p.Finished + (p.TotalBytes > 0 ? (double)p.Bytes / p.TotalBytes : 0)) / p.Total), 0, 100);
    });
    async Task ScanAsync()
    {
        if (busy) return;
        try
        {
            var p = ReadRules(); var s = ReadSettings(); scannedSettings = null;
            SetBusy(true); tabs.SelectedTab = previewPage; summary.Text = "正在扫描。旧预览不可执行；扫描不会修改任何文件。";
            var reporter = new ThrottledProgress<string>(text => status.Text = text);
            var result = await Task.Run(() => PreviewScanner.Scan(p, s, cancellation!.Token, reporter));
            rows = result.Rows; scannedSettings = s; BindRows(); progress.Value = 100;
            status.Text = "扫描完成，没有修改任何文件。" + (result.SkippedLinks > 0 ? $" 已跳过 {result.SkippedLinks} 个符号链接或目录联接。" : "");
        }
        catch (OperationCanceledException) { status.Text = "扫描已取消，没有修改文件。"; }
        catch (Exception ex) { ShowError(ex); status.Text = "扫描未完成，请修正规则后重试。"; }
        finally { SetBusy(false); UpdateSummary(); }
    }
    async Task ExecuteAsync()
    {
        if (busy || scannedSettings == null) return;
        try
        {
            grid.EndEdit(); var chosen = rows.Where(r => r.Selected).ToList(); var s = scannedSettings;
            var overwrites = BatchEngine.ValidateSelected(chosen, s);
            if (!ConfirmBatch(chosen, s, overwrites)) return;
            SetBusy(true); var reporter = OperationReporter();
            var batch = await Task.Run(() => engine.ExecuteAsync(chosen, s, overwrites, cancellation!.Token, reporter));
            foreach (var r in chosen) { r.Status = "执行成功"; r.Selected = false; }
            scannedSettings = null; BindRows(); summary.Text = $"本批 {batch.Records.Count} 个文件执行成功。可点击“撤销上一批”。再次操作请重新扫描。";
            progress.Value = 100; status.Text = "执行成功。日志已保存。";
            if (batch.Warnings.Count > 0) ShowText("文件已完成；请查看保留文件提示", string.Join("\r\n\r\n", batch.Warnings));
            MessageBox.Show(this, $"成功处理 {batch.Records.Count} 个文件。\n操作日志：{engine.JournalPath(batch)}", "执行完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { status.Text = "操作已取消，本批已安全回滚。请重新扫描。"; scannedSettings = null; }
        catch (Exception ex) { ShowError(ex); status.Text = "操作未完成，请查看错误及日志，然后重新扫描。"; scannedSettings = null; }
        finally { SetBusy(false); }
    }
    bool ConfirmBatch(List<PreviewRow> chosen, OperationSettings s, List<string> overwrites)
    {
        using var dialog = new Form { Text = overwrites.Count > 0 ? "覆盖已有目标：再次确认" : "执行前确认", Size = new Size(820, 520), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = LabelOf($"操作：{OperationLabels[(int)s.Kind]}  ·  勾选 {chosen.Count} 个文件  ·  覆盖 {overwrites.Count} 个已有目标\n旧目标先移入对应目录的“.批量改名助手_备份_批次号”文件夹，备份不会自动删除。"); heading.MaximumSize = new Size(760, 0);
        var details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = "覆盖清单：\r\n" + (overwrites.Count == 0 ? "无\r\n" : string.Join("\r\n", overwrites) + "\r\n") + "\r\n本批完整操作清单：\r\n" + string.Join("\r\n\r\n", chosen.Select(r => r.SourcePath + "\r\n→ " + r.TargetPath)) };
        var yes = ButtonOf(overwrites.Count > 0 ? "确认备份并执行" : "确认执行"); yes.DialogResult = DialogResult.OK; var no = ButtonOf("取消"); no.DialogResult = DialogResult.Cancel;
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(details, 0, 1); layout.Controls.Add(Flow(yes, no), 0, 2); dialog.Controls.Add(layout); dialog.AcceptButton = yes; dialog.CancelButton = no;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }
    async Task UndoAsync()
    {
        if (busy) return;
        try
        {
            var batch = engine.LastUndoable(); if (batch == null) throw new UserError("没有可撤销的成功批次。");
            if (MessageBox.Show(this, $"撤销上一批 {batch.Records.Count} 个文件操作？\n原位置或内容发生变化时会停止，避免覆盖其他文件。", "撤销上一批", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SetBusy(true); var reporter = OperationReporter(); await Task.Run(() => engine.UndoAsync(batch, cancellation!.Token, reporter));
            if (batch.Warnings.Count > 0) ShowText("撤销后的保留文件提示", string.Join("\r\n\r\n", batch.Warnings));
            scannedSettings = null; status.Text = "上一批已撤销。请重新扫描预览。"; summary.Text = "撤销成功；原文件已恢复，旧目标备份已恢复。"; progress.Value = 100;
        }
        catch (OperationCanceledException) { status.Text = "撤销已取消并回滚。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    async Task RecoverAsync()
    {
        if (busy) return;
        try
        {
            var batch = engine.Pending(); if (batch == null) throw new UserError("没有需要恢复的中断批次。");
            if (MessageBox.Show(this, "恢复中断批次，将尝试安全回滚至操作前状态。路径或内容冲突时会停止并保留备份。\n是否继续？", "恢复中断批次", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SetBusy(true); cancel.Enabled = false; var reporter = OperationReporter(); await Task.Run(() => engine.RecoverAsync(batch, reporter));
            scannedSettings = null; status.Text = "中断批次已恢复。请重新扫描。"; summary.Text = "恢复完成。"; progress.Value = 100;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    void RefreshHistory()
    {
        try
        {
            var history = engine.History(); var pending = history.Any(j => j.Status is "Running" or "RecoveryNeeded" or "Undoing");
            undo.Enabled = !busy && !pending && history.Any(j => j.Status == "Completed"); recover.Enabled = !busy && pending;
            if (pending) status.Text = "发现中断批次，请先点击“恢复中断批次”；暂存文件和备份已保留。";
        }
        catch (Exception ex) { undo.Enabled = false; recover.Enabled = false; ShowError(ex); }
    }
    void ExportPreview()
    {
        if (busy || rows.Count == 0) throw new UserError("请先完成扫描，生成预览清单。");
        grid.EndEdit();
        using var dialog = new SaveFileDialog { Title = "导出清单（请使用新文件名，不覆盖已有文件）", Filter = "文本清单|*.txt", FileName = "改名预览_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt", AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        PreviewExport.WriteNew(dialog.FileName, rows); status.Text = "清单已导出：" + dialog.FileName;
    }
    void ShowRecentBatch()
    {
        var batch = engine.History().FirstOrDefault(); if (batch == null) throw new UserError("尚无批次记录。");
        var state = batch.Status switch { "Completed" => "成功，可撤销", "Undone" => "已撤销", "RolledBack" => "已回滚", "Cancelled" => "已取消并回滚", "PreflightFailed" => "执行前检查未通过", "PresetRemoval" => "预设移除记录", "Undoing" => "撤销中断，需恢复", _ => "中断或恢复未完成" };
        ShowText("最近批次详情", "批次：" + batch.Id + "\r\n状态：" + state + "\r\n日志：" + engine.JournalPath(batch) + "\r\n错误：" + batch.Error + "\r\n\r\n" +
            string.Join("\r\n\r\n", batch.Records.Select(r => r.Source + "\r\n→ " + r.Target + (r.Backup.Length > 0 ? "\r\n旧目标备份：" + r.Backup : ""))) +
            "\r\n\r\n提示：\r\n" + string.Join("\r\n", batch.Warnings) + "\r\n\r\n回收 / 保留位置：\r\n" + string.Join("\r\n", batch.Retirements.Select(r => r.OriginalPath + " → " + r.RetainedPath + " [" + r.State + "]")));
    }
    async Task RunSelfCheckAsync()
    {
        if (busy) return;
        try
        {
            SetBusy(true); tabs.SelectedTab = previewPage; status.Text = "正在自检：只创建临时模拟文件，不访问所选数据目录…";
            var reporter = OperationReporter();
            var result = await Task.Run(() => EnvironmentSelfCheck.RunAsync(new WindowsRecycleBin(), cancellation!.Token, reporter));
            var failed = result.Items.Any(i => i.Status == "失败"); var cancelled = result.Items.Any(i => i.Status == "已取消");
            status.Text = failed ? "自检发现问题，请查看报告。" : cancelled ? "自检已取消，真实数据未受影响。" : "自检完成，请查看报告中的通过项和提示。";
            progress.Value = failed || cancelled ? 0 : 100;
            ShowText("本机自检报告 · 只使用临时模拟文件", result.ToText() + "\r\n\r\n报告已保存：" + result.ReportPath);
        }
        catch (OperationCanceledException) { status.Text = "自检已取消，未操作真实数据。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    // Diagnostic route used only with an explicit --ui-smoke argument. It creates disposable
    // mock files and uses the same UI preview code; it never executes data operations.
    internal async Task RunSmokeAsync(string output)
    {
        Directory.CreateDirectory(output);
        if (tabs.TabPages.Count != 3 || overwrite.Checked || recursive.Checked || source.Text != "" || target.Text != "") throw new Exception("默认安全设置不正确");
        var scratch = Path.Combine(Path.GetTempPath(), "RenameAssistantUiSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            const string pattern = "dimer_2_j_1.000_U_{0}_L_200_ratio_0.150_filling_1010_nsweep_251_mps.h5";
            foreach (var u in new[] { "1.000", "3.500", "4.000", "4.500" }) File.WriteAllBytes(Path.Combine(scratch, string.Format(pattern, u)), new byte[] { 7, 0, 1, 255 });
            source.Text = scratch;
            void Shot(string name) { using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(output, name), System.Drawing.Imaging.ImageFormat.Png); }
            Shot("01-rules.png");
            await ScanAsync();
            if (rows.Count != 4 || rows.Count(r => r.Selected) != 3 || rows.Single(r => r.OriginalName.Contains("U_1.000")).Matched) throw new Exception("界面预览边界或排除示例错误");
            if (rows.Where(r => r.Selected).Any(r => !r.NewName.Contains("nsweep_100"))) throw new Exception("界面生成的新名错误");
            Shot("02-preview.png"); tabs.SelectedIndex = 2; Application.DoEvents(); Shot("03-help.png");
            var help = tabs.TabPages[2].Controls.OfType<RichTextBox>().Single().Text;
            if (!help.Contains("包含边界") || !help.Contains("备份") || !help.Contains("正则")) throw new Exception("内置说明不完整");
            if (Directory.GetFiles(scratch).Length != 4 || Directory.GetFiles(scratch).Any(f => !Path.GetFileName(f).Contains("nsweep_251"))) throw new Exception("扫描意外修改文件");
            File.WriteAllText(Path.Combine(output, "ui-smoke.txt"), "PASS\nWindows EXE 启动并创建三个中文标签页；默认路径为空，子目录和覆盖关闭；界面扫描 4 个模拟文件，U=1.000 排除，3.500/4.000/4.500 选中，新值 nsweep=100；扫描没有改动文件；内置说明可见。\n");
        }
        finally { Directory.Delete(scratch, true); }
    }
    TabPage BuildHelp()
    {
        var page = new TabPage("找文件规则说明");
        var text = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 11), DetectUrls = false, WordWrap = true };
        page.Controls.Add(text);
        void Add(string heading, string content)
        {
            text.SelectionFont = new Font(text.Font, FontStyle.Bold); text.SelectionColor = Color.FromArgb(35, 71, 117); text.AppendText("\n" + heading + "\n");
            text.SelectionFont = text.Font; text.SelectionColor = Color.FromArgb(40, 40, 40); text.AppendText(content + "\n");
        }
        Add("直接照填：筛选 U，再修改 nsweep", "1. 选择源文件夹，文件通配符填写：*.h5\n2. 添加“数值范围（包含边界）”：字段 U，值 / 下限 3.5，范围上限 4.5。\n3. 改名方式选“设置字段值”：字段名 nsweep，新值 100。\n4. 选操作方式，点击“扫描并生成预览”，检查清单后执行。\nU_1.000 的文件不会选中；U_3.500、U_4.000、U_4.500 都会选中。\n例：dimer_2_j_1.000_U_4.000_L_200_ratio_0.150_filling_1010_nsweep_251_mps.h5\n改为：dimer_2_j_1.000_U_4.000_L_200_ratio_0.150_filling_1010_nsweep_100_mps.h5");
        Add("源文件夹与文件通配符", "源文件夹是开始找文件的位置。文件通配符先决定找哪一类文件：*.h5 找扩展名为 h5 的文件，*.txt 找文本文件，* 或 *.* 找所有文件。* 表示任意长度文本，? 表示一个字符。通配符填写文件名模式，不填写路径。多个类型用分号分隔，例如 *.h5;*.txt;*.csv；这些类型之间满足任意一个即可。");
        Add("是否扫描子文件夹", "默认只找源文件夹中的文件。勾选“包含子文件夹”后也会向下查找。移动或复制保留相对目录，例如 源\\实验一\\a.h5 对应 目标\\实验一\\新名.h5。备份及保留文件夹、软件临时文件、符号链接和目录联接会跳过；源目录下单独的目标目录也跳过。");
        Add("包含、排除和通配符有什么区别", "包含：名字中有指定文本即可；排除（文件名不包含）：名字中有该文本就不选；通配符：按整段文件名的模式选。\n可照填的例子：添加“文件名包含”，值填 dimer_2；再添加“文件名不包含”，值填 backup。这样选择含 dimer_2 且不含 backup 的文件。\n通配符条件示例：*L_200*nsweep_251*.h5。筛选条件作用于完整文件名，包括扩展名。");
        Add("字段和值怎样识别", "把名称中的“字段_值”“字段-值”“字段=值”当作一组，例如 U_4.000、U-4.000、U=4.000。支持 dimer、j、U、L、ratio、filling、nsweep 等字段，也支持其他由字母或汉字开头的字段名。字段值在下一个分隔符处结束；数字可以有小数、正负号或科学记数法。\n同一字段出现多次时不猜测使用哪个，会提示无法确定。对复杂名称请用正则筛选或替换。字段识别在扩展名前进行。");
        Add("数值比较与多个条件", "数值按数字大小比较，3.5 和 3.500 相等；范围 [3.5, 4.5] 包含上下边界。字段等于 / 不等于默认在两边都是数字时按数字比较；也可强制数值或按文本比较，文本模式能区分 001 和 1。默认忽略英文字母大小写，可勾选区分大小写。\n多个普通条件和可选正则筛选都采用“并且”：每条都满足才选中。缺少指定字段时无法判断该条件，因此不会选中；“字段不等于”也不会把缺少字段的文件选中。没有普通条件且不开正则时，通配符下的全部文件会匹配。");
        Add("什么时候使用正则表达式", @"普通条件表达不了复杂格式时使用正则。正则筛选针对完整文件名，改名正则针对扩展名前的名称。^ 表示开头，$ 表示结尾，.* 表示任意文本，\. 表示真正的点。" + "\n" + @"可照填的筛选正则：^dimer_2_.*_nsweep_251_.*\.h5$" + "\n这会筛选以 dimer_2_ 开头、含 _nsweep_251_ 且以 .h5 结尾的文件。正则有错误或单次执行超过 200 毫秒会停止，不能执行。正则替换可用 $1、$2 引用分组。");
        Add("改名与预览", "每批只使用一条改名规则：设置字段值、文本替换、正则替换、添加前后缀、模板编号、大小写转换或去除首尾空格。只改变指定部分；扩展名保持不变。扫描预览不会改动文件。表格会显示完整原路径、新名称、目标路径和原因；只执行已勾选且合法的行。无变化、非法名称、批内重名等行不能执行。修改路径或规则后必须重新预览。");
        Add("原地改名、移动和复制", "原地改名：文件仍在原文件夹中，仅名称变化。移动原文件：成功后原位置不保留文件，文件在目标目录。复制文件：源文件保留，目标目录有改名后的完整副本。复制和移动先写入临时文件，核对长度及 SHA-256 后再提交；移动会保留源暂存，直到整批最终目标校验成功，再送入回收站。哈希只是核对原始字节一致，不理解或改写文件内容。");
        Add("覆盖已有文件与备份位置", "默认不覆盖。两个本批文件生成同一目标时，无论是否开启覆盖都禁止执行。主动勾选允许覆盖后，执行前会弹窗列出覆盖数量和完整清单，再次确认。旧目标先移动到其所在目录下的“.批量改名助手_备份_批次号”文件夹。备份不会自动删除。请检查清单，尤其是移动或复制到有旧数据的目录时。");
        Add("取消、日志和撤销", "点击取消后等待安全回滚完成，不要强制结束程序。复制中断的残缺文件使用软件临时名称，不会冒充最终文件。操作日志保存在本机用户数据目录，可从“查看日志文件夹”打开。撤销上一批会先检查内容和路径；文件已修改、缺失或原位置被占用时停止。中断或断电后，下次启动可点击“恢复中断批次”；冲突时保留完整文件、暂存文件及备份，并说明原因。");
        Add("规则预设与路径限制", "预设可命名保存、加载、删除及导入导出 JSON。只保存文件通配符、子文件夹、筛选条件、大小写及正则设置和改名规则，不保存源目录、目标目录或覆盖状态；加载预设会关闭覆盖。为兼容普通 Windows 配置，本版本要求完整路径少于 260 个字符，单个名称不超过 255 个字符。遇到文件被占用、只读或权限不足时停止并说明原因。");
        Add("命名规则按钮与通用命名", "在改名区域或预览页点击“命名规则与试算”，也可按 F1。能查看当前生效规则，并输入名字试算，不读写文件。\n模板例：实验_{field:U}_{index:0000}_{name}。{name} 是原名称，{parent} 是父目录名，{index} 是序号，{field:U} 是 U 的原始值。{modified:yyyyMMdd} 和 {created:yyyyMMdd} 使用本机文件时间。扩展名会自动加回。编号按匹配文件相对路径排序；取消勾选不重编号。\n.tar.gz、.nii.gz 等复合扩展名默认保护，可自行设置；.env 这类单个前导点名称被视作无扩展名。普通文本替换支持空替换（删除指定文本）和只替换第一处。");
        Add("更多查找格式", @"可按开头、结尾、字段是否存在或值包含来找文件。“字段缺失”是明确查找缺少字段的独立条件；其他字段条件缺少字段时仍不选中。" + "\n" + @"常规字段允许中文、数字、下划线、点和短横线。复杂格式可填写自定义字段提取正则，例如 U[4.000] 使用 (?<![\p{L}\p{N}]){field}\[(?<value>[^\]]+)\]。{field} 会自动替换为当前字段名，value 标记真正要比较或修改的值。未捕获值、多次捕获或字段重复都会拒绝猜测。请先用试算验证自己的格式。");
        Add("特殊 Windows 文件", "移动与复制只处理普通单数据流文件；遇到 EFS 加密或额外的 NTFS 数据流会停止，避免静默遗漏内容，仍可原地改名。复制保留修改时间及 Windows 创建时间；访问权限等元数据按目标目录继承。系统数据流检查与回收站接入尚需原生 Windows 实机验证，详情见随附测试报告。\n");
        Add("完整性校验与回收站", "扫描阶段会只读计算匹配文件的 SHA-256，执行前再次比较。复制会核对长度、复制流、临时副本和最终目标；整批完成后再做一次校验。原地改名不写入文件内容。\n移动成功后清理源暂存、撤销复制及清理取消后的临时副本，都只请求送入系统回收站。程序会阻止系统转为永久删除。回收站不可用（例如某些网络盘）时，文件留在对应目录的“.批量改名助手_保留_批次标记”下，并显示路径，日志记录原始位置与保留位置。回收站内可能显示暂存名称，可按日志核对。异常内容会隔离保留，不自动回收。旧目标备份仍不会自动删除。\n回收站中的移动源副本会额外占用空间，撤销操作不依赖回收站。软件不会自动清空回收站或备份。关闭前请等待完成；磁盘故障、断电或其他程序并发改文件仍可能导致操作停止，日志会保留恢复线索。不要同时用其他程序改动同一批文件。");
        Add("环境与安全自检、清单和最近批次", "顶部“环境与安全自检”会在本机临时目录新建少量模拟文件，实际检查读写、规则、改名 / 复制 / 移动及撤销、SHA-256 和回收站接入。无需安装开发工具，不读取所选源或目标目录；可取消。结束会显示并保存报告及模拟日志，保留模拟目录供核对。临时目录自检通过不代表所有磁盘、权限和网络盘都通过。\n“导出预览清单 TXT”能把当前所有行的路径、名称、状态、勾选情况与源哈希保存为新文本；不会覆盖已有文件。“最近批次详情”集中显示结果、错误、旧目标备份、回收及保留路径。路径接近系统长度上限时，清理文件可能改用同目录的短暂存名 .__bra_k_...bin，准确位置以日志为准。");
        text.SelectionStart = 0; text.ScrollToCaret(); return page;
    }
}
