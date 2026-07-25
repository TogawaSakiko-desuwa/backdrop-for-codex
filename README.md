# Backdrop for Codex

一个面向 **Windows 11 x64** 的非官方桌面伴侣：通过仅绑定回环地址的 Chrome DevTools Protocol（CDP），为 Microsoft Store / MSIX 安装的官方 Codex 桌面应用主工作窗口添加本地动态背景。

> [!CAUTION]
> Backdrop for Codex 是独立的社区项目，与 OpenAI 或 Microsoft 无隶属、赞助、认可或支持关系。CDP 是高权限的浏览器调试接口；“只监听回环地址”不代表没有风险。同一 Windows 用户会话中的恶意进程仍可能尝试连接、观察或控制调试目标。使用前请阅读[安全说明](SECURITY.md)和[威胁模型](THREAT_MODEL.md)。

## 功能

- 使用 WPF-UI 构建 Windows 11 Fluent 工作台，提供 Mica 背景、集成标题栏和原生浅色/深色体验。
- 支持本地 PNG、JPEG、WebP 图片。
- 支持本地 MP4、WebM 视频，以静音循环方式播放。
- 图片与视频均提供“完整显示”“裁剪填满”“拉伸”三种适配模式；“裁剪填满”可在预览中拖动焦点、使用方向键微调或一键恢复居中。
- 提供本地媒体预览和代表性的 Codex 界面预览，可在应用前分别调整深色/浅色主题遮罩（0–60%）、面板不透明度与背景模糊。
- 支持文件选择、单文件拖放和最多 8 条最近使用记录；失效路径会被明确标记且可以单独移除。
- 支持跟随系统、浅色和深色主题；深色主题使用黑色遮罩，浅色主题使用白色遮罩，系统高对比度开启时优先跟随系统可访问性设置。
- 可在工作台暂停或继续视频，并从纯 WPF 通知区域菜单打开窗口、恢复官方背景或退出。
- 外部伴侣运行，不修改 Codex 的 MSIX 包或安装文件。
- 主工作区保持透明；侧栏、顶栏、弹窗和右侧详情页签各使用一层玻璃，助手/用户消息气泡与活动行使用独立底板，代码块、编辑器、diff 和表格等内容表面保留主题底色。
- CDP 失联后通过租约机制清理已注入的页面资源。
- 设置“关于”页可由用户主动导出 schema 2 固定字段白名单诊断报告；报告包含脱敏的安全验证、结构契约匹配和逐项能力原因，不包含媒体路径、包/进程标识、页面标题或 URL、DOM、选择器、聊天、设置内容或原始异常。
- 不包含遥测、行为分析或项目自有崩溃上报。

项目设计不读取聊天内容，也不把本地媒体上传到项目服务器。但 CDP 本身具备远超“换背景”所需的能力；受损或被替换的构建可能滥用这些能力。请只使用可信构建并验证发布物。

## 界面与本地状态

主窗口采用“预览 + 参数检查器”的工作台布局。宽窗口中，媒体预览、最近记录和参数检查器并排呈现；窗口小于约 960 像素时会改为上下布局，并优先保留预览和可滚动的参数区，放宽窗口后重新显示最近记录。图片预览在内存中按需解码，视频预览会在窗口隐藏、暂停或系统要求减少动画时停止播放。

将一个受支持的图片或视频拖入窗口即可建立草稿；选择媒体、切换适配模式、拖动裁剪焦点或调整遮罩与可读性参数都不会立即改变正在运行的 Codex。“完整显示”保持比例并展示完整媒体，“裁剪填满”保持比例并允许裁去窗口之外的部分，“拉伸”则非等比匹配窗口。裁剪焦点只在“裁剪填满”中生效，切换模式时仍会保留其坐标。界面区分“正在编辑的草稿”“已保存的目标配置”和“当前已应用的快照”，因此应用失败时不会把已保存状态误报为正在生效。首次应用或增强启动前会显示 CDP 风险说明，确认可以在“设置 → 安全与隐私”中撤销；“关于”页还提供完整重置入口。

点击窗口关闭按钮或按 `Alt+F4` 会把工作台隐藏到通知区域，使已应用壁纸可以继续运行。首次关闭时会显示一次说明。只有从通知区域选择“退出”才会结束伴侣并执行壁纸、媒体 lease 和应用自有资源的清理；“恢复官方背景”只移除当前注入，不退出伴侣。

