# 元枢项目记忆

## 2026-11-08 GitHub 更新与旧仓库删除

- 所有者要求更新当前源码、将 `yuansu` 更正为 `yuanshu` 并删除旧独立灵动岛仓库。公开主仓库现为 `JasonYang-GJ/yuanshu`，保留仓库身份、公开属性、既有公开历史和标签；本地 origin 已同步改名。
- 公开主线接入当前审查后的源码快照，不把含本机证据路径的未公开中间提交或旧岛私有历史推送到公开仓库。695 项产品相关文件与 2026-11-05 构建相同；本轮只更新文档，没有重跑产品测试或发布安装包。
- `JasonYang-GJ/yuanshu-island` 已在完整远程备份和独立恢复核验后删除，已登录账号及独立连接器回查均为 404。备份包含 3 分支、5 标签、28 提交；旧本地项目目录及未提交修改保持原样，不属于远程 bundle 的备份范围。详见 [仓库合并说明](docs/GITHUB_REPOSITORY_MERGE.md)。

## 2026-11-05 视觉桌面任务批次（已更新本机，受控验收通过）

- 所有者明确要求识图和操作此前未适配的软件；限定在所选单个窗口，不将旧学习助手“禁止移动鼠标”的历史范围当作此新批次的禁止授权。
- 仍走原 SessionCoordinator/Turn。截图发送与普通控件文字许可分开；固定现有路由，只有支持 Vision 的模型才收到图像，当前实现为 Qwen。
- 未知视觉位置的每次输入都需完整图像与精确目标框预览及单步确认。原生输入前、移指针后均重新核对完整源像素；不跨窗口，不开放任意脚本、快捷键或密码输入。
- 未知软件的新 OCR 结果不等于任务完成；最后展示原任务和结果，由用户核对确认。自动验收、受控真模型界面、安装版、真人口述分别记录于 [视觉桌面任务](docs/VISUAL_DESKTOP_TASKS.md)。
- 当前安装为 `0.8.0+source.7bd967d748e99593`，源码 `26385b8af1025b438faa57f32f245a410a1a63e2`。完整 3871 C#、167 前端、71 Rust、26 安装契约、同包 Sandbox 双 PASS、1051 安装文件及 22 表/7 配置保留通过。源版、候选和安装版各有真实 Qwen 与随机自绘画布点击闭环；候选一次受测试快捷键干扰的最终窗口核对失败单独保留。原数据与路由未替换，桌面入口语音已开启。未验证第三方软件视觉任务、真人口述或视觉双击/输入/滚动，不宣称全能兼容；未推送或新建正式冻结标签。

> 本文件记录稳定项目事实和历史决策，不是产品运行时的用户记忆账本。更新时间：2026-11-05。

## 当前状态

- 2026-11-05 所有者明确要求继续补通用软件任务，并选择各类任务都希望支持；同 Turn 受控多步任务框架已交付本机候选 r5 `0.8.0+source.96df0ba66592f5d8`，源 `e52a3c60318aa29240678fb741897422816c484a`。新跑 3681 C#、167 前端、71 Rust、26 安装契约、同包 Sandbox 双 PASS、1049 本机文件及原数据保留通过。r5 真实 Excel 两格原生写入与计算器六步运算独立回读正确；r3 真实 Word 纯正文与浏览器表单通过。撤回早期 Excel UIA 假成功，改用绑定窗口的 NativeOM；小窗可见心跳与全文展示回执分离，长许可改手动确认，同 Turn 停止后实际单元格保持不变。桌面入口自动语音已观察，通用任务真人口述尚待用户验收。该授权替代早期仅打开/搜索上限，保留逐窗口许可和高影响步骤确认；不把框架、模拟测试或少数软件成功称为任意软件全部可用。未推送或新建正式冻结标签。见 `docs/GENERAL_DESKTOP_TASKS.md`。

- 2026-11-04 Steam 修复已安装 `0.8.0+source.4bcf3c114cff6952`，源 `aaf60c58bd05a1d3f5dd0343b4c0884578c98528`：有限纠正句式进入操作规划，“斯蒂姆”对应登记应用，STEM 不误匹配系统信息，仅建议候选；Steam 主窗通过固定 helper 路径、直接父关系和进程身份交接验证。3046 C#、157 前端、67 Rust、26 安装契约、同包隔离安装、1046 本机文件和原数据保留通过。真实 Text UI 的纠正句/音译句均完成，已开 Steam 主窗处于前台，每句一条命令、零聊天调用；错拼零执行。用户反馈已成功打开，但没有对应到新增 Voice Turn；不声称最终包实机冷启动或真人语音日志闭环。首次构建的测试信号竞态和 r2 实机窗口交接失败均保留；见 `docs/VOICE_APPLICATION_CORRECTIONS.md`。

