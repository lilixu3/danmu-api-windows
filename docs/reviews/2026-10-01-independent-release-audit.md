# v0.5.13 → 当前工作树（0.5.22）独立发版审核

日期：2026-10-01  
结论：**NO-GO，当前不建议正式发版。**  
审核状态：已完成；只做审核和隔离取证，**未修复实现、未提交、未推送、未发布**。

## 1. 摘要与范围

确认 **28 个问题组：10 项 P1、17 项 P2、1 项 P3**。跨模块重复发现已合并；问题组内的多个相关表现不重复计数。P1 应在正式发版前消除根因并回归；P2 涉及配置保真、请求兼容性、状态可信度等，不应仅因旧测试通过而忽略；P3 为入口导航错误。

### 正式基线

- GitHub 最新正式 Release 是 **v0.5.13**，非 draft、非 prerelease，发布时间 `2026-09-23T09:31:25Z`，12 个发行资产。
- annotated tag 对象为 `9cb690289ab50096e08de408636e812cd0de0ca6`，解析至提交 **`856904275e54cb64d4d03dc9e67103c714db20f6`**。
- 本地 HEAD、v0.5.13 提交和缓存 origin/main 相同；`v0.5.13..HEAD` 无新提交，暂存区为空。
- **0.5.14～0.5.22 均为本地未提交版本，不是正式发布基线。** 审核范围包含其全部工作树改动，不只审核最后一次界面调整。
- 快照包含 **52 个已跟踪修改文件、119 个未跟踪文件，共 171 个文件**；已跟踪差异为 +2677/−187 行，另有二进制图片。未跟踪项目还包含工具记忆、图片和零字节 shell 遗留文件，不把它们全部算成生产代码。
- `docs/HANDOFF.md`、specs、部分发布协议等被 `.gitignore` 排除，普通 `git diff` 不覆盖这些资料；本轮另行读取相关协议与证据。
- 结束前逐文件重算 SHA256：**171/171 不变**。审核新增内容仅为 scratch 证据、本报告及 HANDOFF 记录，未修改被审实现和原测试。

### 审核方式与覆盖

由相互独立的基线、核心更新、进程生命周期、frp、Go/Node 增强直连、桌面直连集成、发布打包审查分工完成，并对跨模块调用链交叉核验。不是以功能作者的测试结论代替代码审查。

- 核心：PR 合并/存在性分析、更新协调、安装事务、GitHub 访问和应用更新相关修改；用真实服务/VM 加隔离替身复现状态交错。
- 运行时：NodeSupervisor、RuntimeController、端口检测、进程归属、单实例、退出请求、托盘与生命周期完整相关调用链。
- frp：26 个范围内生产文件，包含协议模型/JSON/TOML/管理解析、安装器、监督器、服务和四组页面/VM。
- 增强直连引擎：全部 8 个生产 Go 文件、go.mod/go.sum，全部 13 个权威 Node 宿主文件。
- 桌面增强直连：设置存储、服务、VM、主页面/两个弹窗、SavedStateCheckBox、DI 与模板；补充真实 pointer 和长 JSON 弹窗几何探针。
- 发布：工程配置和依赖锁、Go 获取/构建、bundle 生成/验证、发布脚本、Inno 安装器、runtime 部署/维护及最终 x64 资产只读验证。
- 测试文件用于核对覆盖和执行回归；没有宣称逐行审计所有既有测试、所有忽略目录或外部弹幕核心业务。

## 2. P1：发版前应解决的确定缺陷

### P1-01 管理员安装器执行用户可写 endpoint 提供的程序

**位置：** `installer/DanmuApi.iss:32`、`:178-190`、`:203-205`、`:238-251`。

安装器要求管理员权限，却读取当前用户 APPDATA 下 `instance.endpoint` 的 `exe=`，仅检查存在即 `Exec(ExePath, '--request-exit 60', ...)`。Inno 的 Exec 使用 Setup 凭据，不是原用户的中完整性凭据。

**触发与影响：** 同用户中完整性进程能修改 endpoint 并持有 instance.lock；用户正常给可信安装器授权 UAC、确认安全退出后，可令 endpoint 中任意程序获得安装器管理员权限。无需知道会话 token。执行后复查锁不能撤销此前的高权限执行。

