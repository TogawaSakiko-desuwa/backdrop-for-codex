# 更新日志

本项目的所有重要变更记录在此文件中。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

## [1.4.0] - 2026-07-26

### Added

- 正式开放 schema 2 的多方案工作区：可新建、复制、重命名、删除和切换方案，方案卡片只编辑 `Draft` 的 `Global` 绑定；不新增序列化字段，也不引入 Settings V3。
- 新增空媒体方案。手动应用空方案会保存目标并清理本应用拥有的媒体资源，显示官方背景；增强启动遇到空方案时以普通参数启动 Codex，不启用 CDP。
- 新增完整的工作区三态与类型化运行表面：`Draft`、`SavedDesired`、`ActiveSnapshot` 分别表示当前编辑、已原子提交和实际激活快照；运行表面明确区分 `Official`、`MediaActive`、`Faulted` 与 `Disconnected`。
- 新增带 revision 的类型化应用结果与进度：`MediaActive`、`Official`、`SavedButNotActivated`、`Superseded`、`Canceled` 和 `Failed`，旧 revision 的事件不能覆盖较新的界面状态。

### Changed

- 正常 Workspace、Application 与 runtime 链路全部改用经验证、规范化和深复制的 `SettingsV2` 快照；`SettingsV1` 只保留在迁移、原始 V1 备份恢复、降级兼容和对应测试中。
- 设置写入、Codex 会话和单槽播放池现在由单一 latest-wins actor 排序。Apply 保留一个运行项和最多一个待处理项；新请求替换旧待处理项并请求取消运行项，但会等旧任务安全退出后再触碰 runtime。
- 原子保存成功返回即成为持久化提交点。即使该 revision 随后被替代，`SavedDesired` 仍反映已提交快照；过期请求不得再激活、发布成功、释放新 lease 或清理新 generation。
- activation revision 与 injection generation 使用独立计数器。运行时等价的 Apply 可以提升 `ActiveSnapshot` 而不创建新 generation；运行时等价只由实际 Global 媒体与 fit/focus/glass/overlay 决定，空媒体方案的样式不改变官方背景状态。
- 单槽播放资源增加所有权 token。待处理 lease 直接释放，旧 revision 只能条件释放自己持有的 token；只有显式恢复官方背景、重置或退出可以无条件清空本应用资源。
- 顶部方案栏支持水平滚动、键盘选择、上下文菜单、自动化名称/选中状态、焦点恢复、高对比度、减少动态效果及 125%–200% 缩放；激活期间仍可编辑草稿、切换方案并再次应用。

### Security

- 继续保持既有安全验证顺序：官方包、进程、当前会话、严格 IPv4 回环端点、CDP browser/socket/target、唯一页面，再执行版本无关结构契约。安全失败或持续多目标歧义仍为零 DOM 探针。
- 媒体激活从已保存的同一 `MediaReference` 重新获取固定只读 lease；安全验证或注入开始后的失败继续失败关闭并报告真实清理结果。注入前文件失效且尚未触碰 Codex 时保留旧活动背景。
- schema 仍为 2；1.3.5 reader/writer 兼容 fixture 验证 1.4.0 不增加字段，并保留多方案、隐藏区域绑定、共享或孤立媒体引用及废弃兼容标识。

### Verification

- 非集成自动化套件扩展到 521 个，覆盖 V2 深快照和三种 equality、方案 CRUD/删除重绑、latest-wins checkpoint 与压力、提交点语义、lease 所有权、旧 revision/generation 过滤、空方案、类型化状态和关键 UI/可访问性边界。
- 环境相关 Edge/CDP、当前机器 Codex 身份、通知区域和 UI Automation 冒烟仍须在满足条件的 Windows 11 交互桌面逐项执行；未运行或缺少先决条件时必须记录为“未验证”，不得计为通过。

## [1.3.5] - 2026-07-26

### Fixed

- 修复当前 Codex 设置页真实 app-shell 层级与测试 fixture 不一致，导致右侧 `div.main-surface` 选择器零命中并继续显示原生黑底的问题；新规则按设置导航、主内容 viewport/frame 和精确内容画布逐层锚定，外层 main 与设置卡片保持原生承载层。
- 移除“已安排任务”搜索区域全宽 sticky 的深色玻璃底和 32px 伪元素渐变，仅保留搜索框自身的原生表面，避免高面板不透明度或零模糊配置下继续形成近黑色横带。

## [1.3.4] - 2026-07-26

### Fixed

- 修复当前 Codex 插件、已安排任务、站点、拉取请求和设置页面新增的不透明主表面与 sticky 搜索渐变遮住壁纸的问题；这些页面现在只在经审核的路由外壳上使用一层既有毛玻璃效果，卡片、控件、代码、diff 和编辑器继续保留原生承载层。
- 修复对话输入框上方显示进行中的文件更改摘要时出现整条黑色渐变的问题；仅清除摘要 portal 的独立渐变遮罩，文件统计按钮和输入框表面保持不变。