- 2026-11-03 连续音乐候选已安装 `0.8.0+source.32e14227eee1d075`，源 `287ffd112f404a3c872896620b90b988c455706b`：热歌语义、提示窗排除、绑定搜索标题连续性、音乐收据和单次播放回读已补，普通窗口授权仍严格。2992 C#、157 前端、67 Rust、26 安装契约、同包隔离安装、本机文件和原数据保留通过，实际自动聆听通过。纯搜索首试窗口失败、后两次成功；用户随后确认真人指令连续完成并开始播放，并确认听到音乐后切换/隐藏窗口。对应唯一 Voice Turn 与 Command 仍以窗口变化失败结束，没有聊天回退；用户观察的实际播放通过，自动收尾核对未通过，首次纯搜索失败原因仍未知。详见 `docs/CONTINUOUS_MUSIC_WORKFLOW.md`。

- 2026-11-02 简短应答已更新本机为 `0.8.0+source.5404781c87b90caa`，源 `2d32f97e`：Direct 纯呼唤本机“我在。”、精确“停停”走已有取消，普通/所选记忆统一新版简答提示；旧确认拒绝，回声不能吞下一句，播报不能恢复旧许可。完整 2781 C#、157 前端、67 Rust、26 安装契约与同包隔离安装通过，本机文件、原数据和实际自动聆听通过。真实模型普通问题一句 28 字、显式三步要求完整展开；用户随后真人确认喊名只回“我在”，本轮短答通过；未测定语音端到端时延或长期稳定性。首轮完整构建暴露旧 R4 提示身份断言，最小修正后在新目录重新完整构建。详见 `docs/CONCISE_VOICE_RESPONSES.md`。

- 2026-10-25 第五候选已更新本机为 `0.8.0+source.e84c9e2cfee37b62`，源 `ef8aa54d`：第四候选真人“允许这次识别搜索”因残留动作词误送聊天，已用实际 WPF 回调复现并修复。搜索新增更短精确许可，错词只能提示重说；输入含其它动作仍正常替换，停止优先。新跑 2724 C#、157 前端、67 Rust、26 安装契约、独立审查及同包隔离安装通过；本机文件、原数据保留和实际自动聆听通过。本次真人完整原句准确识别，新 Voice Turn 完成微信输入和结果核对，用户反馈一致，没有打开结果。仅一次当前环境通过，不代表识别稳定性或其它软件全面兼容。历史查询错词保留；合成语音对比未证明 beam 更优，保持原识别器，禁止针对联系人硬编码纠错。

- 2026-10-25 已更新本机为 `0.8.0+source.f57fdcdd8c6ea4b7`，源 `852f0067`。第三候选真人已输入查询，但读取许可被识别为“原是读取这个临时窗口”后取消原请求并误送聊天。第四候选让不确定许可回复留在原请求提示重说，新增更短的结果读取许可，仍只精确授权；停止和新任务替换保留。实际 WPF 回调回归先红后绿，新跑 2711 C#、157 前端、67 Rust、26 安装契约及同包隔离安装通过；本机文件、原数据保留、实际自动聆听通过，完整真人搜索待本轮反馈。首次构建 1 项依赖真实软件目录的测试超时，原单测独立诊断一次通过；仅隔离该测试目录后重新完整构建，保留失败且不声称已证明超时原因。见 `docs/CURRENT_WINDOW_SEARCH.md`。

- 2026-10-22 当前窗口搜索已更新本机为 `0.8.0+source.3e9344b8f2e90bb8`，源 `7b6885de`：通用语言解析、本机视觉回退及直属结果窗口独立 ConsentId 已实现。弱搜索字段不再获输入权限，多可靠候选拒绝。第二候选真人只说原搜索指令，尚未说许可短句；发现许可详情被挤出可见区域，已修正布局与实际可见展示回执。第三候选复用经 469 项相关输入一致核对的 2680 C# 结果，新跑 157 前端、67 Rust、26 安装契约及同包 Sandbox；本机文件、原数据保留、自动语音、完整许可展示与停止通过。微信核心服务成功不等于最终真人口述通过，后者仍待验收。视觉授权只允许搜索聚焦、一次输入和只读核对，不能推广为任意鼠标操作。见 `docs/CURRENT_WINDOW_SEARCH.md`。

