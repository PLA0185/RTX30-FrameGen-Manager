using System.Runtime.InteropServices;

namespace DLSSGManager.NvidiaProfile;

/// <summary>
/// The real driver boundary, driving NVIDIA DRS through NVAPI.
///
/// <para><b>Every constant here comes from NVIDIA's official header files</b>, taken from the public
/// <c>NVIDIA/nvapi</c> repository (MIT licensed, see <c>THIRD_PARTY_NOTICES.txt</c>): the function ids from
/// <c>nvapi_interface.h</c>, the signatures from <c>nvapi.h</c>, and the struct layout from
/// <c>NVDRS_SETTING_V1</c>. Nothing here is written from memory, and nothing is guessed: the layout is
/// asserted at runtime against the numbers the header implies.</para>
///
/// <para><b>Why the layout assertions matter.</b> Marshal a struct into the wrong offset and the driver
/// receives a plausible-looking but wrong request. A test cannot catch that, and the failure appears as a
/// silently misconfigured user profile — in a code path whose entire purpose is to be able to undo itself.
/// So the offsets and the total size are checked before the first call, and a mismatch disables the adapter
/// rather than proceeding.</para>
/// </summary>
public sealed class NvApiDrsAdapter : IDrsAdapter
{
    // ── Official function ids, transcribed from NVIDIA/nvapi's nvapi_interface.h (MIT) ──────────────

    private const uint IdInitialize = 0x0150e828;
    private const uint IdUnload = 0xd22bdd7e;
    private const uint IdDrsCreateSession = 0x0694d52e;
    private const uint IdDrsDestroySession = 0xdad9cff8;
    private const uint IdDrsLoadSettings = 0x375dbd6b;
    private const uint IdDrsSaveSettings = 0xfcbc7e14;
    private const uint IdDrsFindProfileByName = 0x7e4a9a0b;
    private const uint IdDrsGetSetting = 0x73bf8338;
    private const uint IdDrsSetSetting = 0x577dd202;
    private const uint IdDrsDeleteProfileSetting = 0xe4a26362;

    /// <summary>NVAPI_OK. Every other value is a failure and is reported by number, never swallowed.</summary>
    private const int NvApiOk = 0;

    private const int NvApiSettingNotFound = -160;

    // ── Struct numbers implied by the header (NVDRS_SETTING_V1, #pragma pack(push, 4)) ─────────────
    //
    //   version              NvU32                      offset 0
    //   settingName          NvAPI_UnicodeString        offset 4      (NvU16[2048] = 4096 bytes)
    //   settingId            NvU32                      offset 4100
    //   settingType          enum                       offset 4104
    //   settingLocation      enum                       offset 4108
    //   isCurrentPredefined  NvU32                      offset 4112
    //   isPredefinedValid    NvU32                      offset 4116
    //   union (predefined)   largest member 4100       offset 4120
    //   union (current)      largest member 4100       offset 8220
    //   ------------------------------------------------------------------
    //   sizeof                                           12320

    private const int UnicodeStringLength = 2048;
    private const int BinaryDataMax = 4096;

    /// <summary>sizeof(NVDRS_SETTING_V1) with pack(4).</summary>
    private const int SettingSize = 12320;

    /// <summary>MAKE_NVAPI_VERSION(NVDRS_SETTING_V1, 1) = sizeof | (1 &lt;&lt; 16).</summary>
    private const uint SettingVersion = SettingSize | (1u << 16);

    // NVDRS_SETTING_TYPE
    private const uint TypeDword = 0;

    // NVDRS_SETTING_LOCATION
    private const uint LocationCurrentProfile = 0;

    private readonly object _gate = new();
    private bool _loadAttempted;
    private bool _available;
    private string _unavailableReason = "";

    private IntPtr _session = IntPtr.Zero;
    private IntPtr _profile = IntPtr.Zero;

    private InitializeDelegate? _initialize;
    private DrsCreateSessionDelegate? _createSession;
    private DrsDestroySessionDelegate? _destroySession;
    private DrsLoadSettingsDelegate? _loadSettings;
    private DrsSaveSettingsDelegate? _saveSettings;
    private DrsFindProfileByNameDelegate? _findProfileByName;
    private DrsGetSettingDelegate? _getSetting;
    private DrsSetSettingDelegate? _setSetting;
    private DrsDeleteProfileSettingDelegate? _deleteProfileSetting;

    public string Name => "nvapi64.dll";