## [1.3.3] - 2026-07-25

### Changed

- 移除按 Codex 精确版本、受审版本带和通用探针选择表现能力的策略，改为两个程序内置、只读且与版本无关的结构契约：`global-baseline-v1` 独立验证全局背景所需的最小结构，`codex-shell-v1` 按实际页面证据声明玻璃和高级内容表面。相同 DOM fixture 不再因官方 Codex 版本号变化而得到不同契约或能力。
- Codex 四段版本仍用于已验证包身份的自洽检查和脱敏诊断，但不参与表现契约的候选、排序或决胜。高级契约恰好命中一个时使用该契约；零匹配或多重匹配在就绪窗结束后只启用 `global-baseline-v1`，baseline 失败则不注入。
- 结构契约在一次 generation 内锁定，五项能力继续独立且只能降级；证据恢复不会在同一 generation 中重新启用能力或切换契约。
- `LastCompatibilityProfileId` 保留 schema 2 序列化和 V1 迁移透传以兼容旧设置，但已废弃；运行时不再生成、更新或读取该字段作控制，V1 编辑门面和脏状态比较也不再让它参与行为决策。
- 诊断报告升级到 `schemaVersion: 2`，固定为 Environment、Runtime 和 Compatibility 三部分，只导出 Codex 版本、类型化安全结果、活动结构契约、匹配状态和逐项能力原因等白名单字段；不导出路径、包完整名、Publisher、进程/会话/端口、页面标题或 URL、DOM、选择器、原始异常或 CDP Detail。

### Fixed

- 移除 Codex 主内容区原生顶部渐变在透明壁纸上暴露出的首屏黑带；规则只清除经审核节点的 `background-image`，保留 edge-scroll 的原生 0.5px 分隔线和其他表面属性。
- 收紧 `codex-shell-v1` 的结构证据，只有经审核的 header 与 main viewport 锚点同时存在时才启用 Glass/Advanced；普通 `aside` 或单一锚点不再足以命中高级契约。
- 修正初始就绪窗把历史短暂歧义误当作终局歧义、以及单次探测可越过 10 秒上限的问题；终局现在以最新页面集合为准，持续多目标才失败关闭。
- 修正 Codex 冷启动头像浮层 `initialRoute=/avatar-overlay` 被误认作主工作页的问题；端点发现和注入前页面复验现在都会排除该辅助页面，避免有效图片或视频被误报为媒体载入失败或多目标歧义。
- 修正同 generation 的浏览器重连会清空活动契约与能力锁的问题；同代继续只允许能力降级，跨代才重新选择契约。
- 安装包发现不再按 Codex 版本排序或选择最高版本；多个完整验证候选现在一律报告歧义并失败关闭。

### Security

- 包、Publisher、AppId、进程、当前会话、PID/启动时间、监听器所有权、严格 IPv4 回环、CDP browser/socket/target 和唯一页面验证继续严格失败关闭。只有安全目标验证成功后才会运行结构证据探针，任何安全失败或持续多目标歧义都不会执行 DOM 探针。
- 1.3.3 作为既有能力的安全边界与兼容模型重构直接进入 Stable；这是一次明确的 Preview 例外，后续新增的上游敏感能力仍须先发 Preview 再进入 Stable。

### Verification

- 实施前非集成测试基线为 390 个。512 MiB/8 GiB 边界测试改用声明长度只读测试流的修复已经包含在 1.3.1，本版本沿用该基线，不重复把它记作 1.3.3 的改动。
- 当前实现的 436 个非集成测试全部通过；显式启用的 3 个真实 Edge/CDP 用例同时验证冷启动及 `visible` / `full-bleed` / `hidden` 状态和节点重建后的顶部渐变均被清除、无关渐变与 0.5px 分隔线保留、四种 shell 锚点组合、同 DOM 下跨版本契约一致，以及 CSP 受限媒体加载；另有 2 个当前机器用例验证官方 Codex 包和运行中进程身份。

## [1.3.2] - 2026-07-25

### Fixed

- 修复右侧 launcher 尚未创建 tabpanel 时仍由多层不透明 primary surface 遮住壁纸的问题。空态现在只为经审核的最外壳添加一层玻璃，并清除 tabs root 及其 primary chrome 后代背景；审阅、终端、浏览器、文件和侧边任务入口卡片继续保留原生深色表面。
- 打开任意受控右栏 tabpanel 后，空态规则会立即停止匹配并由现有内容态规则接管，无需重新应用壁纸；同级非 launcher primary surface、左栏、错误 controller、编辑器和内容表面均保持隔离。
- 修复 edge-scroll 下层 context 标题行的突兀黑色玻璃条，使其背景、滤镜和边框透明并与主体连续；上层全局标题栏、右侧槽位和关闭按钮继续保留原有表面与交互。

