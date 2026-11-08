# 元枢当前架构（V0.8.0 合并候选及历史冻结背景）

> 2026-11-05 新视觉路径：`ModelDesktopVisualPlanner` 将本次明确许可的单窗口 PNG 通过冻结 Qwen 路由提议标明 normalized1000 的相对坐标，本机按可信图片尺寸转换为有界像素坐标；`DesktopVisualTaskExecutionService` 仍属于原 Session/Turn，复用 TaskObservation/TaskAction。`WindowsDesktopVisualTargetPreview` 在专用 STA 显示完整图片与精确目标框，实际输入由有 JobObject 所有权的短命私有 worker 执行，点击前检查窗口、几何、DPI、完整像素、前台和落点。鼠标移动后再核对像素，按下/抬起同批发送。图片在内存中清理，不进入公共 IPC、历史或 SQLite；非视觉模型拒绝图片，不自动换路由。新 OCR 结果仍需用户核对任务是否完成。代码与验收进度见 [视觉桌面任务](docs/VISUAL_DESKTOP_TASKS.md)。

> 2026-11-05 通用任务确认：`TaskObservation` 和 `TaskAction` 与搜索许可同样要求全文真实可见。灵动岛的原生可见性心跳与精确内容版本展示回执分离；心跳不建立初始握手、不登记版本、不满足展示等待、不授予语音确认。长说明无法完整展示时保留原 Turn，改为“查看确认”的手动完整内容入口；隐藏或断开仍暂停语音。文本覆盖确认展示完整旧值/新值，其它调用保留输入/选择上下文；最终执行仍比较完整快照。候选安装和真实验收状态见 [通用桌面任务](docs/GENERAL_DESKTOP_TASKS.md)。

> 2026-11-04 通用桌面任务：`DesktopTaskRequest` 识别明确软件与操作包络，`SessionCoordinator` 在原 Turn 内协调许可、执行、取消与结果。`ModelDesktopTaskPlanner` 使用冻结的 provider/model 和版本化 Prompt 提议单步有限 JSON；`DesktopTaskExecutionService` 在本机重验目标、快照、效果范围和一次性能力。短命 Host worker 使用 UIA、私有匿名管道和 kill-on-close JobObject，读取与动作前验证窗口身份/前台，动作前复核完整上下文指纹；不使用鼠标坐标或键盘兜底。完整许可详情仅保留在活动 Turn 内存，经私有 IPC 展示；SQLite 只存固定等待摘要和步骤哈希。详见 [实现与验收](docs/GENERAL_DESKTOP_TASKS.md)。

> 2026-11-05 Office 原生核对：可信 Word 纯正文和 Excel 单格通过指定 HWND 下的 NativeOM 对象读取、写入和回读，不使用全局活动文档或工作簿。Excel 的 UIA ValuePattern 曾返回与工作表不符的成功值，现有修正以 Window/Workbook/Sheet/Range 身份、A1 地址、GridItem 行列和写前旧值共同绑定，结果读取 Range.Value2。普通文字及可精确回读的数字之外，公式、合并格、保护、宏及结构化表格不开放写入；具体已通过版本和范围以验收记录为准。

> 2026-11-04 应用语音纠正：规划器与桌面文件解析共用有限句首纠正规范化；原 Turn 文本保持。应用目录完整登记名优先，英文部分名称在原始名称的词边界匹配；一处拼写差异只生成无执行权限的候选名称。完整桌面枚举没有文档匹配时，Host 保留应用澄清；有真实文档则沿用文件确认。Steam 打开动作支持精确安装根下两种固定 helper 布局，读取 OS 直接父关系并校验各自句柄创建时间、路径和存活状态，激活前后重验身份；仅选唯一 `SDL_app / Steam` 主窗口，身份变化即停止。搜索仍使用原 `MatchesRegisteredTarget` 合同。目标绑定、语音单次授权、取消与协议/数据库保持原合同，见 [修复与验收](docs/VOICE_APPLICATION_CORRECTIONS.md)。

> 2026-11-03 连续音乐任务：`MusicChart` 贯穿规划和目标摘要，固定榜单解析器只提供公开元数据；单个 `ApplicationSearchWorkflow` 承担打开、搜索和后续播放，仍由唯一 `SessionCoordinator` 协调。已绑定应用的搜索入口允许仅标题变化，逐步重验应用、进程生命周期、路径、窗口与前台；普通窗口及捕获身份合同不放宽。播放使用已绑定程序的固定数字曲目协议，原生页脚验证状态变化，Host 与 Skill 均核验音乐收据。详见 [实现与验收边界](docs/CONTINUOUS_MUSIC_WORKFLOW.md)。

> 2026-11-02 简短应答：`VoiceReceptionGate` 在 Direct 下区分本机 Call 和精确 Stop；呼唤不提交 Host。`LocalVoiceReplyPresentation` 仅保存绑定当前状态的临时界面反馈，不是另一份会话或任务。普通/所选记忆统一 `chat.general@3`，记忆预览与执行哈希一致；旧提示文件不变。视觉许可播报挂起保留已消费/撤销记录，新的可见准备才绑定新语音代次。细节见 [简短语音应答](docs/CONCISE_VOICE_RESPONSES.md)。

> 2026-10-25 搜索许可分流补充：仅接近完整搜索许可的句尾“搜索”可作为名词保留在本地澄清，前面的搜索/打开或附加操作仍视为新任务。新增精确短句“允许这次搜索 / 拒绝这次搜索”，原长句兼容；近似匹配不进入授权。结果提示明确请勿切换或点击其它窗口，未改变焦点行为、逐窗许可或 Session 状态。

> 2026-10-25 视觉许可语音分流：等待逐窗许可时，中间转写暂缓替换原请求，明确停止仍优先；完整新指令沿用原取消与替换流程。接近许可短句的错误转写仅更新当前私有许可详情为重说提示，不送聊天或产生授权。读取结果支持更短的精确短句，原完整句兼容；用途、ConsentId、窗口、Host/Session/Turn、已展示提示与识别代次约束不变，不增加 Session 状态源。

> 2026-10-22 视觉许可展示：灵动岛只对两种视觉许可状态优先展示完整私有详情及用途明确的语音短句。展示回执校验当前实例、版本、完整文字、展开状态与窗口内的真实可见范围，不能在详情被折叠或挤出区域时提前回执；回执不授予操作权限。通用 Core Bridge 继续只投影固定安全文案，Host 的 Session/Turn 与逐窗 ConsentId 授权边界不变。

> 2026-10-22 当前窗口搜索：`IVisualSearchAutomation` 仅在原生搜索未找到控件且尚未输入时接管。原生候选必须有明确名称/关联标签或独立核实的原生搜索证明；内部编号/帮助文字不单独授权，多可靠候选拒绝。图像读取、输入、窗口身份与结果验证分离；优先精确搜索提示，其次依据当前图的放大镜和分离文字定位已有查询。写入前检查窗口、几何、当前控件与框内光标；写入后精确回读查询及结果。`VisualSearchConsentChannel` 在原 SessionCoordinator 执行内挂起，主窗口搜索与一个直属结果窗口分别使用新 ConsentId；语音许可绑定已显示提示、用途和新的识别代数，授权不持久化、不重放。视觉数据只在本机调用内存中处理。范围与实测见 [当前窗口搜索](docs/CURRENT_WINDOW_SEARCH.md)。

