using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DLSSGManager;

public sealed class OpResult
{
    public bool Ok { get; set; } = true;
    public string Message { get; set; } = "";
    public List<string> Lines { get; } = new();

    /// <summary>
    /// 这一次操作是否**真的往磁盘上写过东西**。
    ///
    /// <para><b>为什么失败时也必须如实回答这个。</b>`Deploy` 有多条**零写入**的早期失败路径（游戏正在
    /// 运行、目录不可写、内核反作弊未授权、源目录无效、代理名无效）。调用方据此决定要不要回滚 ——
    /// 而回滚用的是**上一次**的部署记录：一个零写入的失败被当成「写过」，`Restore` 就会把用户**原本
    /// 正常的安装**整个卸载，报告却写「已回滚」。反过来，真的写到一半才失败却不回滚，会留下半成品。</para>
    ///
    /// <para>默认 <b>false</b> 是保守的那一侧：**没有明确说写过，就不要去回滚别人的东西。**
    /// 每个真正写过盘的返回点必须显式把它置为 `true`。</para>
    /// </summary>
    public bool FilesWritten { get; set; }

    /// <summary>
    /// 这次操作**自己已经把写下去的东西撤销了**。
    ///
    /// <para><b>为什么必须与 <see cref="FilesWritten"/> 分开。</b>`Deploy` 的 `catch` 里已经调用
    /// <c>Rollback(...)</c> 把本步骤写下的代理与 INI 恢复原状 —— 但它**曾经不收回 `FilesWritten`**，
    /// 于是调用方看到「写过盘、且失败」就再回滚一次，**而回滚用的是上一次的部署记录**
    /// （`game.Deployment` 只在成功路径被替换）—— 删掉的是**用户上一次装好的、正在用的安装**。</para>
    ///
    /// <para><b>两个字段各回答一个问题</b>：<see cref="FilesWritten"/> 回答「要不要调用方回滚」，
    /// <see cref="RollbackHandled"/> 回答「我们自己已经收过尾了」。只看前者会把「写了又自己恢复」
    /// 误当成「需要你再来一次」。</para>
    ///
    /// <para><b>触发面不小</b>：进入事务后、第一个游戏目录写入之前只写 `%RestoreRoot%` 下的快照 ——
    /// 磁盘满、杀软锁、源 DLL 被独占、目标 DLL 被别的进程加载，都会走这条「catch 自回滚」的路径。
    /// 而「改设置后重新部署」正好落在它上面。</para>
    ///
    /// <para><b>⚠️ 当前没有任何读取者（Pass D 核实）。</b>写入处只有 <c>Deploy</c> 的收尾
    /// （<c>RollbackHandled = fullyUndone</c>），而调用方 <c>InstallPlanExecutor</c> **目前只用
    /// <see cref="FilesWritten"/> 就够了** —— 因为 <c>Deploy</c> 自回滚成功时会把 <c>FilesWritten</c>
    /// 一并收回，所以「写没写过」这一个问题的答案已经够调用方决定要不要再回滚一次。</para>
    ///
    /// <para><b>它仍然保留，是因为它回答的是另一个问题</b>（「我们自己收尾成功了吗」），
    /// 而那个问题在**回滚失败**时与「要不要回滚」分道扬镳：将来若有调用方需要区分
    /// 「已收尾」与「收尾失败、现场还在」，就得靠它。**在那之前它是「零消费者的公开声明」，
    /// 如实记在报告 §5 里，而不是为了消除这个标注去硬接一个读取者** —— 那会为了让统计好看而加代码。</para>
    /// </summary>
    public bool RollbackHandled { get; set; }

    public void Note(string text)
    {
        Lines.Add(text);
        AppPaths.Log(text);
    }

    public void Fail(string text)
    {
        Ok = false;
        Message = text;
        Lines.Add("错误: " + text);
        AppPaths.Log("错误: " + text);
    }
}

/// <summary>
/// What the Authenticode check found, in enough detail to act on and to report.
///
/// A bool is not enough on the download path: "no signature at all" and "signed, then modified" mean
/// very different things, and only the second one says a mirror served a substituted payload.
/// </summary>
public enum SignatureStatus
{
    /// <summary>Signature matches the bytes. A self-signed chain also counts: the bytes are as signed.</summary>
    Intact,
    /// <summary>No signature block, or not a PE file.</summary>
    NotSigned,
    /// <summary>The file changed after it was signed — the digest in the signature no longer matches.</summary>
    BadDigest,
    /// <summary>WinVerifyTrust reported something else; treated as not intact.</summary>
    Unknown,
}