本项目把壁纸配置和纯界面偏好分开保存：

| 本地文件 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\CodexWallpaper\settings.json` | schema 2 的媒体目录、壁纸档案、语义区域绑定、适配/焦点/遮罩/玻璃/声音/音量/性能偏好、最近记录、CDP 风险确认，以及仅为旧设置透传的废弃兼容配置标识 |
| `%LOCALAPPDATA%\CodexWallpaper\settings.v1.backup.json` | 首次成功迁移 schema 1 前保留的原始字节只读备份；不会随正常 schema 2 保存而改写 |
| `%LOCALAPPDATA%\CodexWallpaper\ui-settings.json` | 主题模式和是否已经显示通知区域提示 |

这些文件都只属于当前 Windows 用户。当前 1.3 界面可把正在编辑的 `Global` 档案的深色和浅色主题遮罩分别调到 0–60%，默认分别为 30% 和 18%；schema 2 原生档案契约允许 0–100%，加载或保存时不会把尚未由 1.3 界面编辑的其他档案强制归一化到 60%。首次读取 schema 1 时，会先按原始字节创建并校验只读备份，再把现有媒体、最近记录、适配、焦点、遮罩、玻璃、风险确认和旧兼容标识迁移为 schema 2 的 `Global` 档案，并以原子替换方式发布新文件；迁移后的声音固定关闭、音量为 50%、性能策略为自动，不会从旧视频推断声音偏好。`LastCompatibilityProfileId` 只为兼容旧设置而原样透传：旧值保持旧值、`null` 保持 `null`，运行时不再生成或更新该值，也不读取它作能力决策。重复加载已迁移文件不会再次迁移。

损坏、超大、不可读取、迁移失败或与既有 V1 备份冲突的设置会进入“需要恢复”状态，程序不会用默认值静默覆盖原文件。高于 schema 2 的文件会进入未来版本只读状态，同样拒绝自动保存或降级覆盖；有效 V2 的 `Global` 绑定若使用当前 1.3 界面尚不能表达的来源，也会保持只读而不会被 V1 兼容界面覆盖。恢复 V1 备份或完整重置必须是显式恢复动作。schema 2 预留了 `Global`、`Home`、`Conversation`、`CodeAndDiff`、`SettingsAndOther` 五个稳定语义区域，但 1.3.3 的用户可见运行时仍只使用 `Global`、本地文件来源和单个活动媒体；区域切换、声音、Wallpaper Engine、模板和保温播放池不属于本版本。完整重置会恢复官方背景、清空设置与最近记录、撤销风险确认、重置 UI 偏好，并且只删除经核验确由本应用拥有的增强启动快捷方式；它也会永久删除 V1 备份，执行前会明确提示。

## 兼容范围

| 项目 | 支持状态 |
| --- | --- |
| Windows 11 x64 | 支持，也是唯一目标平台 |
| 官方 Microsoft Store / MSIX x64 Codex | 唯一目标应用；版本只用于身份自洽检查和诊断，不选择或改变表现契约 |
| 通过安全身份核验的当前或未来官方 Codex 版本 | 按实际页面证据匹配内置结构契约；相同结构获得相同契约与能力 |
| Win32 便携版、网页、CLI 或其他 Codex 客户端 | 不支持 |
| Windows 10、Windows on Arm、macOS、Linux | 不支持 |
| 功能区域、视频声音、多显示器/多个 Codex 窗口使用独立壁纸 | 1.3.3 不支持；当前使用一个 `Global` 档案 |
| 跨用户、远程计算机或非回环 CDP | 明确不支持 |

兼容判断分成两层。安全层仍严格失败关闭：必须是 Windows 11 x64 上的官方 Store/MSIX x64 包，并验证包名、包系列、由已验证身份字段构造的完整包名、应用 ID、可执行文件、当前用户会话、PID、启动时间、CDP 监听器所有权、严格 IPv4 回环端点和目标元数据。任何一项失败都会完全拒绝连接；任何结构探针都不会放宽这些条件。

通过安全层后，结构探针独立评估五类表现能力：

| 能力 | 1.3.3 行为 |
| --- | --- |
| 全局背景 | `global-baseline-v1` 通过时可用；baseline 失败时不注入 |
| 功能区域识别 | 已定义能力与设置契约，1.3.3 尚未实现 |
| 玻璃样式 | `codex-shell-v1` 及对应平台证据通过时可用；单项证据失败只关闭该能力 |
| 音频 | 已定义能力与设置契约，1.3.3 尚未实现且保持静音 |
| 高级内容表面 | `codex-shell-v1` 的核心结构和所需 CSS 平台证据通过时可用 |

安全层先完成包、进程、会话、回环端点、browser/socket/target 和唯一页面验证；安全失败或持续多目标歧义时，结构证据探针不会执行。安全页面判定不再把 `<main>` 等 DOM 锚点当作身份凭据。目标通过安全复验后，`global-baseline-v1` 独立检查 document/body、`body > #root` 和 root 内主工作区；`codex-shell-v1` 再检查 baseline、受审壳层锚点以及所需 CSS 平台能力。结构探针只返回布尔证据，不导出原始 DOM、URL 或选择器。

