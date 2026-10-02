# 独立发版审核修复记录 — 本地 0.5.23（2026-10-02 完成）

> 状态：**原28项根因修复与回归完成；正式发行仍未验收，不签发GO。** 最终Debug/Release各2297通过、0失败、7跳过；实际publish EXE隔离自检、发布后八组锁还原及运行工件来源核验通过。两次间歇PID/端口失败证据保留且根因未定位，不能用后续通过掩盖。
>
> 原始问题与正式基线仍以 [独立审核报告](2026-10-01-independent-release-audit.md) 为准；历史审核未改写，当前结果同步 `docs/HANDOFF.md`。0.5.23和helper `1.0.1+6004732`均为本地未发布修复候选。没有commit/push/tag/upload/Release，没有修改现有用户ACL或安装覆盖。
>
> 本记录区分“原缺陷修复与负向回归通过”和“真实发行矩阵通过”。所有源码、测试及证据路径相对仓库根；已执行结果见§7，首次失败见§1，未闭合的实机/公网条件不得记为通过。

## 1. 范围、状态与证据解释

原审核共 **28 个问题组：10 项 P1、17 项 P2、1 项 P3**。下文保留原始 ID 与标题逐字映射，不重新编号、不将组内表现拆分计数。

- **已修复并验证**：原触发条件有对应生产根因修复和实际通过的正式回归，失败条件仍明确拒绝，不用兜底、重派或吞异常遮盖。下文逐项列出测试与限定证据。
- **VERIFIED（限定范围）**：PowerShell/Go/Node/xUnit实际运行或实际发布EXE自检，结论仅适用于所描述条件；静态审查和fixture不冒充公网/UAC/目标机验收。
- **失败记录保留**：早期定向、整合和全量失败均有原始日志/TRX；随后修法或观察增强单列，后续通过不追认旧命令成功。最终源码/依赖输入2273项保持不变。
- **PENDING**：仅用于仍未取得证据的真实UAC、默认安装适用性、跨架构运行、公网H3/ECH、DPI/长跑等发行条件，以及历史间歇故障定位。

### 已取得的限定证据

| 证据 | 范围与真实结果 | 不覆盖的结论 |
|---|---|---|
| E-PS-BUNDLE：`_local-scratch/approved-installer-bundle-tests.ps1` | **VERIFIED**：exit 0；x64/x86/arm64 合计 30 次脚本结果断言。污染 baseline 仅按清单投影，用户 core/config/hidden 文件不进入新 bundle；完整树校验拒绝清单外文件、隐藏项、空目录、Redis 摘要损坏；附安装器静态信任边界断言 | 未执行原生 payload、安装器或 UAC；fixture 的 PE/宿主内容不能替代最终发布 EXE/安装升级验收 |
| E-PS-REPARSE：同轮隔离 junction 命令 | **VERIFIED**：exit 0；最终 bundle 中目录 junction 显式拒绝，不遍历目标；随后只删除该 junction | 不替代所有文件系统/UNC/权限/长路径矩阵 |
| E-PS-GO：`_local-scratch/approved-go-provenance-tests.ps1` | **VERIFIED**：exit 0；缓存官方 Go 归档对应 **14,985** 个安装输入验证；隔离副本的 compiler、linker、`src/runtime/runtime2.go` 损坏均在执行前拒绝；隐藏未列目录拒绝；继承的恶意 GOROOT/GOENV/GOTOOLCHAIN/GOFLAGS 在成功与失败后恢复；已有 RID 输出在任何构建工作前拒绝且 sentinel 保留 | 未覆盖后续生成产物、签名和最终 EXE；不执行篡改工具链，不以本地 driver receipt 代替官方归档证明 |
| E-NODE历史：`_local-scratch/outbound-approved-evidence/node-native-all.log` | 早期58通过/0失败/0跳过，含20生命周期；后续缺口修复以62项最终日志为准 | 不搬用旧结果覆盖后续修复，不是公网H3/ECH或业务鉴权验收 |
| E-GO-NATIVE：`_local-scratch/outbound-approved-evidence/go-native-final.jsonl` | 引擎负责人交付：上次 Go 检查点 **53 个顶层通过 + 40 个子测试通过，2 个 live 顶层跳过，0 失败**；含原生 helper 生命周期/鉴权及 Windows ACL 负向用例。子测试不重复算作顶层测试 | 两项 live 跳过不算网络 Gate；后续代码/产物变化后需重新验证，不证明跨架构目标机可运行 |
| E-GO-AUX：同目录 `go-test.log`、`go-vet.log`、`go-mod-verify.log` | 引擎负责人交付的检查记录；`go mod verify` 返回 `all modules verified`。`go-test.log` 为较早未启用完整原生 opt-in 的运行，**原生计数以 `go-native-final.jsonl` 为准** | 不将较早带 skip 的记录与原生记录拼成一次全通过；不替代最新源码对应的完整测试 |
| E-DOTNET-FIRST：`artifacts/repair-0.5.23-tests/repair-0.5.23-focused-Debug.trx` | 第一轮260通过/0失败/0跳过，原记录保留 | 不是最终源树全量结果；最终两配置、自检与锁门控见§7 |

### 按时间保留的整合检查点（不覆盖失败记录）