> 2026-10-22 候选修复：手动打开入口向单实例通道传递显式语音启动意图；可见助手初始化合并，停止版本阻止等待初始化的旧打开请求迟到开启麦克风。Host 在未绑定文件的自然桌面打开请求中联合可信应用目录与文档发现进行分流，保留明确文件、同名歧义、应用身份和确认边界。等待原因通过现有私有助手显示通道呈现，通用 Core Bridge 继续只投影固定安全文案。当前窗口通用点击/填写仍未实现；交付状态见 [修复记录](docs/STARTUP_VOICE_AND_DESKTOP_APPLICATION_FIX.md)。

> 2026-10-19 语音修复：仅在本地待命时，将完整的“元枢/元书/原书/袁叔/袁书”及对应“你好”形式归为唤醒词，解决普通离线识别词表缺“枢”的问题；普通对话保留原文字。规范词同时经过现有回声过滤，过渡状态与前后识别重置防止同段重放。唤醒只改变本地接收模式，不创建 Host 请求或授权动作；新请求仍经 SessionCoordinator。撤回未通过真人验收的额外声学唤醒组件，不改变原 ASR 端点与麦克风授权。首个无 Session 的 Voice Turn 由原 Host 回执绑定一次性播报资格。详见 [真人语音与模型验收](docs/UNIFIED_PRODUCT_REAL_USAGE.md)。

> 2026-10-19 完整合并：`src/Yuanshu.Island` 是同仓组件，`build-yuanshu.ps1` 从源码构建 Client、Host 和 Island 并交给唯一 Inno 安装器。Client 按安装相对路径加载 `island/yuanshu-island.exe`，为所有启动入口建立同一显示桥；Tauri 不再注册托盘或独立自启。元枢拥有托盘、语音授权、子进程生命周期；岛通过现有协议投影状态。激活参数只打开已有实例。用户路径配置在安装目录外且不含凭据，隔离环境变量优先。详见 [统一构建](docs/UNIFIED_PRODUCT_BUILD.md)。

> 内置岛取消入口使用原 `island_cancel_v1` 协商能力和 `bridge.task.cancel`，检查服务实例、客户端、请求身份、精确目标与版本。Session 操作锁和 Task 存储事务再次核对版本后才取消；持久幂等键绑定服务/客户端/请求，重复失败不会升级成成功。实际执行仍使用原 SessionCoordinator 与任务取消链，不新增数据库结构或扩大动作权限。

> Client 将自己刚启动的 Island 加入专用 Windows Job（由操作系统管理的子进程组），主程序异常结束时自动回收这个窗口及其子进程。Host 保持原有独立后台任务恢复规则；该进程组不包含用户应用或编程任务。正常退出仍先执行现有停止与窗口关闭流程。

> 搜索框焦点回执实验保留但未接入当前交付路径。合并版 `DesktopAutomationServices.cs` 与此前实际桌面版源码哈希一致，避免一次产品合并携带未通过门禁的性能实验。差异及完整草稿保留方式见 [实验记录](docs/experiments/README.md)。

> 2026-10-17 发行查询重叠：仅精确绑定且为绝对 `cloudmusic.exe` 目标的最新歌曲请求，在真实启动返回正进程 ID 后通过调用范围回调开始匿名查询；无启动身份则等到可见窗口验证完成，兼容实现仍先完成原 Open。桌面路径保持串行，外层关联取消覆盖 Open 和准备，失败/取消观察网络任务并拒绝晚结果输入。结果核对沿用同一单调计时器，前 1.5 秒等待 75ms，此后 200ms；准备间隔100ms、后续操作间隔200ms、12秒结果时限及所有目标/回执/页面规则保留。无新增 IPC、数据库或 Session 状态。见 [本批实现与证据](docs/ASSISTANT_METADATA_RESULTS_LATENCY.md)。

> 2026-10-17 并发目录与搜索定位：`GetApplications(out bool)` 比较等待锁前后成功发布的不可变目录快照，应用偏好解析不再重复扫描刚完成的并发发现；普通解析、旧缓存未命中和失败刷新仍按原规则处理。准备探测仅保留 renderer 的只读定位提示，顶层 FindFirst 仍只作初始化；每次 Open/Prepare/Search 清除并一次消费定位引用。默认原生自动化的每次 Search 创建独立驱动，重新打开根、完整证明唯一性、检查当前 Edit 类型、重建 token 并复读实时父链/原生身份；失效即回退原发现，注入实现不被替换。既有前台、取消、写入回读、提交和结果页检查不变，无新协议/数据库/会话状态。见 [本批诊断与实测](docs/ASSISTANT_SEARCH_FIVE_SECONDS.md)。

> 2026-10-17 第二批提速：`ResolveApplicationPreference` 仅省去新目录的立即二次发现，已有缓存未命中仍刷新。每次 `WaitForNativeSearchInput` 独立创建只读 probe，后续样本复读同一 UIA 引用的实时属性/原生绑定，100ms 轮询、两次稳定身份，不产生写入权限。实际 `SafeForegroundSearchExecutor` 仍完整发现与唯一性校验；成功后返回仅内部可见的只读委托，捕获原窗口生命期、查询及已写入控件。`ReadResults` 有回执时只能复读该控件，失败不换控件；无回执才用原发现路径。新 Open/Search 先清空回执，结果页面校验、IPC/schema/会话状态不变。见 [本批边界与证据](docs/ASSISTANT_SEARCH_FAST.md)。

> 2026-10-17 搜索准备：网易云 `WaitForNativeSearchInput` 使用只读 `DiscoverNativePreparation`；先请求 renderer 的首个 UIA Edit，无输入时向顶层发起一次 FindFirst 初始化，每轮重取原生根。输入候选仍须完整原生唯一性证明及精确身份匹配，随后两次稳定采样；准备阶段不再回退整页 FindAll。实际 Search/ReadResults 的完整 Discover、前台/目标绑定、停止和输入边界不变。多编辑框或无法证明唯一性的页面保守失败，不扩大动作权限。详见 [分段诊断与验收](docs/ASSISTANT_SEARCH_READINESS.md)。

> 2026-10-17 搜索发现候选：`ReadSoleEditIdentity` 使用 `ReadEditIdentityRole` 逐节点读取 IA2 身份、窗口归属与角色，省去唯一性证明不使用的属性/状态读取；完整子树、根前后复核和所有输入计数保留。原 `ReadNode`、UIA 初始化/回退、最终输入检查、Session 进度投影不变，无新 IPC/schema。离线回归通过，真实 CEF 冷启动行为待测，详见 [冷启动优化记录](docs/ASSISTANT_COLD_SEARCH_OPTIMIZATION.md)。

> 2026-10-11 实时执行：`VoiceCommandSegments` 仅在同一监听/识别代际内临时组装有明确语法连接的短语，不保存目标权限。`ApplicationSearchProgressChannel` 以调用范围传递执行枚举，SessionCoordinator 核验当前 Session、原 Turn、OperationId、取消和终态后更新现有 ResultSummary；Client/Island 直接投影，不创建第二任务状态。窗口验证成功后，发行资料查询与只读搜索准备重叠进行，提交前复验完整身份。详见 [自然语音执行与实时进度](docs/ASSISTANT_LIVE_PROGRESS.md)。