## [1.3.1] - 2026-07-25

### Fixed

- 修复从 Home 进入 Conversation 后高级内容表面被永久误降级的问题。高级规则现在以核心工作区和所需 CSS 选择器能力为准；只在特定路由出现的聊天、活动卡片和右栏节点缺失时安全地保持不匹配，不放宽包、进程、回环 CDP 或目标身份验证。
- 适配当前 Codex 右侧面板的嵌套不透明 tabs root 与 toolbar，只清除经审核的右栏 chrome；固定顶栏玻璃收敛到主内容 context surface，不再在视觉上覆盖右侧标题和关闭按钮。
- 精确移除对话输入区外围的原生向上渐变，同时恢复对话气泡、活动卡片及右侧内容壳的预期玻璃效果。
- 媒体大小边界测试改用声明长度的只读测试流，不再为 512 MiB 与 8 GiB 上限创建同等大小的临时文件。

## [1.3.0] - 2026-07-24

### Added

- 引入 schema 2 领域契约：`WallpaperProfile`、`MediaReference`/`MediaSourceKind`、稳定的 `SemanticRegion`、区域绑定、声音/音量和性能策略。1.3.0 的可见运行时仍只启用本地文件、`Global` 档案、静音和单个活动媒体；功能区域、Wallpaper Engine、模板、音频输出和保温池尚未开放。
- 引入 `IWallpaperSourceProvider`、本地文件提供器、`IMediaLease`、`IWallpaperRuntime` 与单槽 `IPlaybackPool`，把来源解析、文件校验、lease 所有权和运行时协调分离。
- 新增设置 V1 → V2 自动迁移：发布 V2 前按原始字节创建并校验只读 `settings.v1.backup.json`，完整迁移媒体、最近记录、构图、遮罩、玻璃、风险确认和兼容标识；声音明确关闭、音量为 50%、性能策略为自动。
- 新增设置恢复状态：损坏、超大、不可读取、备份冲突或迁移失败时要求显式恢复，不再用默认值覆盖；高于 schema 2 的文档进入未来版本只读状态。
- 新增五项独立兼容能力：全局背景、功能区域识别、玻璃样式、音频和高级内容表面。功能区域识别与音频在 1.3.0 中明确标记为未实现。
- 新增用户主动触发的本地诊断 JSON 导出。报告采用类型化字段白名单，只包含应用/系统环境、运行阶段和能力状态，不包含路径、文件名、页面标题、完整 URL、DOM、聊天、设置、标识符或散列，也不会自动上传。
- 新增手工、本地、仅报告结果的媒体 lease/单槽激活基准工具；输出不含媒体路径，不连接 Codex，也不执行发布门槛判定。

### Changed

- 移除 `LoopbackMediaServer`、Kestrel 和临时媒体 HTTP 监听器。媒体文件现在由本地来源提供器通过同一个只读句柄解析最终本地普通文件、核验身份/格式/大小并持有 lease，再由 CDP 文件输入和页面 `blob:` URL 加载；公开的路径重开型媒体检查接口一并移除。
- 本地图片上限设为 512 MiB、单边 32,768 像素和总计 33,554,432 像素，视频上限设为 8 GiB；PNG/JPEG/WebP 尺寸只从调用方持有的固定可寻址流解析，界面预览同时限制解码宽高；拒绝网络、目录、设备以及解析后不属于受支持本地卷的路径。
- Codex 精确版本号不再单独决定准入。`26.715.10079.0`、`26.721.3404.0` 与当前 `26.721.3996.0` 使用精确受审探针包；其余 `26.721.3404.0 <= 版本 < 26.722.0.0` 使用显式受审版本带探针，使同一功能带的小补丁在实时结构验证通过时继续启用玻璃和高级内容表面；范围外官方 Store/MSIX x64 版本使用只声明全局背景的保守通用结构探针。安全验证仍是硬失败，任何探针都不会放宽包、进程、会话、监听器或目标身份要求。
- 全局背景、玻璃和高级内容表面按结构探针结果独立启用或降级；某能力在一次 generation 内降级后不会自行恢复，下一 generation 才可重新评估。探针选择按精确版本、受审版本带、通用的顺序进行，精确或版本带探针的禁用结果不会回退到通用探针。
- 精确与版本带探针现在分别核验全局根结构、玻璃壳层锚点和高级内容锚点；某个可选锚点漂移只降级对应能力。通用探针禁用 Glass/Advanced 时使用明确的兼容策略原因，不再误报为功能尚未实现。
- 适配 `26.721.3996.0` 右侧 Markdown 内容壳使用的 CSS 模块类名，同时保留旧版 `.markdown` 选择器。
- 收紧远程工作页分类：`chatgpt.com` 只接受具有完整路径段边界的 `/codex`，`codex.openai.com` 只接受根入口或同一 `/codex` 边界；认证路径、路径穿越、反斜杠和近似前缀均拒绝。
- 初始页面选择在最长 10 秒内只接受唯一合格工作页；多个候选持续存在时拒绝本次应用并清理准备态资源，不再向多个页面注入。
- 注入实现拆分为生命周期、媒体和样式模块，同时保留 owner/generation 所有权、加载成功判定与幂等清理约束。
- 设置保存统一使用严格 schema 2 校验和同目录原子替换，并在发布前再次逐字节复核预期原文；V2 未知字段被拒绝，重复迁移保持幂等，未由 1.3.0 兼容界面编辑的档案不会被 V1 遮罩上限或最近记录容量隐式改写。
- V1 兼容界面遇到有效但尚不可表达的 V2 `Global` 来源时进入保护性只读状态，不再以空媒体或旧模型覆盖；完整重置会在明确提示后永久删除 V1 备份。
- 开发 SDK 基线更新为 .NET `10.0.301` 并允许同 feature band 的 `latestPatch`；CI 同时验证 `10.0.301` 和 `10.0.302`，正式发布固定使用 `10.0.302`，并用单独安装的 .NET 8 SDK/runtime 托管锁定的 SBOM 工具。

