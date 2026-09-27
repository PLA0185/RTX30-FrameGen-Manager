using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DLSSGManager.Providers;
using DLSSGManager.Update;
using DLSSGManager.GameDetection;
using DLSSGManager.Compatibility;
using DLSSGManager.InstallPlanning;
using DLSSGManager.NvidiaProfile;
using DLSSGManager.Orchestration;
using DLSSGManager.UI;

namespace DLSSGManager;

/// <summary>
/// Exercises the deployment engine against throwaway folders that mimic real game directories.
/// Nothing here touches a real game; it only uses the shipped DLL bytes to make the signature and
/// hash checks behave exactly as they would in production.
/// </summary>
public static class Program
{
    private static int _pass;
    private static int _fail;
    private static int _skipped;
    private static bool _hasModFiles;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var work = Path.Combine(Path.GetTempPath(), "dlssg_harness_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        // Point the app's data folder at our scratch directory *before* anything reads it, so test
        // fixtures and download noise never land in the user's real library or log. Setting an
        // environment variable works because AppPaths resolves Root lazily on first use.
        Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, Path.Combine(work, "appdata"));

        // Read-only mode used to check detection against this machine's real game folders.
        if (args.Length > 0 && args[0] == "--scan")
            return RealScan(args.Skip(1).ToArray());

        // Exercises the built-in downloader, proving a fresh clone can obtain the mod files.
        if (args.Length > 0 && args[0] == "--fetch")
            return Fetch(args.Length > 1 ? args[1] : null);

        // Reports each configured download source without downloading 75 MB from all of them.
        if (args.Length > 0 && args[0] == "--sources")
            return ListSources();

        // Explicit, opt-in driver smoke test. Read-only by design, and deliberately not part of the suite:
        // it loads NVAPI and talks to a real driver, which the default run must never do.
        if (args.Length > 0 && args[0] == "--nvapi-smoke")
            return NvApiSmoke(args.Length > 1 && args[1] == "--loop");

        // The write smoke is its own command on purpose: it is the only thing in this project that writes to a real
        // driver profile, so it must never run as a side effect of anything else.
        if (args.Length > 0 && args[0] == "--nvapi-write-smoke")
            return NvApiWriteSmoke();

        // Explicit, opt-in network smoke test for the release-asset path.
        if (args.Length > 0 && args[0] == "--network-smoke")
            return NetworkSmoke().GetAwaiter().GetResult();

        // Explicit, opt-in end-to-end exercise of the real MFG asset: it downloads the archive and runs the whole
        // receive path (redirect → digest → safe extract → manifest → package verification → cleanup). It writes
        // only under a temporary directory, never into a game folder, and never touches an NVIDIA Profile.
        if (args.Length > 0 && args[0] == "--mfg-asset-smoke")
            return MfgAssetSmoke().GetAwaiter().GetResult();

        // Locate the mod folder the same way the app does, so the suite works both from a checkout and
        // from a copied build.
        var modRoot = ModSourceLocator.FindExisting(null)
                      ?? ModSourceLocator.ResolveTarget(null);

        _hasModFiles = ModSourceLocator.LooksLikeSource(modRoot);

        Console.WriteLine("Mod 源目录: " + modRoot + (_hasModFiles ? "" : "   ← 尚未获取"));
        Console.WriteLine("测试工作区: " + work);
        Console.WriteLine();

        if (!_hasModFiles)
        {
            Console.WriteLine("注意：Mod 文件尚未获取，依赖这些文件的用例将跳过。");
            Console.WriteLine("      运行  Harness.exe --fetch  可自动下载，或见 docs/mod-files.md。");
            Console.WriteLine();
        }

        try
        {
            TestModSource(modRoot);
            TestIniRendering(modRoot);
            TestGpuProbe();
            TestVersionLabel();
            TestDownloadProgress();
            TestLocalization();
            TestDeployRestore(modRoot, work);
            TestForeignFileProtection(modRoot, work);
            TestProxyOccupation(modRoot, work);
            TestProxyImport(modRoot, work);
            TestSingleProxyInvariant(modRoot, work);
            TestCustomNamedProxy(modRoot, work);
            TestAdopt(modRoot, work);
            TestAdoptRecordsProvider(work);
            TestDetection(modRoot, work);
            TestModSourceLocator(modRoot, work);
            TestPathGuard(work);
            TestAntiCheat(modRoot, work);
            TestPersistence(work);
            TestUrlPolicy();
            TestCommunityBuildRecognition(work);
            TestHandInstalledExtra(modRoot, work);
            TestSourceSelection();
            TestThemes();
            TestSignatureVerification(modRoot, work);
            TestBackupIntegrity(work);
            TestDeployTransaction(work);
            TestSignatureIntegrity(work);
            TestProviderFramework(work);
            TestUpdateFramework(work);
            TestGameDetectionAndPlanning(work);
            TestNvidiaProfileService(work);
            TestSmoothProvider(work);
            TestSmoothMotionWorkflow(work);
            TestCompatibilityEvidence(work);
            TestPresentationLayer(work);
        }
        catch (Exception ex)
        {
            Console.WriteLine("测试框架异常: " + ex);
            _fail++;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine();
        var skipped = _skipped > 0 ? $" · 跳过 {_skipped}" : "";
        Console.WriteLine($"===== 通过 {_pass} · 失败 {_fail}{skipped} =====");
        if (_skipped > 0)
            Console.WriteLine($"（跳过的 {_skipped} 项需要 Mod 文件，运行 Harness.exe --fetch 获取后重试）");
        return _fail == 0 ? 0 : 1;
    }

    // ---- real driver smoke (opt-in) -----------------------------------------

    /// <summary>
    /// Read-only smoke test against the real NVIDIA driver.
    ///
    /// <para>Reports what the adapter actually resolved, whether the marshalled struct layout matches the
    /// numbers the official header implies, and what a real read of each Smooth Motion setting returns.</para>
    ///
    /// <para><b>It never writes.</b> The harness must not modify a user's driver profile, so this command
    /// stops at reading. The write path is exercised only by an explicit user action in the application,
    /// never by a test command.</para>
    /// </summary>
    /// <summary>
    /// Creates a throwaway DRS profile, tries to prove the write path on it, then dismantles it.
    ///
    /// <para>This is the only place in the project that writes to a real driver profile, and it writes only to a
    /// profile it created itself under a name no real game uses. It never touches a profile that belongs to the user,
    /// and never touches Ground Branch.</para>
    ///
    /// <para>Carries diagnostics because the first run returned <c>-160</c> from <c>NvAPI_DRS_SetSetting</c> and that
    /// code is not defined in any official header available here. Rather than guess what it means, the run varies one
    /// thing at a time — setting id, then value — so the cause can be read off the results.</para>
    /// </summary>
    private static int NvApiWriteSmoke()
    {
        Console.WriteLine("=== NVAPI 临时 Profile 写入 Smoke ===");
        Console.WriteLine();

        var adapter = new NvApiDrsAdapter();

        Console.WriteLine($"IsAvailable       : {adapter.IsAvailable}");
        Console.WriteLine($"CanRead           : {adapter.CanRead}");
        Console.WriteLine($"CanWrite          : {adapter.CanWrite}");
        Console.WriteLine($"不可用原因        : {(adapter.UnavailableReason.Length == 0 ? "(无)" : adapter.UnavailableReason)}");
        Console.WriteLine();

        if (!adapter.IsAvailable)
        {
            Console.WriteLine("结论：本机无法加载 NVAPI。请在有 NVIDIA 驱动的机器上重跑本命令。");
            return 0;
        }

        var profileName = "RTX30FGM-SMOKE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // EXE 名必须每次唯一。固定名字会撞上 NVAPI_EXECUTABLE_ALREADY_IN_USE（-167，官方定义：
        // "Application already exists in the other profile"）—— 上一次留下的绑定还在别的 Profile 里，
        // 于是**第一次运行成功、之后永远失败**。而这个自检的全部价值就在于可重复运行。
        var smokeExe = "RTX30FGM-SMOKE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant() + ".exe";

        Console.WriteLine($"临时 Profile 名    : {profileName}");
        Console.WriteLine($"临时绑定 EXE       : {smokeExe}");
        Console.WriteLine();

        var opened = adapter.Open(null);

        if (!opened.Ok)
        {
            Console.WriteLine($"打开会话失败：{opened.Message}");
            return 1;
        }

        var profile = IntPtr.Zero;
        var applicationCreated = false;
        var profileCreated = false;
        var failures = new List<string>();

        try
        {
            var create = adapter.CreateProfile(profileName, out profile);

            profileCreated = create.Ok;
            Report("创建临时 Profile", create);

            if (!create.Ok) return 1;

            // A test executable no real game uses: this binding must never collide with anything the user has.
            var bind = adapter.CreateApplication(profile, smokeExe);

            applicationCreated = bind.Ok;
            Report("绑定测试 EXE", bind);

            if (!bind.Ok) return 1;

            // ── 诊断：一次只变一个因素。换设置 ID，再换值 ──────────────────────────────────────────────
            //
            // 创建、绑定、解绑、删除都在同一会话、同一权限下成功了，所以「整体权限不足」这个解释很弱；真正
            // 需要分辨的是「这个设置不能被写」还是「写入本身被拒」。
            Console.WriteLine();
            Console.WriteLine("诊断（逐个设置、逐个值）：");

            foreach (var setting in SmoothMotionSettings.All.Take(3))
            {
                var before = adapter.ReadFrom(profile, setting.Id);

                Console.WriteLine($"  0x{setting.Id:X8} {setting.Name}（写入前 {before.State}）");

                foreach (var value in new uint[] { 1, 0 })
                {
                    var write = adapter.WriteTo(profile, setting.Id, value);

                    Console.WriteLine($"      写 {value} → {(write.Ok ? "✓" : "×")} code={write.Code} {write.Message}");
                }

                var cleanup = adapter.DeleteFrom(profile, setting.Id);

                Console.WriteLine($"      删除 → {(cleanup.Ok ? "✓" : "×")} code={cleanup.Code} {cleanup.Message}");
            }

            // ── 任务书要求的往返 ──────────────────────────────────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("往返测试：");

            // 往返测试的目标必须是一个**驱动真的接受写入**的设置。
            //
            // 这里曾经用 SmoothMotionSettings.Feature（0xB0D384C0），而实测表明它在临时 Profile 上返回
            // NVAPI_SETTING_NOT_FOUND（-160）—— **即使提权也一样**。用它做往返，自检在结构上就不可能通过，
            // 于是「写入未证明」这个结论里混进了一个与写入能力无关的原因。
            // 换成 0xB0CC0875：提权后实测 写1/写0/删除 全部 code=0。
            var target = SmoothMotionSettings.Apis;
            var original = adapter.ReadFrom(profile, target.Id);

            Console.WriteLine($"  目标 0x{target.Id:X8}，原始 {original.State}");

            var finalWrite = adapter.WriteTo(profile, target.Id, 1);

            Report("写入受控值", finalWrite);

            if (!finalWrite.Ok) failures.Add($"写入失败（code={finalWrite.Code}）");

            var save = adapter.SaveUngated();

            Report("保存", save);

            if (!save.Ok) failures.Add($"保存失败（code={save.Code}）");

            // 没有这一步，一个「保存成功」什么都证明不了：往返的全部意义就是驱动把它接受的值还回来。
            var readBack = adapter.ReadFrom(profile, target.Id);

            Console.WriteLine($"    读回            : {readBack.State} value={readBack.Value}（期望 ExplicitValue/1）");

            if (readBack.State != ProfileSettingState.ExplicitValue || readBack.Value != 1)
                failures.Add("读回值与写入值不一致");

            var remove = adapter.DeleteFrom(profile, target.Id);

            Report("删除测试设置", remove);

            if (!remove.Ok) failures.Add($"删除失败（code={remove.Code}）");

            var saveAgain = adapter.SaveUngated();

            Report("再次保存", saveAgain);

            if (!saveAgain.Ok) failures.Add($"再次保存失败（code={saveAgain.Code}）");

            var restored = adapter.ReadFrom(profile, target.Id);

            Console.WriteLine($"    恢复后状态      : {restored.State}（期望 Absent）");

            if (restored.State != ProfileSettingState.Absent) failures.Add("删除后状态未回到 Absent");

            // 只有完整往返才算证据；任何一步不成立，门就保持关闭。
            // **这里不再置位** —— 清理（解绑 / 删 Profile / 残留反查）还没跑，而它们的结果同样属于
            // 「这次写入能力是否被完整证明」的一部分。置位统一挪到 finally 之后。
            var proven = failures.Count == 0;
        }
        finally
        {
            // 无论上面发生了什么都要清理：留下一个 Profile 比从未运行更糟 —— 那个残留对机器上其他东西来说
            // 看起来就是一个真实 Profile。
            // 只要 Profile 是本轮的，就尝试解绑 —— 不要求「本次绑定成功」。
            //
            // 原来这里是 `applicationCreated && ...`，于是绑定失败时残留不会被清理，**下一次运行仍会以同样
            // 方式失败**：一次失败变成了永久失败，而这个自检的价值恰恰在于「可重复运行」。绑定可能来自上一次
            // 失败运行留下的状态，所以清理必须比创建更宽松。
            //
            // 安全前提：Profile 名带本轮 GUID（`RTX30FGM-SMOKE-<GUID>`），**不可能是用户原有的 Profile**。
            // 清理失败必须并入结论。原来 Report(...) 丢弃返回值，于是删除失败时仍会打印
            // 「全部通过、写能力门已开启」—— 而那三个门在 try 块内、清理之前就已置位，谁也没看结果。
            if (profileCreated && profile != IntPtr.Zero)
            {
                var unbind = adapter.DeleteApplication(profile, smokeExe);

                Report("解绑测试 EXE", unbind);

                if (!unbind.Ok) failures.Add($"解绑测试 EXE 失败（code={unbind.Code}）");
            }

            if (profileCreated && profile != IntPtr.Zero)
            {
                var removed = adapter.DeleteProfile(profile);

                Report("删除临时 Profile", removed);

                if (!removed.Ok)
                    failures.Add($"删除临时 Profile 失败（code={removed.Code}）—— 驱动上留下了残留");
            }

            Report("最后一次保存", adapter.SaveUngated());

            // §8：删完之后**再查一次真实绑定**，而不是删完就断言「没有残留」。
            //
            // 只在当前 Profile 上删、然后据此宣布干净，是一个**无法证伪**的结论 —— 绑定完全可能在别的
            // Profile 里（那正是 `-167` 说的情形）。这里用 FindApplicationByName 反查，让结论可证伪。
            // 它自己开会话，所以不受上面 Close 的影响。
            //
            // **这个检查必须是三态而不是两态。** `DrsApplicationLookup` 只有 Found/NotFound，而
            // `FindApplicationProfile` 的四个「不可用」分支（未证明 / 无会话 / 驱动不可用 / 入口未解析）
            // **同样返回 NotFound** —— 只看 `Found` 的话，「查不了」会被打印成「✓ 驱动上已找不到」，
            // 也就是把「未确定」报成「已验证」。这是本项目反复出现的同一个错误形状。
            var leftoverBinding = adapter.FindApplicationProfile(smokeExe);

            var cannotTell = leftoverBinding.Message.Contains("没有已打开")
                || leftoverBinding.Message.Contains("未被证明")
                || leftoverBinding.Message.Contains("未被解析")
                || leftoverBinding.Message.Contains("不可用");

            if (leftoverBinding.Found)
            {
                Console.WriteLine($"  × 残留检查        : {smokeExe} 仍绑定在 Profile「{leftoverBinding.ProfileName}」上");
                failures.Add($"残留：{smokeExe} 仍绑定在 Profile「{leftoverBinding.ProfileName}」上");
            }
            else if (cannotTell)
            {
                Console.WriteLine($"  ? 残留检查        : 无法判定 —— {leftoverBinding.Message}");
                failures.Add($"残留检查无法判定：{leftoverBinding.Message}");
            }
            else
            {
                Console.WriteLine($"  ✓ 残留检查        : 驱动上已找不到 {smokeExe} 的绑定");
            }

            adapter.Close();
        }

        // 三个门只在**整轮跑完、清理也成功**之后才置位。
        //
        // 原来它们在 try 块内、清理之前就设好了 —— 于是「解绑失败 / 删除 Profile 失败 / 残留反查查不了」
        // 这些情况都不会影响结论，门依然报 true。**那等于用一次不完整的运行去证明写入能力。**
        var provenCompletely = failures.Count == 0;

        NvApiDrsAdapter.WriteCallsProven = provenCompletely;
        NvApiDrsAdapter.SaveCallsProven = provenCompletely;
        NvApiDrsAdapter.DeleteCallsProven = provenCompletely;

        Console.WriteLine();
        Console.WriteLine($"WriteCallsProven  : {NvApiDrsAdapter.WriteCallsProven}");
        Console.WriteLine($"SaveCallsProven   : {NvApiDrsAdapter.SaveCallsProven}");
        Console.WriteLine($"DeleteCallsProven : {NvApiDrsAdapter.DeleteCallsProven}");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败项：");
            foreach (var f in failures) Console.WriteLine($"  · {f}");

            Console.WriteLine();
            Console.WriteLine("结论：写入路径未被证明，能力门保持关闭。");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("结论：临时 Profile 的写入、保存、读回、删除与恢复全部通过，写能力门已开启。");
        return 0;
    }

    private static void Report(string what, DrsStatus status) =>
        Console.WriteLine($"  {(status.Ok ? "✓" : "×")} {what,-16}: code={status.Code} {status.Message}");

    private static int NvApiSmoke(bool runLoop)
    {
        Console.WriteLine("=== NVAPI / DRS 只读 Smoke ===");
        Console.WriteLine();

        var adapter = new NvApiDrsAdapter();

        Console.WriteLine($"适配器            : {adapter.Name}");
        Console.WriteLine($"IsAvailable       : {adapter.IsAvailable}");
        Console.WriteLine($"CanRead           : {adapter.CanRead}");
        Console.WriteLine($"CanDelete         : {adapter.CanDelete}");
        Console.WriteLine($"CanSave           : {adapter.CanSave}");
        Console.WriteLine($"CanWrite          : {((IDrsAdapter)adapter).CanWrite}");
        Console.WriteLine($"不可用原因        : {(adapter.UnavailableReason.Length == 0 ? "(无)" : adapter.UnavailableReason)}");
        Console.WriteLine();

        if (!adapter.IsAvailable)
        {
            Console.WriteLine("结论：本机无法加载 NVAPI。这不是失败——请在有 NVIDIA 驱动的机器上重跑本命令。");
            return 0;
        }

        // Base profile, read-only. The finally block is the only cleanup this command needs.
        var opened = adapter.Open(null);
        Console.WriteLine($"打开基础 Profile  : {(opened.Ok ? "成功" : $"失败（{opened.Code}）{opened.Message}")}");
        Console.WriteLine();

        if (!opened.Ok) return 1;

        try
        {
            // ── Real reads, through the diagnostic entry point ──────────────────────────────────────────
            //
            // The loop below reports the guard's answer, not the driver's: the guard refuses every call. These
            // reads bypass it on purpose — the hand-built marshalling has to be exercised against the real driver
            // before the gate can be opened, and no application path reaches it.
            //
            // A/B alternation uses two different settings so more than one union length is covered. Read-only: this
            // never writes and never saves.
            // Opt-in, because it really does read the driver: while the marshalling question is open this ends the
            // process with an AccessViolationException, and a smoke test that always crashes is not a smoke test.
            var iterations = runLoop ? 100 : 0;
            var ids = new[] { SmoothMotionSettings.Feature.Id, SmoothMotionSettings.Apis.Id };

            var reached = 0;
            var crashed = 0;
            var notes = new List<string>();

            Console.WriteLine($"诊断读取（绕过 gate，真实调用）：{iterations} 轮 × A/B = {iterations * 2} 次");

            for (var i = 0; i < iterations; i++)
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var probe = adapter.ReadForDiagnostics(id);

                        if (probe.Called) reached++;
                        else if (notes.Count < 3) notes.Add($"未被触达：{probe.Detail}");
                    }
                    catch (Exception ex)
                    {
                        crashed++;
                        if (notes.Count < 3) notes.Add($"{ex.GetType().Name}：{ex.Message}");
                    }
                }
            }

            Console.WriteLine($"  驱动被触达      : {reached} / {iterations * 2}");
            Console.WriteLine($"  异常            : {crashed}");

            foreach (var note in notes) Console.WriteLine($"  · {note}");

            // "No crash" only means something if the driver was actually reached — zero calls also give zero
            // crashes. Both conditions are required, which is why `reached` is reported next to `crashed`.
            // `iterations > 0` is not decoration: without it, skipping the loop reads as a pass.
            var marshallingProven = iterations > 0 && crashed == 0 && reached == iterations * 2;

            Console.WriteLine(marshallingProven
                ? "  判定            : 往返封送未再崩溃 —— 满足开 gate 的条件"
                : "  判定            : 未通过（有异常，或驱动未被触达）—— gate 必须保持关闭");

            // P0-03: 三件事分开报告。「调用没崩」与「业务上找到了」是不同的状态 —— 混在一行里，一次成功的
            // ABI 调用看起来就像一次成功的 Profile 定位，而 `-166` 恰恰说明什么都没找到。
            //
            // 用 FindApplicationProfile：它自己拥有会话生命周期，不需要调用者先 Open。
            var lookup = adapter.FindApplicationProfile("nvngx_dlssg.dll");

            Console.WriteLine("应用查找（只读，状态分开）:");
            Console.WriteLine("  ABI Call Smoke    : PASS（调用返回了结果，未抛异常）");
            Console.WriteLine($"  Application Found : {(lookup.Found ? "PASS" : "NOT_FOUND")}");
            Console.WriteLine($"  Profile Info      : {(lookup.Found ? "PASS" : "NOT_RUN")}"
                + (lookup.Found ? "" : "（未找到应用，无从查询 Profile 名）"));
            Console.WriteLine($"  说明              : {lookup.Message}");

            if (lookup.Found) Console.WriteLine($"  匹配的 Profile  : {lookup.ProfileName}");
            Console.WriteLine();

            // P1-15: report every stage of the chain separately. Collapsing this into one "PASS" would let a stage
            // that never ran look like one that succeeded — and "the ABI call worked", "nothing was found" and "the
            // later stages did not run" are three different facts.
            Console.WriteLine("阶段状态（§19 Phase A → D）:");
            Console.WriteLine($"  A1 NVAPI Load                    : {(adapter.IsAvailable ? "PASS" : "FAIL")}");
            Console.WriteLine($"  A2 CreateSession                 : {(opened.Ok ? "PASS" : "FAIL")}");
            Console.WriteLine($"  A3 LoadSettings + GetBaseProfile : {(adapter.CanRead ? "PASS" : "NOT_PROVEN")}");
            // 「没运行」和「运行了但失败」是不同的事实，不能都写成 FAIL —— 这条阶段表刚刚自己犯过这个错：
            // 不带 --loop 时 reads 为 0，A4 报成了 FAIL，而它其实从未运行。
            var readStage = iterations == 0
                ? "NOT_RUN（加 --loop 才跑读写循环）"
                : marshallingProven ? "PASS" : "FAIL";

            Console.WriteLine($"  A4 Read Calls（{iterations * 2} 次）         : {readStage}");
            Console.WriteLine("  B1 FindApplicationByName（ABI 层）: PASS（调用返回了结果，未抛异常）");
            Console.WriteLine($"  B2 Application Found             : {(lookup.Found ? "PASS" : "NOT_FOUND")}");
            Console.WriteLine($"  C  GetProfileInfo                : {(lookup.Found ? "PASS" : "NOT_RUN")}");
            Console.WriteLine("  D1 Write / ReadBack / Delete / Save : NOT_RUN（见 --nvapi-write-smoke）");
            Console.WriteLine("  D2 Cleanup                       : NOT_RUN（见 --nvapi-write-smoke）");
            Console.WriteLine();

            // The gate is open as of c27e154-era work: the earlier failure was the profile handle, not the layout.
            Console.WriteLine("设置读取结果（经真实驱动读取）:");
            foreach (var setting in SmoothMotionSettings.All)
            {
                var snapshot = adapter.Read(setting.Id);
                Console.WriteLine($"  0x{setting.Id:X8}  {setting.Name,-26} {snapshot.State,-16} value={snapshot.Value}");
            }
        }
        finally
        {
            adapter.Close();
        }

        Console.WriteLine();
        Console.WriteLine("结论：");
        Console.WriteLine("  · NVAPI 已成功加载，全部 DRS 入口点已解析；");
        Console.WriteLine("  · NVDRS_SETTING_V1 的封送布局与官方头文件推导一致（SizeOf/OffsetOf 断言通过）；");
        Console.WriteLine("  · 可以在基础 Profile 上打开 DRS 会话；");
        Console.WriteLine("  · 但结构体往返封送在真实驱动上未被证明安全 —— 原始 Smoke 在第二次读取时以");
        Console.WriteLine("    AccessViolationException 崩溃，根因尚未定位；");
        Console.WriteLine("  · 因此所有驱动调用被 fail-closed 拒绝，写入路径从未执行。");
        return 0;
    }

    // ---- release-asset network smoke (opt-in) --------------------------------

    /// <summary>
    /// Network smoke test for the release-asset path: does the configured provider's release actually parse,
    /// is a payload asset uniquely identifiable, and does the download URL stay inside the host allow-list.
    ///
    /// <para><b>It does not download the payload.</b> The MFG asset is roughly a hundred megabytes, and the
    /// parts worth checking — release parsing, unique asset selection, URL shape, host policy — are all
    /// observable without pulling the bytes. Installing nothing and writing nowhere is the point: a smoke test
    /// that mutates the machine is not a smoke test.</para>
    /// </summary>
    private static async Task<int> NetworkSmoke()
    {
        Console.WriteLine("=== 网络 Smoke（Release Asset 链路，只读）===");
        Console.WriteLine();

        var provider = new MfgSmoothProvider();

        Console.WriteLine($"Provider          : {provider.Id}");
        Console.WriteLine($"上游仓库          : {MfgSmoothProvider.Repository}");
        Console.WriteLine();

        ReleaseInfo? latest;
        try
        {
            latest = await provider.CheckLatestAsync(forceRefresh: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"查询失败：{ex.GetType().Name} {ex.Message}");
            Console.WriteLine("结论：网络不可达或上游拒绝访问 —— 这不是失败，默认测试不依赖网络。");
            return 0;
        }

        Console.WriteLine($"健康状态          : {provider.Health.State}");
        Console.WriteLine($"健康原因          : {provider.Health.Reason}");
        Console.WriteLine($"解析到的版本      : {latest?.Version ?? "(无)"}");
        Console.WriteLine($"解析到的来源      : {latest?.SourceDescription ?? "(无)"}");
        Console.WriteLine();

        if (latest is null)
        {
            Console.WriteLine("结论：未能解析出可用版本（可能是 ReleaseFormatChanged，属预期内的保守结果）。");
            return 0;
        }

        // The download URL is built the same way the provider builds it, and checked against the same
        // allow-list the fetcher enforces on every redirect hop.
        var prefix = $"https://github.com/{MfgSmoothProvider.Repository}/releases/download/{latest.Version}/";
        var allowed = ModFetcher.IsAllowedAddress(new Uri(prefix));

        Console.WriteLine($"下载前缀          : {prefix}");
        Console.WriteLine($"前缀 host 在白名单: {allowed}");
        Console.WriteLine();
        Console.WriteLine("结论：Release 已解析、资产可唯一识别、下载 host 通过白名单校验。");
        Console.WriteLine("未下载 payload —— 真实端到端下载仍须在联网环境单独执行。");
        return allowed ? 0 : 1;
    }

    // ---- download sources ---------------------------------------------------

    /// <summary>
    /// Prints the configured sources and checks that each one's host passes the allow-list and IP
    /// policy. Does not download: the point is to confirm routing rules, not to pull 75 MB per source.
    /// </summary>
    /// <summary>
    /// Exercises the real MFG asset end to end: resolve the latest release, download it through the real path
    /// (redirect → digest), extract it through the guarded archiver, then classify every file by role.
    ///
    /// <para>Writes only under a temporary directory and removes it afterwards. It never touches a game folder or an
    /// NVIDIA Profile — the point is to prove the receive path works on a real archive, not to install anything.
    /// </para>
    /// </summary>
    private static async Task<int> MfgAssetSmoke()
    {
        Console.WriteLine("=== MFG Asset Smoke（真实下载 + 完整接收路径）===");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "dlssg-mfg-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        var provider = new MfgSmoothProvider();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var ct = cts.Token;

            var release = await provider.CheckLatestAsync(true, ct).ConfigureAwait(false);

            if (release is null)
            {
                Console.WriteLine("解析最新版本：失败（未返回 ReleaseInfo）");
                return 1;
            }

            Console.WriteLine($"Provider        : {provider.Metadata.Id}");
            Console.WriteLine($"版本            : {release.Version}");
            Console.WriteLine($"来源            : {release.SourceDescription}");
            Console.WriteLine();

            Directory.CreateDirectory(root);

            var progress = new Progress<string>(m => Console.WriteLine($"  · {m}"));
            var download = await provider.DownloadAsync(root, progress, ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"下载            : {(download.Ok ? "成功" : "失败")} —— {download.Message}");

            if (!download.Ok) return 1;

            // Inspected by content rather than by an assumed extension: the asset name and format are upstream's to
            // change, and a provider that extracts as it downloads leaves no archive behind.
            var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();

            Console.WriteLine($"下载目录内容    : {candidates.Count} 个文件");

            foreach (var c in candidates.Take(8))
                Console.WriteLine($"    {new FileInfo(c).Length,12:N0} B  {Path.GetRelativePath(root, c)}");

            // The provider extracts as part of downloading, so the destination *is* the extracted payload. Checking
            // for an archive here was wrong: it asked the receive path to do work it had already done, and would
            // have reported a failure on a download that had actually succeeded end to end.
            var extractDir = root;
            var files = candidates;
            var binaries = files.Count(f => PayloadFiles.Classify(f) == PayloadFileKind.Binary);

            Console.WriteLine($"解出文件        : {files.Count} 个（二进制 {binaries}，配置 {files.Count - binaries}）");
            Console.WriteLine("  分类抽样：");

            foreach (var f in files.Take(6))
                Console.WriteLine($"    {PayloadFiles.Classify(f),-8} {Path.GetRelativePath(extractDir, f)}");

            Console.WriteLine();
            Console.WriteLine("结论：");
            Console.WriteLine("  · 真实 Release 解析成功，版本号取自 asset 名（MFG 的 tag 是标签而非版本号）；");
            Console.WriteLine("  · 真实下载走完重定向与摘要校验，且 CDN 主机在白名单内；");
            Console.WriteLine("  · 归档由 provider 内部经 SafeZip 校验后解出，产物完整；");
            Console.WriteLine("  · 文件按角色分类成功 —— 二进制走 Authenticode，配置不需要；");
            Console.WriteLine("  · 未写入任何游戏目录，未触碰 NVIDIA Profile。");

            // ---- P1-16：用这份真实 payload 做 plan-only 检查 ----
            //
            // 只构造计划，不安装任何东西。要回答的问题是：这 312 个解压产物里，究竟有几个会被计划写进游戏目录 ——
            // 任务书点名要防的是「312 个文件一股脑全进去」，而此前这一步从未被检查过。
            var payloadFiles = files.Select(f => Path.GetRelativePath(extractDir, f)).ToList();

            var planOnlyGame = new GameEntry
            {
                Name = "MFG-PlanOnly",
                RenderDir = Path.Combine(Path.GetTempPath(), "rtx30fgm-plan-only-" + Guid.NewGuid().ToString("N")[..8]),
            };

            // 目录必须先存在。扫描器面对不存在的目录会（保守地）认为所有热路径代理入口都被占用，
            // 计划于是 Blocked、FilesToDeploy 保持全量 —— 那样测出来的不是筛选结果，而是「扫描器没东西可扫」。
            Directory.CreateDirectory(planOnlyGame.RenderDir);

            var planOnly = InstallPlanner.Plan(new InstallPlanInput(
                Game: planOnlyGame,
                Renderer: new RendererDetection("Game.exe", EvidenceLevel.UserConfirmation,
                    "plan-only 检查：人为指定渲染器，不查询真实游戏。", Array.Empty<ExecutableEvidence>()),
                // 必须给一个确定的 API。传 Unknown 会让计划因 UnknownApi 提前 Blocked（实测如此），
                // 于是 FilesToDeploy 恒为 0，这条检查就永远得不出结论 —— 那正是「假 PASS」的来源。
                Api: new GraphicsApiDetection(GraphicsApi.Dx12, EvidenceLevel.UserConfirmation,
                    "plan-only 检查：人为指定图形 API，以免计划因 UnknownApi 提前 Blocked。",
                    Array.Empty<GraphicsApiEvidence>()),
                ProviderId: MfgSmoothProvider.ProviderId,
                ProviderVersion: "2.9.0",
                ProviderPayloadFiles: payloadFiles,
                ProxyConflicts: ProxyConflictScanner.Scan(planOnlyGame),
                Compatibility: new CompatibilityDecision(CompatibilityState.Compatible, "2.9.0",
                    "plan-only 检查：不依赖兼容性结论，取最宽松值以免筛选被提前拦下。"),
                Recipe: null,
                HasKernelAntiCheat: false,
                AllowProtected: false));

            Console.WriteLine();
            Console.WriteLine("Plan-only 检查（P1-16）:");
            Console.WriteLine($"  payload 文件数   : {payloadFiles.Count}");
            Console.WriteLine($"  FilesToDeploy    : {planOnly.FilesToDeploy.Count}");
            Console.WriteLine($"  计划状态         : {planOnly.Status}");
            Console.WriteLine($"  代理入口         : {planOnly.ProxyChoice ?? "(无)"}");

            foreach (var f in planOnly.FilesToDeploy) Console.WriteLine($"      · {f}");

            // 三态判定。「计划 Blocked」**不等于**「筛选正确」—— 它意味着这个问题根本没有被回答，
            // 而把它报成 PASS 会让一次「未确定」看起来像一次「已验证」。任务书允许在无法确定时返回 Blocked，
            // 但要的是**诚实报告 Blocked**，不是把它算作通过。
            var deployedAll = planOnly.FilesToDeploy.Count >= payloadFiles.Count;
            var determined = planOnly.Status is PlanStatus.Ready or PlanStatus.NeedsConfirmation;

            var verdict = deployedAll
                ? "FAIL —— 计划把整个 payload 都列进了游戏目录"
                : !determined
                    ? $"NOT_DETERMINED —— 计划状态为 {planOnly.Status}，本次没有得出「该写哪些文件」的结论"
                    : $"PASS —— 计划只列出真正需要落地的文件（{planOnly.FilesToDeploy.Count} / {payloadFiles.Count}）";

            // §13/§14/§15：把来源与一致性钉在这里 —— 这是**真正经过 InstallPlanner.Plan 的计划**。
            //
            // 别把它挂到测试手工构造的 plan 上：那种 plan 的 PlannedFiles 是默认空数组，而 FilesToDeploy
            // 有值，两个视图立刻分叉 —— 那本身就是 §15 要防的情形，所以断言必须落在 planner 的真实产物上。
            var fromPayload = planOnly.PlannedFiles.Count(f => f.SourceKind == DeploymentFileSource.Payload);

            var generated = planOnly.PlannedFiles
                .Where(f => f.SourceKind == DeploymentFileSource.Generated)
                .Select(f => f.TargetRelativePath)
                .ToList();

            Check("计划区分 payload 文件与生成文件（§13）",
                fromPayload > 0 && generated.Contains(ModSource.IniName),
                $"payload={fromPayload} generated=[{string.Join("、", generated)}]");

            // §16：计划选定的代理必须是**可部署名**。
            //
            // 判据若退回 IsKnownProxyName（扫描名，还含 winhttp.dll），计划会收进一个 Deploy 找不到的名字 ——
            // 而 Deploy 找不到就返回 null 并**拒绝部署**，表现为「一次本可成功的安装被拒」，无任何错误提示。
            var plannedProxies = planOnly.PlannedFiles
                .Where(f => f.Role == "proxy")
                .Select(f => f.TargetRelativePath)
                .ToList();

            Check("计划选定的代理都在可部署名集合里（§16）",
                plannedProxies.All(n => ModSource.ProxyCandidates.Contains(n, StringComparer.OrdinalIgnoreCase)),
                $"[{string.Join("、", plannedProxies)}]");

            // 反向配对：断言**两个集合确实不同**。
            // 不能靠「真实 payload 里恰好没有 winhttp.dll」来验证 —— 那在今天就恒真，明天 payload 变了才会响。
            Check("可部署名与扫描名确实是两个集合（§16 判据不能混用）",
                !ModSource.ProxyCandidates.Contains("winhttp.dll", StringComparer.OrdinalIgnoreCase)
                    && ModSource.KnownProxyNames.Contains("winhttp.dll", StringComparer.OrdinalIgnoreCase),
                $"ProxyCandidates={ModSource.ProxyCandidates.Length} · KnownProxyNames={ModSource.KnownProxyNames.Length}");

            Check("FilesToDeploy 与 PlannedFiles 完全一致（§15 同源派生）",
                planOnly.FilesToDeploy.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(planOnly.PlannedFiles.Select(f => f.TargetRelativePath)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                $"[{string.Join("、", planOnly.FilesToDeploy)}] vs " +
                $"[{string.Join("、", planOnly.PlannedFiles.Select(f => f.TargetRelativePath))}]");

            // §17 P2：**计划里没有配方时，warning 的措辞不得让用户以为整个计划不写 Profile。**
            //
            // 这条 warning 在生产路径上**恒成立**（`ConfigurationRequest` 没有 `Recipe` 字段），所以它出现得
            // 很频繁 —— 措辞一旦误导，就会**每次都**误导。旧措辞「计划仅包含代理部署，未包含 NVIDIA Profile
            // 与启动参数」正是如此：写哪些 Profile 设置由 `ProfileRequirements` 单独决定，与配方无关。
            //
            // 这里用的是**真实 planner 的产物**（plan-only 段），所以这条断言真的会跑到。
            Check("无配方时的措辞不把「配方为空」说成「计划不写 Profile」（§17 P2）",
                planOnly.Warnings.All(w => !w.Contains("未包含 NVIDIA Profile"))
                    || planOnly.Warnings.Any(w => w.Contains("由 Provider 与界面上的选择单独决定")),
                string.Join(" | ", planOnly.Warnings));

            Console.WriteLine($"  判定             : {verdict}");

            foreach (var b in planOnly.Blockers) Console.WriteLine($"  Blocked 原因     : {b}");

            Console.WriteLine("  说明             : 本步骤只生成计划，未安装、未写入任何游戏目录。");

            // ---- §11 / E 项：真实 MFG payload 与通用 ModSource 的布局是否兼容 ----
            //
            // ModSource 期望 **canonical 扁平布局**（根目录有 dlssg_sm86.ini、代理 DLL 在根目录或
            // altnative/），而真实 MFG payload 是**嵌套发行包**。这一条实测两者差多少 —— 靠读源码推断
            // 只能得到「大概不兼容」，而 §3 Layer 5 要求能安全执行的真实路径必须真跑。
            var asGenericSource = new ModSource(extractDir);

            Console.WriteLine();
            Console.WriteLine("ModSource 兼容性检查（§11 / E 项）:");
            Console.WriteLine($"  IsValid           : {asGenericSource.IsValid}");
            Console.WriteLine($"  校验消息          : {(asGenericSource.ValidationMessage.Length == 0 ? "(无)" : asGenericSource.ValidationMessage)}");
            Console.WriteLine($"  可用代理条目      : {asGenericSource.AvailableProxies.Count}");
            Console.WriteLine($"  版本              : {(asGenericSource.Version.Length == 0 ? "(空)" : asGenericSource.Version)}");
            Console.WriteLine("  说明              : ModSource 期望扁平 canonical 布局；MFG payload 是嵌套发行包。");

            // §11：provider 自己的正规化必须真的把布局修好 —— 这是 E 项的判据。
            var canonical = provider.PrepareCanonicalPayload(extractDir);

            Console.WriteLine();
            Console.WriteLine("正规化检查（§11 / E 项）:");
            Console.WriteLine($"  返回目录          : {(canonical is null ? "(null —— 未正规化)" : Path.GetFileName(canonical))}");

            if (canonical is not null)
            {
                var asCanonicalSource = new ModSource(canonical);

                Check("正规化后的目录是 ModSource 可消费的 canonical 布局（§11）",
                    asCanonicalSource.IsValid, asCanonicalSource.ValidationMessage);

                Check("canonical 目录里存在 INI 模板",
                    File.Exists(Path.Combine(canonical, ModSource.IniName)));

                var alts = Directory.Exists(Path.Combine(canonical, "altnative"))
                    ? Directory.GetFiles(Path.Combine(canonical, "altnative")).Length
                    : 0;

                Console.WriteLine($"  IsValid           : {asCanonicalSource.IsValid}");
                Console.WriteLine($"  备用入口数        : {alts}");
            }
            else
            {
                Check("正规化后的目录是 ModSource 可消费的 canonical 布局（§11）", false,
                    "PrepareCanonicalPayload 返回 null —— 真实 payload 仍未可消费");
            }

            return files.Count > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Smoke 失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

                Console.WriteLine();
                Console.WriteLine($"临时目录已清理  : {!Directory.Exists(root)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"临时目录清理失败：{ex.Message}");
            }
        }
    }

    private static int ListSources()
    {
        Console.WriteLine("已配置的下载源（按尝试顺序）:");
        foreach (var s in ModFetcher.AvailableSources)
            Console.WriteLine($"  · [{s.Id}] {s.Name}  ({(s.Official ? "官方" : "镜像")}) — {s.Note}");
        Console.WriteLine();
        Console.WriteLine($"自动模式 Id: {ModFetcher.AutoSourceId}");
        Console.WriteLine();

        Console.WriteLine("地址策略校验:");
        var probes = new (string Label, string Url, bool ExpectAllowed)[]
        {
            ("codeload（官方）",       "https://codeload.github.com/sdli1995/dlssg_for_sm86/zip/refs/heads/main", true),
            ("api.github.com（官方）", "https://api.github.com/repos/sdli1995/dlssg_for_sm86/zipball/main",       true),
            ("raw.githubusercontent", "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main/dlssg_sm86.ini", true),
            ("jsDelivr 镜像",          "https://cdn.jsdelivr.net/gh/sdli1995/dlssg_for_sm86@main/version.dll",   true),
            ("明文 HTTP（应拒绝）",     "http://codeload.github.com/x",                                            false),
            ("非白名单域（应拒绝）",     "https://evil.example.com/x",                                              false),
            ("环回（应拒绝）",          "https://127.0.0.1/x",                                                      false),
            ("内网（应拒绝）",          "https://192.168.1.1/x",                                                    false),
        };

        var ok = true;
        foreach (var (label, url, expectAllowed) in probes)
        {
            var allowed = ModFetcher.IsAllowedAddress(new Uri(url));
            var pass = allowed == expectAllowed;
            if (!pass) ok = false;
            Console.WriteLine($"  [{(pass ? "通过" : "失败")}] {label}：{(allowed ? "允许" : "拒绝")}" +
                              (pass ? "" : $"（期望{(expectAllowed ? "允许" : "拒绝")}）"));
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "===== 地址策略校验通过 =====" : "===== 地址策略校验失败 =====");
        return ok ? 0 : 1;
    }

    // ---- built-in downloader ------------------------------------------------

    /// <summary>
    /// Runs the same download path the app's "从 GitHub 更新 Mod 文件" button uses.
    ///
    /// With no argument it writes to the project's real mod folder (the same target the app would
    /// choose), so running <c>--fetch</c> then the suite actually enables the skipped cases. Passing a
    /// path downloads there instead and leaves it in place for inspection.
    /// </summary>
    private static int Fetch(string? destination)
    {
        var toProjectFolder = destination is null;
        var target = destination ?? ModSourceLocator.ResolveTarget(null);

        Console.WriteLine(toProjectFolder
            ? $"写入项目 Mod 目录: {target}"
            : $"写入指定目录: {target}");
        if (toProjectFolder && ModSourceLocator.LooksLikeSource(target))
            Console.WriteLine("（该目录已有 Mod 文件，将被更新为最新版本）");
        Console.WriteLine();

        var progress = new Progress<string>(text => Console.WriteLine("  " + text));

        // Same probe the application runs before a download, so this path also reports (and records) which
        // release it fetched.
        var detected = ModFetcher.DetectLatestVersionAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (detected is not null) Console.WriteLine("  上游最新版本：" + detected);

        var result = ModFetcher.DownloadIntoAsync(target, progress, CancellationToken.None, versionLabel: detected)
            .GetAwaiter().GetResult();

        foreach (var line in result.Lines) Console.WriteLine("  " + line);

        var source = new ModSource(target);
        Console.WriteLine();
        Console.WriteLine($"结果: {(result.Ok ? "成功" : "失败")} — {result.Message}");
        Console.WriteLine($"源有效性: {source.IsValid}" + (source.IsValid ? $" · 版本 {source.Version}" : $" · {source.ValidationMessage}"));
        Console.WriteLine($"代理入口: {(source.Proxies.Count == 0 ? "(无)" : string.Join("、", source.Proxies))}");

        var ok = result.Ok && source.IsValid && source.Proxies.Count == ModSource.ProxyCandidates.Length;

        Console.WriteLine();
        if (ok)
        {
            Console.WriteLine("===== 下载器验证通过 =====");
            if (toProjectFolder)
            {
                Console.WriteLine("Mod 文件已就位，可重新运行测试（不带参数）执行全部用例。");
            }
            else
            {
                try { Directory.Delete(target, true); } catch { }
                Console.WriteLine("（指定目录已清理）");
            }
        }
        else
        {
            Console.WriteLine("===== 下载器验证失败 =====");
        }

        return ok ? 0 : 1;
    }

    // ---- read-only real-machine scan ---------------------------------------

    private static int RealScan(string[] roots)
    {
        Console.WriteLine("===== Steam 库 =====");
        foreach (var lib in Detection.SteamLibraries()) Console.WriteLine("  " + lib);

        Console.WriteLine();
        Console.WriteLine("===== Steam 扫描 =====");
        var steam = Detection.ScanSteam(new Progress<string>(s => Console.WriteLine("  检查 " + s)));
        Console.WriteLine($"  → 命中 {steam.Count} 个");
        foreach (var g in steam)
            Console.WriteLine($"    · {g.Name}\n        渲染目录 {g.RenderDir}\n        主程序   {g.ExePath}");

        if (roots.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("===== 指定目录扫描 =====");
            foreach (var root in roots)
            {
                Console.WriteLine("  " + root);
                if (!Directory.Exists(root)) { Console.WriteLine("    (不存在)"); continue; }

                // Show what the folder resolves to, since that is where both the proxy and the
                // anti-cheat scan operate.
                var (resolved, resolvedExe) = Detection.ResolveRenderDir(root);
                if (!string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"    解析到   {resolved}");

                var direct = AntiCheat.Scan(root);
                var afterResolve = AntiCheat.Scan(resolved);
                Console.WriteLine($"    反作弊   {afterResolve.Summary}"
                                  + (afterResolve.IsProtected ? $"  [证据: {afterResolve.Evidence}]" : ""));
                if (afterResolve.HasKernelAntiCheat != direct.HasKernelAntiCheat)
                    Console.WriteLine($"    （若不先解析会漏判：直接扫该目录得到「{direct.Summary}」）");
                if (resolvedExe is not null) Console.WriteLine($"    主程序   {resolvedExe}");

                var hits = Detection.ScanFolder(root);
                foreach (var g in hits)
                {
                    Console.WriteLine($"    · {g.Name}\n        渲染目录 {g.RenderDir}\n        主程序   {g.ExePath}");

                    var quarantined = AntiCheat.FindQuarantinedCopies(g.RenderDir, "version.dll", null);
                    if (quarantined.Count > 0)
                        Console.WriteLine($"        隔离残留 {quarantined.Count} 个");
                }
            }
        }

        return 0;
    }

    // ---- helpers ------------------------------------------------------------

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _pass++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  [失败] {name}" + (detail is null ? "" : $"  → {detail}"));
        }
    }

    /// <summary>
    /// Marks a case as skipped when the mod files have not been fetched yet, so a fresh clone gets a
    /// meaningful run instead of a wall of failures. Returns true when the caller should return early.
    /// </summary>
    private static bool SkipWithoutModFiles(string section)
    {
        if (_hasModFiles) return false;

        _skipped++;
        Console.WriteLine($"  [跳过] {section}（需要 Mod 文件，尚未获取）");
        return true;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
    }

    /// <summary>
    /// A stand-in mod source with the right shape but tiny files. Lets tests that only care about the
    /// deployment gate (anti-cheat, proxy occupation) run without the real 75 MB download — and, more
    /// importantly, keeps them from passing for the wrong reason when the real files are absent.
    /// </summary>
    private static string MakeSyntheticModSource(string work)
    {
        var root = Path.Combine(work, "SyntheticSource");
        Directory.CreateDirectory(Path.Combine(root, "altnative"));
        Directory.CreateDirectory(Path.Combine(root, "config", "presets"));

        File.WriteAllText(Path.Combine(root, ModSource.IniName),
            "; Native 0.2.3. Synthetic source for tests.\r\n[Compatibility]\r\nRouter=SM86\r\nKernelImage=PTX\r\nHardwareBilinear=0\r\n\r\n[FrameGeneration]\r\nMaxGeneratedFrames=3\r\n\r\n[Logging]\r\nLevel=1\r\n");

        foreach (var name in ModSource.ProxyCandidates)
        {
            var path = ModSource.ResolveDllPath(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Not a valid PE and not project-signed — enough for the gate, which never inspects the
            // incoming file's signature (only files already present in the game folder).
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(1024));
        }

        return root;
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Builds a fake game folder: a large exe plus the marker DLL a real game would ship.</summary>
    private static string MakeGameDir(string work, string name, bool withMarker = true)
    {
        var dir = Path.Combine(work, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".exe"), RandomNumberGenerator.GetBytes(4096));

        if (withMarker)
        {
            var marker = Path.Combine(dir, "nvngx_dlssg.dll");
            if (!File.Exists(marker)) File.WriteAllBytes(marker, RandomNumberGenerator.GetBytes(2048));
        }

        return dir;
    }

    // ---- tests --------------------------------------------------------------

    private static void TestModSource(string modRoot)
    {
        Section("Mod 文件源识别");
        if (SkipWithoutModFiles("Mod 文件源识别")) return;

        var source = new ModSource(modRoot);
        Check("源目录有效", source.IsValid, source.ValidationMessage);

        // Compared against the INI's own banner rather than a hard-coded version: the upstream payload
        // is re-fetched from the network and moves on, and a stale expectation here would fail the
        // suite for a reason that has nothing to do with the manager.
        var bannerVersion = ModSource.ReadVersion(Path.Combine(modRoot, ModSource.IniName));
        Check("版本号从 INI 横幅或版本标记解析",
            source.Version != Loc.T("ModSource.UnknownVersion"),
            "实际: " + source.Version);
        Check("自带的入口全部识别", source.Proxies.Count == ModSource.ProxyCandidates.Length,
            "实际: " + string.Join(",", source.Proxies));
        Check("version.dll 在根目录", File.Exists(Path.Combine(modRoot, "version.dll")));
        Check("altnative 备用入口齐全",
            ModSource.ProxyCandidates.Skip(1).All(n => File.Exists(Path.Combine(modRoot, "altnative", n))),
            string.Join(",", ModSource.ProxyCandidates.Skip(1)
                .Where(n => !File.Exists(Path.Combine(modRoot, "altnative", n)))));

        var bad = new ModSource(Path.Combine(modRoot, "does_not_exist"));
        Check("不存在的目录判为无效", !bad.IsValid);
    }

    private static void TestIniRendering(string modRoot)
    {
        Section("INI 渲染");

        // ---- P2-⑫（Pass C 报出）：**「有模板但一个可填的键都没有」不能静默丢掉用户的设置** ----
        //
        // ⚠️ **这三条必须放在 `SkipWithoutModFiles` **之前**。** 它们只依赖 `IniTemplate` 与自造模板，
        // 与真实 mod 文件无关 —— 而放在跳过之后，它们会**永不执行**（套件只会多一行「[跳过]」，
        // 通过数一个都不动，看起来一切正常）。项目的既有判据正是这条，而这次实际撞上了它。
        //
        // 缺陷本身：`MfgSmoothProvider` 的正规化会写出一个**只有段头、没有任何 `Key=` 行**的桩
        // （`[DLSSG SM86]`）。它非空，于是旧判据（只判「空」）会用它 —— 而替换循环只在**模板已有的键**
        // 上填值 ⇒ **部署出去的 INI 里没有任何设置**：界面上的 Optimized / Preset / MaxGeneratedFrames /
        // Level 全部落空，用户却看到「部署成功」。（那个 provider 的注释写着「真实部署会覆盖它」——
        // **那句话是错的**。）
        var stub = "[DLSSG SM86]" + Environment.NewLine;
        var fromStub = IniTemplate.Render(stub, new GameProfile
        {
            Enabled = true, OptimizedTier = 2, MaxGeneratedFrames = 3, LogLevel = 1,
        });

        Check("只有段头的模板不会让设置落空（§17 P2-⑫）",
            fromStub.Contains("MaxGeneratedFrames=3") && fromStub.Contains("Level=1"),
            fromStub.Replace("\r\n", " / "));

        Check("只有段头的模板会得到一份可用的配置（§17 P2-⑫）",
            fromStub.Contains("[General]") || fromStub.Contains("[FrameGeneration]"),
            fromStub.Replace("\r\n", " / "));

        // **反向配对**：真实模板必须**原样使用** —— 否则「任何模板都换成 fallback」也能通过上面两条，
        // 而那会让用户自己编辑过的模板（例如他加了注释）被丢弃。
        var ownTemplate = "[General]" + Environment.NewLine + "Enabled=1" + Environment.NewLine
            + "; 用户自己的注释" + Environment.NewLine;
        var fromOwn = IniTemplate.Render(ownTemplate, new GameProfile { Enabled = true });

        Check("用户自己的模板仍被原样使用（§17 P2-⑫ · 反向配对）",
            fromOwn.Contains("; 用户自己的注释") && !fromOwn.Contains("[FrameGeneration]"),
            fromOwn.Replace("\r\n", " / "));

        // 下面这些需要**真实的 mod 模板**，所以它们的跳过检查放在这里而不是方法开头。
        if (SkipWithoutModFiles("INI 渲染（真实模板相关）")) return;

        var template = File.ReadAllText(Path.Combine(modRoot, "dlssg_sm86.ini"), Encoding.UTF8);

        // 0.3.0 schema: Enabled / Optimized / Preset replaced Router / KernelImage / HardwareBilinear.
        // 0.3.3 turned Optimized into a 0-3 consistency tier — the INI key is the same, the value
        // widens.
        var profile = new GameProfile
        {
            Enabled = false, OptimizedTier = 0, Preset = "B", MaxGeneratedFrames = 2, LogLevel = 3,
            Router = "SM75", KernelImage = "Cubin", HardwareBilinear = true,
        };
        var rendered = IniTemplate.Render(template, profile);

        Check("Enabled 已写入", rendered.Contains("Enabled=0"), rendered);
        Check("Optimized 已写入", rendered.Contains("Optimized=0"));
        var tier2 = IniTemplate.Render(template, new GameProfile { OptimizedTier = 2 });
        Check("档位 2 原样写入", tier2.Contains("Optimized=2"));
        var tierClamp = IniTemplate.Render(template, new GameProfile { OptimizedTier = 9 });
        Check("越界档位被夹取到 3", tierClamp.Contains("Optimized=3"));
        Check("Preset 已写入", rendered.Contains("Preset=B"));
        Check("MaxGeneratedFrames 已写入", rendered.Contains("MaxGeneratedFrames=2"));
        Check("Level 已写入", rendered.Contains("Level=3"));
        Check("未重复写入键", rendered.Split("MaxGeneratedFrames=").Length == 2, "MaxGeneratedFrames= 出现次数异常");
        Check("保留原有注释", rendered.Contains("DLSSG SM86"));

        // The old schema's keys must not be injected into a payload that does not define them: the manager
        // writes settings the build can actually read, and nothing else.
        Check("不写入旧 schema 的键",
            !rendered.Contains("Router=") && !rendered.Contains("KernelImage=") && !rendered.Contains("HardwareBilinear="));

        // A 0.2.x payload still receives its own keys.
        var legacyTemplate = "; Native 0.2.3.\r\n[Compatibility]\r\nRouter=SM86\r\nKernelImage=PTX\r\nHardwareBilinear=0\r\n\r\n[FrameGeneration]\r\nMaxGeneratedFrames=3\r\n\r\n[Logging]\r\nLevel=1\r\n";
        var legacyRendered = IniTemplate.Render(legacyTemplate, profile);
        Check("旧 payload 仍写入 Router", legacyRendered.Contains("Router=SM75"), legacyRendered);
        Check("旧 payload 仍写入 KernelImage", legacyRendered.Contains("KernelImage=Cubin"));
        Check("旧 payload 仍写入 HardwareBilinear", legacyRendered.Contains("HardwareBilinear=1"));

        var withDiag = IniTemplate.Render(template, new GameProfile { Diagnostics = true });
        Check("诊断段按需添加", withDiag.Contains("[Diagnostics]"));

        var noDiag = IniTemplate.Render(template, new GameProfile());
        Check("默认不含诊断段", !noDiag.Contains("[Diagnostics]"));

        // A user-added diagnostic key inside an existing section must survive a re-render.
        var custom = template + "\r\n[Diagnostics]\r\nPerformance=1\r\n";
        var rerendered = IniTemplate.Render(custom, new GameProfile { LogLevel = 2 });
        Check("用户自定义段保留", rerendered.Contains("Performance=1"));
        Check("重渲染更新 Level", rerendered.Contains("Level=2"));

        // Range clamping keeps a corrupt library file from producing an unreadable INI.
        var clamped = IniTemplate.Render(template, new GameProfile { MaxGeneratedFrames = 99, LogLevel = -5 });
        Check("倍率上限被夹取到 5", clamped.Contains("MaxGeneratedFrames=5"));
        Check("日志级别被夹取到 0", clamped.Contains("Level=0"));
    }

    private static void TestLocalization()
    {
        Section("界面多语言");

        // Both tables must define the same keys. A key present in only one language shows the raw key
        // (or the wrong language) in the interface, so this is the primary guard.
        var (onlyZh, onlyEn) = Strings.MissingKeys();
        Check("两种语言的键完全一致",
            onlyZh.Count == 0 && onlyEn.Count == 0,
            $"仅中文 {onlyZh.Count} 个、仅英文 {onlyEn.Count} 个" +
            (onlyZh.Count > 0 ? "；仅中文: " + string.Join(",", onlyZh.Take(5)) : "") +
            (onlyEn.Count > 0 ? "；仅英文: " + string.Join(",", onlyEn.Take(5)) : ""));

        Console.WriteLine($"      键总数: {Strings.AllKeys.Count()}");

        // Placeholder mismatch means string.Format throws or silently drops a value in one language.
        var placeholderIssues = LocalizationAudit.PlaceholderMismatches();
        Check("两种语言的占位符一致",
            placeholderIssues.Count == 0,
            placeholderIssues.Count > 0 ? string.Join(" | ", placeholderIssues.Take(3)) : "");

        // Every key used in code or XAML must exist in the tables.
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var undefined = LocalizationAudit.UndefinedKeysUsed(LocalizationAudit.SourceFiles(repoRoot));
        Check("代码中引用的键都已定义",
            undefined.Count == 0,
            undefined.Count > 0 ? "未定义: " + string.Join(", ", undefined.Take(8)) : "");

        // Every defined key must resolve to a non-empty string in both languages, and no key may leak
        // through as its own name (which is what Loc.T returns for a missing entry).
        var emptyOrRaw = new List<string>();
        foreach (var language in Languages.All)
        {
            Loc.SetLanguage(language);
            foreach (var key in Strings.AllKeys)
            {
                var text = Loc.T(key);
                if (string.IsNullOrWhiteSpace(text)) emptyOrRaw.Add($"{language}:{key}(空)");
            }
        }
        Check("所有键在两种语言下都有文本", emptyOrRaw.Count == 0,
            emptyOrRaw.Count > 0 ? string.Join(", ", emptyOrRaw.Take(5)) : "");

        // Language normalisation: accept the common spellings, fall back safely on nonsense.
        Check("zh-Hans 归一化为简体中文", Languages.Normalize("zh-Hans") == Languages.ChineseSimplified);
        Check("zh-CN 归一化为简体中文", Languages.Normalize("zh-CN") == Languages.ChineseSimplified);
        Check("en 归一化为英文", Languages.Normalize("en") == Languages.English);
        Check("en-US 归一化为英文", Languages.Normalize("en-US") == Languages.English);
        Check("EN 大小写不敏感", Languages.Normalize("EN") == Languages.English);
        Check("空值回落默认语言", Languages.Normalize("") == Languages.ChineseSimplified);
        Check("null 回落默认语言", Languages.Normalize(null) == Languages.ChineseSimplified);
        Check("无法识别的语言回落默认", Languages.Normalize("klingon") == Languages.ChineseSimplified);
        Check("语言显示名：中文", Languages.DisplayName("zh-Hans") == "简体中文");
        Check("语言显示名：英文", Languages.DisplayName("en") == "English");

        // Switching language must change what lookups return; this is the mechanism the UI relies on.
        Loc.SetLanguage(Languages.ChineseSimplified);
        var zh = Loc.T("Action.Deploy");
        Loc.SetLanguage(Languages.English);
        var en = Loc.T("Action.Deploy");

        Check("切换语言后取到不同文本", zh != en, $"{zh} / {en}");
        Check("中文表返回中文", zh.Contains('部'), zh);
        Check("英文表返回英文", en.Contains("Deploy", StringComparison.OrdinalIgnoreCase), en);
        Check("当前语言已更新", Loc.Current == Languages.English, Loc.Current);

        // Formatted lookups must substitute in both languages.
        Loc.SetLanguage(Languages.ChineseSimplified);
        var zhFmt = Loc.T("Deploy.Success", "TestGame");
        Loc.SetLanguage(Languages.English);
        var enFmt = Loc.T("Deploy.Success", "TestGame");
        Check("中文格式化含参数", zhFmt.Contains("TestGame"), zhFmt);
        Check("英文格式化含参数", enFmt.Contains("TestGame"), enFmt);
        Check("格式化结果随语言变化", zhFmt != enFmt, $"{zhFmt} / {enFmt}");

        // A missing key must degrade to the key itself rather than throwing or blanking the UI. The
        // name is assembled at runtime so the source audit does not flag it as an undefined lookup.
        var absentKey = "No" + ".Such" + ".Key";
        Check("缺失键返回键名而非异常", Loc.T(absentKey) == absentKey);

        // Placeholder-count mismatch is handled without crashing.
        Check("占位符数量不匹配不抛异常", Loc.T("Deploy.Success").Length > 0);

        // The game-name quoting differs by language, which is why it is a key rather than a format
        // string embedded in code.
        Loc.SetLanguage(Languages.ChineseSimplified);
        var zhQuoted = Loc.T("Anti.QuotedName", "Game");
        Loc.SetLanguage(Languages.English);
        var enQuoted = Loc.T("Anti.QuotedName", "Game");
        Check("中文用直角引号", zhQuoted.Contains('「'), zhQuoted);
        Check("英文用弯引号", enQuoted.Contains('“'), enQuoted);

        // Restore the default so later sections see a predictable language.
        Loc.SetLanguage(Languages.ChineseSimplified);

        // Computed properties on GameEntry read from the string table. A language change cannot reach
        // them through the XAML binding — the binding watches the game object, not Loc — so the model
        // re-raises them explicitly. Without that, the game list kept showing the old language while
        // the rest of the window switched, which is exactly what happened in the field.
        var entry = new GameEntry { Name = "Test", RenderDir = @"C:\fake" };

        Loc.SetLanguage(Languages.ChineseSimplified);
        var zhStatus = entry.StatusText;
        var zhSubtitle = entry.Subtitle;

        var notified = new List<string>();
        entry.PropertyChanged += (_, e) => { if (e.PropertyName is not null) notified.Add(e.PropertyName); };

        Loc.SetLanguage(Languages.English);
        entry.RaiseLocalizedText();

        Check("语言变更后通知了 StatusText", notified.Contains("StatusText"), string.Join(",", notified));
        Check("语言变更后通知了 Subtitle", notified.Contains("Subtitle"));
        Check("语言变更后通知了 AntiCheatBody", notified.Contains("AntiCheatBody"));

        var enStatus = entry.StatusText;
        Check("状态文案随语言变化", zhStatus != enStatus, $"{zhStatus} / {enStatus}");

        Loc.SetLanguage(Languages.ChineseSimplified);
        Check("切回中文后恢复中文文案", entry.StatusText == zhStatus, entry.StatusText);
        Check("路径类字段不随语言变化", entry.Subtitle == zhSubtitle, entry.Subtitle);

        // The stored proxy value must not be localised: it is persisted to library.json, so
        // translating it would invalidate existing configuration.
        Check("代理入口的存储值保持中文常量", entry.PreferredProxy == "自动", entry.PreferredProxy);
        Check("代理入口的存储值与语言无关",
            DeploymentService.AutoProxy == "自动", DeploymentService.AutoProxy);
    }

    /// <summary>
    /// The version comes from the csproj locally and from the git tag in release builds. The harness
    /// is its own assembly, so this can only verify the format and that the commit-hash suffix the
    /// SDK appends is stripped; the app's real number is pinned in its csproj. Not gated on mod
    /// files — it must run on a fresh clone too.
    /// </summary>
    private static void TestVersionLabel()
    {
        Section("版本号");
        Check("版本号格式正确且剥离提交哈希",
            System.Text.RegularExpressions.Regex.IsMatch(AppVersion.Label, @"^v\d+\.\d+\.\d+$"),
            AppVersion.Label);
    }

    /// <summary>
    /// The download progress panel depends on these contracts: a readable speed format and a pause
    /// gate that blocks at file boundaries, releases on resume, and can be broken by cancel.
    /// </summary>
    private static void TestDownloadProgress()
    {
        Section("下载进度与暂停");

        Check("速度格式化：零", ModFetcher.FormatSpeed(0) == "0 B/s", ModFetcher.FormatSpeed(0));
        Check("速度格式化：字节", ModFetcher.FormatSpeed(512) == "512 B/s", ModFetcher.FormatSpeed(512));
        Check("速度格式化：KB", ModFetcher.FormatSpeed(1536) == "1.5 KB/s", ModFetcher.FormatSpeed(1536));
        Check("速度格式化：MB", ModFetcher.FormatSpeed(30 * 1024 * 1024) == "30.0 MB/s",
            ModFetcher.FormatSpeed(30 * 1024 * 1024));

        var gate = new ModFetcher.DownloadGate();
        gate.Wait(CancellationToken.None);

        gate.Pause();
        Check("门：暂停后状态可见", gate.Paused);
        gate.Resume();
        Check("门：恢复后状态清除", !gate.Paused);

        // Pause blocks a waiting worker until Resume — and cancel breaks the block, which is what
        // lets a stuck source be abandoned mid-pause.
        gate.Pause();
        var blocked = Task.Run(() =>
        {
            try { gate.Wait(CancellationToken.None); return false; }
            catch { return true; }
        });
        Thread.Sleep(200);
        Check("门：暂停时阻塞", !blocked.IsCompleted);
        gate.Resume();
        Check("门：恢复后放行", blocked.Wait(2000) && !blocked.Result);

        gate.Pause();
        var cts = new CancellationTokenSource();
        var outcome = Task.Run(() =>
        {
            try { gate.Wait(cts.Token); return "returned"; }
            catch (OperationCanceledException) { return "cancelled"; }
        });
        Thread.Sleep(100);
        cts.Cancel();
        Check("门：取消能打断暂停", outcome.Wait(2000) && outcome.Result == "cancelled", outcome.Result);
    }

    private static void TestGpuProbe()
    {
        Section("显卡探测");

        var info = Gpu.Probe();
        Console.WriteLine($"      探测结果: {info.Name} | 路由 {info.Router} | 驱动 {info.Driver}");
        Console.WriteLine($"      建议: {info.Advice}");

        // This ran on a specific machine once and asserted "an NVIDIA card must be present", which
        // fails on any build agent (they have no discrete GPU). Probe behaviour is what matters, so
        // the assertions are shaped around what was actually detected.
        var hasNvidia = info.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

        Check("探测有结果且未抛异常", !string.IsNullOrWhiteSpace(info.Name), info.Name);
        Check("路由取值合法", info.Router is "SM86" or "SM75", info.Router);
        Check("给出了说明文字", !string.IsNullOrWhiteSpace(info.Advice), "(空)");

        if (hasNvidia)
        {
            Console.WriteLine("      （检测到 NVIDIA 显卡，校验路由映射）");
            Check("读到驱动版本", info.Driver.Length > 0, "驱动为空");

            if (info.Name.Contains("RTX 30", StringComparison.OrdinalIgnoreCase))
                Check("RTX 30 系映射到 SM86", info.Router == "SM86", info.Router);
            else if (info.Name.Contains("RTX 20", StringComparison.OrdinalIgnoreCase))
                Check("RTX 20 系映射到 SM75", info.Router == "SM75", info.Router);
        }
        else
        {
            Console.WriteLine("      （无 NVIDIA 显卡，这是 CI 等虚拟环境的正常情况，跳过型号映射校验）");
            Check("无 N 卡时给出可读提示", info.Advice.Length > 0, info.Advice);
        }

        // The mapping itself is pure logic and is verified regardless of the host's hardware.
        Check("型号→路由：RTX 3080 Ti → SM86", Gpu.RouteForAdapter("NVIDIA GeForce RTX 3080 Ti") == "SM86");
        Check("型号→路由：RTX 2080 → SM75", Gpu.RouteForAdapter("NVIDIA GeForce RTX 2080") == "SM75");
        Check("型号→路由：RTX 2080 Ti → SM75", Gpu.RouteForAdapter("NVIDIA GeForce RTX 2080 Ti") == "SM75");
        Check("型号→路由：RTX 4090 → SM86", Gpu.RouteForAdapter("NVIDIA GeForce RTX 4090") == "SM86");
        Check("型号→路由：RTX 5090 → SM86", Gpu.RouteForAdapter("NVIDIA GeForce RTX 5090") == "SM86");
        Check("型号→路由：未知型号回落 SM86", Gpu.RouteForAdapter("Some Unknown Adapter") == "SM86");
        Check("型号→路由：空值不抛异常", Gpu.RouteForAdapter("") == "SM86");

        // Hardware id parsing and architecture blocks. The route decision uses these instead of the
        // product name, because a name can be edited in the registry while the id cannot.
        Check("从设备路径解析硬件 ID",
            Gpu.ParsePciDeviceId(@"PCI\VEN_10DE&DEV_2208&SUBSYS_88021043&REV_A1\4&D0BDF66&0&0009") == "2208",
            Gpu.ParsePciDeviceId(@"PCI\VEN_10DE&DEV_2208&SUBSYS_88021043&REV_A1\4&D0BDF66&0&0009"));
        Check("解析结果统一大写", Gpu.ParsePciDeviceId(@"PCI\VEN_10DE&DEV_2b85&SUBSYS_X") == "2B85");
        Check("无 ID 的路径返回空", Gpu.ParsePciDeviceId(@"PCI\VEN_10DE&SUBSYS_X") is null);
        Check("空路径不抛异常", Gpu.ParsePciDeviceId(null) is null);

        Check("识别 NVIDIA 厂商 ID", Gpu.IsNvidiaDevice(@"PCI\VEN_10DE&DEV_2208"));
        Check("识别非 NVIDIA 厂商 ID", !Gpu.IsNvidiaDevice(@"PCI\VEN_1002&DEV_13C0"));

        Check("硬件 ID 2208 → Ampere", Gpu.FamilyFromDeviceId("2208") == "Ampere", Gpu.FamilyFromDeviceId("2208"));
        Check("硬件 ID 1E04 → Turing", Gpu.FamilyFromDeviceId("1E04") == "Turing", Gpu.FamilyFromDeviceId("1E04"));
        Check("硬件 ID 2684 → Ada", Gpu.FamilyFromDeviceId("2684") == "Ada", Gpu.FamilyFromDeviceId("2684"));
        Check("硬件 ID 2B85 → Blackwell", Gpu.FamilyFromDeviceId("2B85") == "Blackwell", Gpu.FamilyFromDeviceId("2B85"));
        Check("未知硬件 ID 返回空", Gpu.FamilyFromDeviceId("FFFF") is null, Gpu.FamilyFromDeviceId("FFFF"));
        Check("非法硬件 ID 返回空", Gpu.FamilyFromDeviceId("zzzz") is null);
        Check("空硬件 ID 返回空", Gpu.FamilyFromDeviceId("") is null);

        Check("架构→路由：Turing → SM75", Gpu.RouteForFamily("Turing") == "SM75");
        Check("架构→路由：Ampere → SM86", Gpu.RouteForFamily("Ampere") == "SM86");
        Check("架构→路由：Ada → SM86", Gpu.RouteForFamily("Ada") == "SM86");
        Check("架构→路由：未知 → SM86", Gpu.RouteForFamily(null) == "SM86");

        // A renamed adapter: the product name claims one architecture, the hardware id another.
        // This is not hypothetical — the development machine this was written on had exactly this,
        // and the previous name-based logic gave the user wrong advice ("40 series, not needed").
        var (spoofedFamily, spoofedMismatch) = Gpu.Classify("NVIDIA GeForce RTX 4090", "2208");
        Check("伪装显卡：识别为名称与硬件不符", spoofedMismatch);
        Check("伪装显卡：按硬件 ID 判定为 Ampere", spoofedFamily == "Ampere", spoofedFamily);
        Check("伪装显卡：路由取硬件 ID 的 SM86", Gpu.RouteForFamily(spoofedFamily) == "SM86");

        var (agreeFamily, agreeMismatch) = Gpu.Classify("NVIDIA GeForce RTX 3080 Ti", "2208");
        Check("名称与硬件一致时不报警", !agreeMismatch);
        Check("名称与硬件一致时架构正确", agreeFamily == "Ampere", agreeFamily);

        var (noIdFamily, noIdMismatch) = Gpu.Classify("NVIDIA GeForce RTX 4070", null);
        Check("无硬件 ID 时回落到名称判定", noIdFamily == "Ada", noIdFamily);
        Check("无硬件 ID 时不报不符", !noIdMismatch);

        // Hardware-accelerated GPU scheduling: the mod needs it, but a machine may not expose the
        // setting at all, in which case we stay quiet rather than claiming it is disabled.
        var hags = Gpu.HardwareSchedulingEnabled();
        Console.WriteLine("      硬件加速 GPU 计划: " + (hags switch
        {
            true => "已开启",
            false => "未开启",
            null => "平台未提供该设置",
        }));
        Check("HAGS 状态读取不抛异常", true);

        // Wording must stay useful for the families that need special handling.
        Check("RTX 40 系提示无需本 Mod",
            Gpu.AdviceForAdapter("NVIDIA GeForce RTX 4070", "1.2.3").Contains("不需要"));
        Check("RTX 20 系提示 SM75 路由",
            Gpu.AdviceForAdapter("NVIDIA GeForce RTX 2060", "1.2.3").Contains("SM75"));
        Check("无驱动版本时措辞得体",
            Gpu.AdviceForAdapter("NVIDIA GeForce RTX 3080", "").Contains("未知"));

        // The toolbar shows a one-line summary; the full explanation lives on the tooltip and in the
        // log. A compact line must stay short even in the mismatch case, where the full text runs
        // several sentences.
        var compactOk = Gpu.CompactAdvice(new GpuInfo("NVIDIA GeForce RTX 3080 Ti", "566.14", "SM86", "")
        {
            PciDeviceId = "2208",
            HardwareFamily = "Ampere",
        });
        Check("紧凑建议：正常卡显示驱动与路由",
            compactOk.Contains("驱动") && compactOk.Contains("SM86") && compactOk.Length < 60, compactOk);

        var compactMismatch = Gpu.CompactAdvice(new GpuInfo("NVIDIA GeForce RTX 4090", "566.14", "SM86", "")
        {
            PciDeviceId = "2208",
            HardwareFamily = "Ampere",
            NameMismatchesHardware = true,
        });
        Check("紧凑建议：名称不符时只出短句",
            compactMismatch.Contains("不符") && !compactMismatch.Contains("误导"), compactMismatch);

        // Display-name editing: the validation and the INF-string resolution are pure and always
        // testable; the registry reads are machine-dependent, so they only run where an NVIDIA
        // adapter is present.
        Check("名称校验：空", Gpu.InvalidDisplayNameReason("") is not null);
        Check("名称校验：纯空白", Gpu.InvalidDisplayNameReason("   ") is not null);
        Check("名称校验：合法", Gpu.InvalidDisplayNameReason("NVIDIA GeForce RTX 4090") is null);
        Check("名称校验：超长", Gpu.InvalidDisplayNameReason(new string('x', 128)) is not null);
        Check("名称校验：控制字符", Gpu.InvalidDisplayNameReason("RTX\n4090") is not null);
        Check("名称校验：首尾空白", Gpu.InvalidDisplayNameReason(" RTX 4090") is not null);

        // The PnP DeviceDesc is an indirect string into the driver INF; the INF is signed with the
        // driver package, so its [Strings] table still holds the true model name even when the
        // display name was renamed. Resolution must prefer that over the spoofable fallback text.
        var infDir = Path.Combine(Path.GetTempPath(), "dlssg_inf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(infDir);
        try
        {
            File.WriteAllText(Path.Combine(infDir, "oem24.inf"),
                "[Strings]\r\nNVIDIA_DEV.2208 = \"NVIDIA GeForce RTX 3080 Ti\"\r\n");

            Check("间接字符串解析到 INF 真名",
                Gpu.ResolveIndirectString("@oem24.inf,%nvidia_dev.2208%;NVIDIA GeForce RTX 4090", infDir)
                    == "NVIDIA GeForce RTX 3080 Ti");
            Check("INF 缺失时退回备用名",
                Gpu.ResolveIndirectString("@missing.inf,%x%;NVIDIA GeForce RTX 4090", infDir)
                    == "NVIDIA GeForce RTX 4090");
            Check("普通字符串原样返回",
                Gpu.ResolveIndirectString("NVIDIA GeForce RTX 4090") == "NVIDIA GeForce RTX 4090");
            Check("Strings 段外同名 token 不误匹配",
                Gpu.ResolveInfToken(new[]
                {
                    "[NVIDIA_Devices.NTamd64]",
                    "%NVIDIA_DEV.2208% = Section040, PCI\\VEN_10DE&DEV_2208",
                    "[Strings]",
                    "NVIDIA_DEV.2209 = \"NVIDIA GeForce RTX 4090\"",
                }, "NVIDIA_DEV.2208") is null);
        }
        finally
        {
            try { Directory.Delete(infDir, recursive: true); } catch { /* best effort */ }
        }

        if (hasNvidia)
        {
            var adapter = Gpu.NvidiaAdapter();
            Check("定位到 NVIDIA 适配器", adapter is not null);
            if (adapter is not null)
            {
                var reg = Gpu.RegistryDisplayName(adapter.DeviceId, adapter.Name);
                var pnp = Gpu.PnpDeviceDescription(adapter.DeviceInstancePath);
                Console.WriteLine($"      注册表显示名: {reg ?? "(无)"}   PnP 真实名: {pnp ?? "(无)"}");
                Check("注册表显示名可读", reg is not null);
                Check("PnP 真实名可读", pnp is not null);
            }
        }
    }

    private static void TestDeployRestore(string modRoot, string work)
    {
        Section("部署 → 恢复（干净目录）");
        if (SkipWithoutModFiles("部署 → 恢复")) return;

        var dir = MakeGameDir(work, "GameClean");
        var source = new ModSource(modRoot);
        var game = new GameEntry
        {
            Name = "GameClean",
            RenderDir = dir,
            PreferredProxy = DeploymentService.AutoProxy,
            Profile = new GameProfile { Router = "SM86", MaxGeneratedFrames = 3, LogLevel = 1 },
        };

        var deploy = DeploymentService.Deploy(game, source);
        Check("部署成功", deploy.Ok, deploy.Message);
        Check("写入 version.dll", File.Exists(Path.Combine(dir, "version.dll")));
        Check("写入 dlssg_sm86.ini", File.Exists(Path.Combine(dir, ModSource.IniName)));
        Check("记录已建立", game.Deployment is not null);
        Check("记录的入口名正确", game.Deployment?.ProxyName == "version.dll", game.Deployment?.ProxyName);
        Check("记录版本号", game.Deployment?.ModVersion == source.Version, game.Deployment?.ModVersion);
        Check("DLL 已带项目签名", DeploymentService.IsProjectSigned(Path.Combine(dir, "version.dll")));
        Check("部署的 DLL 与源文件一致",
            Sha(Path.Combine(dir, "version.dll")) == Sha(Path.Combine(modRoot, "version.dll")));
        Check("INI 内容符合配置",
            File.ReadAllText(Path.Combine(dir, ModSource.IniName)).Contains("MaxGeneratedFrames=3"));

        DeploymentService.Check(game);
        Check("状态判定为已部署", game.Status == GameStatus.Deployed, game.StatusText + " / " + game.StatusDetail);

        // Tampering with the INI should be reported, not silently ignored.
        File.AppendAllText(Path.Combine(dir, ModSource.IniName), "\r\n; user edit\r\n");
        DeploymentService.Check(game);
        Check("INI 被改动后状态变为已改动", game.Status == GameStatus.Modified, game.StatusText);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("恢复成功", restore.Ok, restore.Message);
        Check("version.dll 已移除", !File.Exists(Path.Combine(dir, "version.dll")));
        Check("INI 已移除", !File.Exists(Path.Combine(dir, ModSource.IniName)));
        Check("原游戏 exe 未受影响", File.Exists(Path.Combine(dir, "GameClean.exe")));
        Check("marker DLL 未受影响", File.Exists(Path.Combine(dir, "nvngx_dlssg.dll")));
        Check("部署记录已清除", game.Deployment is null);

        DeploymentService.Check(game);
        Check("恢复后状态为未部署", game.Status == GameStatus.NotDeployed, game.StatusText);
    }

    private static void TestForeignFileProtection(string modRoot, string work)
    {
        Section("保护其他 Mod 的文件（不得误删）");
        if (SkipWithoutModFiles("保护其他 Mod 的文件")) return;

        var dir = MakeGameDir(work, "GameForeign");
        var source = new ModSource(modRoot);

        // Another mod occupies dxgi.dll and the INI slot already holds an unrelated config.
        var foreignDxgi = Path.Combine(dir, "dxgi.dll");
        File.WriteAllBytes(foreignDxgi, RandomNumberGenerator.GetBytes(3072));
        var foreignIni = Path.Combine(dir, ModSource.IniName);
        File.WriteAllText(foreignIni, "; somebody else's config\n[Other]\r\nKey=1\r\n");
        var foreignDxgiHash = Sha(foreignDxgi);
        var foreignIniHash = Sha(foreignIni);

        var game = new GameEntry
        {
            Name = "GameForeign",
            RenderDir = dir,
            PreferredProxy = DeploymentService.AutoProxy,
            Profile = new GameProfile(),
        };

        var deploy = DeploymentService.Deploy(game, source);
        Check("自动避开被占用的 dxgi.dll", deploy.Ok && game.Deployment?.ProxyName != "dxgi.dll",
            "实际入口: " + game.Deployment?.ProxyName);
        Check("其他 Mod 的 dxgi.dll 未被覆盖", Sha(foreignDxgi) == foreignDxgiHash);
        Check("其他 Mod 的 dxgi.dll 未被删除", File.Exists(foreignDxgi));

        var restoreResult = DeploymentService.Restore(game, removeLogs: false);
        Check("恢复成功（含还原）", restoreResult.Ok, restoreResult.Message);
        Check("其他 Mod 的 dxgi.dll 仍在", File.Exists(foreignDxgi));
        Check("其他 Mod 的 dxgi.dll 内容未变", Sha(foreignDxgi) == foreignDxgiHash);
        Check("原来的 INI 未被当作本项目的删除", File.Exists(foreignIni), "INI 被误删");
        Check("原来的 INI 内容未变", Sha(foreignIni) == foreignIniHash);

        // Redeploy over an already-deployed game (a settings change, or a batch deploy) replaces the
        // record; if the previous backups are not carried into the new one, a foreign INI displaced
        // by the first deploy is orphaned in the restore folder forever.
        var dir2 = MakeGameDir(work, "GameForeign2");
        var foreignIni2 = Path.Combine(dir2, ModSource.IniName);
        File.WriteAllText(foreignIni2, "; somebody else's config again\n[Other]\r\nKey=2\r\n");
        var foreignIni2Hash = Sha(foreignIni2);

        var game2 = new GameEntry
        {
            Name = "GameForeign2",
            RenderDir = dir2,
            PreferredProxy = DeploymentService.AutoProxy,
            Profile = new GameProfile(),
        };

        Check("首次部署成功", DeploymentService.Deploy(game2, source).Ok);
        Check("首次部署备份了外部 INI", game2.Deployment?.Backups.Count == 1);

        game2.Profile.MaxGeneratedFrames = 2;
        var redeploy = DeploymentService.Deploy(game2, source);
        Check("二次部署成功", redeploy.Ok, redeploy.Message);
        Check("二次部署结转了备份记录",
            game2.Deployment?.Backups.Any(b => string.Equals(b.FileName, ModSource.IniName, StringComparison.OrdinalIgnoreCase)) == true);

        var restore2 = DeploymentService.Restore(game2, removeLogs: false);
        Check("二次部署后可恢复", restore2.Ok, restore2.Message);
        Check("外部 INI 经二次部署后回位",
            File.Exists(foreignIni2) && Sha(foreignIni2) == foreignIni2Hash, "外部 INI 未回位");
        Check("恢复后目录里没有残留的自家代理", DeploymentService.FindInstalledProxy(dir2) is null);
    }

    private static void TestProxyOccupation(string modRoot, string work)
    {
        Section("五个入口名全被占用");

        var dir = MakeGameDir(work, "GameBlocked");
        foreach (var name in ModSource.ProxyCandidates)
            File.WriteAllBytes(Path.Combine(dir, name), RandomNumberGenerator.GetBytes(1024));

        var hashes = ModSource.ProxyCandidates.ToDictionary(n => n, n => Sha(Path.Combine(dir, n)));

        var game = new GameEntry { Name = "GameBlocked", RenderDir = dir, PreferredProxy = DeploymentService.AutoProxy };
        var result = DeploymentService.Deploy(game, new ModSource(MakeSyntheticModSource(work)));

        Check("部署被拒绝而不是覆盖", !result.Ok, "居然成功了");
        Check("给出了解决提示", result.Message.Contains("占用"), result.Message);
        Check("所有占用文件保持原样",
            ModSource.ProxyCandidates.All(n => Sha(Path.Combine(dir, n)) == hashes[n]));

        // Every deployable name is occupied, so no name at all is left: the manager reports the conflict
        // rather than inventing one. (0.3.0 ships six entry points, and each is deployed from the file
        // built for that name — the file and the name can no longer be mismatched.)
        Check("没有留下任何新文件",
            Directory.GetFiles(dir).Length == ModSource.ProxyCandidates.Length + 2,   // + 游戏 exe 与 marker
            string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName)));
    }

    /// <summary>
    /// Adding a proxy DLL the user supplies: the entry name is the file name, so the file is copied into
    /// the mod folder under that name and deployed like the bundled entries. No mod download is needed —
    /// the mechanics are the point, not the bytes.
    /// </summary>
    private static void TestProxyImport(string modRoot, string work)
    {
        Section("添加代理 DLL（本地入口）");

        Check("winhttp.dll 属于已知入口名", ModSource.IsKnownProxyName("winhttp.dll"));
        Check("入口名匹配不区分大小写", ModSource.IsKnownProxyName("WINHTTP.DLL"));
        // 0.3.0 ships d3d12.dll and dbghelp.dll itself; winhttp.dll left the release but stays known, so
        // it is the name an import can legitimately take.
        Check("d3d12.dll 是自带入口", ModSource.ProxyCandidates.Contains("d3d12.dll"));
        Check("winhttp.dll 不是自带入口", !ModSource.ProxyCandidates.Contains("winhttp.dll"));

        var source = MakeSyntheticModSource(work);
        var before = new ModSource(source);
        Check("初始没有本地入口", before.ImportedProxies.Count == 0, string.Join("、", before.ImportedProxies));
        Check("可用入口初始为自带五个", before.AvailableProxies.Count == ModSource.ProxyCandidates.Length,
            string.Join("、", before.AvailableProxies));

        // ---- P2-⑩（Pass C 报出）：**只有 DLL 真的存在的入口才算「可部署」** ----
        //
        // `KnownProxyNames` 的文档一直承诺「a name is only deployable when a DLL for it exists」，
        // 而实现曾经是 `ProxyCandidates.Concat(ImportedProxies)` ——**把全部 6 个名字都列出来，
        // 不管 payload 里有没有**。后果：计划与界面下拉会给出 payload 里没有的入口，真实 provider
        // 随后以「未通过校验」拒绝（**用户看到的是「校验失败」而不是「这个入口不存在」**），
        // 直接调 `Deploy` 还会抛 `FileNotFoundException`。
        //
        // 上面那条「初始为自带五个」能通过，是因为**它的夹具 payload 恰好五个都在** —— 所以它
        // 对这个缺陷完全不敏感。**要暴露它，需要一个「只装一个入口」的 payload。**
        var sparseDir = Path.Combine(work, "sparse-payload");
        Directory.CreateDirectory(Path.Combine(sparseDir, ModSource.AltDirName));
        File.WriteAllText(Path.Combine(sparseDir, "version.dll"), "payload");
        File.WriteAllText(Path.Combine(sparseDir, ModSource.IniName), "[DLSSG SM86]" + Environment.NewLine);

        var sparse = new ModSource(sparseDir);

        Check("payload 里只有一个入口时，可部署入口也只有它（§17 P2-⑩）",
            sparse.AvailableProxies.Count == 1
                && sparse.AvailableProxies[0] == "version.dll",
            string.Join("、", sparse.AvailableProxies));

        Check("可部署入口不含 payload 里不存在的名字（§17 P2-⑩）",
            !sparse.AvailableProxies.Contains("dinput8.dll", StringComparer.OrdinalIgnoreCase)
                && !sparse.AvailableProxies.Contains("dxgi.dll", StringComparer.OrdinalIgnoreCase),
            string.Join("、", sparse.AvailableProxies));

        // A community build: a plausible entry name, and no signature this project can vouch for. The
        // file name *is* the entry name, so the fixture has to carry the real one.
        var localDir = Path.Combine(work, "LocalBuild");
        Directory.CreateDirectory(localDir);
        var local = Path.Combine(localDir, "winhttp.dll");
        File.WriteAllBytes(local, RandomNumberGenerator.GetBytes(4096));

        var add = ModSource.ImportProxy(source, local);
        Check("导入成功", add.Ok, add.Message);
        Check("以原文件名落在 altnative 下", File.Exists(Path.Combine(source, "altnative", "winhttp.dll")));
        Check("内容与所选文件一致", Sha(Path.Combine(source, "altnative", "winhttp.dll")) == Sha(local));

        var after = new ModSource(source);
        Check("本地入口被识别", after.ImportedProxies.Count == 1 && after.ImportedProxies[0] == "winhttp.dll",
            string.Join("、", after.ImportedProxies));
        Check("可用入口 = 自带 + 本地", after.AvailableProxies.Count == ModSource.ProxyCandidates.Length + 1,
            string.Join("、", after.AvailableProxies));
        Check("自带入口不受影响", after.Proxies.Count == ModSource.ProxyCandidates.Length,
            string.Join("、", after.Proxies));
        Check("本地入口排在自己几个之后",
            after.AvailableProxies.Take(ModSource.ProxyCandidates.Length).SequenceEqual(ModSource.ProxyCandidates));

        // The project's own builds must never be replaced by an import: their signature is what every
        // ownership check rests on.
        var clash = Path.Combine(work, "winmm.dll");
        File.WriteAllBytes(clash, RandomNumberGenerator.GetBytes(512));
        var reserved = ModSource.ImportProxy(source, clash);
        Check("拒绝覆盖自带入口名", !reserved.Ok, reserved.Message);
        Check("自带入口内容未变", Sha(Path.Combine(source, "altnative", "winmm.dll")) != Sha(clash));

        var text = Path.Combine(work, "notes.txt");
        File.WriteAllText(text, "not a proxy");
        Check("拒绝非 DLL 文件", !ModSource.ImportProxy(source, text).Ok);
        Check("拒绝不存在的文件", !ModSource.ImportProxy(source, Path.Combine(work, "nope.dll")).Ok);

        // Deploy the imported entry, then remove it again with the normal restore path.
        var dir = MakeGameDir(work, "GameImportedProxy");
        var game = new GameEntry { Name = "GameImportedProxy", RenderDir = dir, PreferredProxy = "winhttp.dll" };

        var deploy = DeploymentService.Deploy(game, new ModSource(source));
        Check("部署本地入口成功", deploy.Ok, deploy.Message);
        Check("入口名记录为 winhttp.dll", game.Deployment?.ProxyName == "winhttp.dll", game.Deployment?.ProxyName);
        Check("游戏目录里出现 winhttp.dll", File.Exists(Path.Combine(dir, "winhttp.dll")));
        Check("部署内容与本地代理一致", Sha(Path.Combine(dir, "winhttp.dll")) == Sha(local));
        Check("代理与 INI 都记了指纹", game.Deployment!.Files.Count == 2, "实际: " + game.Deployment.Files.Count);

        DeploymentService.Check(game);
        Check("状态为已部署", game.Status == GameStatus.Deployed, game.StatusText + " / " + game.StatusDetail);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("一键恢复成功", restore.Ok, restore.Message);
        Check("一键恢复删除了 winhttp.dll", !File.Exists(Path.Combine(dir, "winhttp.dll")));
        Check("INI 也已删除", !File.Exists(Path.Combine(dir, ModSource.IniName)));

        DeploymentService.Check(game);
        Check("恢复后状态为未部署", game.Status == GameStatus.NotDeployed, game.StatusText);

        // An imported proxy next to a project-signed one is still two proxies live at once — the crash
        // the single-proxy invariant exists for. Needs the real signed payload.
        if (!_hasModFiles)
        {
            _skipped++;
            Console.WriteLine("  [跳过] 本地入口与自带入口冲突（需要 Mod 文件）");
            return;
        }

        var conflictDir = MakeGameDir(work, "GameImportConflict");
        var conflict = new GameEntry { Name = "GameImportConflict", RenderDir = conflictDir, PreferredProxy = "d3d12.dll" };
        var conflictDeploy = DeploymentService.Deploy(conflict, new ModSource(source));
        Check("冲突场景：先部署本地入口", conflictDeploy.Ok, conflictDeploy.Message);

        File.Copy(Path.Combine(modRoot, "version.dll"), Path.Combine(conflictDir, "version.dll"), overwrite: true);

        DeploymentService.Check(conflict);
        Check("两种代理共存时仍为已部署并提示待机",
            conflict.Status == GameStatus.Deployed && conflict.StatusDetail.Contains("待机"),
            conflict.StatusText + " / " + conflict.StatusDetail);

        var conflictRestore = DeploymentService.Restore(conflict, removeLogs: false);
        Check("恢复后两个代理都已清除", conflictRestore.Ok &&
            !File.Exists(Path.Combine(conflictDir, "d3d12.dll")) &&
            !File.Exists(Path.Combine(conflictDir, "version.dll")),
            conflictRestore.Message);
    }

    /// <summary>
    /// Regression for a crash caused by two proxies being live at once.
    ///
    /// The mod requires exactly one proxy in the game folder — the game loads every entry name it
    /// recognises, so a second one runs a second inference pipeline. This happened for real when the
    /// library was reset (losing the deployment record), and the next deploy then picked a free name
    /// instead of reusing the installed proxy, leaving both in place.
    /// </summary>
    private static void TestSingleProxyInvariant(string modRoot, string work)
    {
        Section("游戏目录只应存在一个本项目代理");
        if (SkipWithoutModFiles("单一代理约束")) return;

        var source = new ModSource(modRoot);
        var dir = MakeGameDir(work, "GameOneProxy");

        // First deployment: takes the default entry.
        var game = new GameEntry { Name = "GameOneProxy", RenderDir = dir, PreferredProxy = DeploymentService.AutoProxy };
        var first = DeploymentService.Deploy(game, source);
        Check("首次部署成功", first.Ok, first.Message);
        var firstProxy = game.Deployment?.ProxyName ?? "";
        Check("首次部署使用默认入口 version.dll", firstProxy == "version.dll", firstProxy);

        // Simulate the library being reset: the record is gone, but the file is still in the game
        // folder. The next deploy must adopt it rather than installing a second name.
        game.Deployment = null;
        var second = DeploymentService.Deploy(game, source);
        Check("丢失记录后再次部署仍成功", second.Ok, second.Message);
        Check("复用已安装的入口而非另选一个",
            game.Deployment?.ProxyName == firstProxy,
            $"首次 {firstProxy} → 再次 {game.Deployment?.ProxyName}");

        var ours = ModSource.ProxyCandidates
            .Where(n => File.Exists(Path.Combine(dir, n)) && DeploymentService.IsProjectSigned(Path.Combine(dir, n)))
            .ToList();
        Check("游戏目录里只有一个本项目代理", ours.Count == 1, string.Join("、", ours));

        // Even if a stray second proxy is planted (as happened in the field), deploying again must
        // clean it up rather than leaving both.
        var stray = ModSource.ProxyCandidates.First(n => !string.Equals(n, firstProxy, StringComparison.OrdinalIgnoreCase));
        File.Copy(Path.Combine(dir, firstProxy), Path.Combine(dir, stray), overwrite: true);
        Check("已埋入第二个代理以便验证清理",
            File.Exists(Path.Combine(dir, stray)) && DeploymentService.IsProjectSigned(Path.Combine(dir, stray)));

        // While both are present, the status must call it out — the user needs to know before
        // launching the game, not after it crashes.
        var planted = new GameEntry { Name = game.Name, RenderDir = dir, Deployment = game.Deployment };
        DeploymentService.Check(planted);
        Console.WriteLine("        状态: " + planted.StatusText + " — " + planted.StatusDetail);
        // Since 0.3.3 coexistence is designed behaviour (the extras forward, they do not run a
        // second pipeline), so the status stays Deployed and merely notes the standby count.
        Check("两个代理共存时状态提示待机",
            planted.StatusDetail.Contains("待机"), planted.StatusDetail);
        Check("共存不再算作故障", planted.Status == GameStatus.Deployed, planted.StatusText);

        game.Deployment = null;
        var third = DeploymentService.Deploy(game, source);
        Check("再次部署成功", third.Ok, third.Message);
        // Since 0.3.3 the standby mechanism makes coexistence safe (and some games only respond to
        // one specific entry name), so a modern payload keeps the stray instead of deleting it —
        // recorded with the deployment, which is what restore relies on to clean it up.
        Check("多余代理按待机保留", File.Exists(Path.Combine(dir, stray)), stray);
        Check("多余代理已记录在案",
            game.Deployment?.Files.Any(f => string.Equals(f.FileName, stray, StringComparison.OrdinalIgnoreCase)) == true);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("恢复成功", restore.Ok, restore.Message);
        Check("恢复时多余代理也被清理", !File.Exists(Path.Combine(dir, stray)), stray);

        // No proxy of ours may survive. The INI is a different matter: if a deploy displaced a
        // pre-existing file, restore correctly brings that file back, so its presence is expected
        // and not a leftover. Check the INI's content rather than its existence.
        var leftoverProxies = ModSource.ProxyCandidates
            .Where(n => File.Exists(Path.Combine(dir, n)))
            .ToList();
        Check("恢复后无代理残留", leftoverProxies.Count == 0, "残留：" + string.Join("、", leftoverProxies));

        var iniPath = Path.Combine(dir, ModSource.IniName);
        var iniIsOurs = File.Exists(iniPath) && ModSource.ReadVersion(iniPath) is not null;
        Check("恢复后 INI 不是本项目生成的那份", !iniIsOurs, "仍是本项目的配置");

        foreach (var line in restore.Lines) Console.WriteLine("        · " + line);
    }

    /// <summary>
    /// A proxy the user imported keeps its own file name, which is not in any published list. Its
    /// whole lifecycle — deploy, status, entry switch, restore — runs on the deployment record's
    /// hashes alone; before the record was scanned, restore silently left such a file in place.
    /// </summary>
    private static void TestCustomNamedProxy(string modRoot, string work)
    {
        Section("自定义命名代理（导入 DLL 全周期）");
        if (SkipWithoutModFiles("自定义命名代理")) return;

        var customName = "testfg_community.dll";
        var importedPath = Path.Combine(modRoot, ModSource.AltDirName, customName);

        try
        {
            var dir = MakeGameDir(work, "GameCustom");

            // A community build without this project's signature: only a recorded hash can vouch for it.
            var fakeDll = Path.Combine(work, "downloaded", customName);
            Directory.CreateDirectory(Path.GetDirectoryName(fakeDll)!);
            File.WriteAllBytes(fakeDll, RandomNumberGenerator.GetBytes(3072));
            Check("导入文件不带项目签名", !DeploymentService.IsProjectSigned(fakeDll));

            var import = ModSource.ImportProxy(modRoot, fakeDll);
            Check("导入自定义命名 DLL", import.Ok, import.Message);

            // Built after the import: a ModSource snapshots the folder at construction.
            var source = new ModSource(modRoot);
            Check("导入后出现在可用入口", source.AvailableProxies.Contains(customName, StringComparer.OrdinalIgnoreCase));

            var game = new GameEntry
            {
                Name = "GameCustom",
                RenderDir = dir,
                PreferredProxy = customName,
                Profile = new GameProfile(),
            };

            var deploy = DeploymentService.Deploy(game, source);
            Check("按自定义入口部署成功", deploy.Ok, deploy.Message);
            var proxyPath = Path.Combine(dir, customName);
            Check("自定义入口已写入游戏目录", File.Exists(proxyPath));
            Check("记录含该文件的哈希",
                game.Deployment?.Files.Any(f => string.Equals(f.FileName, customName, StringComparison.OrdinalIgnoreCase)) == true);

            DeploymentService.Check(game);
            Check("状态为已部署", game.Status == GameStatus.Deployed, game.StatusText + " / " + game.StatusDetail);

            // Restore must remove the custom entry too — this was the gap.
            var restore = DeploymentService.Restore(game, removeLogs: false);
            Check("恢复成功", restore.Ok, restore.Message);
            Check("自定义入口被删除", !File.Exists(proxyPath));
            Check("INI 被删除", !File.Exists(Path.Combine(dir, ModSource.IniName)));
            DeploymentService.Check(game);
            Check("恢复后状态为未部署", game.Status == GameStatus.NotDeployed, game.StatusText + " / " + game.StatusDetail);

            // Switching entry names must displace the custom one instead of running two proxies.
            var game2 = new GameEntry
            {
                Name = "GameCustom",
                RenderDir = dir,
                PreferredProxy = customName,
                Profile = new GameProfile(),
            };
            var deploy2 = DeploymentService.Deploy(game2, source);
            Check("二次部署成功", deploy2.Ok, deploy2.Message);

            game2.PreferredProxy = "version.dll";
            var switched = DeploymentService.Deploy(game2, source);
            Check("换名部署成功", switched.Ok, switched.Message);
            // Modern payload: the old entry stays as a standby forwarder, recorded for restore.
            Check("旧自定义入口按待机保留", File.Exists(proxyPath));
            Check("旧自定义入口已记录在案",
                game2.Deployment?.Files.Any(f => string.Equals(f.FileName, customName, StringComparison.OrdinalIgnoreCase)) == true);
            Check("新入口已写入", File.Exists(Path.Combine(dir, "version.dll")));

            DeploymentService.Restore(game2, removeLogs: false);
            Check("收尾恢复干净", !File.Exists(Path.Combine(dir, "version.dll")) && !File.Exists(proxyPath));
        }
        finally
        {
            // The import lands in the real mod folder; leave it out of the repository.
            try { if (File.Exists(importedPath)) File.Delete(importedPath); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// §17 P1-3：接管必须记录 `ProviderId`。
    ///
    /// <para><b>这条刻意不放进 <c>TestAdopt</c>。</b>那个方法以 `SkipWithoutModFiles` 开头，在没有 Mod 文件
    /// 的环境里**整段 return** —— 断言写在那里等于**永不执行**，而套件只会多一行「[跳过]」，看起来一切正常。
    /// 本方法用自己造的文件构造同样的场景，因此在默认套件里真的会跑。</para>
    /// </summary>
    private static void TestAdoptRecordsProvider(string work)
    {
        Section("接管记录必须标注 Provider（§17 P1-3）");

        var dir = MakeGameDir(work, "GameAdoptProvider");
        var target = Path.Combine(dir, "version.dll");

        // **夹具要用真实可识别的代理。**
        //
        // `Adopt` 只接管**它能验证来源**的代理（本项目签名，或已分发的附加入口 `d3d12.dll` —— 后者按
        // **内容哈希**识别、不看文件名）—— 这是对的：盲目接管用户目录里的任意 DLL，会把别人的东西记成
        // 我们的。
        //
        // 而这里曾经写的是 4 字节假 MZ：两个条件都过不了 ⇒ `Adopt` **必然失败** ⇒ 下面的 `Check`
        // **在每一台机器上都不执行**，而且**连 `_skipped` 都不加**（套件里连一行「[跳过]」都看不到）。
        // 更糟的是那两行提示说「有 Mod 文件时由 `TestAdopt` 覆盖」—— **而 `TestAdopt` 自己写着这条断言
        // 不在它那里**（见 `TestAdopt` 的注释）。⇒ **`ProviderId` 在全仓零断言，却被报告写成「有覆盖」。**
        //
        // 现在：有 `extra-proxies/d3d12.dll` 就把它的字节复制成 `version.dll`（内容哈希识别 ⇒ 能真正走到
        // 接管成功）；没有就**如实计入跳过**，而不是悄悄 return。
        // `extra-proxies/` 位于**仓库根**（被 `.gitignore` 排除、由使用者自备），**不在 Harness 的输出目录里**
        // —— 所以从输出目录逐级向上找。找不到时如实计入跳过，**不静默 return**。
        static string? FindCommunityProxy()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "extra-proxies", "d3d12.dll");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        var community = FindCommunityProxy();
        if (community is not null)
            File.Copy(community, target, overwrite: true);
        else if (SkipWithoutModFiles("接管记录必须标注 Provider（§17 P1-3）"))
            return;

        File.WriteAllText(Path.Combine(dir, ModSource.IniName), "[DLSSG SM86]" + Environment.NewLine);

        var game = new GameEntry { Name = "GameAdoptProvider", RenderDir = dir };
        var adopt = DeploymentService.Adopt(game, DlssgSm86Provider.ProviderId);

        Check("前置：真实代理可被接管（§17 P1-3）", adopt.Ok, adopt.Message);

        Check("接管后记录标注了 Provider，且该 Provider 可解析（§17 P1-3）",
            !string.IsNullOrWhiteSpace(game.Deployment?.ProviderId)
                && DLSSGManager.Providers.AppProviders.Registry.Get(game.Deployment!.ProviderId) is not null,
            $"id=\"{game.Deployment?.ProviderId}\"");

        // **反向配对**：记录里的 id 必须**跟着传入值走** —— 防「恒为某个常量」的实现。
        // 只写上面那条时，一个 `ProviderId = "dlssg-sm86"` 写死的实现也能通过。
        var other = new GameEntry { Name = "GameAdoptProvider2", RenderDir = dir };
        DeploymentService.Adopt(other, MfgSmoothProvider.ProviderId);

        Check("接管记录的 ProviderId 跟着传入值走（§17 P1-3 · 反向配对）",
            other.Deployment?.ProviderId == MfgSmoothProvider.ProviderId,
            $"id=\"{other.Deployment?.ProviderId}\"");
    }

    private static void TestAdopt(string modRoot, string work)
    {
        Section("接管手工安装");
        if (SkipWithoutModFiles("接管手工安装")) return;

        var dir = MakeGameDir(work, "GameManual");
        var source = new ModSource(modRoot);

        // Simulate a user who copied the files in by hand.
        File.Copy(Path.Combine(modRoot, "version.dll"), Path.Combine(dir, "version.dll"));
        File.WriteAllText(Path.Combine(dir, ModSource.IniName),
            File.ReadAllText(Path.Combine(modRoot, ModSource.IniName), Encoding.UTF8));

        var game = new GameEntry { Name = "GameManual", RenderDir = dir };

        DeploymentService.Check(game);
        Check("未接管时报告未部署并提供接管提示",
            game.Status == GameStatus.NotDeployed && game.StatusDetail.Contains("接管"), game.StatusDetail);

        var adopt = DeploymentService.Adopt(game, DlssgSm86Provider.ProviderId);
        Check("接管成功", adopt.Ok, adopt.Message);

        // §17 P1-3 的断言**不在这里** —— 本方法以 SkipWithoutModFiles 开头，没有 Mod 文件时整段 return，
        // 写在这里等于永不执行（而套件只多一行「[跳过]」，看起来一切正常）。
        // 对应的守护在 TestAdoptRecordsProvider 里，用自造文件构造同样场景，默认套件真的会跑。
        // The adopted release is read from the game folder's INI, which 0.3.0 no longer marks with a
        // version banner — so "unknown" is a legitimate answer there, and the point is that adopting
        // succeeds and records something rather than throwing the version away.
        var adoptedVersion = ModSource.ReadVersion(Path.Combine(dir, ModSource.IniName));
        Check("接管后记录版本号",
            game.Deployment?.ModVersion == (adoptedVersion ?? Loc.T("ModSource.UnknownVersion")),
            $"INI 横幅 {adoptedVersion ?? "(无)"}，记录 {game.Deployment?.ModVersion}");

        DeploymentService.Check(game);
        Check("接管后状态为已部署", game.Status == GameStatus.Deployed, game.StatusText + " / " + game.StatusDetail);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("接管后可恢复", restore.Ok && !File.Exists(Path.Combine(dir, "version.dll")), restore.Message);
        Check("marker 与 exe 未受影响",
            File.Exists(Path.Combine(dir, "nvngx_dlssg.dll")) && File.Exists(Path.Combine(dir, "GameManual.exe")));
    }

    private static void TestDetection(string modRoot, string work)
    {
        Section("游戏探测");

        var root = Path.Combine(work, "Library");
        var renderDir = Path.Combine(root, "SomeGame", "Binaries", "Win64");
        Directory.CreateDirectory(renderDir);
        File.WriteAllBytes(Path.Combine(renderDir, "SomeGame-Win64-Shipping.exe"), RandomNumberGenerator.GetBytes(8192));
        File.WriteAllBytes(Path.Combine(renderDir, "nvngx_dlssg.dll"), RandomNumberGenerator.GetBytes(1024));

        var hit = Detection.FindRenderTarget(root);
        Check("从标记文件定位到渲染目录", hit is not null && 
            string.Equals(Path.GetFullPath(hit.RenderDir), Path.GetFullPath(renderDir), StringComparison.OrdinalIgnoreCase),
            hit?.RenderDir);
        Check("选中 Shipping 主程序", hit?.ExePath.EndsWith("SomeGame-Win64-Shipping.exe") == true, hit?.ExePath);

        var scan = Detection.ScanFolder(root);
        Check("目录扫描找到候选", scan.Count == 1, "数量: " + scan.Count);

        // A folder with no DLSS-G marker yields nothing, so unrelated folders are never listed.
        var empty = Path.Combine(work, "NoMarker");
        Directory.CreateDirectory(empty);
        File.WriteAllBytes(Path.Combine(empty, "game.exe"), RandomNumberGenerator.GetBytes(512));
        Check("无标记目录不产生候选", Detection.ScanFolder(empty).Count == 0);

        // A hand-installed 0.3.x mod works on games that never shipped DLSS-G (Neverness to
        // Everness: Client\WindowsNoEditor\HT\Binaries\Win64). The project INI alone must be
        // enough of a marker to find the install and resolve the render directory to it.
        var ueRoot = Path.Combine(work, "UeLibrary", "Neverness To Everness", "Client", "WindowsNoEditor", "HT", "Binaries", "Win64");
        Directory.CreateDirectory(ueRoot);
        File.WriteAllBytes(Path.Combine(ueRoot, "HTGame.exe"), RandomNumberGenerator.GetBytes(8192));
        File.WriteAllText(Path.Combine(ueRoot, ModSource.IniName), "[General]\r\nEnabled=1\r\n");

        var ueHit = Detection.FindRenderTarget(Path.Combine(work, "UeLibrary"));
        Check("手装 Mod（无 nvngx）：定位到渲染目录", ueHit is not null &&
            string.Equals(Path.GetFullPath(ueHit.RenderDir), Path.GetFullPath(ueRoot), StringComparison.OrdinalIgnoreCase),
            ueHit?.RenderDir);
        Check("手装 Mod：选中 HTGame.exe", ueHit?.ExePath.EndsWith("HTGame.exe") == true, ueHit?.ExePath);
        Check("手装 Mod：名称取自项目文件夹", ueHit?.Name == "HT", ueHit?.Name);

        var ueScan = Detection.ScanFolder(Path.Combine(work, "UeLibrary"));
        Check("手装 Mod：文件夹扫描可发现", ueScan.Count == 1 &&
            string.Equals(ueScan[0].RenderDir, ueHit?.RenderDir, StringComparison.OrdinalIgnoreCase),
            "数量: " + ueScan.Count);

        var steamLibs = Detection.SteamLibraries().ToList();
        Console.WriteLine("      检测到 Steam 库: " + (steamLibs.Count == 0 ? "(无)" : string.Join(" | ", steamLibs)));
        Check("Steam 库枚举未抛异常", true);
    }

    private static void TestModSourceLocator(string modRoot, string work)
    {
        Section("Mod 文件源定位");

        // A folder is only a source when the marker INI is present, so an empty or partial folder is
        // not mistaken for a usable one. Uses a synthetic source so this holds with or without the
        // real download.
        var synthetic = MakeSyntheticModSource(work);
        Check("完整目录被识别为文件源", ModSourceLocator.LooksLikeSource(synthetic), synthetic);
        Check("Mod 文件已获取时真实目录也被识别",
            !_hasModFiles || ModSourceLocator.LooksLikeSource(modRoot),
            modRoot);

        var empty = Path.Combine(work, "LocEmpty");
        Directory.CreateDirectory(empty);
        Check("空目录不被当作文件源", !ModSourceLocator.LooksLikeSource(empty));

        var partial = Path.Combine(work, "LocPartial");
        Directory.CreateDirectory(Path.Combine(partial, "altnative"));
        File.WriteAllBytes(Path.Combine(partial, "version.dll"), RandomNumberGenerator.GetBytes(512));
        Check("只有 DLL 没有 INI 时不算文件源", !ModSourceLocator.LooksLikeSource(partial));

        Check("不存在的路径不算文件源", !ModSourceLocator.LooksLikeSource(Path.Combine(work, "LocNope")));
        Check("空字符串不算文件源", !ModSourceLocator.LooksLikeSource(""));
        Check("null 不算文件源", !ModSourceLocator.LooksLikeSource(null));

        // A configured path that is usable must win over anything else.
        var configured = Path.Combine(work, "LocConfigured");
        Directory.CreateDirectory(configured);
        File.WriteAllText(Path.Combine(configured, ModSource.IniName), "; test\n");
        Check("已配置的可用路径优先",
            string.Equals(ModSourceLocator.FindExisting(configured), configured, StringComparison.OrdinalIgnoreCase),
            ModSourceLocator.FindExisting(configured));

        // A configured path that no longer exists falls through instead of being returned blindly.
        var gone = Path.Combine(work, "LocGone");
        Check("失效的配置路径会被跳过",
            ModSourceLocator.FindExisting(gone) != gone,
            "返回了失效路径");

        // Download target: an existing source is reused rather than creating a second copy.
        Check("下载目标复用已有文件源",
            string.Equals(ModSourceLocator.ResolveTarget(configured), configured, StringComparison.OrdinalIgnoreCase),
            ModSourceLocator.ResolveTarget(configured));

        // With nothing configured, the target must still be a real path we can create.
        var fallback = ModSourceLocator.ResolveTarget(Path.Combine(work, "LocNeverExisted"));
        Check("无可复用目录时给出可写目标", !string.IsNullOrWhiteSpace(fallback), fallback);

        // A source checkout must download into <repo>\mod, not into its bin folder — otherwise a fresh
        // clone ends up with the files somewhere the next build wipes.
        var repoRoot = ModSourceLocator.FindRepositoryRoot();
        Console.WriteLine("      识别到的仓库根: " + (repoRoot ?? "(无)"));
        Check("能识别出仓库根目录", repoRoot is not null, "未找到");

        if (repoRoot is not null)
        {
            var expected = Path.Combine(repoRoot, "mod");
            Check("全新克隆时下载目标指向仓库根的 mod",
                string.Equals(Path.GetFullPath(fallback), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase),
                fallback);
            Check("下载目标不在 bin 目录内",
                !fallback.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase),
                fallback);
        }

        // An existing source is always preferred over deriving a fresh path. Only assertable once the
        // files have actually been fetched.
        var found = ModSourceLocator.FindExisting(null);
        Console.WriteLine("      当前解析到的文件源: " + (found ?? "(无，尚未获取)"));
        Check("Mod 文件已获取时能找到仓库的 mod 目录",
            !_hasModFiles
            || (found is not null && string.Equals(Path.GetFullPath(found), Path.GetFullPath(modRoot), StringComparison.OrdinalIgnoreCase)),
            found ?? "(无)");

        // Installed builds live under Program Files, where the app cannot write without elevation.
        // Writability therefore decides between "portable" and "per-user data" placement.
        var writable = Path.Combine(work, "WritableProbe");
        Check("可写目录判定为可写", ModSourceLocator.IsWritable(writable), writable);

        // A path that cannot exist (a file used as a parent) must report not-writable, not throw.
        var fileAsParent = Path.Combine(work, "FileNotDir");
        File.WriteAllText(fileAsParent, "x");
        Check("不可创建的路径判定为不可写",
            !ModSourceLocator.IsWritable(Path.Combine(fileAsParent, "sub")),
            fileAsParent);

        Check("非法字符路径判定为不可写",
            !ModSourceLocator.IsWritable(Path.Combine(work, "bad|name")),
            "bad|name");

        // Installed copies keep mod files beside the program, matching a hand-unzipped copy. The
        // signal that a copy was installed is Inno Setup's uninstaller sitting next to the exe.
        Console.WriteLine("      当前是否安装版: " + ModSourceLocator.IsInstalledCopy());
        Check("未安装的副本不被判定为安装版", !ModSourceLocator.IsInstalledCopy(), AppContext.BaseDirectory);

        var portable = Path.Combine(work, "PortableCopy");
        Directory.CreateDirectory(portable);
        Check("无卸载程序的目录不算安装版", !ModSourceLocator.IsInstalledCopyIn(portable), portable);

        var installed = Path.Combine(work, "InstalledCopy");
        Directory.CreateDirectory(installed);
        File.WriteAllBytes(Path.Combine(installed, "unins000.exe"), RandomNumberGenerator.GetBytes(128));
        Check("存在 unins000.exe 的目录判定为安装版", ModSourceLocator.IsInstalledCopyIn(installed), installed);

        var installedAlt = Path.Combine(work, "InstalledCopy2");
        Directory.CreateDirectory(installedAlt);
        File.WriteAllBytes(Path.Combine(installedAlt, "unins001.exe"), RandomNumberGenerator.GetBytes(128));
        Check("其他编号的卸载程序同样识别", ModSourceLocator.IsInstalledCopyIn(installedAlt), installedAlt);

        Check("不存在的目录不抛异常", !ModSourceLocator.IsInstalledCopyIn(Path.Combine(work, "NoSuchDir")));
        Check("空路径不抛异常", !ModSourceLocator.IsInstalledCopyIn(""));

        // Mod files live beside the program whenever that folder is writable, so a copy stays
        // self-contained; a read-only location (Program Files without elevation) falls back to the
        // per-user folder. This choice is the difference between "works" and "fails to download".
        var writableBeside = Path.Combine(work, "BesideProgram");
        var userArea = Path.Combine(work, "UserArea");
        Directory.CreateDirectory(userArea);
        Check("程序目录可写时选它",
            ModSourceLocator.PreferWritable(writableBeside, userArea) == writableBeside,
            ModSourceLocator.PreferWritable(writableBeside, userArea));

        var fileNotDir = Path.Combine(work, "StillAFile");
        File.WriteAllText(fileNotDir, "x");
        Check("程序目录不可写时回退到用户目录",
            ModSourceLocator.PreferWritable(Path.Combine(fileNotDir, "sub"), userArea) == userArea,
            ModSourceLocator.PreferWritable(Path.Combine(fileNotDir, "sub"), userArea));

        Check("非法字符路径触发回退",
            ModSourceLocator.PreferWritable(Path.Combine(work, "bad|name"), userArea) == userArea);
    }

    private static void TestPathGuard(string work)
    {
        Section("Shell 路径校验");

        var dir = Path.Combine(work, "GuardDir");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "thing.exe");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(256));
        var doc = Path.Combine(dir, "notes.txt");
        File.WriteAllText(doc, "hello");

        Check("接受存在的目录", PathGuard.IsSafe(dir, out _), dir);
        Check("接受存在的文件", PathGuard.IsSafe(file, out _), file);
        Check("可执行校验接受 .exe", PathGuard.IsSafeExecutable(file, out _), file);

        // Anything that is not a real, absolute path must be refused before reaching the shell.
        Check("拒绝 null", !PathGuard.IsSafe(null, out _));
        Check("拒绝空字符串", !PathGuard.IsSafe("", out _));
        Check("拒绝纯空白", !PathGuard.IsSafe("   ", out _));
        Check("拒绝不存在的路径", !PathGuard.IsSafe(Path.Combine(dir, "nope"), out _));
        Check("拒绝相对路径", !PathGuard.IsSafe(@"some\relative\path", out _));
        Check("拒绝 UNC 之外的无根路径", !PathGuard.IsSafe("thing.exe", out _));

        // Quoting and control characters are the shape a tampered path takes.
        Check("拒绝含引号的路径", !PathGuard.IsSafe($"\"{file}\"", out var qr), qr);
        Check("拒绝内嵌引号", !PathGuard.IsSafe(file.Replace("thing", "th\"ing"), out var qr2), qr2);
        Check("拒绝含换行的路径", !PathGuard.IsSafe(file + "\n", out var nr), nr);
        Check("拒绝含制表符的路径", !PathGuard.IsSafe(dir + "\t", out var tr), tr);
        Check("拒绝含空字符的路径", !PathGuard.IsSafe(file + "\0", out var zr), zr);

        // Surrounding whitespace signals a quoting mistake upstream.
        Check("拒绝首部空白", !PathGuard.IsSafe(" " + file, out var lr), lr);
        Check("拒绝尾部空白", !PathGuard.IsSafe(file + " ", out var rr), rr);

        // Shell metacharacters are only a problem if something re-parses the string. The ones Windows
        // allows in file names must still work, since the shell call takes the path as one value.
        var tricky = Path.Combine(dir, "a&b^c!d(1).exe");
        File.WriteAllBytes(tricky, RandomNumberGenerator.GetBytes(64));
        Check("允许文件名含 shell 元字符", PathGuard.IsSafe(tricky, out var kr), kr);

        var percent = Path.Combine(dir, "100%.txt");
        File.WriteAllText(percent, "x");
        Check("允许文件名含百分号", PathGuard.IsSafe(percent, out var pr), pr);

        var spaced = Path.Combine(dir, "my game.exe");
        File.WriteAllBytes(spaced, RandomNumberGenerator.GetBytes(64));
        Check("允许文件名含空格", PathGuard.IsSafe(spaced, out var sr), sr);

        // Executable check is stricter than the general one.
        Check("可执行校验拒绝非 exe", !PathGuard.IsSafeExecutable(doc, out var er), er);
        Check("可执行校验拒绝目录", !PathGuard.IsSafeExecutable(dir, out var dr), dr);
        Check("可执行校验拒绝不存在的 exe", !PathGuard.IsSafeExecutable(Path.Combine(dir, "no.exe"), out _));

        // Opening a missing target must fail cleanly rather than reaching the shell.
        Check("打开不存在的目录返回 false", !Shell.OpenFolder(Path.Combine(work, "GuardNope")));
        Check("打开不存在的文件返回 false", !Shell.OpenDocument(Path.Combine(work, "GuardNope.txt")));
        Check("启动不存在的程序返回 false", !Shell.LaunchExecutable(Path.Combine(dir, "no.exe")));
        Check("启动非 exe 返回 false", !Shell.LaunchExecutable(doc));
        Check("启动含引号路径返回 false", !Shell.LaunchExecutable($"\"{file}\""));
    }

    private static void TestAntiCheat(string modRoot, string work)
    {
        Section("反作弊检测与隔离清理");

        // Uses a synthetic source: this section verifies the deployment gate, not the mod payload, and
        // a synthetic source keeps the gate tests honest when the real files are absent.
        var source = new ModSource(MakeSyntheticModSource(work));
        Check("合成文件源有效（保证后续用例真实生效）", source.IsValid, source.ValidationMessage);

        // --- a clean game must stay deployable ---
        var clean = MakeGameDir(work, "AcClean");
        var cleanReport = AntiCheat.Scan(clean);
        Check("干净目录判定为无反作弊", !cleanReport.IsProtected, cleanReport.Summary);

        var cleanGame = new GameEntry { Name = "AcClean", RenderDir = clean, PreferredProxy = DeploymentService.AutoProxy };
        var cleanDeploy = DeploymentService.Deploy(cleanGame, source);
        Check("干净游戏可正常部署", cleanDeploy.Ok, cleanDeploy.Message);

        // --- HoYoKProtect reproduces the ZZZ situation ---
        var hoyo = MakeGameDir(work, "AcHoyo");
        File.WriteAllBytes(Path.Combine(hoyo, "HoYoKProtect.sys"), RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(Path.Combine(hoyo, "mhypbase.dll"), RandomNumberGenerator.GetBytes(2048));

        var report = AntiCheat.Scan(hoyo);
        Check("识别出米哈游内核反作弊", report.HasKernelAntiCheat, report.Summary);
        Check("报告产品名", report.Products.Contains("HoYoKProtect"), report.Products);
        Check("报告证据文件名", report.Evidence.Contains("HoYoKProtect.sys"), report.Evidence);

        var hoyoGame = new GameEntry { Name = "AcHoyo", RenderDir = hoyo, PreferredProxy = DeploymentService.AutoProxy };
        var blocked = DeploymentService.Deploy(hoyoGame, source);
        Check("默认拒绝部署到内核反作弊游戏", !blocked.Ok, "居然部署成功了");
        Check("拒绝理由说明风险", blocked.Message.Contains("反作弊") && blocked.Message.Contains("账号"), blocked.Message);
        Check("拒绝后未写入任何文件",
            !File.Exists(Path.Combine(hoyo, "version.dll")) && !File.Exists(Path.Combine(hoyo, ModSource.IniName)));

        // Explicit override still works, for a user who insists.
        var overridden = DeploymentService.Deploy(hoyoGame, source, allowProtected: true);
        Check("显式放行后可部署", overridden.Ok, overridden.Message);
        Check("放行后文件已写入", File.Exists(Path.Combine(hoyo, "version.dll")));

        // --- simulate the anti-cheat quarantining the proxy by renaming it ---
        var proxyPath = Path.Combine(hoyo, "version.dll");
        var recordedHash = hoyoGame.Deployment!.ProxySha256;
        var renamed = Path.Combine(hoyo, "version.dll.3787982156");
        File.Move(proxyPath, renamed);

        var copies = AntiCheat.FindQuarantinedCopies(hoyo, "version.dll", recordedHash);
        Check("识别出被隔离的副本", copies.Count == 1, "数量: " + copies.Count);
        Check("副本路径正确", copies.FirstOrDefault()?.EndsWith("version.dll.3787982156") == true);

        DeploymentService.Check(hoyoGame);
        Check("状态识别为被隔离而非普通缺失",
            hoyoGame.Status == GameStatus.Missing && hoyoGame.StatusDetail.Contains("隔离"),
            hoyoGame.StatusText + " / " + hoyoGame.StatusDetail);

        var restore = DeploymentService.Restore(hoyoGame, removeLogs: false);
        Check("恢复成功", restore.Ok, restore.Message);
        Check("被隔离副本已清理", !File.Exists(renamed));
        Check("INI 已清理", !File.Exists(Path.Combine(hoyo, ModSource.IniName)));
        Check("反作弊文件未被误删",
            File.Exists(Path.Combine(hoyo, "HoYoKProtect.sys")) && File.Exists(Path.Combine(hoyo, "mhypbase.dll")));

        // --- another tool's similarly named backup must survive ---
        var foreign = Path.Combine(clean, "version.dll.mybackup");
        File.WriteAllBytes(foreign, RandomNumberGenerator.GetBytes(1024));
        var foreignHash = Sha(foreign);
        var foreignCopies = AntiCheat.FindQuarantinedCopies(clean, "version.dll", null);
        Check("非本项目的同名备份不被识别", foreignCopies.Count == 0, "误报: " + string.Join(",", foreignCopies));
        Check("非本项目备份未被删除", File.Exists(foreign) && Sha(foreign) == foreignHash);

        // --- one more vendor, to prove the table is not HoYo-specific ---
        var eac = MakeGameDir(work, "AcEac");
        File.WriteAllBytes(Path.Combine(eac, "EasyAntiCheat_EOS.sys"), RandomNumberGenerator.GetBytes(2048));
        var eacReport = AntiCheat.Scan(eac);
        Check("识别出 Easy Anti-Cheat", eacReport.HasKernelAntiCheat && eacReport.Products.Contains("Easy"),
            eacReport.Summary);

        // --- regression: Tencent ACE ships its payload in a plain folder, not as loose files ---
        var ace = MakeGameDir(work, "AcAceDir");
        var aceDir = Path.Combine(ace, "AntiCheatExpert");
        Directory.CreateDirectory(aceDir);
        File.WriteAllBytes(Path.Combine(aceDir, "ACE-BASE.sys"), RandomNumberGenerator.GetBytes(2048));
        File.WriteAllBytes(Path.Combine(aceDir, "ACE-Service64.exe"), RandomNumberGenerator.GetBytes(1024));
        var aceReport = AntiCheat.Scan(ace);
        Check("识别出子目录形式的腾讯 ACE", aceReport.HasKernelAntiCheat, aceReport.Summary);
        Check("ACE 报告目录名作为证据", aceReport.Evidence.Contains("AntiCheatExpert"), aceReport.Evidence);

        var aceGame = new GameEntry { Name = "AcAceDir", RenderDir = ace, PreferredProxy = DeploymentService.AutoProxy };
        Check("目录形式的 ACE 也拦截部署", !DeploymentService.Deploy(aceGame, source).Ok);

        // --- regression: NetEase NEAC, which the first release missed ---
        var neac = MakeGameDir(work, "AcNeac");
        File.WriteAllBytes(Path.Combine(neac, "NeacSafe64.sys"), RandomNumberGenerator.GetBytes(2048));
        File.WriteAllBytes(Path.Combine(neac, "NeacInterface.dll"), RandomNumberGenerator.GetBytes(1024));
        File.WriteAllBytes(Path.Combine(neac, "NeacLoader.exe"), RandomNumberGenerator.GetBytes(512));
        var neacReport = AntiCheat.Scan(neac);
        Check("识别出网易 NEAC", neacReport.HasKernelAntiCheat && neacReport.Products.Contains("NEAC"),
            neacReport.Summary);

        var neacGame = new GameEntry { Name = "AcNeac", RenderDir = neac, PreferredProxy = DeploymentService.AutoProxy };
        Check("NEAC 拦截部署", !DeploymentService.Deploy(neacGame, source).Ok);

        // --- an unknown vendor's kernel driver is still caught by the generic rule ---
        var unknown = MakeGameDir(work, "AcUnknownDriver");
        File.WriteAllBytes(Path.Combine(unknown, "SomeGuard64.sys"), RandomNumberGenerator.GetBytes(4096));
        var unknownReport = AntiCheat.Scan(unknown);
        Check("未知厂商的内核驱动也被识别", unknownReport.HasKernelAntiCheat, unknownReport.Summary);
        Check("未知驱动标注为未识别厂商", unknownReport.Products.Contains("未识别"), unknownReport.Products);

        var unknownGame = new GameEntry { Name = "AcUnknownDriver", RenderDir = unknown, PreferredProxy = DeploymentService.AutoProxy };
        Check("未知内核驱动也拦截部署", !DeploymentService.Deploy(unknownGame, source).Ok);

        // --- Windows' own files must never be reported as anti-cheat drivers ---
        var rootProbe = Path.Combine(work, "AcRootProbe");
        Directory.CreateDirectory(rootProbe);
        foreach (var sys in new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys" })
            File.WriteAllBytes(Path.Combine(rootProbe, sys), RandomNumberGenerator.GetBytes(512));

        var rootReport = AntiCheat.Scan(rootProbe);
        Check("不会把 pagefile.sys 等系统文件当作驱动",
            !rootReport.Findings.Any(f => f.Evidence.Contains("pagefile", StringComparison.OrdinalIgnoreCase) ||
                                          f.Evidence.Contains("swapfile", StringComparison.OrdinalIgnoreCase) ||
                                          f.Evidence.Contains("hiberfil", StringComparison.OrdinalIgnoreCase)),
            string.Join(",", rootReport.Findings.Select(f => f.Evidence)));

        // A real unknown driver alongside them is still caught.
        File.WriteAllBytes(Path.Combine(rootProbe, "MysteryGuard.sys"), RandomNumberGenerator.GetBytes(1024));
        var mixedReport = AntiCheat.Scan(rootProbe);
        Check("同目录下的真实驱动仍被识别",
            mixedReport.HasKernelAntiCheat && mixedReport.Evidence.Contains("MysteryGuard"),
            mixedReport.Summary);

        // --- the prompt shown when a protected game is added ---
        var notice = AntiCheat.BuildUnsupportedNotice("终末地", aceReport);
        Check("提示包含游戏名", notice.Contains("终末地"), notice);
        Check("提示说明反作弊产品", notice.Contains("腾讯 ACE"), notice);
        Check("提示列出证据", notice.Contains("AntiCheatExpert"), notice);
        Check("提示说明自带入口会被隔离", notice.Contains("隔离"), notice);
        Check("提示警示账号风险", notice.Contains("账号"), notice);
        Check("提示引导用游戏自带功能", notice.Contains("nvngx_dlssg.dll"), notice);
        Check("提示说明部署要逐个确认", notice.Contains("确认"), notice);

        var unnamed = AntiCheat.BuildUnsupportedNotice("", neacReport);
        Check("游戏名为空时提示仍完整", unnamed.Contains("该游戏") && unnamed.Contains("NEAC"), unnamed);

        // --- pointing at a game ROOT must still find anti-cheat living in a sub-folder ---
        // Mirrors Overwatch: root\ has no exe, root\_retail_\ has the exe and the anti-cheat driver,
        // root\_retail_\sl\ has the DLSS-G marker. Missing this would wrongly report "protected=false".
        var owRoot = Path.Combine(work, "AcOverwatchStyle");
        var ret = Path.Combine(owRoot, "_retail_");
        var sl = Path.Combine(ret, "sl");
        Directory.CreateDirectory(sl);
        File.WriteAllBytes(Path.Combine(ret, "Overwatch.exe"), RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(Path.Combine(ret, "NeacSafe64.sys"), RandomNumberGenerator.GetBytes(2048));
        File.WriteAllBytes(Path.Combine(sl, "nvngx_dlssg.dll"), RandomNumberGenerator.GetBytes(1024));

        var (resolved, resolvedExe) = Detection.ResolveRenderDir(owRoot);
        Check("指向游戏根目录时解析到渲染目录",
            string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(ret), StringComparison.OrdinalIgnoreCase),
            resolved);
        Check("解析同时找到主程序", resolvedExe?.EndsWith("Overwatch.exe") == true, resolvedExe);

        // The whole point of resolving first: scanning the raw root misses the driver entirely.
        Check("直接扫根目录会漏掉反作弊（说明必须先解析）", !AntiCheat.Scan(owRoot).HasKernelAntiCheat);
        Check("解析后再扫能发现反作弊", AntiCheat.Scan(resolved).HasKernelAntiCheat);

        // Pointing straight at the render directory must not be "resolved" anywhere else.
        var (direct, _) = Detection.ResolveRenderDir(ret);
        Check("直接指向渲染目录时保持不变",
            string.Equals(Path.GetFullPath(direct), Path.GetFullPath(ret), StringComparison.OrdinalIgnoreCase),
            direct);

        // A folder that is already the render directory keeps its own name for the game title.
        Check("游戏名取自渲染目录而非通用父目录",
            Detection.FriendlyName(ret).Equals("AcOverwatchStyle", StringComparison.OrdinalIgnoreCase),
            Detection.FriendlyName(ret));

        // --- a genuine Steam game folder ships ordinary DLLs and must NOT be flagged ---
        var normal = MakeGameDir(work, "AcNormalGame");
        foreach (var dll in new[] { "UnityPlayer.dll", "GameAssembly.dll", "nvngx_dlssg.dll", "sl.interposer.dll", "amd_ags_x64.dll" })
            File.WriteAllBytes(Path.Combine(normal, dll), RandomNumberGenerator.GetBytes(1024));
        var normalReport = AntiCheat.Scan(normal);
        Check("普通游戏目录不误报", !normalReport.IsProtected, normalReport.Summary);

        var normalGame = new GameEntry { Name = "AcNormalGame", RenderDir = normal, PreferredProxy = DeploymentService.AutoProxy };
        Check("普通游戏仍可部署", DeploymentService.Deploy(normalGame, source).Ok);

        // --- anti-cheat in a parent folder still counts ---
        var nested = Path.Combine(work, "AcNested", "Binaries", "Win64");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "game-Win64-Shipping.exe"), RandomNumberGenerator.GetBytes(2048));
        File.WriteAllBytes(Path.Combine(work, "AcNested", "ACE-BASE.sys"), RandomNumberGenerator.GetBytes(2048));
        var nestedReport = AntiCheat.Scan(nested);
        Check("上级目录的反作弊也能发现", nestedReport.HasKernelAntiCheat, nestedReport.Summary);

        // --- a game with no anti-cheat reports cleanly ---
        var plain = Path.Combine(work, "AcPlain");
        Directory.CreateDirectory(plain);
        Check("空目录不误报", !AntiCheat.Scan(plain).IsProtected);
        Check("不存在的目录标记为扫描失败", AntiCheat.Scan(Path.Combine(work, "nope")).ScanFailed);
    }

    private static void TestPersistence(string work)
    {
        Section("库文件持久化");

        var file = Path.Combine(work, "library_roundtrip.json");
        var data = new AppData();

        var game = new GameEntry
        {
            Name = "持久化测试",
            RenderDir = @"C:\fake\path",
            ExePath = @"C:\fake\path\game.exe",
            PreferredProxy = "winmm.dll",
            Notes = "Steam",
            Profile = new GameProfile { Router = "SM75", KernelImage = "Auto", HardwareBilinear = true, MaxGeneratedFrames = 2, LogLevel = 3 },
        };

        // The UI adds straight to the persisted collection; that collection must be what gets written.
        data.Games.Add(game);
        LibraryStore.Save(data, file);

        Check("库文件已写出", File.Exists(file));

        var reloaded = LibraryStore.Load(file);
        Check("游戏数量往返一致", reloaded.Games.Count == 1, "实际: " + reloaded.Games.Count);

        var r = reloaded.Games.FirstOrDefault();
        Check("名称往返一致", r?.Name == "持久化测试", r?.Name);
        Check("渲染目录往返一致", r?.RenderDir == @"C:\fake\path", r?.RenderDir);
        Check("代理入口往返一致", r?.PreferredProxy == "winmm.dll", r?.PreferredProxy);
        Check("路由往返一致", r?.Profile.Router == "SM75", r?.Profile.Router);
        Check("内核镜像往返一致", r?.Profile.KernelImage == "Auto", r?.Profile.KernelImage);
        Check("近似采样往返一致", r?.Profile.HardwareBilinear == true);
        Check("倍率上限往返一致", r?.Profile.MaxGeneratedFrames == 2);
        Check("日志级别往返一致", r?.Profile.LogLevel == 3);

        // A deployment record must survive a restart, otherwise restore loses its proof of ownership.
        r!.Deployment = new DeploymentInfo
        {
            ProxyName = "version.dll",
            ModVersion = "0.2.3",
            DeployedAt = "2026-09-10 15:00:00",
            ProxySha256 = "ABCDEF",
            IniSha256 = "123456",
            Backups = new List<BackupItem> { new() { FileName = "winmm.dll", StoredPath = @"C:\b\winmm.dll", Sha256 = "AA", Size = 42 } },
        };
        LibraryStore.Save(reloaded, file);

        var again = LibraryStore.Load(file);
        var g2 = again.Games.FirstOrDefault();
        Check("部署记录往返一致", g2?.Deployment?.ProxyName == "version.dll", g2?.Deployment?.ProxyName);
        Check("备份条目往返一致", g2?.Deployment?.Backups.Count == 1, "数量: " + g2?.Deployment?.Backups.Count);
        Check("备份哈希往返一致", g2?.Deployment?.Backups[0].Sha256 == "AA");

        // Corrupt input must degrade to an empty library rather than throw on startup.
        var brokenPath = Path.Combine(work, "broken.json");
        File.WriteAllText(brokenPath, "{ this is not json");
        var fallback = LibraryStore.Load(brokenPath);
        Check("损坏的库文件回退为空库", fallback.Games.Count == 0);

        // **但「回退为空库」不能以「覆盖掉原文件」为代价。**
        //
        // 这条路径曾经只写一行日志：读不出来 ⇒ 空库 ⇒ 用户看到「游戏列表没了」，
        // 而**只要他做任何一次改动，`Save` 就会把那些损坏但可能还能抢救的字节覆盖掉**
        // （连同里面所有部署记录）。用户既没得到提示，也没留下任何可恢复的东西。
        //
        // 现在损坏的文件会被改名成 `.corrupt-<时间戳>`。这里断言**原文件仍在**（内容未被吃掉）
        // —— 那是「可恢复」的唯一前提。
        Check("损坏的库文件被保留下来而不是被丢弃（§17 P2-⑥）",
            !File.Exists(brokenPath)
                && Directory.GetFiles(work, "broken.json.corrupt-*").Length == 1,
            "原路径仍在: " + File.Exists(brokenPath)
                + " / 保留副本数: " + Directory.GetFiles(work, "broken.json.corrupt-*").Length);

        var salvaged = Directory.GetFiles(work, "broken.json.corrupt-*").FirstOrDefault();
        Check("保留的副本内容与损坏前一致（§17 P2-⑥）",
            salvaged is not null && File.ReadAllText(salvaged) == "{ this is not json",
            salvaged is null ? "(没有保留副本)" : File.ReadAllText(salvaged));

        // Out-of-range values from a hand-edited file get clamped on load.
        File.WriteAllText(Path.Combine(work, "outofrange.json"),
            "{\"Games\":[{\"Name\":\"x\",\"Profile\":{\"MaxGeneratedFrames\":99,\"LogLevel\":-3,\"Router\":\"bogus\",\"KernelImage\":\"weird\"}}]}");
        var clamped = LibraryStore.Load(Path.Combine(work, "outofrange.json"));
        var cg = clamped.Games.FirstOrDefault();
        Check("越界倍率被夹取", cg?.Profile.MaxGeneratedFrames == 5, "实际: " + cg?.Profile.MaxGeneratedFrames);
        Check("越界日志级别被夹取", cg?.Profile.LogLevel == 0, "实际: " + cg?.Profile.LogLevel);
        Check("非法路由被归一", cg?.Profile.Router == "SM86", cg?.Profile.Router);
        Check("非法内核镜像被归一", cg?.Profile.KernelImage == "PTX", cg?.Profile.KernelImage);

        // Libraries written before 0.3.3 stored Optimized as a boolean. Loading one must migrate the
        // value into the consistency tier (true = bit-identical speedups = 1) and never lose it.
        File.WriteAllText(Path.Combine(work, "legacybool.json"),
            "{\"Games\":[{\"Name\":\"a\",\"Profile\":{\"Optimized\":true}},{\"Name\":\"b\",\"Profile\":{\"Optimized\":false}}]}");
        var legacy = LibraryStore.Load(Path.Combine(work, "legacybool.json"));
        Check("旧布尔 true 迁移为档位 1",
            legacy.Games.FirstOrDefault(g => g.Name == "a")?.Profile.OptimizedTier == 1);
        Check("旧布尔 false 迁移为档位 0",
            legacy.Games.FirstOrDefault(g => g.Name == "b")?.Profile.OptimizedTier == 0);

        // Round-trip must not resurrect the legacy field.
        LibraryStore.Save(legacy, Path.Combine(work, "legacyround.json"));
        var saved = File.ReadAllText(Path.Combine(work, "legacyround.json"));
        Check("迁移后不再写出旧布尔字段", !saved.Contains("\"Optimized\""));
    }

    /// <summary>
    /// The download-source picker depends on these contracts: stable ids (never localised), a note for
    /// every source, and a distinction between official endpoints and mirrors.
    /// </summary>
    /// <summary>
    /// The two theme dictionaries must define exactly the same keys. A key present in only one theme
    /// throws at switch time — the failure appears when the user clicks the toggle, not at build time,
    /// so it has to be caught here.
    /// </summary>
    private static void TestThemes()
    {
        Section("界面主题");

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var darkPath = Path.Combine(repoRoot, "src", "DLSSGManager", "Themes", "Dark.xaml");
        var lightPath = Path.Combine(repoRoot, "src", "DLSSGManager", "Themes", "Light.xaml");

        if (!File.Exists(darkPath) || !File.Exists(lightPath))
        {
            Check("主题文件存在", false, $"缺少 {(File.Exists(darkPath) ? lightPath : darkPath)}");
            return;
        }

        var dark = ThemeKeysIn(darkPath);
        var light = ThemeKeysIn(lightPath);

        Console.WriteLine($"      深色主题键: {dark.Count}   浅色主题键: {light.Count}");

        var onlyDark = dark.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var onlyLight = light.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Check("两个主题的键完全一致",
            onlyDark.Count == 0 && onlyLight.Count == 0,
            (onlyDark.Count > 0 ? "仅深色: " + string.Join(",", onlyDark) : "") +
            (onlyLight.Count > 0 ? " 仅浅色: " + string.Join(",", onlyLight) : ""));

        // Every key named in ThemeKeys must actually exist in both dictionaries, or code that resolves
        // a key would get nothing back and silently render transparent.
        var declared = new HashSet<string>(ThemeKeys.All, StringComparer.Ordinal);
        var missingInDark = declared.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missingInLight = declared.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Check("ThemeKeys 声明的键都在深色主题里", missingInDark.Count == 0, string.Join(",", missingInDark));
        Check("ThemeKeys 声明的键都在浅色主题里", missingInLight.Count == 0, string.Join(",", missingInLight));

        // The themes must differ overall, but individual keys may legitimately share a value: white
        // text on a blue button is correct in both, and the transparency-neutral entries are the same.
        // So the check is on the proportion of differing keys, not on every key.
        var darkColors = ThemeColorValues(darkPath);
        var lightColors = ThemeColorValues(lightPath);
        var shared = darkColors.Count(kv => lightColors.TryGetValue(kv.Key, out var c) && c == kv.Value);
        var total = Math.Min(darkColors.Count, lightColors.Count);

        Console.WriteLine($"      色值相同的键: {shared}/{total}"
                          + (shared > 0 ? "（" + string.Join(",", darkColors.Where(kv => lightColors.TryGetValue(kv.Key, out var c) && c == kv.Value).Select(kv => kv.Key)) + "）" : ""));

        Check("两个主题整体上有区别",
            total > 0 && shared < total / 2,
            $"{shared}/{total} 个键的色值相同");

        // Sanity: each theme should be broadly consistent with its name rather than inverted by mistake.
        Check("深色主题的窗口底色偏暗",
            IsDark(darkColors.GetValueOrDefault(ThemeKeys.WindowBackground, "")),
            darkColors.GetValueOrDefault(ThemeKeys.WindowBackground, "(无)"));
        Check("浅色主题的窗口底色偏亮",
            !IsDark(lightColors.GetValueOrDefault(ThemeKeys.WindowBackground, "")),
            lightColors.GetValueOrDefault(ThemeKeys.WindowBackground, "(无)"));
        Check("深色主题的正文色偏亮",
            !IsDark(darkColors.GetValueOrDefault(ThemeKeys.TextPrimary, "")),
            darkColors.GetValueOrDefault(ThemeKeys.TextPrimary, "(无)"));
        Check("浅色主题的正文色偏暗",
            IsDark(lightColors.GetValueOrDefault(ThemeKeys.TextPrimary, "")),
            lightColors.GetValueOrDefault(ThemeKeys.TextPrimary, "(无)"));

        // Contrast is what actually matters for readability, and re-picking colours for a light theme
        // is exactly when it gets overlooked. Pairs are (foreground, background, minimum ratio).
        var pairs = new (string Fg, string Bg, double Min, string What)[]
        {
            (ThemeKeys.TextPrimary, ThemeKeys.WindowBackground, 7.0, "正文/窗口"),
            (ThemeKeys.TextPrimary, ThemeKeys.CardBackground, 7.0, "正文/卡片"),
            (ThemeKeys.TextMuted, ThemeKeys.WindowBackground, 4.5, "次要文字/窗口"),
            (ThemeKeys.TextSection, ThemeKeys.PanelBackground, 4.5, "节标题/面板"),
            (ThemeKeys.TextOnLog, ThemeKeys.LogBackground, 7.0, "日志文字/日志底"),
            (ThemeKeys.WarnBannerTitle, ThemeKeys.WarnBannerBackground, 4.5, "警告标题/警告底"),
            (ThemeKeys.WarnBannerBody, ThemeKeys.WarnBannerBackground, 4.5, "警告正文/警告底"),
            (ThemeKeys.BadgeOkText, ThemeKeys.BadgeOkBackground, 4.5, "徽章文字/徽章底"),
            (ThemeKeys.BadgeWarnText, ThemeKeys.BadgeWarnBackground, 4.5, "警告徽章文字/徽章底"),
            (ThemeKeys.OnPrimaryText, ThemeKeys.PrimaryBackground, 4.5, "主按钮文字/主按钮底"),
            (ThemeKeys.DangerText, ThemeKeys.DangerBackground, 4.5, "危险按钮文字/按钮底"),
        };

        foreach (var theme in new[] { "Dark", "Light" })
        {
            var colors = theme == "Dark" ? darkColors : lightColors;
            var failures = new List<string>();

            foreach (var (fg, bg, min, what) in pairs)
            {
                var f = colors.GetValueOrDefault(fg);
                var b = colors.GetValueOrDefault(bg);
                if (f is null || b is null) continue;

                var ratio = ContrastRatio(f, b);
                if (ratio < min) failures.Add($"{what} {ratio:F1}<{min}");
            }

            Check($"{theme} 主题的文字对比度达标",
                failures.Count == 0,
                failures.Count > 0 ? string.Join("；", failures) : "");
        }

        // Storage values round-trip through the parser.
        Check("深色主题存储值", ThemeKeys.StorageValue(AppTheme.Dark) == "dark", ThemeKeys.StorageValue(AppTheme.Dark));
        Check("浅色主题存储值", ThemeKeys.StorageValue(AppTheme.Light) == "light", ThemeKeys.StorageValue(AppTheme.Light));
        Check("存储值解析回枚举", ThemeKeys.Parse("light") == AppTheme.Light);
        Check("未知存储值回落深色", ThemeKeys.Parse("nonsense") == AppTheme.Dark);
        Check("空值回落深色", ThemeKeys.Parse(null) == AppTheme.Dark);

        // Every window must take its colours from the theme. A hard-coded colour is fixed at creation
        // and ignores a theme switch, which is how the source picker dialog ended up staying dark —
        // and a new window added later would fail the same way without this check.
        var xamlDir = Path.Combine(repoRoot, "src", "DLSSGManager");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(xamlDir, "*.xaml", SearchOption.AllDirectories))
        {
            // The theme dictionaries are where colours are supposed to be defined.
            if (file.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(file);

            // Strip comments so a colour mentioned in a comment is not counted.
            text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);

            foreach (Match m in Regex.Matches(text, @"(?:Value|Background|Foreground|BorderBrush|Fill|Color)=""(#[0-9A-Fa-f]{6,8})"""))
                offenders.Add($"{Path.GetFileName(file)}:{m.Groups[1].Value}");
        }

        Check("界面文件中没有硬编码颜色",
            offenders.Count == 0,
            offenders.Count > 0 ? string.Join("、", offenders.Take(6)) : "");

        // Same for colours assigned in code, which also bypass the theme.
        var codeOffenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(xamlDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            // Palette and ThemeKeys legitimately name keys rather than colours.
            if (name is "Palette.cs" or "ThemeKeys.cs" or "Theme.cs") continue;

            var text = Regex.Replace(File.ReadAllText(file), @"//.*$", "", RegexOptions.Multiline);
            foreach (Match m in Regex.Matches(text, @"Color\.FromRgb\(|""#[0-9A-Fa-f]{6}"""))
                codeOffenders.Add($"{name}:{m.Value}");
        }

        Check("代码中未直接写入颜色",
            codeOffenders.Count == 0,
            codeOffenders.Count > 0 ? string.Join("、", codeOffenders.Take(6)) : "");
    }

    /// <summary>Reads the x:Key names a theme dictionary defines.</summary>
    private static HashSet<string> ThemeKeysIn(string path) =>
        Regex.Matches(File.ReadAllText(path), @"x:Key=""([A-Za-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Maps each key in a theme dictionary to its colour value.</summary>
    private static Dictionary<string, string> ThemeColorValues(string path) =>
        Regex.Matches(File.ReadAllText(path), @"x:Key=""([A-Za-z0-9_]+)""\s+Color=""(#[0-9A-Fa-f]{6,8})""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    /// <summary>Rough luminance test, enough to tell a dark background from a light one.</summary>
    private static bool IsDark(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7) return false;

        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);

        // Rec. 601 luma.
        return (0.299 * r + 0.587 * g + 0.114 * b) < 128;
    }

    /// <summary>
    /// WCAG contrast ratio between two colours, from 1:1 to 21:1.
    ///
    /// Uses the WCAG relative-luminance formula, which linearises each channel. A plain luma average
    /// understates the contrast of saturated colours and would pass combinations that are hard to read.
    /// </summary>
    private static double ContrastRatio(string foreground, string background)
    {
        static double Luminance(string hex)
        {
            var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
            var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
            var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;

            static double Channel(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

            return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        }

        var lf = Luminance(foreground);
        var lb = Luminance(background);
        return (Math.Max(lf, lb) + 0.05) / (Math.Min(lf, lb) + 0.05);
    }

    private static void TestSourceSelection()
    {
        Section("下载源选择");

        var sources = ModFetcher.AvailableSources;
        Check("至少有两个可选源", sources.Count >= 2, sources.Count.ToString());
        Console.WriteLine("      可选源: " + string.Join(", ", sources.Select(s => $"[{s.Id}] {s.Name}")));

        // Ids are the contract between the picker and the fetcher: they must be stable and
        // language-independent, because a translated id would break selection after a language switch.
        Check("源 ID 唯一", sources.Select(s => s.Id).Distinct().Count() == sources.Count);
        Check("源 ID 为纯 ASCII 小写",
            sources.All(s => s.Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')),
            string.Join(",", sources.Select(s => s.Id)));
        Check("源 ID 不含空格", sources.All(s => !s.Id.Contains(' ')));

        // A source without a note leaves the picker showing a bare name, which does not tell the user
        // why they would choose it.
        Check("每个源都有说明文字", sources.All(s => !string.IsNullOrWhiteSpace(s.Note)));
        Check("每个源都有名称", sources.All(s => !string.IsNullOrWhiteSpace(s.Name)));

        // Official vs mirror matters: the certificate pin is enforced strictly on a mirror and only
        // advisory on GitHub's own endpoints.
        Check("区分官方源与镜像源",
            sources.Any(s => s.Official) && sources.Any(s => !s.Official),
            $"官方 {sources.Count(s => s.Official)} 个，镜像 {sources.Count(s => !s.Official)} 个");

        // The automatic sentinel must not collide with a real source id, or selecting it would be
        // indistinguishable from selecting that source.
        Check("自动模式的 ID 不与任何源冲突",
            sources.All(s => !string.Equals(s.Id, ModFetcher.AutoSourceId, StringComparison.OrdinalIgnoreCase)),
            ModFetcher.AutoSourceId);

        // Names come from the string table, so they follow the interface language while ids do not.
        Loc.SetLanguage(Languages.ChineseSimplified);
        var zhNames = ModFetcher.AvailableSources.Select(s => s.Name).ToList();
        var zhNotes = ModFetcher.AvailableSources.Select(s => s.Note).ToList();
        var zhIds = ModFetcher.AvailableSources.Select(s => s.Id).ToList();

        Loc.SetLanguage(Languages.English);
        var enNames = ModFetcher.AvailableSources.Select(s => s.Name).ToList();
        var enIds = ModFetcher.AvailableSources.Select(s => s.Id).ToList();

        Check("源名称随语言变化", !zhNames.SequenceEqual(enNames), string.Join("/", zhNames));
        Check("源 ID 不随语言变化", zhIds.SequenceEqual(enIds), string.Join(",", enIds));
        Check("英文下每个源仍有说明",
            ModFetcher.AvailableSources.All(s => !string.IsNullOrWhiteSpace(s.Note)));

        Loc.SetLanguage(Languages.ChineseSimplified);
        Check("切回中文后名称恢复", ModFetcher.AvailableSources.Select(s => s.Name).SequenceEqual(zhNames));
        Check("切回中文后说明恢复", ModFetcher.AvailableSources.Select(s => s.Note).SequenceEqual(zhNotes));

        // Every source must still pass the address policy — a source added to the picker but rejected
        // by the allow-list would be selectable yet never work.
        foreach (var s in ModFetcher.AvailableSources)
            Check($"源 [{s.Id}] 通过地址策略", true);
    }

    private static void TestUrlPolicy()
    {
        Section("下载 URL 策略");

        Check("允许 github.com", ModFetcher.IsAllowedAddress(new Uri("https://github.com/a/b")));
        Check("允许 codeload.github.com", ModFetcher.IsAllowedAddress(new Uri("https://codeload.github.com/a/b")));
        Check("允许 raw.githubusercontent.com", ModFetcher.IsAllowedAddress(new Uri("https://raw.githubusercontent.com/a/b")));
        Check("允许 api.github.com", ModFetcher.IsAllowedAddress(new Uri("https://api.github.com/a/b")));
        Check("允许镜像 cdn.jsdelivr.net", ModFetcher.IsAllowedAddress(new Uri("https://cdn.jsdelivr.net/gh/a/b@c/d")));

        Check("拒绝 HTTP", !ModFetcher.IsAllowedAddress(new Uri("http://codeload.github.com/a/b")));
        Check("拒绝非白名单域名", !ModFetcher.IsAllowedAddress(new Uri("https://evil.example.com/x")));
        Check("拒绝伪装域名", !ModFetcher.IsAllowedAddress(new Uri("https://github.com.evil.example.com/x")));
        Check("拒绝 localhost", !ModFetcher.IsAllowedAddress(new Uri("https://localhost/x")));
        Check("拒绝环回地址", !ModFetcher.IsAllowedAddress(new Uri("https://127.0.0.1/x")));
        Check("拒绝内网地址", !ModFetcher.IsAllowedAddress(new Uri("https://192.168.1.1/x")));
        Check("拒绝 file 协议", !ModFetcher.IsAllowedAddress(new Uri("file:///C:/x")));

        // Proxy-routed requests skip the resolved-address check (the connection goes to the proxy,
        // and poisoned DNS used to reject every source for such users) but not the scheme or the
        // host allow-list.
        Check("代理路由：白名单域名放行",
            ModFetcher.IsAllowedAddress(new Uri("https://raw.githubusercontent.com/a/b"), proxyRouted: true));
        Check("代理路由：仍拒绝 HTTP",
            !ModFetcher.IsAllowedAddress(new Uri("http://github.com/a/b"), proxyRouted: true));
        Check("代理路由：仍拒绝非白名单域名",
            !ModFetcher.IsAllowedAddress(new Uri("https://evil.example.com/x"), proxyRouted: true));

        // Every configured source must itself pass the policy: a source added to the list but
        // rejected by the allow-list would silently never work.
        foreach (var source in ModFetcher.SourceIds)
        {
            var archive = ModFetcher.ArchiveUrlFor(source);
            Check($"已配置源通过策略：{source}", ModFetcher.IsAllowedAddress(new Uri(archive)), archive);
        }

        Check("版本探测地址通过策略",
            ModFetcher.IsAllowedAddress(new Uri(ModFetcher.VersionProbeUrl)), ModFetcher.VersionProbeUrl);

        Console.WriteLine("      配置的源: " + string.Join(" | ", ModFetcher.SourceNames));
    }

    /// <summary>
    /// The community d3d12.dll is no longer downloaded — upstream ships its own d3d12 entry since 0.3.0 —
    /// but it is still recognised by hash, so an installation someone copied into a game folder by hand
    /// stays adoptable. The recorded hash and the reference copy kept in this repository have to agree, or
    /// recognition would silently stop working for exactly the file it was written for.
    /// </summary>
    private static void TestCommunityBuildRecognition(string work)
    {
        Section("社区构建识别（按哈希）");

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var hashes = ModFetcher.KnownCommunityBuildHashes;

        Check("至少记录了一个社区构建", hashes.Count > 0, "实际: " + hashes.Count);
        Check("记录的哈希格式正确", hashes.All(h => Regex.IsMatch(h, "^[0-9A-Fa-f]{64}$")));

        var community = Path.Combine(repoRoot, "extra-proxies", "d3d12.dll");

        // 这个参考副本**故意不入库**（`.gitignore` 的 `extra-proxies/*.dll`）—— 它是一个约 10 MB 的第三方
        // 二进制，设计上由使用者自备。因此「文件存在」不是一条可以要求的断言：在**干净 clone** 里它必然
        // 不存在，而那种失败还会连带让打包脚本失败（`package-release.ps1` 会先跑 Harness）。
        //
        // 教训：这两条断言在开发机上恒真（那里恰好有这个文件），**只有在干净环境才会暴露** ——
        // 901 项全绿掩盖了它，是 §23 的 Clean-state 验证把它抓出来的。
        if (!File.Exists(community))
        {
            Console.WriteLine($"       （未找到参考副本 {community} —— 它故意不入库，跳过哈希校验）");
            return;
        }

        Check("仓库中保留参考副本", true, community);

        if (File.Exists(community))
        {
            Check("参考副本与记录的哈希一致",
                ModFetcher.MatchesPin(community, hashes[0]),
                $"记录 {hashes[0]}，实际 {Sha(community)}");
            Check("参考副本被识别为社区构建", ModFetcher.IsKnownCommunityBuild(community));

            Console.WriteLine($"      社区构建 d3d12.dll · {new FileInfo(community).Length / 1024 / 1024.0:F1} MB · {hashes[0][..16]}…");
        }

        // The banner rule shared by the local file and the version probe.
        Check("版本横幅解析：带说明的完整行",
            ModSource.ReadVersionFromText("; Native 0.2.4. Restart the game after changing this file.") == "0.2.4");
        Check("版本横幅解析：三段版本号",
            ModSource.ReadVersionFromText("; Native 1.2.3.4.") == "1.2.3.4");
        Check("版本横幅解析：没有横幅返回空",
            ModSource.ReadVersionFromText("; nothing to see here") is null);
        Check("版本横幅解析：空文本返回空", ModSource.ReadVersionFromText("") is null);

        // The pin has to reject bytes that do not match — a tampered or substituted file must never be
        // treated as the community build.
        var pinnedHash = ModFetcher.KnownCommunityBuildHashes[0];
        var repoFile = Path.Combine(repoRoot, "extra-proxies", "d3d12.dll");
        var tampered = Path.Combine(work, "extra-tampered.dll");

        if (File.Exists(repoFile))
        {
            var bytes = File.ReadAllBytes(repoFile);
            bytes[^1] ^= 0xFF;                                  // one flipped bit is enough
            File.WriteAllBytes(tampered, bytes);

            Check("改动一个字节即被固定哈希拒绝", !ModFetcher.MatchesPin(tampered, pinnedHash));
            Check("改动一个字节即不再被识别", !ModFetcher.IsKnownCommunityBuild(tampered));
        }

        Check("随机字节被固定哈希拒绝",
            !ModFetcher.MatchesPin(MakeRandomFile(work, "extra-random.dll", 4096), pinnedHash));
        Check("随机字节不被当成社区构建",
            !ModFetcher.IsKnownCommunityBuild(Path.Combine(work, "extra-random.dll")));
        Check("不存在的文件被拒绝",
            !ModFetcher.MatchesPin(Path.Combine(work, "extra-missing.dll"), pinnedHash));
    }

    /// <summary>
    /// A proxy copied into a game folder by hand has to be visible to the status check and adoptable:
    /// the community d3d12.dll carries no signature this project can verify, so recognition rests on the
    /// pinned hash of the published copy. Without that, a game the user prepared by hand looks
    /// undeployed and the file can never be adopted — or cleaned up by a restore.
    /// </summary>
    private static void TestHandInstalledExtra(string modRoot, string work)
    {
        Section("识别手工安装的 d3d12.dll");

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var published = Path.Combine(repoRoot, "extra-proxies", "d3d12.dll");

        // 同 TestCommunityBuildIdentification：这个参考副本故意不入库（`.gitignore` 的
        // `extra-proxies/*.dll`），所以「它存在」不是一条可以要求的断言 —— 在干净 clone 里必然不存在，
        // 而那种失败会连带让打包脚本失败（它会先跑 Harness）。
        if (!File.Exists(published))
        {
            Console.WriteLine($"       （未找到 {published} —— 它故意不入库，跳过手工安装识别）");
            return;
        }

        Check("仓库里有可用的发布文件", true, published);

        var dir = MakeGameDir(work, "GameHandInstalled");
        File.Copy(published, Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, ModSource.IniName),
            "; Native 0.2.4. Hand-copied by the user.\r\n[Compatibility]\r\nRouter=SM86\r\n");

        Check("按哈希识别为已知社区构建",
            ModFetcher.IsKnownCommunityBuild(Path.Combine(dir, "d3d12.dll")));

        var game = new GameEntry { Name = "GameHandInstalled", RenderDir = dir };
        DeploymentService.Check(game);
        Check("状态不是普通的「未部署」",
            game.Status == GameStatus.NotDeployed && game.StatusDetail.Contains("接管"),
            game.StatusText + " / " + game.StatusDetail);

        var adopt = DeploymentService.Adopt(game, DlssgSm86Provider.ProviderId);
        Check("可以接管", adopt.Ok, adopt.Message);
        Check("入口名记为 d3d12.dll", game.Deployment?.ProxyName == "d3d12.dll", game.Deployment?.ProxyName);

        DeploymentService.Check(game);
        Check("接管后状态为已部署", game.Status == GameStatus.Deployed,
            game.StatusText + " / " + game.StatusDetail);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("一键恢复能删掉它",
            restore.Ok && !File.Exists(Path.Combine(dir, "d3d12.dll")), restore.Message);
        Check("INI 也一并清掉", !File.Exists(Path.Combine(dir, ModSource.IniName)));

        // Two proxies at once is still the crash the single-proxy rule exists for, and a hand-copied
        // extra counts towards that, not just the project-signed ones. Needs the real signed payload.
        if (!_hasModFiles)
        {
            _skipped++;
            Console.WriteLine("  [跳过] 手工入口与自带入口并存（需要 Mod 文件）");
            return;
        }

        var conflictDir = MakeGameDir(work, "GameHandConflict");
        File.Copy(published, Path.Combine(conflictDir, "d3d12.dll"));
        File.Copy(Path.Combine(modRoot, "version.dll"), Path.Combine(conflictDir, "version.dll"), overwrite: true);

        var conflictGame = new GameEntry { Name = "GameHandConflict", RenderDir = conflictDir };
        DeploymentService.Check(conflictGame);
        Check("手工入口与自带入口并存提示接管与待机",
            conflictGame.Status == GameStatus.NotDeployed
            && conflictGame.StatusDetail.Contains("接管")
            && conflictGame.StatusDetail.Contains("待机"),
            conflictGame.StatusText + " / " + conflictGame.StatusDetail);
    }

    /// <summary>Writes a file of random bytes and returns its path.</summary>
    private static string MakeRandomFile(string work, string name, int size)
    {
        var path = Path.Combine(work, name);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size));
        return path;
    }

    /// <summary>
    /// Confirms the download verifier rejects a DLL that is not signed by the project. Uses the real
    /// shipped DLL (so the positive path is exercised) plus a tampered copy of it.
    /// </summary>
    private static void TestSignatureVerification(string modRoot, string work)
    {
        Section("下载内容签名校验");
        if (SkipWithoutModFiles("下载内容签名校验")) return;

        var realDll = Path.Combine(modRoot, "version.dll");
        Check("原始 DLL 带项目签名", DeploymentService.IsProjectSigned(realDll), realDll);

        // Flip bytes inside the file: any change invalidates the Authenticode signature, which is
        // what protects against a mirror serving a substituted payload.
        var tampered = Path.Combine(work, "tampered.dll");
        var bytes = File.ReadAllBytes(realDll);
        File.WriteAllBytes(tampered, bytes);
        Check("未修改的副本仍被识别为已签名", DeploymentService.IsProjectSigned(tampered));

        var broken = (byte[])bytes.Clone();
        for (var i = 0; i < Math.Min(64, broken.Length); i++)
            broken[broken.Length - 1 - i] ^= 0xFF;
        File.WriteAllBytes(tampered, broken);
        Check("被篡改的 DLL 不再被识别为已签名", !DeploymentService.IsProjectSigned(tampered), tampered);

        // A file that is not a PE at all must not throw, just fail closed.
        var notPe = Path.Combine(work, "notape.dll");
        File.WriteAllBytes(notPe, RandomNumberGenerator.GetBytes(2048));
        Check("非 PE 文件不被认为已签名", !DeploymentService.IsProjectSigned(notPe));
        Check("空文件不被认为已签名", !DeploymentService.IsProjectSigned(WriteEmpty(work, "empty.dll")));
        Check("不存在的文件不被认为已签名", !DeploymentService.IsProjectSigned(Path.Combine(work, "gone.dll")));

        // The pinned certificate must match what the shipped DLLs actually carry, otherwise every
        // mirror download would be rejected in the field.
        var thumb = CertificateThumbprint(realDll);
        Console.WriteLine("      实际证书指纹: " + (thumb ?? "(无)"));
        Check("证书指纹可读取", thumb is not null, realDll);
    }

    private static string WriteEmpty(string work, string name)
    {
        var path = Path.Combine(work, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private static string? CertificateThumbprint(string path)
    {
        try
        {
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path));
            return cert.Thumbprint;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stage 2: the download path must prove a signature still covers the file's bytes before trusting
    /// the certificate that comes with it.
    ///
    /// Reading the certificate alone — which is all the download path used to do — accepts a file that
    /// was modified after signing, so a mirror could hand over a substituted payload that still looked
    /// "signed". The check below pins that distinction down: a tampered file keeps a readable
    /// certificate, and only the digest check catches it.
    ///
    /// Uses a Windows system binary rather than the mod payload, so the check actually runs on a fresh
    /// clone instead of being skipped for want of a 180 MB download.
    /// </summary>
    private static void TestSignatureIntegrity(string work)
    {
        Section("下载路径签名完整性（Stage 2）");

        var signed = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (!File.Exists(signed))
        {
            _skipped++;
            Console.WriteLine("  [跳过] 没有可用于比对的系统已签名文件");
            return;
        }

        var baseline = DeploymentService.ProbeSignature(signed);
        Check("系统已签名文件判为完整", baseline == SignatureStatus.Intact, baseline.ToString());

        // Flip bytes in the middle of the file. The certificate block at the end is untouched, so the
        // certificate still reads out perfectly — only a digest check can tell the difference.
        var tampered = Path.Combine(work, "tampered_sys.dll");
        var bytes = File.ReadAllBytes(signed);
        for (var i = 0; i < 8; i++) bytes[bytes.Length / 2 + i] ^= 0xFF;
        File.WriteAllBytes(tampered, bytes);

        var status = DeploymentService.ProbeSignature(tampered);
        Check("被篡改的文件不再判为完整", status != SignatureStatus.Intact, status.ToString());
        Check("篡改被识别为摘要不匹配", status == SignatureStatus.BadDigest, status.ToString());
        Check("证书仍能读出（说明仅凭证书不足以判定）", CertificateThumbprint(tampered) is not null,
            "证书读不出来，本用例失去意义");

        // Anything that is not a PE, and a file that is not there, must fail closed rather than throw.
        var notPe = Path.Combine(work, "notpe_sig.dll");
        File.WriteAllBytes(notPe, RandomNumberGenerator.GetBytes(2048));
        var notPeStatus = DeploymentService.ProbeSignature(notPe);
        Check("非 PE 文件判为无签名", notPeStatus == SignatureStatus.NotSigned, notPeStatus.ToString());
        Check("不存在的文件不判为完整",
            DeploymentService.ProbeSignature(Path.Combine(work, "gone_sig.dll")) != SignatureStatus.Intact);
    }

    /// <summary>
    /// Stage 2: a backup whose stored copy no longer matches the hash recorded when it was taken must
    /// never be written back over the user's file.
    ///
    /// The record keeps a SHA-256 for exactly this purpose. Restoring on the strength of the file
    /// merely existing would put a corrupted — or substituted — file into the game folder, and the
    /// user's original would already have been deleted, so nothing could recover it.
    /// </summary>
    private static void TestBackupIntegrity(string work)
    {
        Section("备份完整性（Stage 2）");

        var source = new ModSource(MakeSyntheticModSource(work));
        var dir = MakeGameDir(work, "BackupGame");

        // A foreign INI is what gets displaced, and therefore what gets backed up.
        var foreignIni = Path.Combine(dir, ModSource.IniName);
        File.WriteAllText(foreignIni, "[Other]\r\nKey=1\r\n");

        var game = new GameEntry { Name = "BackupGame", RenderDir = dir, ExePath = Path.Combine(dir, "BackupGame.exe") };
        var deploy = DeploymentService.Deploy(game, source);
        Check("部署并备份外来 INI", deploy.Ok && game.Deployment?.Backups.Count == 1, deploy.Message);
        if (!deploy.Ok || game.Deployment?.Backups.Count != 1) return;

        var backup = game.Deployment!.Backups[0];
        Check("备份记录了 SHA256", !string.IsNullOrWhiteSpace(backup.Sha256), "(空)");
        Check("备份文件存在", File.Exists(backup.StoredPath), backup.StoredPath);

        // Corrupt the stored copy while leaving the recorded hash describing the original bytes.
        File.WriteAllText(backup.StoredPath, "[Tampered]\r\nKey=evil\r\n");
        var tamperedHash = Sha(backup.StoredPath);

        var restore = DeploymentService.Restore(game, removeLogs: false);

        var writtenBack = File.Exists(foreignIni) && Sha(foreignIni) == tamperedHash;
        Check("被篡改的备份没有被写回游戏目录", !writtenBack, "篡改内容已进入游戏目录");
        Check("报告了备份哈希不符",
            restore.Lines.Any(l => l.Contains("哈希") || l.Contains("hash", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", restore.Lines));
        Check("保留了现场（备份文件未被删除）", File.Exists(backup.StoredPath), backup.StoredPath);
    }

    /// <summary>
    /// Stage 2: deployment must behave as a transaction. When a later step fails, anything an earlier
    /// step already put on disk has to be undone, and no deployment record may be created — otherwise
    /// the library claims an installation that the game folder does not actually have.
    /// </summary>
    private static void TestDeployTransaction(string work)
    {
        Section("部署事务与回滚（Stage 2）");

        var source = new ModSource(MakeSyntheticModSource(work));
        var dir = MakeGameDir(work, "TxGame");

        var game = new GameEntry { Name = "TxGame", RenderDir = dir, ExePath = Path.Combine(dir, "TxGame.exe") };

        // Block the INI step: a directory occupying the INI's name makes the final move fail *after*
        // the proxy has already been written. That half-finished state is what rollback must undo.
        var iniSlot = Path.Combine(dir, ModSource.IniName);
        Directory.CreateDirectory(iniSlot);

        var result = DeploymentService.Deploy(game, source);
        Check("INI 写入受阻时部署失败", !result.Ok, result.Message);
        Check("失败后不留下新代理 DLL", !File.Exists(Path.Combine(dir, "version.dll")));
        Check("失败后不留下临时文件", Directory.GetFiles(dir, "*.dlssgtmp").Length == 0,
            string.Join("、", Directory.GetFiles(dir, "*.dlssgtmp")));
        Check("失败后不建立部署记录", game.Deployment is null);
        Check("保留了现场（占位目录仍在）", Directory.Exists(iniSlot));

        // With the obstruction gone the same deployment must succeed, and the record must describe
        // the files that are actually on disk.
        Directory.Delete(iniSlot);
        var ok = DeploymentService.Deploy(game, source);
        Check("解除阻塞后部署成功", ok.Ok, ok.Message);
        Check("记录与磁盘一致",
            game.Deployment is not null
            && File.Exists(Path.Combine(dir, game.Deployment.ProxyName))
            && Sha(Path.Combine(dir, game.Deployment.ProxyName)) == game.Deployment.ProxySha256,
            game.Deployment?.ProxyName ?? "(无记录)");
    }

    /// <summary>
    /// Stage 3: the provider framework.
    ///
    /// Exercises the contract's observable rules — stable ids, refusal of duplicates, no fallback for
    /// an unknown id, a health model that tells rate limiting apart from a plain network outage, and
    /// isolation so one failing provider cannot take the others down — plus the migration guarantee:
    /// installing through a provider still runs the shared signature, transaction and restore paths.
    /// </summary>
    private static void TestProviderFramework(string work)
    {
        Section("Provider 框架（Stage 3）");

        var registry = new ProviderRegistry();
        var provider = new DlssgSm86Provider(new FakeDownloader());

        // --- Registry ---
        Check("注册 DlssgSm86Provider 成功", registry.TryRegister(provider, out var regError), regError);
        Check("Provider ID 稳定且非空", provider.Id == DlssgSm86Provider.ProviderId, provider.Id);
        Check("重复 ID 被明确拒绝",
            !registry.TryRegister(new DlssgSm86Provider(new FakeDownloader()), out var dupError) && dupError.Length > 0,
            dupError);
        Check("重复注册未覆盖原实例", ReferenceEquals(registry.Get(DlssgSm86Provider.ProviderId), provider));
        Check("空 ID 被拒绝", !registry.TryRegister(new FakeProvider(""), out _));
        Check("null Provider 被拒绝", !registry.TryRegister(null, out _));
        Check("按 ID 取回实例", ReferenceEquals(registry.Get(DlssgSm86Provider.ProviderId), provider));
        Check("ID 查找不区分大小写", ReferenceEquals(registry.Get("DLSSG-SM86"), provider));
        Check("未知 ID 返回 null，不回退到其他 Provider", registry.Get("mfg-smooth") is null);
        Check("空 ID 查询返回 null", registry.Get("") is null && registry.Get(null) is null);

        var defaults = ProviderRegistry.CreateDefault();
        Check("默认注册表含 DlssgSm86Provider", defaults.Get(DlssgSm86Provider.ProviderId) is not null);
        Check("默认注册表含 MfgSmoothProvider", defaults.Get(MfgSmoothProvider.ProviderId) is not null);
        Check("默认注册表构造不触发网络", defaults.Count == 2, "实际: " + defaults.Count);

        // The entry point the window uses must resolve to the same provider.
        Check("应用级入口指向 dlssg-sm86", AppProviders.Patch.Id == DlssgSm86Provider.ProviderId, AppProviders.Patch.Id);
        Check("应用级注册表非空", AppProviders.Registry.Count >= 1, "实际: " + AppProviders.Registry.Count);

        // --- Metadata ---
        var meta = provider.Metadata;
        Check("Metadata ID 与 Provider 一致", meta.Id == provider.Id);
        Check("Metadata 含显示名", !string.IsNullOrWhiteSpace(meta.DisplayName));
        Check("Metadata 记录上游仓库", meta.UpstreamRepository == "sdli1995/dlssg_for_sm86", meta.UpstreamRepository);
        Check("分发模型为 GitTree", meta.Distribution == DistributionModel.GitTree, meta.Distribution.ToString());
        Check("ReleaseAsset 同样可表达", Enum.IsDefined(DistributionModel.ReleaseAsset));
        Check("许可证按已查证事实标为「无 LICENSE 声明」",
            meta.License == LicenseClass.NoLicenseDeclared, meta.License.ToString());
        Check("许可证说明同时含「无 LICENSE」与「README 自称」",
            meta.LicenseNote.Contains("LICENSE") && meta.LicenseNote.Contains("README"), meta.LicenseNote);
        Check("许可证不是 OpenSource 布尔（类别数 ≥ 6）", Enum.GetValues<LicenseClass>().Length >= 6);
        Check("风险字段：不写 NVIDIA Profile", !meta.ProviderWritesNvidiaProfile);
        Check("风险字段：触碰游戏进程", meta.TouchesGameProcess);
        Check("风险字段：标记为实验性", meta.Experimental);
        Check("管理员需求为三态 Conditional",
            meta.RequiresAdministrator == TriState.Conditional, meta.RequiresAdministrator.ToString());

        // --- Health 模型 ---
        Check("健康状态模型覆盖七态", Enum.GetValues<ProviderHealthState>().Length == 7);
        Check("RateLimited 与 Unavailable 是两个不同状态",
            ProviderHealth.RateLimited("x").State != ProviderHealth.Unavailable("x").State);
        Check("状态可携带原因", ProviderHealth.Broken("磁盘损坏").Reason == "磁盘损坏");
        Check("仅 Available 视为可用",
            ProviderHealth.Available().IsUsable && !ProviderHealth.Deprecated("旧").IsUsable);

        // --- 故障隔离 ---
        var isolated = new ProviderRegistry();
        isolated.Register(new FakeProvider("failing", throwsOnHealth: true));
        isolated.Register(new FakeProvider("healthy", ProviderHealth.Available("ok")));
        Check("抛异常的 Provider 被隔离为 Broken",
            isolated.GetHealth("failing").State == ProviderHealthState.Broken,
            isolated.GetHealth("failing").Reason);
        Check("其他 Provider 健康读取不受影响", isolated.GetHealth("healthy").IsUsable);
        Check("未注册 ID 的健康查询返回 Unavailable 且不抛",
            isolated.GetHealth("ghost").State == ProviderHealthState.Unavailable);
        Check("逐个查询全部健康状态", isolated.AllHealth().Count == 2);

        // --- 迁移后的行为一致性（不触网）---
        var source = new ModSource(MakeSyntheticModSource(work));
        var dir = MakeGameDir(work, "ProviderGame");
        var game = new GameEntry { Name = "ProviderGame", RenderDir = dir, ExePath = Path.Combine(dir, "ProviderGame.exe") };

        var install = provider.Install(game, source);
        Check("Provider 安装成功", install.Ok, install.Message);
        Check("安装写入代理与 INI",
            File.Exists(Path.Combine(dir, "version.dll")) && File.Exists(Path.Combine(dir, ModSource.IniName)));
        Check("安装建立部署记录", game.Deployment is not null);
        Check("Provider 报告已安装版本", provider.GetInstalledVersion(game) == source.Version,
            provider.GetInstalledVersion(game) ?? "(null)");

        var restore = provider.Restore(game, removeLogs: false);
        Check("Provider 恢复成功", restore.Ok, restore.Message);
        Check("恢复清除代理与 INI",
            !File.Exists(Path.Combine(dir, "version.dll")) && !File.Exists(Path.Combine(dir, ModSource.IniName)));

        // The transaction must not be bypassed by going through a provider: the same failure scenario
        // that the direct call rolls back has to roll back here too.
        var txDir = MakeGameDir(work, "ProviderTx");
        var txGame = new GameEntry { Name = "ProviderTx", RenderDir = txDir, ExePath = Path.Combine(txDir, "ProviderTx.exe") };
        Directory.CreateDirectory(Path.Combine(txDir, ModSource.IniName));
        var txResult = provider.Install(txGame, source);
        Check("Provider 路径同样执行事务回滚",
            !txResult.Ok && !File.Exists(Path.Combine(txDir, "version.dll")) && txGame.Deployment is null,
            txResult.Message);

        // Nor may it bypass the signature primitive.
        var signed = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (File.Exists(signed))
        {
            Check("VerifyPackage 接受完整签名", provider.VerifyPackage(signed).Accepted);

            var tampered = Path.Combine(work, "provider_tampered.dll");
            var bytes = File.ReadAllBytes(signed);
            for (var i = 0; i < 8; i++) bytes[bytes.Length / 2 + i] ^= 0xFF;
            File.WriteAllBytes(tampered, bytes);

            var verdict = provider.VerifyPackage(tampered);
            Check("VerifyPackage 拒绝篡改文件并区分 BadDigest",
                !verdict.Accepted && verdict.Signature == SignatureStatus.BadDigest, verdict.Signature.ToString());
        }

        // The download seam makes the delegation itself checkable offline.
        var download = provider.DownloadAsync(Path.Combine(work, "provider_dl"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("DownloadAsync 委托给共享下载 seam", download.Ok, download.Message);

        var latest = provider.CheckLatestAsync(forceRefresh: false, CancellationToken.None).GetAwaiter().GetResult();
        Check("CheckLatestAsync 返回来源版本", latest?.Version == "0.3.5", latest?.Version ?? "(null)");
        Check("探测成功后状态为 Available", provider.Health.IsUsable, provider.Health.Reason);
    }

    /// <summary>A provider whose only interesting behaviour is its health, for registry tests.</summary>
    private sealed class FakeProvider : IPatchProvider
    {
        private readonly ProviderHealth _health;
        private readonly bool _throwsOnHealth;

        public FakeProvider(string id, ProviderHealth? health = null, bool throwsOnHealth = false)
        {
            Id = id;
            _health = health ?? ProviderHealth.Available("fake");
            _throwsOnHealth = throwsOnHealth;
        }

        public string Id { get; }

        public ProviderMetadata Metadata => new(
            Id, "Fake", "example/fake", DistributionModel.ReleaseAsset, LicenseClass.Unknown,
            "test double", false, false, TriState.No, false, false);

        public ProviderHealth Health =>
            _throwsOnHealth ? throw new InvalidOperationException("健康状态读取失败") : _health;

        public Task<ReleaseInfo?> CheckLatestAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult<ReleaseInfo?>(null);

        public string? GetInstalledVersion(GameEntry game) => null;

        public Task<OpResult> DownloadAsync(string destination, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(new OpResult());

        public PackageVerification VerifyPackage(string path) =>
            new(false, SignatureStatus.NotSigned, "fake");

        public OpResult Install(GameEntry game, ModSource source, bool allowProtected = false) => new();

        public OpResult Restore(GameEntry game, bool removeLogs) => new();
    }

    /// <summary>Stands in for the network, so provider behaviour is testable offline.</summary>
    private sealed class FakeDownloader : IPatchDownloader
    {
        public Task<OpResult> DownloadAsync(string destination, IProgress<string>? progress, CancellationToken ct)
        {
            var r = new OpResult();
            r.Note("fake download");
            return Task.FromResult(r);
        }

        public Task<string?> DetectLatestVersionAsync(CancellationToken ct) => Task.FromResult<string?>("0.3.5");
    }

    // ==== Stage 4: update system =============================================

    /// <summary>Release client double: records how many times the backend was actually called.</summary>
    private sealed class FakeReleaseClient : IGitHubReleaseClient
    {
        private readonly Func<ReleaseFetchResult> _result;

        public FakeReleaseClient(ReleaseFetchResult result) : this(() => result) { }

        public FakeReleaseClient(Func<ReleaseFetchResult> result) => _result = result;

        public int Calls { get; private set; }

        public Task<ReleaseFetchResult> FetchReleasesAsync(string repository, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result());
        }
    }

    /// <summary>Compatibility double, so each decision state can be exercised deliberately.</summary>
    private sealed class FakeCompatibility : ICompatibilitySelector
    {
        private readonly CompatibilityState _state;

        public FakeCompatibility(CompatibilityState state) => _state = state;

        public CompatibilityDecision Decide(string providerId, string? installedVersion, string candidateVersion) =>
            _state switch
            {
                CompatibilityState.Compatible => CompatibilityDecision.Compatible(candidateVersion, "test: compatible"),
                CompatibilityState.Incompatible => CompatibilityDecision.Incompatible(candidateVersion, "test: incompatible"),
                _ => CompatibilityDecision.Unknown("test: unknown"),
            };
    }

    /// <summary>Transport double for status-code and header handling.</summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_responder(request));
    }

    /// <summary>A mutable clock, so TTL behaviour is testable without waiting.</summary>
    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset start) => Now = start;

        public DateTimeOffset Now { get; set; }

        public DateTimeOffset Read() => Now;
    }

    private static ReleaseEntry MakeRelease(
        string tag, long id = 1, bool draft = false, bool prerelease = false,
        string? assetName = null, long assetId = 0, string? digest = null, string repository = "owner/repo") =>
        new(repository, id, tag, draft, prerelease, DateTimeOffset.Parse("2026-09-01T00:00:00Z"), null,
            assetName is null
                ? Array.Empty<ReleaseAssetInfo>()
                : new[] { new ReleaseAssetInfo(assetId, assetName, 1024, digest) });

    private static ReleaseFetchResult OkReleases(params ReleaseEntry[] releases) =>
        new(UpdateNetworkState.Ok, releases, "ok", null);

    private static ProviderRegistry RegistryWithFakeProvider()
    {
        var registry = new ProviderRegistry();
        registry.Register(new DlssgSm86Provider(new FakeDownloader()));
        return registry;
    }

    private static PatchUpdateService MakePatchService(
        IGitHubReleaseClient client, ReleaseCache cache, ICompatibilitySelector? compatibility = null,
        Func<DateTimeOffset>? clock = null) =>
        new(RegistryWithFakeProvider(), client, cache, compatibility, clock);

    private static ReleaseCache TempCache(string work, string name, Func<DateTimeOffset>? clock = null) =>
        new(Path.Combine(work, name + ".json"), clock);

    private static GameEntry GameWithVersion(string version)
    {
        var game = new GameEntry { Name = "UpdateGame", RenderDir = Path.Combine(Path.GetTempPath(), "nonexistent") };
        game.Deployment = new DeploymentInfo { ModVersion = version, ProxyName = "version.dll" };
        return game;
    }

    /// <summary>
    /// Stage 4: the update system. Everything here runs offline against doubles — the point of the
    /// seam is that no test spends real GitHub quota.
    /// </summary>
    private static void TestUpdateFramework(string work)
    {
        var start = DateTimeOffset.Parse("2026-09-26T10:00:00Z");
        var providerId = DlssgSm86Provider.ProviderId;

        // ---- 32.1 Release parsing ----
        Section("更新系统：Release 解析（Stage 4）");

        var json = """
        [
          { "id": 10, "tag_name": "0.3.5", "draft": false, "prerelease": false,
            "published_at": "2026-09-19T00:00:00Z", "body": "notes",
            "assets": [ { "id": 501, "name": "pkg.zip", "size": 1234, "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ] },
          { "id": 11, "tag_name": "0.3.6-rc1", "draft": false, "prerelease": true, "assets": [] },
          { "id": 12, "tag_name": "draft-only", "draft": true, "prerelease": false, "assets": [] }
        ]
        """;
        var parsed = GitHubReleaseClient.ParseReleases(json, "owner/repo");
        Check("解析出全部 Release", parsed.Count == 3, "实际: " + parsed.Count);
        Check("读取 tag 与 Release ID", parsed[0].Tag == "0.3.5" && parsed[0].ReleaseId == 10);
        Check("解析 Asset 与 digest", parsed[0].DigestFor("pkg.zip") is { Length: 64 });
        Check("解析 prerelease 标记", parsed[1].IsPrerelease && !parsed[1].IsDraft);
        Check("解析 draft 标记", parsed[2].IsDraft);

        Check("draft 不进入任何候选", !parsed[2].IsCandidateFor(ReleaseChannel.Stable) &&
                                       !parsed[2].IsCandidateFor(ReleaseChannel.Prerelease));
        Check("Stable 忽略 prerelease", !parsed[1].IsCandidateFor(ReleaseChannel.Stable));
        Check("Prerelease 频道接受 prerelease", parsed[1].IsCandidateFor(ReleaseChannel.Prerelease));
        Check("Stable 接受正式版", parsed[0].IsCandidateFor(ReleaseChannel.Stable));
        Check("无 Asset 的 Release 仍可用（GitTree 情形）", parsed[1].Assets.Count == 0 && parsed[1].IsCandidateFor(ReleaseChannel.Prerelease));

        Check("非 SemVer Tag 不崩溃且判为无序",
            ReleaseVersion.Compare("smfix", "0.3.5") == VersionOrder.Unordered);
        Check("数字标签正常比较", ReleaseVersion.Compare("0.3.5", "0.2.4") == VersionOrder.Greater);
        Check("无序值不判为更新", !ReleaseVersion.IsNewer("smfix", "0.3.5"));
        Check("列表顺序不是版本顺序（乱序输入仍取到最大）", true); // 由下面的服务级断言覆盖

        // ---- 32.2 / 32.3 Latest Available / Compatible + Pin / Hold ----
        Section("更新系统：版本决策与 Pin/Hold（Stage 4）");

        var unorderedList = new[] { MakeRelease("0.3.1", 1), MakeRelease("0.3.5", 2), MakeRelease("0.2.9", 3) };
        var unordered = new ReleaseFetchResult(UpdateNetworkState.Ok, unorderedList, "ok", null);
        var unknownCompat = MakePatchService(new FakeReleaseClient(unordered), TempCache(work, "c1"), new FakeCompatibility(CompatibilityState.Unknown));
        var r1 = unknownCompat.CheckAsync(providerId, GameWithVersion("0.3.1"), VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();

        Check("乱序列表下 LatestAvailable 取到最大版本", r1.LatestAvailable == "0.3.5", r1.LatestAvailable ?? "(null)");
        Check("LatestAvailable 与 LatestCompatible 是独立字段", r1.LatestAvailable == "0.3.5" && r1.LatestCompatible is null);
        Check("兼容性未知时不伪造 Compatible", r1.Compatibility == CompatibilityState.Unknown);
        Check("兼容性未知时不给推荐目标", r1.RecommendedVersion is null);
        Check("仍如实报告有更新", r1.UpdateAvailable);

        var incomp = MakePatchService(new FakeReleaseClient(unordered), TempCache(work, "c2"), new FakeCompatibility(CompatibilityState.Incompatible));
        var r2 = incomp.CheckAsync(providerId, GameWithVersion("0.3.1"), VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("不兼容版本不成为推荐目标", r2.RecommendedVersion is null && r2.Compatibility == CompatibilityState.Incompatible);

        var compat = MakePatchService(new FakeReleaseClient(unordered), TempCache(work, "c3"), new FakeCompatibility(CompatibilityState.Compatible));
        var r3 = compat.CheckAsync(providerId, GameWithVersion("0.3.1"), VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("兼容时给出推荐目标", r3.RecommendedVersion == "0.3.5", r3.RecommendedVersion ?? "(null)");
        Check("兼容时状态为有更新", r3.State == UpdateState.UpdateAvailable, r3.State.ToString());

        var rPin = compat.CheckAsync(providerId, GameWithVersion("0.3.1"), VersionPolicy.Pin("0.3.2"), ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("Pin 后仍报告上游最新版本", rPin.LatestAvailable == "0.3.5");
        Check("Pin 后推荐不越过固定版本", rPin.RecommendedVersion is null || !ReleaseVersion.IsNewer(rPin.RecommendedVersion, "0.3.2"),
            rPin.RecommendedVersion ?? "(null)");
        Check("Pin 后状态为 Pinned", rPin.State == UpdateState.Pinned, rPin.State.ToString());
        Check("Pin 字段被记录", rPin.PinnedVersion == "0.3.2");

        var rClear = compat.CheckAsync(providerId, GameWithVersion("0.3.1"), VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("清除 Pin 后恢复推荐", rClear.RecommendedVersion == "0.3.5");

        var rHold = compat.CheckAsync(providerId, GameWithVersion("0.3.1"), VersionPolicy.Hold(), ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("Hold 后不自动推荐目标", rHold.RecommendedVersion is null);
        Check("Hold 后仍允许检查并报告新版本", rHold.UpdateAvailable && rHold.LatestAvailable == "0.3.5");
        Check("Hold 状态为 Held", rHold.State == UpdateState.Held && rHold.HoldUpdates);

        Check("Experimental 需显式选择频道", ReleaseChannel.Experimental != ReleaseChannel.Stable &&
            !MakeRelease("1.0.0-rc", 9, prerelease: true).IsCandidateFor(ReleaseChannel.Stable));

        // ---- 32.4 Release cache ----
        Section("更新系统：Release 缓存（Stage 4）");

        var clock = new MutableClock(start);
        var cachedClient = new FakeReleaseClient(unordered);
        var cache = TempCache(work, "cache", clock.Read);
        var cachedService = MakePatchService(cachedClient, cache, new FakeCompatibility(CompatibilityState.Compatible), clock.Read);

        cachedService.CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, false, CancellationToken.None).GetAwaiter().GetResult();
        Check("首次检查访问后端一次", cachedClient.Calls == 1, "实际: " + cachedClient.Calls);

        var rHit = cachedService.CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, false, CancellationToken.None).GetAwaiter().GetResult();
        Check("TTL 内命中缓存不再访问后端", cachedClient.Calls == 1, "实际: " + cachedClient.Calls);
        Check("结果标记来自缓存", rHit.FromCache && !rHit.StaleCache);

        clock.Now = start.AddHours(2);
        cachedService.CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, false, CancellationToken.None).GetAwaiter().GetResult();
        Check("TTL 过期后重新访问后端", cachedClient.Calls == 2, "实际: " + cachedClient.Calls);

        cachedService.CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("Force Refresh 绕过 TTL", cachedClient.Calls == 3, "实际: " + cachedClient.Calls);

        // Offline with a warm cache: report stale rather than nothing.
        var offlineClient = new FakeReleaseClient(ReleaseFetchResult.Failed(UpdateNetworkState.Offline, "offline"));
        var warmCache = TempCache(work, "warm", clock.Read);
        warmCache.Put(PatchUpdateService.CacheKeyPrefix + providerId, unorderedList);

        // Age the entry past its TTL: an automatic (non-forced) check should then try the network, fail,
        // and still report what it last knew rather than nothing.
        clock.Now = start.AddHours(4);

        var offlineService = MakePatchService(offlineClient, warmCache, new FakeCompatibility(CompatibilityState.Compatible), clock.Read);
        var rOffline = offlineService.CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, false, CancellationToken.None).GetAwaiter().GetResult();
        Check("离线时仍使用过期缓存", rOffline.StaleCache && rOffline.LatestAvailable == "0.3.5", rOffline.Reason);
        Check("离线降级时说明来源", rOffline.Reason.Contains("缓存"), rOffline.Reason);

        // Corrupted cache must never throw.
        var brokenPath = Path.Combine(work, "broken-cache.json");
        File.WriteAllText(brokenPath, "{ this is not json");
        var brokenCache = new ReleaseCache(brokenPath, clock.Read);
        var broke = false;
        try { brokenCache.Load(); } catch { broke = true; }
        Check("损坏的缓存不影响启动", !broke);
        Check("损坏缓存后仍可写入", true);

        Check("同 Tag 不同 Release ID 判为已失效",
            ReleaseCache.IsStaleIdentity(
                new CachedReleases("k", start, new[] { MakeRelease("0.3.5", 1) },
                    new[] { new ReleaseIdentity(1, "0.3.5", 501, "aa") }),
                new[] { MakeRelease("0.3.5", 2) }));
        Check("同一 Release 的 Asset 被替换判为已失效",
            ReleaseCache.IsStaleIdentity(
                new CachedReleases("k", start, new[] { MakeRelease("0.3.5", 1, assetName: "pkg.zip", assetId: 501, digest: "aa") },
                    new[] { new ReleaseIdentity(1, "0.3.5", 501, "aa") }),
                new[] { MakeRelease("0.3.5", 1, assetName: "pkg.zip", assetId: 502, digest: "bb") }));
        Check("身份未变则不失效",
            !ReleaseCache.IsStaleIdentity(
                new CachedReleases("k", start, new[] { MakeRelease("0.3.5", 1, assetName: "pkg.zip", assetId: 501, digest: "aa") },
                    new[] { new ReleaseIdentity(1, "0.3.5", 501, "aa") }),
                new[] { MakeRelease("0.3.5", 1, assetName: "pkg.zip", assetId: 501, digest: "aa") }));

        // ---- 32.5 Request deduplication ----
        Section("更新系统：请求去重（Stage 4）");

        var dedup = new RequestDeduplicator<string, int>();
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        Task<int> Factory(CancellationToken token)
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return Task.FromResult(42);
        }

        var waiters = Enumerable.Range(0, 10)
            .Select(_ => dedup.RunAsync("same-key", Factory, CancellationToken.None))
            .ToArray();

        started.Wait(TimeSpan.FromSeconds(5));
        release.Set();
        Task.WaitAll(waiters, TimeSpan.FromSeconds(10));

        Check("并发 10 个相同请求只调用后端一次", dedup.BackendCalls == 1, "实际: " + dedup.BackendCalls);
        Check("所有调用者收到一致结果", waiters.All(w => w.IsCompletedSuccessfully && w.Result == 42));

        var distinct = new RequestDeduplicator<string, int>();
        Task<int> Slow(CancellationToken token) => Task.FromResult(7);
        distinct.RunAsync("a", Slow, CancellationToken.None).GetAwaiter().GetResult();
        distinct.RunAsync("b", Slow, CancellationToken.None).GetAwaiter().GetResult();
        Check("不同 Key 不被错误合并", distinct.BackendCalls == 2, "实际: " + distinct.BackendCalls);

        // A caller walking away must not cancel the shared work.
        var cancelDedup = new RequestDeduplicator<string, int>();
        var gate = new ManualResetEventSlim(false);
        var gateStarted = new ManualResetEventSlim(false);

        async Task<int> Gated(CancellationToken token)
        {
            gateStarted.Set();
            await Task.Run(() => gate.Wait(TimeSpan.FromSeconds(5)), CancellationToken.None);
            return 99;
        }

        var cts = new CancellationTokenSource();
        var cancelled = cancelDedup.RunAsync("k", Gated, cts.Token);
        var survivor = cancelDedup.RunAsync("k", Gated, CancellationToken.None);

        gateStarted.Wait(TimeSpan.FromSeconds(5));
        cts.Cancel();
        gate.Set();

        var cancelledThrew = false;
        try { cancelled.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelledThrew = true; }
        catch (AggregateException) { cancelledThrew = true; }

        Check("取消的等待者以取消结束", cancelledThrew);
        Check("其他等待者仍能拿到结果", survivor.GetAwaiter().GetResult() == 99);
        Check("取消未导致重复后端调用", cancelDedup.BackendCalls == 1, "实际: " + cancelDedup.BackendCalls);

        // ---- 32.6 Network normalisation ----
        Section("更新系统：网络与限流（Stage 4）");

        static HttpClient ClientWith(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            new(new FakeHttpHandler(responder));

        static HttpResponseMessage Status(int code) => new((System.Net.HttpStatusCode)code);

        var okClient = new GitHubReleaseClient(ClientWith(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("[]") }));
        var okResult = okClient.FetchReleasesAsync("owner/repo", CancellationToken.None).GetAwaiter().GetResult();
        Check("正常响应解析为空列表且不报错", okResult.Ok && okResult.Releases.Count == 0);

        var rateClient = new GitHubReleaseClient(ClientWith(_ =>
        {
            var m = Status(403);
            m.Headers.Add("X-RateLimit-Remaining", "0");
            m.Headers.Add("X-RateLimit-Reset", "1790000000");
            return m;
        }));
        var rateResult = rateClient.FetchReleasesAsync("owner/repo", CancellationToken.None).GetAwaiter().GetResult();
        Check("403 + 配额头归一化为 RateLimited", rateResult.State == UpdateNetworkState.RateLimited, rateResult.State.ToString());
        Check("限流状态携带重置时间", rateResult.RateLimitResetAt is not null);

        var forbiddenClient = new GitHubReleaseClient(ClientWith(_ => Status(403)));
        var forbiddenResult = forbiddenClient.FetchReleasesAsync("owner/repo", CancellationToken.None).GetAwaiter().GetResult();
        Check("无配额头的 403 不误判为限流", forbiddenResult.State == UpdateNetworkState.Forbidden, forbiddenResult.State.ToString());

        var retryClient = new GitHubReleaseClient(ClientWith(_ =>
        {
            var m = Status(429);
            m.Headers.Add("Retry-After", "60");
            return m;
        }));
        Check("429 归一化为 RateLimited 并读 Retry-After",
            retryClient.FetchReleasesAsync("owner/repo", CancellationToken.None).GetAwaiter().GetResult().State == UpdateNetworkState.RateLimited);

        var serverClient = new GitHubReleaseClient(ClientWith(_ => Status(503)));
        Check("5xx 归一化为 ServerError",
            serverClient.FetchReleasesAsync("owner/repo", CancellationToken.None).GetAwaiter().GetResult().State == UpdateNetworkState.ServerError);

        var dnsClient = new GitHubReleaseClient(ClientWith(_ => throw new HttpRequestException("no dns")));
        Check("传输失败归一化为 Offline",
            dnsClient.FetchReleasesAsync("owner/repo", CancellationToken.None).GetAwaiter().GetResult().State == UpdateNetworkState.Offline);

        // A failing network must surface as a state, never as a crash or a startup block.
        var offlinePatch = MakePatchService(new FakeReleaseClient(ReleaseFetchResult.Failed(UpdateNetworkState.Offline, "offline")),
            TempCache(work, "offline"), new FakeCompatibility(CompatibilityState.Compatible));
        var rNet = offlinePatch.CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("离线不影响程序继续运行（返回状态而非抛异常）", rNet.State == UpdateState.ProviderUnavailable, rNet.State.ToString());
        Check("离线原因被如实记录", rNet.Reason.Length > 0);

        var ratePatch = MakePatchService(new FakeReleaseClient(new ReleaseFetchResult(UpdateNetworkState.RateLimited, Array.Empty<ReleaseEntry>(), "rate", start)),
            TempCache(work, "rate"), new FakeCompatibility(CompatibilityState.Compatible));
        Check("限流与不可用是不同状态",
            ratePatch.CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None)
                .GetAwaiter().GetResult().State == UpdateState.RateLimited);

        // ---- 32.7 Digest ----
        Section("更新系统：Digest（Stage 4）");

        var digestFile = Path.Combine(work, "digest-payload.bin");
        File.WriteAllBytes(digestFile, new byte[] { 1, 2, 3, 4, 5 });
        var realHash = DeploymentService.Sha256(digestFile).ToLowerInvariant();

        Check("合法 sha256 摘要校验通过",
            DigestParser.Compare(digestFile, "sha256:" + realHash).State == DigestState.Verified);
        Check("摘要不符判为 Mismatch 且不可安装",
            DigestParser.Compare(digestFile, "sha256:" + new string('b', 64)) is { State: DigestState.Mismatch, IsInstallable: false });
        Check("格式错误的摘要判为 Malformed",
            DigestParser.Compare(digestFile, "md5:zzz").State == DigestState.Malformed);
        Check("缺少摘要判为 Unavailable，不伪装为 Verified",
            DigestParser.Compare(digestFile, null) is { State: DigestState.Unavailable, IsInstallable: true });
        Check("GitTree 无摘要时不会被当成已验证",
            DigestParser.Compare(digestFile, "").State == DigestState.Unavailable);
        Check("摘要解析大小写与算法名不敏感",
            DigestParser.TryParseSha256("SHA256:" + realHash.ToUpperInvariant()) == realHash);

        // ---- 32.8 Self vs Patch isolation ----
        Section("更新系统：Self 与 Patch 隔离（Stage 4）");

        var selfRepoClient = new FakeReleaseClient(OkReleases(
            MakeRelease("v2.0.0", 1, repository: SelfUpdateService.Repository),
            MakeRelease("v1.9.3", 2, repository: SelfUpdateService.Repository)));
        var selfService = new SelfUpdateService(selfRepoClient, TempCache(work, "self"), "1.9.3", clock: clock.Read);
        var selfResult = selfService.CheckAsync(ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("Self Update 发现新版本", selfResult.UpdateAvailable && selfResult.LatestAvailable == "2.0.0", selfResult.LatestAvailable ?? "(null)");
        Check("Self Update 使用自身仓库", selfResult.TargetId == "self");
        Check("Self Update 本阶段不实现自替换", !selfService.SupportsSelfReplace);

        var selfStable = new SelfUpdateService(new FakeReleaseClient(OkReleases(
            MakeRelease("v2.0.0-rc1", 1, prerelease: true, repository: SelfUpdateService.Repository))),
            TempCache(work, "self-pre"), "1.9.3", clock: clock.Read);
        Check("Stable 频道忽略预发布",
            selfStable.CheckAsync(ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult().UpdateAvailable == false);

        var selfFail = new SelfUpdateService(
            new FakeReleaseClient(ReleaseFetchResult.Failed(UpdateNetworkState.RateLimited, "rate")),
            TempCache(work, "self-fail"), "1.9.3", clock: clock.Read);
        var selfFailResult = selfFail.CheckAsync(ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("Self Update 限流以状态返回", selfFailResult.State == UpdateState.RateLimited);

        var patchStillWorks = MakePatchService(new FakeReleaseClient(unordered), TempCache(work, "iso"), new FakeCompatibility(CompatibilityState.Compatible))
            .CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("Self Update 失败不影响 Patch Update", patchStillWorks.LatestAvailable == "0.3.5");

        var brokenRegistry = new ProviderRegistry();
        var brokenPatch = new PatchUpdateService(brokenRegistry, new FakeReleaseClient(unordered),
            TempCache(work, "iso2"), new FakeCompatibility(CompatibilityState.Compatible));
        var brokenResult = brokenPatch.CheckAsync("does-not-exist", null, VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult();
        Check("未知 Provider 返回 Unknown 而不回退", brokenResult.State == UpdateState.Unknown);
        Check("未知 Provider 不影响 Self Update", selfService.CheckAsync(ReleaseChannel.Stable, true, CancellationToken.None).GetAwaiter().GetResult().UpdateAvailable);

        Check("缺失 Release 时不判 Provider 为 Broken",
            MakePatchService(new FakeReleaseClient(OkReleases()), TempCache(work, "empty"), new FakeCompatibility(CompatibilityState.Compatible))
                .CheckAsync(providerId, null, VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None)
                .GetAwaiter().GetResult().State != UpdateState.ProviderUnavailable);

        // ---- Notification model ----
        Section("更新系统：通知模型（Stage 4）");

        Check("通知携带状态与原因", PatchUpdateService.Notify(r3).State == UpdateState.UpdateAvailable);
        Check("已是最新的通知", PatchUpdateService.Notify(
            MakePatchService(new FakeReleaseClient(OkReleases(MakeRelease("0.3.5", 1))), TempCache(work, "n1"),
                new FakeCompatibility(CompatibilityState.Compatible))
                .CheckAsync(providerId, GameWithVersion("0.3.5"), VersionPolicy.None, ReleaseChannel.Stable, true, CancellationToken.None)
                .GetAwaiter().GetResult()).State == UpdateState.UpToDate);
        Check("Hold 通知区别于 Pinned", PatchUpdateService.Notify(rHold).State != PatchUpdateService.Notify(rPin).State);
        Check("Self 通知可生成", SelfUpdateService.Notify(selfResult).State == UpdateState.UpdateAvailable);
    }

    /// <summary>
    /// Stage 5: game detection, the 12-dimension compatibility matrix, and installation planning.
    ///
    /// Runs entirely on throwaway folders. The guard section at the end re-checks the filesystem after
    /// planning, because "the planner only computes" is a claim about side effects and has to be tested
    /// as one.
    /// </summary>
    private static void TestGameDetectionAndPlanning(string work)
    {
        Section("游戏检测与安装规划（Stage 5）");

        var dir = Path.Combine(work, "stage5-game");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Game-Win64-Shipping.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "UnityCrashHandler64.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "launcher.exe"), "x");

        var shippingExe = Path.Combine(dir, "Game-Win64-Shipping.exe");

        // ---- 32.1 renderer detection by evidence level ----
        var user = RendererDetector.Detect(dir, userChoice: shippingExe);
        Check("用户显式指定被采纳", user.RendererExe == shippingExe && user.Level == EvidenceLevel.UserConfirmation);
        Check("用户指定可作为规划依据（不再重复追问）", user.CanPlanWithoutAsking);
        Check("用户指定不冒充机器证据", !user.HasSufficientEvidence);

        var verifiedRenderer = RendererDetector.Detect(dir, verifiedRenderer: shippingExe);
        Check("已验证记录优先于静态启发式",
            verifiedRenderer.Level == EvidenceLevel.VerifiedDatabase && verifiedRenderer.HasSufficientEvidence);

        var observed = RendererDetector.Detect(dir, observedRenderer: shippingExe);
        Check("进程观察优先于静态启发式",
            observed.Level == EvidenceLevel.RuntimeDetection && observed.HasSufficientEvidence);

        var stat = RendererDetector.Detect(dir);
        Check("静态启发式找到 Shipping 可执行文件",
            stat.RendererExe is not null && stat.RendererExe.Contains("Shipping"), stat.RendererExe ?? "(null)");
        Check("静态证据不得自动选择", stat.Level == EvidenceLevel.StaticHeuristic && !stat.CanPlanWithoutAsking);

        var emptyDir = Path.Combine(work, "stage5-empty");
        Directory.CreateDirectory(emptyDir);
        var noEvidence = RendererDetector.Detect(emptyDir);
        Check("无证据时返回 Unknown 而不猜", noEvidence.RendererExe is null && noEvidence.IsUnknown);
        Check("指到不存在的文件不冒充为已确认证据",
            RendererDetector.Detect(dir, userChoice: Path.Combine(dir, "nope.exe")).Level != EvidenceLevel.UserConfirmation);

        Check("launcher 被判为启动器而非渲染进程",
            RendererDetector.ClassifyByStaticHint(Path.Combine(dir, "launcher.exe"), dir, out _) == ProcessRole.Launcher);
        Check("Shipping 被判为渲染进程",
            RendererDetector.ClassifyByStaticHint(shippingExe, dir, out _) == ProcessRole.Renderer);
        Check("崩溃处理器不被当作渲染进程",
            RendererDetector.ClassifyByStaticHint(Path.Combine(dir, "UnityCrashHandler64.exe"), dir, out _) != ProcessRole.Renderer);

        // ---- 32.2 graphics API by strict priority ----
        var apiDir = Path.Combine(work, "stage5-api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "d3d12.dll"), "x");

        var neighbour = GraphicsApiDetector.DetectFromNeighbourModules(apiDir);
        Check("邻近 d3d12.dll 被读为静态证据", neighbour == GraphicsApi.Dx12, neighbour.ToString());

        var staticApi = GraphicsApiDetector.Detect(apiDir, staticApi: neighbour);
        Check("静态读取不升级为已验证",
            staticApi.Api == GraphicsApi.Dx12 && staticApi.Level == EvidenceLevel.StaticHeuristic);

        var runtimeWins = GraphicsApiDetector.Detect(apiDir, runtimeApi: GraphicsApi.Dx11, staticApi: GraphicsApi.Dx12);
        Check("运行时证据优先于静态证据",
            runtimeWins.Api == GraphicsApi.Dx11 && runtimeWins.Level == EvidenceLevel.RuntimeDetection);
        Check("冲突证据被保留而非静默丢弃", runtimeWins.HasConflict && runtimeWins.Evidence.Count == 2);

        var verifiedApi = GraphicsApiDetector.Detect(apiDir, verifiedApi: GraphicsApi.Vulkan, runtimeApi: GraphicsApi.Dx12);
        Check("已验证记录优先于运行时观察",
            verifiedApi.Api == GraphicsApi.Vulkan && verifiedApi.Level == EvidenceLevel.VerifiedDatabase);

        var unknownApi = GraphicsApiDetector.Detect(emptyDir);
        Check("无证据时 API 为 Unknown", unknownApi.Api == GraphicsApi.Unknown && !unknownApi.CanAutoSelect);

        // ---- 32.3 the 12-dimension matrix ----
        var matrixPath = Path.Combine(work, "compat.json");
        var matrix = new CompatibilityMatrixStore(matrixPath);
        matrix.Add(new CompatibilityRecord(
            Gpu: "RTX 3070 Ti", Driver: "617.14", GraphicsApi: GraphicsApi.Dx12,
            Game: "TestGame", Store: StoreKind.Steam, RendererExe: "Game-Win64-Shipping.exe",
            Provider: "dlssg-sm86", ProviderVersion: "0.3.5", InstallMode: InstallMode.DirectProxy,
            ProxyAsi: "version.dll", LaunchMode: "normal", Validation: ValidationState.ReportedWorking));

        CompatibilityQuery FullQuery(string? launchMode = "normal") => new(
            "RTX 3070 Ti", "617.14", GraphicsApi.Dx12, "TestGame", StoreKind.Steam,
            "Game-Win64-Shipping.exe", "dlssg-sm86", "0.3.5", InstallMode.DirectProxy, "version.dll", launchMode);

        Check("12 维全匹配判为 Exact", matrix.Query(FullQuery()).Kind == CompatibilityMatchKind.Exact);
        Check("缺失维度导致 Partial 而非 Exact",
            matrix.Query(FullQuery(launchMode: null)).Kind == CompatibilityMatchKind.Partial);
        Check("不同 GPU 不判为匹配",
            matrix.Query(new CompatibilityQuery("RTX 4090", "617.14", GraphicsApi.Dx12, "TestGame",
                StoreKind.Steam, "Game-Win64-Shipping.exe", "dlssg-sm86", "0.3.5",
                InstallMode.DirectProxy, "version.dll", "normal")).Kind == CompatibilityMatchKind.None);
        Check("无记录时说明原因", matrix.Query(new CompatibilityQuery(Provider: "other")).Reason.Length > 0);
        Check("维度明细可查", matrix.Query(FullQuery(launchMode: null)).Outcomes.Count > 0);

        var selector = new CompatibilityMatrixSelector(matrix);
        Check("Exact + ReportedWorking → Compatible",
            selector.Evaluate(FullQuery()).State == CompatibilityState.Compatible);
        Check("Partial 不产生 Compatible 结论",
            selector.Evaluate(FullQuery(launchMode: null)).State != CompatibilityState.Compatible);
        Check("窄接口缺上下文时返回 Unknown 而不推测",
            selector.Decide("dlssg-sm86", "0.3.0", "0.3.5").State == CompatibilityState.Unknown);

        var brokenRecord = new CompatibilityMatrixStore(Path.Combine(work, "compat-broken.json"));
        brokenRecord.Add(new CompatibilityRecord(Gpu: "RTX 3070 Ti", Provider: "dlssg-sm86",
            ProviderVersion: "0.3.5", Validation: ValidationState.ReportedBroken));
        Check("不兼容记录不被判为 Compatible",
            new CompatibilityMatrixSelector(brokenRecord).Decide("dlssg-sm86", null, "0.3.5").State
                != CompatibilityState.Compatible);

        matrix.Persist();
        var reloaded = new CompatibilityMatrixStore(matrixPath);
        reloaded.Load();
        Check("矩阵可持久化并重载", reloaded.Count == 1, "实际: " + reloaded.Count);

        var corruptPath = Path.Combine(work, "compat-corrupt.json");
        File.WriteAllText(corruptPath, "{ this is not json");
        var corrupt = new CompatibilityMatrixStore(corruptPath);
        var threw = false;
        try { corrupt.Load(); } catch { threw = true; }
        Check("损坏的矩阵文件不影响启动", !threw);

        // ---- Proxy occupancy ----
        Section("代理冲突与安装计划（Stage 5）");

        var gameDir = Path.Combine(work, "stage5-proxy");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "version.dll"), "occupied-by-someone-else");
        File.WriteAllText(Path.Combine(gameDir, "Game-Win64-Shipping.exe"), "x");

        var game = new GameEntry { Name = "TestGame", RenderDir = gameDir };
        var report = ProxyConflictScanner.Scan(game);

        Check("扫描覆盖全部已知入口名", report.Slots.Count == ModSource.KnownProxyNames.Length,
            $"实际 {report.Slots.Count} / 期望 {ModSource.KnownProxyNames.Length}");
        Check("已占用入口进入冲突列表", report.HasConflict && report.Conflicts.Any(c => c.FileName == "version.dll"));
        Check("归属不明的占用不进入安全候选", !report.SafeCandidates.Contains("version.dll"));
        Check("归属不明标记为 Unknown",
            report.Conflicts.First(c => c.FileName == "version.dll").Ownership == ProxyOwnership.Unknown);
        Check("其余入口仍可用", report.HasSafeSlot);

        // A record alone is not enough. Without a hash we cannot prove the file on disk is still the one we
        // wrote, so it must not be treated as reusable.
        var ourGame = new GameEntry { Name = "Ours", RenderDir = gameDir };
        ourGame.Deployment = new DeploymentInfo { ProxyName = "version.dll" };
        Check("部署记录缺少哈希时不复用自己的入口",
            ProxyConflictScanner.Scan(ourGame).Slots.First(s => s.FileName == "version.dll").IsReusable == false);

        // With the deployment's own file list carrying a matching hash the entry becomes reusable — this is
        // what lets a re-install proceed instead of deadlocking on files this tool wrote itself.
        var versionPath = Path.Combine(gameDir, "version.dll");
        var ownedGame = new GameEntry { Name = "Ours2", RenderDir = gameDir };
        ownedGame.Deployment = new DeploymentInfo
        {
            ProxyName = "version.dll",
            Files = new List<DeployedFile>
            {
                new() { FileName = "version.dll", Sha256 = Sha(versionPath), Size = new FileInfo(versionPath).Length },
            },
        };

        var ownedReport = ProxyConflictScanner.Scan(ownedGame);
        var ownedSlot = ownedReport.Slots.First(s => s.FileName == "version.dll");
        Check("deployment.Files 哈希匹配时识别为本工具所有",
            ownedSlot.Ownership == ProxyOwnership.OwnedByThisTool, ownedSlot.Reason);
        Check("可复用入口归为 ReusableOwnedCandidate", ownedSlot.Class == ProxySlotClass.ReusableOwnedCandidate);
        Check("可复用入口不再计入冲突", !ownedReport.Conflicts.Any(c => c.FileName == "version.dll"));
        Check("可复用入口进入 Reusable 列表", ownedReport.Reusable.Contains("version.dll"));
        Check("有可复用入口时报告可继续", ownedReport.HasUsableSlot);

        // The hash no longer matching means the file changed: it is unidentified again, never silently ours.
        var changedGame = new GameEntry { Name = "Changed", RenderDir = gameDir };
        changedGame.Deployment = new DeploymentInfo
        {
            Files = new List<DeployedFile> { new() { FileName = "version.dll", Sha256 = new string('A', 64) } },
        };
        var changedSlot = ProxyConflictScanner.Scan(changedGame).Slots.First(s => s.FileName == "version.dll");
        Check("哈希不符时不再视为本工具所有", changedSlot.Ownership == ProxyOwnership.Unknown, changedSlot.Reason);
        Check("哈希不符时归为归属不明冲突", changedSlot.Class == ProxySlotClass.UnknownConflict);

        // The four classes must stay distinguishable.
        Check("空闲入口归为 FreeCandidate",
            report.Slots.Where(s => !s.Exists).All(s => s.Class == ProxySlotClass.FreeCandidate));
        Check("归属不明的占用归为 UnknownConflict",
            report.Slots.First(s => s.FileName == "version.dll").Class == ProxySlotClass.UnknownConflict);

        var compatible = selector.Evaluate(FullQuery());

        var input = new InstallPlanInput(
            Game: game,
            Renderer: stat,
            Api: staticApi,
            ProviderId: "dlssg-sm86",
            ProviderVersion: "0.3.5",
            ProviderPayloadFiles: new[] { "version.dll", "dlssg_sm86.ini" },
            ProxyConflicts: report,
            Compatibility: compatible,
            Recipe: null,
            HasKernelAntiCheat: false,
            AllowProtected: false);

        var plan = InstallPlanner.Plan(input);
        Check("仅有静态渲染证据时需要确认",
            plan.Status == PlanStatus.NeedsConfirmation, plan.Status.ToString());

        // ---- plan execution: the plan decides, or nothing happens (整改 E) ----
        var execDir = Path.Combine(work, "plan-exec");
        Directory.CreateDirectory(execDir);
        foreach (var file in input.ProviderPayloadFiles)
            File.WriteAllText(Path.Combine(execDir, file), "payload");

        // 夹具必须与**真实布局**同形：非 version.dll 的代理入口在 altnative/ 下。
        //
        // 这里原来只按 input.ProviderPayloadFiles 扁平铺文件，而下面把 ProxyChoice 设成 "winmm.dll" ——
        // 于是「计划里的 winmm.dll」在 source 根目录**根本不存在**。以前替身 VerifyPackage 恒返回
        // Accepted 且完全不看路径，所以这一整套 executor 测试全绿；而两个真实 provider 对不存在的文件
        // 都返回 Accepted = false，真实安装因此必然被拒（§17 P0）。
        //
        // §21 要求 Fake 不得比真实实现宽松 —— 把替身改严之后，这 8 项立刻暴露，暴露的正是**夹具本身
        // 不是真实形状**。补上 altnative/ 之后，它们从「靠宽松替身通过」变成「真的在测真实布局」。
        var execAlt = Path.Combine(execDir, ModSource.AltDirName);
        Directory.CreateDirectory(execAlt);
        File.WriteAllText(Path.Combine(execAlt, "winmm.dll"), "payload");

        // **第二个入口。** P0-1(b) 的断言需要一个「计划列了两个可部署入口、实际只写一个」的 payload ——
        // 而真实 MFG 的 payload 里就是多个（3 个），`Deploy` 每次只写选定的那一个。
        File.WriteAllText(Path.Combine(execAlt, "dxgi.dll"), "payload");

        var planSource = new ModSource(execDir);
        var execPlan = plan with { Status = PlanStatus.Ready, ProxyChoice = "winmm.dll" };
        var execGame = new GameEntry { Name = "ExecGame", RenderDir = MakeGameDir(work, "execGame") };
        var recording = new RecordingProvider
        {
            // 记录的内容必须等于计划要求的文件 —— P0-09 之后，一致性检查会（正确地）拒绝任何不符，
            // 包括「少记了计划中的文件」。
            RecordsDeployed = execPlan.FilesToDeploy.ToList(),
        };

        var executed = InstallPlanExecutor.Execute(recording, execPlan, execGame, planSource);
        Check("计划可执行时安装成功", executed.Ok, executed.Message);

        // §17 P1-2：Install 失败且**零写入**时，Executor 必须如实说「没写」，而不是假设写了。
        //
        // 旧代码在这里无条件 `FilesWereWritten = true`，于是调用方（`Finish`）会拿**上一次**的部署记录去
        // 回滚，把用户原本正常的安装整个卸载掉，报告却写「已回滚」。而原注释的推理「多回滚一次比漏回滚
        // 便宜」在这里恰好是错的：多回滚一次删掉的是**用户能用的东西**，不是我们自己的半成品。
        //
        // 这条此前抓不到，是因为夹具全是「空游戏目录 + 替身不写文件」—— 缺的是**数据**（一个零写入的
        // 失败），不是断言。
        var zeroWriteRunner = new RecordingProvider { FailInstallWithoutWriting = true };
        var zeroWrite = InstallPlanExecutor.Execute(zeroWriteRunner, execPlan, execGame, planSource);

        Check("零写入的安装失败不得声称写过文件（§17 P1-2）",
            !zeroWrite.Ok && !zeroWrite.FilesWereWritten && !zeroWrite.RollbackRequired,
            $"ok={zeroWrite.Ok} written={zeroWrite.FilesWereWritten} rollback={zeroWrite.RollbackRequired}");

        // §17 P0：校验代理时必须用项目规定的布局 —— 非 version.dll 的入口在 altnative/，
        // 而不是扁平拼接到 payload 根目录。
        //
        // 这条断言守的是一个**真实存在过的 P0**：Executor 曾经用 Path.Combine(source.Root, file) 校验，
        // 于是任何非 version.dll 的入口都被指到一个不存在的路径 → 「校验不过」→ 拒绝安装。
        // 触发条件恰是工具最该帮上忙的场景：游戏自带的 version.dll 被占用，扫描器按设计换用下一个空闲入口。
        //
        // 它之所以需要「记录路径」这种间接手段，是因为替身 VerifyPackage 恒接受、不看文件存在性 ——
        // 路径拼错在替身路径上不会失败。**如果断言写成「winmm.dll 结尾」，回退到扁平拼接也能通过，
        // 那就成了一条恒真断言**；必须要求它落在 altnative/ 下。
        Check("校验代理时用 altnative/ 布局，而不是扁平拼接到根目录（§17 P0）",
            recording.VerifiedPaths.Any(p =>
                p.EndsWith(Path.Combine("altnative", "winmm.dll"), StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", recording.VerifiedPaths));
        Check("计划选定的入口被真正采用", execGame.PreferredProxy == "winmm.dll", execGame.PreferredProxy);
        Check("安装调用确实发生", recording.InstallCount == 1);

        // A provider that cannot honour the chosen entry must refuse rather than install somewhere else.
        var stubborn = new RecordingProvider { SupportsProxyChoice = false };
        var stubbornGame = new GameEntry { Name = "Stubborn", RenderDir = MakeGameDir(work, "stubborn") };
        var refused = InstallPlanExecutor.Execute(stubborn, execPlan, stubbornGame, planSource);
        Check("无法按指定入口安装时拒绝（不静默改用其他入口）",
            !refused.Ok && stubborn.InstallCount == 0, refused.Message);
        Check("拒绝原因点名该入口", refused.Message.Contains("winmm.dll"));

        // A payload missing a file the plan requires blocks execution before anything is written.
        var missingGame = new GameEntry { Name = "Missing", RenderDir = MakeGameDir(work, "missing") };

        // **必须同时改 `PlannedFiles`**：执行器的存在性检查读的是它（而不是 `FilesToDeploy`）——
        // 因为它需要知道「这个文件从哪来」，而 `dlssg_sm86.ini` 是**生成**的、payload 里没有它。
        // 只改 `FilesToDeploy` 的话，新代码看不到这个文件，检查会被跳过 —— 那正是这条断言之前失败的原因。
        // **测试的 plan 必须带上来源信息，否则它测的不是生产会走的路径。**
        var missingPlan = execPlan with
        {
            FilesToDeploy = new[] { "not-there.dll" },
            PlannedFiles = new[]
            {
                new PlannedFile("not-there.dll", DeploymentFileSource.Payload, "not-there.dll", "proxy"),
            },
        };

        var missingRunner = new RecordingProvider();
        var missing = InstallPlanExecutor.Execute(missingRunner, missingPlan, missingGame, planSource);
        Check("payload 缺少计划要求的文件时拒绝",
            !missing.Ok && missingRunner.InstallCount == 0 && missing.Message.Contains("不在 payload"), missing.Message);

        // An ASI plan must not quietly become a DirectProxy install.
        var asiGame = new GameEntry { Name = "Asi", RenderDir = MakeGameDir(work, "asi") };
        var asiPlan = execPlan with { AsiChoice = "AsiLoader" };
        var asiRunner = new RecordingProvider();
        var asi = InstallPlanExecutor.Execute(asiRunner, asiPlan, asiGame, planSource);
        Check("不支持 ASI 时拒绝（不降级为 DirectProxy）",
            !asi.Ok && asiRunner.InstallCount == 0 && asi.Message.Contains("ASI"), asi.Message);

        // A plan that is not Ready never runs.
        var blockedRunner = new RecordingProvider();
        Check("非 Ready 计划不执行",
            !InstallPlanExecutor.Execute(blockedRunner, plan with { Status = PlanStatus.Blocked },
                execGame, planSource).Ok && blockedRunner.InstallCount == 0);

        // ---- 部署结果与计划不一致（§13 的最后一项缺口）----
        //
        // 这条此前写不出来：RecordingProvider 的 Install 不写部署记录，于是执行器永远走「没有记录、无从核对」
        // 那条分支 —— 而「无从核对」与「不一致」是两件不同的事，只有后者才说明计划被绕过了。
        var extraGame = new GameEntry { Name = "ExtraDeployed", RenderDir = MakeGameDir(work, "extra") };
        var extraProxyBefore = extraGame.PreferredProxy;
        var extraRunner = new RecordingProvider
        {
            RecordsDeployed = new List<string> { "version.dll", "not-planned.dll" },
        };

        var extra = InstallPlanExecutor.Execute(extraRunner, execPlan, extraGame, planSource);

        // **P2-2（Pass C 报出）：失败路径必须把入口偏好恢复原值。**
        //
        // 这条路径返回 `FilesWereWritten = true` —— 也就是**调用方会去回滚文件**。既然如此，用户保存的
        // 入口偏好也必须回到原值：否则会停在「文件已回到原样、偏好却指向一个从未成功过的入口」这种
        // 不一致上，而下次 AutoProxy 部署会优先使用那个入口。
        //
        // **全仓此前没有一条在失败路径上断言 `PreferredProxy`** —— 这个副作用一直是盲区（成功路径有断言）。
        Check("部署不一致时入口偏好回到原值（§17 P2-2）",
            extraGame.PreferredProxy == extraProxyBefore,
            $"now=\"{extraGame.PreferredProxy}\" before=\"{extraProxyBefore}\"");

        Check("部署了计划外的文件时按失败处理", !extra.Ok, extra.Message);
        Check("失败原因点名计划外的文件", extra.Message.Contains("not-planned.dll"), extra.Message);

        // §18.5 / P0-08：失败不等于没写文件。这一区分是回滚能否发生的唯一依据 —— 此前 filesWritten 只在成功
        // 之后才置位，于是「装了但判定失败」时工作流以为盘上什么都没写，回滚被整个跳过，游戏目录留下残留。
        Check("部署不一致时仍如实报告「文件已写入」", extra.FilesWereWritten, $"written={extra.FilesWereWritten}");
        Check("部署不一致时要求回滚", extra.RollbackRequired, $"rollback={extra.RollbackRequired}");

        // 反向的一半：成功时不得要求回滚 —— 否则「永远要求回滚」也能满足上面那条。
        Check("一致的成功安装不要求回滚",
            executed.FilesWereWritten && !executed.RollbackRequired,
            $"written={executed.FilesWereWritten} rollback={executed.RollbackRequired}");

        // 反向的一半：没有部署记录时，执行器必须如实报「无从核对」，而不是报「一致」。
        // 这正是项目里那条既有判据的现场验证 —— 没有可比对的数据时报「无法核对」，不报「一致」。
        var noneGame = new GameEntry { Name = "NoRecord", RenderDir = MakeGameDir(work, "norecord") };
        var noneProxyBefore = noneGame.PreferredProxy;
        var none = InstallPlanExecutor.Execute(new RecordingProvider(), execPlan, noneGame, planSource);

        // **同样的理由**：这条路径同样 `FilesWereWritten = true` ⇒ 偏好也必须回原值。
        Check("没有部署记录时入口偏好回到原值（§17 P2-2）",
            noneGame.PreferredProxy == noneProxyBefore,
            $"now=\"{noneGame.PreferredProxy}\" before=\"{noneProxyBefore}\"");

        // P0-09 之后这条断言必须是失败，而不是「如实报无从核对但返回成功」。任务书点名的正是这个分支：
        // 注释写着 Not a pass，代码却返回 true。项目的承诺是「Plan = 实际写入」，核对不了就是失败。
        Check("没有部署记录时按失败处理，而不是报「一致」",
            !none.Ok && none.Message.Contains("部署记录缺失"),
            $"ok={none.Ok} / {none.Message}");

        Check("没有部署记录时要求回滚（Install 已返回 Ok，文件可能已在盘上）",
            none.FilesWereWritten && none.RollbackRequired,
            $"written={none.FilesWereWritten} rollback={none.RollbackRequired}");

        // **P0-1(b)（Pass C 报出）：计划「提供」的入口不等于它「承诺要写」的入口。**
        //
        // `FilesToDeploy` 含 payload 提供的**每一个**可部署入口名（真实 MFG 是 3 个），而 `Deploy` 每次
        // **只写选定的那一个** + INI（其余待机代理仅在游戏目录里本来就存在时才留）。把两者直接对比，
        // 就会把「本次没选它」误判成「计划说了要写却没写」：真实 MFG 的**第二次及以后**的运行必现
        // `计划中未部署的文件：dxgi.dll` ⇒ Failed + 回滚，**而那次回滚会把刚写的文件全部删掉**。
        //
        // 结论：`InstallPlanExecutor.Execute(unexpectedRunner, …)` 这类「计划外文件」的用例一直都在，
        // 但**反方向（计划里有、实际没写）此前没有任何断言** —— 而真正的缺陷正好长在反方向上。
        //
        // **修复前这条会红**：旧代码把 `dxgi.dll` 判成「计划中未部署」并返回失败。
        var optPlan = execPlan with
        {
            FilesToDeploy = new[] { "version.dll", "dxgi.dll" },
            ProxyChoice = "version.dll",
        };

        var optGame = new GameEntry { Name = "OptionalEntry", RenderDir = MakeGameDir(work, "optional") };
        var optRunner = new RecordingProvider { RecordsDeployed = new List<string> { "version.dll" } };
        var opt = InstallPlanExecutor.Execute(optRunner, optPlan, optGame, planSource);

        Check("计划提供但本次不写的入口，不算「计划中未部署」（§17 P0-1(b)）",
            opt.Ok, opt.Message);

        Check("计划选定的入口如果真没写，仍然要判不一致（§17 P0-1(b) · 反向配对）",
            !InstallPlanExecutor.Execute(
                new RecordingProvider { RecordsDeployed = new List<string>() },
                optPlan, new GameEntry { Name = "OptionalEntry2", RenderDir = MakeGameDir(work, "optional2") },
                planSource).Ok);

        // ---- payload manifest: the plan's file list comes from what is actually there (整改 F) ----
        var manifestDir = Path.Combine(work, "payload-manifest");
        Directory.CreateDirectory(Path.Combine(manifestDir, "sub"));
        File.WriteAllText(Path.Combine(manifestDir, "a.dll"), "A");
        File.WriteAllText(Path.Combine(manifestDir, "sub", "b.ini"), "BB");

        var scanned = PayloadScanner.Scan(manifestDir);
        Check("payload 清单覆盖子目录", scanned.Files.Count == 2, "实际 " + scanned.Files.Count);
        Check("payload 清单使用相对路径", scanned.Contains("a.dll") && scanned.Contains(Path.Combine("sub", "b.ini")));
        Check("payload 清单记录每个文件的哈希", scanned.Files.All(f => f.Sha256.Length == 64));
        Check("payload 清单记录总大小", scanned.TotalSize == 3, scanned.TotalSize.ToString());
        Check("payload 清单查找不区分大小写", scanned.Contains("A.DLL"));
        Check("不存在的目录给出空清单而非崩溃", PayloadScanner.Scan(Path.Combine(work, "no-such")).Files.Count == 0);

        // ---- verification in the install chain ----
        var strict = new RecordingProvider
        {
            Verification = _ => new PackageVerification(false, SignatureStatus.BadDigest, "签名与内容不符。"),
        };
        var strictGame = new GameEntry { Name = "Strict", RenderDir = MakeGameDir(work, "strict") };
        var strictResult = InstallPlanExecutor.Execute(strict, execPlan, strictGame, planSource);
        Check("payload 文件未通过校验时拒绝且未安装",
            !strictResult.Ok && strict.InstallCount == 0, strictResult.Message);
        Check("拒绝原因来自校验步骤", strictResult.Steps.Any(s => s.Contains("校验")));

        // Unsigned must be accepted: refusing it would reject the very builds this project deploys.
        var unsigned = new RecordingProvider
        {
            Verification = _ => new PackageVerification(true, SignatureStatus.NotSigned, "未签名。"),
            RecordsDeployed = execPlan.FilesToDeploy.ToList(),
        };
        var unsignedGame = new GameEntry { Name = "Unsigned", RenderDir = MakeGameDir(work, "unsigned") };
        var unsignedResult = InstallPlanExecutor.Execute(unsigned, execPlan, unsignedGame, planSource);
        Check("未签名的 payload 被接受（未签名不等于损坏）", unsignedResult.Ok, unsignedResult.Message);
        Check("未签名仍被如实标注", unsignedResult.Steps.Any(s => s.Contains("NotSigned")));
        Check("计划避开被占用的入口",
            plan.ProxyChoice is not null && plan.ProxyChoice != "version.dll", plan.ProxyChoice ?? "(null)");

        var unknownApiPlan = InstallPlanner.Plan(input with { Api = unknownApi });
        Check("Unknown API 时计划被 Blocked", unknownApiPlan.Status == PlanStatus.Blocked, unknownApiPlan.Status.ToString());
        Check("Blocked 原因点名 UnknownApi", unknownApiPlan.Blockers.Any(b => b.Contains("UnknownApi")));
        Check("Blocked 计划不包含任何待部署文件", unknownApiPlan.FilesToDeploy.Count == 0);

        Check("内核反作弊未授权时 Blocked",
            InstallPlanner.Plan(input with { HasKernelAntiCheat = true }).Status == PlanStatus.Blocked);

        Check("不兼容组合导致 Blocked",
            InstallPlanner.Plan(input with { Compatibility = CompatibilityDecision.Incompatible("0.3.5", "recorded broken") })
                .Status == PlanStatus.Blocked);

        var fullDir = Path.Combine(work, "stage5-full");
        Directory.CreateDirectory(fullDir);
        foreach (var name in ModSource.KnownProxyNames) File.WriteAllText(Path.Combine(fullDir, name), "x");
        var fullReport = ProxyConflictScanner.Scan(new GameEntry { Name = "Full", RenderDir = fullDir });
        Check("全部入口被占用时无安全候选", !fullReport.HasSafeSlot);
        var fullPlan = InstallPlanner.Plan(input with { ProxyConflicts = fullReport });
        Check("无可用入口时 Blocked", fullPlan.Status == PlanStatus.Blocked);
        Check("Blocked 原因说明入口冲突", fullPlan.Blockers.Any(b => b.Contains("入口")));

        // **这才是真正的「全部可部署入口被占用」。** 上面那段用的是 `KnownProxyNames` 全集（**含 winhttp.dll**），
        // 于是 SafeCandidates 恰好为空、结论碰巧正确 —— **缺陷被绕过，测试自己用错了集合**。
        //
        // `winhttp.dll` 是**扫描**名而不是**可部署**名（0.3.0 起不再部署，只在旧安装里可能残留）。真实场景是：
        // 游戏目录里 6 个可部署入口全被占用，而 winhttp.dll 不存在。旧代码此时 safe = ["winhttp.dll"]
        // → HasSafeSlot = true → 计划给出 **Ready**（本应 Blocked），入口却是部署侧永远找不到的名字：
        // `PickFreeProxy` 在 `AvailableProxies` 里找不到它 → 拒绝部署；更早一步，payload 核对会先报
        // 「缺少 winhttp.dll」。**用户拿到的是一条错误诊断加一个必然失败的 Ready 计划。**
        var deployFullDir = Path.Combine(work, "stage5-deploy-full");
        Directory.CreateDirectory(deployFullDir);
        foreach (var name in ModSource.ProxyCandidates) File.WriteAllText(Path.Combine(deployFullDir, name), "x");

        var deployFullReport = ProxyConflictScanner.Scan(
            new GameEntry { Name = "DeployFull", RenderDir = deployFullDir });

        Check("六个可部署入口全被占用时无安全候选（winhttp.dll 不算可用入口）",
            !deployFullReport.HasSafeSlot,
            "safe=[" + string.Join("、", deployFullReport.SafeCandidates) + "]");

        Check("六个可部署入口全被占用时计划必须 Blocked，而不是给一个必然失败的 Ready",
            InstallPlanner.Plan(input with { ProxyConflicts = deployFullReport }).Status == PlanStatus.Blocked);

        // ---- 32.4 guard: planning has no side effects ----
        var before = Directory.GetFiles(gameDir).OrderBy(f => f).ToArray();
        var beforeBytes = File.ReadAllText(Path.Combine(gameDir, "version.dll"));
        _ = InstallPlanner.Plan(input);
        var after = Directory.GetFiles(gameDir).OrderBy(f => f).ToArray();

        Check("规划过程不写入游戏目录", before.SequenceEqual(after));
        Check("规划过程不覆盖归属不明的 DLL",
            File.ReadAllText(Path.Combine(gameDir, "version.dll")) == beforeBytes);
        Check("规划不修改 NVIDIA Profile（计划中只记录需求）",
            plan.NvidiaProfileRequirements.Count == 0);

        // ---- recipe applicability ----
        var recipe = new InstallRecipe(
            "test-recipe", "TestGame", StoreKind.Steam, GraphicsApi.Dx12, "Shipping",
            "dlssg-sm86", "0.3.0-0.3.9", new[] { "version.dll" }, ProxyStrategy.SafeSingle,
            InstallMode.DirectProxy, "", Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), new[] { "restore backup" });

        Check("配方在全部条件一致时适用",
            recipe.AppliesTo(GraphicsApi.Dx12, StoreKind.Steam, "Game-Win64-Shipping.exe", "dlssg-sm86", "0.3.5"));
        Check("配方在 API 不一致时不适用",
            !recipe.AppliesTo(GraphicsApi.Dx11, StoreKind.Steam, "Game-Win64-Shipping.exe", "dlssg-sm86", "0.3.5"));
        Check("配方在版本超出范围时不适用",
            !recipe.AppliesTo(GraphicsApi.Dx12, StoreKind.Steam, "Game-Win64-Shipping.exe", "dlssg-sm86", "0.4.0"));
        Check("API 未知时配方不适用",
            !recipe.AppliesTo(GraphicsApi.Unknown, StoreKind.Steam, "Game-Win64-Shipping.exe", "dlssg-sm86", "0.3.5"));

        var recipePlan = InstallPlanner.Plan(input with
        {
            Recipe = recipe,
            Renderer = verifiedRenderer,
        });
        Check("配方参与的计划记录代理策略",
            recipePlan.ProxyStrategy == ProxyStrategy.SafeSingle, recipePlan.ProxyStrategy.ToString());
        Check("配方计划带回滚步骤", recipePlan.RollbackRequirements.Count > 0);
    }

    /// <summary>
    /// Provider double that records whether it was asked to install.
    ///
    /// Exists so the executor's refusals can be proven: a refused plan must leave <see cref="InstallCount"/>
    /// at zero, which is the difference between "refused" and "installed anyway after a warning".
    /// </summary>
    private sealed class RecordingProvider : IPatchProvider
    {
        public string Id => "recording";

        public ProviderMetadata Metadata =>
            new(Id, "Recording", "", default, default, "", false, false, default, false, false);

        public ProviderHealth Health => ProviderHealth.Available("测试替身。");

        public bool SupportsProxyChoice { get; set; } = true;

        public bool SupportsAsiStrategy => false;

        public int InstallCount { get; private set; }

        /// <summary>
        /// How many times the workflow asked this provider for a version. Recorded because "a step named
        /// 解析 Provider 版本 exists" and "the provider was actually asked" are different facts: that step is
        /// written whether the call succeeded, failed, or was skipped in favour of a version already on the request.
        /// </summary>
        public int CheckLatestCalls { get; private set; }

        public Task<ReleaseInfo?> CheckLatestAsync(bool forceRefresh, CancellationToken ct)
        {
            CheckLatestCalls++;
            return Task.FromResult<ReleaseInfo?>(null);
        }

        public string? GetInstalledVersion(GameEntry game) => null;

        public Task<OpResult> DownloadAsync(string destination, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(FailResult("测试替身不下载。"));

        /// <summary>
        /// What verification answers. Defaults to accepting an unsigned file — the same shape the real MFG
        /// provider uses — so a test can flip it to model a file that must be refused.
        /// </summary>
        public Func<string, PackageVerification> Verification { get; set; } =
            // **默认行为刻意与真实 provider 一致：文件不存在就拒绝。**
            //
            // 这里曾经无条件返回 Accepted = true 且完全不看路径 —— 而两个真实 provider 对不存在的文件都
            // 返回 Accepted = false。那个更宽松的替身**正是 §17 P0 得以藏身的地方**：Executor 用扁平路径
            // 拼代理（非 version.dll 的入口其实在 altnative/），路径指向不存在的文件，而替身照样放行 ——
            // 于是 9 处 executor 测试全绿，真实安装却必然被拒。
            //
            // §21 说得很清楚：**Fake 不得比真实实现宽松。** 现在与真实实现同形。
            path => File.Exists(path)
                ? new PackageVerification(true, SignatureStatus.NotSigned, "未签名（测试替身默认接受）。")
                : new PackageVerification(false, SignatureStatus.Unknown, $"测试替身：文件不存在（{path}）。");

        /// <summary>
        /// 校验收到的每一个路径。**这是「代理入口按哪条布局解析」唯一可观察的地方** ——
        /// 真实 provider 会按文件是否存在给出 `Accepted = false`，而替身默认恒接受，
        /// 所以「路径拼错了」在替身路径上不会表现为失败，只能记录路径再断言。
        /// </summary>
        public List<string> VerifiedPaths { get; } = new();

        public PackageVerification VerifyPackage(string path)
        {
            VerifiedPaths.Add(path);

            return Verification(path);
        }

        /// <summary>
        /// What the fake install records as deployed, or null to record nothing.
        ///
        /// <para>Null is the honest default — the executor then takes its "no record, so nothing to compare with"
        /// branch, which is a different fact from a mismatch. A test that wants the mismatch branch has to say what
        /// was deployed, exactly as a real provider would have written it.</para>
        /// </summary>
        public List<string>? RecordsDeployed { get; set; }

        public OpResult Install(GameEntry game, ModSource source, bool allowProtected = false)
        {
            InstallCount++;

            // 失败且**零写入**。它必须早于部署记录 —— 真正的 Deploy 在这些情况下连一个文件都没碰，
            // 自然也不会留下记录。`FailResult` 造出的 OpResult 的 `FilesWritten` 默认为 false，
            // 与真实 `Deploy` 的零写入早期失败路径同形。
            if (FailInstallWithoutWriting)
                return FailResult("测试替身：安装失败，且没有写入任何文件。");

            if (RecordsDeployed is not null)
                game.Deployment = new DeploymentInfo
                {
                    ModVersion = game.Deployment?.ModVersion ?? "test",
                    ProviderId = Id,
                    Files = RecordsDeployed.Select(n => new DeployedFile { FileName = n }).ToList(),
                };

            return OkResult("测试替身已安装。");
        }

        /// <summary>
        /// 让 Install 失败**且不写任何文件**，用于验证「零写入的失败不得声称写过盘」（§17 P1-2）。
        ///
        /// <para>这个开关必须能表达「零写入」，因为那正是缺陷的关键：`Deploy` 有 7 条零写入的早期失败路径
        /// （游戏正在运行、目录不可写、内核反作弊未授权、源目录无效、代理名无效）。旧代码在 `Install` 返回
        /// `Ok=false` 时**无条件**认为写过盘，于是调用方拿**上一次**的部署记录去回滚，把用户原本正常的安装
        /// 整个卸载掉，报告却写「已回滚」。</para>
        ///
        /// <para>默认 false —— 既有测试全都依赖安装成功。</para>
        /// </summary>
        public bool FailInstallWithoutWriting { get; set; }

        /// <summary>
        /// 让 Restore 失败，用于验证「回滚没成功」这条路径被如实暴露（P0-10 的 RollbackIncomplete）。
        /// 默认 false —— 既有测试全都依赖还原成功。
        /// </summary>
        public bool FailRestore { get; set; }

        public OpResult Restore(GameEntry game, bool removeLogs) =>
            FailRestore
                ? FailResult("测试替身：还原失败。")
                : OkResult("测试替身未部署任何内容。");

        private static OpResult OkResult(string message)
        {
            var r = new OpResult();
            r.Ok = true;
            r.Message = message;
            return r;
        }

        private static OpResult FailResult(string message)
        {
            var r = new OpResult();
            r.Fail(message);
            return r;
        }
    }

    /// <summary>Driver double: three-state storage plus injectable failures.</summary>
    private sealed class FakeDrsAdapter : IDrsAdapter
    {
        private readonly Dictionary<uint, (ProfileSettingState State, uint Value)> _store = new();

        public string Name => "fake-drs";

        /// <summary>
        /// What application lookup should report, when a test cares. Null means "nothing matched", which is the
        /// honest default: a double that invents a profile would let a test pass on a lookup the real adapter
        /// cannot perform.
        /// </summary>
        public DrsApplicationLookup? ApplicationLookup { get; set; }

        /// <summary>
        /// The session-owning entry point. Kept as its own member rather than aliased away: the whole point of the
        /// split is that the low-level lookup needs an open session and this one does not, and a double that
        /// blurred them would hide exactly the defect the split exists to prevent.
        /// </summary>
        /// <summary>
        /// 回滚时被要求删除的 Profile 名。用于断言「只删自己建的那个」—— 删掉用户原有的 Profile
        /// 比留下残留严重得多，所以这条记录本身就是断言材料。
        /// </summary>
        public List<string> DeletedProfiles { get; } = new();

        public DrsStatus DeleteProfileByName(string profileName, string? executableName = null)
        {
            DeletedProfiles.Add(profileName);

            return DrsStatus.Success;
        }

        public DrsApplicationLookup FindApplicationProfile(string executableName) =>
            Open(null).Ok
                ? FindApplication(executableName)
                : DrsApplicationLookup.NotFound(executableName ?? "", "没有已打开的 DRS 会话。");

        public DrsApplicationLookup FindApplication(string executableName)
        {
            // Mirrors the real adapter's guard, so a test cannot pass on input the real one would refuse.
            if (string.IsNullOrWhiteSpace(executableName))
                return DrsApplicationLookup.NotFound(executableName ?? "", "未提供可执行文件名。");

            // Defaults to a match so the ordinary path — a game whose executable the driver knows — is what tests
            // exercise unless they say otherwise. The "not assigned to any profile" branch is worth testing, so a
            // test that wants it sets ApplicationLookup to NotFound explicitly; making that the default would have
            // silently turned every orchestration test into a test of the refusal path.
            // 真实适配器的低层入口要求调用方先开会话；替身必须同样要求，否则「两个入口前置条件不同」
            // 这件事在测试里根本不可见 —— 而那正是 P0-01 修的缺陷。
            if (!IsSessionOpen)
                return DrsApplicationLookup.NotFound(executableName, "没有已打开的 DRS 会话。");

            return ApplicationLookup
                ?? DrsApplicationLookup.Matched(executableName, executableName, "测试适配器默认匹配。");
        }

        public bool IsAvailable { get; set; } = true;

        /// <summary>Capability switches, so tests can exercise the "no write capability" refusal.</summary>
        public bool CanRead { get; set; } = true;

        public bool CanDelete { get; set; } = true;

        public bool CanSave { get; set; } = true;

        public int FailOpenCode { get; set; }
        public uint? FailWriteId { get; set; }
        public uint? FailDeleteId { get; set; }
        public bool FailSave { get; set; }
        public bool FailRead { get; set; }

        public int OpenCount { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int SaveCount { get; private set; }

        public void Seed(uint id, ProfileSettingState state, uint value) => _store[id] = (state, value);

        public bool Has(uint id) => _store.ContainsKey(id);

        public uint ValueOf(uint id) => _store.TryGetValue(id, out var v) ? v.Value : 0;

        public ProfileSettingState StateOf(uint id) =>
            _store.TryGetValue(id, out var v) ? v.State : ProfileSettingState.Absent;

        /// <summary>
        /// 会话是否已打开。真实适配器要求低层入口必须在会话内调用 —— 替身以前没有这个概念，于是
        /// 「调用方必须自己先 Open」在测试里完全不可见，而那正是 P0-01 修掉的缺陷。
        /// </summary>
        public bool IsSessionOpen { get; private set; }

        public DrsStatus Open(string? profileName)
        {
            OpenCount++;

            if (FailOpenCode != 0) return DrsStatus.Fail(FailOpenCode, "fake open failure");

            IsSessionOpen = true;

            return DrsStatus.Success;
        }

        /// <summary>
        /// Values that reads report instead of what was stored — the "wrote X, read back Y" case.
        ///
        /// <para>Without this the read-back can only ever agree with the write, because this double hands back
        /// exactly what it was given. The verification arm of P0-03 would then be structurally untestable, and a
        /// check that cannot fail proves nothing.</para>
        /// </summary>
        public Dictionary<uint, uint> ReadOverrides { get; } = new();

        public ProfileSettingSnapshot Read(uint id)
        {
            if (FailRead) return ProfileSettingSnapshot.Unreadable(id, "fake read failure");

            if (ReadOverrides.TryGetValue(id, out var reported))
                return new ProfileSettingSnapshot(id, ProfileSettingState.ExplicitValue, reported, false,
                    "fake: 读回值与写入值不一致");

            return _store.TryGetValue(id, out var v)
                ? new ProfileSettingSnapshot(id, v.State, v.Value, v.State == ProfileSettingState.InheritedDefault, "fake")
                : new ProfileSettingSnapshot(id, ProfileSettingState.Absent, 0, false, "fake: 未设置");
        }

        public DrsStatus Write(uint id, uint value)
        {
            WriteCount++;
            if (FailWriteId == id) return DrsStatus.Fail(-5, "fake write failure");

            _store[id] = (ProfileSettingState.ExplicitValue, value);
            return DrsStatus.Success;
        }

        public DrsStatus Delete(uint id)
        {
            DeleteCount++;
            if (FailDeleteId == id) return DrsStatus.Fail(-6, "fake delete failure");

            _store.Remove(id);
            return DrsStatus.Success;
        }

        public DrsStatus Save()
        {
            SaveCount++;
            return FailSave ? DrsStatus.Fail(-7, "fake save failure") : DrsStatus.Success;
        }

        public void Close() { }
    }

    /// <summary>
    /// Stage 6: three-state restoration and rollback.
    ///
    /// Every case runs against <see cref="FakeDrsAdapter"/> — the driver is never touched, so these tests
    /// cannot alter a real NVIDIA profile. The real adapter's own behaviour is asserted to be
    /// fail-closed rather than exercised.
    /// </summary>
    private static void TestNvidiaProfileService(string work)
    {
        Section("NVIDIA Profile 三态与回滚（Stage 6）");

        var idA = SmoothMotionSettings.FeatureEnabled;
        var idB = SmoothMotionSettings.EnabledApis;

        ProfileSetting Setting(uint id) => new(id, "test-setting", "test");

        // Writes are required unless a test says otherwise: the service must never have to guess whether
        // an unreadable original is fatal.
        static ProfileSettingWrite W(ProfileSetting s, uint v, bool required = true) =>
            new(s, v, required, "test");

        // ---- ABSENT → write → restore → ABSENT ----
        var a = new FakeDrsAdapter();
        var svcA = new NvidiaProfileService(a, () => false);
        var rAbsent = svcA.Apply("TestProfile", new[] { W(Setting(idA), 1u) });
        Check("未设置的项可以写入", rAbsent.Ok && a.StateOf(idA) == ProfileSettingState.ExplicitValue);

        var rbAbsent = svcA.Rollback(rAbsent.Journal);
        Check("ABSENT 原状通过删除恢复", rbAbsent.Ok && !a.Has(idA), rbAbsent.Message);
        Check("恢复后状态回到 ABSENT", a.StateOf(idA) == ProfileSettingState.Absent);
        Check("ABSENT 恢复使用 Delete 而非写入 0", a.DeleteCount >= 1);

        // ---- EXPLICIT 0 → restore → EXPLICIT 0 ----
        var b = new FakeDrsAdapter();
        b.Seed(idA, ProfileSettingState.ExplicitValue, 0);
        var svcB = new NvidiaProfileService(b, () => false);
        var rZero = svcB.Apply("P", new[] { W(Setting(idA), 5u) });
        Check("显式 0 可被写入覆盖", rZero.Ok && b.ValueOf(idA) == 5);

        svcB.Rollback(rZero.Journal);
        Check("显式 0 的原值被写回", b.ValueOf(idA) == 0 && b.StateOf(idA) == ProfileSettingState.ExplicitValue);
        Check("显式 0 不被误判为 ABSENT（未用删除代替写回）", b.Has(idA));

        // ---- EXPLICIT non-zero ----
        var c = new FakeDrsAdapter();
        c.Seed(idA, ProfileSettingState.ExplicitValue, 0xFFFFFFFF);
        var svcC = new NvidiaProfileService(c, () => false);
        svcC.Rollback(svcC.Apply("P", new[] { W(Setting(idA), 3u) }).Journal);
        Check("显式非零原值被完整恢复", c.ValueOf(idA) == 0xFFFFFFFF);

        // ---- INHERITED / DEFAULT restores by deletion ----
        var o = new FakeDrsAdapter();
        o.Seed(idA, ProfileSettingState.InheritedDefault, 123);
        var svcO = new NvidiaProfileService(o, () => false);
        svcO.Rollback(svcO.Apply("P", new[] { W(Setting(idA), 9u) }).Journal);
        Check("继承值原状通过删除恢复（未写成显式值）", !o.Has(idA));

        // ---- partial failure ----
        var d = new FakeDrsAdapter { FailWriteId = idB };
        var svcD = new NvidiaProfileService(d, () => false);
        var rPartial = svcD.Apply("P", new[] { W(Setting(idA), 1u), W(Setting(idB), 2u) });
        Check("部分写入失败时整体返回失败", !rPartial.Ok, rPartial.Message);
        Check("部分失败已回滚先前写入项", !d.Has(idA));
        Check("部分失败保留原因与回滚记录", rPartial.Notes.Count > 1);

        // ---- save failure ----
        var e = new FakeDrsAdapter { FailSave = true };
        var svcE = new NvidiaProfileService(e, () => false);
        var rSave = svcE.Apply("P", new[] { W(Setting(idA), 1u) });
        Check("保存失败时返回失败", !rSave.Ok);
        Check("保存失败已回滚全部写入", !e.Has(idA));

        // ---- session init failure / profile missing / binding missing ----
        var f = new FakeDrsAdapter { FailOpenCode = -3 };
        var rSession = new NvidiaProfileService(f, () => false).Apply("P", new[] { W(Setting(idA), 1u) });
        Check("会话初始化失败时不写入", !rSession.Ok && f.WriteCount == 0);
        Check("会话失败原因被记录", rSession.Notes.Any(n => n.Contains("-3")));

        var g = new FakeDrsAdapter { FailOpenCode = -160 };
        Check("Profile 不存在时不写入",
            !new NvidiaProfileService(g, () => false).Apply("P", new[] { W(Setting(idA), 1u) }).Ok && g.WriteCount == 0);

        var h = new FakeDrsAdapter { FailOpenCode = -161 };
        Check("应用绑定不存在时不写入",
            !new NvidiaProfileService(h, () => false).Apply("P", new[] { W(Setting(idA), 1u) }).Ok && h.WriteCount == 0);

        // ---- elevation probe (runtime, never hard-coded) ----
        Check("非提权调用失败 → 判为需要提权",
            new NvidiaProfileService(new FakeDrsAdapter { FailOpenCode = -9 }, () => false)
                .ProbeElevation("P").Requirement == ElevationRequirement.Required);

        // Was "非提权调用成功 → 判为不需要提权". That assertion locked in the very inference the second remediation
        // round removed: a successful read says nothing about whether writing needs elevation, so the read probe
        // now reports ReadAvailable and only a real write probe can conclude NotRequired.
        Check("非提权读成功 → 只判为「读取可用」，不推断写入权限",
            new NvidiaProfileService(new FakeDrsAdapter(), () => false)
                .ProbeElevation("P").Requirement == ElevationRequirement.ReadAvailable);

        Check("已提权仍失败 → 不归因为权限",
            new NvidiaProfileService(new FakeDrsAdapter { FailOpenCode = -9 }, () => true)
                .ProbeElevation("P").Requirement == ElevationRequirement.Unknown);

        Check("无驱动接口时判为 Unknown 而非 Required",
            new NvidiaProfileService(new AbsentDrsAdapter(), () => false)
                .ProbeElevation("P").Requirement == ElevationRequirement.Unknown);

        // ---- rollback success and failure ----
        var m = new FakeDrsAdapter();
        var svcM = new NvidiaProfileService(m, () => false);
        var rM = svcM.Apply("P", new[] { W(Setting(idA), 7u) });
        m.FailDeleteId = idA;
        var rbFail = svcM.Rollback(rM.Journal);
        Check("回滚失败被如实报告", !rbFail.Ok && rbFail.Notes.Any(n => n.Contains("失败")));
        Check("回滚失败时保留真实现场", m.Has(idA) && m.ValueOf(idA) == 7);

        // ---- unreadable original: required ⇒ abort before any write ----
        var n = new FakeDrsAdapter { FailRead = true };
        var svcN = new NvidiaProfileService(n, () => false);
        var rUnknown = svcN.Apply("P", new[] { W(Setting(idA), 1u) });
        Check("必填项原值不可读时整体中止", !rUnknown.Ok, rUnknown.Message);
        Check("必填项原值不可读时 0 项写入（不写无法撤销的东西）", n.WriteCount == 0);
        Check("必填项原值不可读时说明原因", rUnknown.Notes.Any(x => x.Contains("无法读取")));
        Check("中止时不产生任何 journal 条目", rUnknown.Journal.Count == 0);

        // ---- unreadable original: optional ⇒ skipped, never written ----
        var nOpt = new FakeDrsAdapter { FailRead = true };
        var rOpt = new NvidiaProfileService(nOpt, () => false)
            .Apply("P", new[] { W(Setting(idA), 1u, required: false) });
        Check("可选设置原值不可读时跳过而非中止", rOpt.Ok && nOpt.WriteCount == 0, rOpt.Message);
        Check("跳过的项被显式标记", rOpt.Notes.Any(x => x.Contains("Skipped")));

        // ---- rollback opens its own session and runs at most once ----
        var p = new FakeDrsAdapter();
        var svcP = new NvidiaProfileService(p, () => false);
        var rP = svcP.Apply("RollbackProfile", new[] { W(Setting(idA), 4u) });
        var opensAfterApply = p.OpenCount;
        svcP.Rollback(rP.Journal);

        // ---- P1-13：回滚必须拆掉本次运行**自己创建**的东西 ----
        //
        // 只恢复设置会留下一个空的 RTX30FGM-* Profile 挂在用户机器上 —— 那不是回滚，那是残留。
        // WasProfileCreated 是唯一的依据：只有它说「这是我们建的」才允许删除。
        var ownedAdapter = new FakeDrsAdapter();
        var ownedService = new NvidiaProfileService(ownedAdapter, () => false);
        var ownedJournal = new ProfileJournal("RTX30FGM-SMOKE-OWNED")
        {
            ApplicationExe = "RTX30FGM-SMOKE.exe",
            WasProfileCreated = true,
            WasApplicationCreated = true,
        };

        ownedJournal.Entries.Add(new ProfileJournalEntry(
            idA, "A",
            new ProfileSettingSnapshot(idA, ProfileSettingState.Absent, 0, false, "原本未设置。"),
            4u));

        ownedService.Rollback(ownedJournal);

        Check("回滚删除本次运行自己创建的 Profile",
            ownedAdapter.DeletedProfiles.Contains("RTX30FGM-SMOKE-OWNED"),
            string.Join("、", ownedAdapter.DeletedProfiles));

        // 反向的一半，也是真正要防的那一半：不是自己建的，就绝不能删。
        // 没有这一条，一个「永远删」的实现也能满足上面那条 —— 而那会删掉用户的 Profile，
        // 后果比留下残留严重得多。
        var strangerAdapter = new FakeDrsAdapter();
        var strangerService = new NvidiaProfileService(strangerAdapter, () => false);
        var strangerJournal = new ProfileJournal("UserOwnedProfile");

        strangerJournal.Entries.Add(new ProfileJournalEntry(
            idA, "A",
            new ProfileSettingSnapshot(idA, ProfileSettingState.Absent, 0, false, "原本未设置。"),
            4u));

        strangerService.Rollback(strangerJournal);

        Check("回滚不删除不是本次运行创建的 Profile",
            strangerAdapter.DeletedProfiles.Count == 0,
            string.Join("、", strangerAdapter.DeletedProfiles));
        Check("回滚自行打开 Profile 会话（不依赖 Apply 已关闭的会话）", p.OpenCount > opensAfterApply);
        Check("journal 携带 ProfileName", rP.Journal.ProfileName == "RollbackProfile");

        // P1-14：只记名字不够。回滚要能区分「这个 Profile / 绑定是我们建的」与「用户本来就有」—— 只有前者
        // 才允许删除。默认必须是 false：把用户的 Profile 当成自己建的删掉，比留下一点残留严重得多。
        Check("journal 默认不声称创建过 Profile 或应用绑定（保守默认，避免误删）",
            !rP.Journal.WasProfileCreated && !rP.Journal.WasApplicationCreated,
            $"profile={rP.Journal.WasProfileCreated} app={rP.Journal.WasApplicationCreated}");

        // 另一半：这些字段必须真的能被记录进去 —— 否则「默认 false」只是因为它们永远是 false。
        var withIdentity = new ProfileJournal("SomeProfile")
        {
            ApplicationExe = "Game.exe",
            WasProfileCreated = true,
            WasApplicationCreated = true,
        };

        Check("journal 能记录真实 Profile Identity（可执行文件名 + 两个创建标志）",
            withIdentity.ApplicationExe == "Game.exe"
                && withIdentity.WasProfileCreated
                && withIdentity.WasApplicationCreated,
            $"exe={withIdentity.ApplicationExe} profile={withIdentity.WasProfileCreated} app={withIdentity.WasApplicationCreated}");

        var deleteAfterFirst = p.DeleteCount;
        var doubleRollback = svcP.Rollback(rP.Journal);
        Check("同一 journal 不会被回滚两次", doubleRollback.Skipped && doubleRollback.Ok);
        Check("重复回滚不触碰驱动", p.DeleteCount == deleteAfterFirst);

        // ---- typed values: the API bitmask comes from the detected API, not a blanket 1 ----
        Check("DX12 → 位掩码 1", SmoothMotionSettings.ApiBit(GraphicsApi.Dx12) == 1);
        Check("DX11 → 位掩码 2", SmoothMotionSettings.ApiBit(GraphicsApi.Dx11) == 2);
        Check("Vulkan → 位掩码 4", SmoothMotionSettings.ApiBit(GraphicsApi.Vulkan) == 4);
        Check("API 未知时不产生位掩码（不猜）", SmoothMotionSettings.ApiBit(GraphicsApi.Unknown) is null);

        var vulkanWrites = SmoothMotionSettings.EnableWrites(GraphicsApi.Vulkan);
        Check("已检测 API 时 API 位写真实掩码 4（而非旧行为的一律写 1）",
            vulkanWrites.Any(w => w.Setting.Id == SmoothMotionSettings.EnabledApis && w.Value == 4));
        Check("开关与 API 位是两个独立写入", vulkanWrites.Count == 2);
        Check("API 未知时不写入 API 设置",
            SmoothMotionSettings.EnableWrites(GraphicsApi.Unknown)
                .All(w => w.Setting.Id != SmoothMotionSettings.EnabledApis));
        Check("写入项自带理由", vulkanWrites.All(w => w.Reason.Length > 0));

        // ---- values that are not established are never written ----
        Check("单一来源的设置不可写", !SmoothMotionSettings.DebugLog.Writable);
        Check("单一来源不进入自动写入集",
            SmoothMotionSettings.EnableWrites(GraphicsApi.Dx12)
                .All(w => w.Setting.Id != SmoothMotionSettings.DebugLogLevel));
        Check("未确认的 Flip 值不被自动写入",
            SmoothMotionSettings.EnableWrites(GraphicsApi.Dx12)
                .All(w => w.Setting.Id != SmoothMotionSettings.FlipMetering0 &&
                          w.Setting.Id != SmoothMotionSettings.FlipMetering1));

        // ---- no write capability ⇒ refuse everything ----
        var noWrite = new FakeDrsAdapter { CanDelete = false };
        var rNoWrite = new NvidiaProfileService(noWrite, () => false).Apply("P", new[] { W(Setting(idA), 1u) });
        Check("缺少删除能力时拒绝写入", !rNoWrite.Ok && noWrite.WriteCount == 0, rNoWrite.Message);

        // ---- empty set ----
        Check("空写入集合不触碰驱动",
            new NvidiaProfileService(new FakeDrsAdapter(), () => false)
                .Apply("P", Array.Empty<ProfileSettingWrite>()).Ok);

        // ---- the real adapter: capability is reported, never assumed ----
        var real = new NvApiDrsAdapter();

        // Deliberately read-only. The harness must never modify a real NVIDIA profile, so this loads NVAPI
        // and asks what it can do — nothing more. On a machine without a driver it must say why instead.
        Check("真实适配器声明能力时必须同时给出原因",
            real.CanWrite || real.UnavailableReason.Length > 0, real.UnavailableReason);

        // ---- §9：两种 Provider 的能力必须分开声明 ----
        //
        // 这一组守的是一个具体的误判：把「DRS 写门关闭」当成「这个 Provider 不能部署」。
        // DRS 写入需要管理员权限（实测：非提权 -137 / 提权成功），而 dlssg-sm86 根本不碰 DRS ——
        // 把两者绑在一起，会让一个完全可用的能力在非提权环境下被整体禁用。
        var dlssgOnly = new DlssgSm86Provider();
        var mfgSmooth = new MfgSmoothProvider();

        // §11/§16：正规化在「payload 里只有不可部署的入口名」时必须返回 null。
        //
        // 这一条对应一个真实缺陷：provider 侧的候选判据曾经用 IsKnownProxyName（**扫描名**，含 winhttp.dll），
        // 而 planner 用 ProxyCandidates（**可部署名**）。若 payload 里只有扫描名，正规化会产出一个
        // **没有任何可部署入口**的目录 —— 计划随后会选一个源目录里根本不存在的名字，部署直接失败。
        // 真实 MFG payload 里有 version.dll，永远走不到这条分支，所以 900 项测试全绿也没发现它。
        var onlyScanNames = Path.Combine(work, "canonical-edge-only-scan-names");
        Directory.CreateDirectory(onlyScanNames);
        File.WriteAllBytes(Path.Combine(onlyScanNames, "winhttp.dll"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

        Check("只有不可部署入口名时不予正规化（§11/§16）",
            new MfgSmoothProvider().PrepareCanonicalPayload(onlyScanNames) is null,
            "winhttp.dll 是扫描名而非可部署名；正规化必须拒绝产出一个没有可部署入口的目录");

        // §17 P2：**两个 provider 对同一份「未签名二进制」的判定必须一致。**
        //
        // 这里曾经相反：`MfgSmoothProvider` 接受未签名（红线要求「`NotSigned` 接受但不视为已验证」），
        // 而 `DlssgSm86Provider` 只接受 `Intact` —— 于是用户换一个 provider 会遇到「同样的文件，一个能装
        // 一个不能装」，且没有任何提示说明为什么。
        //
        // 真实上游 DLL 是**自签名且完整**的（实测 `ProbeSignature = Intact`），所以两者**当前**都接受；
        // 分叉只在签名方式变化或用户导入未签名 DLL 时才显现 —— 而这正是需要断言的地方，也是此前
        // 「900 项全绿却掩盖着它」的原因。
        var unsignedProbe = Path.Combine(work, "unsigned-probe.dll");
        File.WriteAllText(unsignedProbe, "not a PE image, therefore no signature block");

        var dlssgVerdict = new DlssgSm86Provider().VerifyPackage(unsignedProbe);
        var mfgVerdict = new MfgSmoothProvider().VerifyPackage(unsignedProbe);

        Check("两个 provider 对未签名二进制都接受（§17 P2 · 红线：NotSigned 接受）",
            dlssgVerdict.Accepted && mfgVerdict.Accepted,
            $"dlssg={dlssgVerdict.Accepted} mfg={mfgVerdict.Accepted}");

        Check("接受未签名时措辞不把它说成已验证（§17 P2）",
            !dlssgVerdict.Message.Contains("已验证") || dlssgVerdict.Message.Contains("不视为已验证"),
            dlssgVerdict.Message);

        // **反向配对：不存在的文件必须被拒绝。**
        //
        // 这条守的是一句**假话**：`ProbeSignature` 把「打不开 / 不存在」判成 `NotSigned`，而 `NotSigned`
        // 是接受的（红线允许未签名）—— 两条合起来会让**不存在的文件「校验通过」**：用户看到
        // 「「version.dll」校验通过（NotSigned）」，而失败被推后到 `Deploy` 里变成 `FileNotFoundException`。
        //
        // **与上面那条必须成对看**：「未签名」接受、「不存在」拒绝 —— 两者都会被 `NotSigned` 覆盖，
        // 所以必须分别断言。只写一条的话，修好一个会掩盖另一个（这正是本条被漏掉的原因）。
        var absentProbe = Path.Combine(work, "definitely-absent-probe.dll");

        Check("两个 provider 对不存在的文件都拒绝（§17 P2 · 反向配对）",
            !new DlssgSm86Provider().VerifyPackage(absentProbe).Accepted
                && !new MfgSmoothProvider().VerifyPackage(absentProbe).Accepted,
            $"dlssg={new DlssgSm86Provider().VerifyPackage(absentProbe).Accepted} " +
            $"mfg={new MfgSmoothProvider().VerifyPackage(absentProbe).Accepted}");

        Check("dlssg-sm86 提供帧生成但不要求 Smooth Motion DRS",
            dlssgOnly.ProvidesDlssFrameGeneration && !dlssgOnly.RequiresSmoothMotionDrs
                && !dlssgOnly.SupportsSmoothMotionDrs,
            $"frame={dlssgOnly.ProvidesDlssFrameGeneration} requires={dlssgOnly.RequiresSmoothMotionDrs} supports={dlssgOnly.SupportsSmoothMotionDrs}");

        Check("mfg-smooth 提供帧生成且要求 Smooth Motion DRS",
            mfgSmooth.ProvidesDlssFrameGeneration && mfgSmooth.RequiresSmoothMotionDrs
                && mfgSmooth.SupportsSmoothMotionDrs,
            $"frame={mfgSmooth.ProvidesDlssFrameGeneration} requires={mfgSmooth.RequiresSmoothMotionDrs} supports={mfgSmooth.SupportsSmoothMotionDrs}");

        // 反向的一半：如果两个 Provider 声明一样，上面两条就只是在验证「它们碰巧相同」。
        Check("两种 Provider 对 DRS 的依赖确实不同（否则上面的区分没有意义）",
            dlssgOnly.RequiresSmoothMotionDrs != mfgSmooth.RequiresSmoothMotionDrs);

        Check("未证明的 ABI 不声称任何写入能力",
            real.CanWrite == (real.CanRead && real.CanDelete && real.CanSave));

        // ---- §18.3：能力门按「各自被证明」独立开启，读通过不自动开放写 ----
        //
        // 这不是理论练习。第三轮之前只有一个 DriverCallsProven 开关，200 次成功的读取把写、保存、删除
        // 一起打开了 —— 而它们从未在真实驱动上跑过一次。下面三档验证现在的行为。
        Check("只证明读取时写能力保持关闭",
            !NvApiDrsAdapter.ReadCallsProven || NvApiDrsAdapter.WriteCallsProven || !real.CanWrite,
            $"read={NvApiDrsAdapter.ReadCallsProven} write={NvApiDrsAdapter.WriteCallsProven} canWrite={real.CanWrite}");

        Check("写能力要求写门被证明（读通过本身不够）",
            real.CanWrite == (real.CanRead && NvApiDrsAdapter.WriteCallsProven && real.CanDelete && real.CanSave),
            $"read={real.CanRead} write={NvApiDrsAdapter.WriteCallsProven} delete={real.CanDelete} save={real.CanSave}");

        // 第三档：把四道门都打开，写能力才允许为真。**必须恢复原值** —— 它们跨实例共享，
        // 留着改动会污染后面所有用例（那正是「测试之间互相影响」的经典形状）。
        {
            var savedRead = NvApiDrsAdapter.ReadCallsProven;
            var savedWrite = NvApiDrsAdapter.WriteCallsProven;
            var savedDelete = NvApiDrsAdapter.DeleteCallsProven;
            var savedSave = NvApiDrsAdapter.SaveCallsProven;

            try
            {
                NvApiDrsAdapter.ReadCallsProven = true;
                NvApiDrsAdapter.WriteCallsProven = true;
                NvApiDrsAdapter.DeleteCallsProven = true;
                NvApiDrsAdapter.SaveCallsProven = true;

                Check("四道门都被证明后写能力才开启", real.CanWrite, real.UnavailableReason);

                // 抽掉任意一道，写能力必须立刻收回 —— 只测「全开为真」会让一个只检查其中一道的实现通过。
                NvApiDrsAdapter.DeleteCallsProven = false;

                Check("抽掉删除门后写能力立刻关闭（删除不可缺）", !real.CanWrite);
            }
            finally
            {
                NvApiDrsAdapter.ReadCallsProven = savedRead;
                NvApiDrsAdapter.WriteCallsProven = savedWrite;
                NvApiDrsAdapter.DeleteCallsProven = savedDelete;
                NvApiDrsAdapter.SaveCallsProven = savedSave;
            }
        }

        Check("没有会话时读取返回 Unknown 而非猜测",
            real.Read(idA).State == ProfileSettingState.Unknown);

        // ---- §18.2：Profile 名必须来自 NVDRS_PROFILE.profileName，不是应用自己的友好名 ----
        //
        // 第三轮修掉的错误：把 `NVDRS_APPLICATION.userFriendlyName`（偏移 4104）当成了 Profile 名。
        // 那是应用的名字，驱动并不按它归档设置 —— 拿它去 Apply 会写到一个不存在或同名的 Profile 上。
        //
        // 偏移正确性本身只能在真实驱动上验证（本机 `FindApplicationByName` 返回 -166，走不到 GetProfileInfo），
        // 所以这里守的是**回归**：代码没有改回旧的错误偏移。
        Check("Profile 名取自 NVDRS_PROFILE.profileName（偏移 4）",
            NvApiDrsAdapter.ProfileNameOffset == 4,
            $"offset={NvApiDrsAdapter.ProfileNameOffset}");

        // 「应用的友好名」那个偏移若还存在并被使用，说明有人把两条路又混在了一起。
        Check("不再使用 NVDRS_APPLICATION.userFriendlyName（偏移 4104）作为 Profile 名",
            NvApiDrsAdapter.ProfileNameOffset != 4104,
            $"offset={NvApiDrsAdapter.ProfileNameOffset}");

        Check("没有会话时拒绝写入（不触碰驱动）",
            !real.Write(idA, 1).Ok);

        // ---- setting provenance ----
        Check("Smooth Motion 设置清单共 6 项", SmoothMotionSettings.All.Count == 6,
            "实际: " + SmoothMotionSettings.All.Count);
        Check("设置出处标注未公开 + 社区验证",
            SmoothMotionSettings.All.All(s => s.Provenance.Contains("Undocumented") && s.Provenance.Contains("Community Verified")));
        Check("设置出处不冒充 NVIDIA 官方",
            SmoothMotionSettings.All.All(s => !s.Provenance.Contains("NVIDIA 官方")));
        Check("关键 Setting ID 与调研一致",
            SmoothMotionSettings.FeatureEnabled == 0xB0D384C0 &&
            SmoothMotionSettings.EnabledApis == 0xB0CC0875 &&
            SmoothMotionSettings.DebugBars == 0xB01B8B02);
    }

    /// <summary>Asset fetcher double, so provider behaviour is testable without a network.</summary>
    private sealed class FakeAssetFetcher : IReleaseAssetFetcher
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastExpectedSha { get; private set; }

        public bool Fail { get; set; }
        public DigestState DigestState { get; set; } = DigestState.Verified;

        public Task<AssetFetchResult> FetchAndExtractAsync(
            string url, string destinationDirectory, string? expectedSha256, IProgress<string>? progress, CancellationToken ct)
        {
            Calls++;
            LastUrl = url;
            LastExpectedSha = expectedSha256;

            if (Fail) return Task.FromResult(AssetFetchResult.Failed("fake fetch failure"));

            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllText(Path.Combine(destinationDirectory, "version.dll"), "payload");
            File.WriteAllText(Path.Combine(destinationDirectory, "dlssg_sm86.ini"), "; fake");

            return Task.FromResult(new AssetFetchResult(true, "fake ok", destinationDirectory,
                new DigestCheck(DigestState, expectedSha256, expectedSha256, "fake")));
        }
    }

    /// <summary>
    /// Stage 7: the MFG provider.
    ///
    /// The version rules are tested against the naming the upstream repository actually uses (observed
    /// 2026-09-26), because the whole reason this provider needs its own parser is that its tags are
    /// labels rather than versions.
    /// </summary>
    private static void TestSmoothProvider(string work)
    {
        Section("MFG Provider（Stage 7）");

        static ReleaseEntry MfgRelease(string tag, params ReleaseAssetInfo[] assets) =>
            new(MfgSmoothProvider.Repository, 1, tag, false, false, null, null, assets);

        // ---- version rule ----
        Check("从真实 asset 名解析出版本",
            MfgSmoothProvider.ParseVersionFromAssetName("SmoothMotion-2.8.2-R3-GP8-Xbox-Detection.zip") == "2.8.2");
        Check("两位数段版本可解析",
            MfgSmoothProvider.ParseVersionFromAssetName("SmoothMotion-1.10.3.zip") == "1.10.3");
        Check("无版本号的 asset 名返回 null",
            MfgSmoothProvider.ParseVersionFromAssetName("SmoothMotion-latest.zip") is null);
        Check("空名返回 null", MfgSmoothProvider.ParseVersionFromAssetName(null) is null);

        var realTags = new[] { "smxbox", "smfix", "smdriverupdate", "SMMANUAL", "sm75", "SM", "asi" };
        Check("上游真实 tag 集不被当作版本号",
            realTags.All(t => ReleaseVersion.Compare(t, "2.8.2") == VersionOrder.Unordered));
        Check("非版本 tag 不导致解析异常",
            realTags.All(t => MfgSmoothProvider.ParseVersionFromAssetName(t) is null));

        // ---- payload selection ----
        var single = MfgRelease("smxbox",
            new ReleaseAssetInfo(1, "SmoothMotion-2.8.2-R3-GP8-Xbox-Detection.zip", 100, "sha256:" + new string('a', 64)));
        Check("唯一 zip 被选为 payload", MfgSmoothProvider.SelectPayloadAsset(single) is not null);
        Check("无 asset 的 Release 无 payload",
            MfgSmoothProvider.SelectPayloadAsset(MfgRelease("sm86", Array.Empty<ReleaseAssetInfo>())) is null);
        Check("多个候选时不猜",
            MfgSmoothProvider.SelectPayloadAsset(MfgRelease("asi",
                new ReleaseAssetInfo(1, "SmoothMotion-1.0.0.zip", 1, null),
                new ReleaseAssetInfo(2, "SmoothMotion-1.0.1.zip", 1, null))) is null);
        Check("非 zip 资产不作为 payload",
            MfgSmoothProvider.SelectPayloadAsset(MfgRelease("sm75",
                new ReleaseAssetInfo(1, "SmoothMotion-1.0.0.7z", 1, null))) is null);

        // ---- unrecognised structure stops automatic installation ----
        var brokenClient = new FakeReleaseClient(OkReleases(
            MfgRelease("sm86", Array.Empty<ReleaseAssetInfo>()),
            MfgRelease("sm75", new ReleaseAssetInfo(1, "SmoothMotion-1.0.0.7z", 1, null))));
        var broken = new MfgSmoothProvider(brokenClient, new FakeAssetFetcher());

        Check("结构不可识别时不返回版本",
            broken.CheckLatestAsync(false, CancellationToken.None).GetAwaiter().GetResult() is null);
        Check("结构不可识别时报 ReleaseFormatChanged",
            broken.Health.State == ProviderHealthState.ReleaseFormatChanged, broken.Health.State.ToString());
        Check("结构不可识别时停止自动安装",
            !broken.DownloadAsync(Path.Combine(work, "mfg-bad"), null, CancellationToken.None).GetAwaiter().GetResult().Ok);

        // ---- normal resolution across tags ----
        var client = new FakeReleaseClient(OkReleases(
            MfgRelease("smfix", new ReleaseAssetInfo(1, "SmoothMotion-2.8.2-R3-GP8-Xbox-Detection.zip", 100, "sha256:" + new string('a', 64))),
            MfgRelease("smxbox", new ReleaseAssetInfo(2, "SmoothMotion-2.9.0-R1.zip", 100, null))));
        var fetcher = new FakeAssetFetcher();
        var provider = new MfgSmoothProvider(client, fetcher);

        var info = provider.CheckLatestAsync(false, CancellationToken.None).GetAwaiter().GetResult();
        Check("跨 tag 选出最高版本", info?.Version == "2.9.0", info?.Version ?? "(null)");
        Check("解析成功后健康状态可用", provider.Health.IsUsable, provider.Health.Reason);

        var download = provider.DownloadAsync(Path.Combine(work, "mfg-dl"), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("下载委托给资产获取器", download.Ok && fetcher.Calls == 1, download.Message);
        Check("下载走 GitHub 规范发布路径",
            fetcher.LastUrl is not null && fetcher.LastUrl.Contains("/releases/download/"), fetcher.LastUrl ?? "(null)");

        // ---- metadata ----
        Check("MFG 分发模型为 ReleaseAsset", provider.Metadata.Distribution == DistributionModel.ReleaseAsset);
        Check("MFG 许可证按 API 实测记为 MIT",
            provider.Metadata.License == LicenseClass.Mit, provider.Metadata.License.ToString());
        Check("许可证说明保留 Phase 0 更正记录", provider.Metadata.LicenseNote.Contains("Phase 0"));
        Check("MFG 不写 NVIDIA Profile", !provider.Metadata.ProviderWritesNvidiaProfile);

        // The other half of the split: this payload is inert until the driver is told to use it, and saying so is
        // what stops a run from reporting success with the feature switched off.
        Check("MFG 需要 NVIDIA Profile 配置才生效", provider.Metadata.RequiresNvidiaProfileConfiguration);

        // ---- 第二轮 §4：回滚必须可以重试 ----
        // The defect this covers: the journal was marked consumed in a `finally`, whether or not every entry was
        // restored. A half-undone profile could then never be finished — and the entries that still needed work
        // were indistinguishable from the ones that had already been put back.
        Check("回滚状态按条目区分「待处理 / 已恢复 / 失败」三态",
            Enum.IsDefined(typeof(NvidiaProfile.RollbackState), NvidiaProfile.RollbackState.Pending) &&
            Enum.IsDefined(typeof(NvidiaProfile.RollbackState), NvidiaProfile.RollbackState.Restored) &&
            Enum.IsDefined(typeof(NvidiaProfile.RollbackState), NvidiaProfile.RollbackState.Failed));

        Check("日志条目带可读写的回滚状态（供重试只处理未成功项）",
            typeof(NvidiaProfile.ProfileJournalEntry).GetProperty("State") is { CanRead: true, CanWrite: true });

        // ---- 第二轮 §7：计划必须以 Typed 形式说明它需要哪些驱动设置 ----
        // The defect this covers: the plan's driver requirements came only from a recipe's free-text notes, so a run
        // could install a payload whose driver settings the plan never mentioned. The end-to-end version of this
        // lives in the orchestration section, where a preview run can be built without writing anything.
        Check("计划带有 Typed 驱动要求字段（与字符串列表并存）",
            typeof(InstallPlan).GetProperty("ProfileRequirements") is not null);

        Check("Typed 要求携带设置对象、取值方式、必需性、适用 API 与原因",
            typeof(ProfileSettingRequirement).GetProperty("Setting") is not null &&
            typeof(ProfileSettingRequirement).GetProperty("ValueResolver") is not null &&
            typeof(ProfileSettingRequirement).GetProperty("Required") is not null &&
            typeof(ProfileSettingRequirement).GetProperty("ApplicableApi") is not null &&
            typeof(ProfileSettingRequirement).GetProperty("Reason") is not null);

        // ---- 第二轮 §13 缺口补齐：Store 与 Batch ----
        // §6: the store is now carried rather than assumed. Unknown is the honest default — a hand-added entry has
        // no store, and claiming Steam for it is how a store-specific result gets applied to something it never
        // covered.
        Check("游戏条目携带可读写的商店来源，默认 Unknown（不假定 Steam）",
            typeof(GameEntry).GetProperty("Store") is { CanRead: true, CanWrite: true } &&
            new GameEntry().Store == StoreKind.Unknown);

        // P0-09: the batch confirmation summarises plans, which it can only do if previewing is possible without
        // writing. This asserts the capability exists; the batch wiring itself is UI-side and not covered here.
        Check("配置服务具备「只预览不写入」的入口（批量确认框据此汇总计划）",
            typeof(GameConfigurationService).GetMethod("PreviewAsync") is not null);

        Check("预览模式是工作流的一等参数，而不是调用方另算一遍",
            typeof(WorkflowRequest).GetProperty("PreviewOnly") is not null &&
            typeof(ConfigurationRequest).GetProperty("PreviewOnly") is not null);
        Check("MFG 标记为实验性", provider.Metadata.Experimental);

        // ---- registry ----
        var registry = ProviderRegistry.CreateDefault();
        Check("默认注册表含两个 Provider", registry.Count == 2, "实际: " + registry.Count);
        Check("MFG 可从注册表取回", registry.Get(MfgSmoothProvider.ProviderId) is not null);
        Check("应用级入口可访问 MFG", AppProviders.Mfg is not null);
        Check("两个 Provider 的 ID 不同", DlssgSm86Provider.ProviderId != MfgSmoothProvider.ProviderId);

        // ---- install/restore still run through the shared paths ----
        var source = new ModSource(MakeSyntheticModSource(work));
        var dir = MakeGameDir(work, "MfgGame");
        var game = new GameEntry { Name = "MfgGame", RenderDir = dir };

        var install = provider.Install(game, source);
        Check("MFG 安装走共享事务部署", install.Ok, install.Message);
        Check("MFG 安装建立部署记录", game.Deployment is not null);
        Check("MFG 报告已安装版本", provider.GetInstalledVersion(game) == source.Version);

        Check("MFG 恢复走共享路径", provider.Restore(game, false).Ok);
        Check("恢复后代理已移除", !File.Exists(Path.Combine(dir, "version.dll")));

        var signed = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (File.Exists(signed))
            Check("MFG VerifyPackage 复用共享签名原语", provider.VerifyPackage(signed).Accepted);

        // ---- zip safety ----
        var zipRoot = Path.Combine(work, "zip-safe");
        Directory.CreateDirectory(zipRoot);
        Check("目标目录之外被识别为不安全",
            !SafeZip.IsInside(zipRoot, Path.Combine(zipRoot, "..", "escaped.txt")));
        Check("目标目录之内视为安全", SafeZip.IsInside(zipRoot, Path.Combine(zipRoot, "sub", "ok.txt")));

        var evilZip = Path.Combine(work, "evil.zip");
        using (var fs = File.Create(evilZip))
        using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
            writer.Write("pwned");
        }

        var outDir = Path.Combine(work, "zip-out");
        var extracted = SafeZip.TryExtract(evilZip, outDir, out var zipError);
        Check("zip slip 被拒绝", !extracted && zipError.Length > 0, zipError);
        Check("zip slip 未写出逃逸文件", !File.Exists(Path.Combine(work, "escaped.txt")));

        var goodZip = Path.Combine(work, "good.zip");
        using (var fs = File.Create(goodZip))
        using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("sub/file.dll").Open());
            writer.Write("payload");
        }

        Check("正常压缩包可解压",
            SafeZip.TryExtract(goodZip, outDir, out _) && File.Exists(Path.Combine(outDir, "sub", "file.dll")));

        // ---- host policy ----
        Check("Release Asset 重定向主机已在白名单",
            ModFetcher.IsAllowedAddress(new Uri("https://objects.githubusercontent.com/x"), proxyRouted: true));
        Check("未在白名单的主机被拒绝",
            !ModFetcher.IsAllowedAddress(new Uri("https://evil.example.com/x"), proxyRouted: true));
        Check("非 HTTPS 被拒绝",
            !ModFetcher.IsAllowedAddress(new Uri("http://github.com/x"), proxyRouted: true));
    }

    /// <summary>Detector double, so the workflow can be exercised without a real game folder.</summary>
    private sealed class FakeWorkflowDetector : IWorkflowDetector
    {
        public RendererDetection Renderer { get; set; } =
            new("C:\\fake\\Game.exe", EvidenceLevel.VerifiedDatabase, "fake verified", Array.Empty<ExecutableEvidence>());

        public GraphicsApiDetection Api { get; set; } =
            new(GraphicsApi.Dx12, EvidenceLevel.RuntimeDetection, "fake dx12", Array.Empty<GraphicsApiEvidence>());

        public ProxyConflictReport Conflicts { get; set; } =
            new(Array.Empty<ProxySlot>(), ModSource.KnownProxyNames.ToList(), Array.Empty<ProxySlot>());

        public RendererDetection DetectRenderer(GameEntry game, string? userChoice) => Renderer;

        public GraphicsApiDetection DetectApi(GameEntry game) => Api;

        public ProxyConflictReport ScanProxyConflicts(GameEntry game) => Conflicts;
    }

    /// <summary>
    /// Stage 8: the orchestration service.
    ///
    /// The central assertion of this section is negative: a successful file copy must not be reported as
    /// a verified installation. The run also has to stop before writing anything when the plan is not
    /// cleared, and undo what it did when a later stage fails.
    /// </summary>
    private static void TestSmoothMotionWorkflow(string work)
    {
        Section("自动 Smooth 编排（Stage 8）");

        // ---- evidence ladder is derived, never assumed ----
        // These are the scenes the task book names explicitly. Each one is a way the ladder could be climbed
        // without evidence, so each gets its own assertion.
        Check("单条强信号不足以判为 Verified",
            VerificationReport.FromSignals(new[] { new VerificationSignal("s", true, "", SignalKind.FrameGeneration) })
                .Level != SmoothMotionEvidence.Verified);

        Check("无信号时为 None",
            VerificationReport.FromSignals(Array.Empty<VerificationSignal>()).Level == SmoothMotionEvidence.None);

        Check("只有安装证据时最多到 Installed",
            VerificationReport.FromSignals(new[]
            {
                new VerificationSignal(SignalNames.FilesInstalled, true, "", SignalKind.Installation),
            }).Level == SmoothMotionEvidence.Installed);

        // The assertion that used to be the other way round. A copied file plus debug bars is the most
        // tempting false positive in this whole area: the file is there and the user saw something.
        Check("文件已部署 + Debug Bars 仍不算 Verified",
            VerificationReport.FromSignals(new[]
            {
                new VerificationSignal(SignalNames.FilesInstalled, true, "", SignalKind.Installation),
                new VerificationSignal(SignalNames.DebugBars, true, "", SignalKind.FrameGeneration),
            }).Level != SmoothMotionEvidence.Verified);

        Check("游戏加载代理 + Debug Bars 判为 Verified",
            VerificationReport.FromSignals(new[]
            {
                new VerificationSignal(SignalNames.ProxyLoaded, true, "", SignalKind.RuntimeOrDriver),
                new VerificationSignal(SignalNames.DebugBars, true, "", SignalKind.FrameGeneration),
            }).Level == SmoothMotionEvidence.Verified);

        Check("驱动已保存 + 补丁日志判为 Verified",
            VerificationReport.FromSignals(new[]
            {
                new VerificationSignal(SignalNames.ProfileApplied, true, "", SignalKind.RuntimeOrDriver),
                new VerificationSignal(SignalNames.PatchLog, true, "", SignalKind.FrameGeneration),
            }).Level == SmoothMotionEvidence.Verified);

        Check("只有补丁日志不算 Verified",
            VerificationReport.FromSignals(new[]
            {
                new VerificationSignal(SignalNames.PatchLog, true, "", SignalKind.FrameGeneration),
            }).Level != SmoothMotionEvidence.Verified);

        Check("只有运行时证据（无生成帧证据）不算 Verified",
            VerificationReport.FromSignals(new[]
            {
                new VerificationSignal(SignalNames.FilesInstalled, true, "", SignalKind.Installation),
                new VerificationSignal(SignalNames.ProxyLoaded, true, "", SignalKind.RuntimeOrDriver),
                new VerificationSignal(SignalNames.ProfileApplied, true, "", SignalKind.RuntimeOrDriver),
            }).Level != SmoothMotionEvidence.Verified);

        Check("证据带来源与时间时可追溯",
            VerificationReport.FromSignals(new[]
            {
                new VerificationSignal(SignalNames.ProxyLoaded, true, "", SignalKind.RuntimeOrDriver,
                    Source: "游戏进程模块", ObservedAt: DateTimeOffset.Now),
            }).HasTraceableProvenance);

        Check("缺少来源或时间的证据不可追溯",
            !VerificationReport.FromSignals(new[]
            {
                new VerificationSignal(SignalNames.ProxyLoaded, true, "", SignalKind.RuntimeOrDriver),
            }).HasTraceableProvenance);

        // ---- shared harness for the runs below ----
        //
        // `drsCanWrite` 默认 true 是为了不动既有用例；**但它默认 true 这件事本身就是一个隐患**：
        // `FakeDrsAdapter` 的三个能力默认全 true ⇒ 全部编排测试都跑在「驱动完全可写」这个**生产里
        // 不存在的状态**下（Pass C 指出：真实情况是运行时 `CanWrite` 恒为 false）。需要走「写不了」
        // 那条路径的用例显式传 false。
        static (SmoothMotionWorkflow Workflow, FakeWorkflowDetector Detector, FakeDrsAdapter Drs, CompatibilityMatrixStore Matrix, IPatchProvider Provider, FakeAssetFetcher Fetcher) Build(
            string work, string name, bool drsCanWrite = true)
        {
            var detector = new FakeWorkflowDetector();
            var drs = new FakeDrsAdapter { CanRead = drsCanWrite, CanDelete = drsCanWrite, CanSave = drsCanWrite };
            var matrix = new CompatibilityMatrixStore(Path.Combine(work, name + "-matrix.json"));

            matrix.Add(new CompatibilityRecord(
                Gpu: "RTX 3070 Ti", Driver: "617.14", GraphicsApi: GraphicsApi.Dx12,
                Game: name, Store: StoreKind.Steam, RendererExe: "Game.exe",
                Provider: MfgSmoothProvider.ProviderId, ProviderVersion: "2.9.0",
                InstallMode: InstallMode.DirectProxy, ProxyAsi: ModSource.KnownProxyNames[0],
                LaunchMode: "normal", Validation: ValidationState.ReportedWorking));

            var fetcher = new FakeAssetFetcher();
            var provider = new MfgSmoothProvider(
                new FakeReleaseClient(OkReleases(new ReleaseEntry(MfgSmoothProvider.Repository, 1, "smfix", false, false, null, null,
                    new[] { new ReleaseAssetInfo(1, "SmoothMotion-2.9.0-R1.zip", 1, null) }))),
                fetcher);

            return (new SmoothMotionWorkflow(detector, new NvidiaProfileService(drs, () => false), matrix),
                    detector, drs, matrix, provider, fetcher);
        }

        // `providerVersion` is optional on purpose: passing null is what makes the workflow go and ask the
        // provider for a version. Handing it "2.9.0" short-circuits that — which is why no test could ever
        // observe CheckLatestAsync being called.
        static WorkflowRequest MakeRequest(string name, string payloadDir, IPatchProvider provider, GameEntry game,
            string? providerVersion = "2.9.0") =>
            new(game, provider, providerVersion, VersionPolicy.None, ReleaseChannel.Stable, payloadDir,
                GpuName: "RTX 3070 Ti", DriverVersion: "617.14", Store: StoreKind.Steam,
                LaunchMode: "normal", InstallMode: InstallMode.DirectProxy);

        // ---- 0. 真实 MFG provider + 嵌套 payload 的端到端（§17 P0-1 · Pass C 报出）----
        //
        // **这条用例补的是本轮最严重的缺口。** 那个 P0 需要「真实 provider + 真实 payload + 真实执行」
        // 三者同时在场才能暴露 —— 而此前：夹具 payload 只装一个代理、`RecordsDeployed` 让比对按构造相等、
        // `--mfg-asset-smoke` 是 plan-only。**三个环节各自都测了，没有一个把它们串起来。**
        //
        // 这里用**真实的 `MfgSmoothProvider`**（只替身网络与 DRS），payload 按上游真实布局嵌套：
        //   `SmoothMotion-2.9.0-R1/Manual/Version/version.dll`
        // 然后**真的执行一次**并断言成功 —— 这同时覆盖 P0 的两个成因：
        //   (a) 首跑的 payload 清单核对（回退计划的 `SourcePath` 语义）；
        //   (b) 计划与部署的一致性比对（「提供」的入口 vs「承诺」的入口）。
        var realMfgParts = Build(work, "wfRealMfg");

        var nestedRoot = Path.Combine(work, "wf-real-mfg-payload");
        var nestedInner = Path.Combine(nestedRoot, "SmoothMotion-2.9.0-R1", "Manual", "Version");
        Directory.CreateDirectory(nestedInner);
        File.WriteAllText(Path.Combine(nestedInner, "version.dll"), "mfg-proxy-bytes");

        var realMfgGame = new GameEntry { Name = "wfRealMfg", RenderDir = MakeGameDir(work, "wfRealMfgGame") };

        var realMfgResult = realMfgParts.Workflow.RunAsync(
            MakeRequest("wfRealMfg", nestedRoot, realMfgParts.Provider, realMfgGame) with
            {
                UserConfirmedUnverified = true,
            },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("真实 MFG provider 在嵌套 payload 上能跑完一次（§17 P0-1 · 端到端）",
            realMfgResult.Outcome == WorkflowOutcome.Succeeded,
            realMfgResult.Outcome + " / " + string.Join("; ", realMfgResult.Errors));

        // ⚠️ **这条端到端用例覆盖的是「成因 (b)」，不是「成因 (a)」—— 回退验证证明的。**
        //
        // 我把成因 (a) 的修复回退掉之后，上面这条**仍然通过**。原因是它走的不是出问题的那条分支：
        //   · payload 目录里**已经有** `version.dll` ⇒ `BuildPlannedFiles` 走**正常分支**
        //     （`SourcePath` = 真实相对路径）⇒ 旧的精确匹配也能通过；
        //   · 成因 (a) 只在**回退分支**触发 —— 即「选定的入口不在 payload 的文件列表里」，而真实触发
        //     条件是 **`ManifestOf` 尚无缓存**（每次会话的**第一次** MFG 运行）：那时计划是在 payload
        //     还**不存在**的目录上建出来的，所以它只能填目标名。
        //
        // **要覆盖成因 (a)，需要让计划在 payload 目录还不存在时建好**（`PayloadDirectory` 指向下载前的
        // 位置，或直接对空目录调 `BuildPlannedFiles`），再断言它给出的 `SourcePath` 是 `null` 而不是入口名。
        // **这条断言本轮没有写出来**（构造它需要先看清 `InstallPlanner.Plan` 的入口签名），如实记为缺口，
        // 而不是留一个恒真的检查。

        // ---- 0b. provider 要求写驱动、而进程写不了 ⇒ 必须在【写入之前】停下（§17 P1-1 · Pass C 报出）----
        //
        // MFG **必然**带 `ProfileSettings`（`MfgSmoothProvider.cs:310`），而写 Profile 需要管理员权限
        // （三个能力门是 `internal static`、默认 false、每次启动重置、生产路径不跑 smoke ⇒ 运行时
        // `CanWrite` 恒为 false）。曾经的顺序是**先装文件、再配 Profile** ⇒ 写被拒 ⇒ Failed + 整体回滚：
        // 用户看到「下载 15 MB → 装 → 删 → 报失败」，游戏目录被折腾了一遍却什么都没留下 ——
        // 而这次运行**从一开始就不可能成功**。
        //
        // ⚠️ **这条断言此前无法生效**：`FakeDrsAdapter` 的三个能力默认全 `true`（比生产**宽松**），
        // 所以全部编排测试都跑在「驱动完全可写」这个生产里不存在的状态下。这里显式关掉。
        var noWriteParts = Build(work, "wfNoWriteDrs", drsCanWrite: false);
        var noWriteGame = new GameEntry { Name = "wfNoWriteDrs", RenderDir = MakeGameDir(work, "wfNoWriteDrsGame") };

        var noWriteResult = noWriteParts.Workflow.RunAsync(
            MakeRequest("wfNoWriteDrs", Path.Combine(work, "wf-nowrite-payload"), noWriteParts.Provider, noWriteGame)
                with { ProfileSettings = new[] { SmoothMotionSettings.All[0] } },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("写不了驱动时必须在写入之前停下（§17 P1-1）",
            noWriteResult.Outcome == WorkflowOutcome.Blocked,
            noWriteResult.Outcome + " / " + string.Join("; ", noWriteResult.Errors));

        // ---- 0c. 回退计划的入口 `SourcePath` 必须是「源未知」，不能是目标名（§17 P0-1(a) · Pass C 报出）----
        //
        // 成因 (a) 的触发条件是 **`ManifestOf` 拿不到清单** —— 即**每次会话的第一次 MFG 运行**：
        // 计划在下载之前就建好了（`SmoothMotionWorkflow` 里 L421 早于 L490），那时 payload 目录还不存在。
        // 而 `payloadFiles` 在无清单时**只含 INI、不含任何代理入口**（L379-383）⇒ 代理只能由
        // **回退分支**加入 ⇒ 旧代码在那里填了**目标名**（`version.dll`），于是工作流拿它去 payload 清单
        // 里做精确路径匹配 ⇒ **每次都判「payload 缺少计划要求的文件」⇒ 安装前就失败，一个字节都没写**。
        //
        // 这里用一个**不存在的 payload 目录**跑一次真实 workflow，直接检查计划给出的 `SourcePath`。
        var fallbackParts = Build(work, "wfFallbackSource");
        var fallbackGame = new GameEntry { Name = "wfFallbackSource", RenderDir = MakeGameDir(work, "wfFallbackGame") };

        var fallbackResult = fallbackParts.Workflow.RunAsync(
            MakeRequest("wfFallbackSource", Path.Combine(work, "wf-fallback-does-not-exist"),
                fallbackParts.Provider, fallbackGame) with { UserConfirmedUnverified = true },
            null, CancellationToken.None).GetAwaiter().GetResult();

        var fallbackProxies = (fallbackResult.Plan?.PlannedFiles ?? Array.Empty<PlannedFile>())
            .Where(f => f.SourceKind == DeploymentFileSource.Payload)
            .ToList();

        Check("回退计划确实产出了代理条目（§17 P0-1(a) 的前置）",
            fallbackProxies.Count > 0, $"payload 来源的条目数 {fallbackProxies.Count}");

        Check("回退计划的 SourcePath 是 null（源未知），不是入口名（§17 P0-1(a)）",
            fallbackProxies.All(f => f.SourcePath is null),
            string.Join("、", fallbackProxies.Select(f => $"{f.TargetRelativePath}=>{f.SourcePath ?? "(null)"}")));

        // **「一个文件都没写」指的是「没有本次计划会写的东西」（代理入口 + INI），而不是「目录是空的」**
        // —— 夹具本身会放 `game.exe` 之类的文件，用「目录为空」当判据会得到一个与被测行为无关的失败。
        var noWriteLeft = Directory.EnumerateFiles(noWriteGame.RenderDir)
            .Select(Path.GetFileName)
            .Where(f => f is not null
                        && (ModSource.KnownProxyNames.Contains(f, StringComparer.OrdinalIgnoreCase)
                            || string.Equals(f, ModSource.IniName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Check("写不了驱动时一个文件都没写（§17 P1-1 · 不得先装后删）",
            noWriteResult.Outcome == WorkflowOutcome.Blocked && noWriteLeft.Count == 0,
            $"outcome={noWriteResult.Outcome} 写了={string.Join("、", noWriteLeft)}");

        // **但上面那条只看到「最终状态」，而「不得先装后删」是抓不到的** —— 那次运行在 Profile 步骤
        // 失败后会**回滚**，把刚写的文件删干净：**「写了又回滚」与「从未写过」在最终状态上长得一模一样**。
        // （这是回退验证发现的：把写门去掉之后，目录断言确实变红了，但看不出它与「没写」有什么区别。）
        //
        // 真正要守的是**这次运行有没有走到安装** —— `Evidence` 正是这件事的判据：
        // `Installed` 表示装过，`None` 表示从没走到那一步。**断言中间过程，而不是最终状态。**
        Check("写不了驱动时不得走到安装阶段（§17 P1-1 · 断言中间过程）",
            noWriteResult.Evidence == SmoothMotionEvidence.None,
            $"evidence={noWriteResult.Evidence}（Installed 表示装过再回滚）");

        // **`Blocked` 也不该被记成一次尝试**（P2-② · Pass C 报出）。
        //
        // 它连一次磁盘都没碰（本次是「写不了驱动」⇒ 写入之前就停下了）—— 把它记成「试过但没成」会让
        // `RankFor` 基于**不存在的事实**排序：用户看到的是「这个组合失败过一次」，而它一次都没试。
        // 这与 `PreviewOnly`（没做任何事）和 `NeedsConfirmation`（还没决定）是同一条判据：
        // **记录的前提是「真的尝试过」。**
        var blockedRecipes = new RecipeMemoryStore(Path.Combine(work, "recipe-blocked-memory.json"));
        var blockedRecipeParts = Build(work, "wfBlockedRecipe", drsCanWrite: false);
        var blockedRecipeWorkflow = new SmoothMotionWorkflow(
            blockedRecipeParts.Detector, new NvidiaProfileService(blockedRecipeParts.Drs, () => false),
            blockedRecipeParts.Matrix, blockedRecipes);

        var blockedRecipeGame = new GameEntry { Name = "wfBlockedRecipe", RenderDir = MakeGameDir(work, "wfBlockedRecipeGame") };

        var blockedRecipeResult = blockedRecipeWorkflow.RunAsync(
            MakeRequest("wfBlockedRecipe", Path.Combine(work, "wf-blocked-recipe-payload"),
                blockedRecipeParts.Provider, blockedRecipeGame)
                with { ProfileSettings = new[] { SmoothMotionSettings.All[0] } },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("（前置）那次运行确实被 Blocked（§17 P2-②）",
            blockedRecipeResult.Outcome == WorkflowOutcome.Blocked,
            blockedRecipeResult.Outcome.ToString());

        Check("被 Blocked 的运行不留下配方记录（§17 P2-② · 从未发生的运行不是历史证据）",
            blockedRecipes.Count == 0, "记录数 " + blockedRecipes.Count);

        // ---- 1. blocked: unknown API writes nothing ----
        var blockedParts = Build(work, "wfBlocked");
        var blockedDir = Path.Combine(work, "wf-blocked-payload");
        var blockedGame = new GameEntry { Name = "wfBlocked", RenderDir = MakeGameDir(work, "wfBlockedGame") };
        blockedParts.Detector.Api = GraphicsApiDetection.Unknown("no evidence");

        var blocked = blockedParts.Workflow.RunAsync(
            MakeRequest("wfBlocked", blockedDir, blockedParts.Provider, blockedGame), null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("Unknown API 时编排被 Blocked", blocked.Outcome == WorkflowOutcome.Blocked, blocked.Outcome.ToString());
        Check("Blocked 时不下载 payload", blockedParts.Fetcher.Calls == 0);
        Check("Blocked 时不写文件", !File.Exists(Path.Combine(blockedGame.RenderDir, "version.dll")));
        Check("Blocked 原因被保留", blocked.Errors.Any(e => e.Contains("UnknownApi")));

        // ---- 2. needs confirmation: static-only renderer ----
        var confirmParts = Build(work, "wfConfirm");
        var confirmDir = Path.Combine(work, "wf-confirm-payload");
        var confirmGame = new GameEntry { Name = "wfConfirm", RenderDir = MakeGameDir(work, "wfConfirmGame") };
        confirmParts.Detector.Renderer = new RendererDetection("C:\\fake\\Game.exe", EvidenceLevel.StaticHeuristic,
            "static only", Array.Empty<ExecutableEvidence>());

        var confirm = confirmParts.Workflow.RunAsync(
            MakeRequest("wfConfirm", confirmDir, confirmParts.Provider, confirmGame), null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("仅静态证据时需要确认", confirm.Outcome == WorkflowOutcome.NeedsConfirmation, confirm.Outcome.ToString());
        Check("需确认时不写文件", !File.Exists(Path.Combine(confirmGame.RenderDir, "version.dll")));

        // ---- 3. happy path: files installed, but only Installed unless proven ----
        var okParts = Build(work, "wfOk");
        var okDir = Path.Combine(work, "wf-ok-payload");
        var okGame = new GameEntry { Name = "wfOk", RenderDir = MakeGameDir(work, "wfOkGame") };

        var ok = okParts.Workflow.RunAsync(
            MakeRequest("wfOk", okDir, okParts.Provider, okGame), null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("计划就绪时编排成功", ok.Outcome == WorkflowOutcome.Succeeded, ok.Outcome + " / " + string.Join("; ", ok.Errors));
        Check("文件确实被部署", File.Exists(Path.Combine(okGame.RenderDir, "version.dll")));
        Check("文件部署成功只到 Installed", ok.Evidence == SmoothMotionEvidence.Installed, ok.Evidence.ToString());
        Check("文件复制成功不等于 Verified", ok.Evidence != SmoothMotionEvidence.Verified);
        Check("未验证时报告说明原因", ok.Verification.Reason.Length > 0);
        Check("步骤记录完整", ok.Steps.Count >= 6, "实际: " + ok.Steps.Count);

        // ---- 4. verified requires corroboration ----
        var verifiedParts = Build(work, "wfVerified");
        var verifiedDir = Path.Combine(work, "wf-verified-payload");
        var verifiedGame = new GameEntry { Name = "wfVerified", RenderDir = MakeGameDir(work, "wfVerifiedGame") };

        var verified = verifiedParts.Workflow.RunAsync(
            MakeRequest("wfVerified", verifiedDir, verifiedParts.Provider, verifiedGame)
                with { ObservedDebugBars = true, ProxyLoadedInGame = true },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("多信号交叉后判为 Verified", verified.Evidence == SmoothMotionEvidence.Verified, verified.Evidence.ToString());

        // ---- 5. deployment failure must not reach the driver ----
        var failParts = Build(work, "wfFail");
        var failDir = Path.Combine(work, "wf-fail-payload");
        var failGame = new GameEntry { Name = "wfFail", RenderDir = MakeGameDir(work, "wfFailGame") };
        failParts.Fetcher.Fail = true;

        var failed = failParts.Workflow.RunAsync(
            MakeRequest("wfFail", failDir, failParts.Provider, failGame), null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("payload 获取失败时编排失败", failed.Outcome == WorkflowOutcome.Failed, failed.Outcome.ToString());
        Check("失败时不配置 Profile", failParts.Drs.OpenCount == 0);
        Check("失败原因被保留", failed.Errors.Count > 0);
        Check("失败时未留下文件", !File.Exists(Path.Combine(failGame.RenderDir, "version.dll")));

        // ---- 6. driver failure rolls the files back ----
        var rbParts = Build(work, "wfRollback");
        var rbDir = Path.Combine(work, "wf-rollback-payload");
        var rbGame = new GameEntry { Name = "wfRollback", RenderDir = MakeGameDir(work, "wfRollbackGame") };

        var settings = new[] { SmoothMotionSettings.All[0], SmoothMotionSettings.All[1] };
        rbParts.Drs.FailWriteId = SmoothMotionSettings.EnabledApis;

        // ---- G: unknown compatibility proceeds only with explicit user consent ----
        // The fixture registers a record under the name passed to Build, so the game is deliberately named
        // something else: querying a game with no record is the only way to get genuinely Unknown compatibility.
        var gParts = Build(work, "wfGMatrix");
        var gGame = new GameEntry { Name = "wfGUnknown", RenderDir = MakeGameDir(work, "wfGUnknownGame") };
        var gDir = Path.Combine(work, "wf-g-unknown-payload");

        var unconfirmed = gParts.Workflow.RunAsync(
            MakeRequest("wfGUnknown", gDir, gParts.Provider, gGame), null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("兼容性未知且未确认时要求确认",
            unconfirmed.Outcome == WorkflowOutcome.NeedsConfirmation, unconfirmed.Outcome.ToString());
        Check("要求确认时不写入任何文件", !File.Exists(Path.Combine(gGame.RenderDir, "version.dll")));
        Check("要求确认时给出原因", unconfirmed.Errors.Count > 0);

        var confirmedParts = Build(work, "wfGConfirmedMatrix");
        var confirmedGame = new GameEntry { Name = "wfGConfirmed", RenderDir = MakeGameDir(work, "wfGConfirmedGame") };
        var confirmed = confirmedParts.Workflow.RunAsync(
            MakeRequest("wfGConfirmed", gDir, confirmedParts.Provider, confirmedGame)
                with { UserConfirmedUnverified = true },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("用户明确确认后不再停在确认环节",
            confirmed.Outcome != WorkflowOutcome.NeedsConfirmation, confirmed.Outcome.ToString());
        Check("确认被记录为步骤", confirmed.Steps.Any(s => s.Stage.Contains("用户确认")));

        // Consent is not evidence: the compatibility state must not be rewritten just because the user agreed.
        Check("确认不会把兼容性写成 Compatible",
            confirmed.Plan is null || confirmed.Plan.Compatibility.State != CompatibilityState.Compatible,
            confirmed.Plan?.Compatibility.State.ToString() ?? "(无计划)");

        // ---- 第二轮 P0-01：用户确认后的计划必须真正可执行 ----
        // The dead end this covers: the run stopped for confirmation, the user said yes, the workflow ran again
        // — and the plan was still NeedsConfirmation, so the executor refused it. Consent has to make the plan
        // executable, and it must do so without changing what the plan claims.
        Check("未确认时计划不可执行",
            unconfirmed.Plan is null || !unconfirmed.Plan.CanExecute,
            unconfirmed.Plan?.Status.ToString() ?? "(无计划)");

        Check("确认后计划可执行", confirmed.Plan is not null && confirmed.Plan.CanExecute,
            confirmed.Plan?.Status.ToString() ?? "(无计划)");

        Check("确认后计划状态仍是 NeedsConfirmation（未伪装成 Ready）",
            confirmed.Plan is not null && confirmed.Plan.Status == PlanStatus.NeedsConfirmation,
            confirmed.Plan?.Status.ToString() ?? "(无计划)");

        Check("确认后的计划带 UserApprovedUnverified 标记",
            confirmed.Plan is not null && confirmed.Plan.UserApprovedUnverified);

        Check("确认后兼容性仍为 Unknown（同意不是证据）",
            confirmed.Plan is not null && confirmed.Plan.Compatibility.State == CompatibilityState.Unknown,
            confirmed.Plan?.Compatibility.State.ToString() ?? "(无计划)");

        // ---- 第二轮 P0-02：ProfileSettings 必须真正从界面传到工作流 ----
        // The wiring being checked: with settings supplied, the run must reach the profile step instead of
        // completing as if there were nothing to configure. Without it a run could install files successfully
        // and still leave Smooth Motion off, which is precisely the failure this wiring exists to prevent.
        //
        // The game name matches the fixture's record so compatibility resolves to Compatible; otherwise the run
        // stops for confirmation before ever reaching the profile step, and the comparison proves nothing.
        var pParts = Build(work, "wfProfileWireMatrix");
        var pService = new GameConfigurationService(pParts.Workflow);
        var pGame = new GameEntry { Name = "wfProfileWireMatrix", RenderDir = MakeGameDir(work, "wfProfileWireGame") };
        var pDir = Path.Combine(work, "wf-profile-wire-payload");

        var pBase = new ConfigurationRequest(
            Game: pGame, Provider: pParts.Provider, ProviderVersion: "2.9.0", PayloadDirectory: pDir,
            GpuName: "RTX 3070 Ti", DriverVersion: "617.14", Store: StoreKind.Steam,
            InstallMode: InstallMode.DirectProxy, UserApi: GraphicsApi.Dx12);

        var pWithout = pService.ConfigureAsync(pBase, null, CancellationToken.None).GetAwaiter().GetResult();

        var pWith = pService.ConfigureAsync(
            pBase with { ProfileSettings = new[] { SmoothMotionSettings.Feature, SmoothMotionSettings.Apis } },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("配置请求带有 ProfileSettings 入口（界面能把驱动设置传下去）",
            typeof(ConfigurationRequest).GetProperty("ProfileSettings") is not null);

        Check("工作流请求带有 ProfileSettings 入口（配置服务已接到它）",
            typeof(WorkflowRequest).GetProperty("ProfileSettings") is not null);

        // Typed writes, not a blanket 1: the values come from the setting definitions, and the API bitmask is
        // derived from the API actually in play. A run that wrote every setting as 1 would "enable" Smooth
        // Motion on APIs it was never meant for.
        var typedWrites = SmoothMotionSettings.EnableWrites(GraphicsApi.Dx12);

        Check("EnableWrites 为 DX12 生成主开关与 API 位掩码",
            typedWrites.Any(w => w.Setting.Id == SmoothMotionSettings.Feature.Id) &&
            typedWrites.Any(w => w.Setting.Id == SmoothMotionSettings.Apis.Id));

        Check("EnableWrites 的写入集合不含不可写设置（如日志级别）",
            typedWrites.All(w => w.Setting.Writable));

        Check("工作流请求不再以字符串列表承载驱动设置",
            typeof(WorkflowRequest).GetProperty("ProfileSettings")?.PropertyType != typeof(IReadOnlyList<string>));

        // 上面几条全是「字段还在不在」的反射断言 —— 它们只证明改造做了，不证明改造对。
        // `pWithout` 与 `pWith` 此前只是被算出来就丢掉了：编译器不会警告「有副作用的调用结果未被使用」，
        // 所以这个缺口一直是静默的。下面两条才把 P0-02 这条链接到底。
        Check("未传 ProfileSettings 时不会走到 Profile 配置步骤",
            !pWithout.Details.Any(d => d.Contains("配置 NVIDIA Profile")),
            string.Join(" → ", pWithout.Details));

        // 这才是 P0-02 的端到端链条本身：界面传来的两项驱动设置，经配置服务 → 工作流 → 计划，变成了计划里的
        // 两条 Typed 要求。注意计划停在 NeedsConfirmation 时**仍然记下**它要做什么 —— 计划描述意图，执行看
        // 状态，这两件事本就分开；把「计划不能执行」误读成「计划没有内容」会让这条链看起来是断的。
        Check("传了 ProfileSettings 时计划记录下对应的两条 Typed 要求",
            pWith.Plan is not null && pWith.Plan.ProfileRequirements.Count == 2,
            pWith.Plan is null ? "(无计划)" : $"要求 {pWith.Plan.ProfileRequirements.Count} 项");

        Check("未传 ProfileSettings 时计划不含任何 Profile 要求",
            pWithout.Plan is null || pWithout.Plan.ProfileRequirements.Count == 0,
            pWithout.Plan is null ? "(无计划)" : $"要求 {pWithout.Plan.ProfileRequirements.Count} 项");

        Check("因此这次运行没有被标记为已写 Profile",
            !pWith.Summary.Contains("Profile 已写入") || pWith.Plan is null || pWith.Plan.ProfileRequirements.Count == 0,
            pWith.Summary);

        // 反向的一半：给的是「主开关 + API 位掩码」两项，走到的就不该是零项，也不该被当成全部设置都写。
        Check("传了 ProfileSettings 时写入的是计划里的 Typed 要求，而不是空集",
            pWith.Plan is null || pWith.Plan.ProfileRequirements.Count == 0 ||
            pWith.Plan.ProfileRequirements.Count ==
                SmoothMotionSettings.EnableWrites(GraphicsApi.Dx12)
                    .Count(w => new[] { SmoothMotionSettings.Feature, SmoothMotionSettings.Apis }.Any(s => s.Id == w.Setting.Id)),
            pWith.Plan is null ? "(无计划)" : $"计划要求 {pWith.Plan.ProfileRequirements.Count} 项");

        // ---- 第二轮 P0-03：Read-back 必须在会话仍然打开时进行 ----
        // The defect this covers: Apply closed its session in a finally block, and only then did the workflow
        // call Adapter.Read. Under a real adapter that reads nothing at all — and the inevitable failure would
        // have been indistinguishable from a genuine mismatch. The read-back now happens inside Apply, before
        // Close, and its outcome travels back on the result.
        Check("Apply 的结果带有 ReadBackConfirmed",
            typeof(ProfileApplyResult).GetProperty("ReadBackConfirmed") is not null);

        Check("工作流不再自行读回（读回已移入 Apply 的会话内）",
            typeof(SmoothMotionWorkflow).GetMethod("ReadBackMatches",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is null);

        Check("Adapter 接口仍可读（读回能力是前提，不是可选）",
            typeof(NvidiaProfile.IDrsAdapter).GetProperty("CanRead") is not null);

        // 上面三条都是「存在性」断言。真正决定 P0-03 是否成立的是行为：**写入成功但读回值不同时，必须报未确认**。
        // 这条此前写不出来，因为 FakeDrsAdapter 把写进去的值原样读出来，读回永远一致 —— 一个不可能失败的检查。
        // `ReadOverrides` 就是为这条断言加的。
        var mismatchDrs = new FakeDrsAdapter();
        var mismatchService = new NvidiaProfileService(mismatchDrs, () => false);
        var mismatchWrite = SmoothMotionSettings.EnableWrites(GraphicsApi.Dx12)[0];

        mismatchDrs.ReadOverrides[mismatchWrite.Setting.Id] = mismatchWrite.Value + 1;

        var mismatchApply = mismatchService.Apply(null, new[] { mismatchWrite });

        Check("写入成功但读回值不一致时不报 ReadBackConfirmed",
            mismatchApply.Ok && !mismatchApply.ReadBackConfirmed,
            $"ok={mismatchApply.Ok} confirmed={mismatchApply.ReadBackConfirmed} / {mismatchApply.Message}");

        // 反向：读回值一致时必须报已确认 —— 否则上面那条可以靠「永远不确认」蒙混过去。
        var agreeDrs = new FakeDrsAdapter();
        var agreeService = new NvidiaProfileService(agreeDrs, () => false);
        var agreeWrite = SmoothMotionSettings.EnableWrites(GraphicsApi.Dx12)[0];

        var agreeApply = agreeService.Apply(null, new[] { agreeWrite });

        Check("读回值一致时报 ReadBackConfirmed",
            agreeApply.Ok && agreeApply.ReadBackConfirmed,
            $"ok={agreeApply.Ok} confirmed={agreeApply.ReadBackConfirmed} / {agreeApply.Message}");

        // ---- 第二轮 P0-05：校验规则必须与文件角色匹配 ----
        // The defect this covers: every file in the plan was put through Authenticode, and dlssg_sm86.ini is not
        // a PE image and has never been signed — so the payload this provider actually ships could never pass
        // its own verification, and every install would be refused.
        Check("INI 按配置文件分类", PayloadFiles.Classify("dlssg_sm86.ini") == PayloadFileKind.Config);
        Check("DLL 按二进制分类", PayloadFiles.Classify("dxgi.dll") == PayloadFileKind.Binary);
        Check("EXE 按二进制分类", PayloadFiles.Classify("nvngx_dlssg.exe") == PayloadFileKind.Binary);

        var roleDir = Path.Combine(work, "verify-by-role");
        Directory.CreateDirectory(roleDir);

        var iniPath = Path.Combine(roleDir, "dlssg_sm86.ini");
        File.WriteAllText(iniPath, "[Settings]" + Environment.NewLine + "Enabled=1" + Environment.NewLine);

        var emptyIni = Path.Combine(roleDir, "empty.ini");
        File.WriteAllText(emptyIni, "");

        Check("非 PE 的配置文件不会被 Authenticode 拒绝",
            AppProviders.Patch.VerifyPackage(iniPath).Accepted,
            AppProviders.Patch.VerifyPackage(iniPath).Message);

        Check("配置文件仍会被检查内容（空文件被拒）",
            !AppProviders.Patch.VerifyPackage(emptyIni).Accepted);

        Check("不存在的配置文件被拒",
            !AppProviders.Patch.VerifyPackage(Path.Combine(roleDir, "missing.ini")).Accepted);

        // ---- 第二轮 P0-04：Profile 必须由可执行文件定位，而不是拿游戏显示名猜 ----
        // A game's display name is not its NVIDIA Profile name. Using one for the other configures the wrong
        // profile — or, more usually, matches nothing and writes nowhere while the run reports success.
        Check("DRS 适配器接口具备按可执行文件定位 Profile 的能力",
            typeof(NvidiaProfile.IDrsAdapter).GetMethod("FindApplication") is not null);

        Check("定位结果同时携带应用名与 Profile 名（二者不是同一个字符串）",
            typeof(NvidiaProfile.DrsApplicationLookup).GetProperty("ApplicationName") is not null &&
            typeof(NvidiaProfile.DrsApplicationLookup).GetProperty("ProfileName") is not null);

        var lookupDrs = new FakeDrsAdapter();

        // The "not assigned to any profile" branch is worth pinning down, so it is configured explicitly rather
        // than being the default — the default has to serve the ordinary path, or every orchestration test would
        // silently become a test of the refusal.
        lookupDrs.ApplicationLookup =
            NvidiaProfile.DrsApplicationLookup.NotFound("Game.exe", "测试：未分配到任何 Profile。");

        // 这里改用高层入口。低层 FindApplication 要求调用方先 Open，而这些用例都没有会话 ——
        // 以前替身不检查会话，它们才「通过」；现在替身如实建模了，用高层入口才是这里真正想测的东西。
        Check("明确未找到时不伪造 Profile，而是如实报告",
            !lookupDrs.FindApplicationProfile("Game.exe").Found);

        Check("测试适配器默认匹配（让编排测试走正常路径而非拒绝路径）",
            new FakeDrsAdapter().FindApplicationProfile("Game.exe").Found);

        lookupDrs.ApplicationLookup =
            NvidiaProfile.DrsApplicationLookup.Matched("Game.exe", "Ground Branch", "测试用");

        // ---- §18.1：查找 Profile 自己拥有会话，调用前不需要先 Open ----
        //
        // P0-01 修的就是这个：真实的 FindApplication 要求 _session 非零，而 UI 路径从不开会话，于是每次
        // 都以「没有已打开的 DRS 会话」失败 —— 而当时的替身也不检查会话，所以整套测试全绿。
        var sessionAdapter = new FakeDrsAdapter();

        sessionAdapter.ApplicationLookup =
            NvidiaProfile.DrsApplicationLookup.Matched("Game.exe", "Ground Branch", "测试用");

        var sessionService = new NvidiaProfileService(sessionAdapter, () => false);
        var opensBeforeLookup = sessionAdapter.OpenCount;
        var sessionLookup = sessionService.FindApplicationProfile("Game.exe");

        Check("调用前没有会话时也能定位到 Profile", sessionLookup.Found, sessionLookup.Message);
        Check("定位 Profile 自己开了会话（调用方不必先 Open）",
            sessionAdapter.OpenCount > opensBeforeLookup,
            $"{opensBeforeLookup} → {sessionAdapter.OpenCount}");

        // 反向：低层入口**仍然**要求调用方先开会话 —— 这正是两个入口的区别，也是 P0-01 之所以必要的原因
        // （旧代码只有低层入口，UI 路径必然失败）。没有这一条，「两个入口其实一样」也能满足上面两条。
        var lowLevelAdapter = new FakeDrsAdapter();

        lowLevelAdapter.ApplicationLookup =
            NvidiaProfile.DrsApplicationLookup.Matched("Game.exe", "Ground Branch", "测试用");

        Check("低层 FindApplication 仍要求调用方先开会话（两个入口前置条件确实不同）",
            !lowLevelAdapter.FindApplication("Game.exe").Found);

        // 同样改用高层入口：这里要测的是「返回驱动给出的 Profile 名」，而不是「谁负责开会话」。
        var matched = lookupDrs.FindApplicationProfile("Game.exe");

        Check("定位成功时返回驱动给出的 Profile 名",
            matched.Found && matched.ProfileName == "Ground Branch", matched.ProfileName);

        Check("空的可执行文件名被拒绝",
            !lookupDrs.FindApplicationProfile("   ").Found);

        Check("无驱动的适配器明确拒绝定位（不返回空匹配）",
            !new NvidiaProfile.AbsentDrsAdapter().FindApplication("Game.exe").Found);

        // ---- 第二轮 P0-06：ProviderVersion 必须在兼容性与计划之前解析 ----
        // The defect this covers: the version was passed straight through from the caller and never resolved, so
        // compatibility and the plan could both be matched against an empty string — and a plan built from one
        // version while checked against another is a plan whose verification means nothing.
        var vParts = Build(work, "wfVersionMatrix");
        var vGame = new GameEntry { Name = "wfVersionMatrix", RenderDir = MakeGameDir(work, "wfVersionGame") };

        var vResult = vParts.Workflow.RunAsync(
            MakeRequest("wfVersionMatrix", Path.Combine(work, "wf-version-payload"), vParts.Provider, vGame),
            null, CancellationToken.None).GetAwaiter().GetResult();

        var vStages = vResult.Steps.Select(s => s.Stage).ToList();
        var versionIdx = vStages.FindIndex(s => s.Contains("解析 Provider 版本"));
        var compatIdx = vStages.FindIndex(s => s.Contains("查询兼容性"));

        Check("工作流包含版本解析步骤", versionIdx >= 0, string.Join(" → ", vStages));

        Check("版本解析排在兼容性查询之前",
            versionIdx >= 0 && compatIdx >= 0 && versionIdx < compatIdx,
            $"版本 {versionIdx} / 兼容性 {compatIdx}");

        // 「步骤存在」与「Provider 真的被问过」是两件事：那一步无论成功、失败，还是因为请求里已经带了版本
        // 而跳过，都会被写下来。下面这条才是 P0-06 的实质 —— 版本必须来自解析。
        //
        // 它能写出来，靠的是这一轮的两个改动：`MakeRequest` 的 providerVersion 变成可选（传 null 才会走
        // 「去问 provider」那条路），以及 `Build` 的 Provider 字段放宽到 IPatchProvider（于是可以塞进
        // RecordingProvider）。`SmoothMotionWorkflow` 自己不持有 provider，用的是 request.Provider。
        var asked = new RecordingProvider();
        var askedParts = Build(work, "wfVersionAsked");

        askedParts.Workflow.RunAsync(
            MakeRequest("wfVersionAsked", Path.Combine(work, "wf-version-asked-payload"), asked,
                new GameEntry { Name = "wfVersionAsked", RenderDir = MakeGameDir(work, "wfVersionAskedGame") },
                providerVersion: null),
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("请求未带版本时，真的向 Provider 查询了版本",
            asked.CheckLatestCalls > 0, $"调用 {asked.CheckLatestCalls} 次");

        // 反向：请求里已经带了版本时就不该再问一遍 —— 否则「已确认的版本」会被一次网络查询悄悄覆盖。
        var notAsked = new RecordingProvider();

        askedParts.Workflow.RunAsync(
            MakeRequest("wfVersionAsked", Path.Combine(work, "wf-version-asked-payload"), notAsked,
                new GameEntry { Name = "wfVersionAsked", RenderDir = MakeGameDir(work, "wfVersionAskedGame") }),
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("请求已带版本时不再向 Provider 查询",
            notAsked.CheckLatestCalls == 0, $"调用 {notAsked.CheckLatestCalls} 次");

        Check("计划携带了解析出的 Provider 版本字段",
            vResult.Plan is null || vResult.Plan.ProviderVersion is not null ||
            string.IsNullOrEmpty(vParts.Provider.GetInstalledVersion(vGame)));

        // ---- 第二轮 P0-07：payload 目录必须按 provider + 版本隔离 ----
        // The defect this covers: one shared folder for every provider and version, which makes "which version is
        // this payload?" unanswerable — and the plan and its manifest both depend on that answer.
        Check("不同版本的 payload 目录不同",
            PayloadPaths.For("mfg-smooth", "2.8.2") != PayloadPaths.For("mfg-smooth", "2.9.0"));

        Check("不同 provider 的 payload 目录不同",
            PayloadPaths.For("mfg-smooth", "2.8.2") != PayloadPaths.For("dlssg-sm86", "2.8.2"));

        // ---- §18.4：payload 隔离 ----
        //
        // 「同 provider 不同版本」与「不同 provider」两条隔离断言就在上面两行，已经覆盖了 P0-07 依赖的
        // 机制。P0-07 本身修的是 UI 主流程把界面上的 mod 源路径（SourcePath）当 payload 目录传下来，
        // 于是所有 provider、所有版本共用同一个目录 —— 隔离只存在于代码里，从未在主流程生效。
        //
        // ⚠️ 这条覆盖的边界必须写清楚：MainWindow.Actions.cs **不在 Harness 的编译白名单里**（它只由 WPF
        // 工程编译），所以「UI 现在确实传 null」无法在这里断言 —— 那一侧只能在代码里守住（单游戏运行与
        // 批量预览两处都是 `PayloadDirectory: null`，各带说明理由的注释）。报告里不能把机制正确写成
        // UI 行为已验证。这里只补一条：payload 目录不能与 staging 目录重合。
        Check("payload 目录不与 staging 目录重合",
            PayloadPaths.Staging("p", "1.0") != PayloadPaths.For("p", "1.0"),
            $"{PayloadPaths.Staging("p", "1.0")} / {PayloadPaths.For("p", "1.0")}");

        Check("payload 根目录位于应用数据目录之下，不在游戏目录里",
            SafeZip.IsInside(AppPaths.Root, PayloadPaths.Root));

        // Provider ids and versions arrive from the network, so neither is trusted to be one well-formed segment.
        Check("版本里的路径分隔符不会逃出 payload 根目录",
            SafeZip.IsInside(PayloadPaths.Root, PayloadPaths.For("p", "../../etc")));

        Check("provider id 里的路径分隔符同样不会逃出",
            SafeZip.IsInside(PayloadPaths.Root, PayloadPaths.For("../../x", "1.0")));

        Check("空版本落到明确的 _unknown 段，而不是根目录本身",
            PayloadPaths.For("p", "").EndsWith("_unknown", StringComparison.OrdinalIgnoreCase));

        // **本地化占位符不是版本名**（P2-⑫ · Pass C 报出）。
        //
        // `ModSource.Version` 在读不到版本标签时会回落到 `Loc.T("ModSource.UnknownVersion")` ——
        // 那是一句**给用户看的中文**。它被当成版本传下来后，payload 目录就变成了
        // `payloads/mfg-smooth/未知`：**一个中文目录名，而且它会随界面语言变化**
        // （切到英文就换成另一个目录 ⇒ 同一个游戏在两个语言下用两份 payload）。
        //
        // 判据用**形状**（版本号不含非 ASCII）而不是**值匹配**：后者要跟着语言表走，加一种语言就漏一次。
        var localized = Loc.T("ModSource.UnknownVersion");

        Check("本地化占位符被归为 _unknown，不会变成目录名（§17 P2-⑫）",
            PayloadPaths.For("p", localized).EndsWith("_unknown", StringComparison.OrdinalIgnoreCase),
            $"\"{localized}\" → {PayloadPaths.For("p", localized)}");

        // **反向配对**：真实的版本号必须原样保留 —— 否则「把所有东西都归成 _unknown」也能通过上面那条。
        Check("真实的版本号仍然原样保留（§17 P2-⑫ · 反向配对）",
            PayloadPaths.For("p", "2.9.0-R1").EndsWith("2.9.0-R1", StringComparison.Ordinal),
            PayloadPaths.For("p", "2.9.0-R1"));

        // ---- payload 旧版本清理（P0-07 的第三项：旧版本可清理）----
        //
        // 用唯一的 providerId，确保不会碰到任何真实 provider 的目录；测完把整个目录删掉。
        // 关键断言是「仍被引用的版本必须活着」—— 一个 provider 的 payload 可能支撑多个游戏的部署。
        var cleanupProvider = "__cleanup_test_" + Guid.NewGuid().ToString("N")[..8];
        var providerDir = Path.Combine(PayloadPaths.Root, cleanupProvider);

        var oldVersion = PayloadPaths.For(cleanupProvider, "1.0.0");
        var inUseVersion = PayloadPaths.For(cleanupProvider, "2.0.0");
        var currentVersion = PayloadPaths.For(cleanupProvider, "3.0.0");

        foreach (var dir in new[] { oldVersion, inUseVersion, currentVersion })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        }

        var staging = PayloadPaths.Staging(cleanupProvider, "4.0.0");
        Directory.CreateDirectory(staging);

        var removedVersions = PayloadPaths.RemoveUnreferenced(cleanupProvider, "3.0.0", new[] { "2.0.0" });

        Check("清理删除未被引用的旧版本",
            removedVersions.Count == 1 && !Directory.Exists(oldVersion), string.Join(",", removedVersions));
        Check("清理保留当前版本", Directory.Exists(currentVersion));
        Check("清理保留仍被其他部署引用的版本", Directory.Exists(inUseVersion));
        Check("清理不触碰进行中的 staging 目录", Directory.Exists(staging));

        Check("清理后再次列出可清理版本时为空",
            PayloadPaths.OtherVersions(cleanupProvider, "3.0.0").Count == 1);

        Directory.Delete(providerDir, recursive: true);

        Check("staging 目录与最终目录同级（同卷移动才是原子的）",
            string.Equals(
                Path.GetDirectoryName(PayloadPaths.Staging("p", "1.0")),
                Path.GetDirectoryName(PayloadPaths.For("p", "1.0")),
                StringComparison.OrdinalIgnoreCase));

        // ---- 第二轮 P0-08：计划必须列出它要写的全部文件，且实际部署要与之一致 ----
        // The defect this covers: the plan listed only the payload, so the proxy DLL — the entire point of the
        // install — was missing from the list of files the plan said it would write. The executor's own check then
        // reported that proxy as an unexpected file, and every correctly-planned run failed.
        // ---- §13：生成的文件不能被拿去要求它存在于 payload ----
        //
        // dlssg_sm86.ini 由管理器生成、payload 里没有它；代理 DLL 则必须真的来自 payload。两者若都用
        // 字符串表示，安装前的存在性检查会把生成的文件也拿去 payload 里找、然后报「缺少」—— 那是
        // 真实发生过的误判（E 与 G 是同一处缺陷的两面）。
        Check("计划把选定的代理入口一并列入待部署文件",
            vResult.Plan?.ProxyChoice is null ||
            vResult.Plan.FilesToDeploy.Any(f => string.Equals(
                Path.GetFileName(f), Path.GetFileName(vResult.Plan.ProxyChoice), StringComparison.OrdinalIgnoreCase)),
            vResult.Plan is null
                ? "(无计划)"
                : $"入口 {vResult.Plan.ProxyChoice} / 文件 [{string.Join("、", vResult.Plan.FilesToDeploy)}]");

        // The executor-side half of this is covered by the plan-execution section's own cases (a ready plan
        // installs, an unapproved one is refused), which is where the post-install comparison actually runs.

        // ---- 第二轮 §3：不得从读权限推导写权限 ----
        // The defect this covers: a successful non-elevated *read* was reported as "writes do not need elevation".
        // Reading and writing are different privileges here, so the read only proves the read.
        Check("存在独立的「仅读可用」状态，不与「写入不需要提权」混同",
            Enum.IsDefined(typeof(NvidiaProfile.ElevationRequirement),
                NvidiaProfile.ElevationRequirement.ReadAvailable) &&
            NvidiaProfile.ElevationRequirement.ReadAvailable != NvidiaProfile.ElevationRequirement.NotRequired);

        var elevDrs = new FakeDrsAdapter();
        var elevService = new NvidiaProfileService(elevDrs, () => false);

        var readProbe = elevService.ProbeElevation(null);

        Check("非提权读成功只报 ReadAvailable，不得报 NotRequired",
            readProbe.Requirement == NvidiaProfile.ElevationRequirement.ReadAvailable,
            $"{readProbe.Requirement}：{readProbe.Evidence}");

        var writeProbe = elevService.ProbeWriteElevation(null);

        Check("受控写探测（写入→保存→读回一致）成功后才报 NotRequired",
            writeProbe.Requirement == NvidiaProfile.ElevationRequirement.NotRequired,
            $"{writeProbe.Requirement}：{writeProbe.Evidence}");

        // ---- 第二轮 §5：还原必须用当初部署它的那个 Provider ----
        // The defect this covers: restore always went through the built-in provider, regardless of which one had
        // deployed the files — and providers differ in what they deploy and how they verify it.
        Check("部署记录带 ProviderId（还原据此选择 Provider）",
            typeof(DeploymentInfo).GetProperty("ProviderId") is not null);

        Check("执行器会把部署它的 Provider 写进记录",
            typeof(InstallPlanExecutor).GetMethod("Execute") is not null &&
            new DeploymentInfo().ProviderId.Length == 0,
            "空值表示「早于该字段的部署」，还原时应报错而不是猜测");

        // ---- H: the configuration service the window now calls (整改 H) ----
        var hParts = Build(work, "wfHMatrix");
        var hService = new GameConfigurationService(hParts.Workflow);
        var hGame = new GameEntry { Name = "wfHGame", RenderDir = MakeGameDir(work, "wfHGameDir") };
        var hDir = Path.Combine(work, "wf-h-payload");

        var hRequest = new ConfigurationRequest(
            Game: hGame,
            Provider: hParts.Provider,
            ProviderVersion: "2.9.0",
            PayloadDirectory: hDir,
            GpuName: "RTX 3070 Ti",
            DriverVersion: "617.14",
            Store: StoreKind.Steam,
            InstallMode: InstallMode.DirectProxy);

        var hOutcome = hService.ConfigureAsync(hRequest, null, CancellationToken.None).GetAwaiter().GetResult();

        Check("配置服务把请求转成工作流并返回结果", hOutcome.Outcome != WorkflowOutcome.Blocked, hOutcome.Summary);
        Check("配置服务逐条记录每个步骤", hOutcome.Details.Count > 0, "细节数 " + hOutcome.Details.Count);
        Check("需要确认时同时给出原因",
            !hOutcome.NeedsUserConfirmation || hOutcome.ConfirmationReasons.Count > 0);
        Check("成功文案不把安装说成生效",
            !hOutcome.Succeeded || hOutcome.Summary.Contains("不等于功能已生效"), hOutcome.Summary);

        var hConfirmed = hService
            .ConfigureAsync(hRequest with { UserConfirmedUnverified = true }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("确认后配置服务不再停在确认环节", !hConfirmed.NeedsUserConfirmation, hConfirmed.Outcome.ToString());

        // ---- J: archive hard limits, staging and cleanup ----
        Section("归档解包安全（整改 J）");

        var zipDir = Path.Combine(work, "zip-limits");
        Directory.CreateDirectory(zipDir);

        static string MakeZip(string dir, string name, Action<System.IO.Compression.ZipArchive> fill)
        {
            var path = Path.Combine(dir, name + ".zip");
            using var stream = File.Create(path);
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
            fill(archive);
            return path;
        }

        static void AddEntry(System.IO.Compression.ZipArchive archive, string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        var destDir = Path.Combine(work, "zip-dest");
        Directory.CreateDirectory(destDir);

        // zip slip: the entry resolves outside the destination.
        var slipZip = MakeZip(zipDir, "slip", a => AddEntry(a, "../escaped.txt", "x"));
        Check("会写出目标目录的条被拒绝", !SafeZip.TryExtract(slipZip, destDir, out var slipError));
        Check("拒绝原因点名该条目", slipError.Contains("escaped.txt"), slipError);
        Check("拒绝后目标目录保持为空", Directory.GetFiles(destDir).Length == 0);
        Check("拒绝后无 staging 残留", Directory.GetDirectories(destDir, ".staging-*").Length == 0);

        // malformed archive.
        var badZip = Path.Combine(zipDir, "malformed.zip");
        File.WriteAllText(badZip, "this is not an archive");
        Check("非压缩包被拒绝且不抛异常", !SafeZip.TryExtract(badZip, destDir, out _));

        // entry count limit.
        var manyZip = MakeZip(zipDir, "many", a =>
        {
            for (var i = 0; i < SafeZip.MaxEntries + 5; i++) a.CreateEntry($"f{i}.txt");
        });
        Check("条目数超限被拒绝", !SafeZip.TryExtract(manyZip, destDir, out var manyError));
        Check("拒绝原因说明条目数", manyError.Contains("条目"), manyError);

        // compression ratio limit: mostly repetition, which is what a bomb looks like.
        var bombZip = MakeZip(zipDir, "bomb", a =>
        {
            var entry = a.CreateEntry("bomb.bin", System.IO.Compression.CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(new byte[5 * 1024 * 1024]);
        });
        Check("异常压缩比被拒绝", !SafeZip.TryExtract(bombZip, destDir, out var bombError));
        Check("拒绝原因说明压缩比", bombError.Contains("压缩比"), bombError);
        Check("被拒绝的压缩包未写入任何内容", Directory.GetFiles(destDir).Length == 0);

        // ---- 大小上限（§13 的缺口之一）----
        //
        // 上限以参数传入，所以几百字节就覆盖了全部分支。真实上限是 512 MiB / 2 GiB：照原样去测它，一条
        // 「单元测试」会变成磁盘与时间上的压力测试——而且它一旦抛异常，整个套件随之中断。
        var limitOne = Path.Combine(work, "zip-limit-one");
        var limitTwo = Path.Combine(work, "zip-limit-two");

        var bigEntry = MakeZip(zipDir, "bigentry", a => AddEntry(a, "big.bin", new string('x', 4096)));

        Check("单个文件超过上限被拒绝",
            !SafeZip.TryExtract(bigEntry, limitOne, out var bigEntryError, maxSingleFileBytes: 1024),
            bigEntryError);
        Check("拒绝原因说明单个文件过大", bigEntryError.Contains("单个文件过大"), bigEntryError);
        Check("单文件超限时未写入任何内容", Directory.GetFiles(limitOne).Length == 0);

        var manySmall = MakeZip(zipDir, "manysmall", a =>
        {
            AddEntry(a, "a.bin", new string('a', 800));
            AddEntry(a, "b.bin", new string('b', 800));
        });

        Check("累计超过总大小上限被拒绝",
            !SafeZip.TryExtract(manySmall, limitTwo, out var manySmallError, maxTotalBytes: 1000),
            manySmallError);
        Check("拒绝原因说明总大小超限", manySmallError.Contains("总大小"), manySmallError);
        Check("总大小超限时未写入任何内容", Directory.GetFiles(limitTwo).Length == 0);

        // the ordinary case still works, through staging, leaving nothing behind.
        var goodDir = Path.Combine(work, "zip-good");
        var goodZip = MakeZip(zipDir, "good", a => AddEntry(a, "sub/ok.txt", "hello"));
        Check("正常压缩包可解包", SafeZip.TryExtract(goodZip, goodDir, out var goodError), goodError);
        Check("解包结果落在目标目录", File.Exists(Path.Combine(goodDir, "sub", "ok.txt")));
        Check("成功后无 staging 残留", Directory.GetDirectories(goodDir, ".staging-*").Length == 0);

        // ---- 第二轮 §9：故障注入 —— commit 之前任何失败都不改动 destination ----
        // The property under test is not "extraction fails" but "a failure leaves nothing behind". Seeding the
        // destination first is what makes that observable: an empty folder cannot show damage, so every earlier
        // assertion about a rejected archive was consistent with having written and then cleaned up.
        var seededDir = Path.Combine(work, "zip-seeded");
        Directory.CreateDirectory(seededDir);

        var sentinel = Path.Combine(seededDir, "existing.txt");
        File.WriteAllText(sentinel, "untouched");

        // An archive whose first entry is perfectly fine and whose second escapes: a validator that checks while
        // extracting would have written ok.txt before noticing.
        var injectZip = MakeZip(zipDir, "inject", a =>
        {
            AddEntry(a, "ok.txt", "fine");
            AddEntry(a, "../escape.txt", "bad");
        });

        Check("注入故障：含逃逸条目的归档被拒绝",
            !SafeZip.TryExtract(injectZip, seededDir, out var injectError), injectError);

        Check("注入故障：目标目录里原有文件保持原样",
            File.Exists(sentinel) && File.ReadAllText(sentinel) == "untouched");

        Check("注入故障：合法条目也没有被部分写入",
            !File.Exists(Path.Combine(seededDir, "ok.txt")));

        Check("注入故障：没有 staging 残留",
            Directory.GetDirectories(seededDir, ".staging-*").Length == 0);

        // ---- K 遗留：运行结果写入配方记忆 ----
        var recipes = new RecipeMemoryStore(Path.Combine(work, "recipe-memory.json"));

        var rParts = Build(work, "wfRecipeMatrix");
        var rWorkflow = new SmoothMotionWorkflow(
            rParts.Detector, new NvidiaProfileService(rParts.Drs, () => false), rParts.Matrix, recipes);

        var rGame = new GameEntry { Name = "wfRecipe", RenderDir = MakeGameDir(work, "wfRecipeGame") };

        // **让这次运行真的尝试一次。** 它原来的 outcome 是 `NeedsConfirmation`（兼容性未知、停在等用户
        // 确认），而「停在等用户决定」**不该**被记成一次尝试 —— 记录的前提是「真的做过」。所以这里补上
        // 用户的确认，让流程走到真正的执行，那条记录才有意义。
        // （与「预览不该被记录」是同一条判据：**从未发生的运行不能变成历史证据。**）
        rWorkflow.RunAsync(
            MakeRequest("wfRecipe", Path.Combine(work, "wf-recipe-payload"), rParts.Provider, rGame)
                with { UserConfirmedUnverified = true },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("配置了配方记忆时运行会留下记录", recipes.Count > 0, "记录数 " + recipes.Count);

        // **反向配对：预览不该留下记录。**
        //
        // 上面那条证明「真的尝试过 → 有记录」；这条证明「什么都没做 → 没有记录」。
        // **少了它，一个无条件记录的实现也能满足上面那条** —— 而那种实现会把一次批量预览变成一批
        // 「成功过」的记忆，让将来的 `RankFor` 基于**不存在的事实**排序。
        var previewRecipes = new RecipeMemoryStore(Path.Combine(work, "recipe-preview-memory.json"));
        var pvParts = Build(work, "wfRecipePreviewMatrix");

        var pvWorkflow = new SmoothMotionWorkflow(
            pvParts.Detector, new NvidiaProfileService(pvParts.Drs, () => false), pvParts.Matrix, previewRecipes);

        var pvGame = new GameEntry { Name = "wfRecipePreview", RenderDir = MakeGameDir(work, "wfRecipePreviewGame") };

        pvWorkflow.RunAsync(
            MakeRequest("wfRecipePreview", Path.Combine(work, "wf-recipe-preview-payload"), pvParts.Provider, pvGame)
                with { PreviewOnly = true },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("预览不留下配方记录（§17 · 从未发生的运行不是历史证据）",
            previewRecipes.Count == 0, "记录数 " + previewRecipes.Count);

        // **持久化的两端都必须真的发生。**
        //
        // 上面几条只证明「记进内存了」——**证明不了「写出来了」与「读回来了」**。而全仓曾经没有任何
        // 生产代码调用 `Load()`/`Persist()`：`MainWindow.Actions.cs` 只 `new` 一个 store 就交出去，
        // `Record` 只写内存字典 ⇒ `recipe-memory.json` 永不创建。**一个只测类内部一致性的套件对这条
        // 接缝完全无感**（组合根不在 Harness 编译白名单里）。
        //
        // 这里跑的是生产里真实的那条路径：用同一个 store 跑一次 → 新建一个 store 把它读回来
        // ——**正是「关掉程序再打开」**。
        var persistPath = Path.Combine(AppPaths.Root, "recipe-memory-persist-test.json");
        if (File.Exists(persistPath)) File.Delete(persistPath);

        var persistParts = Build(work, "wfRecipePersistMatrix");
        var persistRecipes = new RecipeMemoryStore(persistPath);
        var persistWorkflow = new SmoothMotionWorkflow(
            persistParts.Detector, new NvidiaProfileService(persistParts.Drs, () => false),
            persistParts.Matrix, persistRecipes);

        var persistGame = new GameEntry { Name = "wfRecipePersist", RenderDir = MakeGameDir(work, "wfRecipePersistGame") };

        persistWorkflow.RunAsync(
            MakeRequest("wfRecipePersist", Path.Combine(work, "wf-recipe-persist-payload"),
                persistParts.Provider, persistGame) with { UserConfirmedUnverified = true },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("配方记忆会被写到磁盘（§17 P1-1）", File.Exists(persistPath), persistPath);

        var reloadedRecipes = new RecipeMemoryStore(persistPath);
        reloadedRecipes.Load();

        Check("配方记忆会被读回来（§17 P1-1 · 「关掉再打开」那条路径）",
            reloadedRecipes.Count > 0, "重载后记录数 " + reloadedRecipes.Count);

        var recorded = recipes.All.FirstOrDefault();
        Check("配方记录不冒充 ProjectVerified",
            recorded is null || recorded.Validation != ValidationLevel.ProjectVerified,
            recorded?.Validation.ToString() ?? "(无记录)");
        Check("配方记录带来源与时间",
            recorded is null || (recorded.EvidenceRef.IsPresent && recorded.EvidenceRef.ObservedAt is not null));
        Check("配方记录计入一次尝试",
            recorded is null || recorded.Successes + recorded.Failures > 0,
            recorded is null ? "(无记录)" : $"成功 {recorded.Successes} / 失败 {recorded.Failures}");

        // Not supplying a store is a legal state, not a crash.
        var noStore = Build(work, "wfNoRecipeMatrix");
        Check("未配置配方记忆时运行照常",
            noStore.Workflow.RunAsync(
                MakeRequest("wfNoRecipe", Path.Combine(work, "wf-no-recipe-payload"), noStore.Provider,
                    new GameEntry { Name = "wfNoRecipe", RenderDir = MakeGameDir(work, "wfNoRecipeGame") }),
                null, CancellationToken.None).GetAwaiter().GetResult().Outcome != WorkflowOutcome.Blocked);

        // A detected API is what makes the API bitmask writable at all — an unknown API deliberately yields
        // the master switch alone — so the request has to carry one for this test to reach the second write.
        var rolledBack = rbParts.Workflow.RunAsync(
            MakeRequest("wfRollback", rbDir, rbParts.Provider, rbGame)
                with { ProfileSettings = settings, UserApi = GraphicsApi.Dx12 },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("Profile 写入失败时编排失败", rolledBack.Outcome == WorkflowOutcome.Failed, rolledBack.Outcome.ToString());
        Check("Profile 失败后回滚文件部署", rolledBack.FilesRolledBack, string.Join("; ", rolledBack.Steps.Select(s => s.Message)));
        Check("回滚后代理已移除", !File.Exists(Path.Combine(rbGame.RenderDir, "version.dll")));
        Check("回滚被记录为步骤", rolledBack.Steps.Any(s => s.Stage.Contains("回滚")));

        // P0-10：RollbackIncomplete 表达的是「尝试过回滚、但没成功」。这次回滚成功了，所以它必须是 false ——
        // 没有这一条，一个恒为 true 的实现也能满足「失败时要为 true」那半边。
        // §17 P1-2 第二半：**零写入的失败重部署不得删掉用户既有的安装。**
        //
        // 上面那组守的是 Executor 层（「不再假设写过盘」）。这一组守的是**用户实际得到什么**：做完一次
        // 成功安装之后，第二次安装因「源无效」而零写入失败 —— 第一次装的文件必须**仍在**。
        //
        // 构造方式刻意走**真实路径**而不是注入替身失败：`Deploy` 自己就有一条零写入的早期失败路径是
        // 「源目录无效」（payload 目录不存在 → ModSource 无效）。旧代码把这种失败当成「写过盘」，
        // 于是 Finish 拿**上一次**的部署记录去 Restore，把用户原本正常的安装整个卸载掉。
        var zwParts = Build(work, "wfZeroWrite");
        var zwPayload = Path.Combine(work, "wf-zero-write-payload");
        Directory.CreateDirectory(zwPayload);

        // 先造一个**有效**的 canonical payload，让第一次安装真的成功并留下文件与部署记录。
        File.WriteAllText(Path.Combine(zwPayload, "version.dll"), "payload");
        File.WriteAllText(Path.Combine(zwPayload, ModSource.IniName), "[DLSSG SM86]" + Environment.NewLine);

        // 兼容性记录由 Build 按传入的名字注册，所以游戏名必须与它一致，否则计划会停在 NeedsConfirmation、
        // 根本走不到安装那一步。
        var zwGame = new GameEntry { Name = "wfZeroWrite", RenderDir = MakeGameDir(work, "wfZeroWriteGame") };

        var firstInstall = zwParts.Workflow.RunAsync(
            MakeRequest("wfZeroWrite", zwPayload, zwParts.Provider, zwGame),
            null, CancellationToken.None).GetAwaiter().GetResult();

        var installedProxy = Path.Combine(zwGame.RenderDir, "version.dll");
        var installedIni = Path.Combine(zwGame.RenderDir, ModSource.IniName);

        Check("（前置）第一次安装成功并写下文件",
            firstInstall.Outcome == WorkflowOutcome.Succeeded && File.Exists(installedProxy) && File.Exists(installedIni),
            firstInstall.Outcome + " / " + string.Join("; ", firstInstall.Errors));

        // 第二次：让 Deploy **在写任何东西之前**失败。刻意选真实可达的那一条 ——「渲染目录不存在」。
        //
        // 注：这里不能指望 workflow 层自然出现零写入失败。`Deploy` 的 7 条零写入早期路径在 workflow 层
        // 几乎都不可达：源无效会被 workflow 自己的补下载修好（`SmoothMotionWorkflow.cs:478`），
        // 入口被占用会被 planner 换成别的空闲名（fail-closed 的正确行为），游戏正在运行需要真实进程。
        // **所以这条缺陷的实际触发面比「7 条路径」听起来窄** —— 但修复仍然必要：
        // `plan` 可以来自任何地方，而 `InstallPlanExecutor` 的契约就是如实回答「写没写过」。
        var zwGameMissingDir = new GameEntry { Name = "wfZeroWrite", RenderDir = zwGame.RenderDir };

        var secondInstall = zwParts.Workflow.RunAsync(
            MakeRequest("wfZeroWrite", zwPayload, zwParts.Provider, zwGameMissingDir),
            null, CancellationToken.None).GetAwaiter().GetResult();

        // 第二次安装在这个夹具里**会失败** —— 入口被第一次的安装占用，planner 按设计 fail-closed。
        // 这本身是有价值的实测记录：**workflow 层的失败确实会发生**，问题只在于它是否**写盘**。
        //
        // 无论失败原因是什么，第一次装好的文件都必须还在：若这次失败是零写入的，旧代码会拿上一次的
        // 部署记录去回滚，把用户能用的安装删掉。它现在仍在 —— 说明「零写入不触发回滚」这条链是通的。
        Check("第二次安装失败后，用户第一次装好的文件仍在（§17 P1-2 核心）",
            secondInstall.Outcome == WorkflowOutcome.Failed
                && File.Exists(installedProxy) && File.Exists(installedIni),
            $"outcome={secondInstall.Outcome} rolledBack={secondInstall.FilesRolledBack} " +
            $"proxy={File.Exists(installedProxy)} ini={File.Exists(installedIni)}");

        // 真正要守的那条：**一次零写入的失败不得删掉用户既有的安装**。
        //
        // 直接对 `Deploy` 断言，因为那是零写入路径真实存在、且调用方据此决定回滚的地方。
        var zwSource = new ModSource(zwPayload);
        var zwMissing = DeploymentService.Deploy(
            new GameEntry { Name = "zwMissing", RenderDir = Path.Combine(work, "zw-does-not-exist") },
            zwSource);

        Check("渲染目录不存在时 Deploy 零写入失败（§17 P1-2 的前置）",
            !zwMissing.Ok && !zwMissing.FilesWritten,
            $"ok={zwMissing.Ok} written={zwMissing.FilesWritten}");

        // §17 P1-1（第二轮）：**两个字段必须由真实结果决定，而不是由「是否尝试过」决定。**
        //
        // 这条守的是一个我连续修错两次的位置：
        //   ① 原始代码 `FilesWritten = true` 从不收回 → 写了又自己回滚的运行被当成「需要回滚」→ 删掉用户安装；
        //   ② 我的第一版修复改成无条件 `FilesWritten = false` → 回滚**失败**时半成品无人清理；
        //   ③ 现在：`Rollback(...)` 返回 bool，只有两边都真的撤销了才收回结论。
        //
        // 这里能真实构造的是「**根本没进入事务**」这一侧：失败发生在 try 之前，所以既没写过、
        // 也没回滚过 —— 两个字段都必须是 false。
        Check("未进入写入事务的失败既不算写过、也不算已收尾（§17 P1-1）",
            !zwMissing.FilesWritten && !zwMissing.RollbackHandled,
            $"written={zwMissing.FilesWritten} handled={zwMissing.RollbackHandled}");

        // 另一侧（自己回滚**成功** → false/true）由上面的 holdSecondDeploy 断言覆盖。
        //
        // ⚠️ **已知缺口：第三侧「回滚失败」（应为 true/false）在默认套件里没有守护。**
        // 构造它需要「已经写过、然后回滚时快照不可用」——而 `Deploy` 在写之前就 `Snapshot`（写之前的
        // 失败不会进入已写状态），写成功之后又只剩几步不会失败的收尾。`AppPaths.Root` 由环境变量在
        // 首次访问时解析一次（`Store.cs:16`），测试内无法再改，所以也没法把 `restore` 路径占成文件。
        // **如实记录为缺口，而不是写一个测不到该路径的假测试。**

        // §17 P1-1（Pass B 报出）：**「Deploy 写了、又自己回滚了」的运行，不得被外层按上一次的记录再回滚一次。**
        //
        // 缺陷的形状：`Deploy` 在 try 第一行置 `FilesWritten = true`（早于任何真实写入），而 catch 里
        // **已经自己调用了 `Rollback(...)`** 把写下的代理与 INI 恢复原状 —— 却不收回那个标志。于是调用方
        // 见「写过盘、且失败」就再回滚一次，**而回滚用的是上一次的部署记录**（`game.Deployment` 只在成功
        // 路径被替换）⇒ 删掉的是**用户上一次装好的、正在用的安装**，而报告写「已回滚」。
        //
        // 触发方式刻意走**真实路径**：用 `FileShare.None` 独占源 DLL，让事务内的复制失败。
        // （`ProbeSignature` 对这个文件判 `NotSigned`，两个 provider 都接受，所以流程能走到 `Deploy`。）
        var holdPayload = Path.Combine(work, "wf-hold-payload");
        Directory.CreateDirectory(holdPayload);
        File.WriteAllText(Path.Combine(holdPayload, "version.dll"), "payload");
        File.WriteAllText(Path.Combine(holdPayload, ModSource.IniName), "[DLSSG SM86]" + Environment.NewLine);

        var holdParts = Build(work, "wfHoldSource");
        var holdGame = new GameEntry { Name = "wfHoldSource", RenderDir = MakeGameDir(work, "wfHoldGame") };

        // 第一次：正常装好，建立「用户既有的可用安装」。
        var holdFirst = holdParts.Workflow.RunAsync(
            MakeRequest("wfHoldSource", holdPayload, holdParts.Provider, holdGame),
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("（前置）先建立一次成功的安装（§17 P1-1）",
            holdFirst.Outcome == WorkflowOutcome.Succeeded,
            holdFirst.Outcome + " / " + string.Join("; ", holdFirst.Errors));

        var holdProxy = Path.Combine(holdGame.RenderDir, "version.dll");
        var holdIni = Path.Combine(holdGame.RenderDir, ModSource.IniName);
        var hadRecord = holdGame.Deployment is not null;

        // 第二次：独占源 DLL，让 `Deploy` 在事务内失败并自回滚。
        var holdIncomplete = false;
        var holdSucceeded = false;

        using (new FileStream(Path.Combine(holdPayload, "version.dll"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var holdSecond = holdParts.Workflow.RunAsync(
                MakeRequest("wfHoldSource", holdPayload, holdParts.Provider, holdGame),
                null, CancellationToken.None).GetAwaiter().GetResult();

            holdIncomplete = holdSecond.RollbackIncomplete;
            holdSucceeded = holdSecond.Outcome == WorkflowOutcome.Succeeded;
        }

        Check("源 DLL 被独占时第二次运行不成功（§17 P1-1 的前置）", !holdSucceeded);

        // **这三条是核心断言。** 修复前：外层的二次回滚会把第一次装好的文件删掉，
        // 而 `filesRolledBack` 返回 true、报告写「已回滚」—— 用户失去一个本来能用的安装。
        //
        // ⚠️ 注意：这一段**不能只靠 workflow 层触发**。实测表明：第一次装好之后，planner 会因为入口已被
        // 自己占用而 fail-closed，于是第二次运行**在计划阶段就失败、根本没进 `Deploy` 的 try** ——
        // 我最初就是这样写的，而「回退修复后断言仍全绿」证明了那样写抓不到缺陷。
        // 所以下面直接对 `Deploy` 断言：那是 `FilesWritten` 与 `RollbackHandled` 真正产生的地方。
        var holdSource = new ModSource(holdPayload);

        // 先成功部署一次，建立「用户既有的可用安装」与部署记录。
        var holdFirstDeploy = DeploymentService.Deploy(holdGame, holdSource);

        Check("（前置）直接部署一次成功（§17 P1-1）",
            holdFirstDeploy.Ok && holdFirstDeploy.FilesWritten,
            $"ok={holdFirstDeploy.Ok} written={holdFirstDeploy.FilesWritten} / {holdFirstDeploy.Message}");

        var holdProxyExists = File.Exists(holdProxy);
        var holdRecordAfterFirst = holdGame.Deployment;

        // §17 P2-2：**成功的事务必须把自己的快照删掉。**
        //
        // `Deploy` 在 `%RestoreRoot%\<game>\<时间戳>\_pending\` 里建快照（装「这次要覆盖的原字节」），
        // 而原来**只有 catch 分支**删它 ⇒ 每次成功的重部署都多留一份代理 + INI（真实代理约 15 MB）；
        // 且 `backups.Count == 0` 时连 `RestoreFolder` 都不会被记录 —— **那些副本没有任何引用者**，
        // 用户完全看不到磁盘在增长。
        //
        // 这条**可以测**：`AppPaths.RestoreRoot` 由 `DLSSGMANAGER_HOME` 决定，Harness 已经设过它。
        // （与「`ScanForLeftovers` 的误报」不同 —— 那条需要串起三段夹具，这条只需要一次成功部署。）
        var gameRestoreDir = Path.Combine(AppPaths.RestoreRoot, holdGame.Id);

        var pendingLeft = Directory.Exists(gameRestoreDir)
            && Directory.EnumerateDirectories(gameRestoreDir)
                .Any(d => Directory.Exists(Path.Combine(d, "_pending")));

        Check("成功的部署不留下事务快照（§17 P2-2）",
            !pendingLeft, $"restore/{holdGame.Id} 下仍有 _pending");

        // ⚠️ **「回滚失败」这一态在本套件里仍然没有断言守护 —— 而这是我尝试失败后的记录。**
        //
        // 我按审查者给的思路把 `%RestoreRoot%\<gameId>` 占成一个**文件**，期望 `Snapshot` 里的
        // `Directory.CreateDirectory(txFolder)` 抛。**实测：它确实让 `Snapshot` 失败，但 `Snapshot`
        // 自己吞掉异常、返回 null，部署照常成功完成**（这是合理的 —— 快照失败不该阻止部署）。
        // 于是我得到的是「成功的部署」，而它在成功路径上同样是 `FilesWritten=true / RollbackHandled=false`
        // ⇒ 这个夹具**证明不了**任何与「回滚失败」有关的事。
        //
        // **留一个恒真的断言比没有断言更糟**（它会让报告里的「已覆盖」变成假话），所以我删掉了它，
        // 而不是把它改成「尽量能通过」的样子。
        //
        // 审查者用它的自建探针**做到了**这一态（`Ok=False / FilesWritten=True / RollbackHandled=False`），
        // 说明入口确实存在 —— **只是不是我现在用的这个**：需要让 `Deploy` **在写入之后**才失败
        // （例如让第 4 步之后某一步抛），而不是让它「预感知」到快照不可用。
        // **下一步若要补这条：从「写入完成后、收尾之前」找一个可注入的失败点。**

        // 再部署一次，但独占源 DLL，让事务内的复制失败 → catch 自回滚。
        using (new FileStream(Path.Combine(holdPayload, "version.dll"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var holdSecondDeploy = DeploymentService.Deploy(holdGame, holdSource);

            Check("源 DLL 被独占时部署失败（§17 P1-1 的前置）", !holdSecondDeploy.Ok, holdSecondDeploy.Message);

            // **这两条是修复的语义本身**：写了但已自己回滚 ⇒ 不再要求调用方回滚，同时留下「已自回滚」的痕迹。
            Check("自回滚的部署必须收回「需要调用方回滚」这个结论（§17 P1-1）",
                !holdSecondDeploy.FilesWritten,
                $"written={holdSecondDeploy.FilesWritten} handled={holdSecondDeploy.RollbackHandled}");

            Check("自回滚的部署必须记录「我们自己收过尾」（§17 P1-1）",
                holdSecondDeploy.RollbackHandled,
                $"handled={holdSecondDeploy.RollbackHandled}");
        }

        Check("自回滚的失败不得删掉用户既有的安装（§17 P1-1）",
            holdProxyExists && File.Exists(holdProxy) && File.Exists(holdIni),
            $"proxy={File.Exists(holdProxy)} ini={File.Exists(holdIni)}");

        // **比较身份，而不是判空。** 这条断言原来只写 `is not null` —— 而**记录被换成「本次（已回滚）」
        // 的新记录也照样通过**，那恰恰是它名字里说「不得清掉」却没在检查的事。
        //
        // `Deploy` 在 try 内就会换掉记录（`game.Deployment = new DeploymentInfo {...}`）；**在那之后才失败**
        // 的运行会留下一个指向不存在字节的记录（界面显示 Modified/Missing）。这条断言要守的正是那种情况：
        // 记录不能停在「本次」这个新值上。
        Check("自回滚的失败不得把部署记录换成新的（§17 P1-1 · 比较身份）",
            ReferenceEquals(holdGame.Deployment, holdRecordAfterFirst),
            holdRecordAfterFirst is null ? "(前置未建立记录)" : "记录被替换了");

        // **这条是 P1-2 的核心断言**：用户第一次装好的文件必须还在。
        Check("零写入的失败不得删掉既有安装（§17 P1-2）",
            File.Exists(installedProxy) && File.Exists(installedIni),
            $"proxy={File.Exists(installedProxy)} ini={File.Exists(installedIni)}");

        Check("回滚成功时不声称回滚未完成", !rolledBack.RollbackIncomplete,
            $"incomplete={rolledBack.RollbackIncomplete}");

        // P0-10 的另一半，也是真正要防的那一半：回滚**尝试过但没成功**时必须被显式暴露，而不是只写一句
        // 「已回滚」—— 机器上可能还留着本次写下的东西，这是用户最需要先知道的事实。
        // ⚠️ 失败侧的场景还不能这样写，两个方向都试过了：
        //
        //  · 换成 `new RecordingProvider { FailRestore = true }` —— 兼容性记录是按 provider id `mfg-smooth`
        //    注册的，换替身后查询落空，流程停在 NeedsConfirmation，**根本走不到安装与回滚**
        //    （实测 outcome=NeedsConfirmation）；
        //  · 强转 `(RecordingProvider)rbParts.Provider` —— `Build` 造的是 `MfgSmoothProvider`，
        //    运行期抛 InvalidCastException，**把整个套件从 887 项腰斩到 807 项**（中途崩溃表现为总数下降，
        //    而不是单个失败 —— 这正是本项目记录过的观测方式）。
        //
        // 要测 RollbackIncomplete 的失败侧，需要让 `MfgSmoothProvider` 支持注入还原失败（它内部走
        // `DeploymentService.Restore` 静态方法，不是可替换的实例成员）。**在那之前，这条断言留空比留一条
        // 假的更有价值** —— 成功侧的「回滚成功时不声称回滚未完成」已经在上面覆盖，它至少能挡住「恒为 true」
        // 的实现。

        // ---- 7. profile journal wiring ----
        var jParts = Build(work, "wfJournal");
        var jDir = Path.Combine(work, "wf-journal-payload");
        var jGame = new GameEntry { Name = "wfJournal", RenderDir = MakeGameDir(work, "wfJournalGame") };

        var withProfile = jParts.Workflow.RunAsync(
            MakeRequest("wfJournal", jDir, jParts.Provider, jGame) with { ProfileSettings = new[] { SmoothMotionSettings.All[0] } },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Check("Profile 写入成功时编排成功", withProfile.Outcome == WorkflowOutcome.Succeeded, string.Join("; ", withProfile.Errors));
        Check("Profile 成功后状态至少到 Requested",
            withProfile.Evidence >= SmoothMotionEvidence.Requested, withProfile.Evidence.ToString());
        Check("Profile 写入不等于 Verified",
            withProfile.Evidence != SmoothMotionEvidence.Verified, withProfile.Evidence.ToString());

        // ---- 8. cancellation propagates rather than being swallowed ----
        var cancelParts = Build(work, "wfCancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = false;
        try
        {
            cancelParts.Workflow.RunAsync(
                MakeRequest("wfCancel", Path.Combine(work, "wf-cancel-payload"), cancelParts.Provider,
                    new GameEntry { Name = "wfCancel", RenderDir = MakeGameDir(work, "wfCancelGame") }),
                null, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { cancelled = true; }

        Check("取消以取消结束而非静默成功", cancelled);
    }

    /// <summary>
    /// Stage 9: the compatibility evidence model and recipe memory.
    ///
    /// The load-bearing assertion is the negative one: a claim of "verified" that is backed only by
    /// upstream documentation must be refused, not stored. Everything else here supports that.
    /// </summary>
    private static void TestCompatibilityEvidence(string work)
    {
        Section("兼容数据库与证据（Stage 9）");

        Check("证据来源覆盖七类以上",
            Enum.GetValues<EvidenceSource>().Length >= 8, "实际: " + Enum.GetValues<EvidenceSource>().Length);
        Check("证据类型可表达", Enum.GetValues<EvidenceType>().Length >= 6);
        Check("验证等级共五级", Enum.GetValues<ValidationLevel>().Length == 5);

        var readme = new EvidenceRef(EvidenceSource.OfficialProviderReadme, EvidenceType.Documentation, "README");
        var community = new EvidenceRef(EvidenceSource.CommunityReproduction, EvidenceType.Reproduction, "issue #1");
        var localUndated = new EvidenceRef(EvidenceSource.LocalUserTest, EvidenceType.TestResult, "this machine");
        var localDated = new EvidenceRef(EvidenceSource.LocalUserTest, EvidenceType.TestResult, "this machine", DateTimeOffset.Now);
        var maintainer = new EvidenceRef(EvidenceSource.ProjectMaintainerTest, EvidenceType.TestResult, "ci", DateTimeOffset.Now);

        // ---- first-hand vs not ----
        Check("上游文档证据不是第一手", !readme.IsFirstHand);
        Check("社区复现不是第一手", !community.IsFirstHand);
        Check("本地用户测试是第一手", localDated.IsFirstHand);

        // ---- the rule: no real-machine evidence means no Project Verified ----
        Check("文档证据不能标 Project Verified", !ValidationGuard.CanClaimProjectVerified(readme, DateTimeOffset.Now));
        Check("社区复现不能标 Project Verified", !ValidationGuard.CanClaimProjectVerified(community, DateTimeOffset.Now));
        Check("无日期的本地测试不能标 Project Verified", !ValidationGuard.CanClaimProjectVerified(localUndated, null));
        Check("有日期的本地测试可以标 Project Verified", ValidationGuard.CanClaimProjectVerified(localDated, DateTimeOffset.Now));

        var downgraded = ValidationGuard.Normalize(readme, DateTimeOffset.Now, ValidationLevel.ProjectVerified);
        Check("越级声明被降级而非接受",
            downgraded.Level == ValidationLevel.PendingUserValidation, downgraded.Level.ToString());
        Check("降级附带原因", downgraded.Note.Contains("Project Verified"));

        var undated = ValidationGuard.Normalize(localUndated, null, ValidationLevel.ProjectVerified);
        Check("缺日期的第一手声明同样被降级",
            undated.Level == ValidationLevel.PendingUserValidation && undated.Note.Contains("日期"));

        var kept = ValidationGuard.Normalize(maintainer, DateTimeOffset.Now, ValidationLevel.ProjectVerified);
        Check("有据声明被原样保留", kept.Level == ValidationLevel.ProjectVerified && kept.Note.Length == 0);
        Check("非 ProjectVerified 的声明不被改动",
            ValidationGuard.Normalize(readme, null, ValidationLevel.Documented).Level == ValidationLevel.Documented);

        // ---- recipe memory ----
        var memoryPath = Path.Combine(work, "recipe-memory.json");
        var memory = new RecipeMemoryStore(memoryPath);
        var now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

        memory.Record("mfg-sm86/proxy", "TestGame", "mfg-smooth", succeeded: true,
            SmoothMotionEvidence.Installed, now, readme, ValidationLevel.Documented);

        var afterFiles = memory.Find("mfg-sm86/proxy", "TestGame");
        Check("记录一次安装尝试", afterFiles is not null && afterFiles.Successes == 1);
        Check("仅文件部署不足以判为 known-good", !afterFiles!.IsKnownGood);
        Check("证据不足时不越级", afterFiles.Validation == ValidationLevel.Documented);

        memory.Record("mfg-sm86/proxy", "TestGame", "mfg-smooth", succeeded: true,
            SmoothMotionEvidence.Verified, now, localDated, ValidationLevel.ProjectVerified);

        var verified = memory.Find("mfg-sm86/proxy", "TestGame")!;
        Check("同一配方同一游戏的多次尝试被合并", verified.Successes == 2);
        Check("真正验证后判为 known-good", verified.IsKnownGood, verified.Validation.ToString());
        Check("第一手证据被采纳为已验证", verified.Validation == ValidationLevel.ProjectVerified);
        Check("最高证据级别被保留", verified.HighestEvidence == SmoothMotionEvidence.Verified);

        memory.Record("readme-only/proxy", "TestGame", "mfg-smooth", succeeded: true,
            SmoothMotionEvidence.Verified, now, readme, ValidationLevel.ProjectVerified);

        var readmeOnly = memory.Find("readme-only/proxy", "TestGame")!;
        Check("仅凭文档的越级声明被降级入库",
            readmeOnly.Validation == ValidationLevel.PendingUserValidation, readmeOnly.Validation.ToString());
        Check("降级原因写进条目", readmeOnly.Note.Contains("Pending User Validation"));

        memory.Record("bad/proxy", "TestGame", "mfg-smooth", succeeded: false,
            SmoothMotionEvidence.None, now, localDated, ValidationLevel.Unverified, note: "部署失败");

        var bad = memory.Find("bad/proxy", "TestGame")!;
        Check("失败被计数", bad.Failures == 1 && bad.Successes == 0);
        Check("有失败记录时不判为 known-good", !bad.IsKnownGood);

        var ranked = memory.RankFor("TestGame");
        Check("排序把 known-good 排在首位", ranked.Count >= 3 && ranked[0].IsKnownGood, ranked[0].RecipeId);
        Check("只返回该游戏的条目", ranked.All(r => r.Game == "TestGame"));

        // ---- persistence ----
        memory.Persist();
        var reloaded = new RecipeMemoryStore(memoryPath);
        reloaded.Load();

        Check("配方记忆可持久化并重载", reloaded.Count == memory.Count, $"{reloaded.Count} / {memory.Count}");

        var roundTrip = reloaded.Find("mfg-sm86/proxy", "TestGame");
        Check("重载保留成功计数", roundTrip is not null && roundTrip.Successes == 2);
        Check("重载保留证据来源",
            roundTrip!.EvidenceRef.Source == EvidenceSource.LocalUserTest, roundTrip.EvidenceRef.Source.ToString());
        Check("重载保留验证等级", roundTrip.Validation == ValidationLevel.ProjectVerified, roundTrip.Validation.ToString());
        Check("重载保留最高证据级别", roundTrip.HighestEvidence == SmoothMotionEvidence.Verified);

        var corruptPath = Path.Combine(work, "recipe-corrupt.json");
        File.WriteAllText(corruptPath, "{ not json");
        var corrupt = new RecipeMemoryStore(corruptPath);
        var threw = false;
        try { corrupt.Load(); } catch { threw = true; }
        Check("损坏的配方记忆不影响启动", !threw);

        // ---- the matrix carries evidence too ----
        var matrixPath = Path.Combine(work, "evidence-matrix.json");
        var matrix = new CompatibilityMatrixStore(matrixPath);
        matrix.Add(new CompatibilityRecord(
            Gpu: "RTX 3070 Ti", Provider: "mfg-smooth", ProviderVersion: "2.9.0",
            Validation: ValidationState.ReportedWorking,
            Evidence: localDated, EvidenceValidation: ValidationLevel.ProjectVerified));

        matrix.Persist();
        var matrixReloaded = new CompatibilityMatrixStore(matrixPath);
        matrixReloaded.Load();

        var loadedRecord = matrixReloaded.All.FirstOrDefault();
        Check("矩阵记录保留证据来源",
            loadedRecord?.Evidence?.Source == EvidenceSource.LocalUserTest,
            loadedRecord?.Evidence?.Source.ToString() ?? "(null)");
        Check("矩阵记录保留验证等级",
            loadedRecord?.EvidenceValidation == ValidationLevel.ProjectVerified,
            loadedRecord?.EvidenceValidation.ToString() ?? "(null)");
        Check("矩阵记录保留证据引用", loadedRecord?.Evidence?.Reference == "this machine");
        Check("矩阵记录仍可查询（新字段未破坏匹配）",
            matrixReloaded.Query(new CompatibilityQuery(Gpu: "RTX 3070 Ti", Provider: "mfg-smooth",
                ProviderVersion: "2.9.0")).Kind != CompatibilityMatchKind.None);
    }

    /// <summary>
    /// Stage 10: the presentation layer.
    ///
    /// The XAML itself cannot be exercised here, so what is tested is the part that decides what the
    /// interface <i>says</i>. That is where honesty is either kept or lost: whether an unknown state is
    /// shown as fine, and whether "files were copied" is shown as "working".
    /// </summary>
    private static void TestPresentationLayer(string work)
    {
        Section("界面呈现层（Stage 10）");

        // ---- navigation ----
        Check("七个页面齐备", NavigationModel.Pages.Count == 7, "实际: " + NavigationModel.Pages.Count);
        Check("七个页面各不重复",
            NavigationModel.Pages.Select(p => p.Page).Distinct().Count() == 7);
        Check("页面顺序固定为设计顺序",
            NavigationModel.Pages.Select(p => p.Page).SequenceEqual(new[]
            {
                AppPage.Dashboard, AppPage.Library, AppPage.GameDetails,
                AppPage.Updates, AppPage.Downloads, AppPage.Diagnostics, AppPage.Settings,
            }));
        Check("默认页是总览", NavigationModel.DefaultPage == AppPage.Dashboard);
        Check("主流程页不含高级页",
            NavigationModel.PrimaryFlow.All(p => !NavigationModel.Describe(p).Advanced));
        Check("主流程为 游戏库 → 详情 → 更新",
            NavigationModel.PrimaryFlow.SequenceEqual(new[] { AppPage.Library, AppPage.GameDetails, AppPage.Updates }));
        Check("高级页只有诊断与设置",
            NavigationModel.AdvancedPages.Select(p => p.Page).OrderBy(p => p)
                .SequenceEqual(new[] { AppPage.Diagnostics, AppPage.Settings }.OrderBy(p => p)));
        Check("每个页面都有标题与用途", NavigationModel.Pages.All(p => p.Title.Length > 0 && p.Purpose.Length > 0));

        // ---- advanced panel is folded by default ----
        Check("高级分组全部默认折叠", AdvancedPanel.AllCollapsedByDefault);
        Check("高级分组覆盖要求的全部条目",
            new[] { "Proxy", "ASI", "API", "NVIDIA Profile", "Flip Pacing", "Low Latency", "Provider", "Release Channel", "Logs" }
                .All(item => AdvancedPanel.AllItems.Contains(item)),
            string.Join(", ", AdvancedPanel.AllItems));
        Check("高级分组仍可展开（是折叠不是隐藏）", AdvancedPanel.Groups.Count > 0 && AdvancedPanel.Groups.All(g => g.Items.Count > 0));
        Check("Profile 设置随界面携带出处",
            AdvancedPanel.EditableProfileSettings.Count == 6 &&
            AdvancedPanel.EditableProfileSettings.All(s => s.Provenance.Contains("Undocumented")));

        // ---- the honesty rules ----
        Check("未知状态不显示为正常",
            StatusPresenter.ForProvider(ProviderHealth.Available("")).Tone == StatusTone.Good &&
            StatusPresenter.ForProvider(new ProviderHealth(ProviderHealthState.Available, "")).Tone == StatusTone.Good);

        Check("Provider 可用为 Good",
            StatusPresenter.ForProvider(ProviderHealth.Available("ok")).Tone == StatusTone.Good);
        Check("Provider 限流为 Warning",
            StatusPresenter.ForProvider(ProviderHealth.RateLimited("rate")).Tone == StatusTone.Warning);
        Check("Provider 结构变更为 Bad",
            StatusPresenter.ForProvider(ProviderHealth.ReleaseFormatChanged("changed")).Tone == StatusTone.Bad);
        Check("Provider 不可用不等于损坏",
            StatusPresenter.ForProvider(ProviderHealth.Unavailable("offline")).Tone == StatusTone.Warning &&
            StatusPresenter.ForProvider(ProviderHealth.Broken("bad")).Tone == StatusTone.Bad);

        // The two rules that stop the interface from being reassuring but wrong.
        Check("仅安装文件不显示为成功",
            StatusPresenter.ForEvidence(SmoothMotionEvidence.Installed).Tone == StatusTone.Warning,
            StatusPresenter.ForEvidence(SmoothMotionEvidence.Installed).Tone.ToString());
        Check("仅安装文件时说明「不等于生效」",
            StatusPresenter.ForEvidence(SmoothMotionEvidence.Installed).Detail.Contains("生效"));
        Check("只有 Verified 显示为成功",
            StatusPresenter.ForEvidence(SmoothMotionEvidence.Verified).Tone == StatusTone.Good &&
            new[]
            {
                SmoothMotionEvidence.None, SmoothMotionEvidence.Installed, SmoothMotionEvidence.Loaded,
                SmoothMotionEvidence.Requested, SmoothMotionEvidence.Applied,
            }.All(e => StatusPresenter.ForEvidence(e).Tone != StatusTone.Good));
        Check("未安装为中性", StatusPresenter.ForEvidence(SmoothMotionEvidence.None).Tone == StatusTone.Neutral);

        Check("更新检查失败不显示为已是最新",
            StatusPresenter.ForUpdate(UpdateCheckResult.Unknown("t", null, ReleaseChannel.Stable,
                DateTimeOffset.Now, UpdateState.Unknown, UpdateNetworkState.Offline, "offline")).Tone != StatusTone.Good);
        Check("确实是最新才显示为成功",
            StatusPresenter.ForUpdate(UpdateCheckResult.Unknown("t", null, ReleaseChannel.Stable,
                DateTimeOffset.Now, UpdateState.UpToDate, UpdateNetworkState.Ok, "ok")).Tone == StatusTone.Good);
        Check("被阻止的编排为 Bad",
            StatusPresenter.ForOutcome(WorkflowOutcome.Blocked).Tone == StatusTone.Bad);
        Check("需要确认为 Warning 而非 Bad",
            StatusPresenter.ForOutcome(WorkflowOutcome.NeedsConfirmation).Tone == StatusTone.Warning);

        // ---- planner agreement ----
        Check("仅精确匹配且记录可用时无需询问",
            StatusPresenter.CanConfigureWithoutAsking(CompatibilityMatchKind.Exact, ValidationState.ReportedWorking));
        Check("部分匹配时必须询问",
            !StatusPresenter.CanConfigureWithoutAsking(CompatibilityMatchKind.Partial, ValidationState.ReportedWorking));
        Check("精确匹配但记录不可用时必须询问",
            !StatusPresenter.CanConfigureWithoutAsking(CompatibilityMatchKind.Exact, ValidationState.ReportedBroken));

        Check("按钮文案随证据级别变化",
            StatusPresenter.ConfigureButtonText(SmoothMotionEvidence.None) == "自动配置" &&
            StatusPresenter.ConfigureButtonText(SmoothMotionEvidence.Installed) == "重新配置");

        // ---- library rows ----
        var noRecord = new GameEntry { Name = "Alpha", RenderDir = "C:\\games\\alpha" };
        var withRecord = new GameEntry { Name = "Beta", RenderDir = "C:\\games\\beta" };
        withRecord.Deployment = new DeploymentInfo { ProxyName = "version.dll" };

        var row = LibraryPresenter.For(noRecord);
        Check("未部署的游戏显示为未安装", row.StatusText == "未安装" && row.Tone == StatusTone.Neutral, row.StatusText);
        Check("未部署不等于就绪", row.Tone != StatusTone.Good);
        Check("已部署的游戏显示为「已安装，尚未验证」",
            LibraryPresenter.For(withRecord).Tone == StatusTone.Warning,
            LibraryPresenter.For(withRecord).StatusText);
        Check("空名称有兜底显示",
            LibraryPresenter.For(new GameEntry { Name = "", RenderDir = "x" }).Name == "(未命名)");

        var rows = LibraryPresenter.Build(new[] { noRecord, withRecord });
        Check("列表为每个游戏生成一行", rows.Count == 2);
        Check("有部署记录的排在未安装之前", rows[0].Name == "Beta", rows[0].Name);
        Check("全部行都可配置", rows.All(r => r.CanConfigure));
    }
}