> 2026-10-11 搜索提速与发行语义：`LatestMusicArtist` 仅由明确、未引号的单歌手最新歌曲请求生成，并随 `ApplicationSearchWorkflow` 进入 canonical 授权作用域；普通 null 分支保持原表示。`LatestMusicResolver` 通过固定匿名 HTTPS 目录读取日期/艺术家/作品，12秒总限时、响应大小上限、不跟随重定向，不使用聊天模型或凭据。网络结果只派生搜索词，返回后重新检查原窗口与取消状态；结果附公开来源、作品日期和平台版本核实状态。Chromium 搜索发现以完整有界 MSAA/IA2 枚举证明唯一输入后快速定位，保留最终 UIA 身份/写入验证，不完整即回退。精确绑定的已开普通 exe 直接激活。无新增 Session/Turn 状态、IPC 或数据库版本。实测与范围见 [提速与发行日期查找](docs/ASSISTANT_FAST_LATEST_MUSIC.md)。

> 自然口语路由修正：`ApplicationSearchRequest` 识别本机目标加顺序搜索、省略打开及有限重复口语前缀；`DesktopFileRequest` 与其共享明确序列边界，仍保留文件字面量优先。`InstalledApplicationCatalog` 在完整注册名匹配之后处理桌面修饰语。所有请求继续进入原 SessionCoordinator 和已绑定应用搜索作用域，未知应用/歧义/缺查询不退回普通聊天。详见 [本批修复记录](docs/ASSISTANT_COMMAND_ROUTING_FIX.md)。

> 贴边收纳修正：`AssistantPlacement` 将休息态停靠球的原生可见窗口裁到直径三分之一，React 在窄窗口内保持 44×44 圆形并按对应边对齐裁切。原生窗口整体仍在所在显示器工作区内，不在相邻显示器或任务栏留下隐藏部分的输入区域。保存结构、沿边中心、完整展开、焦点归还与转写回执不变。见 [贴边收纳](docs/ASSISTANT_ISLAND_EDGE_PEEK.md)。

> 自由摆放候选：Island 原生 `AssistantPlacement` 持有显示器相对位置、停靠边、拖动代际及模式切换等待；只有明确用户拖动完成才保存独立位置文件。前端将 compact 投影为胶囊或贴边球，4 像素拖动阈值与点击互斥，拖动中冻结真实已显示版本并保持其心跳。休息态接受鼠标但不自动激活，自动展开穿透，手动展开可交互。Host/Session/语音授权和独立 Island 四态行为不变。下方固定定位与休息態穿透为此前候选历史，见 [拖动与位置记忆](docs/ASSISTANT_ISLAND_DRAG_PLACEMENT.md)。

> 顶部输入修正：Island 原生 `AssistantSurface` 独立管理展示/交互与快捷键可用状态，版本单调递增；自动展示默认使用原生鼠标穿透并禁止焦点激活。只有托盘、快捷键或 Client 的 `--show-controls` 重复激活进入可操作面板；失焦、收起、Esc 释放交互，不改变语音/Session。激活参数不能作为新独立进程入口。此状态不扩展 Host 协议和动作权限，见 [鼠标穿透修正](docs/ASSISTANT_ISLAND_INPUT_PASS_THROUGH.md)。

> 2026-10-11 灵动岛：`MainWindow.Island` 启动真实 Tauri 子进程，`AssistantIslandBridge` 通过随机、当前用户专用且绑定子进程身份的 Named Pipe 发送临时显示投影。React 展开窗口并显示最终转写后回传精确版本回执；Client 再把原输入交给现有 SessionCoordinator。三秒可见性心跳失效或窗口退出即撤销待提交语音并停止麦克风。此展示协议独立于 Host IPC v12，不新增会话、Provider、数据库或动作权限；控制按钮只调用现有 Client 操作。详见 [整合契约](docs/ASSISTANT_ISLAND_INTEGRATION.md)。

> 2026-10-11 后续功能：`VoiceReceptionGate` 只管理本地接收模式，按监听代际、原生识别代际与切换归属拒绝旧语音及旧播报完成；休息/唤醒词不提交 Host 或 Provider，待命仍开麦并在界面明示，停止撤销本次监听。原生切换更换录音对象并排空旧回调。`FindDesktopFile` 独立于 `OpenFile`，只在桌面第一层进行有界名称枚举；打开路径在原 Turn 中冻结并于确认时复验原指纹。小球记忆入口只打开已有显式本机记忆设置，不增加记忆选择、保存或出站行为。无新增 IPC/schema，SessionCoordinator 仍是唯一会话来源。详见 [后续功能实现与验收](docs/ASSISTANT_FOLLOWUP_20261011.md)。

> 2026-10-11 应用搜索连续流程：`ApplicationSearchRequest` 只解析语言，Host 从安装目录解析并冻结应用 ID、启动绑定、查询和后续动作。`desktop.application.search` 使用一次性精确作用域，由原 SessionCoordinator 执行一个 Turn；原生层复核目标进程与前台、唯一搜索输入、一次提交和对应结果。结果读取与后续动作分开验证，模型文字、同名窗口和结果标题不授予操作权限。NetEase 原生结果读取来自其实际可访问性结构；无可靠媒体标识时拒绝播放。详见 [应用搜索实现](docs/APPLICATION_SEARCH_WORKFLOW.md)。

> 2026-09-22 悬浮入口：AssistantOrbWindow 为纯展示窗口，MainWindow 继续持有现有客户端协调逻辑，Host SessionCoordinator 仍是唯一会话来源。MainWindow 初始化已与 Loaded 解耦且幂等；球、管理页、托盘共用相同的语音/Session 投影。DesktopLaunchOptions 区分手动助手、管理、后台和暂停预览；语音只在可见助手的手动入口或明确开始事件启动。单实例激活区分显示球/管理，不隐式恢复已暂停监听；退出使用有界清理结束当前 Client。无 IPC/schema 或凭据合同改变。

> 2026-09-22 语音生命周期修复：DesktopClient 的 VoiceSessionController 仅管理本次麦克风授权、启停/取消与监听代际，不保存第二套 Session/Turn。主页显式按钮开启，所有识别后的请求仍进入 Host SessionCoordinator。原生监听器按麦克风对象和取消状态核对结果来源，UI 排队回调再比对监听代际；状态刷新只展示已记录状态。配置修复为仓库外同一 Windows 用户的既有 DPAPI 密文/设置恢复，不增加凭据存储方式，不改 IPC/schema。

> 同批语音回复补充：VoiceReplyGate 仅保存一次临时播报资格，绑定本窗口提交的 Turn ID、Session/Coordinator 身份和监听代际；Host 的 Completed/Failed 投影触发一次本地朗读。历史、文字输入和其他窗口提交不自动播报。停止、关闭、新输入、话题/Host 切换撤销旧队列和播报资格；播放取消源按实例归属释放，旧播放完成不能清理新播放。WindowsSpeechOutput 将加入队列和取消串行化，并拒绝已取消请求；播放错误保留为可见提示。无新增 Provider 调用、麦克风隐式授权、IPC 或数据库状态。

> 2026-09-22 通用应用路由：确定性规划器区分明确本机应用与网站；对未指定网站的同名请求，Host 先在受信任的 InstalledApplicationCatalog 解析应用，成功后带 PreferInstalledApplication 本地上下文重新规划，并保留既有应用 ID、目标哈希与单次授权。仅 NotFound 可保留已知网站计划，歧义/安全拒绝向上传递。目录按“完整登记名 → 受限口语前后缀 → 原有别名及唯一匹配”解析；未知名称沿用一次发现刷新。应用名不交给模型猜可执行路径，不新增 IPC/schema。

