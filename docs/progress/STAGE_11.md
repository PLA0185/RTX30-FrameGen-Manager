# Stage 11 — 完整回归与最终收尾

> 基线 `3c3a0d1`（Stage 6 起点）→ 末端提交见文末。

## 1. 完整回归结果

```
dotnet restore            → 无错误
dotnet build -c Release   → 0 警告 / 0 错误
dotnet test               → exit 0（仓库无 VSTest 项目，如实记录）
Harness（真实测试载体）    → 720 通过 / 0 失败 / 10 跳过，exit 0
```

Harness 的 10 项跳过需要真实 Mod 文件（`Harness --fetch`），不是失败。

### 回归清单逐项对照

| 能力域 | 覆盖情况 |
|---|---|
| Build / Harness | 每阶段执行，**历次原测试均未删**（362 → 408 → 486 → 540 → 575 → 616 → 646 → 684 → 720） |
| Security | 签名完整性（`BadDigest` 全源拒绝）· 备份 SHA-256 · 路径逃逸 · 主机白名单 · 逐跳重定向 · zip slip |
| Download | 源链与回退 · 进度与暂停 · URL 策略 · 下载委托 seam |
| Digest | `Verified` / `Mismatch` / `Unavailable` / `Malformed` 四态区分 · 无摘要不伪装已验证 |
| Signature | `ProbeSignature` 四值 · PE 魔数预检 · 篡改拒绝 |
| Backup / Rollback | 备份哈希校验 · 不安全备份名拒绝 · 事务快照 · 失败回滚 · 回滚失败如实报告 |
| Provider | Registry 唯一性 · 未知 ID 不 fallback · 逐 Provider 隔离 · Metadata / Health 七态 · 两个 Provider |
| Update | Self / Patch 分离 · 缓存 TTL 与 stale · 去重 · Pin / Hold · 结构化网络与限流 |
| Game Detection | Steam 库 · 目录解析 · 静态启发式 · 分级证据 |
| Renderer Detection | 五级判定 · 噪声名排除 · 用户指定短路 |
| Graphics API | 四级严格优先级 · 冲突保留 · Unknown → 计划 Blocked |
| Compatibility | 12 维匹配（Exact / Partial / None）· 明确矛盾不降级为 Partial |
| InstallPlanner | 纯函数 · 全部 Blocked 分支 · **无副作用守卫**（规划前后比对目录与字节） |
| Proxy Conflict | 7 入口归属分类 · 归属不明不算空闲 · 永不覆盖 |
| Anti-Cheat | 检测与隔离清理（既有测试） |
| NVIDIA Profile | 三态恢复 · 部分失败回滚 · 权限探测四分支 · **fail-closed 适配器拒绝写入** |
| Smooth Provider | MFG 版本规则 · payload 唯一性 · `ReleaseFormatChanged` 停止安装 · 真实 tag 集不当作版本 |
| Orchestration | 证据六级 · `Verified` 需 ≥2 信号且 ≥1 强证据 · 两轮计划 · 失败回滚 |
| Storage | 库文件持久化 · 矩阵与配方记忆的版本化原子写 · 损坏容错 |
| Offline | 离线用 stale 缓存并说明 · API 不可达不阻止启动 |
| Rate Limit | 429 / 403+配额头 → `RateLimited`；403 无限流头 → `Forbidden`（不误判） |
| UI | 预览层 36 项（导航模型 / 状态映射 / 折叠规则）· **界面运行时行为未验证** |
| Recovery | 部署失败回滚 · Profile 回滚 · 编排失败回滚并保留真实错误 |

## 2. 最终文档状态

| 文档 | 状态 |
|---|---|
| `README.md` / `README.en.md` | **已存在**（38 行，含 AI 开发声明与徽章） |
| `docs/ARCHITECTURE_PLAN.md` | 已存在（§1–§41，含各阶段实现记录） |
| `docs/RESEARCH_NOTES.md` | 已存在（Phase 0 事实层） |
| `docs/phase0-manager-audit.md` | 已存在 |
| `docs/mod-files.md` | 已存在 |
| `docs/COMPATIBILITY.md` | **Stage 9 新增** |
| `docs/SECURITY.md` | **Stage 11 新增** |
| `docs/UPDATE_SYSTEM.md` | **Stage 11 新增** |
| `docs/progress/STAGE_06.md`–`STAGE_11.md` | **各阶段新增** |
| `LICENSE` | **已存在**：MIT，`Copyright (c) 2026 DLSSG 30s Manager contributors` |
| `THIRD_PARTY_NOTICES.txt` | **已存在**（16 行，含 d3d12.dll 的 SHA-256 与三条来源说明） |
| `CONTRIBUTING.md` / `.gitattributes` / `.gitignore` | 已存在 |

