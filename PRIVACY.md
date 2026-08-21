# 隐私说明

最后更新：2026-08-21

Backdrop for Codex 是在用户计算机上运行的开源桌面伴侣。本说明描述项目自身的设计；它不覆盖 OpenAI Codex、Microsoft Store、Windows、GitHub 或其他第三方服务的数据处理。

## 核心结论

- 不包含遥测、广告、用户画像或项目自有崩溃上报。
- 不运行项目自有云服务，不代理 Codex 与 OpenAI 的通信。
- 不以功能需要为由读取、保存或上传聊天内容。
- 媒体与设置在本机处理；本地文件与 Wallpaper Engine Local Project 标识包含绝对路径，因此可能泄露用户名、目录结构或文件命名信息。手动选择的 Wallpaper Engine 安装根不持久化。

## 本机处理的数据

### 设置

设置保存在 `%LOCALAPPDATA%\CodexWallpaper\settings.json`。其中 `CodexWallpaper` 是升级兼容使用的目录名。程序会保存：

- 媒体目录中的来源类型、稳定来源标识、最近已知内容类型和受限长度的最近已知显示名称；本地文件与 Wallpaper Engine Local Project 使用绝对路径，Workshop 项目使用 PublishedFileId；
- 多个方案及其 UUIDv7 标识、当前 `Global` 方案引用、稳定语义区域绑定和最近使用的最多 8 条媒体引用；
- 适配模式、裁剪焦点、深浅主题遮罩、玻璃参数、声音开关、音量和性能策略；
- 稳定语义区域与档案的绑定、CDP 风险确认，以及仅为旧设置透传的废弃 `LastCompatibilityProfileId`。

本地视频与 Wallpaper Engine Image / Video 在 Codex 中保持静音。Scene / Web 在 Wallpaper Engine 已由用户启动时使用动态像素管线；项目可能直接通过 Wallpaper Engine 播放声音，Backdrop 不枚举、静音或改变其音频会话，也不把音频传入 Codex。

设置会保留 Wallpaper Engine 项目最近已知的内容类型和受限显示名称，以便项目暂时缺失时仍能在方案中显示。它不会保存 Steam 根目录、Workshop 派生目录、缩略图路径、进程或窗口标识、渲染器状态或动态流状态。

界面偏好另存于同一用户范围目录下的 `ui-settings.json`，包含主题、托盘提示状态和 Web wallpaper 隐私确认。手动选择的 Wallpaper Engine 安装位置只在当前运行中使用，不写入设置、壁纸方案或诊断导出；重启后程序会重新自动定位。

首次读取 schema 1 或 2 时，程序先把原文件的精确原始字节分别写入同目录的只读 `settings.v1.backup.json` 或 `settings.v2.backup.json` 并核验一致性，再原子发布迁移后的 schema 3。迁移只转换元数据，不扫描 Steam、Wallpaper Engine 或媒体文件。备份包含旧文件原本持有的同类路径和偏好；“只读”用于防止普通误改，不是对当前用户其他进程的访问控制。损坏、不可读取、超大、迁移失败或备份冲突会进入显式恢复状态，不会以默认值静默覆盖。高于 schema 3 的文件只识别为未来版本只读状态，不会被当前版本自动保存或降级覆盖。

这些数据不会由本项目上传。能够以当前 Windows 用户身份读取设置或备份文件的其他进程，可能看到其中的路径和 Workshop PublishedFileId。完整重置会删除本项目的设置及 V1/V2 备份；仅卸载或删除程序目录不一定删除用户范围数据。显式从 V1 备份恢复会再次生成 V3，但不会改写备份本身。

### 本地媒体与 Wallpaper Engine 项目

程序只在用户选择后访问 PNG、JPEG、WebP、MP4 或 WebM 文件。宿主打开所选本地绝对路径，确认它是受支持本地卷上的普通磁盘文件，并核对格式和大小；图片上限为 512 MiB，视频上限为 8 GiB。网络、目录、设备和解析后落到不受支持卷的路径会被拒绝。文件使用期间保持只读句柄，因此编辑、替换、重命名或删除可能暂时被 Windows 拒绝；关闭或更换壁纸后句柄会释放。