**证据：** 静态权限/数据流、隔离字段解析及 Inno 官方语义；未执行安装器或载荷。见 `release/installer-static-evidence.json`。

**建议：** 使用随包可信 relay 或可信安装器自身发送退出请求，并保留原用户权限；不能在高权限上下文执行用户可写文件提供的路径。验证路径/签名也必须纳入明确的信任模型，不用执行后检查代替。

### P1-02 PR 祖先关系的 GitHub compare 方向判断相反

**位置：** `src/DanmuApi.Core/CorePullRequestPresence.cs:251-261`。

调用 `compare(ancestor, descendant)` 后，将 `behind` 当包含、`ahead` 当未包含。GitHub 的状态是 head 相对 base：祖先到后代应为 `ahead`。

**触发与影响：** 正常分支推进误报 PR 缺失；远端回退到 PR 之前却可能误报已包含，使用户在“仅更新”时误丢本地 PR 内容。

**证据：** 公共真实父子提交 `c42877ccf0deeda2ed394a83903b4b2ab3dac41c → fc1b7ff6add61d8af24c9bf978253273833f5afc` 返回 `ahead/ahead_by=1/behind_by=0`，反向为 behind；实际 analyzer 输出 `ahead → Missing`、`behind → Contained`。见 `core/github-compare-public-evidence.json`、`core/probe.stdout.txt`。

**建议：** `ahead/identical` 为祖先关系成立，`behind/diverged` 为不成立；同步纠正 spec02 的对应方向描述，使用真实方向的父/子样例防止测试替身与实现一起写反。

### P1-03 旧普通更新结果可无确认覆盖后来创建的 PR 组合

**位置：** `src/DanmuApi.App/Services/CoreManagementService.cs:565-568`、`:900-910`；`src/DanmuApi.Core/CoreUpdateCoordinator.cs:148-153`、`:290-299`。

普通核心 A 检查得到待更新 B，随后用户在 A 上创建 PR 组合，manifest 的基础 CommitSha 仍为 A。旧 pending.Local 仍是普通核心；协调器比较结论不包含完整 Local 身份，未广播新的组合身份。应用旧结果时又仅按 repo/branch/SHA 验证，绕过 PR 确认。

**证据：** 实际服务探针显示 `disk_stack=True/coordinator_stack=True/pending_stack=False/broadcasts=0`；应用旧 pending 成功、普通安装调用 1 次，随后 `disk_stack=False`。见 `core/probe.stdout.txt`。

**建议：** 在变更锁内核验当前磁盘完整身份和 PR 队列，普通更新不得应用于当前组合；身份改变必须通知并失效所有 pending 消费者。不能只增加 UI 按钮提示，因为托盘等入口仍持有旧结果。

### P1-04 PR 组合“仅更新”候选健康失败后不恢复原核心

**位置：** `src/DanmuApi.App/Services/CoreManagementService.cs:494-512`、`:777-782`；`src/DanmuApi.Core/CoreInstaller.cs:203-215`。

无重新并入 PR 的分支使用普通 InstallAsync，不走 prepared 事务；旧组合在候选健康确认前已替换/归档，启动失败后只保留 Failed，未恢复旧组合。

**证据：** `succeeded=False/disk_applied=True/disk_stack=False/restored_old_backup=0/service=Failed`。见 `core/probe.stdout.txt`。

**建议：** 与 prepared 分支统一事务边界，健康成功后才结束备份保护；失败时停止候选、恢复旧核心并验证旧服务，在同一串行变更内显式报告恢复结果。

### P1-05 frp 清理失败后丢失跟踪，应用仍可能报告成功退出

**位置：** `src/DanmuApi.Runtime/Frp/FrpSupervisor.cs:697-719`、`:232-236`、`:412-427`；`src/DanmuApi.App/Services/AppLifecycleCoordinator.cs:145-153`；frp 联动接线 `src/DanmuApi.App/App.axaml.cs:197-200`。

启动失败清理即使终止器明确失败，仍 ClearProcess、清空 plan、丢 PID；再停止会直接报告 Stopped。Dispose 忽略 Stop 返回的 Failed。应用退出仅等待/验证 Node，frp 异步停止和失败未纳入成功门控。

**影响：** 进程仍存活但监督信息丢失；应用可能退出、释放实例锁、让安装器认定退出成功，却留下隧道。此项合并 frp 与 runtime reviewer 的重复发现。