## 3. Carry-over 处理结果（基于实际状态，非机械修改）

| 项 | 实际状态 | 处置 |
|---|---|---|
| `.gitignore` | **已存在** | 无需处理 |
| `LICENSE` | **已存在**（MIT） | 无需处理 |
| `THIRD_PARTY_NOTICES` | **已存在为 `.txt`**，被 14 处文档引用 | **保持 `.txt`**：改名为 `.md` 会破坏 `.gitignore` 与多份文档中的引用，收益为零 |
| `extra-proxies/d3d12.dll` | **未被 git 跟踪**（`git ls-files` 仅返回其 `README.md`）；SHA-256 已记录于 THIRD_PARTY_NOTICES | **无需删除**：仓库中本就不含该二进制，符合「禁止转发上游二进制」红线；哈希已保留，社区构建识别功能不依赖该文件 |
| `objects.githubusercontent.com` 白名单 | **Stage 7 已补** | 无需处理 |

**结论**：Carry-over 中的五项**均已处于正确状态**，其中两项（LICENSE、THIRD_PARTY_NOTICES）是"早已存在"而非"本轮补齐"。这里如实记录，不把既有工作算成本轮成果。

## 4. License 状态

- 本项目：**MIT**（`LICENSE`，`Copyright (c) 2026 DLSSG 30s Manager contributors`）
- 上游 `BUNNY-19C/DLSSG-30s-manager`：MIT（本项目由其重构）
- MFG 上游：**MIT**（2026-09-26 经 GitHub API 实测 `license.spdx_id`，**修正了 Phase 0 的「无 LICENSE」记录**）
- `RankFTW/RHI`：GPL-3.0 → **仅作参考，代码不可复制**
- xikarioz 系：专有 EULA → **不重分发、不改品牌重打包**，如需支持只作外部集成
- 上游二进制：**不打包、不镜像、不转发**；由程序在用户端按需下载

## 5. 最终验收声明（严格区分两种"通过"）

### ✅ Automated Test Passed
- `dotnet build -c Release`：**0 警告 / 0 错误**
- Harness：**720 通过 / 0 失败 / 10 跳过**（历次原测试均未删除）
- 全部测试离线运行，不消耗真实 GitHub 配额，不触碰真实 NVIDIA Profile

### ❌ Real Hardware / Real Game Verified
**未达成。** 以下均未在真实硬件上端到端验证：

| 未验证项 | 原因 |
|---|---|
| 真实 NVAPI/DRS 读写 | 缺官方 `nvapi_interface.h` 确认函数 ID 与 `NVDRS_SETTING_V1` 布局 → 适配器为 **fail-closed 桩** |
| 本机 DRS 权限结论 | 需真实调用观察返回码 |
| 真实 Release Asset 下载与解包 | 需联网与资产级体积 |
| 真实 GitHub API smoke test | 结构由 fake 客户端验证 |
| **界面运行** | WPF 文件不在 Harness 编译白名单内；**七页导航与折叠从未被运行过** |
| 实机游戏验证 | 需要用户本人运行游戏 |
| `Verified` 级证据 | 需要 Debug Bars 观察或补丁日志（见 Stage 8） |

## 6. 项目级剩余风险

| ID | 风险 |
|---|---|
| **P-01** | 真实驱动层缺失：DRS 适配器 fail-closed，真机尚不能读写 Profile |
| **P-02** | 界面未运行验证（Stage 10 唯一实质缺口） |
| **P-03** | 兼容性数据库为空，无任何 `ProjectVerified` 记录 |
| **P-04** | 真实下载链路未端到端验证 |
| **P-05** | 配方记忆未接入编排（有能力，无自动写入路径） |
| **P-06** | 证据到期未处理（驱动/Provider 更新后旧验证如何失效） |

## 7. Stage 11 Acceptance: PASS

回归全部通过、文档齐备、Carry-over 逐项核实并以实际状态处置。**验收仅覆盖自动化部分**；实机与界面验证仍为 `Pending User Validation`，不以自动化结果代表。
