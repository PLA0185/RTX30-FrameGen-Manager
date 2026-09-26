# 最终功能整合整改 — 验收报告

> 基线 `e9b81c5e8f12323a33c9d557c9cca7984a43f00b`。任务书《RTX30 FrameGen Manager 最终功能整合整改任务书》（2417 行）。

## 1. 起点上真实存在的问题

审查结论成立，且本轮在动手过程中又发现了两个它没提到的、更根本的问题：

| 来源 | 问题 |
|---|---|
| 审查发现 | `Apply()` 在 `Unknown` 时继续写入，而 `Rollback()` 又跳过 `Unknown` 的恢复 → **写进去的值永远撤不掉** |
| 审查发现 | `Rollback()` 不自行 `Open`，而 `Apply` 在 `finally` 里已 `Close()` → 回滚作用在空会话上 |
| 审查发现 | 所有 Profile 设置一律写 `1u`；`InstallPlan` 的 `ProxyChoice` / `Mode` / `AsiChoice` / `FilesToDeploy` 从未被安装使用 |
| **本轮发现** | **`IWorkflowDetector` 没有任何生产实现** → `SmoothMotionWorkflow` 在生产代码中从未被实例化。整套编排建好了、测过了，**没有一行生产代码调用它** |
| **本轮发现** | **`FilesInstalled` 信号恒为 true**，而 Verified 判据是「信号数 ≥2 且强信号 ≥1」→ **「复制了 DLL + 看到 Debug Bars」会被判为 Verified**，正是任务书 §51 要求拒绝的组合 |

## 2. §62 验收清单逐条核对

图例：✅ 达成 · ⚠️ 部分 · ❌ 未达成