baseline 不计入高级契约匹配数。高级契约恰好命中一个时使用该契约；零匹配或多重匹配在最长 10 秒的就绪窗结束后锁定 `global-baseline-v1`，只保留 Global；baseline 失败则恢复官方背景并停止注入。版本号、注册顺序或隐藏优先级都不得用于决胜。活动契约在一次 generation 内锁定，五项能力保持独立且只能降级：baseline 丢失会关闭 Global 并清理，shell 或单项平台证据丢失只关闭相应能力，证据恢复不会在同一 generation 内重新启用或切换契约。Codex 更新仍可能改变页面结构、进程模型或调试行为，从而让部分或全部表现能力暂时不可用；相同 DOM fixture 则必须不受版本号变化影响。Backdrop for Codex 不绕过登录、权限、安全策略或应用签名。

## 工作原理与边界

Backdrop for Codex 在当前用户会话中识别官方 Store/MSIX Codex，连接其回环 CDP 端点，并向已经验证的主工作窗口注入单独的表现层。宿主只处理用户明确选择的本地绝对路径：先打开只读文件句柄，从该句柄解析最终目标，确认它仍是本地普通磁盘文件，再通过同一句柄核对扩展名、文件头/容器签名和大小。图片还必须能从该句柄解析出尺寸，宽和高各不得超过 32,768 像素，总像素不得超过 33,554,432；文件大小上限为 512 MiB，视频上限为 8 GiB。网络、目录、设备和解析后落到不受支持卷的路径会被拒绝。所有界面图片预览也只接受这条固定句柄校验链返回的元数据，并同时设置解码宽高上限。只读 lease 在使用期间固定该文件的身份，避免校验后被普通写入、替换、重命名或删除。随后宿主经本机 CDP 把 lease 解析后的文件绑定到页面中由本项目持有的隐藏 `input[type=file]`；页面用 Codex CSP 原生允许的 `blob:` URL 加载图片或视频。

现场验证表明，当前受审 Codex 版本的 CSP 不允许把回环 `http://127.0.0.1/...` 作为图片/视频来源。本项目不会修改、放宽或绕过 Codex 的 CSP，也不建议使用 CSP bypass。页面脚本只能短暂取得浏览器提供的 `File` 内容和元数据（文件名、大小、MIME type、修改时间），不能取得宿主保存的完整绝对路径。关闭、更换壁纸或 lease 到期时会移除媒体 `src`、撤销 `blob:` URL，并只删除本项目拥有的节点和样式。

媒体 lease 组件不启动 Kestrel、不创建临时 HTTP 监听器，也没有媒体 endpoint 或媒体访问令牌；媒体内容不经过 HTTP 或项目自有网络服务。网络侧只使用由 Codex 持有并经严格验证的本机 CDP 端点。

首次增强启动会在有限时间内等待 Codex 主页面完成挂载；当前上限为 10 秒。只有恰好一个合格工作页时才会注入；如果期间存在多个候选并在截止时仍无法收敛为唯一目标，则拒绝本次应用并清理准备态资源。头像浮层 `initialRoute=/avatar-overlay` 属于辅助页面，端点发现和注入前页面复验都会将其排除。MSIX `file:` 页面必须精确落在系统实际报告的包根目录下 `app\index.html`；远程目标只接受受审主机上的根工作区或具有完整路径段边界的 `/codex` 工作区，并拒绝认证路由、路径穿越和反斜杠歧义。1.3.3 不接受仅凭标题和任意 `127.0.0.1` 内容端口伪装的工作页。文件输入由准备脚本直接返回元素句柄，宿主随后重新核验目标页面，再只向该句柄绑定媒体；中途导航会让旧句柄失效。超时、没有合格页面或媒体加载失败同样失败关闭，不会误报壁纸成功。这些约束不能把 CDP 变成安全边界。关闭壁纸或退出伴侣会清理注入内容和媒体 lease，**不会关闭由 Codex 持有的调试端口**；只有完全退出 Codex 才会关闭该端口。不要把调试端口绑定到 `0.0.0.0`、局域网地址或端口转发；不要以管理员身份运行；不要在不可信的多用户会话中使用。更完整的数据流和剩余风险见[威胁模型](THREAT_MODEL.md)。