- **保留的第二轮整合失败**：`artifacts/repair-0.5.23-tests/repair-0.5.23-direct-desktop-Debug.trx` 为 **270 通过 / 10 失败 / 0 跳过，exit 1**。三个 UI drag fixture 找不到首个 Thumb；七个 `FrpSupervisorFailureTests` fixture 报 `Process was not started by this object` 或 Stop 断言失败。负责者正在根因诊断，不能将其擅自归为环境问题或写成已修复。该轮 `GoToolchainTests` 的官方缓存 opt-in 用例实际执行，无 skip。
- **后续 core-focused 检查点**：`artifacts/repair-0.5.23-tests/repair-0.5.23-core-residual-Debug.trx` 为 **268 通过 / 4 失败 / 0 跳过**，包含一个安全诊断断言与三个 drag geometry 失败；12 个 handoff 用例通过，其他 VM 用例通过。安全诊断按契约不暴露外部原始错误；随后原始异常与目标以非UI绑定属性保留，测试断言安全分类及原异常身份。实际隔离几何证据确认应使用40×20的整条轨道而不是20×20的移动Canvas，25px拖动三种结果的原断言均通过。
- **后续 residual-final 检查点**：`repair-0.5.23-residual-final-Debug.trx` 为 **167 通过 / 1 失败 / 0 跳过**，真实拖动、进程夹具与退出代次用例均通过；唯一失败是候选尚未改变磁盘时过时来源拒绝被误标为恢复失败，随后按事务所有权边界补修，必须重跑验证。
- **中途编译失败保留**：runtime-residual尝试时核心负责人仍在补写helper方法，出现3处 `RecoverUnreturnedCandidate` 未定义；这是没有等待最终原子冻结的整合时序错误，未取得TRX、不算一次通过。方法完成后residual-final已成功编译，保留原日志，不改变编译检查。
- **首次全量Debug检查点**：`repair-0.5.23-full-final-Debug.trx` 为 **2293通过/4失败/7跳过/exit1**。三个维护拒绝用例的旧Supervisor替身Stop返回Stopped但自身Snapshot保持原状态，被新Dispose正确拒绝；随后替身改为真实更新状态，原“维护不主动停止”断言保持，并新增断言验证仅Dispose阶段停止。另一失败为20轮末尾PID26568查得存活，缺少当时原进程句柄/创建时间，事后PID已不存在，**不能据此断言是PID复用或宣称生产根因已修复**；保留原日志，增强原句柄与安全身份观察并保留原PID不存活断言，后续结果另列。
- **第二次全量Debug检查点**：`repair-0.5.23-full-verified-Debug.trx` 为 **2296通过/1失败/7跳过/exit1**。原句柄增强的20loop和维护拒绝/Dispose断言均通过；唯一失败是helper已退出但端口11866仍有listener，缺少故障时owner PID，事后listener消失。只读查得本机IPv4 TCP动态端口范围为1024起、13977个；该环境事实**不证明端口复用**，未修改系统范围、不增大超时、不忽略listener。继续增加原helper句柄与IP Helper owner-PID观察；历史间歇根因仍未确认。
- **Node 补充缺口后的检查点**：引擎负责人最新交付 `_local-scratch/outbound-approved-evidence/node-review-gap-full.log`，**62 通过 / 0 失败 / 0 跳过**，含真实 native 20 轮（该 native 用例约 **53.874 秒**；整套日志约 **68.297 秒**）；负责人报告 reviewer 静态补充复核已覆盖重开缺口。该记录取代 58 用例记录作为最新 Node 模块检查点，但不签发整仓最终 Gate。
- **新生成资产检查点**：协调者交付并由 JSON 核对 `artifacts/repair-0.5.23-tests/artifact-verification-final.json` 的 `pass=true`：三 RID PE machine、各 6 个 checksum、33 个 current sourceInputs/source ZIP 字节、10 个 productionInputs 与 13 个当前 Node host 资产一致；三个 helper 共享 sourceSha256 `5a76d508466c6a1e64169c7c67ac9e71e26fb92687af42bfd4fa0d3d91a5da43`。`artifacts/bundled-runtime-repair-0.5.23-x64-final` 新建 exit 0。协调者确认 Go 输入自 fresh build 后未变、未复用过时 EXE。它仍不是签名发行包/安装验收或最终 EXE 自检。

早期 `_local-scratch/outbound-approved-evidence/node-native-test.log` 的 **30秒整套harness超时**记录保留，不算通过。新增Windows安全预检产生40次ACL检查，实测每轮启动约2.5秒；仅整套测试预算改为90秒，产品预算未改。最终62项套件68.297秒，其中真实20轮53.874秒。后续成功不能追认旧命令成功；Node补充缺口另有修前失败和修后正式结果。

## 2. P1 修复与验证（10 项）

### P1-01 管理员安装器执行用户可写 endpoint 提供的程序

- **生产根因修法**：`installer/DanmuApi.iss` 从本安装包提取可信 `DanmuApi.ExitRelay.exe`，只通过 `ExecAsOriginalUser` 执行；删除读取/执行 endpoint `exe=` 的安装器路径。`src/DanmuApi.App/Program.cs` 的 installer probe/exit 入口在 UI 启动前执行；`Services/RunningInstanceExitRequester.cs` 在任何 profile 探测前检查 TokenElevation，拒绝 elevated token，采用原用户固定 roaming `AppPaths`，不读取 configured runtime/settings。每条安装入口都执行可信原用户 probe，而非先凭管理员 APPDATA 的空锁判断无需探测。
- **判据不放宽**：probe 区分空闲、真实 modern/legacy 锁持有、失败；request ACK 不等于退出，只有实际锁可取得并释放后才完成，再由新 probe 复核。0.5.13 或更早版本不支持自动退出，非零结果保留手动托盘退出/停止服务提示。最初即提权或无法恢复原用户 token 时明确阻止，不把 wrong-profile 空锁当成功。
- **正式测试**：`RunningInstanceExitTests.InstallerRelayRefusesElevatedTokenBeforeLookingAtAnyProfile`、`InstallerProbeUsesRealModernAndLegacyLocksWithoutReadingSettingsOrEndpointExe`、`RelayIgnoresUntrustedEndpointExecutableButKeepsAuthenticationAndCompletion`、`InstallerOnlyExecutesExtractedPackageRelayAsOriginalUserAndChecksItsExitCode`、`RequestExitStillRequiresTheToken`、`ExitRequestCompletesOnlyAfterTheLockIsReleased`。
- **证据/边界**：E-PS-BUNDLE验证安装器静态信任边界。另以本机Inno7.1.0对x64/x86/arm64完整Pascal代码执行编译，**3/3 exit0**；仅隔离副本移除两个签名指令，载荷为惰性文本fixture，不执行setup/UAC、不读取签名资料。证据 `_local-scratch/installer-relay-syntax-048a6d687c194747b1a96dda1df0b07f/syntax-evidence.json`；不是可交付安装包或安装验收。真实 UAC、over-the-shoulder 切换账户、最初即管理员启动、v0.5.13 升级矩阵 **PENDING**。

### P1-02 PR 祖先关系的 GitHub compare 方向判断相反

- **生产根因修法**：`src/DanmuApi.Core/CorePullRequestPresence.cs` 将 `compare(base, head)` 的 `ahead/identical` 判为祖先包含成立，`behind/diverged` 判为不成立，未知状态保留 Unknown 与证据，不用方向反写的替身兜底。
- **正式测试**：`CorePullRequestPresenceTests.AuditCompareDirectionMatchesGitHubHeadRelativeToBase`、`UnknownCompareStatusIsNeverGuessed`、`MergedPullRequestWhoseMergeCommitIsNotInHistoryIsMissing`。
- **状态**：已修复；正式方向/未知状态回归在最终两配置均通过，断言以GitHub的head相对base语义为准。历史公共父子提交取证保留，不冒充本轮新的公网调用。