打开 Wallpaper Engine 来源库或解析已保存引用时，程序会在本机读取 Steam 与 Wallpaper Engine 的安装元数据，并扫描已安装 Workshop 目录以及 `projects/myprojects`、`projects/backup`。来源库会按需读取项目预览图，并在当前进程内最多缓存 64 项、合计 48 MiB；缓存不写入磁盘，也不会由项目上传。它不浏览在线 Workshop，不自动订阅或下载，也不修改 Steam 配置。Application / Unknown 项目会被过滤并拒绝执行。仓库与 Release 不分发 Wallpaper Engine 二进制、Workshop 内容或用户项目。

Wallpaper Engine Image / Video 的入口文件仍通过上述固定只读文件 lease 和直接 `blob:` 管线处理，不要求 Wallpaper Engine 进程运行。Workshop 的持久身份是 PublishedFileId，每次使用时重新定位实际目录；Local Project 的身份是本机绝对项目根。取消订阅、项目删除或 Wallpaper Engine 缺失时，设置只保留 last-known 类型与显示名称，Apply 会禁用；内容重新出现后可按稳定身份重新解析。

Scene / Web 只在 Wallpaper Engine 已经运行且项目窗口通过验证时激活。Backdrop 只捕获该窗口的像素，不捕获桌面，不向第三方 Web wallpaper 传递脚本、键盘/鼠标输入、音频或 Codex 内容，也不建立媒体 HTTP 服务。项目声音可能由 Wallpaper Engine 直接播放；Backdrop 不会自动启动 Wallpaper Engine，也不枚举、静音或改变 Wallpaper Engine 音频会话。

首次应用 Web wallpaper（包括增强快捷启动）前，Backdrop 会明确说明第三方项目可能自行联网并播放声音。只有用户确认后才继续；取消、关闭提示或没有可用提示界面都会失败关闭。该确认只作为本机 UI 偏好保存，不写入可迁移的壁纸方案。

为在崩溃后关闭本项目创建的 pop-out，程序会在 `%LOCALAPPDATA%\BackdropForCodex\wallpaper-engine-owned-windows.json` 暂存最多 32 个高熵窗口名称。该恢复日志不包含项目路径、PublishedFileId、PID、HWND、进程信息或 renderer 状态；只有精确关闭并证明窗口消失后才删除对应记录。

Codex 页面当前的 CSP 不允许从回环 HTTP 地址加载这些图片或视频。本项目不修改、不放宽也不绕过该 CSP。宿主经本机 CDP 把已校验、由 lease 锁定的文件绑定到页面内本项目拥有的隐藏文件输入，页面再生成 CSP 原生允许的 `blob:` URL。页面脚本在这段时间可以访问所选文件的内容，以及浏览器提供的文件名、大小、MIME type 和修改时间；它不能取得宿主传给 CDP 的完整绝对路径。宿主本身仍知道该路径，并按上一节所述保存设置。

媒体 lease 组件不运行 Kestrel、不监听临时 HTTP 端口，也不生成媒体 endpoint 或媒体访问令牌。媒体文件内容不经过 HTTP、项目自有服务或维护者控制的基础设施。详见 [v1.5.0 威胁模型](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/blob/v1.5.0/THREAT_MODEL.md)。

### CDP 与 Codex 页面

项目通过回环 CDP 给经过验证的 Codex 主工作窗口添加表现层。程序不解析或保存聊天内容，也不从页面读取媒体的完整本机路径。关闭、更换或失联时会移除本项目创建的媒体节点和样式。CDP 能够控制页面，请只使用本仓库发布的可信构建。

程序会先验证官方包、进程、当前会话、严格 IPv4 回环端点和唯一工作页。原始 DOM、页面 URL 与内部选择器不会被保存或导出。

