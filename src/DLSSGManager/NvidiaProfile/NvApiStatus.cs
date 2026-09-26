namespace DLSSGManager.NvidiaProfile;

/// <summary>
/// NVAPI 状态码的官方名称与含义。
///
/// <para><b>为什么需要这个类。</b>此前代码把 `-160` / `-137` / `-167` 当作「未知错误码」写在报告与
/// 注释里，理由是「本机的研究资料副本里查不到」。**那个理由不成立** —— 官方头文件里全都有定义，
/// 只是本机副本不全。把「我的副本里没有」当成「官方没有定义」，等于把一个可修的问题标成不可推进。</para>
///
/// <para><b>定义来源</b>（`NVIDIA/nvapi` 官方仓库，逐行核对，仓库根目录 `nvapi_lite_common.h`）：
/// <code>
/// L320: NVAPI_INVALID_USER_PRIVILEGE    = -137  //!&lt; The application will require Administrator privileges to access this API.
/// L344: NVAPI_SETTING_NOT_FOUND         = -160  //!&lt; Setting is not found.
/// L350: NVAPI_EXECUTABLE_NOT_FOUND      = -166  //!&lt; Application not found in the Profile.
/// L351: NVAPI_EXECUTABLE_ALREADY_IN_USE = -167  //!&lt; Application already exists in the other profile.
/// </code>
/// 获取方式（`raw.githubusercontent.com` 在本机不可达，改走 git 通道）：
/// `git ls-remote https://github.com/NVIDIA/nvapi.git HEAD` 验证通道 →
/// `git clone --depth 1 --filter=blob:none --sparse` → 头文件在仓库根目录。</para>
///
/// <para><b>扩展纪律</b>：这里只列**已经在官方头文件中逐行核对过**的码。要新增一个，必须先去官方
/// 完整源里检索到它的名字与值，**不得凭数值相邻推断语义**（`-166` 与 `-167` 相邻但语义不同）。</para>
/// </summary>
public static class NvApiStatus
{
    /// <summary><c>NVAPI_OK</c> —— 调用成功。</summary>
    public const int Ok = 0;

    /// <summary>
    /// <c>NVAPI_INVALID_USER_PRIVILEGE</c> —— 该 API 需要管理员权限。
    ///
    /// <para>**这个码是「需要提权」的唯一权威依据。** 它出现时，正确的下一步是做普通权限 / 提权对照
    /// 实验，而不是继续猜 ABI。</para>
    /// </summary>
    public const int InvalidUserPrivilege = -137;

    /// <summary><c>NVAPI_SETTING_NOT_FOUND</c> —— 该设置不存在。**这是三态里的 <c>Absent</c>，不是错误。**</summary>
    public const int SettingNotFound = -160;

    /// <summary><c>NVAPI_EXECUTABLE_NOT_FOUND</c> —— 该 Profile 上没有这个名字的应用。业务上「没找到」，不是失败。</summary>
    public const int ExecutableNotFound = -166;

    /// <summary>
    /// <c>NVAPI_EXECUTABLE_ALREADY_IN_USE</c> —— **该应用已存在于另一个 Profile 中。**
    ///
    /// <para>这条曾经被误解得很严重：自检用固定的 `RTX30FGM-SMOKE.exe`，于是**第一次运行成功、之后永远
    /// 失败**（上一次留下的绑定还在别的 Profile 里）。固定名字必然会撞上这个码 —— 自检的对象名必须每次唯一。</para>
    /// </summary>
    public const int ExecutableAlreadyInUse = -167;

    /// <summary>该码是否表示「需要管理员权限」。**只认官方那一个码，不做数值推断。**</summary>
    public static bool RequiresElevation(int code) => code == InvalidUserPrivilege;

    /// <summary>
    /// 该码是否属于「业务上没找到」而不是「调用出错」。
    ///
    /// <para>这类码必须与真正的失败分开报告：`SETTING_NOT_FOUND` 是三态里的 `Absent`，
    /// `EXECUTABLE_NOT_FOUND` 是「这个游戏没有绑定 Profile」。**把它们算作调用失败，会让正常状态看起来像故障。**</para>
    /// </summary>
    public static bool IsNotFound(int code) => code is SettingNotFound or ExecutableNotFound;

    /// <summary>
    /// 该码是否值得原样重试。
    ///
    /// <para>`EXECUTABLE_ALREADY_IN_USE` 重试**永远不会成功** —— 它说的是「名字已被占用」，换名字才行。
    /// 把它归为可重试是自检「一次失败变成永久失败」的成因之一。</para>
    /// </summary>
    public static bool IsNameCollision(int code) => code == ExecutableAlreadyInUse;

    /// <summary>
    /// 官方名称，用于报告与日志。未知数值返回 <c>null</c> —— **调用方必须自己决定怎么措辞，
    /// 不允许把它悄悄写成「未知错误」了事**（那是本项目真实犯过的错）。
    /// </summary>
    public static string? NameOf(int code) => code switch
    {
        Ok => "NVAPI_OK",
        InvalidUserPrivilege => "NVAPI_INVALID_USER_PRIVILEGE",
        SettingNotFound => "NVAPI_SETTING_NOT_FOUND",
        ExecutableNotFound => "NVAPI_EXECUTABLE_NOT_FOUND",
        ExecutableAlreadyInUse => "NVAPI_EXECUTABLE_ALREADY_IN_USE",
        _ => null,
    };

    /// <summary>官方含义（取自头文件注释），未知数值返回 <c>null</c>。</summary>
    public static string? Describe(int code) => code switch
    {
        Ok => "调用成功。",
        InvalidUserPrivilege => "该 API 需要管理员权限。",
        SettingNotFound => "设置不存在（三态里的「未设置」，不是错误）。",
        ExecutableNotFound => "该名称的应用不在这个 Profile 上。",
        ExecutableAlreadyInUse => "该应用已存在于另一个 Profile 中 —— 换个名字，重试无用。",
        _ => null,
    };

    /// <summary>
    /// 报告用的完整描述。**已知码给出官方名称与含义；未知码明确标注「未在官方头文件中核对到」** ——
    /// 这与「官方没有定义」是两句不同的话，措辞上必须区分开。
    /// </summary>
    public static string Report(int code)
    {
        var name = NameOf(code);
        var describe = Describe(code);

        if (name is not null && describe is not null) return $"{name}（{code}）：{describe}";

        return $"状态码 {code}：未在已核对的官方头文件片段中找到定义 —— 需去官方完整源检索，不得按数值推断语义。";
    }
}