**证据：** frp 探针 `Failed → Stopped`，终止调用仅 1 次而自有进程仍活；运行时真实临时子进程经真实归属验证拒绝终止后，`LIFECYCLE_EXIT_RESULT=True/SHUTDOWN_CALLED=True/CHILD_ALIVE=True`，Dispose 正常完成。只清理审查创建的句柄，未停止用户服务。见 `frp/probe-output.log`、`runtime/runtime-probe.log`。

**建议：** 只有确认退出才能释放句柄/计划；失败保留所有权和可重试停止入口。退出协调器等待全部受管进程并校验停止成功。安全拒杀必须保留，不能以放宽归属或强杀其他进程来修复。

### P1-06 配置路径子串匹配可误认领并终止手动 frp

**位置：** `src/DanmuApi.Platform/WindowsProcessTerminator.cs:254-263`；孤儿扫描调用 `src/DanmuApi.Runtime/Frp/FrpSupervisor.cs:605-623`。

归属检查仅 `CommandLine.Contains(expectedConfig)`。手动使用同一个 frpc.exe、配置为 `frpc.toml.manual` 的进程也匹配 `frpc.toml`。

**证据：** 对实际生产归属谓词输入该路径，`Accepted=true`。未实际杀用户进程。见 `frp/probe-output.log`。

**建议：** 按 Windows 参数规则拆分命令行，精确识别 -c/--config 并比较规范化完整路径；结合创建时间/启动身份。不要仅凭 PID、名字或子串判断归属。

### P1-07 frp 扫描拒绝诊断可能写出手动进程的认证 Token

**位置：** `src/DanmuApi.Platform/WindowsProcessTerminator.cs:84-87`；`src/DanmuApi.Runtime/Frp/FrpSupervisor.cs:635-637`。

归属拒绝返回完整实际 CommandLine，新 frp 扫描直接写入诊断。手动 frpc 支持 `tcp --token ...`，因此不同配置的手动进程会把认证参数带进宿主日志/诊断。底层诊断模板部分为既有代码，但新增扫描使该敏感参数场景进入新增功能。

**证据：** 可信 frpc 的帮助确认 -t/--token；对审查自有进程进行实际元数据查询，拒绝诊断包含 `--token AUDIT_NOT_A_SECRET`、`LeaksMarker=true`。标记不是秘密。见 `frp/frpc-tcp-help.log`、`frp/probe-output.log`。

**建议：** 不记录实际完整命令行，报告 PID 和匹配失败项即可；必要参数经结构化脱敏，禁止依赖 UI 二次遮盖已经写出的日志。

### P1-08 设置损坏阻止“核心停止必停隧道”

**位置：** `src/DanmuApi.App/Services/FrpTunnelService.cs:813-821`。

联动先 ReloadCore，读取/解析失败即跳过，停止方向也被新设置读取阻断。

**证据：** 将隔离存储的 `frp_server_port` 改为 `bad-int`，核心 Stopped 后 `StopCalls=0/TunnelState=Running`；合法设置对照为 StopCalls=1/Stopped。见 `frp/probe-output.log`。

**建议：** 对所有非 Running 核心状态，直接按既有运行计划执行停止，不依赖新设置；只在自动启动方向读取并严格验证设置。保留配置异常诊断。

### P1-09 frp 代理消失/closed/未知状态仍持续显示 Running 和旧地址

**位置：** `src/DanmuApi.Runtime/Frp/FrpSupervisor.cs:346-356`、`:448-454`、`:482-489`。

不确定结果只写 ProbeResult.Note；Refresh 仅看 Diagnostic，最终返回旧 Running 快照。

**证据：** 当前 Running 下分别给出空代理、closed、waiting、未知状态，四次均保持 Running、旧公网地址、无诊断。另在最终存活复核前发布 Running，可出现 `Starting → Running → Failed`。见 `frp/probe-output.log`。

**影响与建议：** 用户可复制已不可用的旧入口，状态可能长期不转重连；违反 spec05 的运行判据。非 running 观测必须清除已证可用地址，更新原因及代理列表；分离探测/发布，在最终存活复核后才能发布 Running。

### P1-10 共享自定义运行目录中的 helper 私有会话没有 Windows ACL 保护

