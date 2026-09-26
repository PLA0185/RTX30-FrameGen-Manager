using DLSSGManager.GameDetection;

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

/// <summary>
/// How well a setting's value semantics are established.
///
/// Exists so that "somebody said so once" cannot be written to a user's driver as if it were settled.
/// The ids themselves are undocumented; their <i>values</i> vary even more in how well they are
/// corroborated, and the weaker ones must not be guessed at.
/// </summary>
public enum ValueConfidence
{
    /// <summary>No usable source. Never written.</summary>
    Unknown,

    /// <summary>A single source. Recorded and displayed, but not written automatically.</summary>
    SingleSource,

    /// <summary>Corroborated by multiple independent sources.</summary>
    CommunityVerified,
}

/// <summary>One readable/writable driver setting.</summary>
/// <param name="Id">The DRS setting id.</param>
/// <param name="Name">Human label, for logs and the journal.</param>
/// <param name="Provenance">
/// Where the id came from. For the Smooth Motion ids this is
/// <c>Undocumented / Community Verified / Experimental</c> — never "NVIDIA official".
/// </param>
/// <param name="Confidence">How well the values below are corroborated.</param>
/// <param name="OnValue">The value that means "on", when the setting is a switch.</param>
/// <param name="OffValue">The value that means "off".</param>
/// <param name="Writable">
/// False for settings whose values are not established well enough to write. Such a setting stays
/// visible (so it can be shown and reasoned about) but is never part of a write.
/// </param>
public sealed record ProfileSetting(
    uint Id,
    string Name,
    string Provenance,
    ValueConfidence Confidence = ValueConfidence.CommunityVerified,
    uint OnValue = 1,
    uint OffValue = 0,
    bool Writable = true);

/// <summary>
/// One requested write: which setting, to what value, whether it is required, and why.
///
/// Replaces the previous "everything is written as 1" shape. A required setting whose original state
/// cannot be read aborts the whole operation; an optional one is skipped. Both are decided before any
/// write happens.
/// </summary>
public sealed record ProfileSettingWrite(ProfileSetting Setting, uint Value, bool Required, string Reason)
{
    public static ProfileSettingWrite Required_(ProfileSetting setting, uint value, string reason) =>
        new(setting, value, true, reason);

    public static ProfileSettingWrite Optional(ProfileSetting setting, uint value, string reason) =>
        new(setting, value, false, reason);
}

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

    /// <summary>True when the read actually established something.</summary>
    public bool WasRead => State != ProfileSettingState.Unknown;
}

/// <summary>How far a single journal entry has been undone.</summary>
public enum RollbackState
{
    /// <summary>Not attempted, or attempted and left for a retry.</summary>
    Pending,

    /// <summary>Put back the way it was found.</summary>
    Restored,

    /// <summary>
    /// The attempt failed. Kept — not dropped — because the retry has to know which entries still need work, and
    /// because a silently discarded failure is indistinguishable from a success.
    /// </summary>
    Failed,
}

/// <summary>One applied write, paired with what it replaced.</summary>
public sealed record ProfileJournalEntry(
    uint SettingId,
    string Name,
    ProfileSettingSnapshot Before,
    uint WrittenValue)
{
    /// <summary>
    /// Where this entry stands in the rollback.
    ///
    /// <para>Per entry rather than per journal, because a rollback can half-succeed: one setting may be restored
    /// while another fails. A single flag for the whole journal cannot express that, so it either re-runs
    /// everything (undoing what was already put back) or runs nothing (leaving the profile half-configured).</para>
    /// </summary>
    public RollbackState State { get; set; } = RollbackState.Pending;
}

/// <summary>
/// The record of one apply, and the thing a rollback is performed from.
///
/// Carries the profile name because a rollback has to <i>open a session itself</i>: by the time the
/// caller decides to undo, the apply has already closed its session, so a rollback that assumed an open
/// session would operate on nothing.
///
/// <see cref="IsRolledBack"/> makes rollback single-shot. Undoing twice would delete or rewrite settings
/// that the first pass already restored, which is exactly how a "restore" turns into a fresh change.
/// </summary>
public sealed class ProfileJournal
{
    public ProfileJournal(string? profileName) => ProfileName = profileName;

    /// <summary>Which profile these entries came from. Null means the base profile.</summary>
    public string? ProfileName { get; }