### P1-03 旧普通更新结果可无确认覆盖后来创建的 PR 组合

- **生产根因修法**：`src/DanmuApi.Core/CoreInstallationModels.cs` 中 `CoreInstallationManifest.SourcesEqual` 比较完整来源/组合身份；`CoreUpdateCoordinator.cs` 将同 SHA 的身份变化纳入结论广播。`src/DanmuApi.App/Services/CoreManagementService.cs` 在串行变更锁内以 `EnsureUpdateIsCurrent` 重新核验当前完整磁盘来源，拒绝旧普通 pending 覆盖当前组合；页面消费者刷新完整 pending 快照。
- **正式测试**：`CoreManagementServiceTests.AuditBuildingSameBaseStackBroadcastsPendingIdentityAndRejectsOldOrdinaryResult`、`AuditOrdinaryUpdateRechecksSourceAfterWaitingForMutationLock`、`AuditPreparedAndStackUpdatesRejectEveryChangedSourceField`；`CoreUpdateCoordinatorTests.AuditReconciliationBroadcastsEveryChangedSourceIdentity`、`AuditEquivalentReparsedPrListDoesNotCauseSpuriousBroadcasts`；`CorePageViewModelTests.AuditPagePendingRefreshesCompleteSameBaseIdentityWithoutAnotherCheck`。
- **状态**：已修复；完整来源、锁等待、pending刷新和交接回归在最终两配置均通过，独立残余路径已复核。

### P1-04 PR 组合“仅更新”候选健康失败后不恢复原核心

- **生产根因修法**：`src/DanmuApi.App/Services/CoreManagementService.cs` 的 update-only 分支与 `src/DanmuApi.Core/CoreInstaller.cs`/`CoreInstallationModels.cs` 共用 prepared branch 安装与受保护备份确认/恢复协议；候选健康成功前不结束恢复点。失败停止候选、恢复旧核心并核对旧服务健康，同一 mutation gate 内完成，保留候选与恢复失败诊断。
- **正式测试**：`CoreManagementServiceTests.AuditUpdateOnlyHealthFailureRollsBackAndKeepsBothFailureDiagnostics`、`AuditUpdateOnlyRecoveryHoldsLockUntilOldServiceHealthVerified`、`PreparedRecoveryHoldsMutationGateUntilOldCoreIsRestored`。
- **状态**：已修复；候选健康失败/恢复锁、同步Completed/Inspect/取消/清理报告回调异常及无变更过时拒绝的正式回归在最终两配置通过；先前交接探针前后对照见§6。

### P1-05 frp 清理失败后丢失跟踪，应用仍可能报告成功退出

- **生产根因修法**：`src/DanmuApi.Runtime/Frp/FrpSupervisor.cs` 只有确认退出后才释放 process/plan/PID；拒杀或清理失败保持受管所有权和可重试 Stop，Dispose 传播失败。`src/DanmuApi.App/Services/FrpTunnelService.cs` 和 `AppLifecycleCoordinator.cs` 通过 shutdown barrier 排空启动/联动，等待 Node、frp、更新调度暂停，并在状态、PID/HasOwnedProcess 全部确认后才允许桌面退出；`src/DanmuApi.Runtime/RuntimeController.cs` 阻止退出期间排队启动。
- **正式测试**：`FrpSupervisorFailureTests.FailedStartupCleanupRetainsHandlePlanPidAndRetriesStop`、`DisposePropagatesSafetyRefusalWithoutDestroyingRetryableStop`；`FrpLifecycleFailureTests.ShutdownDrainsInFlightStartBlocksQueuedStartsAndKeepsCleanupRetryable`；`RuntimeShutdownBarrierTests.ShutdownRejectsQueuedStartsAndDisposeFailureKeepsStopRetryable`；`AppLifecycleShutdownTests.ConcurrentExitCallersWaitForTheSameTunnelBarrierResult`、`AStoppedSnapshotCannotAuthorizeExitWithRetainedNodeOwnership`。
- **状态**：已修复；停止重试/Dispose/并发退出和确定性queuedresume回归在最终两配置均通过，独立复核确认准入代次。未放宽归属、未强杀无关进程；历史间歇PID/端口失败仍单列未知。

### P1-06 配置路径子串匹配可误认领并终止手动 frp

- **生产根因修法**：`src/DanmuApi.Platform/WindowsProcessArguments.cs` 按 Windows 参数规则拆分，精确识别无歧义 `-c/--config` 参数并比较规范化完整路径。`WindowsProcessTerminator.cs` 不再以 `CommandLine.Contains` 授权 frp 终止，原可执行文件/PID/创建时间归属约束继续保留。
- **正式测试**：`WindowsProcessArgumentsTests.FrpRequiresAnExactUnambiguousConfigArgument`、`ParserPreservesSpacesUnicodeAndBackslashQuoteRules`；`FrpSupervisorFailureTests.DisposePropagatesSafetyRefusalWithoutDestroyingRetryableStop`。
- **状态**：已修复；精确参数和测试拥有实进程拒绝/停止回归在最终两配置均通过，不以PID名称或路径前缀放行。

### P1-07 frp 扫描拒绝诊断可能写出手动进程的认证 Token

- **生产根因修法**：`src/DanmuApi.Platform/WindowsProcessTerminator.cs` 的归属拒绝诊断只报告 PID/失败检查项，不输出实际完整 CommandLine；`src/DanmuApi.Runtime/Frp/FrpSupervisor.cs` 保留拒绝原因而不记录手工参数凭据。敏感信息在写日志前消除，不依赖 UI 二次遮盖。
- **正式测试**：`FrpSupervisorFailureTests.ActualMetadataRefusalNeverLeaksTokenMarkerOrRawCommandLine`（使用非秘密 synthetic marker）。
- **状态**：已修复；合成Token标记、实际归属拒绝诊断回归在最终两配置均通过，不记录真实认证资料。

### P1-08 设置损坏阻止“核心停止必停隧道”

