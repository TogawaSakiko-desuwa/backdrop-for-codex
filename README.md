# Backdrop for Codex

一个面向 **Windows 11 x64** 的非官方桌面伴侣：通过仅绑定回环地址的 Chrome DevTools Protocol（CDP），为 Microsoft Store / MSIX 安装的官方 Codex 桌面应用主工作窗口添加本地动态背景。

> [!CAUTION]
> Backdrop for Codex 是独立的社区项目，与 OpenAI 或 Microsoft 无隶属、赞助、认可或支持关系。CDP 是高权限的浏览器调试接口；“只监听回环地址”不代表没有风险。同一 Windows 用户会话中的恶意进程仍可能尝试连接、观察或控制调试目标。使用前请阅读[安全说明](SECURITY.md)和[威胁模型](THREAT_MODEL.md)。

## 功能

- 使用 WPF-UI 构建 Windows 11 Fluent 工作台，提供 Mica 背景、集成标题栏和原生浅色/深色体验。
- 支持本地 PNG、JPEG、WebP 图片。
- 支持本地 MP4、WebM 视频，以静音循环方式播放。
- 提供本地媒体预览和代表性的 Codex 界面预览，可在应用前调整填充方式、面板不透明度与背景模糊。
- 支持文件选择、单文件拖放和最多 8 条最近使用记录；失效路径会被明确标记且可以单独移除。
- 支持跟随系统、浅色和深色主题；系统高对比度开启时优先跟随系统可访问性设置。
- 可在工作台暂停或继续视频，并从纯 WPF 通知区域菜单打开窗口、恢复官方背景或退出。
- 外部伴侣运行，不修改 Codex 的 MSIX 包或安装文件。
- 主工作区保持透明；侧栏、顶栏和弹窗各使用一层玻璃，助手/用户消息气泡与活动行使用独立底板，在不铺设全局暗色遮罩的前提下保持文字可读。
- CDP 失联后通过租约机制清理已注入的页面资源。
- 不包含遥测、行为分析或项目自有崩溃上报。

项目设计不读取聊天内容，也不把本地媒体上传到项目服务器。但 CDP 本身具备远超“换背景”所需的能力；受损或被替换的构建可能滥用这些能力。请只使用可信构建并验证发布物。

## 界面与本地状态

主窗口采用“预览 + 参数检查器”的工作台布局。宽窗口中，媒体预览、最近记录和参数检查器并排呈现；窗口小于约 960 像素时会改为上下布局，并优先保留预览和可滚动的参数区，放宽窗口后重新显示最近记录。图片预览在内存中按需解码，视频预览会在窗口隐藏、暂停或系统要求减少动画时停止播放。

将一个受支持的图片或视频拖入窗口即可建立草稿；选择媒体和移动参数不会立即改变正在运行的 Codex。界面区分“正在编辑的草稿”“已保存的目标配置”和“当前已应用的快照”，因此应用失败时不会把已保存状态误报为正在生效。首次应用或增强启动前会显示 CDP 风险说明，确认可以在“设置 → 安全与隐私”中撤销；“关于”页还提供完整重置入口。

点击窗口关闭按钮或按 `Alt+F4` 会把工作台隐藏到通知区域，使已应用壁纸可以继续运行。首次关闭时会显示一次说明。只有从通知区域选择“退出”才会结束伴侣并执行壁纸、媒体 lease 和应用自有资源的清理；“恢复官方背景”只移除当前注入，不退出伴侣。

本项目把壁纸配置和纯界面偏好分开保存：

