# 安全策略

Backdrop for Codex 会连接高权限的本地调试接口。请先阅读[威胁模型](THREAT_MODEL.md)，不要把“仅回环”理解为完整隔离。

本文适用于 Backdrop for Codex `v1.5.1`。

## 支持的版本

| 版本 | 安全更新 |
| --- | --- |
| `v1.5.x` | 支持 |
| 旧版本、Fork、自行修改的构建 | 不保证 |

安全修复通常发布到最新稳定版本；若漏洞影响范围不同，安全公告会另行说明。

## 私下报告漏洞

请不要通过公开 Issue、Discussion、PR、日志附件或社交媒体披露未修复漏洞。

1. 打开仓库的 **Security** 页面，选择 **Report a vulnerability**，创建私密安全报告。
2. 如果仓库未启用私密漏洞报告，请使用维护者 GitHub 个人资料中公布的私密联系方式；只需先请求安全联系渠道，不要在公开内容中附带漏洞细节。
3. 包含受影响提交/版本、Windows 与 Codex 版本、复现步骤、预期影响、概念验证和建议缓解措施。
4. 删除聊天内容、访问令牌、用户名、绝对文件路径及其他个人数据。需要样本时优先构造最小化的虚假数据。

可合理期待的非约束性目标是：7 天内确认收到，14 天内给出初步分级，此后至少每 30 天更新一次。复杂问题、维护者可用性或上游依赖可能延长处理时间。

## 优先关注范围

- 接受或启动非严格 IPv4 回环的 CDP 端点，或意外重新引入媒体网络监听器；
- 未验证调试目标、包身份、进程或 WebSocket 端点而连接错误目标；
- 通过媒体路径、设置、日志、HTML、CSS 或 JavaScript 产生的注入、目录穿越或数据泄露；
- reparse point、符号链接、校验后替换或文件身份混淆，使未选择或未校验的文件进入渲染器；
- Steam / Wallpaper Engine 安装定位、VDF/ACF/`project.json` 解析、Workshop PublishedFileId 或 Local Project 根目录验证被绕过，造成路径穿越、reparse 逃逸、重复身份混淆或执行 Application 项目；
- 仅凭窗口标题、CLI 退出码或 HWND 捕获 Wallpaper Engine，未核验 PID、启动时间、可执行路径和非干扰放置；Wallpaper Engine 返回项目路径时未比对其与所选项目的一致性，或把未返回项目路径误当成项目身份已得到证明；误用全局 pause/play/mute 影响用户现有桌面壁纸；
- Windows Graphics Capture 捕获桌面或错误窗口、关闭失败后恢复 pop-out 到屏幕、旧 generation 清理新动态资源，或 MSE 背压导致无界内存增长；
- 多个合格页面目标存在时仍进行注入，或结构能力探针被用来绕过安全身份失败；
- 能够读取或导出 Codex 聊天、凭据或会话状态的非预期代码路径；
- 诊断导出包含白名单之外的路径、标题、URL、DOM、聊天、设置、标识符或散列，或未经用户主动操作发送数据；
- 设置迁移未先保留并核验原始 V1/V2 字节、自动覆盖恢复/未来 schema 状态，或让损坏设置进入运行时；
- 并发 Apply、Cancel、恢复官方背景、重置或恢复备份导致旧 revision 覆盖新 `SavedDesired`/`ActiveSnapshot`、发布错误成功状态或越过排他 barrier；
- playback ownership 失效，导致旧 revision 释放较新的媒体 lease/槽位或清理不属于自己的 injection generation；
- 提权、任意命令执行、任意文件写入或不安全自动启动；
- 构建、依赖、GitHub Actions、发布物、SBOM 或 attestations 的供应链问题；
- 租约清理失败，导致注入内容在伴侣退出后继续存在。Codex 自身的调试端口按设计会持续到 Codex 完全退出，这一点本身不是漏洞。

## 通常不在范围内

- OpenAI Codex、Microsoft Store、Windows、Chromium/.NET 或第三方依赖自身的漏洞；请同时按其上游流程报告。
- Steam、Wallpaper Engine 或用户安装的第三方 Workshop / Web wallpaper 自身的漏洞；如果 Backdrop 的验证、隔离或清理明显扩大影响，仍欢迎私下报告。
- Windows 10、Arm、Win32/便携版 Codex、网页或 CLI 等明确不支持环境。
- 已拥有管理员、内核或当前用户任意代码执行能力的攻击者所能完成的通用操作；但如果本项目明显扩大影响，仍欢迎私下报告。
- 仅造成外观差异且没有安全或隐私影响的问题。

## 安全边界

- 只连接经过验证的官方 Store/MSIX Codex 和严格 IPv4 `127.0.0.1` 调试端点；不接受非回环、重定向或异常调试 URL，也不为媒体启动网络监听器。
- 使用普通用户权限运行，不修改或重新签名 Codex 包，不写入系统保护目录。
- 本地媒体和 Wallpaper Engine Image / Video 入口会验证最终文件、项目根、格式和大小，并在使用期间保持只读句柄；路径和文件名不会拼入可执行脚本或不受控 URL。
- Wallpaper Engine 来源只读取经过验证的本机安装、已安装 Workshop 和 Local Projects；Application / Unknown 不执行，也不会浏览在线 Workshop、订阅、下载或修改 Steam 配置。
- Scene / Web 要求 Wallpaper Engine 已经运行，只捕获经过验证的独立项目窗口，不捕获桌面，不使用全局播放/静音命令，也不传递第三方脚本、输入、音频或 Codex 内容。Backdrop 不枚举、静音或改变 Wallpaper Engine 音频会话；项目可能播放声音，第三方 Web 项目也可能自行联网。
- Codex 包、进程、当前会话、监听器和唯一工作页的安全验证先于页面兼容判断；页面结构不能覆盖安全失败。
- 不修改或绕过 Codex 内容安全策略，不读取聊天，不添加遥测或项目自有云服务。
- 旧设置迁移前保留并核验原始 V1/V2 备份；损坏、冲突或未来版本设置不会被默认值静默覆盖。
- 日志不记录聊天或媒体绝对路径；诊断报告只在用户主动导出时生成，使用固定字段白名单且不自动上传。
- 发布物提供 SHA-256、SPDX SBOM 与 GitHub 来源证明。

## 协调披露

维护者确认问题后会评估影响、准备修复和验证证据、申请适用的 CVE，并在修复可用时发布公告。请在维护者确认修复窗口前保密。我们会在公告中尊重报告者的署名偏好；未经同意不会公开报告者身份。