- **生产根因修法**：`src/DanmuApi.App/Services/FrpTunnelService.cs` 对所有非 Running 核心状态按既有受管计划停隧道，不依赖新设置重读；只有自动启动方向读取严格设置，损坏设置诊断仍保留。
- **正式测试**：`FrpLifecycleFailureTests.CorruptedNewSettingsCannotBlockStopOnAnyNonRunningCoreState`；`FrpTunnelServiceTests.TunnelStopsWheneverTheServiceStops`。
- **状态**：已修复；所有非Running状态与损坏设置联动停止的正式回归在最终两配置均通过。

### P1-09 frp 代理消失/closed/未知状态仍持续显示 Running 和旧地址

- **生产根因修法**：`src/DanmuApi.Runtime/Frp/FrpSupervisor.cs` 分离 probe 与发布；非 running/空代理观测撤销旧公网地址，更新真实重连原因/代理快照。启动和刷新均在最后进程存活复核后才发布 Running。
- **正式测试**：`FrpSupervisorFailureTests.NonRunningObservationClearsOldAddressAndPublishesTruthfulReconnecting`、`RefreshMustRecheckLivenessBeforePublishingRunning`、`StartupMustNotPublishRunningBeforeItsFinalLivenessCheck`。
- **状态**：已修复；缺失/closed/waiting/未知和最后存活复核回归在最终两配置均通过。真实frps断网长跑矩阵仍PENDING，旧地址不充当已验证入口。

### P1-10 共享自定义运行目录中的 helper 私有会话没有 Windows ACL 保护

- **生产根因修法**：`runtime/node-host/app-outbound-runtime.js` 在生成 helper token/私密 pending 文件之前调用并验证受管安全目录；`runtime/outbound/src/secure_directory_windows.go` 的 prepare/verify 模式校验 owner、DACL、祖先可替换权限、reparse、helper EXE 与其目录，建立当前用户及必要系统主体可访问的受保护 DACL。每次写私密 pending 之前再验证；不能证明安全时失败，不降级到 Unix `0o600`。
- **正式测试**：Go `TestWindowsSecureDirectoryProtectsPendingBeforeWrite`、`TestWindowsSecureDirectoryRejectsReplaceableAncestor`、`TestWindowsSecureDirectoryRejectsWritableExecutable`、`TestWindowsSecureDirectoryAllowsReadonlyAncestors`；Node `session ACL preparation precedes helper token and verify precedes every pending session byte`、`ACL rejection blocks helper startup and writes no token session or pending file`。
- **证据/边界**：已修复；真实Windows保护/不安全祖先负向Go用例、Node写前保护及最终x64 EXE鉴权启停通过。当前不安全profile仍明确失败；默认安装/共享路径完整适用矩阵PENDING。差异详见§5，不宣称所有默认profile均可运行。

## 3. P2 修复与验证（17 项）

### P2-01 快捷 PR 更新按页面选中变体，而非待更新的变体核对

- **生产根因修法**：`src/DanmuApi.App/ViewModels/CorePageViewModel.cs` 的快捷 PR 路径固定绑定 `update.Variant`，检查目标安装、调用 presence analyzer、标题和应用均使用该目标；异步页面选择变化不改变正在核对的对象。
- **正式测试**：`CorePageViewModelTests.AuditQuickPrUpdateUsesPendingVariantForInspectionEvidenceAndPrompt`、`AuditQuickPrUpdateVerifiesActualDevTargetEvenWhenSelectedStableIsNotInstalled`、`AuditQuickPrUpdateKeepsTargetAndDoesNotPublishHealthAcrossAsyncPageChanges`。
- **状态**：已修复；上述正式回归在最终Debug/Release均通过，见§7。

### P2-02 IPv6-only listener 错误阻止 IPv4 Node 启动

- **生产根因修法**：`src/DanmuApi.Runtime/PortAvailability.cs` 按目标地址/地址族匹配 listener，区分 IPv6-only 与会阻断 IPv4 的双栈监听；继续保留 Windows 非独占 IPv4 listener 检测，不回退为“普通 bind 成功就空闲”。
- **正式测试**：`PortAvailabilityTests.IPv6OnlyLoopbackListenerDoesNotBlockIPv4`、`IPv6OnlyWildcardListenerDoesNotBlockIPv4`、`DualStackListenerBlocksIPv4`、`TargetAddressDoesNotConflictWithAnotherIPv4Interface`、`IPv6OnlyListenerDoesNotDelayIPv4ReleaseCheck`、`ProbeReportsListeningEvenWhenANonExclusiveBindWouldSucceed`。
- **状态**：已修复；IPv6-only/dual-stack/目标地址与非独占IPv4、真实Node预检回归在最终两配置均通过。

### P2-03 frp JSON 严格解析存在忽略和未分类异常

- **生产根因修法**：`src/DanmuApi.Core/Frp/FrpConfigJson.cs` 对出现的公共/角色/代理受管字段检查类型、范围、重复键及数组元素；畸形输入分类为 Problems，不抛未经分类的访问/字典异常，不用旧值替代错误输入。
- **正式测试**：`FrpConfigJsonTests.BothRolesClassifyMalformedCommonFields`、`ClassifiesMalformedClientRootFields`、`ClassifiesMalformedManagedProxyFieldsEvenWhenInactive`、`DuplicateKeysAreClassifiedWithoutEscapingExceptions`、`ExtraProxyShapeAndManagedElementsAreStillValidated`。
- **状态**：已修复；上述正式回归在最终Debug/Release均通过，见§7。

### P2-04 frp 导入、导出和 TOML 生成不保真

- **生产根因修法**：`src/DanmuApi.Core/Frp/FrpConfigJson.cs` 定义整份导入的清除/默认语义并报告 AppliedDefaults，不继承旧可选字段或 auth；`FrpConfigWriter.cs` 各代理种类保留 compression/encryption。`src/DanmuApi.App/ViewModels/FrpTunnelConfigViewModel.cs` 从同一有效表单快照导出，认证采用明确 keep/set/clear 草稿意图，不混入另一份已保存凭据。
- **正式测试**：`FrpConfigJsonTests.WholeServerImportClearsOldOptionalFieldsAndReportsDefaultsAndTokenClear`、`WholeClientImportResetsOptionsRatherThanInheritingSavedValues`、`AllProxyKindsRoundTripCompressionAndEncryption`；`FrpConfigWriterTests.EveryProxyKindGeneratesCompressionAndEncryption`；`FrpTunnelViewTests.ExportUsesKeepSetAndClearTokenDraftAndClearDoesNotWriteBeforeSave`、`WholeImportWithoutAuthStagesClearAndPreservesOppositeRoleDraft`。
- **状态**：已修复；完整草稿导入/导出、认证意图和各代理transport回归在最终两配置均通过；HTTP/HTTPS的JSON/TOML由官方frpc verify实际接受，不读取真实Token取证。