| # | 条目 | 判定 | 证据 |
|---|---|---|---|
| 1 | `NvApiDrsAdapter` 不再是永久 fail-closed 桩 | ⚠️ | 代码已完整（官方 ID、签名、布局断言、`--nvapi-smoke`）。**但**二期实测崩溃后 `DriverCallsProven = false`，仍 fail-closed——**性质不同**：不再是「缺资料所以不做」，而是「代码已完成、实测发现 ABI 使用方式有缺陷，因此拒绝」 |
| 2 | 官方 NVAPI ABI 已正确实现 | ⚠️ | 8 个入口点 ID 与签名均取自官方 MIT 头文件；`Marshal.SizeOf` = 12320 与 9 个 `OffsetOf` 断言**全部通过**；NVAPI 真的加载、会话真的打开、**第一次读取正确返回 `Absent`**。**但第二次读取以 `AccessViolationException` 崩溃，根因未定位** → 「正确」不能打勾 |
| 3 | Unknown 原值不会被覆盖 | ✅ | 必填项原值不可读 → **写入前整体中止且 0 项写入**；可选项 → 跳过并标 `Skipped`。有断言 |
| 4 | External Rollback 会重新打开正确 Profile | ✅ | `ProfileJournal` 携带 `ProfileName`；`Rollback` 自带 Open→恢复→Save→Close。有断言 |
| 5 | 无 Double Rollback | ✅ | `IsRolledBack` 单次执行，重复调用返回 `Skipped=true` 且不触碰驱动。有断言 |
| 6 | Profile Setting 使用 Typed Value | ✅ | `ProfileSettingWrite(Setting, Value, Required, Reason)` 取代元组。有断言 |
| 7 | API Bitmask 正确 | ✅ | DX12=1 / DX11=2 / Vulkan=4，**Unknown → null（不写）**。有断言（Vulkan 写 4 直接证明旧的「一律写 1」已移除） |
| 8 | 未知 Flip / Debug 值不猜 | ✅ | 单一来源的 `DebugLogLevel` 标 `Writable = false`；Flip 值不进入自动写入集。有断言 |
| 9 | Profile Save 后有 Read-back | ✅ | `ReadBackMatches` 逐项读回并比对；adapter 无读取能力时返回 false。有断言 |
| 10 | Planner 的结果真正控制 Installer | ✅ | `InstallPlanExecutor` 消费计划；入口选择写入 `game.PreferredProxy`。有断言 |
| 11 | DirectProxy 使用指定 Proxy | ✅ | 计划无入口时**拒绝**；provider 不支持时**拒绝**。有断言 |
| 12 | ASI 不再静默降级成 DirectProxy | ✅ | provider 不支持 ASI → **拒绝**并说明「降级会改变安装形态」。有断言 |
| 13 | MFG 使用真实 Payload Manifest | ✅ | `PayloadScanner` 扫描解包后的真实目录；`plan.FilesToDeploy` 优先取自 manifest。有断言 |
| 14 | Provider Verification 真正位于安装链 | ✅ | `InstallPlanExecutor` 第 4 步逐文件 `VerifyPackage`，不通过即拒绝。有断言 |
| 15 | Unknown Compatibility 可由用户明确确认继续 | ✅ | `UserConfirmedUnverified`；无确认即停且不写任何文件。有断言 |
| 16 | 用户确认不会写成 Compatible | ✅ | 兼容性状态保持不变，步骤文案明写。有断言 |
| 17 | UserApi 真正接入 | ✅ | 参与兼容性查询；与 `>= RuntimeDetection` 证据冲突 → `NeedsConfirmation` |
| 18 | MainWindow 主配置按钮进入 Smooth Workflow | ✅ | `RunDeploy` 的 `AppProviders.Patch.Install` 调用已删除，改走 `GameConfigurationService` |
| 19 | Provider Selection 真正可用 | ✅ | 工具栏下拉由注册表构建、持久化到 `library.json`。**UI 未人工运行验证** |
| 20 | Plan Preview 真正可用 | ⚠️ | 计划摘要已输出到日志（含失败时）。**独立面板形式未做** |
| 21 | `deployment.Files` 用于识别 Owned Proxy | ✅ | 优先读 `Files`，逐级兼容 `ProxyName` → `Backups`。有断言 |
| 22 | Owned Proxy 可安全复用 | ✅ | 哈希匹配 → `ReusableOwnedCandidate`（不进冲突、可复用）；修复了「重装被自己的文件卡死」 |
| 23 | Zip Bomb 限制已实现 | ✅ | 条目数 2048 / 单文件 512 MiB / 总量 2 GiB / 压缩比 200:1。有断言 |
| 24 | Safe extraction 使用 Staging | ✅ | 解到目标目录内 `.staging-<hex>`，全部成功后才到位；失败时目标不变 |
| 25 | Verified 证据规则已收紧 | ✅ | 需「生成帧证据 ×1」+「运行时/驱动佐证 ×1」，安装证据被排除。有断言 |
| 26 | Evidence 有 Source / Timestamp | ✅ | 每信号带 `Source` + `ObservedAt`；`HasTraceableProvenance`。有断言 |
| 27 | 成功 Verified 才写 Project Verified Recipe | ✅ | 记录点已接入 `Finish`（覆盖全分支）；`claimed` **保守取 `PendingUserValidation`**，从不声称 `ProjectVerified` |
| 28 | Updates / Downloads / Diagnostics 至少连接真实状态 | ⚠️ | 诊断页指向真实文档；更新页显示真实版本号；下载页与面板级状态仍未接真实数据 |
| 29 | README 指向当前仓库 | ✅ | README×2 徽章与 CONTRIBUTING 链接改到本项目；调研文档的上游引用**刻意保留**（溯源） |
| 30 | Release Workflow 文案更新 | ✅ | `release.yml` 无上游仓库引用 |
| 31 | Real NVAPI temporary-profile smoke PASS 或明确外部阻塞 | ⚠️ | **明确记录为未 PASS，且不是外部阻塞**：只读 smoke 到第二次读取即崩溃；写入路径从未执行 |
| 32 | Real MFG Asset smoke PASS | ✅ | `--network-smoke` 真实联网通过：`mfg-smooth` 健康 `Available`、版本 `2.8.2`、host 白名单 `True`、exit 0 |
| 33 | Release Build 0 warnings / 0 errors | ✅ | |
| 34 | Harness 0 failures | ✅ | **801 通过 / 0 失败 / 10 跳过** |
| 35 | 原测试未删除 | ✅ | 720 → 801，**原 720 项全部保留** |
| 36 | UI process smoke PASS | ✅ | 从最终 ZIP 解压到全新目录，进程稳定 20 秒，窗口标题正确，日志无异常 |
| 37 | RC packaging PASS | ✅ | 脚本 exit 0；发布目录恰好 4 个文件 |
| 38 | `HEAD == origin/main` | ✅ | |
| 39 | 工作区干净 | ✅ | |