/// <summary>
/// The only place that writes into game directories. Every write is preceded by a backup of anything
/// that is not ours, and every removal is gated on the file being provably ours (project signature
/// or a matching recorded hash), so a restore can never eat a ReShade dxgi.dll by accident.
/// </summary>
public static class DeploymentService
{
    public const string AutoProxy = GameEntry.AutoProxy;

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// True when the file's Authenticode signature is intact and was made by the project's certificate.
    ///
    /// <see cref="X509Certificate.CreateFromSignedFile"/> only extracts the certificate — it does not
    /// check that the signature still matches the bytes. A tampered DLL would therefore look
    /// "signed" if that were the only check, which matters because downloads may come from a mirror.
    /// The signature is validated through WinVerifyTrust and the result is interpreted fail-closed.
    /// </summary>
    public static bool IsProjectSigned(string path)
    {
        var cert = ReadSignerCertificate(path, out var signatureIntact);
        if (cert is null) return false;

        using (cert)
        {
            if (!signatureIntact) return false;

            var subject = cert.Subject ?? "";
            return subject.Contains("DLSSG", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reads the signer certificate and reports whether the file's signature still matches its
    /// contents.
    ///
    /// A self-signed certificate cannot chain to a trusted root, so chain validation necessarily
    /// fails even for an untouched file. Only the two outcomes that mean "the bytes are as signed"
    /// are accepted; every other result, including a bad digest, is treated as unsigned.
    /// </summary>
    public static X509Certificate2? ReadSignerCertificate(string path, out bool signatureIntact)
    {
        signatureIntact = false;

        X509Certificate2 cert;
        try
        {
            cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        }
        catch
        {
            // No signature block at all, or not a PE file.
            return null;
        }

        signatureIntact = IsSignatureIntact(path);
        return cert;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>Generic verify; accepts any certificate including a self-signed one.</summary>
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    private const int S_OK = 0;
    /// <summary>Chain terminates in an untrusted root — expected for the project's self-signed cert.</summary>
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    /// <summary>No signature present, or the hash in it does not match the file.</summary>
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);

    /// <summary>
    /// Verifies that the file's signature matches its contents. Fails closed: anything other than
    /// "verified" or "verified but self-signed" counts as not intact.
    /// </summary>
    private static bool IsSignatureIntact(string path) => ProbeSignature(path) == SignatureStatus.Intact;

    /// <summary>
    /// The same check as <see cref="IsSignatureIntact"/>, but reporting what was found instead of a
    /// single bool.
    ///
    /// The download path needs the difference: "not signed at all" and "signed, then modified" call
    /// for different wording and different log lines, and collapsing them into false loses exactly the
    /// signal that says a mirror served a substituted file.
    /// </summary>
    public static SignatureStatus ProbeSignature(string path)
    {
        // A file that is not a PE image has no signature to speak of. Saying so up front keeps
        // WinVerifyTrust's answer for that case from surfacing as an unexplained error code, which
        // would otherwise read as "something went wrong" rather than "this is not a signed binary".
        try
        {
            using var head = File.OpenRead(path);
            if (head.ReadByte() != 'M' || head.ReadByte() != 'Z') return SignatureStatus.NotSigned;
        }
        catch
        {
            return SignatureStatus.NotSigned;
        }

        IntPtr filePtr = IntPtr.Zero;
        IntPtr dataPtr = IntPtr.Zero;

        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, filePtr, false);

            var data = new WinTrustData
            {
                cbStruct = Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = 0,
            };

            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPtr, false);

            var result = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, dataPtr);

            // Close the state or the handle stays open until the process exits.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, dataPtr, false);
            WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, dataPtr);

            if (result == S_OK) return SignatureStatus.Intact;

            if (result == CERT_E_UNTRUSTEDROOT)
            {
                // The bytes are as signed; only the chain is untrusted, which is inherent to a
                // self-signed certificate. Callers confirm identity via the pinned thumbprint.
                return SignatureStatus.Intact;
            }

            if (result == TRUST_E_BAD_DIGEST)
            {
                AppPaths.Log($"签名校验未通过（摘要不匹配，文件可能被篡改）：{path}");
                return SignatureStatus.BadDigest;
            }

            if (result == TRUST_E_NOSIGNATURE)
            {
                AppPaths.Log($"签名校验未通过（无签名）：{path}");
                return SignatureStatus.NotSigned;
            }