### P2-05 Follow 快捷开关可覆盖整份 frp 参数

- **生产根因修法**：`src/DanmuApi.App/Services/FrpSettingsStore.cs`/`FrpTunnelService.cs` 的快捷开关严格重读磁盘，只 patch Follow 键；读取损坏时显式失败且不写其他设置。
- **正式测试**：`FrpConfigurationPersistenceTests.FollowShortcutRereadsDiskAndPatchesOnlyItsKey`、`MalformedDiskBlocksFollowAndSaveRatherThanWritingFallbackValues`。
- **状态**：已修复；上述正式回归在最终Debug/Release均通过，见§7。

### P2-06 frp 表单草稿保护、实时校验及服务端 Token 入口不完整

- **生产根因修法**：`src/DanmuApi.App/ViewModels/FrpTunnelConfigViewModel.cs` 以完整私有草稿指纹跟踪代理/TLS/compression/encryption/认证意图，字段变化即时校验，saved 状态更新不覆盖脏草稿；`Views/FrpTunnelConfigView.axaml` 为客户端/服务端都提供认证输入与清除入口。指纹不写日志/UI。
- **正式测试**：`FrpTunnelViewTests.ValidationUpdatesWithoutSavingAndNeverDefaultsBlankManagedFields`、`EveryPreviouslyOmittedDraftFieldSurvivesSavedConfigurationChanges`、`ChangesToSavedProxyOptionsUpdateAnUntouchedForm`、`TokenDraftCanBeRevertedToKeepWithoutDiscardingOtherFields`、`ServerRoleHasUsableTokenInputAndClearControls`。
- **状态**：已修复；完整草稿、实时验证与服务端Token控件回归在最终两配置均通过。DPI/输入法完整实机矩阵仍PENDING。

### P2-07 Node 请求桥静默改变部分原生请求语义

- **生产根因修法**：`runtime/node-host/app-outbound-bridge.js` 在 dispatch 前将无法忠实支持的 raw request-target、显式 Host、fetch integrity 等语义留给原生请求，保留原 Request/init/重载输入；302/303 POST 改 GET 时删除全部 body 相关 header，而不是仅删除部分字段。
- **正式测试**：`node-tests/app-outbound-bridge.test.cjs` 的 `unsupported ClientRequest raw target and explicit Host bypass before dispatch unchanged`、`fetch integrity and Host bypass preserve Request/init identity with native semantics`、`302/303 POST redirects remove every request body header`；后续补充 `mutable ClientRequest Host rejects before any helper or native dispatch`、`mutable ordinary ClientRequest headers still dispatch once with unchanged business values`。
- **证据/边界**：已修复；62项Node包含构造时不接管、可变Host发送前拒绝和普通首部单次派发；独立复核确认，最终bundle与源码一致。原生绕过只用于已知不支持语义，组件故障不得降级。

### P2-08 helper 小块慢响应被缓冲到 EOF

- **生产根因修法**：`runtime/outbound/src/proxy.go` 在 header 发出后及时 Flush，每个上游 chunk 的 Write/Flush 同步传播错误，保留背压，不增加脱离取消/时限的后台缓冲泵。
- **正式测试**：`TestProxyDeliversSmallChunkBeforeEOF`、`TestFlushingWriterPropagatesFlushFailure`、`TestProxyCopyHonorsBackpressureAndWriteFailure`（`proxy_stream_test.go`）。
- **证据/边界**：已修复；真实小块EOF前交付、Flush错误和背压回归Go通过，最终helper源码/ZIP/PE/摘要一致。

### P2-09 helper 上传体读取不受请求时限约束

- **生产根因修法**：`runtime/outbound/src/proxy.go` 将真实 server Body.Read 的连接 read deadline 纳入同一请求 context，取消时推进 deadline 解阻；不完整上传不复位过期 deadline，防止 net/http 响应阶段继续无限 drain。读取完成且 context 仍有效才允许业务 dispatch，保留大小上限和失败诊断。
- **正式测试**：`TestProxySlowUploadDeadlineHasZeroDispatch`、`TestAppProxyDisconnectCancelsUpstream`。
- **证据/边界**：已修复；Go约30ms budget实际解开仍未关闭上传且zero dispatch，断开取消回归通过；最终helper来源核验通过，不增大生产超时。

### P2-10 helper /request JSON 接受畸形协议

- **生产根因修法**：`runtime/outbound/src/request_decode.go` 在结构体/字典折叠前 token 化解码，严格检查唯一键、完整必需字段、字符串/整数类型、精确二元 header tuple 和尾随文档；`main.go` 在解码/策略检查成功前不 dispatch。
- **正式测试**：`TestRequestDecoderRejectsMalformedProtocolWithoutDispatch`（`request_decode_test.go`）；兼容空 body/headers：`TestEmpty204ResponseKeepsRequiredFields`。
- **证据/边界**：已修复；重复/未知/缺失/null/tuple/尾随文档负向用例与空204兼容Go回归通过，非法文档零dispatch；新资产来源重验通过。

### P2-11 主页面启用开关拒绝/保存失败后视觉状态漂移

- **生产根因修法**：`src/DanmuApi.App/Views/OutboundDirectView.axaml` 使用由实际已保存值控制的输入控件，不让 ToggleSwitch 乐观反转成为另一真相；`ViewModels/OutboundDirectViewModel.cs` 的拒绝/保存失败按实际可验证保存状态同步，不伪造回滚。
- **正式测试**：`OutboundDirectViewTests.SavedStateEnableInputNeverDriftsOrOptimisticallyReverses`（鼠标/键盘等输入及拒绝/失败组合）；`OutboundDirectViewModelTests.DisabledSourcesCanBeClearedButEnablingCannotInventDefaultSources`。
- **状态**：已修复；pointer/键盘/真实25px拖动在空来源、拒绝保存和挂起状态下均保持实际保存值，最终两配置通过。

### P2-12 停止竞态把直连 VM 从 off 覆盖回 ready