| 本地文件 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\CodexWallpaper\settings.json` | 媒体绝对路径、填充与可读性参数、最近记录、CDP 风险确认和兼容配置标识 |
| `%LOCALAPPDATA%\CodexWallpaper\ui-settings.json` | 主题模式和是否已经显示通知区域提示 |

两个文件都只属于当前 Windows 用户。完整重置会恢复官方背景、清空设置与最近记录、撤销风险确认、重置 UI 偏好，并且只删除经核验确由本应用拥有的增强启动快捷方式。

## 兼容范围

| 项目 | 支持状态 |
| --- | --- |
| Windows 11 x64 | 支持，也是唯一目标平台 |
| Microsoft Store / MSIX 版 Codex `26.715.10079.0` | 当前兼容配置支持，也是唯一目标应用 |
| 其他 Codex 版本 | 默认拒绝连接，需经兼容性审查后显式放行 |
| Win32 便携版、网页、CLI 或其他 Codex 客户端 | 不支持 |
| Windows 10、Windows on Arm、macOS、Linux | 不支持 |
| 多显示器/多个 Codex 窗口使用独立壁纸 | v1 不支持；当前使用一组全局设置 |
| 跨用户、远程计算机或非回环 CDP | 明确不支持 |

Codex 更新可能改变其页面结构、进程模型或调试行为，从而暂时破坏兼容性。Backdrop for Codex 不绕过登录、权限、安全策略或应用签名。

## 工作原理与边界

Backdrop for Codex 在当前用户会话中识别官方 Store/MSIX Codex，连接其回环 CDP 端点，并向已经验证的主工作窗口注入单独的表现层。宿主只处理用户明确选择的绝对路径：先规范化路径，核对扩展名与对应的文件头/容器签名，再以只读 lease 保持同一文件，避免校验后被普通写入、替换或删除。随后宿主经本机 CDP 把该文件绑定到页面中由本项目持有的隐藏 `input[type=file]`；页面用 Codex CSP 原生允许的 `blob:` URL 加载图片或视频。

现场验证表明，当前受审 Codex 版本的 CSP 不允许把回环 `http://127.0.0.1/...` 作为图片/视频来源。本项目不会修改、放宽或绕过 Codex 的 CSP，也不建议使用 CSP bypass。页面脚本只能短暂取得浏览器提供的 `File` 内容和元数据（文件名、大小、MIME type、修改时间），不能取得宿主保存的完整绝对路径。关闭、更换壁纸或 lease 到期时会移除媒体 `src`、撤销 `blob:` URL，并只删除本项目拥有的节点和样式。

当前代码中 `LoopbackMediaServer` 仍作为兼容过渡层保留，用于持有上述只读 lease；它的 endpoint 和随机令牌不再注入 DOM，Codex 渲染器也不再通过该 HTTP endpoint 读取媒体。后续计划将其收敛为不监听端口的 lease-only 组件。在完成该重构前，它仍会短暂创建仅绑定 `127.0.0.1` 的单文件服务，因此相关回环约束仍属于安全不变量。

首次增强启动会在有限时间内等待 Codex 主页面完成挂载；当前上限为 10 秒，超时即失败关闭，不会把“尚未找到主页面”误报为壁纸成功。这些约束不能把 CDP 变成安全边界。关闭壁纸或退出伴侣会清理注入内容和媒体 lease，**不会关闭由 Codex 持有的调试端口**；只有完全退出 Codex 才会关闭该端口。不要把调试端口绑定到 `0.0.0.0`、局域网地址或端口转发；不要以管理员身份运行；不要在不可信的多用户会话中使用。更完整的数据流和剩余风险见[威胁模型](THREAT_MODEL.md)。

## 安装与使用

1. 在 Windows 11 x64 上安装官方 Store/MSIX Codex；当前兼容配置只放行 `26.715.10079.0`，其他版本会失败关闭。
2. 从本仓库的 GitHub Releases 下载 `BackdropForCodex-vX.Y.Z-win-x64.zip`、`SHA256SUMS` 文件和 SPDX SBOM。
3. 按下文验证 SHA-256 与 GitHub 构建来源证明，然后将 ZIP 解压到普通用户可写的空目录；若曾运行改名前的本地原型，请勿覆盖旧目录，以免旧可执行文件残留。
4. 先完全退出所有 Codex 进程，再启动 `BackdropForCodex.exe`，选择受支持的本地图片或视频并确认本机调试端口风险；工具不会强制结束已经运行的 Codex。
5. 首次成功后，桌面会创建或更新 `Codex（动态背景）.lnk`。以后可在 Codex 完全退出时用它执行增强启动；若伴侣已在托盘，快捷方式会把请求转给同一用户会话中的首实例。移动 EXE 后需从新位置再成功启动一次以更新快捷方式。
6. 在 Fluent 工作台切换壁纸、调整预览或暂停视频；通知区域菜单可重新打开窗口、恢复官方背景或退出。关闭工作台只会隐藏窗口。恢复或退出伴侣不会关闭由 Codex 持有的 CDP 端口，使用完毕后请完全退出 Codex。