### Security

- 把版本兼容与安全身份判定分离，但继续严格验证 Windows 11 x64、官方包名/包系列、由身份字段构造的完整包名、应用 ID、进程、当前会话、PID、启动时间、监听器所有权、IPv4 回环端点和目标元数据；任何安全失败都会关闭全部能力并拒绝连接。
- reparse/symbolic-link 输入在已打开句柄上解析最终路径，并固定卷序列号与文件索引；校验与播放共用 lease，缩小校验后替换窗口。
- 设置迁移在可验证的 V1 原始备份落盘前不会覆盖现有文档；恢复状态和未来 schema 均禁止隐式写回。
- 诊断导出保持手动、本地、无遥测，并从数据结构上限制为非敏感白名单字段。
- 页面目标不再因标题和任意本机内容端口而被信任；MSIX `file:` 目标必须位于实际观测包根目录的精确入口。媒体上传绑定到准备脚本直接返回的元素句柄，并在上传前重新核验页面，使中途导航无法用同 URL 的伪造输入接收文件。

## [1.2.1] - 2026-07-24

### Fixed

- 新增官方 Store/MSIX Codex `26.721.3404.0` 的精确受审兼容配置，同时继续支持 `26.715.10079.0`。
- 将单一兼容配置改为按精确版本索引的不可变目录；包、进程、会话、回环端点和页面目标仍必须与命中的配置完全一致，未知版本继续失败关闭。

## [1.2.0] - 2026-07-24

### Added

- 图片和视频新增“完整显示”“裁剪填满”“拉伸”三种适配模式；裁剪填满支持直接拖动焦点、键盘微调和一键恢复居中。
- 深色与浅色主题新增独立的壁纸遮罩，分别使用黑色和白色，并可在 0–60% 范围内调整。

### Changed

- 右侧所有详情页签统一使用受限作用域的玻璃效果；代码块、文件编辑器、diff 和表格等内容承载面继续保留主题底色。
- 设置继续使用 schema 1，旧 `Contain`/`Cover` 配置无需迁移；`Cover` 的界面名称更新为“裁剪填满”，并新增向后兼容的 `Stretch` 值。
- 适配模式、裁剪焦点、主题遮罩和右侧玻璃化均纳入自动化测试、Release 构建、格式检查及单文件发布验证，不要求手工验收。

## [1.1.1] - 2026-07-24

### Fixed

- 首页四张顶层建议卡片现在使用主题自适应的半透明玻璃底板，透明度与模糊跟随现有面板设置，同时保留原有悬停、键盘焦点、禁用态和高对比度行为。
- 此修复不新增设置项或持久化字段，现有 schema 1 配置无需迁移。

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

[Unreleased]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.4.0...HEAD
[1.4.0]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.3.5...v1.4.0
[1.3.5]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.3.4...v1.3.5
[1.3.4]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.3.3...v1.3.4
[1.3.3]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.3.2...v1.3.3
[1.3.2]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/tag/v1.2.0
[1.1.1]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/tag/v1.1.1
[1.1.0]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/tag/v1.1.0
[1.0.0]: https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/commit/ec1e464
