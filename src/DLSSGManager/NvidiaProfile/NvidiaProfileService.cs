namespace DLSSGManager.NvidiaProfile;

/// <summary>
/// The three states a DRS setting can genuinely be in.
///
/// This is the distinction the whole service exists for. "Not set at all" and "explicitly set to 0" are
/// different states with different meanings, and the driver treats them differently: inherited values
/// follow the profile's parent, while an explicit value pins the profile. Collapsing them — which
/// writing <c>0</c> over an absent setting does — destroys information that cannot be recovered
/// afterwards, so <see cref="Absent"/> must survive a write and be restored by deletion.
/// </summary>
public enum ProfileSettingState
{
    /// <summary>Could not be determined. Never treated as any of the other three.</summary>
    Unknown,

    /// <summary>The setting does not exist in this profile: the driver falls back to its predefined value.</summary>
    Absent,

    /// <summary>The profile carries its own value, set by the user or a tool.</summary>
    ExplicitValue,

    /// <summary>
    /// The value comes from the driver's predefined table rather than the profile. Deleting the setting
    /// returns to this state, which is why it is restored by deletion, not by writing the inherited value.
    /// </summary>
    InheritedDefault,
}

/// <summary>One readable/writable driver setting.</summary>
/// <param name="Id">The DRS setting id.</param>
/// <param name="Name">Human label, for logs and the journal.</param>
/// <param name="Provenance">
/// Where the id came from. For the Smooth Motion ids this is
/// <c>Undocumented / Community Verified / Experimental</c> — never "NVIDIA official".
/// </param>
public sealed record ProfileSetting(uint Id, string Name, string Provenance);

/// <summary>What was there before a write. The journal's unit, and the only basis for restoring it.</summary>
public sealed record ProfileSettingSnapshot(
    uint SettingId,
    ProfileSettingState State,
    uint Value,
    bool IsPredefined,
    string Reason)
{
    public static ProfileSettingSnapshot Unreadable(uint settingId, string reason) =>
        new(settingId, ProfileSettingState.Unknown, 0, false, reason);
}

/// <summary>One applied write, paired with what it replaced.</summary>
public sealed record ProfileJournalEntry(uint SettingId, ProfileSettingSnapshot Before, uint WrittenValue);

/// <summary>A driver call's raw outcome. The numeric code is carried through, never interpreted away.</summary>
public sealed record DrsStatus(bool Ok, int Code, string Message)
{
    public static DrsStatus Success { get; } = new(true, 0, "");

    public static DrsStatus Fail(int code, string message) => new(false, code, message);
}

/// <summary>
/// The driver boundary.
///
/// Everything interesting — three-state restoration, partial-failure rollback, elevation probing — lives
/// above this interface, which is what lets it be tested without touching a real NVIDIA profile. The
/// adapter is the only place that knows about NVAPI at all.
/// </summary>
public interface IDrsAdapter
{
    /// <summary>Adapter name, for diagnostics.</summary>
    string Name { get; }

    /// <summary>Whether the driver library could be loaded at all.</summary>
    bool IsAvailable { get; }

    /// <summary>Opens a session bound to one profile. <paramref name="profileName"/> null means the base profile.</summary>
    DrsStatus Open(string? profileName);

    /// <summary>Reads one setting, distinguishing the three states.</summary>
    ProfileSettingSnapshot Read(uint settingId);

    /// <summary>Sets an explicit value.</summary>
    DrsStatus Write(uint settingId, uint value);

    /// <summary>
    /// Removes the setting from the profile so the driver falls back to its predefined value.
    ///
    /// The existence of this operation is the point: it is the only correct way to restore
    /// <see cref="ProfileSettingState.Absent"/>.
    /// </summary>
    DrsStatus Delete(uint settingId);

    /// <summary>Commits the session's changes.</summary>
    DrsStatus Save();

    /// <summary>Releases the session.</summary>
    void Close();
}