- **生产根因修法**：`src/DanmuApi.App/Services/OutboundDirectService.cs` 发布与返回使用同一最终验证结果，按 runtime epoch/revision 拒绝旧代次，发布前复核当前 runtime；`ViewModels/OutboundDirectViewModel.cs` 丢弃过时返回/排队 Changed 通知，采用服务实际最终快照。
- **正式测试**：`OutboundDirectServiceTests.FinalLivenessStopReturnsTheActualPublishedSnapshotNotTheCandidate`、`StopAndSameIdentityRestartInvalidatesThePreviousVerificationEpoch`、`StopDuringHealthResponseCannotPublishReadyAgain`；`OutboundDirectViewModelTests.StaleRefreshReturnCannotReplaceAnOffNotification`、`QueuedOldReadyAndOldOffCannotOverrideLaterStopOrRestart`。
- **状态**：已修复；停止/同身份重启/排队通知与最终快照回归在两配置均通过，旧ready不得覆盖新off。

### P2-13 整次测速预检失败伪装为完整结果并覆盖历史

- **生产根因修法**：`src/DanmuApi.App/Services/OutboundDirectService.cs` 用 `OutboundDiagnosticRun` 区分 Completed/Failed/Cancelled，只携带实际已执行请求行；整次失败不捏造零耗时逐域名结果或完成时间。`ViewModels/OutboundDirectViewModel.cs` 只提交完整结果到最近历史，取消/失败保留旧完成记录。
- **正式测试**：`OutboundDirectServiceTests.PreflightFailureHasNoInventedRowsAndNoCompletionTime`、`CancellationReturnsCancelledRunWithOnlyActualRequestRowsAndNoCompletionTime`；`OutboundDirectViewModelTests.IncompleteTestsPreserveTheLastCompletedCountsTimeAndConfiguration`、`IncompleteEnvelopeNeverReplacesCompletedHistory`。
- **状态**：已修复；上述正式回归在最终Debug/Release均通过，见§7。

### P2-14 测速分类和记录关联到 VM 旧配置

- **生产根因修法**：`src/DanmuApi.App/Services/OutboundDirectService.cs` 在一次测速 envelope 中传递不可变、已验证的实际配置与完成时间，前/后校验 runtime/session/config；VM 按该次配置分类并关联 Recent，不取缓存表单或 VM 旧配置。
- **正式测试**：`OutboundDirectServiceTests.DiagnoseUsesOneActuallyVerifiedConfigurationAndRealCompletionTime`、`PostvalidationRejectsChangedConfigurationSessionOrRuntime`；`OutboundDirectViewModelTests.ActualRunConfigurationClassifiesRowsAndLinksHistoryEvenWhenVmCachedAnotherConfig`、`QuickSourceChangesKeepCompletedRowsAndMarkTheirOriginalConfiguration`。
- **状态**：已修复；上述正式回归在最终Debug/Release均通过，见§7。

### P2-15 长 JSON 弹窗将取消/确认按钮挤出窗口

- **生产根因修法**：`src/DanmuApi.App/Services/UiDialogService.cs` 的 `PromptMultilineTextAsync` 使用固定动作区的 `Auto,*,Auto` Grid，文本区内部滚动且受窗口约束，不以放大窗口代替溢出修复。
- **正式测试**：`OutboundDirectViewTests.LongMultilinePromptKeepsActionsInsideWindowAndTextScrollsInternally`（可编辑/只读）。
- **状态**：已修复；200行长JSON在560/400/320受限高度、编辑/只读模式下滚动与固定按钮的真实控件几何回归两配置通过；150%/200%DPI实机仍PENDING。

### P2-16 bundle 校验不拒绝清单外文件，发行递归带入

- **生产根因修法**：`build/New-OutboundRuntimeBundle.ps1` 的 ValidateOnly 对主清单、元数据和 optional Redis 构造精确文件树，`-Force` 检查隐藏项，逐目录检查后才下降；拒绝清单外文件/目录和 reparse。Create 不盲目拒绝污染 baseline，而是只复制被批准、已验证的主/Redis 清单项，用户配置/核心/日志/cache 不随包。`build/Build-WindowsRelease.ps1` 按主清单与 Redis 清单投影 runtime，并在最终 runtime 树用于安装/归档前再次验证。
- **正式测试**：`OutboundRuntimeBundleTests.BundleScriptAssemblesFreshOfflineBundleForEachPeArchitecture`、`ReleaseStructureGateRejectsEveryUnlistedFileAndDirectoryIncludingHidden`、`ReleaseStructureGateRejectsDirectoryJunctionBeforeFollowingIt`、`OptionalRedisPayloadIsVerifiedByTheFinalReleaseGate`、`ReleaseScriptProjectsManifestFilesAndRevalidatesAfterPublishWithoutRecursiveRuntimeCopy`。
- **证据/边界**：已修复；正式bundle负向两配置通过，30组隔离脚本与真实junction拒绝、最终及publish旁bundle闭合清单验证通过。未创建正式ZIP/安装包；synthetic fixture不是公开发行资产验收。

### P2-17 Go 构建继承 GOROOT，来源记录可能失真

- **生产根因修法**：`build/Get-GoToolchain.ps1` 以固定官方归档 URL/SHA256/字节数验证完整 installed tree，记录 driver/compiler/linker/标准输入和全输入 digest；在 version/env 检查前固定 GOROOT、GOENV=off、GOTOOLCHAIN=local 并验证实际 GOTOOLDIR。`Build-OutboundHelper.ps1` 所有调用都验证该环境，finally 恢复继承值；记录 sourceInputs、每 RID 的实际 productionSourceInputs，构建后核对原始输入未变化；OutputRoot 必须新建，不覆盖旧 RID 产物。`New-OutboundRuntimeBundle.ps1` 与 `src/DanmuApi.Platform/BundledRuntimeStartup.cs` 拒绝缺失/畸形官方来源及 compiler/linker/标准输入 metadata。
- **正式测试**：`GoToolchainTests.AllGoCallsPinRootAndDisableUserConfigAndBuildRecordsCompleteToolProvenance`、`OfficialCachedTreeRejectsCompilerLinkerStandardInputAndHiddenExtrasAndRestoresEnvironment`；`OutboundRuntimeBundleTests.CompleteOfficialToolchainProvenanceCannotBeMissingOrMalformed`。
- **证据/边界**：已修复；Go缓存篡改/环境恢复opt-in正式回归两配置通过，三架构构建与33源码/ZIP/生产输入/官方编译器链接器标准库摘要核验通过。全输入摘要 `8e86d357d24994679569a68c9c1977347799a99b18b58beb151e6577813049b9`来自官方归档，不以可改写本地receipt作信任依据。