- 2026-10-22：本机已更新为 `0.8.0+source.a8af58b72a2fae09`。用户明确授权手动打开即语音，暂停实例主动重开也属于本次语音意图；后台、自启和安装结束仍不开麦。完整 2300 项 C#、154 项前端、67 项 Rust、26 项安装合同及相同包的隔离安装通过，实际桌面自动麦克风、暂停重开和重复入口通过。通用桌面应用分流修复后，原句及 QQ/微信的实际文字执行均打开目标窗口；用户随后真人口述原句成功，实际新 Voice Turn、应用目标及窗口也已核对。原数据和配置保持。用户指定 QQ、微信优先做当前窗口口述步骤；通用点击/任意填写尚无完整权限与执行通路，不能用本批启动修复代替。详情见 `docs/STARTUP_VOICE_AND_DESKTOP_APPLICATION_FIX.md`。

- 2026-10-20：按所有者要求，GitHub 当前开发入口统一到 `JasonYang-GJ/yuansu`，展示名称为“元枢 Yuanshu（内置灵动岛）”。原 `yuanshu-island` 经确认归档为只读并保持私有，保留历史、分支和迁移说明；不将私有历史导入公开仓库。公开前复核 R10 的 598 个源文件全部匹配，上传不新增产品行为、不发布安装包或冻结标签。详见 `docs/GITHUB_REPOSITORY_MERGE.md`。

- 2026-10-19 R10：本机已升级为 `0.8.0+source.0e1f0953b779040d`。撤回真人漏检的独立声音唤醒检测，待命仅接受完整有限同音短词；保留识别代际、回声、停止及唤醒段不提交 Host 的边界。“原书”等同音词仍可能误唤醒。修复无既有 Session 时首条语音的自动播报资格，记事本仅增加严格微软包身份交接。完整构建、隔离安装及本机入口通过；新版真实千问停止和连续问答、记事本操作、真人问答播报以及“你好元枢”待命唤醒通过，短词稳定性无扩大结论。原历史/配置保持，结束时已停止麦克风，保留 R9 失败证据；精确身份和分层结果见 `docs/UNIFIED_PRODUCT_REAL_USAGE.md`，不覆盖下方首次合并记录。

- 2026-10-19：用户明确授权以元枢为主体完整合并超级灵动岛，统一源码仓库、构建和安装更新，保留现有功能与数据。0.8.0 为合并候选；源在 `src/Yuanshu.Island`，不再依赖旧岛仓库或旧发布二进制。保持原安装 AppId、数据库格式和 `com.yuanshu.island` 数据身份；外置数据/模型以安装目录外的用户路径配置继承。统一构建入口为 `scripts/build-yuanshu.ps1`。原有未交付聚焦实验保留为补丁，实际交付行为使用此前已使用源码。计划与验收口径见 `docs/UNIFIED_PRODUCT_PLAN.md`；未因合并自动声明历史阶段正式冻结。
- 2026-10-19：合并包本机交付完成。2,206 项 C#、154 项前端、67 项 Rust 通过；最终完整安装包通过独立 Windows Sandbox 的升级/卸载/重装与假数据保留，随后原机仅执行 0.7.0 → 0.8.0 升级。桌面真实启动使用原数据位置、单托盘及同一组进程，原历史/配置/凭据/位置内容校验一致；停在语音暂停状态。源与安装包精确摘要、复用测试依据和未验证边界见 `docs/UNIFIED_PRODUCT_ACCEPTANCE.md`。源码仍为保留既有修改的开发候选，未推送或创建正式冻结标签。

- 2026-09-14：所有者明确授权上传当前完整源码，并将原私人 GitHub 仓库改为公开展示；该新授权覆盖此前仅私人备份/不公开源码的阶段限制，不放行安装包、模型捆绑或签名发布，不新增开源许可。公开范围见 `docs/PUBLIC_SOURCE_STATUS.md`；当前产品改动身份为 `aa50d3839e65cab05872f9eb1213fb5ed898a1c2`。

