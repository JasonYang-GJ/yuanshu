# 变更记录

本项目从 V0.2.1 起采用可追溯版本记录。更早内容以历史报告和 Git 历史为准。

## 未正式发布：0.8.0 合并候选

- 2026-11-08 GitHub 仓库更名为 `JasonYang-GJ/yuanshu`，同步当前已实现源码，更新通用与视觉操作的说明及验收边界；旧独立灵动岛远程历史保留本机私有备份。本次不改变产品行为、不发布安装包或新增冻结标签。旧仓库实际处理状态见 [仓库合并说明](docs/GITHUB_REPOSITORY_MERGE.md)。

- 2026-11-05 视觉桌面任务已更新本机 `0.8.0+source.7bd967d748e99593`：明确窗口图片许可、Qwen 图像请求、相对坐标转换、精确目标预览及有限鼠标/文字输入接入原任务协调。未知任务每步确认，最终结果由用户核对；源版、候选和安装版均完成真实模型规划及随机自绘画布点击。完整检查、同包隔离安装、数据保留升级、真实桌面入口与语音开启通过；第三方软件视觉任务、真人口述和视觉双击/输入/滚动的真窗口验收仍未覆盖。DPI 裁切、坐标空间修复及失败证据保留于 [视觉桌面任务](docs/VISUAL_DESKTOP_TASKS.md)。

- 2026-11-03 连续音乐任务候选已更新本机：热歌语义读取官方榜单并规划明确播放，排除网易云临时提示窗，绑定搜索允许单纯标题变化。完整技术和安装验证、原数据保留及实际自动聆听通过；纯搜索首试窗口状态失败、随后两次完成。用户随后确认真人连续执行并开始播放，但切换/隐藏窗口后对应任务最终仍记录窗口核对失败；用户观察通过和自动收尾未通过分别保留，见 [连续音乐任务](docs/CONTINUOUS_MUSIC_WORKFLOW.md)。

- 2026-11-02 简短应答本机更新：纯呼唤本机短答，普通聊天默认一句，明确要求时展开；“停停”直接停止当前回答。修正短应答回声误吞后续问题，以及播报挂起时旧许可不可恢复的边界。完整构建、隔离与本机安装、原数据保留和真实模型短答通过；用户已真人确认喊名只回“我在”，见 [简短语音应答](docs/CONCISE_VOICE_RESPONSES.md)。

- 2026-10-25 第五候选本机更新：补充搜索许可错词的本地澄清，避免“允许这次识别搜索”取消原搜索并进入聊天；搜索提示使用更短的精确许可句，结果提示提醒不要切换窗口。完整回归、独立审查、隔离与本机安装、原数据保留和桌面自动聆听通过；本次真人原句准确识别并完成微信搜索结果核对，没有打开结果。其它软件与语音稳定性未扩大验收。

- 2026-10-25 第四候选本机更新：修复部分许可误转写分流，疑似许可回复只提示重说，不自动授权；结果读取新增更短确认句，旧句兼容。完整回归及安装、数据保留、自动聆听通过，后续真人搜索仍失败，搜索许可的另一种误转写继续在第五候选修复。见 [当前窗口搜索](docs/CURRENT_WINDOW_SEARCH.md)。

- 当前窗口搜索批次已更新本机：增加通用应用搜索表达、本机视觉搜索与直属结果窗口逐次许可；原生字段必须有明确搜索语义，控件、焦点、输入和结果分别核验，视觉路径不按回车或打开结果。修复灵动岛许可短句被挤出可见区域的问题，仅在完整许可真正可见后准备语音确认。技术检查、隔离安装、原数据保留及实际产品许可展示/停止通过，最终真人口述验收仍待完成。精确身份与边界见 [当前窗口搜索](docs/CURRENT_WINDOW_SEARCH.md)。

- 元枢与超级灵动岛统一为一个源码仓库、桌面入口、托盘和安装包；组件源码位于 `src/Yuanshu.Island`。
- 统一构建脚本从锁定依赖构建 Client、Host 和 Island，保留原数据、配置与各项授权边界。
- 包含 R10 的语音播报、待命唤醒及记事本身份交接修复；已验证范围和限制见 [真实使用验收](docs/UNIFIED_PRODUCT_REAL_USAGE.md)。
- GitHub 当前源码入口统一为 `JasonYang-GJ/yuansu`；旧私有仓库保留迁移说明和历史，见 [仓库合并说明](docs/GITHUB_REPOSITORY_MERGE.md)。
- 本节记录开发候选，不新增正式冻结标签或签名安装包发行。

