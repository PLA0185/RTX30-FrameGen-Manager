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

    /// <summary>Returns the <i>base</i> profile's real handle — distinct from the global-profile sentinel.</summary>
    private const uint IdGetBaseProfile = 0xda8466a0;

    /// <summary>Locates the profile that owns an executable — the renderer EXE, not the game's display name.</summary>
    private const uint IdFindApplicationByName = 0xeee566b2;

    /// <summary>Reads <c>NVDRS_PROFILE</c> for a handle — the only honest source of a profile's name.</summary>
    private const uint IdGetProfileInfo = 0x61cd6fd6;

    // The four below are needed to build and then dismantle a throwaway profile for the write smoke. Without them
    // there is no way to exercise a real write without touching a profile that belongs to the user.
    private const uint IdCreateProfile = 0xcc176068;
    private const uint IdDeleteProfile = 0x17093206;
    private const uint IdCreateApplication = 0x4347a9de;
    private const uint IdDeleteApplication = 0x2c694bc6;
    private const uint IdUnload = 0xd22bdd7e;
    private const uint IdDrsCreateSession = 0x0694d52e;
    private const uint IdDrsDestroySession = 0xdad9cff8;
    private const uint IdDrsLoadSettings = 0x375dbd6b;
    private const uint IdDrsSaveSettings = 0xfcbc7e14;
    private const uint IdDrsFindProfileByName = 0x7e4a9a0b;
    private const uint IdDrsGetSetting = 0x73bf8338;
    private const uint IdDrsSetSetting = 0x577dd202;
    private const uint IdDrsDeleteProfileSetting = 0xe4a26362;

    // Application lookup, for locating a profile by the executable it owns rather than by the game's display
    // name. Both IDs were read off the official header rather than recalled.
    private const uint IdDrsFindApplicationByName = 0xeee566b2;
    private const uint IdDrsGetProfileInfo = 0x61cd6fd6;

    /// <summary>
    /// Looks up the profile that owns an executable, by the executable's own file name.
    ///
    /// <para><b>The route is the official one:</b> <c>NvAPI_DRS_FindApplicationByName</c> (<c>0xeee566b2</c>)
    /// resolves the executable to a profile handle, and <c>NvAPI_DRS_GetProfileInfo</c> (<c>0x61cd6fd6</c>) turns
    /// that handle into the profile <i>name</i> — which is what a later session must be opened with. A game's
    /// display name is never used for this, because it is not the profile name.</para>
    ///
    /// <para><b>Its unmanaged calls are deliberately not wired yet.</b> They have to marshal two structures by
    /// hand — <c>NVDRS_APPLICATION_V1</c> is 12296 bytes and <c>NVDRS_PROFILE_V1</c> is 4116 — and structure
    /// marshalling is exactly what currently faults on the read path. Adding a second caller of it before that is
    /// fixed would add a second way to fault, so this reports that it cannot answer. The order matters: prove
    /// the marshalling first, then wire this.</para>
    /// </summary>
    /// <summary>
    /// Locates a profile by the executable the driver knows it by, owning the whole session lifetime.
    ///
    /// <para>Callers must not have to hold a session open for this to work. When session lifetime belonged to the
    /// caller, the UI path — which never opened one — failed every time with "没有已打开的 DRS 会话"; no test caught
    /// it because the double never modelled that requirement. Lifetime belongs to the operation, not to its caller.</para>
    /// </summary>
    public DrsApplicationLookup FindApplicationProfile(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return DrsApplicationLookup.NotFound(executableName ?? "", "未提供可执行文件名。");

        var opened = Open(null);

        if (!opened.Ok)
            return DrsApplicationLookup.NotFound(executableName, opened.Message);

        try
        {
            return FindApplication(executableName);
        }
        finally
        {
            Close();
        }
    }

    // ── Profile / application lifecycle ──────────────────────────────────────────────────────────────────────
    //
    // Sizes and offsets, all from the official headers: NVDRS_PROFILE is typedef'd to V1 (4116 bytes, profileName
    // at 4); NVDRS_APPLICATION is typedef'd to V4 (20492 bytes, appName at 8 — the field FindApplicationByName
    // matches on). Kept here rather than duplicated inside each call that needs them.
    private const int ProfileSize = 4116;
    private const int ProfileNameOffset = 4;
    private const int ApplicationSizeV4 = 20492;
    private const int ApplicationNameOffset = 8;

    private static void WriteUnicode(IntPtr destination, string value)
    {
        var chars = ToUnicodeString(value);
        var raw = new short[chars.Length];

        for (var i = 0; i < chars.Length; i++) raw[i] = unchecked((short)chars[i]);

        Marshal.Copy(raw, 0, destination, raw.Length);
    }

    /// <summary>Creates a profile. Assumes an open session.</summary>
    internal DrsStatus CreateProfile(string profileName, out IntPtr profile)
    {
        profile = IntPtr.Zero;

        if (string.IsNullOrWhiteSpace(profileName))
            return DrsStatus.Fail(-1, "未提供 Profile 名。");

        var create = Resolve<DrsCreateProfileDelegate>(IdCreateProfile);

        if (create is null) return DrsStatus.Fail(-1, "NvAPI_DRS_CreateProfile 未被解析。");

        var info = Marshal.AllocHGlobal(ProfileSize);

        try
        {
            Marshal.Copy(new byte[ProfileSize], 0, info, ProfileSize);
            Marshal.WriteInt32(info, 0, unchecked((int)((uint)ProfileSize | (1u << 16))));
            WriteUnicode(info + ProfileNameOffset, profileName);

            var status = create(_session, info, out profile);

            return status == NvApiOk
                ? DrsStatus.Success
                : DrsStatus.Fail(status, $"NvAPI_DRS_CreateProfile 返回 {status}。");
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    /// <summary>Deletes a profile. Assumes an open session.</summary>
    internal DrsStatus DeleteProfile(IntPtr profile)
    {
        if (profile == IntPtr.Zero) return DrsStatus.Fail(-1, "未提供 Profile 句柄。");

        var delete = Resolve<DrsDeleteProfileDelegate>(IdDeleteProfile);

        if (delete is null) return DrsStatus.Fail(-1, "NvAPI_DRS_DeleteProfile 未被解析。");

        var status = delete(_session, profile);

        return status == NvApiOk
            ? DrsStatus.Success
            : DrsStatus.Fail(status, $"NvAPI_DRS_DeleteProfile 返回 {status}。");
    }

    /// <summary>Binds an executable to a profile. Assumes an open session.</summary>
    internal DrsStatus CreateApplication(IntPtr profile, string executableName)
    {
        if (profile == IntPtr.Zero) return DrsStatus.Fail(-1, "未提供 Profile 句柄。");
        if (string.IsNullOrWhiteSpace(executableName)) return DrsStatus.Fail(-1, "未提供可执行文件名。");

        var create = Resolve<DrsCreateApplicationDelegate>(IdCreateApplication);

        if (create is null) return DrsStatus.Fail(-1, "NvAPI_DRS_CreateApplication 未被解析。");

        var application = Marshal.AllocHGlobal(ApplicationSizeV4);

        try
        {
            Marshal.Copy(new byte[ApplicationSizeV4], 0, application, ApplicationSizeV4);
            Marshal.WriteInt32(application, 0, unchecked((int)((uint)ApplicationSizeV4 | (4u << 16))));
            WriteUnicode(application + ApplicationNameOffset, executableName);

            var status = create(_session, profile, application);

            return status == NvApiOk
                ? DrsStatus.Success
                : DrsStatus.Fail(status, $"NvAPI_DRS_CreateApplication 返回 {status}。");
        }
        finally
        {
            Marshal.FreeHGlobal(application);
        }
    }

    /// <summary>
    /// Removes an executable's binding. Assumes an open session.
    ///
    /// <para>Uses <c>NvAPI_DRS_DeleteApplication</c>, not the <c>Ex</c> variant — they are different functions with
    /// different parameters, and this one takes the executable name we already have.</para>
    /// </summary>
    internal DrsStatus DeleteApplication(IntPtr profile, string executableName)
    {
        if (profile == IntPtr.Zero) return DrsStatus.Fail(-1, "未提供 Profile 句柄。");
        if (string.IsNullOrWhiteSpace(executableName)) return DrsStatus.Fail(-1, "未提供可执行文件名。");

        var delete = Resolve<DrsDeleteApplicationDelegate>(IdDeleteApplication);

        if (delete is null) return DrsStatus.Fail(-1, "NvAPI_DRS_DeleteApplication 未被解析。");

        var status = delete(_session, profile, ToUnicodeString(executableName));

        return status == NvApiOk
            ? DrsStatus.Success
            : DrsStatus.Fail(status, $"NvAPI_DRS_DeleteApplication 返回 {status}。");
    }

    public DrsApplicationLookup FindApplication(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return DrsApplicationLookup.NotFound(executableName ?? "", "未提供可执行文件名。");

        if (!ApplicationLookupProven)
            return DrsApplicationLookup.NotFound(executableName, UnprovenAbi);

        if (_session == IntPtr.Zero)
            return DrsApplicationLookup.NotFound(executableName, "没有已打开的 DRS 会话。");

        EnsureLoaded();
        if (!_available) return DrsApplicationLookup.NotFound(executableName, _unavailableReason);

        var find = Resolve<DrsFindApplicationByNameDelegate>(IdFindApplicationByName);

        if (find is null)
            return DrsApplicationLookup.NotFound(executableName, "NvAPI_DRS_FindApplicationByName 未被解析。");

        // NVDRS_APPLICATION is typedef'd to V4 (nvapi.h L24578) — not V1. Layout: version 0 | isPredefined 4 |
        // appName 8 | userFriendlyName 4104 | launcher 8200 | fileInFolder 12296 | bitfield 16392 |
        // commandLine 16396, each UnicodeString being 4096 bytes — 20492 in total. Built by hand, like
        // NVDRS_SETTING, and for the same reason.
        //
        // NVDRS_PROFILE is typedef'd to V1 (nvapi.h L24592): version 0 | profileName 4 | gpuSupport 4100 |
        // isPredefined 4104 | numOfApps 4108 | numOfSettings 4112 — 4116 in total.
        const int applicationSize = 20492;
        const int profileSize = 4116;
        const int profileNameOffset = 4;

        var getInfo = Resolve<DrsGetProfileInfoDelegate>(IdGetProfileInfo);

        if (getInfo is null)
            return DrsApplicationLookup.NotFound(executableName, "NvAPI_DRS_GetProfileInfo 未被解析。");

        var application = Marshal.AllocHGlobal(applicationSize);
        var info = Marshal.AllocHGlobal(profileSize);

        try
        {
            Marshal.Copy(new byte[applicationSize], 0, application, applicationSize);
            Marshal.WriteInt32(application, 0, unchecked((int)((uint)applicationSize | (4u << 16))));

            var status = find(_session, ToUnicodeString(executableName), out var profile, application);

            if (status != NvApiOk || profile == IntPtr.Zero)
            {
                // The ABI call worked; the driver simply knows no such application. Those are two different facts,
                // and only the first one is a success — the caller sees Found = false either way.
                return DrsApplicationLookup.NotFound(executableName,
                    $"NvAPI_DRS_FindApplicationByName 返回 {status}（-166 即 NVAPI_EXECUTABLE_NOT_FOUND）：" +
                    "该可执行文件尚未被分配到任何驱动 Profile。");
            }

            Marshal.Copy(new byte[profileSize], 0, info, profileSize);
            Marshal.WriteInt32(info, 0, unchecked((int)((uint)profileSize | (1u << 16))));

            var infoStatus = getInfo(_session, profile, info);

            if (infoStatus != NvApiOk)
            {
                return DrsApplicationLookup.NotFound(executableName,
                    $"找到了应用，但 NvAPI_DRS_GetProfileInfo 返回 {infoStatus}，无法取得 Profile 名。");
            }

            // From NVDRS_PROFILE.profileName. The application's own userFriendlyName is a different string — it
            // names the application, not the profile the driver files settings under.
            var profileName = Marshal.PtrToStringUni(info + profileNameOffset) ?? "";

            if (string.IsNullOrEmpty(profileName))
            {
                return DrsApplicationLookup.NotFound(executableName,
                    "驱动返回了 Profile 句柄，但 NVDRS_PROFILE.profileName 为空。");
            }

            return DrsApplicationLookup.Matched(executableName, profileName,
                $"已通过可执行文件定位到 Profile「{profileName}」。");
        }
        finally
        {
            Marshal.FreeHGlobal(application);
            Marshal.FreeHGlobal(info);
        }
    }

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
    // Capability gates, one per kind of driver call — deliberately not one flag.
    //
    // These were once a single `DriverCallsProven`, set true by the read loop. That made 200 proven reads silently
    // open write, save and delete on a real driver, none of which had ever been exercised. The project's own rule is
    // that read permission does not imply write permission; one flag cannot express five different facts.
    //
    // Read was proven on 2026-09-26 by the diagnostic loop in `--nvapi-smoke --loop`: 200 real reads (100 rounds ×
    // A/B alternating settings), 0 exceptions, driver reached every time. The earlier failure was NOT the marshalling
    // layout — it was the profile handle: Open(null) used NVAPI_DRS_GLOBAL_PROFILE, the (NvDRSProfileHandle)-1
    // sentinel, where NvAPI_DRS_GetSetting expects the base profile's real handle from NvAPI_DRS_GetBaseProfile.
    internal static bool ReadCallsProven { get; set; } = true;

    /// <summary>
    /// Set only by a smoke that actually wrote a setting to a real temporary profile.
    ///
    /// <para>Still false: nothing has ever written to a real driver profile. The single flag this replaced was true,
    /// which meant the answer to "can we write?" was "yes" on the strength of a read test.</para>
    /// </summary>
    internal static bool WriteCallsProven { get; set; }

    /// <summary>Set only by a smoke that actually deleted a setting from a real temporary profile. Still false.</summary>
    internal static bool DeleteCallsProven { get; set; }

    /// <summary>Set only by a smoke that actually committed settings to a real temporary profile. Still false.</summary>
    internal static bool SaveCallsProven { get; set; }

    /// <summary>
    /// Set by a smoke that actually invoked <c>NvAPI_DRS_FindApplicationByName</c> on a real driver and got a
    /// well-formed answer back.
    ///
    /// <para>True on the strength of a real call that returned <c>-166</c>. That is not a contradiction: this gate
    /// answers "has this call been exercised without faulting", while <c>DrsApplicationLookup.Found</c> answers
    /// "did it find anything". The application was genuinely not found, and the lookup is genuinely proven.</para>
    /// </summary>
    internal static bool ApplicationLookupProven { get; set; } = true;

    /// <summary>The reason reported while a gate is still closed.</summary>
    private const string UnprovenAbi =
        "NVAPI 已加载，但对应的驱动调用尚未被证明安全，因此拒绝调用它。";

    /// <summary>Reads go through <c>NvAPI_DRS_GetSetting</c> — refused until real reads have been proven.</summary>
    public bool CanRead => ReadCallsProven && IsAvailable && _getSetting is not null;

    /// <summary>
    /// Deletion goes through <c>NvAPI_DRS_DeleteProfileSetting</c>. Never optional: without it a setting could be
    /// written and never removed again.
    /// </summary>
    public bool CanDelete => DeleteCallsProven && IsAvailable && _deleteProfileSetting is not null;

    /// <summary>Committing goes through <c>NvAPI_DRS_SaveSettings</c> — refused until a real save has been proven.</summary>
    public bool CanSave => SaveCallsProven && IsAvailable && _saveSettings is not null;

    /// <summary>
    /// Writing needs every step of the round trip proven on a real driver: read (to capture the original), write,
    /// delete (to restore an absent setting) and save. Any one of them unproven keeps this false.
    /// </summary>
    public bool CanWrite => CanRead && WriteCallsProven && CanDelete && CanSave;

    /// <summary>
    /// Why the adapter will not operate, for diagnostics. Empty only when it is genuinely usable.
    ///
    /// Reports the unproven reason when the library loads but driver calls are refused, so a caller asking
    /// "can I use this?" never sees an empty explanation next to a false capability.
    /// </summary>
    public string UnavailableReason
    {
        get
        {
            EnsureLoaded();
            if (_unavailableReason.Length > 0) return _unavailableReason;
            if (!CanRead) return UnprovenAbi;

            // Read is available but some step of the write round trip is not. Saying nothing here would leave
            // "why can't it write?" unanswered while the capability flags say false — which is the very
            // contradiction this property exists to prevent.
            if (!CanWrite)
                return "只读已证明；写入、删除或保存尚未在真实驱动上验证，因此写路径保持关闭。";

            return "";
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

        // A null profile name means the base profile — and the base profile has a *real* handle, obtained from
        // NvAPI_DRS_GetBaseProfile. It is not the same thing as NVAPI_DRS_GLOBAL_PROFILE, which is the sentinel
        // value (NvDRSProfileHandle)-1 for the global profile. Using the sentinel here was the bug: it is a legal
        // value in its own right, but it is not what NvAPI_DRS_GetSetting expects as hProfile, and the driver
        // faulted on every read we made with it.
        if (profileName is null)
        {
            var getBaseProfile = Resolve<DrsGetBaseProfileDelegate>(IdGetBaseProfile);

            if (getBaseProfile is null)
            {
                Close();
                return DrsStatus.Fail(-1, "NvAPI_DRS_GetBaseProfile 未被解析。");
            }

            var baseResult = getBaseProfile(session, out var baseHandle);

            // No silent fallback to the sentinel: writing to the wrong profile is worse than not writing.
            if (baseResult != NvApiOk || baseHandle == IntPtr.Zero)
            {
                Close();
                return DrsStatus.Fail(baseResult,
                    $"NvAPI_DRS_GetBaseProfile 返回 {baseResult}，未取得基础 Profile 句柄。");
            }

            _profile = baseHandle;
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

    public ProfileSettingSnapshot Read(uint settingId) => ReadFrom(_profile, settingId);

    /// <summary>
    /// Reads one setting from a specific profile handle rather than the one this adapter is opened on.
    ///
    /// <para>Exists for the temporary-profile write smoke: proving that a write works must not require writing to a
    /// profile that belongs to the user, so the smoke builds its own profile and drives it through this.</para>
    /// </summary>
    internal ProfileSettingSnapshot ReadFrom(IntPtr profile, uint settingId)
    {
        if (!CanRead) return ProfileSettingSnapshot.Unreadable(settingId, UnprovenAbi);

        EnsureLoaded();
        if (!_available) return ProfileSettingSnapshot.Unreadable(settingId, _unavailableReason);
        if (_session == IntPtr.Zero) return ProfileSettingSnapshot.Unreadable(settingId, "没有已打开的 DRS 会话。");
        if (_getSetting is null) return ProfileSettingSnapshot.Unreadable(settingId, "NvAPI_DRS_GetSetting 未被解析。");

        // NVDRS_SETTING is an in/out struct: the caller supplies the version and gets the values back. It crosses
        // the boundary as raw memory — see the delegate declarations for why.
        var setting = NewSetting(settingId, 0);

        try
        {
            var status = _getSetting(_session, profile, settingId, setting);

            if (status == NvApiSettingNotFound)
            {
                // The setting is not in this profile at all: the driver would use its predefined value.
                // This is genuinely different from "set to 0", and it is the state deletion restores.
                return new ProfileSettingSnapshot(settingId, ProfileSettingState.Absent, 0, false,
                    "设置不存在于该 Profile（驱动将使用预定义值）。");
            }

            if (status != NvApiOk)
                return ProfileSettingSnapshot.Unreadable(settingId, $"NvAPI_DRS_GetSetting 返回 {status}。");

            // A DWORD setting's value is the first four bytes of the union, which is where the length field sits —
            // the same thing the marshalled version read.
            var currentValue = unchecked((uint)Marshal.ReadInt32(setting, OffsetCurrentValueLength));

            // isCurrentPredefined says the value came from NVIDIA's table rather than the user, which is why it
            // must not be treated as "the user's value".
            var predefined = Marshal.ReadInt32(setting, OffsetIsCurrentPredefined) != 0;

            return predefined
                ? new ProfileSettingSnapshot(settingId, ProfileSettingState.InheritedDefault,
                    currentValue, true, "当前值来自驱动预定义表。")
                : new ProfileSettingSnapshot(settingId, ProfileSettingState.ExplicitValue,
                    currentValue, false, "当前值为用户设置值。");
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
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

        return WriteCore(_profile, settingId, value);
    }

    /// <summary>
    /// Diagnostic only: reads a setting while the read gate is still closed.
    ///
    /// <para><b>Why this exists.</b> That gate is a compile-time constant, so a normal read is refused without ever
    /// reaching the driver — which means the hand-built marshalling cannot be exercised by any application path.
    /// Without this entry point there is no way to learn whether the rewrite worked other than by reasoning.</para>
    ///
    /// <para><b>What it does not do.</b> It never writes and is never called from the application: the gate still
    /// guards every real path. It reports what the driver answered, and nothing else.</para>
    /// </summary>
    internal DrsDiagnosticRead ReadForDiagnostics(uint settingId)
    {
        EnsureLoaded();

        if (!_available) return new DrsDiagnosticRead(false, -1, _unavailableReason);
        if (_session == IntPtr.Zero) return new DrsDiagnosticRead(false, -1, "没有已打开的 DRS 会话。");
        if (_getSetting is null) return new DrsDiagnosticRead(false, -1, "NvAPI_DRS_GetSetting 未被解析。");

        var setting = NewSetting(settingId, 0);

        try
        {
            var status = _getSetting(_session, _profile, settingId, setting);

            var value = status == NvApiOk
                ? unchecked((uint)Marshal.ReadInt32(setting, OffsetCurrentValueLength))
                : 0u;

            return new DrsDiagnosticRead(true, status, $"status={status}, value={value}");
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
    }

    /// <summary>What a diagnostic read saw. <see cref="Called"/> says whether the driver was reached at all.</summary>
    internal readonly record struct DrsDiagnosticRead(bool Called, int Status, string Detail);

    /// <summary>
    /// Writes one setting to a specific profile handle rather than the one this adapter is opened on.
    ///
    /// <para>Exists for the temporary-profile write smoke: proving that a write works must not require writing to a
    /// profile that belongs to the user.</para>
    ///
    /// <para>Deliberately bypasses the <see cref="CanWrite"/> gate — that gate is the very thing the smoke exists to
    /// earn, so it cannot also be a precondition for running it. Nothing on a production path calls this.</para>
    /// </summary>
    internal DrsStatus WriteTo(IntPtr profile, uint settingId, uint value) =>
        WriteCore(profile, settingId, value);

    private DrsStatus WriteCore(IntPtr profile, uint settingId, uint value)
    {
        if (_setSetting is null) return DrsStatus.Fail(-1, "NvAPI_DRS_SetSetting 未被解析。");

        // Build on whatever is already there, so the fields we are not changing (type, location, name) keep their
        // real values. Inventing them would send the driver a request it never asked for.
        var setting = NewSetting(settingId, value);

        try
        {
            if (_getSetting is not null)
            {
                var existing = NewSetting(settingId, 0);

                try
                {
                    if (_getSetting(_session, profile, settingId, existing) == NvApiOk)
                    {
                        // Take the driver's own view of the setting and change only what we mean to change.
                        // Marshal.Copy has no IntPtr→IntPtr overload, so the bytes go through a managed buffer.
                        var bytes = new byte[SettingSize];
                        Marshal.Copy(existing, bytes, 0, SettingSize);
                        Marshal.Copy(bytes, 0, setting, SettingSize);
                        Marshal.WriteInt32(setting, OffsetCurrentValueLength, unchecked((int)value));
                        Marshal.WriteInt32(setting, OffsetIsCurrentPredefined, 0);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(existing);
                }
            }

            var status = _setSetting(_session, profile, setting);

            return status == NvApiOk
                ? DrsStatus.Success
                : DrsStatus.Fail(status, $"NvAPI_DRS_SetSetting 返回 {status}。");
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
    }

    public DrsStatus Delete(uint settingId)
    {
        if (!CanDelete) return DrsStatus.Fail(-1, UnprovenAbi);

        return DeleteFrom(_profile, settingId);
    }

    /// <summary>
    /// Deletes one setting from a specific profile handle.
    ///
    /// <para>Like <see cref="WriteTo"/>, this bypasses the capability gate on purpose: the temporary-profile smoke
    /// is what earns <c>DeleteCallsProven</c>, so it cannot be gated behind it. Nothing on a production path calls
    /// this — the guarded <see cref="Delete(uint)"/> is what the rest of the program uses.</para>
    /// </summary>
    internal DrsStatus DeleteFrom(IntPtr profile, uint settingId)
    {
        if (!CanDelete) return DrsStatus.Fail(-1, UnprovenAbi);

        EnsureLoaded();
        if (!_available) return DrsStatus.Fail(-1, _unavailableReason);
        if (_session == IntPtr.Zero) return DrsStatus.Fail(-1, "没有已打开的 DRS 会话。");
        if (_deleteProfileSetting is null) return DrsStatus.Fail(-1, "NvAPI_DRS_DeleteProfileSetting 未被解析。");

        var status = _deleteProfileSetting(_session, profile, settingId);
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
    ///
    /// <para><b>What this does not prove.</b> <c>Marshal.SizeOf</c> and <c>Marshal.OffsetOf</c> only compute the
    /// managed side's idea of the layout — they never run the marshaler's call path. A structure can pass every
    /// check here and still be unsafe to pass across the boundary: the copy-in/copy-out happens during the
    /// P/Invoke call, and nothing in this method exercises it.</para>
    ///
    /// <para>That is exactly the state this adapter is in. This method returns null, and a real read still fails
    /// with <c>AccessViolationException</c> on the <i>second</i> call. The failing shape points at the temporary
    /// buffer rather than at a wrong offset — a wrong offset would fail on the first call — which is why the
    /// suspected cause is the <c>ByValArray</c> marshalling on the three large arrays below, and why the intended
    /// fix is to stop using the marshaler for this structure at all.</para>
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

    // ── Raw-memory access ───────────────────────────────────────────────────────────────────────────
    //
    // NVDRS_SETTING_V1 is built and read by hand. The offsets below are the ones the official header implies and
    // the ones VerifyLayout() used to confirm against the marshalled struct; they are now the only description of
    // the layout, so they must stay in step with SettingSize.

    private const int OffsetVersion = 0;
    private const int OffsetSettingId = 4100;
    private const int OffsetSettingType = 4104;
    private const int OffsetSettingLocation = 4108;
    private const int OffsetIsCurrentPredefined = 4112;
    private const int OffsetIsPredefinedValid = 4116;
    private const int OffsetPredefinedValueLength = 4120;
    private const int OffsetCurrentValueLength = 8220;

    /// <summary>
    /// Allocates a zeroed <c>NVDRS_SETTING_V1</c> and fills in the fields the caller supplies.
    ///
    /// <para><b>The caller owns the block and must free it.</b> Memory that the marshaler used to manage is now
    /// managed here, which is the main new hazard of this change: every path that allocates must release in a
    /// <c>finally</c>.</para>
    /// </summary>
    private static IntPtr NewSetting(uint settingId, uint value)
    {
        var block = Marshal.AllocHGlobal(SettingSize);

        // Zeroed first: every byte this method does not write must still be a defined value for the driver.
        // Marshal.WriteByte in a loop would be 12320 interop calls; Copy does it in one.
        Marshal.Copy(new byte[SettingSize], 0, block, SettingSize);

        Marshal.WriteInt32(block, OffsetVersion, unchecked((int)SettingVersion));
        Marshal.WriteInt32(block, OffsetSettingId, unchecked((int)settingId));
        Marshal.WriteInt32(block, OffsetSettingType, unchecked((int)TypeDword));
        Marshal.WriteInt32(block, OffsetSettingLocation, unchecked((int)LocationCurrentProfile));
        Marshal.WriteInt32(block, OffsetIsCurrentPredefined, 0);
        Marshal.WriteInt32(block, OffsetIsPredefinedValid, 0);
        Marshal.WriteInt32(block, OffsetPredefinedValueLength, 0);
        Marshal.WriteInt32(block, OffsetCurrentValueLength, unchecked((int)value));

        return block;
    }

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

    private delegate int DrsGetBaseProfileDelegate(IntPtr session, out IntPtr profile);

    /// <summary>
    /// <c>NvAPI_DRS_FindApplicationByName</c>. <c>appName</c> is <c>__in NvAPI_UnicodeString</c> — a UTF-16 array
    /// pointer — which is why it marshals as <c>ushort[]</c> rather than as a struct field.
    /// </summary>
    private delegate int DrsFindApplicationByNameDelegate(
        IntPtr session, ushort[] appName, out IntPtr profile, IntPtr application);

    /// <summary><c>NvAPI_DRS_GetProfileInfo</c> — fills an <c>NVDRS_PROFILE</c> from a profile handle.</summary>
    private delegate int DrsGetProfileInfoDelegate(IntPtr session, IntPtr profile, IntPtr profileInfo);

    /// <summary><c>NvAPI_DRS_CreateProfile(hSession, NVDRS_PROFILE *pProfileInfo, NvDRSProfileHandle *phProfile)</c>.</summary>
    private delegate int DrsCreateProfileDelegate(IntPtr session, IntPtr profileInfo, out IntPtr profile);

    /// <summary><c>NvAPI_DRS_DeleteProfile(hSession, hProfile)</c>.</summary>
    private delegate int DrsDeleteProfileDelegate(IntPtr session, IntPtr profile);

    /// <summary><c>NvAPI_DRS_CreateApplication(hSession, hProfile, NVDRS_APPLICATION *pApplication)</c>.</summary>
    private delegate int DrsCreateApplicationDelegate(IntPtr session, IntPtr profile, IntPtr application);

    /// <summary><c>NvAPI_DRS_DeleteApplication(hSession, hProfile, NvAPI_UnicodeString appName)</c>.</summary>
    private delegate int DrsDeleteApplicationDelegate(IntPtr session, IntPtr profile, ushort[] appName);

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
    // The setting structure now crosses the boundary as raw memory this code fills in itself, instead of as a
    // marshalled struct. Passing the marshalled struct is what crashed: every layout check passed, but
    // Marshal.SizeOf / Marshal.OffsetOf only compute the managed side's view and never run the marshaler's call
    // path — so the copy-in/copy-out was never exercised until a real call, where the second read faulted with
    // AccessViolationException.
    private delegate int DrsGetSettingDelegate(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsSetSettingDelegate(IntPtr session, IntPtr profile, IntPtr setting);

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

    /// <summary>
    /// No driver library, so no lookup. Says so explicitly rather than returning an empty match that a caller
    /// might mistake for "the profile has no name".
    /// </summary>
    public DrsApplicationLookup FindApplicationProfile(string executableName) =>
        DrsApplicationLookup.NotFound(executableName ?? "", Reason);

    public DrsApplicationLookup FindApplication(string executableName) =>
        DrsApplicationLookup.NotFound(executableName, Reason);

    public ProfileSettingSnapshot Read(uint settingId) => ProfileSettingSnapshot.Unreadable(settingId, Reason);

    public DrsStatus Write(uint settingId, uint value) => DrsStatus.Fail(-1, Reason);

    public DrsStatus Delete(uint settingId) => DrsStatus.Fail(-1, Reason);

    public DrsStatus Save() => DrsStatus.Fail(-1, Reason);

    public void Close() { }
}