    /// <summary>
    /// 这次运行实际绑定的可执行文件名，以及 Profile / 应用绑定是不是本次运行创建的。
    ///
    /// <para>回滚必须知道它究竟动过什么。只记 <see cref="ProfileName"/> 不够：同一个 Profile 名下可能有多个
    /// 应用绑定，而「本次是不是创建了它」决定了回滚应当删掉它、还是只恢复设置 —— <b>删掉用户原本就有的
    /// Profile 或绑定，比留下一点残留严重得多</b>，而只看名字无法区分这两种情况。</para>
    /// </summary>
    public string? ApplicationExe { get; init; }

    /// <summary>本次运行是否创建了这个 Profile（而非使用已有的）。只有为真时回滚才允许删除它。</summary>
    public bool WasProfileCreated { get; init; }

    /// <summary>本次运行是否创建了这个应用绑定。只有为真时回滚才允许解绑。</summary>
    public bool WasApplicationCreated { get; init; }

    public List<ProfileJournalEntry> Entries { get; } = new();

    /// <summary>Set once a rollback has actually run against this journal.</summary>
    public bool IsRolledBack { get; private set; }

    public int Count => Entries.Count;

    /// <summary>Marks the journal as consumed. Internal: only the service may do this.</summary>
    internal void MarkRolledBack() => IsRolledBack = true;
}

/// <summary>A driver call's raw outcome. The numeric code is carried through, never interpreted away.</summary>
public sealed record DrsStatus(bool Ok, int Code, string Message)
{
    public static DrsStatus Success { get; } = new(true, 0, "");

    public static DrsStatus Fail(int code, string message) => new(false, code, message);
}

/// <summary>
/// The driver boundary.
///
/// Everything interesting — three-state restoration, pre-flight validation, partial-failure rollback,
/// elevation probing — lives above this interface, which is what lets it be tested without touching a
/// real NVIDIA profile. The adapter is the only place that knows about NVAPI at all.
/// </summary>
/// <summary>
/// What the driver reports about an executable, and the profile that owns it.
///
/// <para><b>Both names are carried.</b> The application name is what the driver matched by, and the profile
/// name is what a later session must be opened with — they are not the same string, and collapsing them is how
/// the wrong profile ends up being configured.</para>
/// </summary>
public sealed record DrsApplicationLookup(
    bool Found,
    string ApplicationName,
    string ProfileName,
    string Message)
{
    /// <summary>Nothing matched. Whether that means "create it" or "decline" is the caller's decision.</summary>
    public static DrsApplicationLookup NotFound(string applicationName, string message) =>
        new(false, applicationName, "", message);

    public static DrsApplicationLookup Matched(string applicationName, string profileName, string message) =>
        new(true, applicationName, profileName, message);
}

public interface IDrsAdapter
{
    /// <summary>Adapter name, for diagnostics.</summary>
    string Name { get; }

    /// <summary>Whether the driver library could be loaded at all.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Whether this adapter can read and delete. Reported separately from <see cref="IsAvailable"/> so a
    /// partially implemented adapter cannot accidentally be treated as fully usable.
    /// </summary>
    bool CanRead { get; }

    /// <summary>Whether deletion is implemented. Writing requires it.</summary>
    bool CanDelete { get; }

    /// <summary>Whether committing is implemented. Writing requires it.</summary>
    bool CanSave { get; }

    /// <summary>Whether writing is permitted at all: requires read, delete and save.</summary>
    bool CanWrite => CanRead && CanDelete && CanSave;

    /// <summary>Opens a session bound to one profile. <paramref name="profileName"/> null means the base profile.</summary>
    DrsStatus Open(string? profileName);

    /// <summary>
    /// Finds the driver profile that owns an executable, starting from the executable's own file name.
    ///
    /// <para><b>Why this exists:</b> a game's display name is not its NVIDIA Profile name, and the two differ
    /// often enough that using one for the other configures the wrong profile — or, more usually, finds nothing
    /// and writes nowhere while reporting success. The renderer executable is the one thing this program
    /// actually knows before it deploys, so that is where the lookup starts.</para>
    ///
    /// <para>An adapter that cannot answer must say so rather than guessing; the caller may then create the
    /// application, or decline.</para>
    /// </summary>
    DrsApplicationLookup FindApplication(string executableName);

