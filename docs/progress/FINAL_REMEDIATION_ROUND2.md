# 第二轮最终整改 — 验收报告

> 基线 `eeb73c9`。任务书：《RTX30 FrameGen Manager 第二轮最终整改任务书》（1492 行）。**未新增 Stage，未做大范围架构重写。**

## 一、本轮修复的接线型缺陷（§1 的九项 P0）

| 项 | 缺陷（都是「看起来能用、实际走不通」） | 提交 |
|---|---|---|
| P0-01 | 用户点「继续」后**依然无法安装**：确认让流程继续了，但计划状态仍是 `NeedsConfirmation`，而执行器只认 `Ready` | `640cc49` |
| P0-02 | 界面**从未把驱动设置传下去**：装完文件，驱动没被碰 | `07c9fb9` |
| P0-03 | 读回发生在**会话已关闭之后**：真实适配器下读的是空会话，且「必然失败」与「值不一致」不可区分 | `8ea9e37` |
| P0-04 | 拿**游戏显示名**当 NVIDIA Profile 名 | `2bbe235`（接口层） |
| P0-05 | 对 `.ini` 要求 Authenticode 签名 → **provider 拒绝自己发布的 payload** | `4461cee` |
| P0-06 | 版本**全程只做透传**，`CheckLatestAsync` 一次都没调用 → 兼容性与计划可能都在拿空版本比对 | `e82536c` |
| P0-07 | 所有 provider、所有版本**共用同一个 payload 目录** → 「这是哪个版本的」无法回答 | `3ae61ef` |
| P0-08 | 计划**漏掉代理 DLL**（这次安装的产物本身）；部署结果无人核对 | `3c94405` |
| P0-09 | 批量部署**直接调 `provider.Install`**，绕过计划、payload 校验与兼容性确认 | `f111de6`（核心） |

其中 **P0-08 揭出两个连锁缺陷**：修好「计划缺代理」之后，执行器第 1 步「计划里的文件都必须在 payload 中」立刻开始拒绝**每一次正确安装**——因为代理来自 mod source 而非 payload。**一条检查从「从不触发」变成「全部触发」时，要先怀疑检查本身。**

## 二、§2–§12 的完成情况

| 节 | 内容 | 状态 | 提交 |
|---|---|---|---|
| §3 | 禁止从读权限推导写权限（读成功 → `ReadAvailable`；只有受控 Write+Save+ReadBack 成功才记 `NotRequired`） | ✅ | `71038a3` |
| §4 | 回滚可重试（`RollbackState{Pending,Restored,Failed}`，全部成功才 `MarkRolledBack`） | ✅ | `3efb46c` |
| §5 | 还原按 `deployment.ProviderId` 选 Provider，**缺失时报错不 fallback** | ✅ | `17516a1` |
| §6 | `StoreKind` 取自 `GameEntry` 真实来源，不再无条件写死 Steam | ✅ | `115448b` |
| §7 | Planner 输出 Typed `ProfileSettingRequirement`（携带真实设置对象） | ✅ | `9349f49` |
| §8 | `ProviderWritesNvidiaProfile` 与 `RequiresNvidiaProfileConfiguration` 拆分 | ✅ | `7f124b7` |
| §9 | SafeZip 原子性：**播种式故障注入**证明 commit 前失败时 destination 完全不变 | ✅ | `128f00c` |
| §11 | README 按现状重写（Provider 框架、实验性标记、边界说明、RC 状态） | ✅ | `f8db9b9` |
| §2 | NVAPI AccessViolation 专项 | ❌ **未做** | — |
| §10 | `--mfg-asset-smoke` 真实下载 | ✅ **已完成**（真实下载 312 文件 / exit 0） | `882ab9b` |
| — | **额外修掉**：`ModFetcher.AllowedHosts` 缺 `release-assets.githubusercontent.com` | ✅ 真实下载此前**不可能完成**（每一跳都做白名单检查） | `882ab9b` |
| §12 | Updates/Downloads/Diagnostics 接真实状态 | ❌ **未做** | — |
| §13 | 补测试（确认流程/Profile Wiring/Read-back/ProviderVersion/Payload 隔离/Verification/Plan 精确执行/Batch/Restore/Store/SafeZip） | ⚠️ **部分**（各修复项已随附断言，并补了 Store 默认值 / `PreviewAsync` / `PreviewOnly` 三项；**清单式逐项核对未做完**） | `82bfac5` |
| P0-09 | 预览汇总接入确认框 | ✅ **已完成**（确认前逐个预览，把「被阻止的计划」与「需要确认的计划」追加进确认正文） | `8ea5438` |

