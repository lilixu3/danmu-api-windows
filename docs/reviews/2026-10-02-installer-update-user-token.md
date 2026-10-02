# 应用内安装更新权限交接修复记录

日期：2026-10-02。正式基线为 v0.5.23；原本地修复候选为 0.5.24，相关验证记录保留。2026-10-03 按用户要求将本次修复合入 v0.5.23 同版本重建，替换既有发行版三架构产物及签名清单，不新建版本或改写原 tag。修复源码同步到 main；最终发行资产与构建提交以现有发行页的替换说明为准。

## 根因与可复现证据

用户原更新任务的 installer.log 在 PrepareToInstall 报原用户中继退出 1，Setup 随后退出 7；没有开始文件复制。原应用确已退出，安装目录仍为 0.5.22。这与用户报告一致。

旧更新助手通过 `UseShellExecute=true, Verb=runas` 启动 Setup。Inno 的 `ExecAsOriginalUser` 无法从已明确提升的入口恢复普通用户令牌，随包中继因此仍以提升的管理员令牌运行，被现有安全检查拒绝。两条隔离原生启动链取得以下实测：

| 启动链 | 中继实际令牌 | 原用户 probe 结果 |
|---|---|---|
| 普通 ShellExecute，Setup 自身请求 UAC | Limited / Medium | 10：当前用户实例锁确实被占用 |
| 旧助手 `Verb=runas` | Full / High | 1：stderr 明确拒绝管理员令牌 |

原任务没有保存历史中继 stderr，不能恢复当时每项原生元数据；上述实验复现了同一旧入口的确定性权限冲突。退出成功不代表后续实例验证成功，不能将错误归因于用户未退出或端口占用。

证据保留于 `scratch/exit7-proof-20261002/`。复现使用独立 AppId，在复制前中止，不接管实际应用。

## 修复

- 新助手由普通 ShellExecute 启动安装器，由 Setup manifest 请求 UAC。
- 新安装器兼容旧助手 runas 入口。随包签名中继固定原应用进程句柄，验证实际映像、创建时间和安装目录，在原应用还存活时取得其普通用户令牌。提升的原应用只可使用同 SID/会话的 linked limited token；无法取得合法令牌时在 ready 前显式失败。
- 通过显式 token KnownFolder 取得原用户目录，用户任务和实例锁操作同步 impersonation，不读提升账户 profile，不执行 endpoint 中的程序，不读运行目录设置，不杀其他实例。
- 原应用退出后取得现代 instance.lock 和旧 app.lock，原子发布 prepared，在安装复制期间持续持锁。错误先报告并继续持锁到 Setup 的 finish 或原 Setup 句柄退出；超时不成为提前释放依据。
- Setup 保存 CreateProcessW 原生 handle/PID，检查中继存活并在复制前复核；正常结束要求释放两把锁、released 和退出 0。
- 更新助手增加安全分类诊断；x86 安装识别使用 Registry32，其他目标使用 Registry64；更新安装器不读写提升账户 HKCU 自启项。

## 原生协议修正