**位置：** `runtime/node-host/app-outbound-runtime.js:116-123`、`:253`、`:301-302`。

私有 session 使用 Unix `0o600`，但 Windows pending/最终文件仍继承父目录 DACL。

**触发与影响：** 自定义 runtime_root 位于其他账户可读/改的共享目录时，对方可取得 endpoint/token 调用 helper，具有写权限时还能改写会话/设置。**不表示默认用户 profile 必然泄漏。**

**证据：** 直接调用生产 atomicJson 写无敏感标记文件，实际继承 `BUILTIN\\Users:ReadAndExecute`、`Authenticated Users:Modify`。见 `outbound-engine/acl-evidence.json`。

**建议：** 在生成 pending session 前建立并验证专用目录/文件 DACL，限制为当前用户及必要系统主体；不能证明权限安全时明确失败，不能仅凭 0o600 报 ready。

## 3. P2：确定的功能、数据和兼容性问题

### P2-01 快捷 PR 更新按页面选中变体，而非待更新的变体核对

**位置：** `src/DanmuApi.App/ViewModels/CorePageViewModel.cs:719-721`、`:736-739`；`MainWindowViewModel.cs:327-335`。

目标为 Dev 组合、页面选中 Stable 时，实际 VM 报“当前核心不是本地 PR 组合”；两个变体都有组合时可能展示另一套 PR。证据 `core/probe.stdout.txt`。所有核对、标题和 manifest 读取应绑定 `update.Variant`。

### P2-02 IPv6-only listener 错误阻止 IPv4 Node 启动

**位置：** `src/DanmuApi.Runtime/PortAvailability.cs:34-42`、`:71-74`。

新全系统监听表仅比较端口，不区分地址族。真实 `::1:4955/DualMode=false` 下 IPv4 可绑定，Probe 却 Listening，默认 0.0.0.0 的 Node 启动失败并误报外部实例。证据 `runtime/runtime-probe.log`。应按目标地址/族判断冲突，同时保留对 Windows 非独占 IPv4 监听的检测，不能回退成“普通绑定成功即空闲”。

### P2-03 frp JSON 严格解析存在忽略和未分类异常

**位置：** `src/DanmuApi.Core/Frp/FrpConfigJson.cs:184-190`、`:267-283`、`:357-384`、`:416-434`、`:470-476`。

webServer 数字、auth.method 数字、addr 数字/端口 0、transport=false 等错误输入仍 Succeeded；数字域名抛 InvalidOperationException，重复键抛 ArgumentException，未转 Problems。证据 `frp/probe-output.log`。应检查所有出现的受管字段类型/范围、重复键和数组元素，并统一拒绝为诊断结果，不能沿用值掩盖输入错误。

### P2-04 frp 导入、导出和 TOML 生成不保真

**位置：** `src/DanmuApi.Core/Frp/FrpConfigJson.cs:78-90`、`:203`、`:401-414`；`FrpConfigWriter.cs:44-52`；`src/DanmuApi.App/ViewModels/FrpTunnelConfigViewModel.cs:223`、`:270-275`。

HTTP/HTTPS 已保存 compression 未导出/生成；整份导入只给 bindPort 时静默保留旧 vhost/泛域名且没有 AppliedDefaults。导出取表单其他参数，却取已保存 Token；无 auth 导入也不清掉旧 Token。前者有动态探针，Token 混用有完整源码数据流证据。见 `frp/probe-output.log`。定义整份导入的清除/默认语义；导出使用同一份有效表单快照，所有可编辑选项应保真或明确拒绝。

### P2-05 Follow 快捷开关可覆盖整份 frp 参数

**位置：** `src/DanmuApi.App/Services/FrpTunnelService.cs:282-288`；`FrpSettingsStore.cs:124-148`。

未首次 Reload，或磁盘外部变更而内存旧时，开关以旧完整设置写回。隔离服务端配置只改 Follow 后变为 client、地址空、vhost=0。见 `frp/probe-output.log`。应严格重读，并只修改 Follow 键；读取失败须显式失败而不覆盖配置。

### P2-06 frp 表单草稿保护、实时校验及服务端 Token 入口不完整

**位置：** `src/DanmuApi.App/ViewModels/FrpTunnelConfigViewModel.cs:373-381`、`:463-495`；`src/DanmuApi.App/Views/FrpTunnelConfigView.axaml:46-69`、`:138-163`。