## 三、验证结果

```
Build:                              0 warnings / 0 errors（dotnet build --no-incremental -c Release）
Harness:                            854 passed / 0 failed / 10 skipped（原 801 项全保留，新增 53 项）
Network smoke:                      PASS（Release 解析 + host 白名单）
Real MFG Asset Smoke:               PASS（真实下载 mfg-smooth 2.8.2 → 312 个文件，二进制 8 / 配置 304，exit 0）
dotnet test:                        exit 0
RC Packaging:                       PASS（scripts/package-release.ps1 exit 0）
UI Process Smoke:                   PASS（从最终 ZIP 解压到全新临时目录，进程稳定 20 秒，句柄非 0，日志无 Exception/Fatal/Unhandled，只终止本次 PID，未误杀其他进程）
EXE:                                artifacts/release-candidate/win-x64/DLSSGManager.exe（66,061,093 B）
ZIP:                                artifacts/RTX30-FrameGen-Manager-win-x64-1.9.3.zip（57.7 MB）
SHA256 (EXE):                       0C4EA8B7CB0D649A8F5B7D8D8A0A909E8C909F13D73F9956B7205A1C860E67DA
SHA256 (ZIP):                       37E0CC6068BA343F765A7AF3FDB9FFBA0E60407B4C1F703688B0546537B32113
发布目录内容:                        恰好 4 个文件（EXE + LICENSE + README.md + THIRD_PARTY_NOTICES.txt）
```

> **关于 §10 这个额外发现**：`--network-smoke` 覆盖了「解析 Release + 检查 host 白名单」，看起来网络路径已经测过——但它**从未跟随重定向**。真实下载第一跳是 `github.com`，随后重定向到 CDN 主机；白名单对每一跳都校验，缺了那一个就拒绝。**编译、单元测试、结构断言都不可能发现它**，只有真的去下载才会撞上。这正是 §10 单独列一项的理由。

`--no-incremental` 是刻意的：**增量编译不会重新报告未改动文件的警告**——本轮之前有一条 `CS8601` 被增量缓存掩盖了多轮。

## 四、Pending User Validation

- **真实 NVIDIA Profile 读写**：`NvApiDrsAdapter` 仍为 fail-closed（`DriverCallsProven = false`）。本轮未改其封送方式，**§2 未完成**。
- **视觉 UI**：Provider 下拉、Plan Preview、七页导航、批量部署的三阶段流程 —— **均未人工运行验证**。`dotnet build` 只证明 `x:Name` 引用完整，`LocalizationAudit` 只证明本地化键齐全。
- **实机游戏**：`Applied` / `Verified` 需在真实游戏上完成。
- **真实 MFG Asset 端到端下载**：`--network-smoke` 只验证了 API 解析与 host 白名单，**未下载 payload**。

## 五、Known Risks

1. **§2 未做 → P0-04 只有接口层**：`FindApplication` 的实现刻意停在「拒绝并说明」，因为结构体封送正是当前崩溃的根因；在它被证明安全之前接入第二个调用者等于新增第二个崩溃点。**顺序不可颠倒。**
2. **P0-09 的确认框仍不描述计划**：用户看到的是防作弊风险，而不是各游戏的实际计划与被阻止的原因。
3. **§13 是随附式而非清单式**：每个修复都带了断言，但没有按任务书逐项补齐清单。
4. **UI 路径的测试覆盖有限**：批量部署、Provider 下拉、Plan Preview 的改动**不在 Harness 编译白名单内**，Build 0/0 与 Harness 全绿只说明没有破坏既有行为，不等于新路径被执行过。

## 六、Final Integration Remediation: **PARTIAL**

**达成**：§1 的九项 P0 中八项已修复（P0-04 为接口层），§3–§9、§11 完成，全量回归与 RC 打包通过，UI 进程级验证通过。

**未达成**：**§2（NVAPI AccessViolation 专项）** 未做，因此 `NvApiDrsAdapter` 仍不可用，P0-04 的真实调用无法接入；§10、§12 未做；§13 为部分。

**不宣布 PASS 的理由**：真实驱动读写仍不可用，而它是本项目存在的理由之一。把它记为 PARTIAL 并写清剩余项与它们之间的依赖顺序，比宣布完成更接近事实。