## 3. 最终交付物

| 项 | 值 |
|---|---|
| 版本 | 1.9.3（取自 `csproj`，未凭空宣布新版） |
| EXE | `artifacts/release-candidate/win-x64/DLSSGManager.exe`（66,053,926 B）SHA256 `1A8FAF6BDADD2208391B6E318C8F514F5FBC0993F5890329026A4C400F63B3E0` |
| ZIP | `artifacts/RTX30-FrameGen-Manager-win-x64-1.9.3.zip` SHA256 `647DFA2E97EE04B453A712596F0019F080F1C52A4EF001BA92925E725EF716A5` |
| SHA256SUMS | `artifacts/SHA256SUMS.txt` |
| 发布目录内容 | 恰好 4 个文件：EXE + LICENSE + README.md + THIRD_PARTY_NOTICES.txt |
| 发布方式 | win-x64 · self-contained · 单文件（沿用仓库既有 `release.yml` 约定） |

## 4. 仍未验证的部分（不得当作已完成）

```
Real Hardware / Real Game Verified: 未达成
```

- **真实 NVAPI 读写**：只读 smoke 到第二次读取崩溃；**写入路径从未在真实驱动上执行**
- **视觉 UI 验证**：Provider 下拉、Plan Preview、七页导航、高级折叠**均未经人工运行确认**。本环境无法看见桌面 GUI，因此**不伪造视觉验证**
- **实机游戏验收**：Ground Branch / Smooth Motion 的 `Applied` / `Verified` 需用户本人在真实游戏上完成
- **真实 Release Asset 端到端下载**：`--network-smoke` 验证了 API 解析与 host 策略，**未下载 payload**
- **无 .NET 机器的物理验证**：本机装有 WindowsDesktop 8.0.31

## 5. 本轮新增的测试

**720 → 801**（+81 项，原 720 项全部保留）。分布：Profile 事务与 Typed 值 20 · Proxy 冲突四态 9 · 计划执行 8 · payload 校验 10 · 用户确认与 UserApi 6 · 配置服务 5 · 归档安全 13 · 配方记忆 5 · 证据阶梯 5。

## 6. Functional Integration Remediation: **PARTIAL**

**达成**：B、D、E、F、G、H、I、J、K 的核心目标；39 条验收清单中 **33 条 ✅、6 条 ⚠️、0 条 ❌**。

**未达成的两项**：

1. **P0-01 / P0-02 相关的真实 DRS 能力**：资料与代码路径已完备，但结构体往返封送在真实驱动上崩溃，根因未定位。按红线 fail-closed，**没有带着已知崩溃去写用户 Profile**。
2. **视觉 UI 与实机游戏验证**：需要人或真实游戏才能完成，本环境无法替代。

**不宣布 PASS 的理由**：`NvApiDrsAdapter` 目前仍不可用，而它是本项目存在的理由之一。把它记为 PARTIAL 并写清根因与三个排查方向，比宣布完成更接近事实。