脏状态指纹缺少 ProxyKind/TLS/compression/encryption/Token，相关未保存输入可能在已保存字段变化时被 LoadFields 覆盖。字段变更未触发实时 Validate。Token 输入/清除仅位于客户端可见区域，服务端无直接入口。属确定源码证据。应完整跟踪草稿、实时校验并为两个角色提供认证设置入口。

### P2-07 Node 请求桥静默改变部分原生请求语义

**位置：** `runtime/node-host/app-outbound-bridge.js:64-67`、`:149-155`、`:188-192`、`:206-211`、`:247-251`。

原 ClientRequest `/a/../signed` 被规范化为 `/signed`，自定义 Host 被删除；POST 302/303 改 GET 后仍保留 Content-Encoding/Content-Language；fetch 错误 integrity 原生拒绝，桥却成功。这些均有正式 Node 22.23.2 原生对照，见 `outbound-engine/node-evidence.json`。未证明当前业务使用 integrity，不升 P1。应忠实保留 request-target/redirect/integrity；无法支持的语义明确不接管，不能静默改写。

### P2-08 helper 小块慢响应被缓冲到 EOF

**位置：** `runtime/outbound/src/proxy.go:116-123`。

WriteHeader 后直接 io.Copy，没有 Flush。真实 loopback 服务器已写 first-chunk，但 180ms 内客户端连 headers 都未收到；EOF 才到达。见 `outbound-engine/go-evidence.log`。影响首块延迟和慢流增量处理；应及时 flush，保留背压、取消和写失败诊断，以“EOF 前收到首块”验证。

### P2-09 helper 上传体读取不受请求时限约束

**位置：** `runtime/outbound/src/proxy.go:69-73`、`:136-140`。

timeout context 未解除 local.Body 的阻塞读取；32MiB 仅限大小。15ms deadline 下 180ms 后仍等待上传，关闭 pipe 后才 timeout，业务 dispatch=0。见 `outbound-engine/go-evidence.log`。上传阶段须纳入同一 deadline，并在取消时解除读取。

### P2-10 helper /request JSON 接受畸形协议

**位置：** `runtime/outbound/src/main.go:25-31`、`:93-104`、`:136-145`。

未知字段、重复 url、缺失/null headers/body、超过二项的 header tuple 均被接受。五类离线输入 HTTP 200，各 dispatch 一次。见 `outbound-engine/go-evidence.log`。最终 URL 仍经 allowedTarget，**不是白名单绕过**。应严格检查键唯一、字段存在/类型及 tuple 长度。

### P2-11 主页面启用开关拒绝/保存失败后视觉状态漂移

**位置：** `src/DanmuApi.App/Views/OutboundDirectView.axaml:75-78`；`src/DanmuApi.App/ViewModels/OutboundDirectViewModel.cs:377-383`、`:449-450`。

标准 ToggleSwitch 先自行反转；空来源拒绝或未落盘保存失败时 VM.Enabled 未变，OneWay 不纠正当前值。真实 pointer 两用例均为控件 true、VM false、保存 false，来源 SavedStateCheckBox 对照正确。见 `outbound-desktop` 探针结果。应采用由实际保存状态唯一控制的开关输入路径，覆盖鼠标/键盘/拖动，而非仅执行 Command。

### P2-12 停止竞态把直连 VM 从 off 覆盖回 ready

**位置：** `src/DanmuApi.App/Services/OutboundDirectService.cs:197-199`、`:751-755`；`OutboundDirectViewModel.cs:499-505`。

Publish 已把过时 ready 钳制 off，RefreshAsync 仍返回原候选 ready，VM 用返回值覆盖 Changed 已送达的 off。确定性隔离竞态得到 Runtime Stopped、Service off、VM ready、IsEngineReady=true。证据 `outbound-desktop/stop-race.json`。发布与返回须为同一最终验证结果，拒绝过时代次；下一轮轮询能纠正不代表本次发布正确。

### P2-13 整次测速预检失败伪装为完整结果并覆盖历史

**位置：** `src/DanmuApi.App/Services/OutboundDirectService.cs:337-340`；`OutboundDirectViewModel.cs:550-565`。