> 2026-09-22 开发候选：`DesktopFileRequest → AssistantCommandService → IDesktopFileCatalog` 在本机匹配明确桌面文件名，解析出的路径保存在原 Session Turn 的 FilePath 中，确认续接前检查原计划及文件元数据。仍走现有 OpenFile 权限与单次确认，不新增 IPC/schema。`WindowsDesktopSearchAutomationDriver` 通过绑定窗口的 renderer MSAA/IA2 元数据补充 UIA 搜索语义，以原生窗口/uniqueID 识别重复代理；实际输入仍使用 UIA ValuePattern、精确回读和提交边界检查。该层不接网络、模型、截图、远程调试或鼠标坐标操作。具名搜索不匹配当前窗口时，用已注册应用的精确进程路径补充名称；无法匹配则等待用户切换窗口。

> 2026-09-12：当前产品源码 `19081d3` 已含只读 Bridge、应用发现/搜索和指针文字回答候选；Desktop IPC v12、SQLite schema v11。以下旧阶段段落仍描述对应阶段的历史合同。上述安装身份保留；本地 Gate4C 修复候选已实现经用户批准的两次临时快捷键交互，捕获仍严格少于 10 秒，冻结文字阅读/单次发送为 120 秒；发送前只验证原窗口身份，不再取图。Client 仅保存一次UI操作及其精确取消委托，SessionCoordinator 仍为唯一协调入口；关闭要等Host确认取消。详见 [修复记录](docs/V0.7.0_GATE4C_REPAIR.md)，真实使用闭环仍待验收。

> 冻结背景（2026-08-30）：V0.6.0 / Stage 4 的 S4-R1/R2/R3/R4 均为 `INTEGRATED_PASS`，正式标签、独立标签源码产物与 C1 证据均已形成。该历史冻结状态不等于当前候选已获对外分发批准。

## 1. 运行结构

```text
用户
  ↓ 语音 / 文字 / 可见选择与确认
DesktopClient（WPF）
  ├─ 离线语音识别与朗读
  ├─ SessionUiPresenter：把唯一阶段映射为唯一用户状态
  ├─ 新话题、停止、项目/文件选择、窗口同意或拒绝
  ├─ 不直接访问 SQLite、Codex 或 Windows 动作
  ├─ 设置页：普通聊天 Provider/Model、数据去向、凭据和健康状态
  ├─ 设置页：用户显式管理的本机长期记忆及主动本地预览（不自动发给模型）
  └─ 编程 Agent 独立显示为 Codex
  ↓ 当前用户 Named Pipe，protocol v12（当前候选）
DesktopHost
  ├─ SessionCoordinator（唯一会话与前台 Turn 协调入口）
  │    ├─ ConversationService → RoutedConversationProvider
  │    │    ├─ PromptRegistry → chat.general@1/@2 + window.pointer.answer@1 + intent.semantic@1
  │    │    └─ ModelRouter → Provider Registry
  │    │         ├─ CodexChatModelProvider（生产策略安全停用）
  │    │         ├─ DeepSeekChatModelProvider
  │    │         └─ QwenChatModelProvider（仅手动备用）
  │    ├─ AssistantCommandService → 权限策略与 Windows Skills
  │    │    └─ ModelSemanticIntentSuggester（不可信建议）→ 确定性重规划
  │    ├─ Window Vision → 单窗口捕获、本机 OCR/UIA
  │    └─ LocalTaskEntryService → Codex Project Skill
  ├─ DeterministicIntentPlanner（确定性意图规划）
  ├─ CapabilityPolicyEngine（权限白名单）
  ├─ DPAPI Provider Credential Store（Key 不进 SQLite）
  ├─ MemoryService → 专用 DPAPI Memory Content Protector → SQLite Memory Ledger
  ├─ SQLite：Session / Conversation / Task / Evidence / Audit / AI Invocation / Memory
  └─ Session 增量通知与 Host 恢复
```

DesktopHost 仍是唯一业务编排和审计边界。SessionCoordinator 协调“当前在聊什么、当前在做什么、缺什么信息以及如何取消”，但不拥有任何项目、文件、窗口或动作授权。Model Router 只选择普通聊天供应商，也不拥有权限。

当前 Core 还提供 `bridge.handshake` 与 `bridge.snapshot.get`，能力为 `bridge_snapshot_v1`。Bridge 仅投影现有 Session/Task 状态，不拥有任务、权限或命令执行能力；Core 实现不证明灵动岛联合验收完成。

## 2. 正式源码范围

V0.4.0 Stage 2 正式源码由下面这些内容共同组成：

- `src/`：生产项目；阶段 2 新增 `ScreenGuide.AI.DeepSeek` 与 `ScreenGuide.AI.Qwen`，并扩展 AI Core、Codex、Host、Protocol、Persistence 和 DesktopClient。
- `tests/`：自动化与真实桌面测试项目，包含 FakeCodexCli、FakeBrowser、Provider 网络边界和实际 WPF UI Automation 测试替身/Runner。
- `prompts/runtime/`：受版本控制的 Prompt Registry 与 Prompt 内容。
- `tools/`：3 个集成与真实验收 Runner。
- `installer/`：Inno Setup 安装定义、简体中文语言文件和用户数据删除工具。
- `scripts/`：发布与隔离安装验收脚本。
- `assets/branding/`：正式品牌资源。
- `Directory.Build.props`、`global.json`、`ScreenGuide.slnx` 和依赖锁文件：版本与可重复构建元数据。
- `PRODUCT.md`、`ARCHITECTURE.md`、`MEMORY.md`、`CHANGELOG.md`、`ROADMAP.md`、`AGENTS.md` 与 `docs/baselines/`：项目唯一事实来源。

`bin/`、`obj/`、`artifacts/`、运行日志、SQLite 运行数据库、语音模型和用户配置不属于源码。

## 3. 模块边界

