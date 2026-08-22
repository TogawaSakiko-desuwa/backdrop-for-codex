<p align="center">
  <strong>简体中文</strong> · <a href="README.en.md">English</a>
</p>

# Backdrop for Codex

Backdrop for Codex 是一个面向 **Windows 11 x64** 的非官方开源伴侣，为官方 Microsoft Store / MSIX Codex 桌面应用添加可预览、可保存并可随时恢复的工作区背景。支持本地图片、静音循环视频、已安装的 Wallpaper Engine Image / Video，以及实验性的 Scene / Web 项目。

**不修改 Codex 安装文件 · 不向项目自有服务上传本地媒体 · 不读取聊天 · 不收集遥测**

[![Latest release](https://img.shields.io/github/v/release/TogawaSakiko-desuwa/backdrop-for-codex?display_name=tag)](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest)
[![CI](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/actions/workflows/ci.yml/badge.svg)](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

[快速开始](#下载与快速开始) · [v1.5.0 更新](#v150-更新亮点) · [核心功能](#核心功能) · [兼容性](#兼容性与限制) · [源码构建](#从源码构建)

[**下载 Windows 11 x64 便携版 →**](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest)

`v1.5.0` · 无需安装 · 使用普通用户权限运行 · 支持本地媒体、Wallpaper Engine Image / Video，以及实验性 Scene / Web

> [!CAUTION]
> Backdrop for Codex 是独立社区项目，与 OpenAI 或 Microsoft 无隶属、赞助、认可或支持关系。它通过本机回环地址上的 Chrome DevTools Protocol（CDP）工作；请勿以管理员身份运行或转发调试端口，使用完毕后应完全退出 Codex。详情见[安全说明](SECURITY.md)和[威胁模型](THREAT_MODEL.md)。

## 效果展示

<p align="center">
  <img src="docs/images/codex-backdrop-conversation.png" alt="Codex 对话工作区使用本地图片背景" width="100%" />
</p>

<p align="center"><sub>对话工作区中的实际背景效果</sub></p>

<table>
  <tr>
    <td width="33%"><img src="docs/images/codex-backdrop-warm.png" alt="Codex 工作区使用暖色本地图片背景" /></td>
    <td width="33%"><img src="docs/images/codex-backdrop-vivid.png" alt="Codex 工作区使用高饱和本地图片背景" /></td>
    <td width="34%"><img src="docs/images/codex-backdrop-camp.png" alt="Codex 工作区使用露营主题本地图片背景" /></td>
  </tr>
  <tr>
    <td align="center">暖色背景与半透明界面</td>
    <td align="center">高饱和背景与可读性遮罩</td>
    <td align="center">全窗口背景与内容卡片</td>
  </tr>
</table>

<p align="center"><sub>示例媒体仅用于展示本地背景效果，不随本项目或 Release 发布。</sub></p>

## v1.5.0 更新亮点

### Wallpaper Engine 来源

> [!IMPORTANT]
> Scene / Web 在 v1.5.0 中为实验性功能，当前仍在完善；Image / Video 是稳定使用路径。

- 浏览本机已安装的 Workshop 项目和 Local Project，可按类型或来源筛选，并通过项目缩略图快速选择。
- 支持 Image、Video，以及实验性的 Scene / Web 项目。Image / Video 可直接播放；使用 Scene / Web 前需先启动 Wallpaper Engine。

### 工作台与运行链路

- 重新设计背景工作台，将背景方案、来源库、最近使用、预览、应用和恢复官方背景集中在同一套操作流程中。
- 重构来源、设置、激活和清理链路，使本地媒体与 Wallpaper Engine 项目使用统一的背景方案；现有方案会自动迁移。
- 实验性 Scene / Web 会在遇到可恢复故障时逐级降低画质，并在短暂的捕获、编码或页面连接中断后尝试恢复。
- 适配当前 Codex 对话与 Markdown 表格结构，并修复深色主题文字对比度和对话框交互问题。

[查看完整更新日志](CHANGELOG.md#150---2026-08-21)

## 下载与快速开始

| Release 文件 | 用途 |
| --- | --- |
| `BackdropForCodex-v1.5.0-win-x64.zip` | 普通用户下载；解压后直接运行 |
| `BackdropForCodex-v1.5.0-SHA256SUMS.txt` | 核对下载文件的 SHA-256 |
| `BackdropForCodex-v1.5.0-win-x64.spdx.json` | 机器可读的 SPDX SBOM |

1. 在 Windows 11 x64 上安装官方 Microsoft Store / MSIX x64 Codex。
2. 从 [GitHub Releases](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest) 下载 `BackdropForCodex-v1.5.0-win-x64.zip`，解压到普通用户可写的空目录。
3. 完全退出所有 Codex 进程，然后启动 `BackdropForCodex.exe`。
4. 新建或选择背景方案，添加本地图片、视频或已安装的 Wallpaper Engine 项目，调整预览后点击“应用并启动 Codex”。
5. 在随后出现的 CDP 风险提示中阅读安全边界；确认后，应用才会继续启动 Codex 并应用背景。
6. 首次媒体激活成功后，应用会尝试在桌面创建 `Codex（动态背景）.lnk`；以后可用它执行增强启动。

> [!NOTE]
> `v1.5.0` 便携版未进行 Authenticode 代码签名。遇到 Windows SmartScreen 提示时，请先确认文件来自本仓库的 Release，并核对 SHA-256 或 GitHub artifact attestation。

<details>
<summary><strong>验证 SHA-256 与 GitHub 构建来源</strong></summary>

在下载目录打开 PowerShell：

```powershell
Get-FileHash .\BackdropForCodex-v1.5.0-win-x64.zip -Algorithm SHA256
Get-Content .\BackdropForCodex-v1.5.0-SHA256SUMS.txt
```

确认 ZIP 的散列与清单完全一致。安装 [GitHub CLI](https://cli.github.com/) 后还可以验证构建来源：

```powershell
gh attestation verify .\BackdropForCodex-v1.5.0-win-x64.zip `
  --repo TogawaSakiko-desuwa/backdrop-for-codex
```

</details>

## 核心功能

### 媒体来源

- 使用本地 PNG、JPEG、WebP 图片或静音循环 MP4、WebM 视频。
- 从经过验证的官方 Steam / Wallpaper Engine 安装中发现本机已安装的 Workshop 项目和 `projects/myprojects`、`projects/backup` 本地项目；不浏览在线 Workshop，不订阅、下载或修改 Steam 配置。
- 唯一有效安装会自动选择；存在多个安装或自动发现失败时，可在来源库手动选择安装位置，并可随时恢复自动发现。手动选择仅在本次运行中生效。
- Wallpaper Engine Image / Video 项目可直接播放；实验性 Scene / Web 要求 Wallpaper Engine 已经运行。项目可能通过 Wallpaper Engine 播放声音，Backdrop 不静音或转发该音频。

### 工作台与方案

- 管理多个背景方案，包括新建、复制、重命名、删除，以及不使用自定义媒体的“官方背景”方案。
- 提供“完整显示”“裁剪填满”“拉伸”三种适配模式，并支持拖动焦点和方向键微调。
- 分别调整浅色/深色主题遮罩、面板不透明度和背景模糊，应用前即可预览。
- 支持文件选择、单文件拖放、最近使用记录，以及视频和实验性 Scene / Web 动态背景的暂停与继续。

### 应用与恢复

- 快速重复应用时以最后一次操作为准，旧请求不会覆盖更新的背景。
- 从通知区域重新打开工作台、恢复官方背景或完全退出伴侣。
- 作为外部伴侣运行，不修改、替换或重新签名 Codex 的 MSIX 包。

<details>
<summary><strong>查看背景方案工作台</strong></summary>

![Backdrop for Codex 背景方案工作台](docs/images/backdrop-workbench.png)

</details>

## 兼容性与限制

| 项目 | 当前状态 |
| --- | --- |
| Windows 11 x64 | 支持，也是唯一目标平台 |
| 官方 Microsoft Store / MSIX x64 Codex | 支持；使用前会核验包、进程、会话、回环端点和目标页面 |
| PNG、JPEG、WebP | 支持 |
| MP4、WebM | 支持静音循环播放 |
| 已安装的 Wallpaper Engine Image / Video | 支持 Workshop 与 Local Project；直接使用受验证媒体文件，不要求 Wallpaper Engine 正在运行 |
| 已安装的 Wallpaper Engine Scene / Web | 实验性支持 Workshop 与 Local Project；要求经过验证的 Wallpaper Engine 已经运行，且系统支持动态捕获与播放。项目声音可能由 Wallpaper Engine 直接播放 |
| Wallpaper Engine Application、Unknown | 永久拒绝，不执行 |
| 在线 Workshop、自动订阅/下载、用户属性、预设、播放列表、向 Codex 转发声音或输入 | 不支持 |
| 本地媒体限制 | 仅本地普通磁盘文件；图片不超过 512 MiB、单边 32,768 像素和约 33.5 MP，视频不超过 8 GiB |
| Codex Win32 便携版、Codex 网页版/CLI、Windows 10、Windows on Arm、macOS、Linux | 不支持 |
| 多窗口独立壁纸、分区域壁纸、视频声音 | 当前不支持 |

实验性 Scene / Web 不会使用 Wallpaper Engine 的全局 pause/play/mute 命令，也不会捕获桌面。使用前请先启动 Wallpaper Engine；Backdrop 不会自动启动它。Scene / Web 可能播放声音，Backdrop 不枚举、静音或改变任何 Wallpaper Engine 音频会话，也不会把音频转发到 Codex。首次使用 Web 项目会提示其可能联网和播放声音。项目仓库和 Release 不包含 Wallpaper Engine 二进制、Workshop 内容或用户项目。

兼容性依据实际 Codex 页面和进程状态判断。安全核验失败时不会注入；只有部分视觉效果不可用时，应用会保留可用背景并使用高可读性回退。Codex 更新可能暂时影响兼容性，请优先使用最新版本的 Backdrop for Codex。

## 常见问题

### 关闭窗口后为什么还在运行？

点击关闭按钮或按 `Alt+F4` 只会把工作台隐藏到通知区域，已应用背景会继续运行。请从通知区域菜单选择“退出”以结束伴侣。

### 找不到 Codex，或者应用背景失败怎么办？

确认使用的是官方 Microsoft Store / MSIX x64 Codex，且 Backdrop for Codex 没有以管理员身份运行。完全退出所有 Codex 进程后，重新打开工作台或使用 `Codex（动态背景）.lnk`。仍然失败时，可在设置中导出诊断报告，并通过 [GitHub Issues](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/issues/new/choose) 提交问题。

### Codex 更新后背景失效怎么办？

先恢复官方背景并完全退出 Codex，再查看[最新 Release](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest)是否包含兼容性更新。不要尝试绕过失败的安全核验。

### 如何恢复官方背景？

工作台和通知区域菜单都提供“恢复官方背景”。该操作只清理当前注入，不删除已经保存的背景方案；再次点击“应用并启动 Codex”即可恢复所选方案。

### 如何完整重置或卸载？

1. 打开设置，在“危险区”选择“重置应用”。应用会恢复官方背景，并删除设置、最近媒体、风险确认、UI 偏好及由本应用拥有的桌面快捷方式。
2. 从通知区域菜单退出 Backdrop for Codex，并完全退出 Codex。
3. 删除之前解压 `win-x64.zip` 的目录。

应用设置保存在 `%LOCALAPPDATA%\CodexWallpaper`。如果重置报告部分失败，请检查该目录和桌面快捷方式是否仍然存在。

## 安全与隐私

- 只连接经过核验的官方 Store/MSIX Codex 和严格 IPv4 回环 CDP 端点；回环地址仍不能防御同一 Windows 用户会话中的恶意进程。
- 本地媒体经校验后通过受控文件输入和 `blob:` URL 加载，不启动媒体 HTTP 服务。
- Scene / Web 只把已验证项目窗口的画面传入 Codex；不传递 Wallpaper Engine 脚本、键盘/鼠标输入、音频或 Codex 内容。第三方 Web wallpaper 自身仍可能联网并通过 Wallpaper Engine 播放声音。
- 不修改或绕过 Codex 内容安全策略（CSP），不读取聊天，也不代理 Codex 与 OpenAI 的通信。
- 不发送遥测、行为分析或项目自有崩溃报告；诊断报告只在用户明确导出时生成。
- 更换、恢复或退出时，只清理本项目拥有的页面资源。Codex 持有的 CDP 端口只有在完全退出 Codex 后才会关闭。

完整数据流和安全边界见：

- [安全策略](SECURITY.md)
- [威胁模型](THREAT_MODEL.md)
- [隐私说明](PRIVACY.md)

## 工作原理

1. 本地文件和 Wallpaper Engine Image / Video 经本机校验后，以页面允许的本地媒体方式加载。
2. Scene / Web 从已运行的 Wallpaper Engine 捕获独立项目窗口的画面，再把像素传给 Codex；不传递项目脚本、输入或 Codex 内容。
3. 应用前会核验 Codex、回环调试端点和目标页面；更换背景、恢复或退出时只清理本项目创建的资源。

## 从源码构建

前置条件：Windows 11 x64，以及 `.NET SDK 10.0.301` 或同一 feature band 的更新补丁。SDK 选择以仓库的 [`global.json`](global.json) 为准。

```powershell
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet test .\BackdropForCodex.slnx `
  --configuration Release `
  --filter "Category!=Integration&Category!=BrowserContract"
dotnet run --project .\src\BackdropForCodex.App\BackdropForCodex.App.csproj
```

发布参数、格式检查、DCO 和实现约束见[贡献指南](CONTRIBUTING.md)。

## 获取帮助与参与贡献

- [报告问题或提出功能建议](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/issues/new/choose)
- [更新日志](CHANGELOG.md)
- [贡献指南](CONTRIBUTING.md)
- [致谢](ACKNOWLEDGEMENTS.md)
- [第三方声明](THIRD_PARTY_NOTICES.md)

欢迎提交缺陷修复、文档和经过讨论的功能改进。所有提交必须带有符合 [DCO](DCO.md) 的 `Signed-off-by` 行。安全或隐私问题请按[安全策略](SECURITY.md)私下报告，不要创建公开 Issue。

## 许可证与声明

本项目以 [Apache License 2.0](LICENSE) 发布。第三方组件遵循各自许可证，详见[第三方声明](THIRD_PARTY_NOTICES.md)和随 Release 提供的 SBOM。

“OpenAI”“Codex”“Microsoft”“Windows”“Steam”“Wallpaper Engine”等名称和标识可能是其各自所有者的商标。本项目仅为说明兼容性而引用这些名称，不获得任何商标许可，也不暗示隶属、认可或支持。