发布物目前可能没有 Authenticode 代码签名。SHA-256 只能检测字节是否一致，GitHub artifact attestation 用于验证发布物由本仓库工作流产生；两者都不能替代代码审查、Windows 代码签名或端点防护。

### 验证发布物

在下载目录打开 PowerShell：

```powershell
Get-FileHash .\BackdropForCodex-vX.Y.Z-win-x64.zip -Algorithm SHA256
Get-Content .\BackdropForCodex-vX.Y.Z-SHA256SUMS.txt
```

确认 ZIP 的散列与清单完全一致。安装 [GitHub CLI](https://cli.github.com/) 后还可以验证构建来源证明：

```powershell
gh attestation verify .\BackdropForCodex-vX.Y.Z-win-x64.zip --repo TogawaSakiko-desuwa/backdrop-for-codex
```

同一发布中的 `BackdropForCodex-vX.Y.Z-win-x64.spdx.json` 是机器可读的软件物料清单（SBOM）。

## 隐私

Backdrop for Codex 不发送遥测，不提供项目自有云服务，也不代理 Codex 与 OpenAI 的通信。设置会在本机保存当前媒体的绝对路径和最多 8 条最近使用路径；完整路径由宿主持有，不会注入页面 DOM，但页面会在生成 `blob:` URL 前短暂接触所选文件的内容和有限元数据。调试日志可能包含运行状态和错误信息，设计上不记录聊天内容或回显媒体文件路径；分享日志前仍应人工检查。详见[隐私说明](PRIVACY.md)。Codex 本身的数据处理继续受 OpenAI 自身条款和隐私政策约束。

## 从源码构建

前置条件：Windows 11 x64、[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。运行应用或进行手工兼容性验证还需要已安装的 Store/MSIX Codex。

```powershell
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet publish .\src\BackdropForCodex.App\BackdropForCodex.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --no-restore `
  --output .\artifacts\local-publish `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugSymbols=false `
  -p:DebugType=None
dotnet run --project .\src\BackdropForCodex.App\BackdropForCodex.App.csproj
```

### 代码结构

- `src/BackdropForCodex.App`：WPF/Fluent 界面、响应式布局、本地化、通知区域、UI 偏好和 ViewModel 编排。
- `src/BackdropForCodex.Core`：媒体校验与 lease、Codex 兼容性与进程核验、CDP 注入、运行时协调、持久化设置和快捷方式安全边界。
- `tests/BackdropForCodex.Core.Tests`：Core 单元测试以及不启动真实 Codex 的 App 状态、偏好和错误映射测试；需要本机环境的测试统一标记为 `Integration`。

默认测试不会启动或连接真实 Codex：

```powershell
dotnet test .\BackdropForCodex.slnx `
  --configuration Release `
  --filter "Category!=Integration"
```

通知区域生命周期需要已解锁的 Windows 11 交互桌面；构建 Debug 版本后可运行本地冒烟测试，确认关闭主窗口前后都能发现托盘图标：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\tests\Smoke\TrayLifecycle.ps1 `
  -Configuration Debug `
  -ProbeBeforeClose
```

集成测试必须显式选择。机器兼容性测试要求安装受审 Store/MSIX Codex，其中进程核验用例还要求 Codex 正在当前用户会话中运行；启动就绪测试会启动本机 Edge/CDP 测试页。确认环境后可分别运行：

```powershell
$env:BACKDROP_FOR_CODEX_RUN_MACHINE_TESTS = "1"
dotnet test .\tests\BackdropForCodex.Core.Tests\BackdropForCodex.Core.Tests.csproj `
  --configuration Release `
  --filter "Category=Integration&FullyQualifiedName~CurrentMachineCompatibilityTests"
Remove-Item Env:\BACKDROP_FOR_CODEX_RUN_MACHINE_TESTS

$env:BACKDROP_FOR_CODEX_RUN_STARTUP_RACE_TESTS = "1"
dotnet test .\tests\BackdropForCodex.Core.Tests\BackdropForCodex.Core.Tests.csproj `
  --configuration Release `
  --filter "Category=Integration&FullyQualifiedName~PuppeteerWallpaperSessionStartupReadinessTests"
Remove-Item Env:\BACKDROP_FOR_CODEX_RUN_STARTUP_RACE_TESTS
```

Pull Request 会在 Windows runner 上重新执行锁定依赖还原、格式检查、Release 构建、非集成测试和单文件发布形态检查，并接受 CodeQL 分析。正式标签的 Release 工作流还会生成并校验 SBOM、SHA-256 与 GitHub 构建来源证明。

提交变更前请阅读[贡献指南](CONTRIBUTING.md)，所有提交必须带有符合 [DCO](DCO.md) 的 `Signed-off-by` 行。安全问题请不要创建公开 Issue，应按[安全策略](SECURITY.md)私下报告。

## 许可证与商标

本项目以 [Apache License 2.0](LICENSE) 发布。第三方组件仍遵循各自许可证，详见[第三方声明](THIRD_PARTY_NOTICES.md)和随发布提供的 SBOM。

“OpenAI”“Codex”“Microsoft”“Windows”等名称和标识可能是其各自所有者的商标。本项目仅为说明兼容性而引用这些名称，不获得任何商标许可，也不暗示认可。

---

## English summary

Backdrop for Codex is an independent, unofficial companion for **Windows 11 x64**. It uses a loopback-only CDP connection to add a local PNG/JPEG/WebP image or a muted, looping MP4/WebM video behind the main workspace of the official Microsoft Store/MSIX Codex desktop app. The current compatibility profile explicitly allows Codex `26.715.10079.0` and fails closed for other versions. After validating and read-locking the explicitly selected file, the host binds it to an owned file input over local CDP and the reviewed page loads it through a CSP-native `blob:` URL. Backdrop for Codex neither bypasses nor recommends bypassing Codex's CSP. The main workspace remains transparent, while the sidebar, title bar, dialogs, message bubbles, and activity rows receive scoped readability surfaces. Its responsive Fluent/Mica studio provides local preview, drag-and-drop, recent media, fit/readability controls, system/light/dark themes, and revocable risk settings. Closing the window hides it in the notification area; the tray reopens the studio, restores the official background, or exits. Lease cleanup removes the media source, revokes the blob URL, and removes owned resources after disconnect. It does not modify the Codex package and is not designed to read chats.

The project has no telemetry or project-operated cloud service. Wallpaper settings remain in `%LOCALAPPDATA%\CodexWallpaper\settings.json` and include the absolute path of the current media and up to eight recent paths; theme and one-time tray UI preferences are stored separately in `ui-settings.json`. The page can briefly access the selected file's contents and browser-exposed metadata, but not its absolute host path. A transitional loopback media-server type still holds the read-only lease and may temporarily listen on `127.0.0.1`; its endpoint/token are not injected into the DOM and the renderer does not fetch the wallpaper over HTTP. v1 uses one global wallpaper configuration and does not support per-monitor or per-window wallpapers. CDP remains a powerful local-control interface: loopback binding reduces remote exposure but does not protect against malicious processes running as the same Windows user. Exiting the companion removes its DOM/media changes, but the CDP port remains open until Codex itself fully exits. Use verified releases, never expose the debugging endpoint beyond loopback, and review [SECURITY.md](SECURITY.md), [PRIVACY.md](PRIVACY.md), and [THREAT_MODEL.md](THREAT_MODEL.md) before use. Backdrop for Codex is not affiliated with, endorsed by, or supported by OpenAI or Microsoft.