    public bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _available;
        }
    }

    /// <summary>
    /// Whether the resolved entry points may actually be called.
    ///
    /// <para><b>False, despite everything resolving.</b> A read-only smoke test on real hardware
    /// (RTX 3070 Ti, driver <c>32.0.16.1714</c>) did all of this successfully — loaded NVAPI, passed every
    /// struct-layout assertion, opened a DRS session on the base profile, and read one setting correctly —
    /// and then crashed with <c>AccessViolationException</c> on the very next read:
    /// <c>Attempted to read or write protected memory</c>.</para>
    ///
    /// <para>Marshalling <c>NVDRS_SETTING_V1</c> back out of the driver is therefore <b>not</b> proven
    /// correct, and the failure mode is a crash inside the process that is configuring a user's driver
    /// profile. Until the layout is understood well enough to be safe, every operation refuses to run.</para>
    ///
    /// <para>An unproven ABI fails closed. That rule does not stop applying just because the entry points
    /// were found — finding them was never the hard part.</para>
    /// </summary>
    private static readonly bool DriverCallsProven = false;

    /// <summary>The reason reported while <see cref="DriverCallsProven"/> is false.</summary>
    private const string UnprovenAbi =
        "NVAPI 已加载且结构体布局断言通过，但真实驱动上的只读 Smoke 在第二次读取时触发 " +
        "AccessViolationException：NVDRS_SETTING_V1 的往返封送尚未被证明正确，因此拒绝调用任何驱动接口。";

    /// <summary>Reads would go through <c>NvAPI_DRS_GetSetting</c> — refused until the layout is proven.</summary>
    public bool CanRead => DriverCallsProven && IsAvailable && _getSetting is not null;

    /// <summary>
    /// Deletion would go through <c>NvAPI_DRS_DeleteProfileSetting</c>. Never optional: without it a setting
    /// could be written and never removed again.
    /// </summary>
    public bool CanDelete => DriverCallsProven && IsAvailable && _deleteProfileSetting is not null;

    /// <summary>Committing would go through <c>NvAPI_DRS_SaveSettings</c> — refused until the layout is proven.</summary>
    public bool CanSave => DriverCallsProven && IsAvailable && _saveSettings is not null;

    public bool CanWrite => CanRead && CanDelete && CanSave;

    /// <summary>
    /// Why the adapter will not operate, for diagnostics. Empty only when it is genuinely usable.
    ///
    /// Reports the unproven-ABI reason when the library loads but driver calls are refused, so a caller
    /// asking "can I use this?" never sees an empty explanation next to a false capability.
    /// </summary>
    public string UnavailableReason
    {
        get
        {
            EnsureLoaded();
            if (_unavailableReason.Length > 0) return _unavailableReason;
            return DriverCallsProven ? "" : UnprovenAbi;
        }
    }

    public DrsStatus Open(string? profileName)
    {
        EnsureLoaded();
        if (!_available) return DrsStatus.Fail(-1, _unavailableReason);
        if (_createSession is null) return DrsStatus.Fail(-1, "NvAPI_DRS_CreateSession 未被解析。");

        Close();

        var create = _createSession(out var session);
        if (create != NvApiOk || session == IntPtr.Zero)
            return DrsStatus.Fail(create, $"NvAPI_DRS_CreateSession 返回 {create}。");

        _session = session;

        var load = _loadSettings?.Invoke(session) ?? -1;
        if (load != NvApiOk)
        {
            Close();
            return DrsStatus.Fail(load, $"NvAPI_DRS_LoadSettings 返回 {load}。");
        }

        // A null profile name means the base/global profile. Naming a profile that does not exist is a
        // failure, not something to paper over by silently falling back to the global profile: writing to
        // the wrong profile is worse than not writing.
        if (profileName is null)
        {
            _profile = GlobalProfile;
            return DrsStatus.Success;
        }

        if (_findProfileByName is null)
        {
            Close();
            return DrsStatus.Fail(-1, "NvAPI_DRS_FindProfileByName 未被解析。");
        }

        var buffer = ToUnicodeString(profileName);
        var found = _findProfileByName(session, buffer, out var profile);
        if (found != NvApiOk || profile == IntPtr.Zero)
        {
            Close();
            return DrsStatus.Fail(found, $"未找到 Profile「{profileName}」（NvAPI_DRS_FindProfileByName 返回 {found}）。");
        }

        _profile = profile;
        return DrsStatus.Success;
    }

    /// <summary>The handle NVAPI uses for the global profile: <c>((NvDRSProfileHandle) -1)</c>.</summary>
    private static readonly IntPtr GlobalProfile = new(-1);

    public ProfileSettingSnapshot Read(uint settingId)
    {
        if (!CanRead) return ProfileSettingSnapshot.Unreadable(settingId, UnprovenAbi);

        EnsureLoaded();
        if (!_available) return ProfileSettingSnapshot.Unreadable(settingId, _unavailableReason);
        if (_session == IntPtr.Zero) return ProfileSettingSnapshot.Unreadable(settingId, "没有已打开的 DRS 会话。");
        if (_getSetting is null) return ProfileSettingSnapshot.Unreadable(settingId, "NvAPI_DRS_GetSetting 未被解析。");

        // NVDRS_SETTING is an in/out struct: the caller supplies the version and gets the values back.
        var setting = NewSetting(settingId, 0);
        var status = _getSetting(_session, _profile, settingId, ref setting);

        if (status == NvApiSettingNotFound)
        {
            // The setting is not in this profile at all: the driver would use its predefined value.
            // This is genuinely different from "set to 0", and it is the state deletion restores.
            return new ProfileSettingSnapshot(settingId, ProfileSettingState.Absent, 0, false,
                "设置不存在于该 Profile（驱动将使用预定义值）。");
        }

        if (status != NvApiOk)
            return ProfileSettingSnapshot.Unreadable(settingId, $"NvAPI_DRS_GetSetting 返回 {status}。");

        // settingLocation distinguishes a value that lives in the profile from one inherited from the
        // global/default profile. isCurrentPredefined says the value came from NVIDIA's table rather than
        // the user, which is why it must not be treated as "the user's value".
        var predefined = setting.IsCurrentPredefined != 0;

        if (predefined)
        {
            return new ProfileSettingSnapshot(settingId, ProfileSettingState.InheritedDefault,
                setting.CurrentValueLength, true, "当前值来自驱动预定义表。");
        }

        return new ProfileSettingSnapshot(settingId, ProfileSettingState.ExplicitValue,
            setting.CurrentValueLength, false, "当前值为用户设置值。");
    }

    public DrsStatus Write(uint settingId, uint value)
    {
        if (!CanWrite) return DrsStatus.Fail(-1, UnprovenAbi);

        EnsureLoaded();
        if (!_available) return DrsStatus.Fail(-1, _unavailableReason);
        if (_session == IntPtr.Zero) return DrsStatus.Fail(-1, "没有已打开的 DRS 会话。");
        if (_setSetting is null) return DrsStatus.Fail(-1, "NvAPI_DRS_SetSetting 未被解析。");

        if (!CanDelete)
            return DrsStatus.Fail(-1, "当前不具备删除能力，拒绝写入：写入后无法撤回。");

        // Build on whatever is already there, so the fields we are not changing (type, location, name) keep
        // their real values. Inventing them would send the driver a request it never asked for.
        var setting = NewSetting(settingId, value);
        var existing = NewSetting(settingId, 0);
        if (_getSetting is not null && _getSetting(_session, _profile, settingId, ref existing) == NvApiOk)
        {
            setting = existing;
            setting.CurrentValueLength = value;
            setting.IsCurrentPredefined = 0;
        }

        var status = _setSetting(_session, _profile, ref setting);
        return status == NvApiOk
            ? DrsStatus.Success
            : DrsStatus.Fail(status, $"NvAPI_DRS_SetSetting 返回 {status}。");
    }

    public DrsStatus Delete(uint settingId)
    {
        if (!CanDelete) return DrsStatus.Fail(-1, UnprovenAbi);

        EnsureLoaded();
        if (!_available) return DrsStatus.Fail(-1, _unavailableReason);
        if (_session == IntPtr.Zero) return DrsStatus.Fail(-1, "没有已打开的 DRS 会话。");
        if (_deleteProfileSetting is null) return DrsStatus.Fail(-1, "NvAPI_DRS_DeleteProfileSetting 未被解析。");

        var status = _deleteProfileSetting(_session, _profile, settingId);
        return status == NvApiOk
            ? DrsStatus.Success
            : DrsStatus.Fail(status, $"NvAPI_DRS_DeleteProfileSetting 返回 {status}。");
    }

    public DrsStatus Save()
    {
        if (!CanSave) return DrsStatus.Fail(-1, UnprovenAbi);

        EnsureLoaded();
        if (!_available) return DrsStatus.Fail(-1, _unavailableReason);
        if (_session == IntPtr.Zero) return DrsStatus.Fail(-1, "没有已打开的 DRS 会话。");
        if (_saveSettings is null) return DrsStatus.Fail(-1, "NvAPI_DRS_SaveSettings 未被解析。");

        var status = _saveSettings(_session);
        return status == NvApiOk
            ? DrsStatus.Success
            : DrsStatus.Fail(status, $"NvAPI_DRS_SaveSettings 返回 {status}。");
    }

    public void Close()
    {
        if (_session == IntPtr.Zero) return;

        try
        {
            _destroySession?.Invoke(_session);
        }
        catch
        {
            // Releasing a session must never throw out of a finally block.
        }

        _session = IntPtr.Zero;
        _profile = IntPtr.Zero;
    }

    // ── Loading ────────────────────────────────────────────────────────────────────────────────────

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loadAttempted) return;
            _loadAttempted = true;

            // Layout first: a wrong offset would send the driver a plausible but wrong request, and no test
            // can catch that.
            var layout = VerifyLayout();
            if (layout is not null)
            {
                _available = false;
                _unavailableReason = layout;
                return;
            }

            try
            {
                _initialize = Resolve<InitializeDelegate>(IdInitialize);
                _createSession = Resolve<DrsCreateSessionDelegate>(IdDrsCreateSession);
                _destroySession = Resolve<DrsDestroySessionDelegate>(IdDrsDestroySession);
                _loadSettings = Resolve<DrsLoadSettingsDelegate>(IdDrsLoadSettings);
                _saveSettings = Resolve<DrsSaveSettingsDelegate>(IdDrsSaveSettings);
                _findProfileByName = Resolve<DrsFindProfileByNameDelegate>(IdDrsFindProfileByName);
                _getSetting = Resolve<DrsGetSettingDelegate>(IdDrsGetSetting);
                _setSetting = Resolve<DrsSetSettingDelegate>(IdDrsSetSetting);
                _deleteProfileSetting = Resolve<DrsDeleteProfileSettingDelegate>(IdDrsDeleteProfileSetting);

                if (_initialize is null || _createSession is null || _loadSettings is null)
                {
                    _available = false;
                    _unavailableReason = "NVAPI 未导出必需的 DRS 入口点。";
                    return;
                }

                var status = _initialize();
                if (status != NvApiOk)
                {
                    _available = false;
                    _unavailableReason = $"NvAPI_Initialize 返回 {status}（可能未安装 NVIDIA 驱动）。";
                    return;
                }

                _available = true;
                _unavailableReason = "";
            }
            catch (DllNotFoundException)
            {
                _available = false;
                _unavailableReason = "未找到 nvapi64.dll（本机没有 NVIDIA 驱动）。";
            }
            catch (EntryPointNotFoundException)
            {
                _available = false;
                _unavailableReason = "nvapi64.dll 未导出 nvapi_QueryInterface。";
            }
            catch (Exception ex)
            {
                _available = false;
                _unavailableReason = $"加载 NVAPI 失败：{ex.GetType().Name} {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Checks the marshalled layout against the numbers the official header implies. Returns null when it
    /// matches, or a reason when it does not.
    /// </summary>
    private static string? VerifyLayout()
    {
        var size = Marshal.SizeOf<NvDrsSetting>();
        if (size != SettingSize)
            return $"NVDRS_SETTING_V1 的封送大小是 {size}，官方头文件推导为 {SettingSize}；已拒绝使用该布局。";

        var checks = new (string Field, int Expected)[]
        {
            (nameof(NvDrsSetting.Version), 0),
            (nameof(NvDrsSetting.SettingName), 4),
            (nameof(NvDrsSetting.SettingId), 4100),
            (nameof(NvDrsSetting.SettingType), 4104),
            (nameof(NvDrsSetting.SettingLocation), 4108),
            (nameof(NvDrsSetting.IsCurrentPredefined), 4112),
            (nameof(NvDrsSetting.IsPredefinedValid), 4116),
            (nameof(NvDrsSetting.PredefinedValueLength), 4120),
            (nameof(NvDrsSetting.CurrentValueLength), 8220),
        };

        foreach (var (field, expected) in checks)
        {
            var actual = (int)Marshal.OffsetOf<NvDrsSetting>(field);
            if (actual != expected)
                return $"{field} 的偏移是 {actual}，官方头文件推导为 {expected}；已拒绝使用该布局。";
        }

        return null;
    }

    private static T? Resolve<T>(uint id) where T : Delegate
    {
        var address = QueryInterface(id);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvapi_QueryInterface")]
    private static extern IntPtr QueryInterface(uint id);

    // ── Marshalling ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>NVDRS_SETTING_V1</c>.
    ///
    /// The two trailing unions are declared as their largest member (<c>NVDRS_BINARY_SETTING</c>, which is a
    /// length plus 4096 bytes), because that is what determines the layout. For a DWORD setting the first
    /// four bytes of each union are the value itself, which is why the length fields double as the DWORD.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct NvDrsSetting
    {
        public uint Version;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = UnicodeStringLength)]
        public ushort[] SettingName;

        public uint SettingId;
        public uint SettingType;
        public uint SettingLocation;
        public uint IsCurrentPredefined;
        public uint IsPredefinedValid;

        // union { u32PredefinedValue | binaryPredefinedValue | wszPredefinedValue | u64PredefinedValue }
        public uint PredefinedValueLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = BinaryDataMax)]
        public byte[] PredefinedValueData;

        // union { u32CurrentValue | binaryCurrentValue | wszCurrentValue | u64CurrentValue }
        public uint CurrentValueLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = BinaryDataMax)]
        public byte[] CurrentValueData;
    }

    private static NvDrsSetting NewSetting(uint settingId, uint value) => new()
    {
        Version = SettingVersion,
        SettingName = new ushort[UnicodeStringLength],
        SettingId = settingId,
        SettingType = TypeDword,
        SettingLocation = LocationCurrentProfile,
        IsCurrentPredefined = 0,
        IsPredefinedValid = 0,
        PredefinedValueLength = 0,
        PredefinedValueData = new byte[BinaryDataMax],
        CurrentValueLength = value,
        CurrentValueData = new byte[BinaryDataMax],
    };

    /// <summary>
    /// Converts a managed string to the fixed-size UTF-16 buffer NVAPI expects, truncating at the buffer
    /// limit rather than overflowing it.
    /// </summary>
    private static ushort[] ToUnicodeString(string value)
    {
        var buffer = new ushort[UnicodeStringLength];
        var length = Math.Min(value.Length, UnicodeStringLength - 1);
        for (var i = 0; i < length; i++) buffer[i] = value[i];
        return buffer;
    }

    // ── Function pointers ──────────────────────────────────────────────────────────────────────────
    //
    // All NVAPI entry points use __cdecl (nvapi_lite_salstart.h: "#define NVAPI_INTERFACE extern
    // __success(return == NVAPI_OK) NvAPI_Status __cdecl"). On x64 there is only one calling convention, so
    // this declaration is correct on the platform this project ships for.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsCreateSessionDelegate(out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsDestroySessionDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsLoadSettingsDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsSaveSettingsDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsFindProfileByNameDelegate(IntPtr session, ushort[] profileName, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsGetSettingDelegate(IntPtr session, IntPtr profile, uint settingId, ref NvDrsSetting setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsSetSettingDelegate(IntPtr session, IntPtr profile, ref NvDrsSetting setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsDeleteProfileSettingDelegate(IntPtr session, IntPtr profile, uint settingId);
}

/// <summary>
/// A driver boundary that is not there at all — used on machines without an NVIDIA driver, and as the
/// default before an adapter is chosen.
///
/// Distinct from <see cref="NvApiDrsAdapter"/>: this one does not claim the driver exists. Every call
/// reports the same honest reason, so callers can branch on capability instead of on exceptions.
/// </summary>
public sealed class AbsentDrsAdapter : IDrsAdapter
{
    private const string Reason = "未检测到可用的 NVIDIA 驱动接口。";

    public string Name => "(无驱动接口)";

    public bool IsAvailable => false;

    public bool CanRead => false;

    public bool CanDelete => false;

    public bool CanSave => false;

    public DrsStatus Open(string? profileName) => DrsStatus.Fail(-1, Reason);

    public ProfileSettingSnapshot Read(uint settingId) => ProfileSettingSnapshot.Unreadable(settingId, Reason);

    public DrsStatus Write(uint settingId, uint value) => DrsStatus.Fail(-1, Reason);

    public DrsStatus Delete(uint settingId) => DrsStatus.Fail(-1, Reason);

    public DrsStatus Save() => DrsStatus.Fail(-1, Reason);

    public void Close() { }
}