| 模块 | 当前职责 | 不应承担 |
|---|---|---|
| `ScreenGuide.DesktopClient` | WPF 界面、托盘、可见确认、Host 生命周期、语音交互、Session 状态呈现 | 直接执行动作、直接读写 SQLite、直接调用 Codex |
| `ScreenGuide.DesktopProtocol` | IPC protocol v12、Session 有界投影/游标页/精确 Turn、AI/显式记忆/逐 Turn 出站确认及一次性指针区域 DTO、当前用户 Pipe 客户端、跨 IPC 敏感文本清理 | 业务规则、权限判断、Key 或记忆密文持久化和模型调用 |
| `ScreenGuide.DesktopHost` | SessionCoordinator、业务编排、MemoryService、权限、恢复、审计、增量状态通知 | 让 UI 绕过 Host Service 直接访问存储 |
| `ScreenGuide.AI.Core` | 供应商无关 Chat Model 契约、Provider Registry、Model Router、Prompt Registry、语义建议校验及确定性意图规划 | Provider HTTP/CLI 细节、自由执行工具或隐式授予权限 |
| `ScreenGuide.AI.DeepSeek` | 固定 DeepSeek 官方目的地的普通聊天 HTTP/SSE Provider、错误和健康映射 | 保存 Key、决定 Session、编程 Agent 或电脑权限 |
| `ScreenGuide.AI.Qwen` | 固定阿里云百炼兼容端点、仅 `qwen3.7-plus` 的普通聊天 HTTP/SSE Provider；消费但不公开 reasoning，拒绝 Tool Call | 自动 fallback、保存 Key、改变 Session/权限或替代 Codex 编程 Agent |
| `ScreenGuide.Core` | Session、任务、对话、AI 调用审计、权限和独立 Memory Ledger 领域契约 | Windows、SQLite 或模型供应商细节 |
| `ScreenGuide.Persistence` | SQLite schema v11 与 Session/任务/对话/AI 调用/受保护记忆、窗口身份及安全出站审计元数据存储 | UI、Key、记忆明文和模型调用 |
| `ScreenGuide.Skills.*` | 可替换技能接口与 Windows 低风险动作 | 任意桌面控制 |
| `ScreenGuide.Vision.*` | 单窗口捕获、敏感窗口拒绝、本机 OCR/UIA、一次性指针锚点与最大 640×480 的内存区域裁剪 | 全桌面捕获、持久化图像/OCR 或云端上传 |
| `ScreenGuide.Voice.Windows` | 本机采音、离线识别、回声过滤、朗读 | 保存录音或后台隐蔽监听 |
| `ScreenGuide.Agent.*` | Agent 抽象、安全停用的 Codex 普通聊天适配、独立 Codex 编程连接和进程树取消 | 把普通聊天路由与编程 Agent 混为同一配置，或决定电脑动作权限 |
| `ScreenGuide.Evidence` | Git、测试和任务结果证据 | 代替真实用户验收 |

## 4. Session Coordinator 数据模型

### Session

`SessionRecord` 表示一个明确话题，包含：

- 唯一 Session ID 与关联的 Conversation ID；
- 标题、创建设备、当前/归档状态；
- 当前选中的已授权项目；
- 创建、最近活动、更新时间和并发版本。

同一时刻只有一个当前 Session。首次输入自动创建，后续输入默认续接；“新话题”显式创建另一个 Session。

### Turn

`SessionTurnRecord` 表示用户的一次原始请求，包含：

- 原始文字、输入方式、会话内顺序号和幂等键；
- 工作类型：普通聊天、桌面动作、窗口观察、编程任务或未知；
- 当前阶段和缺失上下文；
- 关联的 Plan、Conversation Turn、Task、Operation、项目、文件和单窗口身份 `{HWND, PID, ProcessStartTimeUtc, ProcessName, Title}`；
- UI 已确认的 Expected Intent/Target 与 Host 规划得到的 Canonical Plan Target；
- 是否需要确认、是否已经确认、是否请求取消；
- 结果、错误、完成时间和并发版本。

同一 Session 内 `sequence_number` 和 `idempotency_key` 均唯一，重复提交不会重复启动 Provider 或动作。更新使用乐观并发版本；每个 Turn 的补充、确认与取消还经过同一把串行门，避免确认和取消并发启动两次。终态 Turn 不接受普通迟到结果覆盖。

### 状态

前台工作状态：

`Understanding`、`Responding`、`WaitingForProject`、`WaitingForFile`、`WaitingForWindow`、`WaitingForWindowConsent`、`WaitingForConfirmation`、`Executing`、`ObservingWindow`。

编程状态：

`ProgrammingTask`、`WaitingForUser`。编程任务可以在后台继续，不会被同一 Session 的普通聊天自动取代。

终态：

`Completed`、`Failed`、`Cancelled`、`Interrupted`。

## 5. 连续对话与并发规则

1. 首页输入统一调用 `SessionCoordinator.SubmitAsync`，不再每句话创建新 Conversation。
2. Session 一对一关联 Conversation；同一 Session 的普通聊天复用 Conversation。阶段 2 不依赖 Provider Thread：每个 Turn 从 `IConversationStore` 重建完整消息历史，再交给该 Turn 冻结的 Provider/Model。
3. 新前台输入会先取消同一 Session 的旧前台 Turn，再启动新 Turn。
4. 编程任务拥有独立 Task ID 和监视器，可在后台运行；用户可以同时继续普通聊天。
5. 创建、切换或通过指定 Session 提交输入都经过同一个“当前 Session”串行入口；切换会停止上一话题的前台工作，不会自动取消已经转为后台编程任务的真实状态。
6. UI 快照携带 Coordinator 实例 ID、实例启动时间与 ChangeVersion：同一 Host 只接受不旧的版本；Host 重启时只接受启动时间更新的新实例，拒绝旧 Host 的迟到快照。

这套 Session 只管理当前话题和任务状态，不提取用户长期事实，也不跨 Session 自动检索历史。

## 6. 真取消与迟到结果保护

```text
用户停止 / 新输入
  → Session Turn CancellationTokenSource
  → ConversationService.CancelAsync
  → IConversationProvider.CancelAsync
  → ModelRouter 按 Turn 找到真实 Provider
  → Codex 普通聊天在接触 CLI 前按策略失败关闭，或 DeepSeek/Qwen HTTP/SSE 取消
  → Conversation Turn = Cancelled
  → Session Turn = Cancelled
```

- 窗口观察同时调用对应 Operation 的取消入口。
- 编程 Turn 的取消会调用真实 Task 取消，而不只是改变 Session UI。
- Coordinator 等待旧前台工作进入终态，再继续替代请求。
- Conversation 的取消与成功提交在 SQLite 中争夺唯一终态：取消先把仍在 Running 的 Turn 原子结束，成功只能提交仍在 Running 的 Turn。取消赢得终态后，迟到 Provider 结果不能再插入 Assistant 消息。
- Session Turn 的确认、授权和取消由每 Turn 串行门协调；SessionStore 的终态和版本条件继续阻止迟到结果把取消改回成功。
- ModelRouter 在 Turn 开始时冻结路由；设置中途改变只影响下一轮。取消会到达这个 Turn 实际使用的 Provider，不会误取消新 Provider 的其他请求。
- Codex 普通聊天在接触 CLI 前按 `ProductionDisabled`/`PolicyDisabled` 失败关闭，不启动进程；DeepSeek 与 Qwen 取消真实 HTTP 请求和流式读取。Windows Job Object 进程树取消属于独立 Codex 编程 Agent 的真实 Task 取消边界；被取消的活动调用或任务都拒绝迟到成功。
- Host 关闭时会取消仍在内存中的前台工作和监视器；重启恢复不会自动重放。

## 7. 上下文补齐与安全边界

### 项目

缺项目时保存原始请求并进入 `WaitingForProject`。用户只能选择仍处于授权状态的项目；选择后更新 Session 项目上下文并重新规划同一 Turn。若 Session 原来选择的项目已经撤权或失效，Coordinator 会清除该选择并回到等待项目，而不是丢失原请求。

### 文件

缺文件时进入 `WaitingForFile`。选择路径必须存在；补齐后重新规划同一 Turn。打开文件仍需要本次可见确认。

### 窗口

没有可用目标窗口时进入 `WaitingForWindow`；用户切换后重试。识别出窗口后进入 `WaitingForWindowConsent`，同意只绑定当时显示的窗口句柄、标题和进程；拒绝直接取消。窗口或搜索目标变化会撤销旧确认并要求重新确认。

模型输出、屏幕内容、网站内容、文档内容和 SessionCoordinator 都不能授予权限。原有 CapabilityPolicyEngine、项目授权、文件确认、单窗口同意和敏感动作禁令仍是最终安全门禁。