### 日志和诊断信息

程序会在本机显示运行状态、版本、连接阶段和错误信息以便排错；这些信息不会由项目上传。项目自有日志不记录聊天正文或媒体绝对路径。系统异常或内存转储仍可能包含敏感数据，不应公开上传。

“设置 → 关于”中的诊断导出只在用户明确操作、确认说明并选择保存位置后创建本地 JSON 文件；程序不会在后台生成、定期收集或自动上传它。`schemaVersion: 2` 报告固定分为 Environment、Runtime 和 Compatibility，只包含：

- 报告 schema 版本；
- 应用版本、Windows 版本、进程架构和 .NET 运行时描述；
- 壁纸运行阶段、是否活动、是否暂停，以及动态壁纸能力或失败原因的枚举代码；
- Codex 四段版本、类型化安全阶段与终态原因；
- 活动结构契约、匹配状态，以及五类兼容能力的枚举状态与枚举原因。

报告结构没有媒体路径或文件名、包完整名、Publisher、进程/PID/会话/端口、页面标题、完整 URL、DOM、选择器、聊天、设置内容、CDP Detail、用户/设备标识符、散列或原始异常字段。它也不是日志、设置或内存转储的打包器。用户选择的保存目录仍可能由 OneDrive、企业备份或其他第三方软件同步，分享前应检查实际文件。

公开分享日志、截图或设置文件前，请删除用户名、绝对路径、聊天、令牌和其他敏感内容。内存转储可能包含完整进程内存，请勿公开上传。

## 网络通信

项目自身正常运行所需的网络控制连接仅限本机严格 IPv4 回环 CDP。直接媒体通过页面内 `blob:` URL 读取，Scene / Web 的捕获画面通过 CDP 投递到页面拥有的 MSE buffer；两者都不经过项目自有 HTTP 服务，伴侣也不为媒体启动任何网络监听器。退出伴侣会释放 lease，但 Codex 自身持有的 CDP 端口会持续到 Codex 完全退出。

第三方 Wallpaper Engine Web wallpaper 可能按项目作者的实现自行联网，Wallpaper Engine、Steam、Codex 桌面应用、Windows、Microsoft Store、GitHub CLI 或操作系统组件也可能按各自设置联网。这些通信不是由本项目代理或控制，适用相应提供方的政策。Backdrop 不向 Web wallpaper 提供 Codex 内容或输入，但无法把像素捕获误解为阻止第三方项目自身联网。

## 保留、删除和备份

本项目维护者不会收到上述本地数据，因此无法代用户访问、导出或删除。“恢复官方背景”会停止当前媒体并清理本项目创建的页面资源，但不会删除已保存方案。设置和 V1/V2 备份会保留到用户执行完整重置或自行删除。用户导出的诊断报告保存在用户选择的位置，不由重置功能搜索或删除。系统备份、企业漫游配置或同步软件可能另行保留副本。

首次媒体激活成功后，程序会尝试在当前用户桌面创建或更新 `Codex（动态背景）.lnk`，其参数为 `--launch`。删除该快捷方式可移除入口；取消设置窗口中的风险确认会阻止媒体方案下次自动增强启动。空方案通过该入口启动时只验证并普通启动官方 Codex，不附加调试参数，也不建立 CDP。快捷方式不属于系统级启动项，也不会让程序随 Windows 自动启动。若伴侣已在托盘，第二实例只通过当前用户、当前 Windows 会话的本地命名管道转发显示或启动命令。

## 儿童、出售和共享

本项目不面向儿童收集数据，不出售个人信息，也不与维护者控制的广告商或数据经纪商共享数据，因为项目没有收集管道。

## 变更

本说明会随数据处理方式的变化更新。版本历史可通过 Git 查看。

隐私或安全问题请按 [v1.5.0 安全策略](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/blob/v1.5.0/SECURITY.md) 私下报告。