1. Inno `Exec(...ewNoWait...)` 的 ResultCode 实测为 259，不能当作 PID。改用 CreateProcessW 的 PROCESS_INFORMATION，实际进程句柄、PID、含空格参数和退出码逐项一致；不存在的程序显式返回 Win32 2。
2. 32 位 Setup 的 STARTUPINFO/PROCESS_INFORMATION 为 68/16 字节，CommandLine 使用按值 String 的 UTF-16 指针。`var String` 为二级指针，不符合 Win32 ABI。
3. 原生固定大小 TokenElevation/TokenSessionId 等信息使用精确 4 字节缓冲区，TokenLinkedToken 使用 IntPtr.Size。原空缓冲长度查询在本机产生 ERROR_BAD_LENGTH，已保留失败 TRX 并修正根因。
4. 实际 Setup 主程序与提取中继的 `{tmp}` 位于不同目录。签名元数据 fixture 证明实际 `.tmp` 带现有发行证书和时间戳，父目录 owner 为 Administrators，普通用户仅读取执行。中继分别固定实际 Setup 映像并验签，验证实际创建者 PID 和创建时间；不要求两目录相等，也不拿外层安装包哈希冒充内层映像。
5. 中继使用独立、排序、双 NUL 结尾的 UTF-16 环境块启动，大小写不敏感地过滤 DOTNET_/CORECLR_/COMPlus_/COR_/COREHOST_，单独加入 DOTNET_EnableDiagnostics=0。原生 fixture 的 3 个真实子进程确认 NUL 后 Unicode 哨兵仍完整、所有前缀被过滤、SystemRoot/TEMP 保留；成功和不存在 exe（Win32 2）后父进程的完整环境均未改变。证据为 `scratch/envnativefixture/env-installer.log`。删除旧的父环境修改/恢复逻辑，同时消除删除不存在变量返回 203 的误拒；启动线程句柄在 finally 内关闭。

签名实测只出现既有自签根 UntrustedRoot，未改变证书信任或签名身份。原生 ABI 和签名证据分别保留于 `scratch/exit7-native-proof-20261002/`、`scratch/exit7-signed-metadata-20261002/`。

## 验证状态

初次定向回归编译成功，129 通过 / 1 失败；失败定位到固定大小 token API 的 ERROR_BAD_LENGTH。修正后定向 Debug 回归为 140 通过 / 0 失败 / 0 跳过。后续实际 Setup 验证、原生创建者用例、诊断和环境块修改需要以最终统一结果为准。

验证中保留了以下新增失败与根因，不将重跑成功追认为首次成功：

- 诊断正则的可变有界重复使 NonBacktracking 自动机达到 2440 节点，超出 .NET 默认 1000 限制。初轮完整 Debug：2353 通过 / 40 失败 / 7 跳过，40 项均为诊断类型初始化错误；改为线性字符匹配，匹配前限制行长 1024，保留白名单和固定文本对照。随后定向测试 194 通过 / 1 失败，定位到类型字符类漏掉 Win32Exception 的数字；修正后 195 通过 / 0 失败 / 0 跳过。
- 真实签名同源中继的 Verify 在 AccessCheck 返回 Win32 1338；安全描述符原只读取 Owner/DACL，缺少 AccessCheck 所需的 Group。修复必须补齐 OWNER|GROUP|DACL 并保留权限拒绝，增加真实安全描述符的原生回归；不能跳过 AccessCheck。
- 补齐 Group 前的完整 Debug 为 2396 通过 / 0 失败 / 7 跳过。同期 Release 的 helper 停止用例在 helper 已退出、端口无匹配 owner 的验证之后，因测试临时 node.exe 删除拒绝失败；该事件另取证，不能标作全量通过。

## 真实同源隔离升级流程

补齐 Group 后，使用同源 lease（生产 SHA256 `ED8F3F14C848B6817CC7290424A51B8E4865ED0F705FBB817FA19D57270C750D`）及冻结 Inno Code 的两条签名隔离流程均完成，Setup 退出 0：

- 旧 `UseShellExecute=true, Verb=runas` 链：验证签名与真实 Setup 创建者、捕获原应用 Limited token、ready、原应用自行退出、prepared、复制前后现代锁 Win32 32 / 旧锁 Win32 33、finish/released、两把锁重获、无测试残留。
- 新普通 ShellExecute/空 Verb 链：相同检查与握手顺序通过，Setup manifest 自提权，退出 0。

测试使用独立 AppId、目标目录、注册键和 Local/Roaming 子目录；lease 的原生签名、权限、令牌、KnownFolder、锁和进程身份验证保持生产逻辑，只替换固定 profile 子目录名和 fixture 安装识别，并加入安全布尔观察。fixture parent/helper 模拟旧助手协议，复制的是测试 receipt，不覆盖生产程序。这是有界原生流程证明，不代表未改路径的完整生产更新 E2E 或跨账户 UAC、跨架构实机通过。