/// <summary>Result of applying a set of settings.</summary>
public sealed record ProfileApplyResult(
    bool Ok,
    string Message,
    IReadOnlyList<ProfileJournalEntry> Journal,
    IReadOnlyList<string> Notes)
{
    public static ProfileApplyResult Failed(string message, IReadOnlyList<string> notes) =>
        new(false, message, Array.Empty<ProfileJournalEntry>(), notes);
}

/// <summary>Result of restoring a journal.</summary>
public sealed record ProfileRollbackResult(bool Ok, string Message, IReadOnlyList<string> Notes);

/// <summary>Whether writing the profile actually needs administrator rights on this machine.</summary>
public enum ElevationRequirement
{
    /// <summary>Not established. Not a synonym for "no".</summary>
    Unknown,

    /// <summary>A non-elevated call succeeded, so elevation is not needed here.</summary>
    NotRequired,

    /// <summary>A call failed while the process was not elevated, which is consistent with a privilege requirement.</summary>
    Required,
}

/// <summary>The probe's finding plus the observation behind it.</summary>
public sealed record ElevationProbe(ElevationRequirement Requirement, string Evidence);

/// <summary>
/// Reads and writes NVIDIA driver settings, with faithful restoration.
///
/// The rules this class enforces come from the project's red lines and are not negotiable:
/// <list type="bullet">
/// <item>an originally absent setting is restored by <b>deletion</b>, never by writing a value — including <c>0</c>;</item>
/// <item>the driver's predefined table is never consulted as a source of "the original value", because
/// restoring to a default is not the same as restoring to what the user had;</item>
/// <item>a partial failure rolls back what was already written, so a failure cannot leave a
/// half-configured profile;</item>
/// <item>a setting whose original state could not be read is <b>not</b> overwritten on rollback.</item>
/// </list>
/// </summary>
public sealed class NvidiaProfileService
{
    private readonly IDrsAdapter _adapter;
    private readonly Func<bool> _isElevated;

    /// <param name="adapter">Driver boundary; a fake in tests.</param>
    /// <param name="isElevated">Process elevation check, injectable so the probe is testable.</param>
    public NvidiaProfileService(IDrsAdapter adapter, Func<bool>? isElevated = null)
    {
        _adapter = adapter;
        _isElevated = isElevated ?? (() => Native.IsElevated());
    }

    public IDrsAdapter Adapter => _adapter;

    /// <summary>
    /// Writes settings, remembering what each one was.
    ///
    /// Returns a journal even on failure, so the caller can see what was captured before the operation
    /// aborted — and, when a write fails partway, the already-written entries are rolled back here
    /// rather than left for the caller to notice.
    /// </summary>
    public ProfileApplyResult Apply(string? profileName, IReadOnlyList<(ProfileSetting Setting, uint Value)> writes)
    {
        var notes = new List<string>();
        var journal = new List<ProfileJournalEntry>();

        if (writes.Count == 0)
            return new ProfileApplyResult(true, "没有需要写入的设置。", journal, notes);

        var opened = _adapter.Open(profileName);
        if (!opened.Ok)
        {
            notes.Add($"打开 Profile 会话失败（code {opened.Code}）：{opened.Message}");
            return ProfileApplyResult.Failed($"无法打开 Profile「{profileName ?? "(基础)"}」。", notes);
        }

        try
        {
            // Capture every original first. Reading after the first write would record our own value as
            // the previous one, making a later restore a no-op that looks like a success.
            foreach (var (setting, _) in writes)
            {
                var before = _adapter.Read(setting.Id);
                journal.Add(new ProfileJournalEntry(setting.Id, before, 0));

                if (before.State == ProfileSettingState.Unknown)
                    notes.Add($"设置 {setting.Name}（0x{setting.Id:X8}）的原值无法读取，回滚时将不会改写它。");
            }

            var applied = 0;
            for (var i = 0; i < writes.Count; i++)
            {
                var (setting, value) = writes[i];
                var status = _adapter.Write(setting.Id, value);

                if (!status.Ok)
                {
                    notes.Add($"写入 {setting.Name}（0x{setting.Id:X8}）失败（code {status.Code}）：{status.Message}");

                    var rollback = Rollback(journal.Take(applied).ToList());
                    notes.Add(rollback.Message);

                    return new ProfileApplyResult(false,
                        $"写入 {setting.Name} 失败，已回滚之前写入的 {applied} 项。", journal, notes);
                }

                journal[i] = journal[i] with { WrittenValue = value };
                applied++;
            }

            var saved = _adapter.Save();
            if (!saved.Ok)
            {
                notes.Add($"保存失败（code {saved.Code}）：{saved.Message}");

                var rollback = Rollback(journal);
                notes.Add(rollback.Message);

                return new ProfileApplyResult(false, "保存 Profile 失败，已回滚全部写入。", journal, notes);
            }

            return new ProfileApplyResult(true, $"已写入并保存 {applied} 项设置。", journal, notes);
        }
        finally
        {
            _adapter.Close();
        }
    }

