# Backdrop for Codex

一个面向 **Windows 11 x64** 的非官方开源伴侣：让官方 Microsoft Store / MSIX Codex 桌面应用使用本地图片或静音循环视频作为工作区背景。

**不修改 Codex 安装文件 · 不向项目自有服务上传本地媒体 · 不读取聊天 · 不收集遥测**

[![Latest release](https://img.shields.io/github/v/release/TogawaSakiko-desuwa/backdrop-for-codex?display_name=tag)](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest)
[![CI](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/actions/workflows/ci.yml/badge.svg)](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

[下载最新版](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest) · [快速开始](#快速开始) · [安全说明](SECURITY.md) · [隐私说明](PRIVACY.md) · [Complete English README](README.en.md)

> [!CAUTION]
> Backdrop for Codex 是独立社区项目，与 OpenAI 或 Microsoft 无隶属、赞助、认可或支持关系。
> 它通过本机回环地址上的 Chrome DevTools Protocol（CDP）工作；CDP 是高权限调试接口，同一 Windows 用户会话中的恶意进程仍可能尝试连接。
> 请勿以管理员身份运行或转发调试端口，使用完毕后应完全退出 Codex。详情见[安全说明](SECURITY.md)和[威胁模型](THREAT_MODEL.md)。

![Backdrop for Codex 1.4.0 多方案工作台（脱敏示例）](docs/images/backdrop-1.4.0-workspace.png)

## 快速开始

1. 在 Windows 11 x64 上安装官方 Microsoft Store / MSIX x64 Codex。
2. 从 [GitHub Releases](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest) 下载 `BackdropForCodex-vX.Y.Z-win-x64.zip`。
   建议同时下载 `BackdropForCodex-vX.Y.Z-SHA256SUMS.txt` 和 SPDX SBOM。
3. 验证发布物后，将 ZIP 解压到普通用户可写的空目录。
4. 完全退出所有 Codex 进程，启动 `BackdropForCodex.exe`，阅读并确认 CDP 风险提示。
5. 新建或选择一个方案，拖入或选择本地图片/视频，调整预览参数并应用。也可以清除媒体并应用空方案，让 Codex 使用官方背景。
6. 首次媒体激活成功后会尝试创建或更新 `Codex（动态背景）.lnk`，以后可用它执行增强启动；快捷方式创建失败不会影响当前背景，可在工作台重试。

> [!NOTE]
> 当前发布物可能没有 Authenticode 代码签名。SHA-256 用于检查下载字节是否一致，GitHub artifact attestation 用于验证构建来源；它们不能替代代码审查、Windows 代码签名或端点防护。

<details>
<summary><strong>验证 SHA-256 与 GitHub 构建来源</strong></summary>

在下载目录打开 PowerShell，并把 `vX.Y.Z` 替换为实际版本：

```powershell
Get-FileHash .\BackdropForCodex-vX.Y.Z-win-x64.zip -Algorithm SHA256
Get-Content .\BackdropForCodex-vX.Y.Z-SHA256SUMS.txt
```

确认 ZIP 的散列与清单完全一致。安装 [GitHub CLI](https://cli.github.com/) 后还可以验证构建来源：

```powershell
gh attestation verify .\BackdropForCodex-vX.Y.Z-win-x64.zip `
  --repo TogawaSakiko-desuwa/backdrop-for-codex
```

同一 Release 中的 `BackdropForCodex-vX.Y.Z-win-x64.spdx.json` 是机器可读的软件物料清单。

</details>

### 使用提示

- 方案栏管理多个背景方案。新建、复制、重命名、删除、切换方案，以及选择媒体或调整参数，都只会修改草稿；底部仍只有一个“应用”操作会提交并尝试改变 Codex。
- 工作台分别显示草稿、已保存目标和实际活动快照。保存成功但激活失败时，界面会明确显示“已保存但未激活”，不会把目标值伪装成正在运行的背景。
- 应用采用 latest-wins：快速重复点击时，只保留最新待处理请求，旧请求会安全取消或标记为已替代；已经完成的原子保存不会被假装回滚。
- 点击关闭按钮或按 `Alt+F4` 会把工作台隐藏到通知区域，已应用背景会继续运行。
- “恢复官方背景”会取消未完成的应用并只清理本应用拥有的媒体资源，不修改已保存目标；再次点击“应用”即可恢复所选方案。
- 空方案是可保存、可选择的正式方案。手动应用空方案不会为了显示官方背景而启动已关闭的 Codex；增强快捷方式则会验证官方包并以普通参数启动 Codex，不附加调试参数。
- 只有从通知区域选择“退出”才会结束伴侣并清理应用持有的资源。真正退出、完整重置或恢复 V1 备份时，如有脏草稿会要求选择“丢弃并继续”或“取消”，不会隐式应用。
- 退出伴侣不会关闭由 Codex 持有的 CDP 端口；只有完全退出 Codex 才会关闭该端口。

## 核心功能

- 支持本地 PNG、JPEG、WebP 图片，以及静音循环播放的 MP4、WebM 视频。
- 支持多个背景方案，以及方案的新建、复制、重命名、删除和键盘选择；删除被引用方案时会把相关区域原子重绑到用户确认的替代方案。
- 支持不含媒体的 Official 空方案；空方案的样式参数不会制造“已激活媒体”的假状态。
- 提供“完整显示”“裁剪填满”“拉伸”三种适配模式；裁剪填满支持拖动焦点、方向键微调和恢复居中。
- 可分别调整浅色/深色主题遮罩、面板不透明度与背景模糊，并在应用前预览效果。
- 支持文件选择、单文件拖放和最多 8 条最近使用记录；失效路径会被明确标记并可单独移除。
- 支持跟随系统、浅色和深色主题，并优先遵循 Windows 高对比度等可访问性设置。
- 方案栏支持水平滚动、上下文菜单、焦点恢复、UI Automation 选中状态、减少动态效果及 125%–200% 缩放；窗口继续在 960 px 处切换双栏/上下布局，最小尺寸为 640×520。
- 可在工作台暂停或继续视频，并从通知区域重新打开窗口、恢复官方背景或退出。
- 作为外部伴侣运行，不修改、替换或重新签名 Codex 的 MSIX 包。

## 兼容与限制

| 项目 | 当前状态 |
| --- | --- |
| Windows 11 x64 | 支持，也是唯一目标平台 |
| 官方 Microsoft Store / MSIX x64 Codex | 支持；必须先通过包、进程、会话、回环端点和唯一页面核验 |
| 多个合格 Codex 工作页 | 最多等待约 10 秒收敛为唯一目标；持续多目标时拒绝本次应用 |
| PNG、JPEG、WebP | 支持 |
| MP4、WebM | 支持静音循环播放 |
| 本地媒体限制 | 仅本地普通磁盘文件；图片不超过 512 MiB、单边 32,768 像素和约 33.5 MP，视频不超过 8 GiB |
| 设置格式 | 继续使用 schema 2；1.4.0 没有新增序列化字段，1.3.5 可读取并保留多方案数据 |
| Win32 便携版、网页、CLI 或其他 Codex 客户端 | 不支持 |
| Windows 10、Windows on Arm、macOS、Linux | 不支持 |
| 不同页面区域使用不同壁纸、视频声音、Wallpaper Engine、多个 Codex 窗口使用独立壁纸 | 当前不支持 |

兼容能力按实际页面结构判断，Codex 版本号本身不决定表现能力。Codex 更新可能改变页面结构、进程模型或调试行为，从而暂时影响部分或全部背景效果；安全核验失败时不会注入。

## 安全与隐私

- 只接受经过严格核验的官方 Store/MSIX Codex，以及严格 IPv4 回环 CDP 端点；回环地址仍不是针对同一用户进程的安全边界。
- 不修改或绕过 Codex 的内容安全策略（CSP）。本地媒体经校验后通过页面内受控文件输入绑定，并使用 `blob:` URL 加载。
- 不启动媒体 HTTP 服务，不创建媒体 endpoint 或访问令牌；媒体不会经过项目自有网络服务。
- 不读取聊天，不代理 Codex 与 OpenAI 的通信，不发送遥测、行为分析或项目自有崩溃报告。
- 设置保存在当前 Windows 用户的 `%LOCALAPPDATA%\CodexWallpaper`，其中可能包含多个方案、所选媒体的绝对路径、区域绑定和最近使用记录。
- 诊断报告只在用户明确导出时生成，使用固定字段白名单，不会自动上传。
- 每次活动媒体由单槽所有权 token 绑定；过期请求只能释放自己持有的 lease 和注入 generation，不能清理较新的背景。更换、恢复或退出时会清理本项目拥有的页面资源和媒体 lease；Codex 持有的 CDP 端口仍需通过完全退出 Codex 来关闭。

完整的数据流、失败关闭条件和剩余风险见：

- [安全策略](SECURITY.md)
- [威胁模型](THREAT_MODEL.md)
- [隐私说明](PRIVACY.md)

## 工作原理

1. 工作区把 `Draft`、`SavedDesired` 和 `ActiveSnapshot` 作为三个独立、不可变的 schema 2 快照；编辑不会伪装成保存或激活。
2. 单一 actor 按 revision 排序设置写入、Codex 会话和单槽播放操作。原子保存成功是持久化提交点；旧 revision 不能覆盖新状态。
3. 媒体方案先通过同一个只读文件句柄确认本地普通文件、格式、大小和图片尺寸，并把规范路径和真实媒体类型保存回同一 `MediaReference`。
4. runtime 从已经保存的快照重新取得固定 lease，然后依次核验官方 Codex 的包、进程、用户会话、严格 IPv4 回环监听器、CDP browser/socket/target 和唯一主工作页面。
5. 安全验证完成后，经本机 CDP 把已核验媒体绑定到本项目持有的页面文件输入，由 Codex 原生允许的 `blob:` URL 加载。
6. 活动 lease、播放槽和注入资源分别带所有权 token/generation。更换背景、恢复官方背景或退出时，只移除本项目实际拥有的节点、样式、URL 和媒体资源。

运行状态不靠异常或单一 `IsActive` 推断，而是明确报告：

- `MediaActive`：媒体与对应 generation 已实际激活；
- `Official`：本应用当前没有活动媒体资源；
- `Faulted`：安全验证或注入阶段失败，界面同时显示结构化错误和清理结果；
- `Disconnected`：Codex 失联且本应用资源清理已经确认。

一次应用也会返回 `SavedButNotActivated`、`Superseded`、`Canceled` 或 `Failed` 等类型化结果，因此“已保存”“已激活”和“请求已完成”不会混成一个布尔值。

更完整的实现约束与安全审查清单见[威胁模型](THREAT_MODEL.md)。

## 从源码构建

前置条件：Windows 11 x64，以及 `.NET SDK 10.0.301` 或同一 feature band 的更新补丁。SDK 选择以仓库的 [`global.json`](global.json) 为准。

```powershell
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet test .\BackdropForCodex.slnx `
  --configuration Release `
  --filter "Category!=Integration"
dotnet run --project .\src\BackdropForCodex.App\BackdropForCodex.App.csproj
```

1.4.1 分支包含 533 个非集成自动化用例。用例数量不等于验证结论；只有实际执行命令并记录结果后才能写作“通过”。需要真实 Edge/CDP、当前机器 Codex、交互式通知区域或 UI Automation 的环境测试，缺少先决条件或未执行时必须逐项记录为“未验证”，不得计入通过数。

发布参数、格式检查、DCO 和安全实现约束见[贡献指南](CONTRIBUTING.md)；并发 checkpoint、集成测试和通知区域冒烟测试见[测试说明](tests/README.md)。

## 文档与贡献

- [更新日志](CHANGELOG.md)
- [Complete English README](README.en.md)
- [贡献指南](CONTRIBUTING.md)
- [安全策略](SECURITY.md)
- [威胁模型](THREAT_MODEL.md)
- [隐私说明](PRIVACY.md)
- [测试说明](tests/README.md)
- [第三方声明](THIRD_PARTY_NOTICES.md)

欢迎提交缺陷修复、文档和经过讨论的功能改进。所有提交必须带有符合 [DCO](DCO.md) 的 `Signed-off-by` 行。安全或隐私问题请按[安全策略](SECURITY.md)私下报告，不要创建公开 Issue。

## 许可证与声明

本项目以 [Apache License 2.0](LICENSE) 发布。第三方组件遵循各自许可证，详见[第三方声明](THIRD_PARTY_NOTICES.md)和随 Release 提供的 SBOM。

“OpenAI”“Codex”“Microsoft”“Windows”等名称和标识可能是其各自所有者的商标。本项目仅为说明兼容性而引用这些名称，不获得任何商标许可，也不暗示认可或支持。

## English documentation

The complete English documentation, including setup, profiles, latest-wins behavior, state semantics, security boundaries, and build instructions, is available in [README.en.md](README.en.md).
