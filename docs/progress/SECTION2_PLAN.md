# §2 实施方案：绕开 marshaler 的 NVDRS_SETTING_V1 往返

> 状态：**未实施**。本文件是可直接执行的方案，不是完成记录。
> 目标文件：`src/DLSSGManager/NvidiaProfile/NvApiDrsAdapter.cs`

## 一、要解决的问题

`--nvapi-smoke` 的现状（已实测复现）：

```
CanRead           : False
CanWrite          : False
不可用原因        : NVAPI 已加载且结构体布局断言通过，但真实驱动上的只读 Smoke 在第二次读取时
                    触发 AccessViolationException：NVDRS_SETTING_V1 的往返封送尚未被证明正确，
                    因此拒绝调用任何驱动接口。
```

**根因已收窄，不是猜测**：

- `VerifyLayout()`（用 `Marshal.SizeOf` + 逐字段 `Marshal.OffsetOf`）**全部通过** —— 偏移与总大小 12320 都与官方头文件推导一致。
- 但 `Marshal.SizeOf` / `Marshal.OffsetOf` **只计算托管侧对布局的看法，从不执行 marshaler 的调用路径**。copy-in/copy-out 发生在 P/Invoke 调用期间，布局检查完全不经过那里。
- **已排除**：布局错、加载错（入口点全部解析成功）、调用约定错（`NvAPI_Initialize` 与 `CreateSession` 正常）。
- **剩余怀疑对象**：三个大数组字段上的 `[MarshalAs(UnmanagedType.ByValArray, SizeConst = …)]`（**L474 / L486 / L492**）。
- **失败形状**：「第一次能过、第二次崩」指向**临时缓冲**的分配与释放，而不是错误偏移 —— 偏移错会在第一次就失败。

## 二、改法：不再让 marshaler 碰这个结构体

把结构体的构造与读取全部改为**手工内存操作**，委托签名从 `ref NvDrsSetting` 改为 `IntPtr`。

### 2.1 字段偏移（官方头文件推导，`pack(4)`）

| 字段 | 偏移 | 写入方式 |
|---|---|---|
| `version` | 0 | `WriteInt32` |
| `settingName`（`NvU16[2048]`） | 4 | `Marshal.Copy`，UTF-16，4096 字节 |
| `settingId` | 4100 | `WriteInt32` |
| `settingType` | 4104 | `WriteInt32` |
| `settingLocation` | 4108 | `WriteInt32` |
| `isCurrentPredefined` | 4112 | `WriteInt32` |
| `isPredefinedValid` | 4116 | `WriteInt32` |
| `predefinedValue`（union，`NVDRS_BINARY_SETTING`） | 4120 | 长度 `WriteInt32` + 数据 `Copy` |
| `currentValue`（union，同上） | 8220 | 长度 `WriteInt32` + 数据 `Copy` |
| **总大小** | **12320** | `AllocHGlobal(12320)` |

> DWORD 设置的值就是 union 的第一个 DWORD —— 现有代码正是这样读的（`setting.CurrentValueLength` 被当作值使用），改用偏移后**语义不变**。

### 2.2 改动清单

> **这是 5 处联动改动，必须一次做完。** 任何一处未完成都会让工作区不可编译，因此不要拆成多个提交或分多次编辑。精确落点（`NvApiDrsAdapter.cs`，行号以 `dff61be` 为准）：
>
> | # | 落点 | 改动 |
> |---|---|---|
> | 1 | **L469–L505** | 删除 `NvDrsSetting` 结构体与全部 `[MarshalAs]` 特性 |
> | 2 | **L507–L520** | `NewSetting` 由 `new NvDrsSetting { … }` 改为分配 + 清零 + 写 `version`，**返回 `IntPtr`** |
> | 3 | **L236–L273**（`Read`） | 改为手工内存：分配 → 写 `version`/`settingId` → 调用 → 按偏移读回 → `finally` 释放 |
> | 4 | **L275–L302**（`Write`） | 同上；先取回现有结构（保留 type / location / name），只改值（偏移 8220）与 `isCurrentPredefined`（4112） |
> | 5 | **L559 / L562**（两个委托）+ **L422–L449**（`VerifyLayout`） | 委托第四/第三参数 `ref NvDrsSetting` → `IntPtr`；`VerifyLayout` 改为断言常量自洽性 |
>
> 现有 `NewSetting` 写入的字段（供改造时对照，语义都要保留）：`Version = SettingVersion` · `SettingName = new ushort[UnicodeStringLength]` · `SettingId = settingId` · `SettingType = TypeDword` · `SettingLocation = LocationCurrentProfile` · `IsCurrentPredefined = 0` · `IsPredefinedValid = 0` · `PredefinedValueLength = 0` · `CurrentValueLength = value`。