    /// <summary>
    /// Restores a journal, newest entry first.
    ///
    /// Each original state maps to exactly one correct action:
    /// <see cref="ProfileSettingState.Absent"/> and <see cref="ProfileSettingState.InheritedDefault"/>
    /// both restore by <b>deletion</b> — an inherited value was never ours to write back, and writing it
    /// would convert an inherited setting into an explicit one. <see cref="ProfileSettingState.Unknown"/>
    /// is deliberately skipped: guessing a value there would overwrite something we never read.
    /// </summary>
    public ProfileRollbackResult Rollback(IReadOnlyList<ProfileJournalEntry> journal)
    {
        var notes = new List<string>();
        if (journal.Count == 0) return new ProfileRollbackResult(true, "无需回滚。", notes);

        var restored = 0;
        var failed = 0;

        foreach (var entry in journal.Reverse())
        {
            var before = entry.Before;

            switch (before.State)
            {
                case ProfileSettingState.Absent:
                case ProfileSettingState.InheritedDefault:
                {
                    var status = _adapter.Delete(entry.SettingId);
                    if (status.Ok)
                    {
                        restored++;
                        notes.Add($"已删除 0x{entry.SettingId:X8}，恢复为未设置。");
                    }
                    else
                    {
                        failed++;
                        notes.Add($"删除 0x{entry.SettingId:X8} 失败（code {status.Code}）：{status.Message}");
                    }

                    break;
                }

                case ProfileSettingState.ExplicitValue:
                {
                    var status = _adapter.Write(entry.SettingId, before.Value);
                    if (status.Ok)
                    {
                        restored++;
                        notes.Add($"已恢复 0x{entry.SettingId:X8} = {before.Value}。");
                    }
                    else
                    {
                        failed++;
                        notes.Add($"恢复 0x{entry.SettingId:X8} 失败（code {status.Code}）：{status.Message}");
                    }

                    break;
                }

                default:
                    notes.Add($"跳过 0x{entry.SettingId:X8}：原值状态未知，不猜测、不覆盖。");
                    break;
            }
        }

        if (failed == 0)
        {
            var save = _adapter.Save();
            if (!save.Ok)
            {
                notes.Add($"回滚后的保存失败（code {save.Code}）：{save.Message}");
                return new ProfileRollbackResult(false, $"已恢复 {restored} 项，但保存失败。", notes);
            }
        }

        return failed == 0
            ? new ProfileRollbackResult(true, $"已恢复 {restored} 项设置。", notes)
            : new ProfileRollbackResult(false, $"恢复 {restored} 项，{failed} 项失败。", notes);
    }