status 损坏/会话预检失败时服务生成零耗时逐域名失败行，VM 当完成结果保存计数和时间。探针实际新增 /request=0，但历史“需要密钥 2”变 0、失败变 2、17:00 变 17:05，显示“测速完成”。应以结构化结果区分宿主整次失败、取消、完整域名结果；未实际完成时保留旧完成记录。

### P2-14 测速分类和记录关联到 VM 旧配置

**位置：** `src/DanmuApi.App/Services/OutboundDirectService.cs:329-343`；`OutboundDirectViewModel.cs:535`、`:552`、`:593-599`。

轮询间磁盘改为 h3，服务按新配置测，VM 按缓存 h2 分类和关联 Recent。探针 h3/TMDB401 被判连接失败，记录 h2 且未标配置变化。结果应携带本次不可变、已验证配置快照，目标选择、会话核验、分类、Recent 使用同一快照。

### P2-15 长 JSON 弹窗将取消/确认按钮挤出窗口

**位置：** `src/DanmuApi.App/Services/UiDialogService.cs:1037-1045`、`:1061-1076`。

无上限 StackPanel 令多行 TextBox 按内容长高。实际调用 PromptMultilineTextAsync、200 行非敏感 JSON：客户区 560，TextBox 1953，确认按钮 Y=2040～2058，完全越界。见 `outbound-desktop/multiline-geometry.json`。改为固定按钮区的 Auto,*,Auto Grid，文本区内部滚动/高度受约束；单纯允许放大窗体不足以解决长配置。

### P2-16 bundle 校验不拒绝清单外文件，发行递归带入

**位置：** `build/New-OutboundRuntimeBundle.ps1:147-153`；`build/Build-WindowsRelease.ps1:64`、`:79`；`installer/DanmuApi.iss:48`。

ValidateOnly 只验证清单条目，后续复制/归档整个树。隔离 fixture 添加清单外 config/.env、danmu_api_stable/worker.js、outbound/foreign.exe，校验仍 exit0。见 `release/manifest-extras-evidence.json`。**当前最终 ZIP 未发现用户数据，不声称已经泄漏。** 应拒绝完整树的未授权项或仅按验证清单复制，并检查最终发行树。

### P2-17 Go 构建继承 GOROOT，来源记录可能失真

**位置：** `build/Build-OutboundHelper.ps1:20`、`:48-54`、`:129`；`build/Get-GoToolchain.ps1:23-27`、`:53-58`。

固定 go.exe/GOTOOLCHAIN 不固定 GOROOT。官方 go.exe 的版本仍 1.26.0，但 GOTOOLDIR 可指向继承根目录；完整替代根目录可供另一套 compiler/linker/标准库。隔离只读 env 探针确认选择机制，未执行替代工具链构建，**不声称当前产物被污染**。见 `release/go-root-env-evidence.json`。构建前固定/验证 GOROOT 并隔离用户 Go 配置，记录实际使用工具而非仅驱动摘要。

## 4. P3：入口文案与实际导航不一致

### P3-01 托盘“核对 PR 后更新（打开核心页）”实际打开概览

**位置：** `src/DanmuApi.App/Services/TrayService.cs:143-147`、`:218-220`；`src/DanmuApi.App/App.axaml.cs:732-737`。

新分支调用 _openConsole，其注入实现 NavigateTo("overview")；属确定调用链证据。应注入打开 core 的专用动作并增加入口导航断言，不能仅更换通知文字。

## 5. 本轮验证及其边界

| 验证 | 实际结果 |
|---|---|
| .NET Debug 全量 | **1933 通过、0 失败、7 跳过，exit 0** |
| .NET Release 全量 | **1933 通过、0 失败、7 跳过，exit 0** |
| Node 宿主测试 | **53 通过、0 失败、0 跳过，exit 0** |
| Go test / vet / mod verify | **全部 exit 0；all modules verified** |
| Debug/Release × noRID/x64/x86/arm64 CI/ForceEvaluate 锁还原 | **8/8 exit 0** |
| 独立桌面负向/对照探针 | **7/7 观察断言成立，exit 0** |
| 核心/frp/runtime/引擎/发布独立探针 | **复现上述缺陷，exit 0** |
| 被审文件 SHA256 重验 | **171/171 不变** |
| 报告计数/文件行范围检查 | **10 P1 / 17 P2 / 1 P3；45 个完整引用位置有效** |
| git diff --check | **exit 0；仅既有 LF/CRLF 提示，无空白错误** |