            AppPaths.Log($"签名校验返回未知结果 0x{result:X8}，按未签名处理：{path}");
            return SignatureStatus.Unknown;
        }
        catch (Exception ex)
        {
            AppPaths.Log("验证签名失败: " + ex.Message);
            return SignatureStatus.Unknown;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
        }
    }

    /// <param name="determined">
    /// 这次检查是否**真的能判定**。为 false 表示「查不到」。
    ///
    /// <para><b>为什么必须把它与空表分开。</b>调用方用这个结果守「游戏正在运行就不要写」。而空表
    /// **同时**表示两件事：「确实没有进程占用」与「我们查不到」—— 两者的处置**相反**：前者可以继续
    /// 部署，后者应当停下来。曾经这里两种情况都返回空表，于是**查不到被当成了不在运行**，
    /// 失败被推后到写入阶段变成 `Access to the path is denied`（那时用户已经动过磁盘了）。</para>
    /// </param>
    public static List<Process> ProcessesRunningIn(string directory, out bool determined)
    {
        var result = new List<Process>();
        determined = false;
        string full;
        try { full = Path.GetFullPath(directory).TrimEnd('\\') + "\\"; }
        catch { return result; }

        // 路径可解析 ⇒ 到目前为止可以判定；下面任何一次「读不到进程路径」都会把它降回去。
        determined = true;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = Native.GetProcessPath(p.Id);
                if (string.IsNullOrEmpty(path))
                {
                    // **读不到一个进程的路径，就不能声称「它不在这个目录」** —— 它可能恰恰就在
                    // （受保护进程、跨权限读取、刚启动还没映射映像）。如实降级为「无法判定」。
                    determined = false;
                    continue;
                }

                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) continue;
                var dirFull = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                if (dirFull.StartsWith(full, StringComparison.OrdinalIgnoreCase)) result.Add(p);
            }
            catch
            {
                // A process that exits mid-enumeration is not a reason to fail the check.
            }
        }

        return result;
    }

    public static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".dlssg_write_probe_" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Unblock(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); }
        catch { /* No mark-of-the-web, or a filesystem without alternate streams. */ }
    }

    /// <summary>
    /// True when the file is provably this project's: either it carries the project's self-signed
    /// certificate, or it matches the hash recorded when we wrote it.
    /// </summary>
    public static bool IsOurs(string path, string? recordedHash)
    {
        if (!File.Exists(path)) return false;
        if (IsProjectSigned(path)) return true;
        if (!string.IsNullOrEmpty(recordedHash))
        {
            try { return string.Equals(Sha256(path), recordedHash, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        return false;
    }

    /// <summary>
    /// Every entry-name slot a proxy of ours could occupy: the known names, plus any file a
    /// deployment record claims. A proxy the user imported keeps its own file name — that name is
    /// the entry name the game resolves — so it appears in no fixed list and only the record vouches
    /// for it. The INI is excluded: it is a record of ours too, but it is not a proxy, and counting
    /// it would report a false "multiple proxies" fault.
    /// </summary>
    private static IEnumerable<string> OwnedEntryNames(DeploymentInfo? prev)
    {
        var names = new List<string>(ModSource.KnownProxyNames);
        if (prev is not null)
            names.AddRange(prev.Files.Select(f => Path.GetFileName(f.FileName)));

        return names.Where(n => !string.Equals(n, ModSource.IniName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The hash the deployment record claims for an entry name, or null when it claims none.</summary>
    private static string? RecordedHashFor(DeploymentInfo? prev, string name)
    {
        if (prev is null) return null;

        var file = prev.Files.FirstOrDefault(f =>
            string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase));
        if (file is not null) return file.Sha256;

        return string.Equals(prev.ProxyName, name, StringComparison.OrdinalIgnoreCase)
            ? prev.ProxySha256
            : null;
    }

    /// <summary>
    /// First entry name in the game directory that already holds something of ours: a DLL carrying this
    /// project's signature, a published extra (the community d3d12.dll, by hash), or a file the given
    /// record claims whose bytes still match.
    ///
    /// Scanning covers the record's own entries as well as <see cref="ModSource.KnownProxyNames"/>, so
    /// a proxy the user imported under its own name is recognised on the next status check instead of
    /// the game looking undeployed.
    /// </summary>
    public static string? FindInstalledProxy(string renderDir, DeploymentInfo? prev = null)
    {
        foreach (var name in OwnedEntryNames(prev))
        {
            var path = Path.Combine(renderDir, name);
            if (File.Exists(path) && IsOurProxyAt(renderDir, name, prev)) return name;
        }

        return null;
    }

    /// <summary>
    /// Chooses which proxy entry name to use, from the names this source can actually provide.
    ///
    /// An existing proxy of ours is reused in preference to a free name, because the game loads every
    /// entry name it recognises: installing under a second name would leave two proxies live at once.
    /// Switching names is only for the case where another product already occupies the current one.
    ///
    /// An explicit choice is honoured only when the source has a DLL for it; otherwise the caller is
    /// told, rather than a different entry being installed behind the user's back.
    /// </summary>
    private static string? PickFreeProxy(GameEntry game, ModSource source)
    {
        var available = source.AvailableProxies;

        var wanted = game.PreferredProxy;
        if (!string.Equals(wanted, AutoProxy, StringComparison.OrdinalIgnoreCase))
        {
            return available.Contains(wanted, StringComparer.OrdinalIgnoreCase)
                ? available.First(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        // Reuse our own installation rather than picking a second, unoccupied name.
        var installed = FindInstalledProxy(game.RenderDir, game.Deployment);
        if (installed is not null) return installed;

        foreach (var name in available)
        {
            var path = Path.Combine(game.RenderDir, name);
            if (!File.Exists(path)) return name;
        }

        return available.FirstOrDefault();
    }

    /// <summary>
    /// Installs the proxy and INI into the game folder.
    ///
    /// <paramref name="allowProtected"/> defaults to false: a game with a kernel-mode anti-cheat will
    /// quarantine the proxy before it loads (so the mod cannot work) and may record a violation
    /// against the account, so that combination is refused unless the caller explicitly overrides.
    /// </summary>
    public static OpResult Deploy(GameEntry game, ModSource source, bool allowProtected = false)
    {
        var r = new OpResult();

        if (!source.IsValid)
        {
            // The common case for a fresh clone is that the mod files were never fetched, so say that
            // rather than only reporting which file is missing.
            var hint = Directory.Exists(source.Root)
                ? $"（{source.Root}）"
                : Loc.T("Deploy.SourceMissingHint");

            r.Fail(Loc.T("Deploy.SourceUnavailable", source.ValidationMessage, hint));
            return r;
        }

        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
        {
            r.Fail(Loc.T("Deploy.NeedDir"));
            return r;
        }

        var running = ProcessesRunningIn(game.RenderDir, out var runningDetermined);
        if (running.Count > 0)
        {
            r.Fail(Loc.T("Deploy.GameRunning", Loc.Join(running.Select(p => p.ProcessName))));
            return r;
        }

        // **「查不到」不等于「不在运行」—— 但也**不能因此拒绝**。**
        //
        // 有些进程的映像路径读不出来（受保护进程、跨权限），而普通权限下**这是常态**：系统里总有
        // 若干这样的进程。所以「有任何进程读不到就拒绝部署」会让本工具在普通权限下**完全不可用**
        // —— 实测把全部部署用例打成了失败（118 通过 / 5 失败）。
        //
        // 正确的处置是**记录下来让用户知情**，而不是替他做一个他无法推翻的决定：如果是游戏本身
        // 读不到，用户的下一步（关闭游戏重试）与「游戏在运行」时的建议一致；如果不是，他也不会
        // 被无谓地挡住。
        if (!runningDetermined)
        {
            r.Note("提示：有进程的映像路径读不出来（通常是权限不足），因此「游戏是否正在运行」无法完全确认。"
                   + "如果这次部署遇到文件被占用，请先关闭游戏再重试。");
        }

        var protection = AntiCheat.Scan(game.RenderDir);
        game.Protection = protection;

        if (protection.HasKernelAntiCheat && !allowProtected)
        {
            r.Fail(Loc.T("Deploy.BlockedKernel", protection.Summary, protection.Evidence));
            return r;
        }

        if (!IsWritable(game.RenderDir))
        {
            r.Fail(Loc.T("Deploy.NoPermission"));
            return r;
        }

        var proxy = PickFreeProxy(game, source);
        if (proxy is null)
        {
            r.Fail(string.Equals(game.PreferredProxy, AutoProxy, StringComparison.OrdinalIgnoreCase)
                ? Loc.T("Detail.EntriesTaken")
                : Loc.T("Deploy.InvalidProxyName", game.PreferredProxy));
            return r;
        }

        var prev = game.Deployment;
        var proxyDest = Path.Combine(game.RenderDir, proxy);
        var proxyTaken = File.Exists(proxyDest) && !IsOurs(proxyDest, prev?.ProxySha256);

        if (proxyTaken)
        {
            r.Fail(Loc.T("Deploy.ProxyTaken", proxy));
            return r;
        }

        var iniDest = Path.Combine(game.RenderDir, ModSource.IniName);
        var iniText = IniTemplate.Render(source.IniText, game.Profile);
        var restoreFolder = Path.Combine(AppPaths.RestoreRoot, game.Id, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var backups = new List<BackupItem>();
        string? tmp = null;
        string? iniTmp = null;

        // Transaction state: snapshots of the bytes this deployment is about to replace, so a failure
        // in a later step can be undone. A false flag means the file was not there at all, and
        // rollback should delete what we wrote instead of restoring anything.
        var txFolder = Path.Combine(restoreFolder, "_pending");
        var proxyExisted = File.Exists(proxyDest);
        var iniExisted = File.Exists(iniDest);
        string? rollbackProxy = null;
        string? rollbackIni = null;

        // The mod requires exactly one proxy in the game folder: the game loads every entry name it
        // recognises, so two would run two inference pipelines at once. Rather than trusting the
        // deployment record (which can be absent, e.g. after the library is reset), scan the entry
        // names for anything of ours and remove all but the one being installed.
        //
        // "Ours" includes a file the record claims by hash: an entry the user added themselves carries
        // no signature this project can vouch for, so the record is the only way to recognise it.
        var redundantProxies = OwnedEntryNames(prev)
            .Where(n => !string.Equals(n, proxy, StringComparison.OrdinalIgnoreCase))
            .Select(n => (Name: n, Path: Path.Combine(game.RenderDir, n)))
            .Where(x => File.Exists(x.Path) && IsOurProxyAt(game.RenderDir, x.Name, prev))
            .ToList();

        try
        {
            // 从这里开始就可能写盘了 —— 而且**只有从这里开始才可能**。
            //
            // 上面那 7 个早期返回点（游戏正在运行、目录不可写、内核反作弊未授权、源目录无效、代理名无效…）
            // 全都是**零写入**的，默认 `false` 已经如实反映了它们。
            //
            // 这个位置是保守选择：进入事务后任何一步都可能写（trim 多余代理、备份外来 INI、快照、复制
            // 代理与 INI），而失败时无法精确知道写到了哪一步 ——「宁可多回滚一次，不可漏回滚」。
            //
            // **这条区分为什么重要**：调用方（`SmoothMotionWorkflow.Finish`）据此决定要不要回滚，而回滚
            // 用的是**上一次**的部署记录。零写入的失败若被当成「写过」，`Restore` 会把用户**原本正常的
            // 安装**整个卸载掉，报告却写「已回滚」—— 用户失去了一个能用的安装，而程序说它清理干净了。
            r.FilesWritten = true;

            // Extra proxies of ours in the folder: on 0.2.x payloads two live pipelines crash, so
            // they are trimmed. On 0.3.3+ the loader runs them as standby forwarders by design (the
            // game loads whichever name it recognises first), and some games only respond to one
            // specific entry name — so there they stay, recorded with the deployment so a later
            // restore still removes every file we are responsible for.
            if (source.IsLegacySchema)
            {
                foreach (var (name, path) in redundantProxies)
                {
                    r.Note(Loc.T("Deploy.RemoveRedundant", name));
                    File.Delete(path);
                }
            }
            else if (redundantProxies.Count > 0)
            {
                foreach (var (name, _) in redundantProxies)
                    r.Note(Loc.T("Deploy.StandbyKept", name));
            }

            // Displace anything that is not ours, keeping a copy so restore can put it back.
            //
            // Ownership is decided by content, not only by the recorded hash: the INI we generate is
            // meant to be edited (that is what the profile settings do), so its hash drifts from the
            // record. Judging by hash alone would classify our own file as a foreign one, back it up,
            // and then restore it on uninstall — leaving our config behind in the game folder.
            if (File.Exists(iniDest) && !LooksLikeProjectIni(iniDest) && !IsOurs(iniDest, prev?.IniSha256))
            {
                backups.Add(Backup(iniDest, restoreFolder, r));
            }

            // Stage 2 transaction: snapshot what we are about to overwrite, so a failure in any later
            // step can be undone rather than leaving a half-applied deployment in the game folder.
            if (proxyExisted) rollbackProxy = Snapshot(proxyDest, txFolder, "proxy");
            if (iniExisted) rollbackIni = Snapshot(iniDest, txFolder, "ini");

            tmp = proxyDest + ".dlssgtmp";
            File.Copy(source.DllPath(proxy), tmp, overwrite: true);
            File.Move(tmp, proxyDest, overwrite: true);

            iniTmp = iniDest + ".dlssgtmp";
            File.WriteAllText(iniTmp, iniText, new UTF8Encoding(false));
            File.Move(iniTmp, iniDest, overwrite: true);

            Unblock(proxyDest);
            Unblock(iniDest);

            var proxyHash = Sha256(proxyDest);
            var iniHash = Sha256(iniDest);

            // Carry the previous record's backups into the new one. Without this, a redeploy (a
            // settings change, or a batch deploy over an already-deployed game) silently drops the
            // only references to displaced foreign files — the user's original INI would sit in the
            // restore folder forever and never come back. New backups win on a name clash.
            var carried = (prev?.Backups ?? new List<BackupItem>())
                .Where(b => backups.All(n => !string.Equals(n.FileName, b.FileName, StringComparison.OrdinalIgnoreCase)))
                .Concat(backups)
                .ToList();

            game.Deployment = new DeploymentInfo
            {
                ProxyName = proxy,
                ModVersion = source.Version,
                DeployedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ProxySha256 = proxyHash,
                IniSha256 = iniHash,
                // Restore works from each item's StoredPath, not this string; it names the folder
                // this deploy wrote into, or the previous one's when nothing new was displaced.
                RestoreFolder = backups.Count > 0 ? restoreFolder : prev?.RestoreFolder ?? "",
                Backups = carried,
                // Every file this deployment is responsible for: the proxy and INI it wrote, plus —
                // on 0.3.3+ payloads — the standby proxies left in place instead of removed. The
                // record is what lets the entry-name scan and restore recognise files that carry no
                // signature this project can vouch for.
                Files = new List<DeployedFile>
                {
                    new() { FileName = proxy, Sha256 = proxyHash, Size = new FileInfo(proxyDest).Length },
                    new() { FileName = ModSource.IniName, Sha256 = iniHash, Size = new FileInfo(iniDest).Length },
                },
            };

            if (!source.IsLegacySchema)
            {
                foreach (var (name, path) in redundantProxies)
                {
                    game.Deployment!.Files.Add(new DeployedFile
                    {
                        FileName = name,
                        Sha256 = Sha256(path),
                        Size = new FileInfo(path).Length,
                    });
                }
            }

            r.Note(Loc.T("Deploy.Done", proxy, ModSource.IniName, game.RenderDir));

            // The summary names the keys actually written, which the INI schema decides: a 0.3.0
            // payload never reads Router or KernelImage, so logging them would report settings that
            // exist only in the interface's legacy panel.
            if (source.IsLegacySchema)
            {
                r.Note(Loc.T("Detail.SettingSummary",
                    game.Profile.Router,
                    game.Profile.KernelImage,
                    game.Profile.MaxGeneratedFrames + 1,
                    Loc.T(game.Profile.HardwareBilinear ? "Deploy.On" : "Deploy.Off"),
                    game.Profile.LogLevel));
            }
            else
            {
                r.Note(Loc.T("Detail.SettingSummaryModern",
                    Loc.T(game.Profile.Enabled ? "Deploy.On" : "Deploy.Off"),
                    game.Profile.OptimizedTier,
                    game.Profile.Preset,
                    game.Profile.MaxGeneratedFrames + 1,
                    game.Profile.LogLevel));
            }

            if (backups.Count > 0)
            {
                r.Note(Loc.T("Deploy.BackedUp", backups.Count, restoreFolder));
            }

            // **事务快照用完就删。** `_pending` 里装的是「这次要覆盖的文件的原字节」，只在本次事务
            // 失败回滚时需要；一旦走到这里（写入已经完成、记录也已经落下），它就再也没有引用者了。
            //
            // 而原来**只有 catch 分支**删它 ⇒ **每次成功的重部署都在 `%RestoreRoot%` 里多留一份
            // 代理 + INI 的副本**（真实代理约 15 MB）。用户看不到任何提示，磁盘却在每次「改设置再部署」
            // 时增长 —— 而 `backups.Count == 0` 时连 `RestoreFolder` 都不会被记录，
            // **那些副本没有任何东西指向它们**。
            try { if (Directory.Exists(txFolder)) Directory.Delete(txFolder, recursive: true); }
            catch { /* 快照目录可丢弃；删不掉不影响这次部署的结果 */ }

            r.Message = Loc.T("Deploy.Success", proxy);
        }
        catch (Exception ex)
        {
            // A failed copy/move (locked file, full disk) must not leave a stray *.dlssgtmp in the
            // game folder — nothing else ever recognises or cleans those.
            foreach (var leftover in new[] { tmp, iniTmp })
            {
                if (leftover is null) continue;
                try { File.Delete(leftover); } catch { /* best effort */ }
            }

            // Undo whatever the earlier steps already wrote. Without this the folder keeps a proxy
            // whose INI never arrived, while the library records no deployment at all — the two then
            // disagree about a game that is in fact half-modified.
            var rollbackNotes = new List<string>();
            var proxyUndone = Rollback(proxyDest, rollbackProxy, proxyExisted, rollbackNotes);
            var iniUndone = Rollback(iniDest, rollbackIni, iniExisted, rollbackNotes);

            // **只有两边都真的撤销了，才能收回「需要调用方回滚」这个结论。**
            //
            // 曾经这里无条件 `FilesWritten = false`，于是回滚**失败**时（快照建不出来、还原副本被 AV 占用）
            // 写进去的半成品留在游戏目录，而外层因为看到「没写过」就跳过了 `Provider.Restore` ——
            // **残留无人清理，用户没有任何途径知道要手工删**。更糟的是工具自己的残留会被自己的扫描判成
            // `UnknownConflict`，planner 于是改选下一个空闲名，**可能同时存在两个代理**。
            //
            // 「尝试过回滚」与「回滚成功」是两件事 —— 只有后者才配得上「已收尾」。
            var fullyUndone = proxyUndone && iniUndone;
            r.FilesWritten = !fullyUndone;
            r.RollbackHandled = fullyUndone;

            r.Fail(Loc.T("Deploy.Failed", ex.Message));
            foreach (var note in rollbackNotes) r.Note(note);

            // **只有回滚真的完成了才删快照。**
            //
            // `txFolder` 里装的是「本次要覆盖的文件的原字节」，**没有第二份**（`Backup()` 只对被判为
            // 外来的 INI 另存一份到 `restoreFolder`）。回滚因目标被占用而**失败**时，这份原字节是用户
            // 恢复文件的唯一希望 —— 而这里曾经**无条件**删掉它，**同时还告诉用户「请手工检查该文件」**：
            // 让他去检查一个我们已经把恢复材料删掉的文件。
            if (fullyUndone)
            {
                try { if (Directory.Exists(txFolder)) Directory.Delete(txFolder, recursive: true); }
                catch { /* 快照目录可丢弃；删不掉不影响这次部署的结果 */ }
            }
            else
            {
                // 留着它，并把位置写出来 —— 用户需要一个能自己动手的入口。
                r.Note($"未完成的回滚快照保留在：{txFolder}");
            }
        }

        return r;
    }

    /// <summary>
    /// Copies a file into the transaction folder so a failed deployment can put it back.
    ///
    /// Returns the snapshot path, or null when the copy could not be made — the caller then reports
    /// that this particular file could not be rolled back, rather than assuming it was.
    /// </summary>
    private static string? Snapshot(string path, string txFolder, string label)
    {
        try
        {
            Directory.CreateDirectory(txFolder);
            var target = Path.Combine(txFolder, label + "_" + Path.GetFileName(path));
            File.Copy(path, target, overwrite: true);
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes one file's part of a failed deployment: puts the snapshot back when there was one, or
    /// deletes the file this run created.
    ///
    /// Never stays silent: a rollback that could not happen is reported, because the game folder is
    /// then in a state the user has to inspect by hand.
    ///
    /// <para><b>返回「这次回滚是否真的完成了」。</b>调用方必须看它才能决定要不要把「需要回滚」这个结论
    /// 交出去 —— 曾经这里返回 `void`，而调用方**无条件**宣布「已收尾」，于是回滚失败时（快照建不出来、
    /// 还原副本被占用）写进去的半成品留在游戏目录，外层却因为 `FilesWritten = false` 而不再回滚它。
    /// **「尝试过回滚」与「回滚成功」是两件事，只有后者才能收回那个结论。**</para>
    /// </summary>
    private static bool Rollback(string dest, string? snapshot, bool existedBefore, List<string> notes)
    {
        try
        {
            if (existedBefore)
            {
                if (snapshot is null || !File.Exists(snapshot))
                {
                    notes.Add(Loc.T("Deploy.RollbackNoSnapshot", Path.GetFileName(dest)));
                    return false;
                }

                File.Copy(snapshot, dest, overwrite: true);
                notes.Add(Loc.T("Deploy.RollbackRestored", Path.GetFileName(dest)));
            }
            else if (File.Exists(dest))
            {
                File.Delete(dest);
                notes.Add(Loc.T("Deploy.RollbackRemoved", Path.GetFileName(dest)));
            }

            // 没有需要撤销的东西也算完成 —— 目标状态已经达成。
            return true;
        }
        catch (Exception ex)
        {
            notes.Add(Loc.T("Deploy.RollbackFailed", Path.GetFileName(dest), ex.Message));
            return false;
        }
    }

    private static BackupItem Backup(string path, string restoreFolder, OpResult r)
    {
        Directory.CreateDirectory(restoreFolder);
        var stored = Path.Combine(restoreFolder, Path.GetFileName(path));
        File.Copy(path, stored, overwrite: true);
        var item = new BackupItem
        {
            FileName = Path.GetFileName(path),
            StoredPath = stored,
            Sha256 = Sha256(stored),
            Size = new FileInfo(stored).Length,
        };
        r.Note(Loc.T("Deploy.BackupFile", item.FileName, item.Size / 1024));
        return item;
    }

    public static OpResult Restore(GameEntry game, bool removeLogs)
    {
        var r = new OpResult();

        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
        {
            r.Fail(Loc.T("Restore.NeedDir"));
            return r;
        }

        var running = ProcessesRunningIn(game.RenderDir, out var runningDetermined);
        if (running.Count > 0)
        {
            r.Fail(Loc.T("Restore.GameRunning", Loc.Join(running.Select(p => p.ProcessName))));
            return r;
        }

        // **恢复同样记提示而不是拒绝。** 理由与 `Deploy` 那处相同：普通权限下「有进程路径读不出来」
        // 是常态，用它拒绝会让工具不可用。而这里的提示对用户同样可行动。
        if (!runningDetermined)
        {
            r.Note("提示：有进程的映像路径读不出来（通常是权限不足），因此「游戏是否正在运行」无法完全确认。"
                   + "如果恢复时遇到文件被占用，请先关闭游戏再重试。");
        }

        if (!IsWritable(game.RenderDir))
        {
            r.Fail(Loc.T("Deploy.NoPermission"));
            return r;
        }

        var prev = game.Deployment;
        var removed = 0;

        try
        {
            // 1) The proxy we recorded; if there is no record, any project-signed DLL in a known entry name.
            // Scan the record's own entries as well as the known names: a proxy imported under its
            // own name appears in neither list alone. A stray proxy can be present (a name switch
            // that predates the single-proxy rule, a restored backup, an earlier record lost from
            // the library), and leaving it behind would keep a proxy live in a folder the user
            // expects to be clean.
            foreach (var name in OwnedEntryNames(prev))
            {
                var path = Path.Combine(game.RenderDir, name);
                if (!File.Exists(path)) continue;

                // Only files the record claims are accepted by hash; anything else must prove
                // itself by carrying the project's signature.
                if (IsOurs(path, RecordedHashFor(prev, name)))
                {
                    File.Delete(path);
                    removed++;
                    r.Note(Loc.T("Restore.Removed", name));
                }
                else
                {
                    r.Note(Loc.T("Restore.ProxyKept", name));
                }
            }

            // 2) The INI. A recorded deployment proves we put an INI at this exact path, so a
            //    hash mismatch only means the user edited it by hand — still ours to remove.
            var iniPath = Path.Combine(game.RenderDir, ModSource.IniName);
            if (File.Exists(iniPath))
            {
                var byHash = prev is not null && IsOurs(iniPath, prev.IniSha256);
                var byBanner = LooksLikeProjectIni(iniPath);

                if (byHash || byBanner)
                {
                    if (prev is not null && !byHash)
                        r.Note(Loc.T("Restore.IniEdited", ModSource.IniName));

                    File.Delete(iniPath);
                    removed++;
                    r.Note(Loc.T("Restore.Removed", ModSource.IniName));
                }
                else
                {
                    r.Note(Loc.T("Restore.IniForeign", ModSource.IniName));
                }
            }

            // 3) Copies an anti-cheat quarantined by renaming (e.g. version.dll.3787982156). Only
            //    files that are provably ours are removed, so another tool's ".bak" survives.
            foreach (var name in OwnedEntryNames(prev))
            {
                foreach (var copy in AntiCheat.FindQuarantinedCopies(game.RenderDir, name, RecordedHashFor(prev, name)))
                {
                    try
                    {
                        File.Delete(copy);
                        removed++;
                        r.Note(Loc.T("Restore.QuarantinedRemoved", Path.GetFileName(copy)));
                    }
                    catch (Exception ex)
                    {
                        r.Note(Loc.T("Restore.QuarantinedFailed", Path.GetFileName(copy), ex.Message));
                    }
                }
            }

            // 4) Put back whatever we displaced — but only after proving the stored copy is still the
            //    bytes we took. The record keeps a SHA-256 for exactly this purpose: writing back a
            //    backup that has since been altered or swapped would put a corrupt (or substituted)
            //    file into the game folder, and the user's original is already gone by then.
            if (prev is not null && prev.Backups.Count > 0)
            {
                foreach (var b in prev.Backups)
                {
                    if (!File.Exists(b.StoredPath))
                    {
                        r.Note(Loc.T("Restore.BackupLost", b.FileName));
                        continue;
                    }

                    // The name comes from a library file, which the user or another tool can edit, so
                    // it is not trusted to stay inside the render directory.
                    if (!IsSafeBackupTarget(game.RenderDir, b.FileName, out var dest))
                    {
                        r.Note(Loc.T("Restore.BackupUnsafeName", b.FileName));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(b.Sha256))
                    {
                        r.Note(Loc.T("Restore.BackupNoHash", b.FileName));
                        continue;
                    }

                    if (!BackupMatchesRecord(b))
                    {
                        r.Note(Loc.T("Restore.BackupHashMismatch", b.FileName));
                        continue;
                    }

                    File.Copy(b.StoredPath, dest, overwrite: true);
                    removed++;
                    r.Note(Loc.T("Restore.BackupRestored", b.FileName));
                }
            }

            if (removeLogs)
            {
                var logs = Path.Combine(game.RenderDir, ModSource.LogDirName);
                if (Directory.Exists(logs))
                {
                    Directory.Delete(logs, recursive: true);
                    r.Note(Loc.T("Restore.LogsDeleted", ModSource.LogDirName));
                }
            }

            game.Deployment = null;
            r.Message = removed > 0 ? Loc.T("Restore.Done", removed) : Loc.T("Restore.None");
            r.Note(r.Message);
        }
        catch (Exception ex)
        {
            r.Fail(Loc.T("Restore.Failed", ex.Message));
        }

        return r;
    }

    /// <summary>
    /// True when the stored copy still hashes to what the record captured when it was taken.
    ///
    /// Fails closed: a file that cannot be read counts as a mismatch, because the point of the check
    /// is to guarantee the bytes, and an unreadable file cannot guarantee anything.
    /// </summary>
    private static bool BackupMatchesRecord(BackupItem b)
    {
        try
        {
            return string.Equals(Sha256(b.StoredPath), b.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a backup's recorded file name against the render directory, refusing anything that
    /// escapes it.
    ///
    /// The name is persisted in the library file, which the user — or any other tool that can write
    /// to %APPDATA% — is free to edit, so a record reading <c>..\..\Windows\System32\evil.dll</c> must
    /// not be able to steer a restore out of the game folder. Separators, drive letters and ".." are
    /// rejected outright, and the resolved path is confirmed to sit under the directory itself.
    /// </summary>
    private static bool IsSafeBackupTarget(string renderDir, string fileName, out string dest)
    {
        dest = "";

        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (fileName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return false;
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) return false;

        try
        {
            var root = Path.GetFullPath(renderDir);
            var full = Path.GetFullPath(Path.Combine(root, fileName));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            dest = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Recognises the project's INI by its banner or its own file name. Deliberately stricter than a
    /// bare keyword search so an unrelated mod's INI sharing the slot is left alone.
    /// </summary>
    private static bool LooksLikeProjectIni(string path)
    {
        try
        {
            var head = File.ReadLines(path).Take(4).ToList();
            if (head.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"Native\s+[0-9]+\.[0-9]+")))
                return true;

            return File.ReadAllText(path).Contains("dlssg_sm86", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Takes over an installation that was copied in by hand, so the manager can restore it later.</summary>
    public static OpResult Adopt(GameEntry game, string providerId)
    {
        var r = new OpResult();
        if (!Directory.Exists(game.RenderDir))
        {
            r.Fail(Loc.T("Restore.NeedDir"));
            return r;
        }

        var proxy = FindInstalledProxy(game.RenderDir);
        if (proxy is null)
        {
            r.Fail(Loc.T("Adopt.NotFound"));
            return r;
        }

        var proxyPath = Path.Combine(game.RenderDir, proxy);
        var iniPath = Path.Combine(game.RenderDir, ModSource.IniName);
        var iniExists = File.Exists(iniPath);

        game.Deployment = new DeploymentInfo
        {
            ProxyName = proxy,

            // **记录是谁接管的。**
            //
            // 没有它，恢复时的 `ProviderForRestore` 会因为 id 为空而**直接拒绝**（那是刻意的诚实选择：
            // 不猜 provider，因为猜错会删掉错误的文件）—— 而 `Adopt.Confirm` 的文案却告诉用户
            // 「之后恢复只能按记录里的 SHA256 删除这些文件」。
            // **一个承诺了却必然失败的按钮，比没有按钮更糟：用户会以为接管之后还能撤销。**
            ProviderId = providerId,
            ModVersion = (iniExists ? ModSource.ReadVersion(iniPath) : null) ?? Loc.T("ModSource.UnknownVersion"),
            DeployedAt = File.GetLastWriteTime(proxyPath).ToString("yyyy-MM-dd HH:mm:ss") + Loc.T("Adopt.AdoptedSuffix"),
            ProxySha256 = Sha256(proxyPath),
            IniSha256 = iniExists ? Sha256(iniPath) : "",
            RestoreFolder = "",
            Backups = new List<BackupItem>(),
        };

        r.Note(Loc.T("Adopt.Done", proxy) + (iniExists ? $" + {ModSource.IniName}" : Loc.T("Adopt.NoIni")));
        r.Note(Loc.T("Adopt.Note"));
        r.Message = Loc.T("Adopt.Done", proxy);
        return r;
    }

    /// <summary>
    /// Outcome of inspecting a game folder. Produced by <see cref="Evaluate"/> and applied by
    /// <see cref="Apply"/>, which lets the expensive filesystem work run off the UI thread while the
    /// property changes land on it.
    /// </summary>
    public sealed record GameCheck(GameStatus Status, string Detail, ProtectionReport? Protection);

    /// <summary>
    /// Inspects a game folder and reports its state. Reads the filesystem but modifies nothing, so it
    /// is safe to call from a background thread.
    ///
    /// Split from <see cref="Apply"/> because it is genuinely expensive: each candidate entry name is
    /// checked with WinVerifyTrust over a ~15 MB DLL, and matching against the record hashes the file
    /// again. Running that for several games on the UI thread froze the window for seconds.
    /// </summary>
    public static GameCheck Evaluate(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.RenderDir))
            return new GameCheck(GameStatus.Unknown, Loc.T("Status.NoRenderDir"), null);

        if (!Directory.Exists(game.RenderDir))
            return new GameCheck(GameStatus.Missing, Loc.T("Status.RenderDirMissing"), null);

        // Drives the warning banner, so it is refreshed on every check.
        var protection = AntiCheat.Scan(game.RenderDir);

        var root = game.RenderDir;
        var prev = game.Deployment;

        // Since 0.3.3 several proxies of the project may share a folder by design: the first one the
        // game loads becomes active and the rest forward their exports without installing hooks, so
        // there is no second pipeline and no crash. Extra proxies are therefore reported as a note
        // on the normal status, not as a fault. (The manager itself still installs exactly one.)
        var liveProxies = OwnedEntryNames(prev)
            .Where(n => File.Exists(Path.Combine(root, n)) && IsOurProxyAt(root, n, prev))
            .ToList();
        var standby = liveProxies.Count > 1
            ? " " + Loc.T("Status.StandbyNote", liveProxies.Count - 1)
            : "";

        if (prev is null)
        {
            var found = FindInstalledProxy(root);
            return new GameCheck(GameStatus.NotDeployed,
                found is null ? Loc.T("Status.NotDeployedDetail") : Loc.T("Status.ManualInstall", found) + standby,
                protection);
        }

        var proxyPath = Path.Combine(root, prev.ProxyName);
        var iniPath = Path.Combine(root, ModSource.IniName);
        var proxyOk = File.Exists(proxyPath);
        var iniOk = File.Exists(iniPath);

        if (!proxyOk || !iniOk)
        {
            // A kernel anti-cheat renames the proxy rather than deleting it, so a vanished DLL with
            // quarantined copies left behind is reported distinctly from a plain missing file.
            var quarantined = AntiCheat.FindQuarantinedCopies(root, prev.ProxyName, prev.ProxySha256);

            var detail = quarantined.Count > 0
                ? Loc.T("Status.Quarantined", prev.ProxyName, quarantined.Count, protection.Summary)
                : !proxyOk ? Loc.T("Status.MissingProxy", prev.ProxyName) : Loc.T("Status.MissingIni", ModSource.IniName);

            return new GameCheck(GameStatus.Missing, detail, protection);
        }

        try
        {
            var proxyMatch = string.Equals(Sha256(proxyPath), prev.ProxySha256, StringComparison.OrdinalIgnoreCase);
            var iniMatch = string.Equals(Sha256(iniPath), prev.IniSha256, StringComparison.OrdinalIgnoreCase);

            return proxyMatch && iniMatch
                ? new GameCheck(GameStatus.Deployed,
                    $"Mod {prev.ModVersion} · {prev.ProxyName} · {prev.DeployedAt}" + standby, protection)
                : new GameCheck(GameStatus.Modified,
                    proxyMatch ? Loc.T("Status.IniModified") : Loc.T("Status.ProxyMismatch"),
                    protection);
        }
        catch (Exception ex)
        {
            return new GameCheck(GameStatus.Unknown, Loc.T("Status.VerifyFailed", ex.Message), protection);
        }
    }

    /// <summary>Applies an evaluation's results. Call on the UI thread.</summary>
    public static void Apply(GameEntry game, GameCheck check)
    {
        if (check.Protection is not null) game.Protection = check.Protection;
        game.Status = check.Status;
        game.StatusDetail = check.Detail;
    }

    /// <summary>
    /// True when an entry-name slot holds something this project put there: a DLL carrying our signature,
    /// a published extra (byte for byte), or a file a deployment record claims whose bytes still match.
    ///
    /// The record half exists for entries the user added themselves, and the extra half for the community
    /// build this project distributes — a file installed by hand from that distribution is recognised by
    /// its pinned hash rather than by a signature it does not carry. Without it, a manually copied
    /// d3d12.dll would look like "nothing deployed" and could never be adopted or restored. The signature
    /// check stays as a fallback, so a deployment made before records carried hashes is recognised
    /// exactly as it was before.
    /// </summary>
    private static bool IsOurProxyAt(string root, string name, DeploymentInfo? prev)
    {
        var recorded = prev?.Files.FirstOrDefault(f =>
            string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase));

        var path = Path.Combine(root, name);

        if (recorded is not null)
        {
            try
            {
                if (string.Equals(Sha256(path), recorded.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                // An unreadable file falls through to the checks below, which will also fail.
            }
        }

        return ModFetcher.IsKnownCommunityBuild(path) || IsProjectSigned(path);
    }

    /// <summary>Evaluates and applies in one call, for callers already off the UI thread.</summary>
    public static void Check(GameEntry game) => Apply(game, Evaluate(game));

    public static string? LatestLogFile(string renderDir)
    {
        var dir = Path.Combine(renderDir, ModSource.LogDirName, "logs");
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).GetFiles("*.jsonl")
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }
}