## 0.6.0 - 2026-08-30（V2 阶段 4：Final Freeze）

> S4-R1/R2/R3/R4 均已 `INTEGRATED_PASS`，V0.6.0 / Stage 4 已完成 Final Freeze。正式标签 `v0.6.0-stage4` 指向 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，annotated tag object 为 `20045c7960c182a052a5e0b2552ce0ed14a3863f`；本 C1 只记录冻结证据，不改变标签或二进制身份，也不代表已获对外分发批准。

- S4-R1 将单窗口授权绑定到 `{HWND, PID, ProcessStartTimeUtc, ProcessName, Title}`，schema 升至 v11；身份缺失或变化逐层失败关闭。
- S4-R2 以独立本机 Runner 完成固定合同的真实语音/视觉评测：真实语音 18/20，STOP 取消通过；真实视觉 20/20，身份变化失败关闭；不保存声音、转写正文或窗口像素。
- S4-R3 将 Desktop IPC 升至 v11，增加有界 Session bootstrap/delta/reset、消息 keyset 分页、精确 Turn 查询、Client 有界缓存和持久化提交后的 Task 状态唤醒；SQLite 仍是状态真源。
- S4-R4 把 Session/Turn 临时操作门闩抽取到 Host 内部单例注册表，并在 holder/waiter 全部释放后移除 key；不改变 SessionCoordinator 状态所有权、权限或事件合同。
- C0 把 Version/AssemblyVersion/FileVersion/InformationalVersion 与安装包名统一到 V0.6.0；protocol/schema 均保持 v11，AppId、安装行为、Provider、Prompt、凭据和权限合同不变。
- 回滚到 V0.5.0 必须让 Host 与 Client 成对回滚，保留 v11 主库，只在隔离目录使用匹配的 `pre-v11-from-v10`；没有匹配备份时失败关闭。
- 同 AppId 安装—卸载—重装生命周期、数字签名和语音模型许可/分发尚未放行；本冻结版本不得声称已获对外分发批准，也不开始 S4-R5 或 Stage 5。
- 独立干净标签源码的离线 locked restore、`build-desktop-release.ps1 -SkipTests` Client/Host publish 与安装包编译通过；发布目录 539 个文件，Client/Host ProductVersion 均为 `0.6.0+3a591a7b6af7da7d97e07093d4c33a3f44553b82`，FileVersion 均为 `0.6.0.0`。安装包 `元枢-V0.6.0-安装包.exe` 为 64,203,075 bytes，SHA-256 `5F912F94960E1E90A1EF918139C46751FCA8377A4055B5E68BA060A9BF4E56D7`，`NotSigned`。

## 0.5.0 - 2026-08-30（V2 阶段 3：通过）

> S3-R1/R2/R3、离线 Release 门禁、实际 Release 本机流程和独立干净标签源码构建均已通过。正式标签 `v0.5.0-stage3` 指向 `d553e7e9d606037df87d98e99250de5498f5934a`；安装包身份见 `docs/baselines/V0.5.0_STAGE3.md`。

- S3-R3 新增逐 Turn 长期记忆选择和完整出站确认：默认 0 条，用户查看 Provider、模型、HTTPS 去向、项目绑定及完整正文后，单次确认最多发送一次。
- 新增 `chat.general@2` 和严格 `USER_SELECTED_MEMORY_CONTEXT_V1` User JSON；记忆是不可信参考数据，不能覆盖当前输入、安全、身份、目标、工具、权限、授权或确认，且 `intent.semantic` 永不接收记忆。
- SQLite/IPC 合同升级到 v10；v9 → v10 建立唯一 pre-v10 备份并原子迁移。审计只保留安全引用、路由、Prompt 身份、计数和 manifest，不保存记忆正文或临时出站 block。
- 取消、变化、过期或重启会让确认失效；确认消费后禁止 retry、fallback 或 resend。