原 fixture 启动器曾因未提升而无法写独立 HKLM 键，以及自造 primary token 返回 1346；这些是 fixture 准备失败，已分开保留证据。修正为高权限独立注册准备、实际普通进程启动原应用后才开展真实链验证；不把准备失败误算成生产成功或通过放宽令牌检查修复。

证据位于 `artifacts/installer-lease-after-20261002-evidence/`；原 1338 错误位于 `attempt6-accesscheck-1338/`，其余先前失败和源快照未覆盖。用户实际 App PID/创建时间前后相同。

## 退出中继连接取消竞态的追加修复

最终 Release 回归发现 `RunningInstanceExitTests.RelayIgnoresUntrustedEndpointExecutableButKeepsAuthenticationAndCompletion` 在 `TcpClient.GetStream` 抛非连接套接字异常。测试使用真实认证命令服务，服务端只有完整读入认证帧后才释放，不存在认证前主动关闭的 fixture 条件。

.NET 8.0.30 的有界最小复现确认：连接取消与完成竞争时，`await ConnectAsync` 可以正常返回，而 token 已取消且 Socket handle 已关闭，随后 GetStream 抛同款异常。取消-on-accept 第 19 次及 1 ms CancelAfter 第 3984 次复现；仅 peer 提前关闭的 6000 次没有复现。修复在窄连接超时作用域内，连接返回后明确检查取消结果并报告失败；请求已开始写入之后的错误不重复发送退出命令。仅未派发命令的旧端点准备/拒绝连接探测保留原协议。

冻结源码在 .NET 8.0.30 的 1024 次取消-on-accept 压力中全部明确返回取消失败，0 InvalidOperationException、0 重复连接；完整认证帧后 FIN/RST 两负例明确失败且不重派发。独立链接实际源码的 Release 测试18/18通过，仓库 Debug/Release 安装及连接定向各209通过/0失败/0跳过。证据为 `scratch/connection-cancel-20261002/evidence.json`、压力日志、TRX 与最小复现源；主全量覆盖独立程序集无法祖先查到的 installer 源静态断言。

连接修复已重新编译并签名到独立的 connect-final 候选目录，原候选保留；两者字节与摘要分别记录。

## 本地签名候选（连接修复前证据，保留）

`_local-scratch/build-installer-lease-0.5.24.ps1` 退出 0；实际 App/Setup 为 0.5.24，现有发行证书与时间戳保持。运行依赖从已验证 v0.5.23 包逐文件 SHA256 复核复制；此次未改 Node/Go/宿主业务文件。最终签名 App 的 `--release-self-test` 退出 0：真实 Node 22.23.2、生产依赖、增强直连 helper 鉴权/协议/退出清理、重复准备保留配置、通知 PNG/COM 提交均通过。

候选位于 `artifacts/installer-lease-0.5.24-x64/`，没有安装覆盖或公开发布：

- App SHA256：`BA4E73C8B8296FD08840E5EF179E7030676317F477885CA7FEC2E74996EE3D2F`
- Setup SHA256：`D178B9CE06B94FCFEA70B4FB215AB9FB6F639688137D7040CF88E4FEEA483847`

## 最终本地签名候选与验证

包含安装权限交接与连接取消修复的最终 x64 安装器为 `artifacts/installer-lease-0.5.24-x64-connect-final/DanmuApi-0.5.24-win-x64-setup.exe`，App 与 Setup 均为 0.5.24.0。两者公共证书指纹仍为 `D6224E17E9B87EBB53F2DCF02936451343FF4826`，均带时间戳；保留原自签根信任状态，没有变更系统证书信任。

- App SHA256：`AD20AD5D63DD3DFD9685F1012B41FB528EB6A8D5EEBFFFD52DB0037AFFD4F9B1`
- Setup SHA256：`E786892FBF830431089182EDECBB8E0DF74B4A06A10BCD9FAB9119D9F7165EE1`