## 4. P3 修复与验证（1 项）

### P3-01 托盘“核对 PR 后更新（打开核心页）”实际打开概览

- **生产根因修法**：`src/DanmuApi.App/Services/TrayService.cs` 注入独立的目标核心页导航回调，以 pending 的 `Variant` 导航；普通 pending 仍走原应用入口。组合更新不再误用打开概览回调。
- **正式测试**：`TrayServiceTests.PullRequestPendingUpdateOpensTargetCorePageInsteadOfOverview`、`OrdinaryPendingUpdateStillUsesPendingApply`。
- **状态**：已修复；目标变体核心导航与普通pending原路径的正式Avalonia回归两配置通过。

## 5. ACL 环境证据：失败与安全根不可混淆

1. **原始默认 profile 下的路径不是自动可信**。引擎负责人在原始测试环境观察到 ACL 安全检查拒绝：原始 profile/default temporary 路径具有不符合 helper 私密会话边界的其他用户访问或可替换祖先权限。该条件再次出现时仍必须显式失败；本轮没有通过放宽可信 SID、取消祖先检查或改写用户目录 ACL 让它“通过”。这也不等于宣称所有机器默认 profile 都不安全。
2. **安全隔离根是另一组明确条件**。原生负责人专门新建受保护的可信根，证据为 `_local-scratch/outbound-approved-evidence/secure-root.json`；主协调者的隔离 TEMP 根记录在 `_local-scratch/repair-secure-root-0.5.23.json`。JSON 保存本机 root/owner/SDDL 与用途；本文不硬编码个人机器路径/SID。主协调者记录明确为隔离测试新根，未修改已有用户目录权限。
3. E-NODE 的真实 helper 鉴权/20 轮、E-GO-NATIVE 的 pending DACL/可替换祖先/可写 EXE 负向断言是在相应安全隔离条件下执行。**“安全隔离根通过”不能覆盖“原始不安全 profile 拒绝”，两者都应保留为事实**。
4. 默认正式安装目录、便携目录、自定义共享 runtime_root 的完整实机适用性 **PENDING**；无法证明安全的运行路径应返回失败诊断，不能静默改用普通网络、不受保护 session 或默认成功。

## 6. 后续 reviewer 残留与补充回归

| 残留标签 | 关联与待证边界 | 当前结论 |
|---|---|---|
| **corehandoff** | Report/Inspect/取消及清理报告异常在返回备份句柄之前不能丢失事务责任；无磁盘变更的过时拒绝不能制造恢复失败 | 已修复；原/当前探针对照、24个交接用例、严格过时来源回归均纳入最终168项定向通过；独立helper复核无残余 |
| **queuedresume** | 失败退出恢复时不能使旧排队启动复活；Start/Adopt/Restart、frp/Follow/自动生命周期按准入代次判定 | 已修复；真实gate控制的确定性排序回归纳入168项通过；独立静态复核确认旧代次拒绝、新请求可用 |
| **failclosedempty** | disabled空sources之后遇到非法配置，不能以旧空选择授权原生网络 | 已修复；62项Node含四源fetch/https零native派发负向与合法禁用恢复；独立静态复核确认 |
| **mutableHost** | 创建后改写Host不能被静默丢弃或改路重发 | 已修复；setter/批量setter/removal/prototype发送边界零helper首部、零native派发及正常首部单派发正向，纳入62项Node通过；独立复核确认 |

最终修正后的共享xUnit定向结果为 **168通过/0失败/0跳过/exit0**，TRX `artifacts/repair-0.5.23-tests/repair-0.5.23-residual-closed-Debug.trx`。该轮涵盖核心页面/安装器、退出准入、frp进程夹具及真实开关输入。额外的finally报告回调分支是同一API交接保证的补强，不增加原28项计数；旧失败记录和根因均保留。服务级JSON凭据脱敏另有81项（27形式×状态/helper/异常链）在诊断sink之前验证，独立复核确认不依赖VM遮盖。

补充正式入口均在最终Debug/Release实际通过：

- `CoreInstallerTests.AuditArchiveUpdateRechecksCompleteSourceAtReplacementAfterDownload`：2case/config，过时来源严格拒绝而不触动外来磁盘。
- `CoreInstallerTests.AuditPostReplacementFailureBeforeBackupHandoffRestoresOldDisk`：8case/config；`AuditPostReplacementRollbackFailurePreservesOriginalAndRecoveryErrors`：4；`AuditCleanupDiagnosticCallbackFailureCannotDestroyCandidateHandoffOrOriginalFailure`：12。
- `RuntimeShutdownBarrierTests.ResumeBeforeQueuedContinuationCannotRevivePrePauseOrPausedRequests`：3case/config；`FrpLifecycleFailureTests.FailedDrainResumeBeforeGateContinuationCannotReviveOldStartRequests`：4。
- `OutboundDirectServiceTests.ServiceRedactsQuotedAndEnvironmentCredentialsBeforeLoggingWithoutAVm`：81case/config，状态/helper/异常链均在sink之前脱敏。
- Node `invalid config after disabled empty selection blocks every supported route with zero native dispatch`：最终日志ok34，四源零native派发及显式合法禁用恢复。

最终独立证据复核确认 **28/28问题组均有通过的正式入口**；86处C#引用对应85个不同方法，按TRX的className+method核对，在两配置均Passed；Node/Go引用均在实际日志通过。原28ID/标题和顺序零差异。该复核只验证证据覆盖，不重复源码审查、不签发GO。

不搬用旧58项Node或260项C#冒充补充修复通过；最终源/工件/self-test闭环见§7。若今后变更源码、构建脚本或Node资产，必须重新生成受影响工件，不能改写metadata冒充新二进制。

## 7. 最终验证结果与未验收条件

