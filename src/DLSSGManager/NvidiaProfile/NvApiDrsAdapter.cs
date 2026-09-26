namespace DLSSGManager.NvidiaProfile;

/// <summary>
/// The real driver boundary — <b>not implemented yet, deliberately and visibly</b>.
///
/// <para>Everything above it is finished and tested: the three-state rules, the partial-failure rollback,
/// the journal, the elevation probe. What is missing is the P/Invoke layer, and it is missing for a
/// concrete reason rather than an oversight: driving DRS correctly needs the official
/// <c>nvapi_interface.h</c> for two things this project does not currently have — the function ids beyond
/// the five already verified, and the exact layout of <c>NVDRS_SETTING_V1</c>. Writing either from
/// memory would produce a struct marshalling bug that fails in the driver, on a user's machine, in a code
/// path whose whole purpose is to be able to undo itself.</para>
///
/// <para><b>The adapter therefore fails closed.</b> Rather than writing what it cannot delete, it refuses
/// to write at all: a session that can set a value but not remove it is exactly the situation that turns
/// a cosmetic change into a permanent one. <see cref="CanWrite"/> requires <see cref="CanDelete"/>, and
/// <see cref="CanDelete"/> is false until <c>NvAPI_DRS_DeleteProfileSetting</c> is confirmed.</para>
///
/// <para>Stated plainly: <b>no driver call has been made from this class, and none has been verified on
/// real hardware.</b> It reports unavailability instead of pretending.</para>
/// </summary>
public sealed class NvApiDrsAdapter : IDrsAdapter
{
    private const string NotConfirmed =
        "尚未确认官方 nvapi_interface.h 中的函数 ID 与 NVDRS_SETTING_V1 结构体布局，未对驱动发起任何调用。";

    public string Name => "nvapi64.dll";

    /// <summary>False until the library is actually loaded and its entry points verified.</summary>
    public bool IsAvailable => false;

    /// <summary>
    /// Whether this adapter may delete a setting. False until the delete entry point is confirmed — and
    /// it gates writing, because an undo that does not exist is not an undo.
    /// </summary>
    public bool CanDelete => false;

    /// <summary>Whether this adapter may write a setting. Requires <see cref="CanDelete"/>.</summary>
    public bool CanWrite => CanDelete;

    public DrsStatus Open(string? profileName) =>
        DrsStatus.Fail(-1, $"未能打开 DRS 会话：{NotConfirmed}");

    public ProfileSettingSnapshot Read(uint settingId) =>
        ProfileSettingSnapshot.Unreadable(settingId, NotConfirmed);

    public DrsStatus Write(uint settingId, uint value) =>
        DrsStatus.Fail(-1, $"拒绝写入：{NotConfirmed}；且当前不具备删除能力，写入后无法回滚。");

    public DrsStatus Delete(uint settingId) =>
        DrsStatus.Fail(-1, $"未能删除设置：{NotConfirmed}");

    public DrsStatus Save() =>
        DrsStatus.Fail(-1, $"未能保存：{NotConfirmed}");

    public void Close()
    {
        // No session exists, so there is nothing to release.
    }
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

    public DrsStatus Open(string? profileName) => DrsStatus.Fail(-1, Reason);

    public ProfileSettingSnapshot Read(uint settingId) => ProfileSettingSnapshot.Unreadable(settingId, Reason);

    public DrsStatus Write(uint settingId, uint value) => DrsStatus.Fail(-1, Reason);

    public DrsStatus Delete(uint settingId) => DrsStatus.Fail(-1, Reason);

    public DrsStatus Save() => DrsStatus.Fail(-1, Reason);

    public void Close() { }
}