    /// <summary>
    /// The same lookup, but the implementation owns the whole session lifetime.
    ///
    /// <para>Callers must not have to hold a session open for this to work. That requirement is exactly what made
    /// the UI path fail: it never opened one, so every lookup returned "没有已打开的 DRS 会话" — and no test caught
    /// it, because the double never modelled the requirement.</para>
    /// </summary>
    DrsApplicationLookup FindApplicationProfile(string executableName);

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

    /// <summary>
    /// 删除一个 Profile（以及可选的一个应用绑定），自己管会话生命周期。
    ///
    /// <para>**只供回滚「本次运行自己创建的东西」使用。** 调用方必须先确认该 Profile 由本次运行创建
    /// （<c>ProfileJournal.WasProfileCreated</c>）—— <b>删掉用户原有的 Profile 比留下残留严重得多</b>，
    /// 而名字本身无法区分这两种情况。不用它做「清理」，只用它撤销自己造成的变化。</para>
    /// </summary>
    DrsStatus DeleteProfileByName(string profileName, string? executableName = null);

    /// <summary>Releases the session.</summary>
    void Close();
}

/// <summary>Result of applying a set of settings.</summary>
/// <param name="ReadBackConfirmed">
/// True only when every written setting was read back and matched, <b>while the session was still open</b>.
///
/// <para>A successful save says the driver accepted the request; it does not say the stored value is the one
/// that was asked for. The read-back happens inside <see cref="NvidiaProfileService.Apply"/> rather than in the
/// caller because the session is closed on the way out — a read from outside would be reading nothing at all,
/// and reporting that as "not confirmed" would be indistinguishable from a real mismatch.</para>
/// </param>
public sealed record ProfileApplyResult(
    bool Ok,
    string Message,
    ProfileJournal Journal,
    IReadOnlyList<string> Notes,
    bool ReadBackConfirmed = false)
{
    public static ProfileApplyResult Failed(string message, ProfileJournal journal, IReadOnlyList<string> notes) =>
        new(false, message, journal, notes);
}

/// <summary>Result of restoring a journal.</summary>
/// <param name="Skipped">True when nothing ran because the journal had already been rolled back.</param>
public sealed record ProfileRollbackResult(bool Ok, string Message, IReadOnlyList<string> Notes, bool Skipped = false);

/// <summary>Whether writing the profile actually needs administrator rights on this machine.</summary>
public enum ElevationRequirement
{
    /// <summary>Not established. Not a synonym for "no".</summary>
    Unknown,

    /// <summary>A non-elevated write actually succeeded, so elevation is not needed here.</summary>
    NotRequired,

    /// <summary>
    /// A non-elevated <b>read</b> succeeded — and nothing more.
    ///
    /// <para>Reading and writing are different privileges on this API, and this project's rule is that a
    /// successful read only proves a successful read. Concluding "writes do not need elevation" from one is the
    /// exact inference this state exists to prevent: <see cref="NotRequired"/> is reserved for a probe that
    /// actually wrote, committed and read the value back.</para>
    /// </summary>
    ReadAvailable,

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
/// <item>a required setting whose original state cannot be read <b>aborts the whole apply before a single
/// write</b>, because writing something we cannot undo is worse than not writing at all;</item>
/// <item>an optional such setting is skipped and reported, never guessed at;</item>
/// <item>a partial failure rolls back what was already written, so a failure cannot leave a
/// half-configured profile;</item>
/// <item>rollback opens its own session and runs at most once per journal.</item>
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
    /// <para><b>Everything is decided before anything is written.</b> The originals are read first, then
    /// the write set is validated: a required setting whose original could not be read aborts the call
    /// with zero writes, and an unreadable optional setting is dropped from the set. Only then does the
    /// first write happen — so there is no window in which something is changed that cannot be undone.</para>
    /// </summary>
    /// <summary>
    /// Locates a profile by the executable the driver knows it by — the renderer EXE, not the game's display name.
    ///
    /// <para>Passed through to the adapter rather than reimplemented: the adapter owns the driver interaction, and
    /// there is exactly one correct way to ask.</para>
    /// </summary>
    public DrsApplicationLookup FindApplication(string executableName) =>
        _adapter.FindApplication(executableName);

