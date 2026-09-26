# Stage 6 — NVIDIA Profile Service

> 基线 `3c3a0d1` → 本阶段提交见文末。只记录实际做了什么、与设计的偏差、以及仍未验证的部分。

## Implemented

- `src/DLSSGManager/NvidiaProfile/NvidiaProfileService.cs`
  - `ProfileSettingState { Unknown, Absent, ExplicitValue, InheritedDefault }`
  - `ProfileSettingSnapshot` / `ProfileJournalEntry` / `DrsStatus`
  - `IDrsAdapter`：驱动边界（`Open` / `Read` / `Write` / `Delete` / `Save` / `Close`）
  - `NvidiaProfileService`：`Apply`（快照 → 写入 → 保存，部分失败自动回滚）、`Rollback`（按 journal 逆序恢复）、`ProbeElevation`（运行时探测）
  - `SmoothMotionSettings`：6 个 Setting ID + 出处标注
- `src/DLSSGManager/NvidiaProfile/NvApiDrsAdapter.cs`
  - `NvApiDrsAdapter`：真实驱动适配器，**fail-closed**（见下）
  - `AbsentDrsAdapter`：无驱动接口时的显式实现（与"有驱动但未实现"区分）

## 三态恢复的正确性

| 原值状态 | 恢复动作 | 理由 |
|---|---|---|
| `Absent` | **`Delete`** | 用 `SetSetting(id, 0)` 会把「未设置」永久污染成「显式为 0」，且不可逆 |
| `ExplicitValue` | 写回原值 | 原值是用户/其他工具的显式设置，必须原样还原 |
| `InheritedDefault` | **`Delete`** | 继承值不是本工具写的；写回会把继承项变成显式项，语义被改变 |
| `Unknown` | **不动作** | 猜一个值去覆盖没读到的东西，是唯一比不做更糟的选择 |

**关键实现细节**：`Apply` 在**任何写入之前**先读取全部原值。若边写边读，第二次读到的会是本次写入的值，回滚就成了"把我们的值当成用户原值写回去"的空操作——表面成功、实际丢失。

## 权限探测

不写死「需要管理员」。官方 DRS 文档未声明该要求，社区经验仅作为可能性记录：
- 非提权调用**成功** → `NotRequired`（有观察证据）
- 非提权调用**失败** → `Required`，并在证据文本中注明「官方未声明，记为社区经验」
- **已提权仍失败** → `Unknown`（原因不是权限，不得归因）
- 适配器不可用 → `Unknown`（不是 `Required`）

## 真实适配器为何 fail-closed

`_research/` 中**没有** `nvapi_interface.h`。当前只有 5 个已核对的函数 ID（`NvAPI_Initialize 0x0150E828`、`NvAPI_DRS_CreateSession 0x0694D52E`、`NvAPI_DRS_LoadSettings 0x375DBD6B`、`NvAPI_DRS_FindProfileByName 0x7E4A9A0B`、`NvAPI_DRS_SetSetting 0x577DD202`），而 `NvAPI_DRS_DeleteProfileSetting` / `GetSetting` / `SaveSettings` 的 ID 与 `NVDRS_SETTING_V1` 的结构体布局**均未确认**。

凭记忆写 P/Invoke 会产生结构体封送错误，其后果发生在用户机器上的驱动层，而且正好发生在"本该能撤销自己"的代码路径上。

因此 `NvApiDrsAdapter`：
- `IsAvailable => false`（未加载库、未发起任何调用）
- `CanDelete => false`；**`CanWrite` 要求 `CanDelete`** —— 不能删除就不写，避免留下无法恢复的 Profile 修改
- 所有操作返回带原因的失败，而非静默无操作

**本阶段未对真实 NVIDIA Profile 发起任何写操作。**

## Tests

新增 35 项（Harness 540 → 575，原测试未删）：

| 组 | 覆盖 |
|---|---|
| 三态恢复 | ABSENT→写→删→ABSENT · EXPLICIT 0 写回 0（未误判为删除）· EXPLICIT 非零完整恢复 · INHERITED 用删除恢复 |
| 部分失败 | 第二个设置写入失败 → 先前写入被回滚 · 失败原因与回滚记录保留 |
| 保存失败 | 返回失败且全部写入被回滚 |
| 会话失败 | 初始化失败不写入 · Profile 不存在 · 应用绑定不存在 · 错误码被记录 |
| 权限探测 | 非提权失败→Required · 非提权成功→NotRequired · 已提权失败→Unknown · 无适配器→Unknown |
| 回滚 | 成功路径 · 删除失败时如实报告且保留现场 |
| 未知原值 | 给出警告 · 回滚时**不猜测、不覆盖** |
| 真实适配器 | 不可用 · 缺删除能力时拒绝写入 · 未发起调用 |
| Setting 出处 | 6 项齐备 · 标注含 Undocumented/Community Verified · **不含「NVIDIA 官方」** · ID 与调研一致 |

全部使用 `FakeDrsAdapter`，**不依赖真实 NVIDIA Profile**。

## Baseline vs Current Build

```
Build:    0 warnings / 0 errors  →  0 warnings / 0 errors
Harness:  540 passed / 0 failed / 10 skipped  →  575 passed / 0 failed / 10 skipped
dotnet test: exit 0（仓库无 VSTest 项目，测试载体是 test/Harness）
暂不适用：真实 NVAPI（见 Pending User Validation）
```

## Known Issues

- 真实 NVAPI 适配器**未实现**（缺官方头文件确认函数 ID 与结构体布局）
- 因此真机上尚不能读/写 NVIDIA Profile
- Harness 中 10 项跳过测试仍需要 Mod 文件（`Harness --fetch`）

## Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S6-01** | 真实 DRS 适配器缺失 | 服务层完整，驱动层为 fail-closed 桩；需 `nvapi_interface.h` 确认 `DeleteProfileSetting`/`GetSetting`/`SaveSettings` 的 ID 与 `NVDRS_SETTING_V1` 布局 |
| **S6-02** | 真机权限结论未取得 | `ProbeElevation` 逻辑已测，但本机实际是否需要提权**未验证** |
| **S6-03** | `EnsureProfile` / 应用绑定创建未实现 | Profile 或绑定不存在时当前仅报告失败，不自动创建（创建 Profile 属更大改动，留待需要时） |
| **S6-04** | Operation Journal 未持久化 | journal 在内存中返回给调用方；跨进程崩溃恢复需落盘（Stage 8 编排会需要） |

## Carry-over

`.gitignore` / `LICENSE` / `THIRD_PARTY_NOTICES.md`；`extra-proxies/d3d12.dll` 应删只留 SHA-256 常量；下载白名单补 `objects.githubusercontent.com`；**新增**：获取官方 `nvapi_interface.h` 以完成真实 DRS 适配器。

## Files Changed

```
src/DLSSGManager/NvidiaProfile/NvidiaProfileService.cs   (新增)
src/DLSSGManager/NvidiaProfile/NvApiDrsAdapter.cs        (新增)
test/Harness/Program.cs                                  (+210)
test/Harness/Harness.csproj                              (+2)
docs/progress/STAGE_06.md                                (新增)
```

## Pending User Validation

- 真实 NVIDIA Profile 的读写在真机上的行为（需要 `nvapi_interface.h` 完成后才能进行）
- 本机写入 DRS 是否真的需要管理员权限（需真实调用观察返回码）

## Stage 6 Acceptance: PASS

Automated tests passed. **Real hardware / real driver verified: NO** — 驱动层为 fail-closed 桩，未对真实 Profile 发起任何调用。