电脑操作页的“已发现应用/网站”入口也先创建 Session Turn，再进入等待确认和执行。Session Input 同时携带结构化 `ExpectedIntentKind` 与 `ExpectedTarget`：应用目标是已发现的 Application ID，网站目标是规范化后的完整 HTTPS URI。Assistant Plan 返回 `CanonicalTarget`，Session Turn 持久化 Expected/Plan Target；Host 在初次规划和确认执行两处都用 Ordinal 精确比较。任何意图或目标不一致都会结束 Turn，绝不执行；UI 只做同样的第二层显示前复核。

## 8. IPC、AI 设置与状态更新

- DesktopClient 与 DesktopHost 使用当前 Windows 用户专属 Named Pipe，当前 protocol v12；v10 增加逐 Turn 记忆选择和出站确认，v11 增加 Session 有界投影、游标分页与精确 Turn 读取，v12 增加一次性、10 秒有效、精确窗口绑定的指针区域准备/本机 OCR/取消，以及纯文本出站预览/单次确认方法，旧协议不混用。SQLite 仍为 schema v11。
- v8 新增 `ai.settings.get`、`ai.chat-route.set`、`ai.credentials.set/delete` 和 `ai.provider.health`。AI 设置 DTO 只返回 Provider/Model、能力、数据目的地、健康和配置状态，绝不返回完整 Key。
- 普通聊天路由存入本地 `settings/ai-settings.json`；Key 单独存入 DPAPI 密文。设置页明确显示同一 Session 的既有历史会随下一条消息发送给新 Provider；当前运行回答不切换。
- `sessions.current` 返回最多 32 个最新 Turn、全部非终态 Turn 和 50 条最新消息的有界 bootstrap；`sessions.messages.page` 使用 `(sequence_number < cursor)` keyset 分页，每页最多 50 条；`sessions.turn.get` 读取精确权威 Turn。
- `sessions.wait` 使用最长 30 秒的本机长轮询，返回 `NoChange`、最多 32 个 Turn/50 条消息 upsert 的 `Delta`，或 `ResetRequired`。Host journal 只是有界唤醒/投影状态；SQLite Store 仍是事实真源，不建立持久 delta 表。
- Host 重启、Session 切换、旧 Coordinator 世代、journal gap/overflow 或响应倒退均要求 reset。Coordinator 实例 ID、启动时间、Session ID 和 ChangeVersion 共同绑定响应；DesktopClient 只维护有界显示缓存，按消息 ID+序号去重，不能用缓存授予确认或动作权限。
- Session/Turn 的临时操作串行由 Host 内部单例 `SessionOperationGateRegistry` 负责。引用计数覆盖 holder 与 waiter，取消、异常和释放后归零的 key 会被移除；它可以参与记忆同意发布/确认、Turn 取消等操作的串行化，但不持久化状态、不发布事件，不拥有或决定记忆同意、任务状态或权限，也不替代 `_currentSessionGate`。
- IPC 服务将并发连接限制为 64，另保留 8 个忙碌响应槽；未发送首个请求的连接 1 秒释放，监听临时错误会退避重试。
- 无效长度、无效 JSON 等坏请求帧只记录并关闭该连接，不会让 Host 接受循环或关停流程失败。
- 单次投影软上限为 512 KiB，Named Pipe 仍保留 4 MiB 硬帧上限；单条内容导致超限时稳定失败关闭，不截断内容。

## 9. AI、Prompt 与模型接入

### 统一普通聊天入口

`ConversationService` 仍只依赖既有 `IConversationProvider`，其当前实现为 `RoutedConversationProvider`。这个兼容层从 ConversationStore 读取普通聊天历史：无记忆普通聊天使用默认 `chat.general@1`；只有已完成逐 Turn 记忆完整出站确认时才使用 `chat.general@2`。指针区域回答使用 `window.pointer.answer@1`，并只构建获批的 OCR JSON User 消息与当前问题，不带既有会话历史或图片。所有路径都消费本 Turn 冻结路由并调用统一 `IChatModelProvider`；SessionCoordinator 没有增加 Provider 分支。

`IChatModelProvider` 统一 System Prompt、Messages、Model、可选采样参数、CancellationToken、流式回调、Usage、Finish Reason、Provider Metadata、健康和错误；`ChatModelCapabilities` 显式描述 Streaming、Tool Calling、Vision、JSON Object、JSON Schema、Reasoning 和 Context Window，调用方按真实能力使用。

`ChatProviderRegistry` 当前注册：

- `codex / codex-default`：保留普通聊天适配器描述，但生产策略固定为 `ProductionDisabled`/`PolicyDisabled`，在探测或启动 CLI、读取项目或发送用户正文之前失败关闭；它不是阶段 2 发布目标。独立 Codex 编程 Agent 不受影响。
- `deepseek / deepseek-v4-flash`、`deepseek-v4-pro`：固定 HTTPS 目的地 `https://api.deepseek.com`，声明 Streaming、JSON Object 和 Reasoning；Key 只从凭据 lease 读取，不从环境变量、源码、SQLite 或普通配置读取。
- `qwen / qwen3.7-plus`：固定阿里云百炼 HTTPS 目的地，声明 Streaming 与 JSON Object；只允许用户手动选择，不自动 fallback、重试或重发。

Registry 中的注册本身不构成真实账号验收证据；阶段 2 千问 `qwen3.7-plus` 的真实 Health、Ordinary Chat 与 Cancellation 已在准确 SHA `f7506a6013d83318572c63865607d78861e669bc` 通过。未来如 Provider 或模型合同发生变化，必须针对新 SHA 重新获得授权并形成新证据；Codex 普通聊天继续保持 `ProductionDisabled`/`PolicyDisabled`，不属于阶段 2 真实 Provider 发布目标。

### 路由与无静默降级

`ModelRouter` 从 `IAiSettingsStore` 读取默认普通聊天路由，在 Turn 开始时冻结 Provider/Model，并保存 Turn → Provider 活动映射供真实取消使用。设置只影响下一轮；Provider 故障会返回明确错误，没有自动付费重试，也不会静默把内容改发另一个数据目的地。

### Prompt 与调用审计

`prompts/runtime/registry.json` 当前注册 `chat.general@1`、`chat.general@2`、`window.pointer.answer@1` 和 `intent.semantic@1`。`chat.general@1` 仍是无记忆普通聊天的默认 Prompt；`chat.general@2` 只用于用户逐 Turn 明确选择并完整确认的记忆出站；`window.pointer.answer@1` 只解释当前问题与获批的局部 OCR 文字，并在不足/歧义时明确说明；`intent.semantic@1` 始终不接收记忆或指针 OCR。每项包含 ID、版本、用途、文件、SHA-256、适用 Provider、创建时间和修改原因；加载时拒绝越界路径、重复项和哈希不一致。

SQLite `ai_invocations` 记录 Provider/Model、Prompt ID/版本/哈希、数据去向、状态、时间、Usage、Provider Request ID 和安全失败码；不记录 Key、Authorization Header、Prompt 正文或完整 Conversation 副本。

### 凭据