    /// <summary>
    /// Locates a profile by the executable the driver knows it by, letting the adapter own the session.
    ///
    /// <para>This is the entry point callers should use: it does not depend on some other part of the program
    /// having opened a session first.</para>
    /// </summary>
    public DrsApplicationLookup FindApplicationProfile(string executableName) =>
        _adapter.FindApplicationProfile(executableName);

    /// <summary>
    /// 删除一个 Profile 及其绑定。**调用方必须先确认它由本次运行创建** —— 见接口上的说明。
    /// </summary>
    public DrsStatus DeleteProfileByName(string profileName, string? executableName = null) =>
        _adapter.DeleteProfileByName(profileName, executableName);

    public ProfileApplyResult Apply(string? profileName, IReadOnlyList<ProfileSettingWrite> writes)
    {
        var notes = new List<string>();
        var journal = new ProfileJournal(profileName);

        if (writes.Count == 0)
            return new ProfileApplyResult(true, "没有需要写入的设置。", journal, notes);

        if (!_adapter.CanWrite)
            return ProfileApplyResult.Failed(
                $"{_adapter.Name} 不具备完整写入能力（需要 read + delete + save），已拒绝写入。", journal, notes);

        var opened = _adapter.Open(profileName);
        if (!opened.Ok)
        {
            notes.Add($"打开 Profile 会话失败（code {opened.Code}）：{opened.Message}");
            return ProfileApplyResult.Failed($"无法打开 Profile「{profileName ?? "(基础)"}」。", journal, notes);
        }

        try
        {
            // ── 1. Read every original first. Reading after the first write would record our own value
            //       as the previous one, making a later restore a no-op that looks like a success.
            var captured = new List<(ProfileSettingWrite Write, ProfileSettingSnapshot Before)>();
            foreach (var write in writes)
            {
                var before = _adapter.Read(write.Setting.Id);
                captured.Add((write, before));
            }

            // ── 2. Required + unreadable ⇒ abort with zero writes.
            var blocking = captured
                .Where(c => c.Write.Required && !c.Before.WasRead)
                .ToList();

            if (blocking.Count > 0)
            {
                foreach (var (write, before) in blocking)
                {
                    notes.Add($"必填设置「{write.Setting.Name}」（0x{write.Setting.Id:X8}）的原值无法读取：{before.Reason}");
                }

                notes.Add("已在写入前整体中止：没有任何设置被修改。");

                return ProfileApplyResult.Failed(
                    $"必填设置的原值无法读取（{blocking.Count} 项），未写入任何设置。", journal, notes);
            }

            // ── 3. Optional + unreadable ⇒ drop from the set.
            var plan = new List<(ProfileSettingWrite Write, ProfileSettingSnapshot Before)>();
            foreach (var (write, before) in captured)
            {
                if (!before.WasRead)
                {
                    notes.Add($"可选设置「{write.Setting.Name}」（0x{write.Setting.Id:X8}）的原值无法读取，已跳过。" +
                              $"Skipped: 0x{write.Setting.Id:X8}");
                    continue;
                }

                plan.Add((write, before));
            }

            if (plan.Count == 0)
                return new ProfileApplyResult(true, "全部设置都因原值不可读而跳过，未修改任何内容。", journal, notes);

            // ── 4. Write.
            foreach (var (write, before) in plan)
            {
                var status = _adapter.Write(write.Setting.Id, write.Value);

                if (!status.Ok)
                {
                    notes.Add($"写入「{write.Setting.Name}」（0x{write.Setting.Id:X8}）失败（code {status.Code}）：{status.Message}");

                    var rollback = Rollback(journal);
                    notes.Add(rollback.Message);

                    return ProfileApplyResult.Failed(
                        $"写入「{write.Setting.Name}」失败，已回滚之前写入的 {journal.Count} 项。", journal, notes);
                }

                journal.Entries.Add(new ProfileJournalEntry(write.Setting.Id, write.Setting.Name, before, write.Value));
            }

            // ── 5. Commit.
            var saved = _adapter.Save();
            if (!saved.Ok)
            {
                notes.Add($"保存失败（code {saved.Code}）：{saved.Message}");

                var rollback = Rollback(journal);
                notes.Add(rollback.Message);

                return ProfileApplyResult.Failed("保存 Profile 失败，已回滚全部写入。", journal, notes);
            }

            // ── 6. Read back, while the session is still open (see ReadBackConfirmed).
            var readBackConfirmed = true;

            foreach (var (write, _) in plan)
            {
                var after = _adapter.Read(write.Setting.Id);

                if (after.State != ProfileSettingState.ExplicitValue || after.Value != write.Value)
                {
                    readBackConfirmed = false;
                    notes.Add($"读回「{write.Setting.Name}」（0x{write.Setting.Id:X8}）与写入值不一致：" +
                              $"期望 {write.Value}，实际 {after.State} / {after.Value}。");
                }
            }

            return new ProfileApplyResult(true, $"已写入并保存 {journal.Count} 项设置。", journal, notes,
                ReadBackConfirmed: readBackConfirmed);
        }
        finally
        {
            _adapter.Close();
        }
    }

