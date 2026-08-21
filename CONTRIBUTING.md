# 贡献指南

感谢你改进 Backdrop for Codex。提交代码即表示你愿意遵守[行为准则](CODE_OF_CONDUCT.md)，并以 [DCO 1.1](DCO.md) 对每个提交作出来源声明。

## 开始之前

- 一般缺陷和功能建议使用对应 Issue 表单；先搜索重复项。
- 安全或隐私漏洞必须按 [SECURITY.md](SECURITY.md) 私下报告，不要公开概念验证、日志或截图。
- 大型架构改动、依赖新增、遥测/联网、CDP 暴露面或兼容范围变化，应先在 Issue 中形成维护者认可的设计方向。
- 本项目只支持 Windows 11 x64 与官方 Microsoft Store/MSIX Codex。扩大平台或客户端范围不是普通兼容修复。

## 开发环境

需要 Windows 11 x64、Git 和 .NET SDK `10.0.301` 或同一 feature band 的更新补丁。`global.json` 以 `10.0.301` 为下限并使用 `latestPatch`；CI 必须同时保持 `10.0.301`、`10.0.302` 通过，正式发布固定使用 `10.0.302`。Release job 还安装 .NET 8 SDK/runtime 来托管锁定的 `Microsoft.Sbom.DotNetTool`；这不会改变应用的 SDK 锁定版本。在实际 Codex 上进行手工兼容性验证时，还需要已安装的官方 Store/MSIX x64 Codex；验证过程不得读取或公开真实账号的聊天数据。

```powershell
git clone <your-fork-url>
cd backdrop-for-codex
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet publish .\src\BackdropForCodex.App\BackdropForCodex.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output .\artifacts\local-publish -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true
dotnet format .\BackdropForCodex.slnx --verify-no-changes --no-restore
```

不要把真实聊天、令牌、用户绝对路径或受版权限制的媒体加入仓库、构建材料或 Issue/PR 附件。需要展示路径场景时使用临时目录和虚构名称。

## 代码结构

- `src/BackdropForCodex.App`：WPF 工作台、来源库、预览、设置、通知区域和用户可见错误映射。
- `src/BackdropForCodex.Core/Media`：本地媒体与 Wallpaper Engine 来源发现、项目解析、窗口渲染和活动媒体 lease。
- `src/BackdropForCodex.Core/Dynamic`：Scene / Web 的窗口捕获、帧调度、Media Foundation 编码、画质档位和页面流传输。
- `src/BackdropForCodex.Core/Runtime`：应用事务、latest-wins 调度、活动 lease、运行状态和恢复/清理边界。
- `src/BackdropForCodex.Core/Injection`：CDP 会话以及安装、样式、媒体和生命周期脚本模块。
- `src/BackdropForCodex.Core/Settings`：schema 3 设置、V1/V2 迁移、原子存储和工作区快照。
- `tests/BackdropForCodex.Core.Tests`：按 AppSupport、Dynamic、Injection、Media、Runtime 和 Settings 划分的自动化测试。

## 分支、提交与 DCO

从最新默认分支创建范围单一的分支。提交应保持可审查、说明动机，并由实际作者签署：

```powershell
git commit -s -m "fix: reject non-loopback CDP endpoints"
```

这会加入：

```text
Signed-off-by: Your Name <you@example.com>
```

签署名称和邮箱必须是你愿意永久出现在公开 Git 历史中的身份。修正最近一次遗漏可使用 `git commit --amend -s`；批量改写公开历史前先与维护者沟通。合并他人提交时不得替对方伪造签署。

## 实现与安全约束

- 保持 Windows 11 x64 与官方 Microsoft Store/MSIX Codex 的支持边界；新增平台或客户端范围应先讨论。
- 不读取聊天，不修改或重新签名 Codex 包，不要求管理员权限，不绕过 Codex 内容安全策略。
- CDP 只接受经过包、进程、会话和监听器核验的严格 IPv4 `127.0.0.1` 端点；安全验证必须先于页面兼容判断。
- 本地媒体必须验证最终路径、普通本地文件、格式和大小，并在使用期间保持只读句柄。不得把路径或文件名拼接进脚本、HTML、CSS、命令行或 URL。
- Wallpaper Engine 来源只读取已安装 Workshop 与 `projects/myprojects`、`projects/backup`。Application / Unknown 不执行；Scene / Web 只处理经过验证的项目窗口像素，不捕获桌面或转发脚本、输入、音频和 Codex 内容，也不枚举、静音或改变 Wallpaper Engine 音频会话。
- 异步 I/O 应支持取消和有界超时；并发应用、更换和清理不得让旧请求覆盖或释放较新的背景资源。
- 设置写入保持严格校验和原子替换；旧 schema 迁移前保留原始只读备份，损坏或未来版本设置不得被默认值静默覆盖。
- 日志不得包含聊天或媒体绝对路径。诊断导出只能由用户主动触发，使用固定字段白名单且不自动上传。
- 不加入遥测、崩溃上传、更新检查或项目自有远程服务，除非相应的产品、安全和隐私设计已经公开讨论。
- 新增 NuGet 依赖前说明必要性、许可证、维护状态和攻击面；版本集中维护在 `Directory.Packages.props`，并更新 `THIRD_PARTY_NOTICES.md`。

安全边界变化必须同步更新 [THREAT_MODEL.md](THREAT_MODEL.md)、[PRIVACY.md](PRIVACY.md) 和用户文档。

## 验证

提交 PR 前，请按改动范围运行适用的格式、构建、测试和发布命令。至少应执行：

```powershell
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet format .\BackdropForCodex.slnx --verify-no-changes --no-restore
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet test .\BackdropForCodex.slnx `
  --configuration Release `
  --filter "Category!=Integration&Category!=BrowserContract"
```

涉及真实 Codex、Edge/CDP、Wallpaper Engine、WGC/MF/MSE、托盘或可访问性的环境测试，应在专用账号或虚构内容下进行。PR 中只列出实际运行的命令与场景；没有运行的项目明确写为“未验证”。

## Pull Request

PR 应：

- 解释问题、方案、用户可见变化和明确不做的内容；
- 关联 Issue，并标出安全、隐私、兼容或迁移影响；
- 列出实际运行的命令和手工场景，不把“应该通过”写成已验证；
- 更新 README、变更日志、威胁模型、隐私或第三方声明（如适用）；
- 不包含生成目录、个人设置、真实媒体、日志秘密或无关格式化；
- 确保每个 commit 具有有效 DCO `Signed-off-by`。

维护者可能要求拆分范围、补充验证证据、重写提交或拒绝不符合项目方向的变更。提交 PR 不保证合并。

## 许可证

除非明确另行说明，你有意提交并被项目接收的贡献依据 [Apache License 2.0](LICENSE) 提供。DCO 是来源证明，不是额外许可证或版权转让协议。