完整 .NET 回归启用了真实 Node、真实 helper、frp fixture 和 20 循环长冒烟。**“缺陷探针 exit 0”表示成功复现问题，不是产品通过验收。** 现有测试全部通过与本报告并不矛盾：方向反写、状态交错、失败传播、输入控件、权限边界等此前未被正确覆盖。

七项跳过为：两项 frp 实际下载/损坏缓存网络测试、原生通知 opt-in、optional Redis 真 bundle、外部研究依赖包签名 fixture、reparse point 权限用例、旧 Kotlin bundle 迁移。跳过不算对应 Gate 通过。

### 最终现有 x64 资产只读核验

- `artifacts/signed-0.5.22/` 五项资产摘要、RSA 更新清单、ZIP 7474 项、ZIP EXE 与 publish EXE 一致性已独立核实；未见用户 core/config/log/session 路径入包。
- Node Authenticode 为 Valid。App/setup 的发布者一致，但系统链状态含 UntrustedRoot；不写成系统信任通过。
- 读取既有最终 EXE `--verify-app-release`、`--release-self-test` 实际 exit0 报告，确认隔离准备/helper 启停/鉴权/配置保留证据；本轮没有重新安装、签名或执行统一发布脚本。
- 自检不是“真实 v0.5.13 升级到本版、升级中断、卸载数据保留”的替代。

## 6. 未闭合的验收与既有风险

### 验收缺口，不冒充确定代码缺陷

- 公网 **同次 H3＋ECH 完整响应** Gate 仍未通过；H2/ECH 和构建能力不等同 H3 公网可用。
- TMDB 带有效凭据的真实业务、默认 3000ms 下完整业务链未完成验收；401 仅证明传输与认证失败，不能算业务成功。
- 真实公网 frps、断网/重连、PR 核对/组合更新全链路、Win10/11 安装/升级/卸载保留及中断恢复矩阵未闭合。
- win-x86/win-arm64 helper 构建与 PE 核验不等于目标机运行验收；150%/200% DPI、路径/权限矩阵和 24h 长跑仍需证据。
- 0.5.20/0.5.21 记录中的 Node PID-alive、临时 helper/frp 文件清理瞬时失败根因仍未确定。本轮两配置全量通过仅说明未重现，不能追认根因已解决。

### 基线既有业务重派风险，单列不算本次新增阻断

`runtime/node-host/android-server.js:550-565` 在 Worker timeout/exit 后重派完整 payload。隔离调用原函数得到 deliveries=2，第一次是否已产生副作用并无保证。但该函数与 v0.5.13 **逐字相同**，不归因于新 Go 握手竞争/新 Node 桥。建议后续仅在能证明尚未派发时允许重派，需独立修复与副作用测试。

### 发布资料需要同步

- `docs/WINDOWS_RELEASE.md:5` 仍写 0.3.1。
- `docs/APPLICATION_UPDATES.md:33`、`:37`、`:67` 的 CER/SHA、仅 x64、latest 0.5.0，与现发布协议三架构/12资产/不上传 CER/SHA 不一致。
- `docs/specs/04-acceptance-checklist.md:82-83` 与 spec02 新 PR 核对/更新契约冲突。
- 更新日志 0.5.14 应明确本地未发布，避免被当成正式基线。
- 关键协议/HANDOFF 被忽略：正式交付前应明确哪些必须随仓库保存，避免接手者无法从源码仓库重建验收依据。本轮不擅自改变 .gitignore。

其他未证实风险（如 PID 查询到 taskkill 的复用窗口、frp 管理请求端口归属复核/重定向、慢盘/UNC 总预算）保留为待验证，不计入 28 项确定缺陷。

## 7. 证据索引与复现

所有审计 scratch 路径以下列根目录为前缀：`_local-scratch/audit-0.5.22/`。