- 新增与 Conversation、Session/Turn、编程 Task、Provider Thread 和 `ai_invocations` 分离的本机长期记忆账本。
- 设置页新增“长期记忆（阶段 3）”，仅支持用户显式新增、查看、修正、启停和确认删除，并明确“仅保存在本机；当前不会自动发送给模型”。
- 标题/正文通过专用 Windows DPAPI `CurrentUser` 保护后持久化；项目作用域只接受精确已授权项目，修正/启停/删除使用版本冲突保护，删除清除密文并保留无内容墓碑。
- SQLite 合同先升级到 schema v9，Desktop IPC 合同升级到 protocol v9；v8 → v9 前生成 pre-v9 备份并原子迁移。V0.4.0 Stage 2 回滚必须使用匹配的 pre-v10/pre-v9 备份或隔离数据目录。
- 本阶段不自动提取或后台检索记忆，不包含 RAG、向量数据库、用户画像或权限授予；只有逐 Turn 完整确认路径允许单次发送用户选中的记忆。
- S3-R2 设置页新增用户显式触发的“本地相关记忆预览”：只读取 Active、未到期的 Global/精确已授权项目候选，以固定短语/词/双字组规则排序，最多展示 8 条和 4,000 字符。
- S3-R2 预览查询和搜索替身不持久化，操作不修改记忆，也不调用 Prompt、Provider、Session、Conversation 或 `ai_invocations`；S3-R3 的出站必须另行逐 Turn 选择并确认。
- Stage 3 离线 Release 定向门禁 90/90、实际 Release DesktopProduct 2/2 与集成后 smoke 1/1 通过；真实 Provider、凭据读取和网络活动均为 0。
- 标签源码 locked restore、Client/Host Release publish 和安装包编译通过；发布目录 539 个文件，安装包 64,173,791 bytes，SHA-256 `4683E10CD6C5317EB537681978DB8A77E2DC15041838EF3DCE2DEE47C7C16F95`，未签名。

## 0.4.0 - 2026-08-29（V2 阶段 2：通过）

> 阶段 2 功能、真实 Provider、真实 Codex 编程隔离和 Release 定向验收已通过。普通聊天发布目标是 DeepSeek + 千问，千问仅作手动备用；Codex 普通聊天保持安全停用，独立 Codex 编程 Agent 不随聊天 Provider 变化。

### 已实现能力

- 新增供应商无关的 Chat Model 请求/响应、能力、健康、错误、流式回调、Usage、Finish Reason、Provider Metadata 和每 Turn 取消契约。
- 新增 `ChatProviderRegistry` 与 `ModelRouter`；路由按 Turn 冻结，只支持用户明确设置，不做静默 fallback 或自动付费重试。
- 新增 `RoutedConversationProvider`，从元枢 ConversationStore 重建完整会话历史，使同一 Session 可 A → B → A 切换而不依赖 Provider Thread。
- 新增 `prompts/runtime` Prompt Registry，当前注册 `chat.general@1` 和 `intent.semantic@1`；校验相对路径、Provider 范围和 SHA-256，并加入固定小型回归评测集。
- 保留 Codex 普通聊天适配器并固定为 `ProductionDisabled`/`PolicyDisabled`，与既有 Codex 编程 Connector/Skill 分离；它不是阶段 2 发布目标，聊天 Provider 切换不改变编程 Agent、项目权限和 TaskEvidence。
- 新增 DeepSeek 普通聊天 Provider，固定官方 HTTPS 目的地，支持当前注册模型、SSE/JSON Object、健康检查、大小限制、取消和 401/402/429/5xx/超时/网络/非法响应等安全错误映射。
- 新增千问手动备用普通聊天 Provider，仅暴露 `qwen3.7-plus`，固定阿里云百炼 HTTPS 端点与文本 Chat Completions 负载；支持 SSE/JSON Object、Usage、健康、取消和安全错误映射，拒绝 Tool Call，内部 reasoning 只做有界消费且不公开。千问失败不会自动改发 DeepSeek/Codex。
- 新增 Windows DPAPI `CurrentUser` 凭据存储、短生命周期 lease、原子替换、删除/损坏恢复和缓冲清零；Key 不写 Git、SQLite 或普通设置。
- IPC 协议候选升级到 v8，新增 AI 设置、路由、凭据和健康方法；设置页明确普通聊天与 Codex 编程 Agent、Provider/Model、配置状态和数据发送目的地，Key 不回显且删除前确认。
- 新增 `ai_invocations` 和 SQLite schema v8，记录 Provider/Model/Prompt/目的地/状态/Usage 等追踪信息；旧 schema 升级前生成 pre-v8 备份。
- 新增只建议、不授权的 AI 语义意图：严格结构解析、置信度/歧义门槛和本机确定性重规划；模型 target、权限或确认主张不能直接执行。
- 强化日志、Crash、IPC 和 UI 错误的敏感信息清理。