- 2026-09-12 候选核对：产品源码 `19081d39e443a465770fc9e402d9a30c3d1d0ae0`、V0.7.0 安装 Client/Host 配对与 EXE 哈希匹配；最近正式标签仍为 `v0.6.0-stage4`。前次仅测试诊断/文档变化；后续用户已批准Gate4C交互修复和项目数据迁移，旧安装身份仍保留。
- 2026-09-08 交接报告已记录 Gate4A/B PASS；旧 R2/canary 的失败记录不能覆盖较新交接结论，本轮未重测。Gate4C 尚未完成：使用真实 MainWindow 点击逻辑、fake API 在捕获前停止的诊断证明 Prepare 时前台为元枢而非外部测试窗口；出站确认还有相同前台约束，阅读预览沿用原锚点 10 秒期限。
- 用户已明确同意 Gate4C 交互/时限方案：临时Ctrl+Alt+F8两次手势，首次准备30秒、捕获严格少于10秒、冻结文字阅读/单次发送120秒，已在本地候选实现；真实OCR/Provider闭环仍待用户可见同意。详见修复记录 `docs/V0.7.0_GATE4C_REPAIR.md` 和 `docs/V0.7.0_GATE4C_INTERACTION_PROPOSAL.md`。当前证据账见 `docs/V0.7.0_CANDIDATE_CLOSEOUT.md`。

- V0.6.0 / Stage 4 已完成 Final Freeze，S4-R1/R2/R3/R4 均为 `INTEGRATED_PASS`。正式标签 `v0.6.0-stage4` 指向 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，annotated tag object 为 `20045c7960c182a052a5e0b2552ce0ed14a3863f`；C1 只记录冻结证据，不改变标签源码或二进制身份。Stage 4 到此结束，不新增 S4-R5。
- Stage 5 的唯一方向是 Safe Distribution & Upgrade Readiness。S5-R1 许可/NOTICE 与 S5-R2 真实 Windows Sandbox 生命周期均已 PASS；S5-R2 状态为 `S5-R2_REAL_SANDBOX_LIFECYCLE_PASS`，证据绑定 exact SHA `2dc99dda8ef31cad0fdd6ed0ccf74b34e8b59867`。S5-R3 签名/release identity 与 S5-R4 最终分发验收仍为 `NOT_STARTED / NOT_AUTHORIZED`；Stage 5 尚未完成，也不授权下载、未来安装、签名、采购、联网、上传或发布。
- S5-R1 Offline Inventory、Official Evidence Verification、四轴归属合同与 exact LICENSE/NOTICE bundle 均已 PASS；状态为 `S5-R1_CONTRACT_PASS`、`FAIL_CLOSED_INFRASTRUCTURE_IMPLEMENTED`、`NOTICE_BUNDLE_IMPLEMENTATION_PASS`、`EXTERNAL_DISTRIBUTION_BLOCKED`。Owner 选择专有编译产品分发且不公开分发源码；保留 AI 辅助图标并作事实披露，不主张独占、商标注册、必然版权保护或绝对不侵权；Owner 亲自阅读并接受适用的 Microsoft Windows SDK license/REDIST 条件。冻结 attribution 的 539 条 payload 与 2 条 installer-container entry 已绑定显式 component/version、精确材料、UTF8_NO_BOM_LF_V1/原始 SHA-256 与组件内 NOTICE path；Inno 精确为 6.7.3，ChineseSimplified.isl 绑定 Kira exact commit。七个非 win-x64 Sherpa runtime 与中文语音模型保持排除，WinSDK BuildTools 保持 build-only。唯一详细记录为 `docs/V2_STAGE5_DISTRIBUTION_LICENSE_INVENTORY.md`。
- packaging/NOTICE 门禁仍保持失败关闭：目录缺失/冲突、跨组件错绑、material/hash/version/path 错误、default-branch 替代、NTFS reparse path chain 和 staging 越界均拒绝；只有 exact bundle 全部通过才生成无 wildcard/optional 的 Inno include。技术 bundle 与 S5-R2 生命周期 PASS 均不等于外部分发放行；数字签名/signed identity 和最终发布仍是独立 Gate。
- S5-R2 在一次授权 Sandbox 运行中完成 5 次 installer/uninstaller 进程（3 install/upgrade + 2 uninstall，均 exit 0），验证 Host/Client 成对、schema `10 → 11 → 10`、唯一匹配备份、NOTICE layout、数据保留和 v11 DB hash 保持；网络、Provider、凭据读取均为 0。宿主 536 文件 tree SHA-256 与卸载登记前后不变。详细证据见 `docs/baselines/V0.6.0_STAGE5_S5_R2_LIFECYCLE.md`；历史 loose-directory 文件消失主体仍未知，但不推翻 sealed transport 的真实 PASS。
- V2 阶段 1“统一会话中枢”已经通过；363/363 自动化和实际 Release DesktopClient + DesktopHost 的真实桌面验收均通过。
- V0.2.1 标签 `v0.2.1-baseline` 保留为上一版回滚点；回滚必须同时使用 pre-v7 备份或隔离数据目录。
- V2 阶段 2“可替换 AI 大脑与模型路由”已通过：统一 Chat Model、Provider Registry、Model Router、Prompt Registry、DPAPI、安全停用的 Codex 普通聊天适配器、DeepSeek/千问普通聊天 Provider、设置 UI/IPC、语义建议和 schema v8 AI 调用审计。
- 阶段 2 普通聊天发布目标是 DeepSeek + 千问；千问是手动备用，无自动 fallback/retry/resend。DeepSeek 真实证据已冻结，Qwen 真实 Health/聊天/取消和普通聊天选 Qwen 时的真实 Codex 编程隔离均已通过。Codex 普通聊天保持 `ProductionDisabled`/`PolicyDisabled`，不是发布目标。
- S3-R1 本机加密记忆账本、S3-R2 确定性本地预览与 S3-R3 schema/protocol v10、chat.general@2 和逐 Turn 完整出站确认均已集成。默认 0 条，只有用户单次确认的普通聊天 Turn 才最多发送一次。
- 当前指针理解 C1/C2 候选使用 Desktop IPC v12、SQLite v11：第一次可见同意只允许一次性、严格小于 10 秒、精确窗口绑定的最大 640×480 本机裁剪/OCR；第二次逐 Turn 可见预览显示冻结 Provider/Model/HTTPS origin、`window.pointer.answer@1`、完整问题与 OCR 文字，并明确不发送图片。确认绑定 Turn、路由、Prompt、问题/OCR/锚点/预览哈希，单次消费后最多 1 个纯文本 Provider 请求；取消、Stop、过期、篡改或变化均为 0 次，无 retry/fallback/resend。