`WindowsDpapiCredentialStore` 使用 Windows DPAPI `CurrentUser` 和绑定 Provider ID 的 entropy 保护 Key，密文位于用户本地应用数据的 `secrets` 目录。写入采用临时文件后原子替换；解密值只在短生命周期 lease 中暴露并尽量清零。UI/IPC 只显示 Missing、Configured 或 NotRequired，不支持读回 Key。日志、Crash、IPC 和 UI 错误经过敏感文本清理。

### 普通聊天与编程 Agent

Codex 普通聊天适配器由 `CodexChatModelProvider` 承载，但生产策略有意失败关闭；编程任务仍走独立 `CodexConnector` / `CodexSkillAdapter`。在 DeepSeek 与千问之间手动切换普通聊天不改变项目授权、Codex 编程生命周期、Git 边界或 TaskEvidence。

### 语义意图与安全

确定性 Planner 仍是第一入口。只有它仍判断为普通聊天且文字命中有限候选条件时，`ModelSemanticIntentSuggester` 才把当前用户文字交给当前 Chat Provider。输出必须是严格五字段结构，并经过枚举、长度、置信度（至少 0.80）、歧义和缺失上下文组合校验。模型 target 不被采用；Host 只把通过门槛的意图类型重新交给确定性 Planner，并用真实本机上下文重算目标、上下文、确认和权限。失败、非法输出或低置信度都回到保守路径。

电脑动作继续采用确定性 Intent Planner + Capability Policy + 白名单 Skill，不是开放式模型 Tool Calling。S3-R1 新增本机加密记忆账本，S3-R2 增加确定性本地预览；S3-R3 只允许用户在单个普通聊天 Turn 查看完整出站内容后单次确认，且 `intent.semantic`、动作授权与 Capability Policy 永不接收记忆。

### 本机长期记忆账本

`MemoryService` 是显式记忆 CRUD 和本地预览的唯一验证与编排入口。领域只允许四类记忆、全局或精确已授权项目作用域、`UserExplicit` 来源、固定置信度 1.0 和乐观并发版本。`SqliteMemoryStore` 与 Conversation、Session、Task、Provider Thread、`ai_invocations` 分表且不复用其状态。`WindowsDpapiMemoryContentProtector` 使用独立的 CurrentUser purpose/entropy/version 保护标题和正文；凭据 Store 与记忆 Protector 互不复用。

预览候选由 Store 在解密前限定为 Active、未到期的 Global 加可选精确已授权项目，最多 200 条。Core 的纯排序器只在内存中用规范化短语、词和字母数字双字组计算固定分数，按分数/更新时间/Guid 排序，并把结果限制为 8 条和 4,000 字符。查询、索引及规范化替身均不持久化；预览不修改记忆，不触发 Provider、Prompt 或 `ai_invocations`。

## 10. 数据、迁移与恢复

- 默认运行数据：`%LOCALAPPDATA%\ScreenGuide\V01`。
- SQLite：`state\tasking.db`；日志：`logs`；Codex 辅助数据：`codex`；任务证据：`evidence`；AI 路由：`settings\ai-settings.json`；DPAPI 密文：`secrets\<provider>.bin`。
- schema v9 新增独立 `memory_items`；schema v10 给 Session Turn、Conversation Turn 和 `ai_invocations` 增加内容无关的 consent/来源/manifest 元数据；schema v11 只给 `session_turns` 增加 `window_process_id` 与 `window_process_started_at_utc`。迁移按打开时的原始版本生成唯一 `pre-v11-from-vN` 备份；v10→v11 是单事务，失败保持 v10。历史 v10 Turn 的新列为 NULL，窗口确认与执行必须失败关闭并重新取证。
- V0.5.0 只支持 schema v10，不能打开 schema v11。回滚必须保留 v11 主库，并在隔离目录使用匹配来源的 pre-v11 备份；继续回滚 Stage 2/Stage 1 时仍遵守既有 pre-v10/pre-v8 边界，不能用当前源码冒充冻结历史版本。
- 重启恢复：运行中的 Session Turn 先标记 `Interrupted`，再与已有 Conversation Turn / Task 终态对账；可安全等待的项目补充状态保留，不自动执行原请求。
- 单窗口像素和录音不写入数据库或仓库；单窗口像素在本机分析后清零。

## 11. 构建、测试与发布

### Stage 4 本机真实使用评测边界

`tools/ScreenGuide.Stage4.RealUsageRunner` 是独立人工验收工具，不是 DesktopClient/DesktopHost 的运行时组件。它没有 Session、数据库、Provider、网络或凭据依赖；命令行只接受评测模式和 exact SHA，也不提供任意输出文件参数。

语音适配器直接订阅 `OfflineContinuousVoiceListener` 的 partial/final/fault 事件，但事件正文只进入单次内存比较器：固定版本规范化后仅保留匹配布尔值、final 次数、单调时间戳和稳定错误码。每次尝试独立 Start/Stop 监听器；Stop、超时或 fault 会结束当前尝试，fault/取消同时结束批次，因此迟到事件不会跨入下一次监听周期。

视觉适配器先创建并显示固定测试 HWND，再把同意绑定到该精确身份；之后经 `WindowsWindowCaptureTargetVerifier`、`WindowsSingleWindowCaptureService`、`WindowsGraphicsCaptureBackend` 和 `WindowsLocalWindowVisionProvider` 走真实本机路径。每一帧只在一次分析期间存在，`CapturedWindowFrame.Dispose` 清零字节；普通批次或专用场景只要目标身份变化就立即按取消终态结束，不回退到桌面捕获。

公共指标聚合器固定四种终态 `Success/Failure/Cancelled/Blocked`，排除 warmup，使用 nearest-rank 计算 p50/p95，并分别保留 capture/analysis/end-to-end 汇总。安全报告合同当前为 `s4-r2.usage-evaluation.v3`，采用封闭类型和稳定错误码 allowlist，无法携带自由文本诊断；视觉 v3 只增加脱敏帧形态和 OCR 检出布尔值，不保存像素或 OCR 正文。

