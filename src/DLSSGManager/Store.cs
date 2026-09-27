using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DLSSGManager;

/// <summary>App-owned locations. Nothing here ever lives inside a game directory except the two deployed files.</summary>
public static class AppPaths
{
    /// <summary>
    /// Overrides the data folder. Set by the test harness so its deliberately-corrupt fixtures and
    /// download logs stay out of the user's real library and log.
    /// </summary>
    public const string RootOverrideVariable = "DLSSGMANAGER_HOME";

    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                return Path.GetFullPath(overridden);
            }
            catch
            {
                // A malformed override falls back to the standard location.
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DLSSGManager");
    }

    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string RestoreRoot => Path.Combine(Root, "restore");
    public static string LogFile => Path.Combine(Root, "manager.log");

    /// <summary>Mod files shipped next to the app, used when this copy is portable.</summary>
    public static string BundledModDir => Path.Combine(AppContext.BaseDirectory, "mod");

    /// <summary>
    /// Per-user mod folder, used when the app is installed somewhere read-only such as
    /// <c>C:\Program Files</c>. Keeps the download working without requiring elevation.
    /// </summary>
    public static string UserModDir => Path.Combine(Root, "mod");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RestoreRoot);
    }

    private static readonly object FileLock = new();

    public static void Log(string message)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.AppendAllText(LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never take the app down.
            }
        }
    }
}

public static class LibraryStore
{
    /// <summary>
    /// Reads the library. <paramref name="path"/> exists so tests can round-trip against a scratch file
    /// instead of the user's real library.
    /// </summary>
    /// <summary>
    /// 上一次 <see cref="Load"/> 是否**降级**过 —— 即库文件读不出来、本次以空库继续。
    ///
    /// <para><b>为什么需要它。</b>字节保留（改名成 <c>.corrupt-&lt;时间戳&gt;</c>）只是这条缺陷的一半：
    /// **另一半是「用户应当知道」**。不给信号的话，界面会把「库损坏」显示成**和「首次运行」一模一样**
    /// 的空列表 —— 用户会以为自己的游戏列表被删了，而实际上它就在旁边那个文件里。</para>
    ///
    /// <para>它是静态的、只反映**最近一次** <c>Load</c>：调用方（启动路径）在 `Load` 之后立即读取它。</para>
    /// </summary>
    public static bool LastLoadWasDegraded { get; private set; }

    /// <summary>损坏文件被改名后的路径；无法保留时为 null（那时它接下来可能被覆盖）。</summary>
    public static string? LastLoadSalvagePath { get; private set; }