## 长期架构决策

1. 渐进演进，不推倒重来。V0.2.1 中通过测试的窗口、权限、语音、存储和 Codex 连接能力必须保留。
2. DesktopClient 不直接访问 SQLite、Codex 或 Windows 动作；DesktopHost 是唯一编排和审计边界。
3. 电脑操作采用确定性意图 + 权限白名单；模型、网页、屏幕、文档和 SessionCoordinator 都不能自行扩大权限。
4. 只捕获用户确认的单个前台窗口，绝不回退为全桌面；授权只对显示的单个窗口和当前 Turn 生效。
5. 运行数据不进入仓库；默认使用用户本地应用数据目录，也可通过SCREEN_GUIDE_DATA_DIRECTORY指定外置目录。2026-09-12用户要求本项目数据全部放E盘，运行数据、语音模型、临时文件和验证记录已迁移并设置E盘路径，旧路径保留兼容跳转；卸载默认保留数据。
6. 模型供应商必须位于可替换接口后。阶段 1 的 Codex 真实证据是历史冻结事实；阶段 2 普通聊天发布目标改为 DeepSeek + 千问，不与 Codex 编程 Agent 绑定。
7. Conversation/Provider Thread、Session/Turn、编程 Task 和未来长期记忆是四种不同状态，不能混用。
8. 安装产物不提交 Git，以版本标签、哈希、测试记录和外部快照关联。
9. 正式版本只能在标签存在、干净源码构建和测试通过、安装验收通过、工作区干净后宣布冻结。
10. 产品运行时长期记忆是独立状态真源，不得复用 Conversation、Session/Turn、编程 Task、Provider Thread 或 `ai_invocations`。本地预览不等于同意；S3-R3 只允许用户为单个普通聊天 Turn 查看完整出站快照后单次确认，不能授予任何权限，也不能自动发送。
11. 单窗口授权的可信身份是 `{HWND, PID, ProcessStartTimeUtc, ProcessName, Title}`。schema v11 在 Session Turn 内持久化 PID 与启动时间，protocol 维持 v10；公开桌面操作 IPC 不接受客户端 HWND/标题作为窗口授权，Host 完整身份必须一直传到 UI Automation 并在控件读取、写入和提交前重验。确认、UIA、捕获后端/回退和分析前任一身份缺失或变化都必须清帧并重新确认，历史 v10 Turn 不得复用旧授权。
12. S4-R2 的真实使用指标只能由独立、前台、人工启动的本机 Runner 产生：每项 1 次预热 + 至少 20 次正式尝试；失败率分母只含 success+failure，取消/阻塞单列，延迟使用 Stopwatch 与 nearest-rank。不得加入产品遥测、后台采样或任意正文证据，声音、转写、窗口图像和身份一律不持久化。

## 阶段 1 已确认决策