- .NET SDK 由 `global.json` 固定到 10.0.400，允许同补丁线更新。
- 普通依赖与 win-x64 发布依赖使用锁文件，发布脚本在 locked mode 下恢复。
- `scripts/build-desktop-release.ps1` 是发布入口；Stage 4 冻结版本为 V0.6.0，安装包名固定为 `元枢-V0.6.0-安装包.exe`。正式产物身份来自 `v0.6.0-stage4` 的独立干净标签源码构建；后续仅文档 C1 只回填证据，不改变标签源码或二进制来源。
- V0.3.0 冻结证据保持不变：全量自动化 363/363；实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 已完成阶段 1 的 10 轮连续对话、3 次真取消和项目/文件/单窗口场景，证据目录为 `%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`。
- 阶段 2 离线 Release 定向 QA 173/173 通过；集成后 Qwen 56/56、R4 Runner 61/61、设置/无 fallback/工作负载隔离 3/3 通过。R1/R2/R3 已通过证据被复用，没有机械重跑全矩阵。
- 阶段 2 普通聊天发布目标是 DeepSeek + 千问。DeepSeek 既有真实证据已冻结；Qwen 真实 Health、Ordinary Chat 和 Cancellation 通过。Codex 普通聊天不属于发布目标，普通聊天选 Qwen 时的独立真实 Codex 编程 Task 回归已通过。
- 阶段 3 离线 Release 定向门禁 90/90 通过；实际 Release WPF Client + DesktopHost 通过 Stage 2 路由回归和 Stage 3 记忆 CRUD、预览、完整出站确认、单次发送、输入变化失效与删除流程，Provider 为进程内 Fake，网络、真实凭据和真实 Provider 请求均为 0。
- 阶段 3 标签 `v0.5.0-stage3` 指向 `d553e7e9d606037df87d98e99250de5498f5934a`，annotated tag object 为 `a67d2b24308edb2ce72db97676b837f5018617b8`。独立干净标签源码 locked restore、Client/Host Release publish 和安装包编译通过，发布目录 539 个文件；Client/Host ProductVersion 均为 `0.5.0+d553e7e9d606037df87d98e99250de5498f5934a`，FileVersion 均为 `0.5.0.0`。
- Stage 4 的 S4-R1/R2/R3/R4 均已 `INTEGRATED_PASS`，V0.6.0 已完成 Final Freeze；S4-R4 只把 Session/Turn 临时操作门闩有界化，不改变 SessionCoordinator 状态所有权、权限或事件合同。正式标签 `v0.6.0-stage4` 指向 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，annotated tag object 为 `20045c7960c182a052a5e0b2552ce0ed14a3863f`；本 C1 不改变标签或二进制，Stage 4 不新增 S4-R5。
- Stage 4 S4-R2 最终代码候选 `996da9acb9cf00537794668bfd81f65d1444cca8` 通过 focused 3/3、Vision 35/35、Voice 43/43、Runner/DesktopHost Release 构建、独立 QA 和安全/架构门禁。真实语音为 18/20，另有 STOP 取消 PASS；真实视觉为 20/20，identity-change 按 `Cancelled / vision.identity_changed` 结束。各批次清理均完成，网络和 Provider 请求均为 0。
- Stage 4 独立干净标签源码的离线 locked restore、`build-desktop-release.ps1 -SkipTests` Client/Host publish 和安装包编译通过，发布目录 539 个文件；Client/Host ProductVersion 均为 `0.6.0+3a591a7b6af7da7d97e07093d4c33a3f44553b82`，FileVersion 均为 `0.6.0.0`。安装包 `元枢-V0.6.0-安装包.exe` 为 64,203,075 bytes，SHA-256 为 `5F912F94960E1E90A1EF918139C46751FCA8377A4055B5E68BA060A9BF4E56D7`，未签名。
- Stage 5 当前只有 [Charter / Preflight](docs/V2_STAGE5_CHARTER.md)，推荐方向为 Safe Distribution & Upgrade Readiness，实施状态为 `NOT_STARTED / NOT_AUTHORIZED`。它不是运行时架构，不新增 S4-R5，也不修改 Stage 4 tag、产物、schema、protocol、Prompt、Provider、凭据或权限；版本和新 tag 尚未决定。
- V0.5.0 安装包 `artifacts/release/元枢-V0.5.0-安装包.exe` 为 64,173,791 bytes，SHA-256 为 `4683E10CD6C5317EB537681978DB8A77E2DC15041838EF3DCE2DEE47C7C16F95`，未签名。标签后的仅文档证据提交不改变标签源码或二进制来源。
- 阶段 2 标签 `v0.4.0-stage2` 指向 `33b5859dcaa697bacd5edc5036a58d162b723a0e`，annotated tag object 为 `47a2b3b70fc22904954e2291470e809e58303eca`。独立干净标签源码 locked restore 和 Release build 通过，发布目录 538 个文件；Client/Host ProductVersion 均为 `0.4.0+33b5859dcaa697bacd5edc5036a58d162b723a0e`，FileVersion 均为 `0.4.0.0`。
- V0.4.0 安装包 `artifacts/release/元枢-V0.4.0-安装包.exe` 为 64,128,304 bytes，SHA-256 为 `222DC720E677202BBCAEC6D507F48ACFA8B2FCA31535FF7B03010DF80EE7DEE9`，未签名。标签后的仅文档证据提交不改变标签源码或二进制来源。
- 阶段 1 标签 `v0.3.0-stage1` 继续指向源码提交 `0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`，annotated tag object 为 `9fc790ade57fa2d3c18bc5ee84e8dc9e7018aa89`。
- 标签源码的 locked restore 通过。发布目录共 533 个文件；Client/Host ProductVersion 为 `0.3.0+0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`，FileVersion 为 `0.3.0.0`。
- 安装包 `artifacts/release/元枢-V0.3.0-安装包.exe` 为 64,039,656 bytes，SHA-256 为 `42C609E130B29C6D96784C2B0266473B6D3417BE0DC5FE9C81C7517CB100FCC7`，未签名。标签后的仅文档证据提交不改变标签源码或二进制来源。

## 12. 当前主要技术债

- `SessionCoordinator.cs`、`MainWindow.xaml.cs`、`SqliteTaskStore.cs` 等文件较大；阶段 1 为稳定边界保留了集中实现，后续只能在测试保护下逐步拆分。
- ChangeVersion 与有界 Host journal 仍是单 Host 进程内的显示唤醒状态，不是持久事件日志；journal gap 必须回到 SQLite 有界 bootstrap。
- 编程 Task 状态由 `TaskStateChangeHub` 在持久化提交后发布唤醒提示。Session 同步器先订阅游标、再做一次轻量权威读取，忽略重复/倒序通知；终态只有证据最终提交后才投影，已移除 250ms 轮询和周期性完整 Task Details 读取。
- 单窗口身份已绑定 HWND、PID、进程启动时间、进程名和标题；公开 `desktop.action.execute` 不把客户端 HWND/标题当作窗口授权，Session Host 冻结的完整身份会在 UI Automation 控件读取、写入和提交前重复核验。动态标题变化会保守地要求重新确认，这是防止 HWND/PID 复用和目标漂移的安全取舍。
- DeepSeek/Qwen 真实验收是已冻结的精确 SHA 证据；日后修改 Provider 或模型合同时必须对新 SHA 重新获得最小真实请求授权，不得泛化旧结论。
- Codex 普通聊天只保留 `codex-default` 描述并在生产策略下失败关闭，不提供真实普通聊天模型或 Usage；这不影响独立 Codex 编程 Agent。
- Provider 切换会把同一 Conversation 的既有历史交给新的数据目的地；UI 已明确提示，但仍需真实用户体验验收。
- 每轮重建完整 Conversation 历史并以字符上限保护；Token 预算、摘要和上下文裁剪尚未实现。
- DPAPI 保护静态密文，但不抵御已取得同一 Windows 用户权限、管理员权限或运行时内存读取能力的恶意程序。
- 语义意图只覆盖有限候选句式，保守回退是有意安全选择；不能把它宣传为完整自然语言操作理解。
- 阶段 3 只支持逐 Turn 明确选择、完整可见确认和单次普通聊天发送；没有自动注入、RAG、向量库、后台检索或跨 Session 自动个性化。Conversation 历史和 `ai_invocations` 仍不是长期记忆。
- schema v11 对 V0.5.0 schema v10 向前不兼容；回滚必须让 Host 与 Client 成对回到 V0.5.0，保留 v11 主库，并只在隔离目录使用匹配的 `pre-v11-from-v10`。没有匹配备份时失败关闭；更早回滚继续管理 pre-v10/pre-v8 备份。
- 安装包未签名，语音模型许可与分发方案未放行，同 AppId 安装—卸载—重装生命周期也未在隔离干净环境完成；V0.6.0 Final Freeze 不能据此视为已获对外分发批准。