1. **委托签名**：`GetSetting` 与 `SetSetting` 的第四个/第三个参数由 `ref NvDrsSetting` 改为 `IntPtr`。**两处都要改**（`GetSetting` 传 `ref setting`，`SetSetting` 传 `ref setting`）。
2. **删除 `NvDrsSetting` 结构体**（L469–L500 附近）与其 `[MarshalAs]` 特性。
3. **`NewSetting`** 改为返回 `IntPtr`：`AllocHGlobal(SettingSize)` + 清零（`Marshal.Copy(new byte[SettingSize], 0, ptr, SettingSize)`）+ 写 `version = SettingVersion`，其余字段按需写。
4. **`Read`**：分配 → 写 `version` 与 `settingId` → 调用 → 成功时按偏移读取 `isCurrentPredefined`（4112）与 `currentValue` 长度（8220）→ **`finally` 中 `FreeHGlobal`**。
5. **`Write`**：先 `Read` 取回现有结构（或直接手工读一次），改 `currentValue` 长度（8220）与 `isCurrentPredefined`（4112），再调用 `setSetting` → **`finally` 中 `FreeHGlobal`**。
6. **`VerifyLayout`**：不能再对已删除的结构体做 `SizeOf`/`OffsetOf`。**改为断言常量的自洽性**（偏移互不重叠、末字段 + union 大小 == 12320），并在注释中保留「本检查不覆盖封送安全性」这条边界。

### 2.3 内存纪律

- **每一个 `AllocHGlobal` 都必须在 `finally` 中 `FreeHGlobal`。** 这是本改动最主要的新风险：原先由 marshaler 管理的内存现在由代码管理。
- 建议：`Read` / `Write` 各自用 `try/finally`，**不要**在 `EnsureLoaded` 或字段里长期持有指针。

## 三、必须同时做的前置改造（易漏，且不可分）

**当前 `--nvapi-smoke` 的真实读取在 fail-closed 下已被跳过** —— 它的输出明确写着「受 fail-closed 保护，未调用驱动」。

**因此：只改封送、不改 smoke，就无从验证。** smoke 必须改为：

1. **绕过 `CanRead` 保护**，直接对 `_getSetting` 发起真实调用（仅供 smoke 使用的内部诊断入口，**不改变 `DriverCallsProven` 语义**）。
2. 跑 **≥100 次** `Open + GetSetting` 的 **A/B/A/B** 循环（A、B 为两个不同的设置 ID，交替读取以覆盖不止一个 union 长度）。
3. **计数 `AccessViolationException`**，报告 0 次才允许把 `DriverCallsProven` 改为 `true`。
4. 循环只读，**不写入任何设置**。

## 四、验证步骤（顺序不可换）

1. `dotnet build -c Release --no-incremental` → 0 警告 / 0 错误。
2. `Harness.exe --nvapi-smoke` → **观察 `CanRead` 是否变为 `True`**（这是封送是否修好的第一信号）。
3. 若 `CanRead = True` → 改造 smoke 跑 **≥100 次 A/B/A/B 循环**，要求 **0 AccessViolation**。
4. 通过后：`DriverCallsProven = true`，并把 `--nvapi-smoke` 恢复为正常路径（保留循环作为可选开关）。
5. Harness 全量回归（**原 801 项不得删除**）→ 0 失败。
6. 收尾模板：打包 → 对最终 ZIP 跑 UI Process Smoke → 刷新报告。

## 五、红线与回滚

**红线（未变）**
- **≥100 次只读循环 0 AccessViolation** 才可把 `DriverCallsProven` 改为 `true`。
- 可用 `GetBaseProfile` 做**对照实验**，但**不得预设它是根因**。
- **Read 通过前禁止 Write。**
- 不自动修改用户真实的 NVIDIA Profile；不自动修改真实 Ground Branch。

**回滚**
- 本改动集中在 `NvApiDrsAdapter.cs` 一个文件内。若封送改造失败：`git checkout <上一个提交> -- src/DLSSGManager/NvidiaProfile/NvApiDrsAdapter.cs`，`DriverCallsProven` 保持 `false`，行为回到当前的 fail-closed。
- **失败即回滚，不要保留半成品** —— 一个未证实的封送实现留在代码里，比保持 fail-closed 更危险。

## 六、为什么当时没直接做

该改动需要约 250 行代码加多轮真机验证（含一次性改造 smoke），而当时的会话预算不足以安全完成。**改完若无法验证，等于把一个未证实的实现留在代码里** —— 这比不改更糟。方案、字段偏移与验证顺序已完整记录在此，可直接执行。