1. `SessionCoordinator` 是唯一当前话题和前台 Turn 协调入口，只协调状态、取消、补充和恢复，不拥有权限。
2. 一个 Session 表示一个明确话题，一个 Turn 表示用户的一次原始请求；Session 一对一关联 Conversation。
3. 首页后续输入默认继续当前 Session；只有用户明确“新话题”或选择会话才切换，不使用超时自动猜测。
4. 新前台输入先真正取消旧前台 Turn，再启动替代 Turn；编程任务可以在后台继续，用户可同时聊天。
5. 取消必须到达 Conversation Provider / Codex 进程树、窗口观察或真实编程 Task；Conversation 的取消与成功提交在 SQLite 中争夺唯一终态，每 Turn 串行门和并发版本阻止迟到结果或并发确认复活/重复执行。
6. 缺项目、文件、窗口或窗口同意时保留原始 Turn；补齐后重新规划同一 Turn。项目撤权会清除失效上下文并重新等待，目标变化时旧确认失效。
7. Host 重启恢复上次当前 Session；运行中的工作标记为 `Interrupted` 且不自动重放。等待项目等安全状态可保留。
8. S4-R3 使用 protocol v11：Session bootstrap 最多 32 Turn/50 消息，wait 返回 NoChange/有界 Delta/ResetRequired，历史消息按 50 条 keyset 游标分页；Coordinator 世代、Session 和版本共同拒绝旧响应，客户端缓存只是显示状态。
9. SQLite schema v7 保存 `sessions`、`session_turns` 及结构化 Expected/Plan Target；从 v5 或中间 v6 升级前自动生成 pre-v7 备份。
10. V0.2.1 不能直接打开 schema v7；程序回滚时必须同时使用升级前备份或隔离数据目录。
11. UI 已选应用绑定已发现的 Application ID，网站绑定规范化完整 HTTPS URI；Session Input、Plan 和 Turn 保存结构化期望/实际目标，Host 在规划与确认两处做 Ordinal 精确比较。
12. 同 AppId 的安装/卸载测试不能覆盖仍需保护的正式安装登记；本机保留 V0.2.0 时不运行 `test-desktop-installer.ps1`，完整安装生命周期改在干净机验收。
13. 正式冻结使用两提交：先提交源码/文档并创建标签，再从标签重建和计算安装包哈希，最后用单独证据提交回填提交号与哈希，避免自引用循环。
14. Stage 5 不得伪装成 S4-R5，也不以内部重构冒充用户价值；唯一 Charter 为 `docs/V2_STAGE5_CHARTER.md`。版本/tag、许可判定、干净机生命周期、证书/私钥/费用、真实签名与发布都必须在对应切片获得 Owner 可见授权。

## 阶段 2 已冻结决策

1. 普通聊天业务只依赖供应商无关的 `IChatModelProvider`。Provider 差异必须留在实现层；SessionCoordinator、权限和 Conversation 领域不增加 DeepSeek/千问/Codex 分支。
2. `ChatProviderRegistry` 只接收声明 `OrdinaryChat` 工作负载的 Provider，并校验 Provider/Model 唯一；新增 Provider 不应改变 SessionCoordinator。
3. `ModelRouter` 第一版只支持用户明确选择的默认路由。每个 Turn 开始时冻结 Provider/Model；设置变化只影响下一轮，没有静默 fallback、自动付费重试或隐式跨供应商发送。
4. 对话连续性由元枢 `ConversationStore` 保存的消息历史负责，而不是依赖 Provider Thread。A → B → A 时每轮把同一 Conversation 历史交给当时选中的 Provider。
5. 普通聊天与编程 Agent 独立：`CodexChatModelProvider` 作为安全停用适配器保留，生产普通聊天以 `ProductionDisabled`/`PolicyDisabled` 失败关闭；编程任务继续走 `CodexConnector` / `CodexSkillAdapter`。DeepSeek/千问聊天切换不能改变项目授权或 TaskEvidence。
6. Prompt Registry 使用仓库内受版本控制的文件和清单：`chat.general@1` 仍是无记忆普通聊天的默认 Prompt，`chat.general@2` 只用于用户逐 Turn 明确选择并完整确认的记忆出站，`window.pointer.answer@1` 只用于逐 Turn 确认的指针 OCR 纯文本回答，`intent.semantic@1` 始终不接收记忆或指针 OCR。每次加载校验相对路径、适用 Provider 和 SHA-256；每次调用记录 Prompt ID/版本/哈希。
7. Provider/Model 设置与凭据分开。路由 ID 写普通设置文件；DeepSeek/Qwen Key 分别使用 Windows DPAPI `CurrentUser` 加密密文，只通过各自短生命周期 lease 读取，Key 不进 Git、SQLite、普通日志或 IPC 响应。
8. 不做静默跨 Provider 降级。Provider 故障必须给用户明确、安全提示；是否切换数据目的地由用户决定。
9. AI 语义层只提供不可信的意图类型建议。严格本机解析、置信度和歧义门槛通过后，仍由确定性 Planner 用真实上下文重算；模型 target、缺失上下文和任何“用户已同意”主张都不能授权。
10. protocol v8 只新增 AI 设置/凭据/健康 IPC，不创建第二套 Session 状态。设置页必须明确普通聊天与 Codex 编程 Agent 的责任和数据目的地。
11. SQLite schema v8 新增 `ai_invocations`，只记录 Provider/Model/Prompt/目的地/状态/Usage 等调用证据，不保存 Key、Prompt 正文或完整 Conversation。升级前生成 pre-v8 备份。
12. Qwen 是 DeepSeek 的手动备用普通聊天 Provider，仅注册 `qwen3.7-plus`。它固定发送到阿里云百炼官方兼容端点，不允许自定义 URL，不自动 fallback/重试/重发，不接管 Codex 编程 Agent；reasoning 只做有界消费，绝不进入 UI、数据库或日志。
12. V0.3.0 只支持 schema v7。回滚到 `v0.3.0-stage1` 时必须保留 schema v8 数据库并使用 pre-v8 备份或隔离数据目录；不使用破坏性 Git/文件清理。

