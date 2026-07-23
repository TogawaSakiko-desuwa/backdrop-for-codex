# 贡献指南

感谢你改进 Backdrop for Codex。提交代码即表示你愿意遵守[行为准则](CODE_OF_CONDUCT.md)，并以 [DCO 1.1](DCO.md) 对每个提交作出来源声明。

## 开始之前

- 一般缺陷和功能建议使用对应 Issue 表单；先搜索重复项。
- 安全或隐私漏洞必须按 [SECURITY.md](SECURITY.md) 私下报告，不要公开概念验证、日志或截图。
- 大型架构改动、依赖新增、遥测/联网、CDP 暴露面或兼容范围变化，应先在 Issue 中形成维护者认可的设计方向。
- 本项目只支持 Windows 11 x64 与官方 Microsoft Store/MSIX Codex。扩大平台或客户端范围不是普通兼容修复。

## 开发环境

需要 Windows 11 x64、Git 和 .NET 10 SDK。在实际 Codex 上进行手工兼容性验证时，还需要已安装的官方 Store/MSIX Codex；验证过程不得读取或公开真实账号的聊天数据。

```powershell
git clone <your-fork-url>
cd backdrop-for-codex
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet publish .\src\BackdropForCodex.App\BackdropForCodex.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output .\artifacts\local-publish -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true
dotnet format .\BackdropForCodex.slnx --verify-no-changes --no-restore
```

不要把真实聊天、令牌、用户绝对路径或受版权限制的媒体加入仓库、构建材料或 Issue/PR 附件。需要展示路径场景时使用临时目录和虚构名称。

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

## 实现约束

- 保持 nullable、分析器和警告策略通过；公开 API 和并发状态应有清晰的不变量。
- 异步 I/O 支持取消与超时，重连使用有上限退避，清理操作保持幂等。
- 任何监听器必须显式绑定 `127.0.0.1`；发现到的 CDP URL 必须解析并拒绝非回环地址。
- 不把媒体路径、文件名或设置值拼接成 JavaScript、HTML、CSS、命令行或任意 URL。
- 不读取聊天，不修改/重签 Codex 包，不要求管理员权限，不静默添加自动启动。
- 不加入遥测、崩溃上传、更新检查或项目自有远程服务，除非治理文档、隐私说明、威胁模型和明确用户同意机制已先行评审。
- 日志不得包含聊天、媒体服务令牌或媒体绝对路径；异常对象和 DTO 同样需要脱敏。
- 新增 NuGet 依赖前说明必要性、许可证、维护状态和攻击面；版本在 `Directory.Packages.props` 集中管理，并更新 `THIRD_PARTY_NOTICES.md`。

安全边界变更必须同步更新 [THREAT_MODEL.md](THREAT_MODEL.md)、实现中的失败关闭约束和 PR 验证说明。文档本身不是安全控制。

## 验证与安全审查

每个行为变更都应给出可由公开源码复核的验证说明，列明实际执行的构建/发布命令、适用的手工场景和未验证项。涉及本地服务或 CDP 时，设计与评审至少考虑：

- IPv4 回环与非回环地址；
- 取消、超时、断线、导航、重连和硬退出；
- 错误包、进程、窗口和端点；
- 令牌轮换、方法限制、Range 边界和任意路径不可达；
- 损坏/超大设置、路径脱敏和资源上限；
- 重复清理、并发切换和媒体文件消失。

手工验证只使用专用账号或虚构聊天，不在 Issue/PR 上传含真实数据的页面截图。说明 Windows build、Codex 来源/版本和已验证场景。Pull Request 还会接受 Windows Release 构建、单文件发布形态检查与 CodeQL 分析。

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