    public static AppData Load(string? path = null)
    {
        LastLoadWasDegraded = false;
        LastLoadSalvagePath = null;

        var file = path ?? AppPaths.LibraryFile;
        try
        {
            if (File.Exists(file))
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var data = System.Text.Json.JsonSerializer.Deserialize<AppData>(json, JsonOptions);
                if (data is not null)
                {
                    foreach (var g in data.Games) Normalize(g);
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            // **损坏的文件必须先挪走，否则它会被下一次 `Save` 覆盖掉。**
            //
            // 这里的失败路径曾经只写一行日志：读不出来 ⇒ 返回空库 ⇒ 用户看到「游戏列表没了」，
            // 而**只要他做任何一次改动，`Save` 就会把那些损坏但可能还能抢救的字节覆盖成一份新库**
            // （连同里面所有的部署记录）。**用户既没有得到提示，也没有留下任何可恢复的东西。**
            //
            // 现在把它改名成 `.corrupt-<时间戳>` 保留现场，并在日志里写出位置 ——
            // 这样「读不出来」这件事**不消耗掉原始的字节**，需要时还能人工看一眼或抢救。
            AppPaths.Log($"读取库文件失败: {ex.Message}");
            LastLoadWasDegraded = true;
            LastLoadSalvagePath = null;
            try
            {
                var salvage = $"{file}.corrupt-{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Move(file, salvage, overwrite: true);
                LastLoadSalvagePath = salvage;
                AppPaths.Log($"损坏的库文件已保留为: {salvage}（本次以空库继续，原文件未被覆盖）");
            }
            catch (Exception moveEx)
            {
                // 连改名都失败（被占用、无权限）—— 此时**更要**说明，因为那份文件接下来有被覆盖的风险。
                AppPaths.Log($"无法保留损坏的库文件（{moveEx.Message}）；它可能在下一次保存时被覆盖。");
            }
        }

        return new AppData();
    }

    public static void Save(AppData data, string? path = null)
    {
        var file = path ?? AppPaths.LibraryFile;
        try
        {
            AppPaths.EnsureCreated();
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = System.Text.Json.JsonSerializer.Serialize(data, JsonOptions);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("保存库文件失败: " + ex.Message);
        }
    }

    private static void Normalize(GameEntry g)
    {
        g.Profile = g.Profile ?? new GameProfile();
        g.Profile.Router = NormalizeRouter(g.Profile.Router);
        g.Profile.KernelImage = NormalizeKernel(g.Profile.KernelImage);
        g.Profile.Preset = NormalizePreset(g.Profile.Preset);

        // Libraries written before 0.3.3 stored Optimized as a boolean; migrate once so the tier
        // becomes the single source of truth (true = bit-identical speedups = tier 1).
        if (g.Profile.Optimized is bool legacy)
        {
            g.Profile.OptimizedTier = legacy ? 1 : 0;
            g.Profile.Optimized = null;
        }
        g.Profile.OptimizedTier = Math.Clamp(g.Profile.OptimizedTier, 0, 3);

        g.Profile.MaxGeneratedFrames = Math.Clamp(g.Profile.MaxGeneratedFrames, 1, 5);
        g.Profile.LogLevel = Math.Clamp(g.Profile.LogLevel, 0, 3);
        if (g.Deployment is not null)
        {
            g.Deployment.Backups ??= new List<BackupItem>();
            g.Deployment.Files ??= new List<DeployedFile>();
        }
    }

    /// <summary>Auto (let the game or driver profile decide), A (force UI recomposition off) or B (on).</summary>
    public static string NormalizePreset(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "A" => "A",
        "B" => "B",
        _ => "Auto",
    };

    public static string NormalizeRouter(string? value) =>
        string.Equals(value, "SM75", StringComparison.OrdinalIgnoreCase) ? "SM75" : "SM86";

    public static string NormalizeKernel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cubin" => "Cubin",
        "auto" => "Auto",
        _ => "PTX",
    };

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// The shipped dlssg_sm86.ini is mostly comments; it is used verbatim as a template and only the keys it
/// actually contains are rewritten.
///
/// That last part is deliberate: upstream changed the schema between releases (0.3.0 has Enabled,
/// Optimized and Preset; the 0.2.x line had Router, KernelImage and HardwareBilinear). Writing keys a
/// build does not know would leave dead settings in every deployed INI, so a key that the template does
/// not define is simply not written.
/// </summary>
public static class IniTemplate
{
    /// <summary>
    /// Values for the keys the manager exposes. Harmless to include all of them: only the ones present in
    /// the template are written.
    /// </summary>
    private static Dictionary<string, string> Values(GameProfile p) => new(StringComparer.OrdinalIgnoreCase)
    {
        // 0.3.0+ (Optimized became a 0-3 consistency tier in 0.3.3; the key name is unchanged)
        ["Enabled"] = p.Enabled ? "1" : "0",
        ["Optimized"] = Math.Clamp(p.OptimizedTier, 0, 3).ToString(),
        ["Preset"] = p.Preset,
        ["MaxGeneratedFrames"] = Math.Clamp(p.MaxGeneratedFrames, 1, 5).ToString(),
        ["Level"] = Math.Clamp(p.LogLevel, 0, 3).ToString(),

        // 0.2.x (kept so an older payload still receives the user's settings)
        ["Router"] = p.Router,
        ["KernelImage"] = p.KernelImage,
        ["HardwareBilinear"] = p.HardwareBilinear ? "1" : "0",
    };

    public static string Render(string templateText, GameProfile p)
    {
        // **「有模板但一个可填的键都没有」也必须走 fallback。**
        //
        // 这里原来只判「空」：模板非空就用它。而 `MfgSmoothProvider` 的正规化会写出一个**只有段头、
        // 没有任何 `Key=` 行**的桩（`[DLSSG SM86]`）—— 那份桩非空 ⇒ 用它 ⇒ 下面的替换循环
        // **一个键都匹配不到** ⇒ **部署出去的 INI 里没有任何设置**：界面上的 Optimized / Preset /
        // MaxGeneratedFrames / Level 全部落空，而用户看到的是「部署成功」。
        // （那个 provider 的注释写着「真实部署会覆盖它」—— **那句话是错的**，覆盖只发生在模板已有的键上。）
        //
        // 判据改成**「模板里有没有可填的键」**，而不是「模板是不是空的」。
        var text = HasAnyFillableKey(templateText, p) ? templateText : FallbackTemplate(p);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var values = Values(p);

        for (var i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=");
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            if (values.TryGetValue(key, out var v))
                lines[i] = $"{key}={v}";
        }

        var body = string.Join("\r\n", lines);

        if (p.Diagnostics && !body.Contains("[Diagnostics]", StringComparison.OrdinalIgnoreCase))
        {
            // A 0.2.x profiling section. 0.3.0 moved the equivalent knobs into its README and ignores an
            // unknown section, so appending it stays harmless.
            body += "\r\n\r\n[Diagnostics]\r\n; 记录异步 GPU 计时；PipelineSteps=1 会明显影响性能，仅用于剖析。\r\nPerformance=1\r\nPipelineSteps=0\r\n";
        }

        return body.TrimEnd() + "\r\n";
    }