## 阶段 2 已确认验收

- R1/R2/R3 全部 `INTEGRATED_PASS`；DeepSeek Flash/Pro 真实验收证据已冻结，R4 不重测。
- Qwen `qwen3.7-plus` 在 `f7506a6013d83318572c63865607d78861e669bc` 通过真实 Health、Ordinary Chat 和 Cancellation：3/3 HTTP、2/2 模型请求，DeepSeek/Codex 普通聊天请求 0，无 retry/fallback/resend。
- R4 离线 Release 定向 QA 173/173 通过；集成后 Qwen 56/56、R4 Runner 61/61、设置/无 fallback/工作负载隔离 3/3 通过。
- 普通聊天选为 Qwen 时，真实 Codex 编程回归仅 1 Task/1 attempt，指定文件为唯一 Git 变化，指定 `dotnet test` 通过，任务时窗内 `ai_invocations=0`。
- 最终标签源码 locked restore、Client/Host win-x64 publish 和安装包编译通过；发布目录 538 个文件，Client/Host ProductVersion 均绑定 `33b5859d...`，安装包 64,128,304 bytes 且未签名。

## 阶段 3 R1 / R2 / R3 冻结决策

1. 记忆类别只允许 UserFact、UserPreference、ProjectNote、Decision；作用域只允许 Global 或精确已授权 Project ID；来源仅 `UserExplicit`，置信度固定 1.0。
2. 标题和正文使用专用 DPAPI CurrentUser 保护器后存入 SQLite，不能复用 Provider 凭据存储；数据库、日志和错误证据不保存明文。
3. 修正使用 ExpectedVersion 覆盖同一逻辑项，不保存旧明文/密文历史；删除必须显式确认并原子清空密文，只保留无内容墓碑。
4. schema v9 只新增记忆表/索引；protocol v9 只新增显式记忆 CRUD。v8 → v9 前建立 pre-v9 备份并原子迁移，V0.4.0 回滚使用该备份或隔离目录。
5. S3-R1 不创建 Prompt、Provider、AI Invocation、向量或 RAG 入口；记忆不能改变当前输入、目标、项目/文件/窗口权限、同意或确认。
6. S3-R2 只增加用户点击触发的本地词法预览：Active/未到期候选最多 200，结果最多 8 条/4,000 字符；查询和搜索替身不持久化，预览零状态写入、零模型调用。
7. S3-R3 使用 protocol/schema v10 与 `chat.general@2`：有序选择 1–8 条、完整显示 Provider/Model/HTTPS origin/项目/正文后单次确认；原子复核失败时 Provider 与 Invocation 都为 0，确认消费后禁止 retry/fallback/resend。
8. 出站 block 是临时 User JSON，不持久化；审计只保存 ID/version、路由、Prompt 身份、计数和 manifest，不保存 query、标题、正文、原始 block 或 plaintext hash。语义建议永不接收记忆。

## 阶段 1 已确认验收