## 安装与使用

1. 在 Windows 11 x64 上安装官方 Store/MSIX x64 Codex；所有官方版本都必须先通过同一组严格安全身份与唯一目标检查，再按实际页面证据匹配 `global-baseline-v1` 与 `codex-shell-v1`，版本号本身不决定表现能力。
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

Backdrop for Codex 不发送遥测，不提供项目自有云服务，也不代理 Codex 与 OpenAI 的通信。schema 2 设置会在本机媒体目录中保存当前媒体的绝对路径和最多 8 条最近使用引用；完整路径由宿主持有，不会注入页面 DOM，但页面会在生成 `blob:` URL 前短暂接触所选文件的内容和有限元数据。诊断报告只会在用户从设置页明确选择保存位置后生成；schema 2 固定白名单仅包含环境、运行状态、安全结果、活动结构契约、匹配状态和逐项能力原因，不会自动上传，也不会导出 DOM、选择器、页面/进程标识或原始异常。调试日志可能包含运行状态和错误信息，设计上不记录聊天内容或回显媒体文件路径；分享日志、截图或转储前仍应人工检查。详见[隐私说明](PRIVACY.md)。Codex 本身的数据处理继续受 OpenAI 自身条款和隐私政策约束。

## 从源码构建

前置条件：Windows 11 x64、[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) `10.0.301` 或同一 feature band 的更新补丁。仓库的 `global.json` 以 `10.0.301` 为开发下限并使用 `latestPatch`；CI 同时验证 `10.0.301` 与 `10.0.302`，正式发布固定使用 `10.0.302`。发布工作流还安装 .NET 8 SDK/runtime 来托管锁定的 `Microsoft.Sbom.DotNetTool`，应用本身仍只由精确的 `10.0.302` 构建。运行应用或执行显式选择的机器兼容性测试还需要已安装的 Store/MSIX Codex。

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
- `src/BackdropForCodex.Core`：媒体来源与 lease、单槽播放池、Codex 安全核验与能力探针、模块化 CDP 注入、运行时协调、schema 2 设置与快捷方式安全边界。
- `tests/BackdropForCodex.Core.Tests`：Core 单元测试以及不启动真实 Codex 的 App 状态、偏好和错误映射测试；需要本机环境的测试统一标记为 `Integration`。
- `tools/BackdropForCodex.Benchmarks`：手工、本地、仅报告结果的媒体 lease/单槽激活基准；不作为发布阈值判定器。

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

如需采集本机媒体 lease 与单槽激活的参考数据，可手工运行：

```powershell
dotnet run --project .\tools\BackdropForCodex.Benchmarks\BackdropForCodex.Benchmarks.csproj `
  --configuration Release -- `
  --media .\path\to\sample.mp4 `
  --iterations 25 `
  --output .\artifacts\benchmark.json
```

该工具输出不含输入路径的 JSON，记录采集时间、OS/.NET/进程架构、媒体来源种类/格式/长度、迭代数、冷启动与热迭代分位数，以及进程私有字节变化；它不会启动/连接 Codex，也不会自动判定通过或失败。输入媒体仍会像正常运行时一样在本机打开、校验并持有 lease。

Pull Request 会在 Windows runner 上用精确的 `10.0.301` 和 `10.0.302` 分别执行锁定依赖还原、格式检查、Release 构建、非集成测试和单文件发布形态检查，并接受 CodeQL 分析。正式标签的 Release 工作流固定使用 `10.0.302`，在发布前再次执行格式检查与非集成测试，并生成、校验 SBOM、SHA-256 与 GitHub 构建来源证明。

提交变更前请阅读[贡献指南](CONTRIBUTING.md)，所有提交必须带有符合 [DCO](DCO.md) 的 `Signed-off-by` 行。安全问题请不要创建公开 Issue，应按[安全策略](SECURITY.md)私下报告。