### 最终验收

- R1/R2/R3 已集成通过；R4 复用已冻结的 DeepSeek 真实证据，未重复发送 DeepSeek 请求。
- 精确 SHA `f7506a6013d83318572c63865607d78861e669bc` 上 Qwen Health、Ordinary Chat 和 Cancellation 真实验收通过：3/3 HTTP、2/2 模型请求，无 retry/fallback/resend，取消三层终态均为 `Cancelled`。
- R4 离线 Release 定向 QA 173/173 通过；集成后 Qwen 56/56、R4 Runner 61/61、设置/无 fallback/工作负载隔离 3/3 通过。
- 普通聊天保存为 Qwen 时，真实 Codex 编程回归仅 1 个 Task/1 次 attempt，指定文件为唯一 Git 变化，指定 `dotnet test` 真实通过，普通聊天 Provider 请求为 0。
- `v0.4.0-stage2` annotated tag 指向 `33b5859dcaa697bacd5edc5036a58d162b723a0e`；干净标签源码 locked restore、Client/Host publish 和安装包编译通过。
- V0.4.0 安装包为 64,128,304 bytes，SHA-256 `222DC720E677202BBCAEC6D507F48ACFA8B2FCA31535FF7B03010DF80EE7DEE9`，`NotSigned`；本机未执行同 AppId 安装生命周期，尚未获对外分发放行。
- 长期记忆、RAG、向量数据库、用户画像、复杂多 Agent 产品功能、手机端、云端远程控制和大规模 Tool Calling 不属于本条目。

## 0.3.0 - 2026-08-24（V2 阶段 1：通过）

> 功能、363/363 自动化、最终 Release 真实用户流程和源码/产物身份冻结均已通过；标签后的仅文档证据提交不改变标签所指源码。完整安装生命周期仍待干净机验收，不视为已获对外分发放行。

### 统一会话中枢

- 新增 DesktopHost `SessionCoordinator`，把普通聊天、受控电脑动作、单窗口观察和编程任务统一登记为 Session / Turn。
- 新增 `ISessionStore`、SQLite `sessions` / `session_turns` 和 schema v7 迁移；当前 Session、项目上下文、原始请求、等待状态及结构化期望/计划目标可跨 Host 重启保留。
- 首页输入默认继续当前 Session 和同一 Conversation / Provider Thread；新增明确“新话题”入口，不再每句话新建 Conversation。
- 编程任务与普通聊天共享 Session 但保持独立 Turn；编程任务可在后台运行，用户仍能继续聊天。

### 真取消与冲突保护

- 新输入和停止会把取消传到 ConversationService、Provider / Codex 进程树、窗口观察或真实编程 Task。
- Conversation 的取消与成功提交在 SQLite 中争夺唯一终态；取消生效后不再接受 Assistant 消息或迟到成功。Coordinator 等待旧前台工作结束后再继续替代请求。
- 加入当前 Session 串行门、每 Turn 串行门、幂等键、Turn 版本、Session/Turn 归属检查、并发确认保护和重启对账，防止重复执行、跨会话补充和旧结果覆盖。

### 上下文补齐

- 缺项目进入 `WaitingForProject`；选择已授权项目后在同一原始 Turn 继续编程请求。
- Session 中已撤权或失效的项目会被清除，并在同一原始 Turn 重新等待项目。
- 缺文件进入 `WaitingForFile`；选择存在的文件后在同一 Turn 继续，并保留可见确认。
- 缺窗口进入 `WaitingForWindow`；窗口可用后进入按单窗口绑定的 `WaitingForWindowConsent`。
- 窗口授权同意与拒绝均安全收束；窗口或搜索目标变化时旧确认失效。

### UI 与 IPC

- DesktopClient 新增统一 Session 状态卡、新话题、停止、项目/文件选择、窗口重试、窗口同意/拒绝和确认入口。
- 新增 `SessionUiPresenter`，把每个 Coordinator 阶段映射为唯一用户提示和控件组合；Coordinator 实例 ID、启动时间与版本共同拒绝旧 Host / 旧版本快照覆盖新 UI。
- IPC 协议升级到 v7，新增 Session 命令和 `sessions.wait` 长轮询；实时 Session 状态从全量频繁轮询改为变化唤醒。
- Named Pipe 服务增加监听错误重试、64 个连接上限、8 个忙碌响应槽、首请求超时和坏请求帧隔离。
- 电脑操作页改走 Session Turn；应用绑定已发现的 Application ID，网站绑定规范化完整 HTTPS URI。Host 在规划和确认两处精确比较 Expected Intent/Target 与 Canonical Plan Target，不一致时结束 Turn 且不执行。