- `reviewed-worktree-snapshot.json`、`tracked-baseline.diff`：基线及 171 文件快照。
- `source-unchanged.json`、`evidence-summary.json`：结束完整性和回归汇总。
- `core/probe.stdout.txt`、`core/github-compare-public-evidence.json`：四项核心问题。
- `runtime/runtime-probe.log`：IPv6-only 及退出失败传播。
- `frp/probe-output.log`、`frp/frpc-tcp-help.log`、`frp/coverage-sha256.json`：frp 动态和覆盖证据。
- `outbound-engine/node-evidence.json`、`acl-evidence.json`、`go-evidence.log`：桥对照、Windows ACL、Go 离线探针。
- `outbound-desktop/results/outbound-audit-final.trx`、`stop-race.json`、`multiline-geometry.json` 及其余 JSON：7 个负向/对照探针。
- `release/*-evidence.json`：安装器信任边界、bundle 清单外文件、Go 根目录及资产只读校验。
- `lockgate-results.json`、八个 `restore-*.log`：新鲜锁还原。
- `artifacts/audit-0.5.22-tests/independent-audit-0.5.22-{Debug,Release}.trx`：本轮全量结果。

以下命令在仓库根目录执行；`$dotnet/$node/$go/$python` 为本机实际定位的可执行文件，分别对应 .NET SDK 8.0.424、内置 Node 22.23.2、Go 1.26.0 和 Python。不要把 PATH 上仅有运行时的 dotnet 当 SDK。scratch launcher 内保留了本轮精确机器命令，通用文档不写个人绝对工具路径。

```powershell
# 已执行，两配置均1933通过/0失败/7跳过。
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\audit-0.5.22-full-tests.ps1 -Configuration Debug
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\audit-0.5.22-full-tests.ps1 -Configuration Release
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\audit-0.5.22\lockgate.ps1

# 独立探针：exit0是复现问题，不是通过产品验收。
& $dotnet run --project .\_local-scratch\audit-0.5.22\core\CoreAuditProbe.csproj -p:RestorePackagesWithLockFile=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryBuildProps=false -p:ImplicitUsings=enable -p:Nullable=enable
& $dotnet run --project .\_local-scratch\audit-0.5.22\runtime\RuntimeAudit.csproj
& $dotnet run --project .\_local-scratch\audit-0.5.22\frp\AuditProbe.csproj -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false
& $dotnet test .\_local-scratch\audit-0.5.22\outbound-desktop\AuditDesktop.csproj -p:RestoreLockedMode=false --logger 'console;verbosity=detailed'
& $node .\_local-scratch\audit-0.5.22\outbound-engine\audit-node.cjs
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\audit-0.5.22\release\repro-installer-static.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\audit-0.5.22\release\repro-manifest-extras.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\audit-0.5.22\release\repro-go-root-env.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\audit-0.5.22\release\verify-release-readonly.ps1

# 已执行，171文件不变；读取已有TRX/日志，不重新跑测试。
& $python .\_local-scratch\audit-0.5.22\verify-source-unchanged.py
& $python .\_local-scratch\audit-0.5.22\collect-evidence.py
```

原 Node 测试覆盖 `node-tests/app-outbound-bridge.test.cjs`、`app-outbound-runtime.test.cjs`、`app-outbound-host.test.cjs` 三份文件，使用内置 Node；复现可显式执行 `node --test node-tests/app-outbound-bridge.test.cjs node-tests/app-outbound-runtime.test.cjs node-tests/app-outbound-host.test.cjs`，不依赖 Windows shell 通配展开。Go 原测试在 `runtime/outbound/src/` 执行 `go test -count=1 ./...`、`go vet ./...`、`go mod verify`。独立 Go 探针在 scratch 的 `outbound-engine/go/` 执行 `go test -run '^TestAudit' -count=1 -v .`，使用隔离 GOMODCACHE/GOCACHE、GOTOOLCHAIN=local、GOPROXY/GOSUMDB=off；具体本机证据保留于该目录，不与原工程门控混淆。

## 8. 发版判断

现有自动化门控通过，但不足以覆盖本次确认的权限跨越、PR 数据丢失、隧道归属/残留和状态失真。**本轮审核完成，正式发版不通过。**

修复必须针对每项根因，并将上述负向观察转为产品正确行为的回归断言；不能以自动回滚默认值、静默重试、放宽归属、扩大超时或弱化断言使症状消失。正式候选还须重新运行两配置回归、八组依赖锁、最终 EXE 自检及未闭合的真实发行验收。

本报告不授权任何实现改动或发布动作。当前 0.5.22 仍是本地测试包，不把它升格为正式发行。
