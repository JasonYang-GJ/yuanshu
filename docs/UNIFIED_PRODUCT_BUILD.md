# 构建、安装和更新元枢（内置灵动岛）

自 0.8.0 合并候选起，本仓库是唯一构建来源。`src/Yuanshu.Island` 是内置界面组件，不能单独作为桌面产品安装。原岛仓库只用于历史追溯，不参与当前构建。历史 `build-desktop-release.ps1` 仍保留其冻结版本校验，当前产品使用下列统一入口。

在 Windows x64 准备 PowerShell 7、`global.json` 指定的 .NET SDK、Node.js/npm、Rust MSVC 与 Windows C++ 构建工具，以及 Inno Setup 6.7.3。运行界面需要微软 Edge WebView2 Runtime。语音模型和用户凭据不包含在安装包中，原模型目录和凭据继续沿用。

```powershell
pwsh -NoProfile -File .\scripts\build-yuanshu.ps1
```

首次构建按锁文件恢复 NuGet、npm 和 Cargo 依赖；不会更新依赖版本。脚本依次验证 C#、前端和 Rust，构建 Client/Host/Island，校验统一版本，收集上游许可文本并生成唯一 Inno 安装包。新输出在 `artifacts/unified/<版本>-<时间>/`，包含 `app/`、`installer/`、`evidence/` 与 `build-result.json`。不会清空或覆盖已有构建目录。

未提交开发稿只能显式使用 `-AllowUncommitted` 生成本机候选；源清单逐文件记录 SHA-256，产物版本绑定清单摘要，并在构建结束检查源码未变。`-SkipTests` 仅供诊断，结果明确标记未执行测试，不能作为完成验收依据。历史正式冻结仍需干净源码、标签和相应发布门禁。

若只修构建或第三方资料，可用 `-ReuseDotnetEvidenceFrom <先前构建的evidence目录>` 复用完整 C# 回归。脚本必须确认全部 C# 源码、测试、资源、提示词、项目和锁文件逐项未变，所有测试程序集的 TRX 齐全且无失败/跳过；否则拒绝复用。新构建保存原始报告及两份源码清单的摘要，仍重跑前端、Rust 和安装合同检查。

安装包更新原元枢安装身份和位置，安装/卸载前要求退出产品，不强杀进程；新版本一次替换全部三个组件。桌面和开始菜单使用主动语音入口，可见助手就绪后开始监听；安装结束、后台和明确暂停入口不开麦。灵动岛和主窗口由同一进程生命周期管理。更新方式是运行新的完整元枢安装包；没有增加云更新服务。本次入口变化的交付状态见 [修复记录](STARTUP_VOICE_AND_DESKTOP_APPLICATION_FIX.md)。

原默认主数据目录及 `com.yuanshu.island` 配置身份保持不变。使用过开发版外置目录的用户，可在退出后用 `scripts/Set-YuanshuRuntimeProfile.ps1` 指定已有数据库、模型和连接名称。该命令只写 `%LOCALAPPDATA%/Yuanshu/runtime-profile.json` 路径配置，保留旧配置备份，不读取凭据或移动数据库。不要把这个本机配置提交到仓库。配置覆盖从用户/系统环境继承的旧路径；进程显式指定不同数据目录，或指定连接名称时，仍使用隔离环境，不读取用户配置。

隔离安装验收使用 `New-UnifiedSandboxLifecycle.ps1` 生成 Windows Sandbox 配置，再运行其中的验收脚本；脚本拒绝在宿主执行。它检查安装/升级/卸载/重装和假数据保留，不替代真实桌面窗口、媒体、语音或真实模型验收。

原独立岛已开启的登录启动偏好，只有在命令格式和程序身份均匹配时才迁移到元枢；新安装不主动开启。元枢设置页与安装器使用同一个启动项，卸载只移除仍指向本安装目录的精确启动命令。安装测试必须等待卸载日志实际成功、关闭且程序/登记移除；仅等最初启动的卸载进程退出不足以证明第二阶段已完成，参见 [Inno Setup 6.7.3 卸载实现](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Projects/Src/Setup.Uninstall.pas#L247)。