    /// <summary>
    /// Restores a journal, newest entry first, in a session of its own.
    ///
    /// <para><b>The session is the point.</b> A rollback happens after the apply has closed its session —
    /// possibly much later, possibly from another part of the program. Assuming an open session would
    /// mean silently operating on nothing, so this opens one from the journal's profile name, restores,
    /// saves, and closes.</para>
    ///
    /// <para><b>Single-shot.</b> A journal that has already been rolled back returns immediately. Undoing
    /// twice would delete or rewrite settings the first pass already restored — a "restore" that quietly
    /// becomes a fresh change.</para>
    ///
    /// <para>Each original state maps to exactly one correct action:
    /// <see cref="ProfileSettingState.Absent"/> and <see cref="ProfileSettingState.InheritedDefault"/>
    /// both restore by <b>deletion</b> — an inherited value was never ours to write back, and writing it
    /// would convert an inherited setting into an explicit one. <see cref="ProfileSettingState.Unknown"/>
    /// is deliberately skipped: guessing a value there would overwrite something we never read.</para>
    /// </summary>
    public ProfileRollbackResult Rollback(ProfileJournal journal)
    {
        var notes = new List<string>();
        if (journal.Count == 0) return new ProfileRollbackResult(true, "无需回滚。", notes);

        if (journal.IsRolledBack)
            return new ProfileRollbackResult(true,
                $"该 journal（Profile「{journal.ProfileName ?? "(基础)"}」）已回滚过，已跳过以防重复回滚。",
                notes, Skipped: true);

        var opened = _adapter.Open(journal.ProfileName);
        if (!opened.Ok)
        {
            notes.Add($"为回滚打开 Profile 会话失败（code {opened.Code}）：{opened.Message}");
            return new ProfileRollbackResult(false, "无法为回滚打开 Profile 会话，未恢复任何设置。", notes);
        }

        var restored = 0;
        var failed = 0;
        var skipped = 0;

        try
        {
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                // A retry exists precisely because a pass can half-succeed, so an entry already put back must not be
                // touched again — undoing it twice is not a restore, it is a new change.
                if (entry.State == RollbackState.Restored) continue;

                switch (entry.Before.State)
                {
                    case ProfileSettingState.Absent:
                    case ProfileSettingState.InheritedDefault:
                    {
                        var status = _adapter.Delete(entry.SettingId);
                        if (status.Ok)
                        {
                            restored++;
                            entry.State = RollbackState.Restored;
                            notes.Add($"已删除 0x{entry.SettingId:X8}（{entry.Name}），恢复为未设置。");
                        }
                        else
                        {
                            failed++;
                            entry.State = RollbackState.Failed;
                            notes.Add($"删除 0x{entry.SettingId:X8} 失败（code {status.Code}）：{status.Message}");
                        }

                        break;
                    }

                    case ProfileSettingState.ExplicitValue:
                    {
                        var status = _adapter.Write(entry.SettingId, entry.Before.Value);
                        if (status.Ok)
                        {
                            restored++;
                            entry.State = RollbackState.Restored;
                            notes.Add($"已恢复 0x{entry.SettingId:X8}（{entry.Name}）= {entry.Before.Value}。");
                        }
                        else
                        {
                            failed++;
                            entry.State = RollbackState.Failed;
                            notes.Add($"恢复 0x{entry.SettingId:X8} 失败（code {status.Code}）：{status.Message}");
                        }

                        break;
                    }

                    default:
                        skipped++;
                        notes.Add($"跳过 0x{entry.SettingId:X8}（{entry.Name}）：原值状态未知，不猜测、不覆盖。");
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
        }
        finally
        {
            _adapter.Close();

            // Single-shot only when it actually finished. A partially failed rollback stays open to a retry — the
            // entries that came back are marked Restored and will be skipped, so the retry touches only what is
            // still wrong. Marking it consumed regardless is what made a half-undone profile unrecoverable.
            if (failed == 0 && skipped == 0) journal.MarkRolledBack();
        }

        // 恢复设置之后，还要把本次运行**自己创建**的东西拆掉。只恢复设置会留下一个空的 RTX30FGM-* Profile
        // 挂在用户机器上 —— 那不是回滚，那是残留。
        //
        // 两个标志是唯一的依据：只有确认「这是我们建的」才允许删除。**用户原有的 Profile 绝不经过这里** ——
        // 删掉它比留下残留严重得多，而名字本身区分不了这两种情况。
        //
        // 位置在 finally 之后：DeleteProfileByName 自开自合会话，会打断外层那个。
        var profileRemoved = false;

        if (failed == 0 && journal.WasProfileCreated)
        {
            var removal = _adapter.DeleteProfileByName(journal.ProfileName ?? "", journal.ApplicationExe);

            if (removal.Ok)
            {
                profileRemoved = true;
                notes.Add($"已删除本次运行创建的 Profile「{journal.ProfileName}」。");
            }
            else
            {
                // 删不掉就是没回滚干净：留一个空 Profile 会一直出现在用户的驱动面板里。
                failed++;
                notes.Add($"删除本次运行创建的 Profile 失败（code {removal.Code}）：{removal.Message}");
            }
        }

        var summary = $"已恢复 {restored} 项";
        if (profileRemoved) summary += "，并已删除本次创建的 Profile";
        if (skipped > 0) summary += $"，跳过 {skipped} 项（原值未知）";
        if (failed > 0) summary += $"，{failed} 项失败";

        return new ProfileRollbackResult(failed == 0, summary + "。", notes);
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
                // A successful <i>read</i> is what proves the session works. Any of Absent / ExplicitValue /
                // InheritedDefault means the read established something; only Unknown means it did not.
                var probe = _adapter.Read(SmoothMotionSettings.FeatureEnabled);

                if (probe.WasRead)
                {
                    return elevated
                        ? new ElevationProbe(ElevationRequirement.Unknown,
                            "已在提权状态下调用成功，无法据此判断非提权是否需要提权。")
                        : new ElevationProbe(ElevationRequirement.ReadAvailable,
                            "非提权状态下成功读取 DRS —— 这只证明读取可用，不证明写入也不需要提权。");
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

    /// <summary>
    /// Establishes whether a non-elevated <b>write</b> works, by performing one and reading it back.
    ///
    /// <para><b>This writes to the driver profile.</b> It is deliberately absent from the default path: it saves
    /// the original value, writes a different one, commits, reads it back, and restores what was there — and it is
    /// reachable only from an explicit, user-initiated probe (the <c>--nvapi-smoke</c> mode), never from a normal
    /// configure run. A probe that silently modified someone's profile would be worse than not knowing.</para>
    ///
    /// <para>It answers <see cref="ElevationRequirement.NotRequired"/> only when the <i>whole</i> sequence
    /// succeeded without elevation. Anything less leaves the question open, which is the honest answer.</para>
    /// </summary>
    public ElevationProbe ProbeWriteElevation(string? profileName)
    {
        if (!_adapter.IsAvailable)
            return new ElevationProbe(ElevationRequirement.Unknown, $"{_adapter.Name} 不可用，无法探测。");

        if (!_adapter.CanWrite)
            return new ElevationProbe(ElevationRequirement.Unknown,
                "适配器不具备写入能力（需读、删、保存三者齐备），无法探测写入权限。");

        var elevated = _isElevated();

        var opened = _adapter.Open(profileName);
        if (!opened.Ok)
        {
            return elevated
                ? new ElevationProbe(ElevationRequirement.Unknown,
                    $"已提权仍失败（code {opened.Code}），原因不是权限。")
                : new ElevationProbe(ElevationRequirement.Required,
                    $"非提权打开会话失败（code {opened.Code}），与权限要求一致。");
        }

        try
        {
            var before = _adapter.Read(SmoothMotionSettings.FeatureEnabled);

            if (!before.WasRead)
                return new ElevationProbe(ElevationRequirement.Unknown,
                    $"无法读取原值，不能做可恢复的写入探测：{before.Reason}");

            // The probe value differs from whatever is there, so a matching read-back cannot be a coincidence of
            // writing back the same number.
            var probeValue = before.State == ProfileSettingState.ExplicitValue && before.Value == 1u ? 0u : 1u;

            var written = _adapter.Write(SmoothMotionSettings.FeatureEnabled, probeValue);
            if (!written.Ok)
            {
                return elevated
                    ? new ElevationProbe(ElevationRequirement.Unknown,
                        $"已提权仍无法写入（code {written.Code}），原因不是权限。")
                    : new ElevationProbe(ElevationRequirement.Required,
                        $"非提权写入失败（code {written.Code}），与权限要求一致；官方文档未声明该要求，故记为社区经验。");
            }

            var saved = _adapter.Save();
            if (!saved.Ok)
            {
                RestoreProbeValue(before);
                return new ElevationProbe(ElevationRequirement.Unknown,
                    $"写入成功但保存失败（code {saved.Code}），无法判定写入权限；已尝试恢复原值。");
            }

            var after = _adapter.Read(SmoothMotionSettings.FeatureEnabled);
            RestoreProbeValue(before);

            if (!after.WasRead || after.State != ProfileSettingState.ExplicitValue || after.Value != probeValue)
                return new ElevationProbe(ElevationRequirement.Unknown,
                    "写入与读回不一致，无法判定写入权限；已尝试恢复原值。");

            return elevated
                ? new ElevationProbe(ElevationRequirement.Unknown,
                    "已在提权状态下写入成功，无法据此判断非提权是否需要提权。")
                : new ElevationProbe(ElevationRequirement.NotRequired,
                    "非提权状态下完成「写入 → 保存 → 读回一致」全序列，本机写入确实不需要管理员权限。");
        }
        finally
        {
            _adapter.Close();
        }
    }

    /// <summary>
    /// Puts the probed setting back the way it was found.
    ///
    /// <para>Best effort on purpose. A failure here is not hidden — it is expressed by the probe's answer
    /// remaining "unknown", rather than by claiming the profile was restored.</para>
    /// </summary>
    private void RestoreProbeValue(ProfileSettingSnapshot before)
    {
        try
        {
            if (before.State == ProfileSettingState.Absent)
                _adapter.Delete(SmoothMotionSettings.FeatureEnabled);
            else if (before.State == ProfileSettingState.ExplicitValue)
                _adapter.Write(SmoothMotionSettings.FeatureEnabled, before.Value);

            _adapter.Save();
        }
        catch
        {
            // Swallowed deliberately: the caller's answer is already "unknown", which is what an unclean probe
            // actually establishes.
        }
    }
}

/// <summary>
/// The Smooth Motion setting ids and their value semantics.
///
/// <para><b>Provenance is part of the data, not a comment.</b> These ids do not appear in NVIDIA's
/// published driver-setting headers, so they are <c>Undocumented</c>; their values are corroborated by
/// independent community sources, so they are <c>Community Verified</c>; and their use on RTX 30 is
/// <c>Experimental</c> because NVIDIA documents frame generation for RTX 40 and above. Describing them as
/// "NVIDIA official settings" would be false.</para>
///
/// <para><b>Values that are not established are not written.</b> A setting whose values rest on a single
/// source is marked <see cref="ValueConfidence.SingleSource"/> and <c>Writable = false</c>: it stays
/// visible and reviewable, but it is never guessed into a user's driver profile.</para>
/// </summary>
public static class SmoothMotionSettings
{
    public const string Provenance =
        "Undocumented + Community Verified + Experimental（未出现在 NVIDIA 公开头文件；数值由多个独立社区来源交叉印证）";

    /// <summary>Master switch, per application. 4 sources agree: Off=0 / On=1.</summary>
    public const uint FeatureEnabled = 0xB0D384C0;

    /// <summary>API bitmask, 4 sources agree on the mask semantics.</summary>
    public const uint EnabledApis = 0xB0CC0875;

    public const uint FlipMetering0 = 0xB03A4546;
    public const uint FlipMetering1 = 0xB03A4547;

    /// <summary>Diagnostic logging level. Single source — recorded, never written.</summary>
    public const uint DebugLogLevel = 0xB053C379;

    /// <summary>
    /// Debug bars. Independent projects use it to confirm frames are actually being generated, which
    /// makes it the only external signal that reaches the driver's own presentation path.
    /// </summary>
    public const uint DebugBars = 0xB01B8B02;

    // ── API bitmask (4 sources agree: DX12=1, DX11=2, Vulkan=4) ──────────────────────────────

    public const uint ApiDx12Bit = 1;
    public const uint ApiDx11Bit = 2;
    public const uint ApiVulkanBit = 4;

    /// <summary>
    /// The bit for one graphics API, or <b>null for <see cref="GraphicsApi.Unknown"/></b>.
    ///
    /// Null is not "0": an unknown API means we do not know which bit to set, and writing the wrong one
    /// (or an empty mask) would configure the driver for an API the game does not use. The caller must
    /// treat null as "do not write this setting".
    /// </summary>
    public static uint? ApiBit(GraphicsApi api) => api switch
    {
        GraphicsApi.Dx12 => ApiDx12Bit,
        GraphicsApi.Dx11 => ApiDx11Bit,
        GraphicsApi.Vulkan => ApiVulkanBit,
        _ => null,
    };

    // ── Sets ─────────────────────────────────────────────────────────────────────────────────

    public static ProfileSetting Feature { get; } = new(
        FeatureEnabled, "Smooth Motion 开关", Provenance,
        ValueConfidence.CommunityVerified, OnValue: 1, OffValue: 0);

    public static ProfileSetting Apis { get; } = new(
        EnabledApis, "启用的图形 API（位掩码）", Provenance,
        ValueConfidence.CommunityVerified, OnValue: 7, OffValue: 0);

    /// <summary>
    /// Flip metering pair. Three sources agree on the on/off values, so these are writable when the
    /// caller has a reason to change them — but nothing in this project does so automatically yet.
    /// </summary>
    public static ProfileSetting Flip0 { get; } = new(
        FlipMetering0, "Flip Metering 0", Provenance,
        ValueConfidence.CommunityVerified, OnValue: 0xFFFFFFFF, OffValue: 0);

    public static ProfileSetting Flip1 { get; } = new(
        FlipMetering1, "Flip Metering 1", Provenance,
        ValueConfidence.CommunityVerified, OnValue: 1, OffValue: 0);

    /// <summary>Single source: shown, understood, <b>never written</b>.</summary>
    public static ProfileSetting DebugLog { get; } = new(
        DebugLogLevel, "调试日志级别", Provenance + "；单一来源，仅诊断用",
        ValueConfidence.SingleSource, OnValue: 1, OffValue: 0, Writable: false);

    public static ProfileSetting DebugBarsSetting { get; } = new(
        DebugBars, "Debug Bars", Provenance,
        ValueConfidence.CommunityVerified, OnValue: 1, OffValue: 0);

    public static IReadOnlyList<ProfileSetting> All { get; } = new[]
    {
        Feature, Apis, Flip0, Flip1, DebugLog, DebugBarsSetting,
    };

    /// <summary>Settings this project is willing to write, by id.</summary>
    public static bool IsWritable(uint settingId) =>
        All.FirstOrDefault(s => s.Id == settingId)?.Writable ?? false;

    /// <summary>
    /// The writes that turn Smooth Motion on for one game.
    ///
    /// Only two settings are written automatically, and both are the ones with multi-source values:
    /// the master switch, and the API mask derived from a <i>detected</i> API. The flip-metering pair is
    /// deliberately left alone — nothing in the project has evidence for which value a given game needs,
    /// and guessing there would be exactly the "unknown value written as fact" mistake this file exists
    /// to prevent.
    /// </summary>
    /// <param name="api">The detected graphics API. Unknown produces no API write at all.</param>
    public static IReadOnlyList<ProfileSettingWrite> EnableWrites(GraphicsApi api)
    {
        var writes = new List<ProfileSettingWrite>
        {
            ProfileSettingWrite.Required_(Feature, 1, "启用 Smooth Motion（4 来源一致的 On 值）。"),
        };

        var bit = ApiBit(api);
        if (bit is not null)
        {
            writes.Add(ProfileSettingWrite.Required_(Apis, bit.Value,
                $"按检测到的 {api} 设置 API 位掩码。"));
        }

        return writes;
    }
}