最终实际签名 App 的 `--release-self-test` 退出 0，验证空目录依赖准备、真实 Node 22.23.2 及架构、helper 鉴权/协议/退出清理、重复准备保留配置、PNG/通知 COM 和隔离身份清理。生产依赖从已验证的 v0.5.23 逐文件 SHA256 复核；本次没有修改 Node/Go 业务，也没有发公网请求。

可复现命令与实际结果（构建脚本要求新的输出目录，既有证据不可覆盖）：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\build-installer-lease-0.5.24.ps1 -RunLabel connect-final
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\repair-lockgate-0.5.23.ps1 -RunLabel installer-0524-connect-candidate-final
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\_local-scratch\freeze-installer-lease-0.5.24.ps1 -Mode Verify -RunLabel connect-cancel-final
```

三项均退出 0。后构建依赖锁检查为 Debug/Release × 无 RID/win-x64/win-x86/win-arm64 共 8/8，显式使用 CI/ForceEvaluate；冻结源码与依赖输入 2219 项不变。最终候选的实际哈希、证书和时间戳又经只读复核，与记录一致。

最终安装/连接定向在 Debug、Release 各为 209 通过 / 0 失败 / 0 跳过；完整 Debug 为 2403 通过 / 2 失败 / 7 跳过，完整 Release 为 2405 通过 / 0 失败 / 7 跳过。整体回归 Gate 仍为失败，7 项跳过也不算对应验收通过。

最终汇总为 `artifacts/installer-lease-0.5.24-connect-final-summary.json`，其中明确 `AllRegressionGatePassed=false`、`ProductionE2E=false`、`Installed=false`、`Published=false`。本地分支仍为 `fix/installer-update-user-token`，没有提交、推送、tag 或新 Release；实际用户安装未覆盖，v0.5.23 已发布资产保持原样。

## 独立运行期回归限制

当前完整验证不得宣称全部通过。临时 node.exe 删除错误的后续只读取证显示：非 ReadOnly、DACL 允许 DELETE、只读 DELETE 权限打开成功、原生文件使用者数量 0；这些是事后状态，未恢复历史失败瞬间，因此根因仍未确认。

测试只增加原 Node 句柄身份与失败瞬间的只读诊断，原异常仍抛出，没有重试/预算扩大。带诊断的定向停止用例还捕获 helper 原句柄已退出、GetExtendedTcpTable 仍在断言时刻显示相同 PID 的 LISTEN 行（原 helper PID38528、端口11210）；不是以 TIME_WAIT 解释，也不能仅用后续成功消除这项证据。该失败单独保留，不属于安装更新权限交接验证成功的证明。

连接修复后的完整 Debug 为 2403 通过 / 2 失败 / 7 跳过。两项失败均保留，不以本地构建成功代替完整发行门控：

- 测速取消用例已 `await pending`，并非漏等待；生产取消方法先触发 Cancel 后写等待文案，诊断任务收尾若先写入取消终态，后续等待文案可覆盖终态。源码确认竞态窗口，实际 UI 的具体交错未实测；本次未扩大为增强直连界面修复。
- frp 用例在 TemporaryDirectory.Dispose 递归删除时报目录仍被占用。supervisor/server 的退出与泵等待已有，日志读取 Task 未等待不能直接认定为目录持有者；失败瞬间占用者仍未确认。本次未添加清理重试或放宽退出断言。

两项路径没有引用 AppInstanceLock.SendCommand，现有源码与堆栈不显示它们由本次更新修复引起。此前 Node 删除及 helper LISTEN 事件也继续保留为未关闭项。

最终 Release、新候选与八组依赖锁实际结果已记录于上文、交接页和 connect-final 汇总；完整生产覆盖安装、跨账户 UAC、x86/ARM64 原生升级仍未验收。此次权限冲突根因修复的完成不代表上述矩阵或独立运行期问题已关闭。
