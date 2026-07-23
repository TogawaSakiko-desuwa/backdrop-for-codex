# 更新日志

本项目的所有重要变更记录在此文件中。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

## [1.1.0] - 2026-07-23

### Added

- 加入基于 WPF-UI 的 Windows 11 Fluent/Mica 工作台，提供本地图片/视频预览、代表性 Codex 可读性预览、拖放选择与最近媒体缩略图。
- 加入跟随系统、浅色、深色主题与高对比度跟随策略，并将纯 UI 偏好独立持久化到 `ui-settings.json`。
- 加入外观、安全与隐私、关于/重置设置页，以及可撤销的 CDP 风险确认流程。
- 将 App 状态、偏好与错误映射测试纳入解决方案；依赖真实本机环境的用例统一标记为显式选择的 `Integration` 测试。

### Changed

- 主窗口改为宽屏双栏、窄屏上下排列的响应式工作台，并区分编辑草稿、已保存目标与当前活动快照，避免应用失败后错误显示为已生效。
- 通知区域实现迁移到 WPF-UI.Tray；关闭主窗口现在隐藏到通知区域并仅在首次显示说明，退出仍执行完整清理。
- CI 现在执行格式检查、App/Core Release 构建、非集成测试与单文件发布形态检查。

### Fixed

- 修复通知区域图标在主窗口获得原生句柄前静默注册失败的问题；现在窗口关闭后仍可从托盘重新打开、恢复官方背景或退出。

### Security

- 风险确认改为持久化但可随时撤销；完整重置同时清理壁纸设置、最近记录、UI 偏好和经所有权核验的增强启动快捷方式。
- 发布 SBOM 和许可证目录显式校验并携带 WPF-UI、WPF-UI.Tray 与 CommunityToolkit.Mvvm。

## [1.0.0] - 2026-07-23

### Added

- 建立 Windows 11 x64、Store/MSIX Codex 专用的初始代码与仓库治理结构。
- 加入本地图片/静音循环视频背景、托盘控制、CDP 租约清理的基础能力。
- 加入同一用户会话的单实例命令转发、可撤销风险确认与桌面增强启动快捷方式。
- 加入 CI、CodeQL、依赖更新、可验证发布、SHA-256、SPDX SBOM 与构建来源证明流程。

### Changed

- 将媒体加载切换为 CSP 原生路径：宿主经本机 CDP 把用户明确选择、经文件头/容器签名校验、规范化并由只读 lease 锁定的文件绑定到自有隐藏文件输入，页面使用 `blob:` URL，不修改或绕过 Codex CSP。
- 主工作区改为透明；侧栏、顶栏和弹窗各保留一层玻璃，助手/用户消息气泡及活动行增加可读性底板，避免全局暗色遮罩和重复玻璃叠加。
- `LoopbackMediaServer` 暂作为只读 lease 的兼容过渡层保留，但 endpoint/token 不再注入 DOM，Codex 渲染器不再通过 HTTP 读取媒体；后续将收敛为 lease-only 组件。

### Fixed

- 修复回环 HTTP 媒体被当前 Codex CSP 拒绝时仍可能显示“壁纸已应用”的假成功。
- 修复根层、主区与嵌套导航重复铺设玻璃导致壁纸被多层遮挡的问题。
- 修复 Codex 启动早期主页面尚未挂载时首次点击失败或误判的问题：加入最长 10 秒的有界等待，并在超时或加载失败时清理准备态资源。
- 修复更换壁纸后重新走已被 CSP 拒绝的 HTTP 来源、导致现场修复未保持的问题。

### Security

- 将 CDP 与媒体服务限制为回环地址，并记录同一用户进程仍可攻击本地调试端点的剩余风险。
- 复验完整 MSIX 包名、激活 PID、进程启动时间、Windows 会话和监听器所有权；媒体服务保持已校验文件的只读句柄。
- 明确禁止 CSP bypass；关闭、更换或 lease 到期时移除媒体 `src`、撤销 `blob:` URL，并仅删除带有本项目 owner/generation 的节点和样式。

[Unreleased]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/tag/v1.1.0
[1.0.0]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/commit/ec1e464
