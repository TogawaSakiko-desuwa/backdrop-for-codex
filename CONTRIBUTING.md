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
- 媒体路径保持 lease-only，不得重新加入 Kestrel、临时 HTTP 监听器、媒体 endpoint/token 或其他网络传输；发现到的 CDP URL 必须解析并只接受严格 IPv4 `127.0.0.1`。
- 本地媒体必须通过已打开句柄解析最终路径，验证本地普通文件、文件身份、扩展名、文件头/容器签名和大小，并让校验与使用共享同一个只读 lease。当前上限为图片 512 MiB、单边 32,768 像素、总计 33,554,432 像素，视频 8 GiB；界面图片预览必须只使用这条校验链返回的元数据，并同时限制解码宽高。
- 不把媒体路径、文件名或设置值拼接成 JavaScript、HTML、CSS、命令行或任意 URL。
- Codex 版本号只可用于已验证包身份的自洽检查和脱敏诊断，不得作为安全准入、结构契约候选、排序、决胜或表现能力的输入。安全身份/进程/会话/监听器/端点/browser/socket/target/唯一页面验证必须先行且失败关闭；安全失败或持续多目标歧义时，结构证据探针必须保持零调用。
- 内置表现契约保持版本无关和只读：`global-baseline-v1` 独立声明 Global 所需的最小结构，`codex-shell-v1` 按审核过的布尔证据声明 Glass/Advanced。高级契约零匹配或多重匹配只能使用 Global baseline，不得以版本、注册顺序或隐藏优先级决胜；baseline 失败不得注入。
- 五项能力保持独立：全局背景、功能区域识别、玻璃样式、音频和高级内容表面。1.3.3 不得误报区域识别或音频已实现；活动契约在一次 generation 内锁定，能力只允许降级且证据恢复不得重新启用。
- 初始结构就绪轮询窗口最多 10 秒且只接受一个合格工作页；持续多目标歧义必须拒绝并清理，不能任意挑选或同时注入。窗口结束后的单次 Global fallback 复验与安装使用独立的 10 秒 operation deadline，不得退回无界 caller token。
- schema 2 更改必须保持严格未知字段/引用/范围校验、同目录原子保存、V1 原始字节只读备份、恢复状态和未来 schema 只读语义；不得用默认设置静默覆盖异常文档。废弃的 `LastCompatibilityProfileId` 只为旧设置原样透传，运行时、V1 编辑门面和脏状态比较都不得让它重新参与控制。
- 不读取聊天，不修改/重签 Codex 包，不要求管理员权限，不静默添加自动启动。
- 不加入遥测、崩溃上传、更新检查或项目自有远程服务，除非治理文档、隐私说明、威胁模型和明确用户同意机制已先行评审。
- 日志不得包含聊天或媒体绝对路径；异常对象和 DTO 同样需要脱敏。诊断导出必须由用户主动触发并使用 `schemaVersion: 2` 的 Environment、Runtime、Compatibility 固定白名单，且不自动上传；只允许脱敏版本、类型化安全结果、活动契约、匹配状态和逐项能力原因，禁止加入路径、文件名、包完整名、Publisher、进程/会话/端口、页面标题/URL/DOM/选择器、聊天、设置、标识符、散列、CDP Detail 或原始异常文本。
- 手工基准工具只报告本机 lease/单槽测量，不连接 Codex、不输出输入路径，也不自动执行发布阈值判定。
- 新增或扩大的上游敏感能力必须先发 Preview 并通过真实 Codex 验证后再进入 Stable。1.3.3 仅因重构既有能力而直接进入 Stable，不得把这次例外推广为常规发布策略。
- 新增 NuGet 依赖前说明必要性、许可证、维护状态和攻击面；版本在 `Directory.Packages.props` 集中管理，并更新 `THIRD_PARTY_NOTICES.md`。

安全边界变更必须同步更新 [THREAT_MODEL.md](THREAT_MODEL.md)、实现中的失败关闭约束和 PR 验证说明。文档本身不是安全控制。

## 验证与安全审查

每个行为变更都应给出可由公开源码复核的验证说明，列明实际执行的构建/发布命令、适用的手工场景和未验证项。涉及媒体、设置、诊断或 CDP 时，设计与评审至少考虑：

- 严格 IPv4 回环与非回环地址，以及是否意外引入媒体监听器；
- 取消、超时、断线、导航、重连和硬退出；
- 错误包、进程、会话、监听器、窗口和端点，以及安全拒绝不能被结构探针覆盖；
- 相同结构证据在任意官方版本下得到相同结果、baseline 失败、唯一/零/多高级契约匹配、Global-only 回退、活动契约锁定、五项能力独立降级及同 generation 不重新启用；
- 无目标、唯一目标、持续多目标和 10 秒截止；
- reparse/symbolic-link 最终路径、文件身份、网络/设备路径拒绝、格式签名、媒体大小上限与图片尺寸/像素预算；
- V1 原始备份、重复迁移、备份冲突、损坏/超大设置、严格未知字段、未来 schema 只读、保存前原文复核、未编辑 V2 档案保留和原子写入；
- 诊断 schema 2 的明确用户操作、精确字段白名单、目标文件覆盖，以及路径、目标元数据、DOM/选择器和异常内容脱敏；
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
