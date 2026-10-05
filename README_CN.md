# 批量文件改名助手 1.2

C# / .NET 8 WinForms，Windows 10/11 x64 自包含单文件便携程序。最终用户直接运行 EXE，无需开发环境。软件离线工作，不解析用户文件格式；只按原始字节进行完整性校验。

## 项目

- `Renamer.App`：中文界面、独立说明页、命名规则及试算按钮、预设和 Windows 严格回收站适配器。
- `Renamer.Core`：字段 / 正则 / 模板规则、精确数值比较、只读预览指纹、安全批次、日志、撤销与恢复。
- `Renamer.Tests`：仅使用新建模拟文件；`Program.cs` 是原有回归，`UpgradeTests.cs` 是安全和通用规则回归。
- `测试原始结果.json`、`使用说明.txt`、`测试报告.txt`：本次实际交付记录。

## 构建和维护

用户不需要执行以下命令。维护者在 .NET 8 SDK 环境中可运行 `构建.cmd`，或：

```text
dotnet publish Renamer.App/Renamer.App.csproj -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -o dist
dotnet run --project Renamer.Tests/Renamer.Tests.csproj -c Release -- test-results.json
```

没有第三方业务 NuGet 包。SDK 恢复 Microsoft 的 WindowsDesktop 和运行时打包依赖；EXE 使用 SingleFile、自包含运行时、原生库自解压和压缩。不可裁剪 WinForms。构建脚本不触碰用户数据。

Windows 原生 UI 诊断入口 `--ui-smoke 输出目录` 仅生成模拟文件、预览和三页截图；当前交付未运行成功，不算通过项。

## 本次安全修订

1. 扫描计算源及已有目标 SHA-256，执行前再次比较，防止大小 / 时间没变而内容改变。
2. 执行与撤销持有只读共享保护句柄，原地改名不打开任何用户数据写流。
3. 副本采用 CreateNew 临时文件、落盘刷新、长度和源流哈希校验、重读副本校验，提交后与整批提交前再次核验。
4. 移动整批通过才清理源暂存；撤销移动保留目标暂存直到恢复整批通过。
5. 旧目标始终先移入相邻批次备份；批内重名从不覆盖。
6. 数据清理不调用 File.Delete。Retire 先记录意图，再移动到相邻保留目录，然后调用 IRecycleBin；失败保留路径并警告。
7. WindowsRecycleBin 在 STA 上调用 IFileOperation，要求 FOFX_RECYCLEONDELETE、EARLYFAILURE。PreDeleteItem 缺少回收标志时返回 E_ABORT，禁止永久删除降级。未收到完成确认时不伪报成功。
8. 覆盖备份和保留目录不自动删除。日志版本 2，仍可读取版本 1 成功批次；中断恢复有路径 / 哈希冲突时停止。
9. Windows 复制 / 移动拒绝 EFS 和 NTFS 附加流，避免普通字节流复制静默丢失额外内容。原地改名不受此限制。
10. 生产数据流程没有 File.Delete / Directory.Delete。权限探测仅 CreateNew 一个空的自有 DeleteOnClose 文件；UI smoke 和测试夹具仅删除自己新建的模拟临时目录。日志 / 预设 JSON 原子更新只用于软件元数据。

## 规则扩展

十进制数字字符串规范化比较避免二进制舍入及 decimal 下溢；科学计数指数绝对值最多 100000，数字文本最多 4096 字符。字段等于可切换文本模式。通配符分号组合、Unicode 字段、自定义 value 捕获、前后缀、首次替换、缺失追加、复合扩展名、模板序号 / 字段 / 时间均有模拟回归。

正则每次执行 200 ms 限时。普通条件全部 AND，单条多通配符内部 OR。模板编号按匹配文件相对路径稳定排序；取消勾选不会重排。

## 实际测试边界

85 项 PASS、0 FAIL、2 SKIP，均为 Linux 上的真实模拟文件操作或托管逻辑测试。NTFS 附加流和原生 Windows 回收站测试在该平台明确 SKIP。模拟回收站把模拟文件移动进测试夹具目录，不代表真实系统回收站。

Windows 原生 GUI / COM、ACL / 共享锁、DPI、实际 NTFS 和跨磁盘仍未实机验证。Linux 文件共享和权限测试不能代替 Windows 对应行为。没有断电、真实坏盘或磁盘耗尽实测；中断恢复测试是构造持久化中断状态。不要把成功交叉编译等同于完整 Windows 验收。

## 参考的 Microsoft 接口文档

- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_transfer_source_flags
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirststreamw
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-win32_find_stream_data

未创建仓库、提交到外部服务或使用 GitHub Actions。所有源文件均在此包内。

## 1.2 复查补充

- EnvironmentSelfCheck：新建唯一临时目录，用真实核心引擎演练 Copy / Rename / Move 和 Undo；宿主传入 WindowsRecycleBin 时实际尝试系统回收站。报告说明作用域，不把临时目录成功扩展为所有磁盘成功。
- PreviewExport 使用 FileMode.CreateNew，拒绝覆盖任意已有文件。
- Program 捕获启动异常，尽力写诊断日志并给出中文错误；不能捕获应用进入 Main 之前的系统加载失败。
- ValidateJournal 校验日志 ID、状态、空结构、哈希和临时目录关系，损坏时停止执行 / 恢复。
- 旧版跨目录恢复不再借用 File.Move 的跨设备隐式复制删除行为，恢复阶段也使用哈希校验和回收站。
- Retire 长路径回退到相邻短名称，完整对应关系留在批次日志。
- 生产引擎拒绝处理自己的日志目录，WinForms 宿主进一步保护整个应用数据目录。
- FinalAuditTests.cs 包含这轮新增的 14 项实际模拟回归；最终 85 PASS / 0 FAIL / 2 SKIP。

原生 Windows GUI、启动、系统回收站和 NTFS 专项依然没有在交付环境完成，不能写成已通过。应用内自检能供实际运行电脑验证，不代替交付前 Windows 验收。