### 安全

- SessionCoordinator 只协调状态，不授予权限；V0.2.1 的项目授权、文件确认、单窗口同意、确定性意图和动作白名单保持不变。
- 另一个 Session 不能为原 Turn 补项目、文件或窗口同意；用户拒绝后不捕获、不执行。
- 本阶段没有接入 DeepSeek、长期记忆、RAG、向量数据库、第二个普通聊天 Provider、复杂多 Agent 产品功能或更宽 Tool Calling。

### 验证

- 实际 Release DesktopClient + DesktopHost + 真实 Codex 连续对话 10/10，通过。
- 真实 Codex 取消 3/3，通过；每次取消后观察 10 秒，无迟到旧回答。
- 项目、文件和单窗口授权同意/拒绝共 5/5 场景，通过；项目/文件均续接同一原始 Turn。
- 独立桌面产品验收 2/2，通过。
- 实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 验收 5/5，`Failed=0`、`FalseCompleted=0`；项目/文件续接和单窗口同意/拒绝均通过。
- 真实验收证据：`%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`；真实 Notepad 严格校验窗口句柄与标题。
- 自动化全量测试：363/363 通过，失败 0，跳过 0。
- 官方 NuGet 源全 solution 已知漏洞检查通过，没有已知易受攻击的直接或传递依赖。
- 本机为保护同 AppId 的现有 V0.2.0 安装、卸载登记与用户数据，没有运行完整安装—卸载—重装脚本；这是一项安全保护，不计为测试失败，干净机发布生命周期验收仍需单独执行。
- 正式源码提交：`0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`。
- 正式标签：`v0.3.0-stage1`；annotated tag object：`9fc790ade57fa2d3c18bc5ee84e8dc9e7018aa89`。
- Client/Host ProductVersion：`0.3.0+0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`；FileVersion：`0.3.0.0`；发布目录：533 个文件。
- 安装包：`artifacts/release/元枢-V0.3.0-安装包.exe`，64,039,656 bytes，SHA-256 `42C609E130B29C6D96784C2B0266473B6D3417BE0DC5FE9C81C7517CB100FCC7`，`NotSigned`。
- 标签之后的提交只回填上述发布证据，不属于 `v0.3.0-stage1` 源码，也不改变发布二进制。

## 0.2.1 - 2026-08-23

### 基线冻结

- 把冻结前 39 修改、2 删除、44 未跟踪的真实 V0.2 成果纳入正式版本管理。
- 新增当前产品、架构、项目记忆、路线图、文件分类和版本基线文档。
- 为所有项目生成普通构建和 win-x64 发布依赖锁，发布时启用严格恢复。
- 发布/安装验收脚本更新到 V0.2.1，并允许验证指定旧安装包。
- 安装验收增加同 AppId 正式安装保护；检测到现有元枢时拒绝执行临时卸载。

### 稳定性修补

- Host 清除默认 EventLog Logger，只使用项目自己的滚动文件日志，避免普通用户权限下启动失败被 EventLog 异常掩盖。
- Host 启动失败审计改用独立令牌，保证关闭过程中仍尽量记录失败证据。
- Windows 搜索控件激活复用已有稳健前台窗口激活逻辑，减少只调用一次 `SetForegroundWindow` 的不稳定。

### 验证

- Release 构建 0 警告、0 错误。
- 真实 Windows 桌面全量测试 298/298。
- NuGet 官方漏洞源未发现已知易受攻击依赖。
- V0.2.0 与 V0.2.1 安装包均通过隔离安装、Host 初始化、卸载、重装和历史保留验收。

## 0.2.0 - 2026-08-19

- 建立可见常驻语音入口、确定性低风险动作、单窗口本机理解、普通对话和 Codex 编程任务能力。
- 旧安装包及历史说明保留在外部快照和 `DESKTOP_V02_CORRECTION_REPORT.md`。
- 当时成果没有对应的完整干净 Git 提交；V0.2.1 是首次正式冻结的可恢复基线。