## 许可证与商标

本项目以 [Apache License 2.0](LICENSE) 发布。第三方组件仍遵循各自许可证，详见[第三方声明](THIRD_PARTY_NOTICES.md)和随发布提供的 SBOM。

“OpenAI”“Codex”“Microsoft”“Windows”等名称和标识可能是其各自所有者的商标。本项目仅为说明兼容性而引用这些名称，不获得任何商标许可，也不暗示认可。

---

## English summary

Backdrop for Codex is an independent, unofficial companion for **Windows 11 x64**. It uses a strictly verified loopback CDP connection to place a local PNG/JPEG/WebP image or a muted, looping MP4/WebM video behind the main workspace of the official Microsoft Store/MSIX x64 Codex desktop app. The selected local file is opened once, resolved to its final local regular-file target, checked for extension/signature/size consistency through that same handle, and held by a read-only lease. Images must also expose parseable dimensions no greater than 32,768 pixels per side and 33,554,432 pixels in total; UI previews consume only metadata returned by this same pinned-stream validation path and cap both decode dimensions. The host then binds the resolved file to an owned file input over CDP and the page loads it through a CSP-native `blob:` URL. There is no Kestrel instance, temporary media HTTP listener, media endpoint, or media token. Backdrop for Codex neither modifies nor bypasses Codex's CSP or package.

Codex version is not a presentation-capability gate in 1.3.3. Package identity, publisher, architecture, application id, process/session/start time, listener ownership, strict IPv4 loopback addressing, CDP browser/socket/target metadata, and the single eligible page still fail closed. The verified four-part version is used only for identity consistency and diagnostics. Only after those checks pass does a read-only evidence probe evaluate the built-in, version-independent `global-baseline-v1` and `codex-shell-v1` contracts. Exactly one advanced-contract match enables its reviewed glass and advanced surfaces; no match or an ambiguous match falls back to Global-only after the bounded readiness window, while a failed baseline prevents injection. The selected contract is locked for the generation and each capability can only degrade until the next generation. Identical DOM evidence must produce identical contracts and capabilities for current and future official versions. Region recognition and audio remain unimplemented and playback remains muted.

Durable settings use schema 2 with a media catalog, profiles, and stable semantic-region bindings, while the 1.3.3 UI/runtime still exposes only one `Global` profile, local files, muted playback, and one active media slot. The compatibility UI limits edits to the Global overlay to 60%, but schema-2 values up to 100% in untouched profiles are preserved rather than globally normalized. A first schema-1 load preserves the exact original bytes as read-only `settings.v1.backup.json` before atomically publishing the migrated document. Corrupt, unreadable, conflicting, or failed migrations enter explicit recovery instead of being overwritten with defaults; future schemas and valid V2 Global bindings that the 1.3 compatibility UI cannot represent remain read-only. The deprecated `LastCompatibilityProfileId` is preserved verbatim solely for old-settings round trips and no longer controls or records runtime behavior. A full reset explicitly and permanently deletes the V1 backup. UI-only preferences remain in `ui-settings.json`. The project has no telemetry, crash upload, or project-operated cloud service. A schema-2 diagnostic JSON report is created only after explicit user export and contains allow-listed Environment, Runtime, and Compatibility fields, including the Codex version, typed security result, active contract, match state, and per-capability reasons. It never contains media paths, package/publisher/process/session/port details, page titles or URLs, DOM or selectors, chats, settings, stable identifiers, raw exceptions, or CDP details, and it is not uploaded. A separate manual benchmark reports path-free local lease/single-slot measurements without enforcing release thresholds.

CDP remains a powerful local-control interface: loopback addressing does not protect against malicious processes running as the same Windows user. Exiting the companion releases the media lease, removes owned DOM/media resources, and revokes the `blob:` URL, but the Codex-owned CDP port remains open until Codex itself fully exits. Development requires .NET SDK `10.0.301` or a later patch in that feature band; CI covers `10.0.301` and `10.0.302`, while releases build the application with exact SDK `10.0.302` and use a separately installed .NET 8 SDK/runtime only to host the locked SBOM tool. Use verified releases, never expose or forward the debugging endpoint beyond loopback, and review [SECURITY.md](SECURITY.md), [PRIVACY.md](PRIVACY.md), and [THREAT_MODEL.md](THREAT_MODEL.md) before use. Backdrop for Codex is not affiliated with, endorsed by, or supported by OpenAI or Microsoft.