- 实际 Release DesktopClient + DesktopHost + 真实 Codex 连续对话 10/10，通过；同一 Session 正确理解“刚才那个”“第二个”“继续”“不是这个，我说的是……”和前文引用。
- 真实 Codex 打断 3/3，通过；每次取消后继续观察 10 秒，旧回答没有重新出现。
- 项目补充、文件补充和单窗口同意/拒绝共 5/5 场景，通过；项目、文件均在同一原始 Turn 续接。
- 独立桌面产品验收 2/2，通过；包含实际 Release WPF Client/Host、UI 停止、状态同步、窗口拒绝和进程恢复场景。
- 实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 验收 5/5，`Failed=0`、`FalseCompleted=0`；Notepad 严格校验窗口句柄与标题；运行证据：`%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`。
- 自动化全量测试 363/363，通过；失败 0，跳过 0。
- 官方 NuGet 源全 solution 已知漏洞检查通过，没有已知易受攻击的直接或传递依赖。
- 标签源码 locked restore 成功；发布目录 533 个文件。Client/Host ProductVersion 为 `0.3.0+0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`，FileVersion 为 `0.3.0.0`。
- 安装包 `artifacts/release/元枢-V0.3.0-安装包.exe` 为 64,039,656 bytes，SHA-256 为 `42C609E130B29C6D96784C2B0266473B6D3417BE0DC5FE9C81C7517CB100FCC7`，未签名。

## 阶段 0 保留事实

- 阶段 0 正式基线：V0.2.1；独立干净源码验收通过，以 `v0.2.1-baseline` 为冻结点。
- 冻结前 39 修改、2 删除、44 未跟踪，共 85 个变化；71 正式能力、11 保留后重构、3 历史遗留、0 构建产物、0 无法判断。
- V0.2.1 最终真实桌面测试 298/298；V0.2.0 已安装目录中的 533 个发布文件与受保护旧发布目录逐一哈希一致。

## 尚未解决

- V0.6.0 标签源码冻结证据已完成：发布目录 539 个文件，Client/Host ProductVersion 均绑定 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，安装包为 64,203,075 bytes，SHA-256 `5F912F94960E1E90A1EF918139C46751FCA8377A4055B5E68BA060A9BF4E56D7`，且未签名。S5-R2 isolated same-AppId lifecycle 已 PASS；尚未解决的是数字签名、signed release identity、语音模型许可/分发和最终外部分发放行。
- 回滚到 V0.5.0 必须让 Host 与 Client 成对回滚，保留 schema v11 主库，并只在隔离目录使用匹配的 `pre-v11-from-v10`；没有匹配备份时失败关闭。
- 阶段 3 只有显式本机预览与逐 Turn 完整确认后的单次发送；没有自动提取、自动模型注入、后台/语义检索、RAG、向量数据库或跨 Session 自动个性化。
- 固定 Prompt 评测集已建立，但真实模型质量、Token/成本对比和长期回归趋势尚未形成发布证据。
- Codex 普通聊天只保留 `codex-default` 描述并在生产策略下失败关闭，不提供真实普通聊天模型或 Usage；独立 Codex 编程 Agent 继续保留。
- Provider 切换会把同一 Conversation 既有历史发送到新的数据目的地；UI 已提示，真实用户是否理解仍需验收。
- 每轮发送完整 Conversation 历史并设字符上限；没有 Token 精确预算、摘要和上下文裁剪。
- DPAPI 不抵御同一 Windows 用户高权限恶意进程或运行时内存读取。
- S4-R3 已移除长会话全量重复快照和编程任务 250ms 状态轮询；SQLite 仍是事实真源，Host journal/Task 事件与 Client cache 只负责有界投影和唤醒。S4-R4 已把 Session/Turn 临时门闩抽到内部单例注册表并在空闲时清零；`SessionCoordinator.cs`、`MainWindow.xaml.cs` 和部分 SQLite Store 仍较大。
- S4-R2 已完成真实麦克风 1+20、独立 STOP 场景、可见测试窗口 1+20 和身份变化人工验收；这些结果只证明当前本机与固定评测合同，不等于所有麦克风、口音、窗口内容或 Windows 设备上的普遍质量保证。
- 安装包无数字签名；语音模型分发与许可证待定。
- 阶段 3 的 S3-R1/R2/R3 已集成；自动/语义检索、画像或其他模型使用仍需新的独立批准。

## 更新规则

- 只记录已经确认、未来仍有用的事实和“为什么这样决定”。
- 路线图设想写入 `ROADMAP.md`，不能写成已实现。
- 当前功能写入 `PRODUCT.md`，当前结构写入 `ARCHITECTURE.md`。
- 每个正式版本必须新增基线文档和 Git 标签；不得覆盖旧版本记录。