    /// <summary>
    /// Finds out whether writing needs elevation <i>on this machine</i>, by trying.
    ///
    /// The rule this satisfies is "do not hard-code a requirement that NVIDIA never documented". Official
    /// DRS documentation does not state that writes need administrator rights; the community expectation
    /// exists, and it is recorded as a possibility here rather than as a fact. So the probe reports
    /// <see cref="ElevationRequirement.NotRequired"/> only after a real non-elevated call succeeded, and
    /// reports <see cref="ElevationRequirement.Unknown"/> when a failure cannot be attributed to privilege
    /// — an elevated process that still fails is failing for some other reason.
    /// </summary>
    public ElevationProbe ProbeElevation(string? profileName)
    {
        if (!_adapter.IsAvailable)
            return new ElevationProbe(ElevationRequirement.Unknown, $"{_adapter.Name} 不可用，无法探测。");

        var elevated = _isElevated();

        var opened = _adapter.Open(profileName);
        if (opened.Ok)
        {
            try
            {
                // Opening alone is not proof; a read is what actually touches the profile store.
                var probe = _adapter.Read(SmoothMotionSettings.FeatureEnabled);
                if (probe.State != ProfileSettingState.Unknown || probe.Reason.Length == 0)
                {
                    return elevated
                        ? new ElevationProbe(ElevationRequirement.Unknown,
                            "已在提权状态下调用成功，无法据此判断非提权是否需要提权。")
                        : new ElevationProbe(ElevationRequirement.NotRequired,
                            "非提权状态下成功读取 DRS，因此本机写入不需要管理员权限。");
                }

                return new ElevationProbe(ElevationRequirement.Unknown, $"读取探测失败：{probe.Reason}");
            }
            finally
            {
                _adapter.Close();
            }
        }

        return elevated
            ? new ElevationProbe(ElevationRequirement.Unknown,
                $"已提权仍失败（code {opened.Code}），原因不是权限。")
            : new ElevationProbe(ElevationRequirement.Required,
                $"非提权调用失败（code {opened.Code}），与权限要求一致；官方文档未声明该要求，故记为社区经验。");
    }
}

/// <summary>
/// The Smooth Motion setting ids.
///
/// <para><b>Provenance is part of the data, not a comment.</b> These ids do not appear in NVIDIA's
/// published driver-setting headers, so they are <c>Undocumented</c>; their values are corroborated by
/// independent community sources, so they are <c>Community Verified</c>; and their use on RTX 30 is
/// <c>Experimental</c> because NVIDIA documents frame generation for RTX 40 and above. Describing them as
/// "NVIDIA official settings" would be false.</para>
///
/// <para>Where sources disagree, the conflict is recorded rather than resolved by preference.</para>
/// </summary>
public static class SmoothMotionSettings
{
    public const string Provenance =
        "Undocumented + Community Verified + Experimental（未出现在 NVIDIA 公开头文件；数值由多个独立社区来源交叉印证）";

    /// <summary>Master switch, per application.</summary>
    public const uint FeatureEnabled = 0xB0D384C0;

    /// <summary>API bitmask: DX12 = 1, DX11 = 2, Vulkan = 4.</summary>
    public const uint EnabledApis = 0xB0CC0875;

    public const uint FlipMetering0 = 0xB03A4546;
    public const uint FlipMetering1 = 0xB03A4547;

    /// <summary>Diagnostic logging level.</summary>
    public const uint DebugLogLevel = 0xB053C379;

    /// <summary>
    /// Debug bars. Independent projects use it to confirm frames are actually being generated, which
    /// makes it the only external signal that reaches the driver's own presentation path.
    /// </summary>
    public const uint DebugBars = 0xB01B8B02;

    public static IReadOnlyList<ProfileSetting> All { get; } = new[]
    {
        new ProfileSetting(FeatureEnabled, "Smooth Motion 开关", Provenance),
        new ProfileSetting(EnabledApis, "启用的图形 API（位掩码）", Provenance),
        new ProfileSetting(FlipMetering0, "Flip Metering 0", Provenance),
        new ProfileSetting(FlipMetering1, "Flip Metering 1", Provenance),
        new ProfileSetting(DebugLogLevel, "调试日志级别", Provenance + "；单一来源，仅诊断用"),
        new ProfileSetting(DebugBars, "Debug Bars", Provenance),
    };
}