| 门控 | 结果 | 证据与限定 |
|---|---|---|
| 最终定向修复回归 | **168通过/0失败/0跳过，exit0** | `repair-0.5.23-residual-closed-Debug.trx`；核心交接/页面、退出代次、原进程句柄及真实开关输入 |
| 完整 .NET Debug / Release | **各2297通过/0失败/7跳过，exit0** | `artifacts/repair-0.5.23-tests/repair-0.5.23-full-observed-{Debug,Release}.trx`；真实Node/helper/frp与20loop启用；跳过明细在下方 |
| 发布后8组CI/ForceEvaluate锁还原 | **8/8 exit0，零NU错误** | `artifacts/repair-0.5.23-tests/lockgate-final/results.json`；Debug/Release×无RID/win-x64/win-x86/win-arm64，禁止up-to-date假门控 |
| 最终Node三份正式测试 | **62通过/0失败/0跳过，exit0** | `node-review-gap-full.log`；20轮真实helper、四源坏配置零native派发和可变Host零helper派发；源码与最终bundle字节一致 |
| Go test / vet / mod verify | **53顶层+40子测试通过、0失败、2live跳过；vet/mod verify exit0** | `go-native-final.jsonl`（PowerShell UTF-16）；子测试不重复算顶层。`TestLiveH3ECH`/`TestNativeHelperLiveRequests`仍未验收 |
| 三架构helper/最终bundle来源 | **通过** | `artifact-verification-final.json`：每RID PE、6摘要、33源码/ZIP、10生产输入；官方14985工具链输入；13当前Node资产；最终和publish旁bundle闭合清单6480项离线验证 |
| 实际publish EXE隔离self-test | **exit0** | `published-exe-self-test.{json,txt}`；完整离线依赖部署、helper鉴权启停/清理、配置保留、COM通知提交；不是测试宿主，不是已签名发行包 |
| 冻结源码/依赖完整性 | **2273/2273不变** | `frozen-source-inputs-observed-final.json`→`final-evidence-summary.json`；最终两配置、publish和锁还原没有改变依赖图或源码输入 |
| 安装器语法与可信relay回归 | **静态/正式回归通过，Inno三架构编译3/3 exit0** | 惰性、签名关闭fixture的完整Pascal代码编译；真实UAC/原用户/换账户/旧版升级/卸载矩阵仍 **PENDING** |
| 公网同次H3+ECH及真实业务 | **PENDING** | 不将live跳过、H2/ECH历史观察、401或备用节点成功冒充H3+ECH完整业务 |
| 跨架构/Win10与11/DPI/路径/24h矩阵 | **PENDING** | 本轮仅x64实机；x86/arm64构建/PE核验不等于目标机运行。150%/200%DPI、真实frps断网长跑等未完成 |
| 正式发版判断 | **不签发GO，未发布** | 原28项修复与回归完成，但未知间歇失败和真实发行矩阵尚未闭合 |

### 七项 .NET 跳过（两配置相同）

- `FrpBinaryInstallerNetworkTests.InstallDownloadsVerifiesAndLandsAUsableBinary`
- `FrpBinaryInstallerNetworkTests.CorruptedCacheIsRejectedAndRefetchedInsteadOfInstalled`：未启用真下载网络opt-in，不作下载验收。
- `NativeNotificationTests.IsolatedWindowsNotificationIsAcceptedAndIdentityIsCleaned`：测试host的专用opt-in未启用；独立的实际publish EXE通知提交已通过，但不追认这条测试执行。
- `OptionalRedisStagerTests.RealBundlePayloadStagesAndPackagedNodeResolvesIt`：未提供可选Redis真载荷。
- `CoreDependencyServiceTests.ActualResearchPackSignatureSchemaAndWindowsExtractionValidateWithoutNetwork`：未提供研究依赖真实包fixture。
- `CorePullRequestMergeServiceTests.CopyRejectsReparsePointsAndNeverDescendsIntoGitMetadata`：当前用户无法创建所需符号链接；另有真实junction拒绝证据，但不等价为本条通过。
- `BundledRuntimePreparerTests.PublishedKotlinMigratesOnlyAfterCompleteTrustedHashesAndPreservesUserData`：未提供已发布Kotlin迁移fixture。

名称、失败历史和计数均由TRX直接提取到 `final-evidence-summary.json`。原生观察 `native-observations-final.json` 包含两配置共10项成功：20轮Node、helper正常/突停、HTTP/HTTPS两种官方frpc JSON/TOML验证。最初PID26568和端口11866两次失败没有当时原身份/owner证据，**根因仍未知**；新的观察增强、严格断言和最终通过不是生产根因修复证明。自检EXE SHA256为 `3DCF1E3C17C549BDCC3965B13896AFE3CB5A71FFCDF6CDB3BD82D8A341415C3F`，位于 `artifacts/publish-repair-0.5.23-win-x64/`，未安装到用户目录。

## 8. 已执行验证命令与重复执行约束

以下从仓库根执行。测试脚本显式提供真实Node/helper/frp、LONG_SMOKE=1、官方Go缓存opt-in和新建受保护TEMP。命令拒绝覆写既有TRX、工件或证据；重复执行需选新RunLabel/输出根，不删除首次失败记录。不得读取签名秘密、真实session token或用户配置。

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\repair-tests-0.5.23.ps1 -Configuration Debug -RunLabel full-observed -BundlePath .\artifacts\bundled-runtime-repair-0.5.23-x64-final
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\repair-tests-0.5.23.ps1 -Configuration Release -RunLabel full-observed -BundlePath .\artifacts\bundled-runtime-repair-0.5.23-x64-final
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\repair-publish-0.5.23.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\repair-lockgate-0.5.23.ps1 -RunLabel final
# $python为已定位解释器；均为本轮已执行结果汇总。
& $python .\_local-scratch\verify-repair-artifacts-0.5.23.py
& $python .\_local-scratch\extract-native-observations-0.5.23.py
& $python .\_local-scratch\repair-final-evidence-0.5.23.py finish --label observed-final --run-label full-observed
git diff --check
```

引擎实际执行 `$node --test` 的三份文件：`node-tests/app-outbound-bridge.test.cjs`、`app-outbound-runtime.test.cjs`、`app-outbound-host.test.cjs`，其中真实helper在独立保护根下运行。Go在 `runtime/outbound/src` 执行 `go test -count=1 -json ./...`、`go vet ./...`、`go mod verify`；实际环境固定官方GOROOT、GOENV=off、GOTOOLCHAIN=local，Windows原生opt-in已启用，证据见§1。

**结论：原28项修复与回归已交付，本地0.5.23候选已构建和验证；没有正式发版。** 公网/UAC/跨架构/DPI/长跑等验收和未知间歇故障仍须在正式发行前闭合，不因本轮测试通过而变为GO。
