# Backdrop for Codex 1.5.0 便携版

适用于 Windows 11 x64，为官方 Microsoft Store / MSIX x64 Codex 桌面应用添加自定义背景。

## 1.5.0 新增

- 来源库可浏览本机已安装的 Wallpaper Engine Workshop 和 Local Project，并按类型或来源筛选。
- 新工作台集中管理背景方案、来源、最近使用和预览；旧版方案会在首次启动时自动迁移。
- Image / Video 项目可直接播放；Scene / Web 需先启动 Wallpaper Engine，并会在遇到可恢复故障时逐级降低画质。Backdrop 不会自动启动 Wallpaper Engine。
- Scene / Web 项目可能通过 Wallpaper Engine 播放声音，Web 项目可能联网；Application 和未知类型不会执行。

## 快速开始

1. 将 ZIP 完整解压到一个普通文件夹，不要直接在压缩包内运行。
2. 完全退出所有 Codex 进程。
3. 运行 `BackdropForCodex.exe`。
4. 新建或选择背景方案，添加本地图片、视频或已安装的 Wallpaper Engine 项目，然后点击“应用并启动 Codex”。
5. 在随后出现的 CDP 风险提示中阅读并确认安全边界；应用随后会启动 Codex 并应用背景。
6. 需要撤销时，在应用内点击“恢复官方背景”。

关闭主窗口后，应用会继续在通知区域运行。要完全退出，请使用通知区域图标的退出命令。

## 文件说明

- `BackdropForCodex.exe`：主程序，无需安装 .NET 运行时。
- `LICENSE`、`NOTICE`：项目许可证与版权声明。
- `PRIVACY.md`：隐私说明。
- `THIRD_PARTY_NOTICES.md`、`licenses/`：第三方软件声明与许可证。

卸载前，先在“设置”的“危险区”选择“重置应用”，以恢复官方背景并删除应用设置和由应用创建的桌面快捷方式；再从通知区域完全退出并删除解压目录。若重置报告部分失败，请手动检查 `%LOCALAPPDATA%\CodexWallpaper` 和桌面上的 `Codex（动态背景）.lnk`。

项目主页与问题反馈：<https://github.com/TogawaSakiko-desuwa/backdrop-for-codex>

Backdrop for Codex 是独立社区项目，与 OpenAI 或 Microsoft 无隶属、认可或赞助关系。
