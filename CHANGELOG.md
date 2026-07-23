# 更新日志

本项目的所有重要变更记录在此文件中。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

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

[Unreleased]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/tag/v1.0.0