    /// <summary>
    /// 模板里有没有**至少一个我们能填的键** —— 即：模板里的某个键名**出现在 <c>Values(p)</c> 里**。
    ///
    /// <para><b>为什么不是「有没有 `Key=` 行」。</b>那是上一版的判据，而它比替换循环**宽**：
    /// 替换循环只在**它认得的键**上填值（<c>values.TryGetValue</c>）。于是当模板里全是**不认识的键**
    /// —— 最典型的触发是**上游把设置改名**（例如 `Enabled` → `Enable`）—— 旧判据说「有键，用模板」，
    /// 替换循环却**一个都命中不了** ⇒ **部署出去的 INI 里没有任何用户设置，而用户看到「部署成功」**。
    /// 这与本轮已修的 P2-⑫（桩 INI 无键）**后果完全相同**，只是触发条件更窄。</para>
    ///
    /// <para><b>判据必须与替换循环同宽，而不是同形。</b>两者都问「这个键我填得了吗」——
    /// 只按语法（有没有 <c>=</c>）判断，就会在「语法上是键、语义上不认识」的那一类上漏掉。</para>
    /// </summary>
    private static bool HasAnyFillableKey(string? templateText, GameProfile p)
    {
        if (string.IsNullOrWhiteSpace(templateText)) return false;

        var known = Values(p);

        return templateText.Replace("\r\n", "\n").Split('\n')
            .Select(line => Regex.Match(line, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*="))
            .Any(m => m.Success && known.ContainsKey(m.Groups[1].Value));
    }

    /// <summary>
    /// Used when no usable template exists — either none could be read, or what was read has no keys to fill.
    /// It mirrors the current upstream schema.
    ///
    /// <para><b>首行必须带上 <c>dlssg_sm86</c> 这个标识。</b>卸载时的归属判据
    /// （<c>DeploymentService.LooksLikeProjectIni</c>）是「前 4 行含 <c>Native x.y</c>」**或**
    /// 「全文含 <c>dlssg_sm86</c>」。而这份模板此前两样都没有 ⇒ 用户一旦编辑过它（哈希对不上部署记录），
    /// 卸载就会把它当成**别人的文件**保留下来（实测消息：「保留 dlssg_sm86.ini：不是本项目的配置文件，未删除」）。
    /// **给出去的文件必须能证明它属于谁。**</para>
    /// </summary>
    private static string FallbackTemplate(GameProfile p) => string.Join("\r\n", new[]
    {
        $"; dlssg_sm86 —— Generated by DLSSG 30 系管理器 on {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Restart the game after changing this file.",
        "[General]",
        "Enabled=1",
        "",
        "[FrameGeneration]",
        "Optimized=1",
        // **`Preset` 必须有**（Pass E 报出）：`Values(p)` 里有它（0.3.0 schema 的 A/B 预设），
        // 而这份 fallback 此前漏了 ⇒ 走 fallback 时**用户选的预设被静默丢弃**，而界面说「部署成功」。
        // 本轮把 fallback 的触发条件放宽到 `HasAnyFillableKey` 之后，这条路径从「几乎不用」变成
        // **真的会用**（`MfgSmoothProvider` 的桩 INI 正是它要处理的场景）—— 键集没跟着核对。
        // **与已修的 P2-⑫ 后果同型：路径生效了，但键集不完整。**
        // （0.2.x 的 `Router` / `KernelImage` / `HardwareBilinear` 故意不放 —— 它们只对旧 payload 有意义，
        //   而模板只在**读不到可用模板**时用，那时没有旧 payload 可迁就。）
        $"Preset={p.Preset}",
        $"MaxGeneratedFrames={Math.Clamp(p.MaxGeneratedFrames, 1, 5)}",
        "",
        "[Logging]",
        $"Level={Math.Clamp(p.LogLevel, 0, 3)}",
    });
}
