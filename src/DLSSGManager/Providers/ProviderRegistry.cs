namespace DLSSGManager.Providers;

/// <summary>
/// The patch providers this build knows about.
///
/// Kept deliberately plain — a list plus an id index, no plugin loading, no dynamic assembly
/// resolution, no remote code. Remote JSON may describe a provider; it may never execute one.
///
/// Two rules matter more than the rest:
/// a duplicate id is a hard failure rather than a silent overwrite, and an unknown id returns null
/// rather than quietly resolving to some other provider (a caller asking for MFG must never be
/// handed dlssg_for_sm86).
/// </summary>
public sealed class ProviderRegistry
{
    private readonly List<IPatchProvider> _providers = new();
    private readonly Dictionary<string, IPatchProvider> _byId = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IPatchProvider> All => _providers;

    public int Count => _providers.Count;

    public IReadOnlyList<string> Ids => _providers.Select(p => p.Id).ToList();

    /// <summary>Registers a provider, throwing when the id is unusable or already taken.</summary>
    public void Register(IPatchProvider provider)
    {
        if (!TryRegister(provider, out var error)) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Registers a provider, reporting why it was refused instead of throwing.
    ///
    /// Refusal is deliberate rather than a replace: two providers sharing an id would make every
    /// later lookup ambiguous, and a silent overwrite would hide the mistake.
    /// </summary>
    public bool TryRegister(IPatchProvider? provider, out string error)
    {
        error = "";

        if (provider is null)
        {
            error = "Provider 为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(provider.Id))
        {
            error = "Provider ID 不能为空。";
            return false;
        }

        if (_byId.ContainsKey(provider.Id))
        {
            error = $"Provider ID 重复：{provider.Id}（已注册同 ID 的 Provider，拒绝覆盖）";
            return false;
        }

        _providers.Add(provider);
        _byId[provider.Id] = provider;
        return true;
    }

    /// <summary>
    /// Looks a provider up by id. An unknown id yields null — never another provider.
    /// </summary>
    public IPatchProvider? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _byId.TryGetValue(id, out var provider) ? provider : null;
    }

    public bool Contains(string? id) => Get(id) is not null;

    /// <summary>
    /// Health of one provider, with the call itself isolated: a provider that throws while reporting
    /// its state is reported as <see cref="ProviderHealthState.Broken"/> instead of taking down the
    /// caller that was merely trying to draw a status line.
    /// </summary>
    public ProviderHealth GetHealth(string? id)
    {
        var provider = Get(id);
        if (provider is null)
            return ProviderHealth.Unavailable($"未注册的 Provider：{id ?? "(空)"}");

        try
        {
            return provider.Health ?? ProviderHealth.Broken("Provider 未返回健康状态。");
        }
        catch (Exception ex)
        {
            return ProviderHealth.Broken($"读取健康状态时抛出异常：{ex.Message}");
        }
    }

    /// <summary>Health of every registered provider, each isolated from the others' failures.</summary>
    public IReadOnlyList<(string Id, ProviderHealth Health)> AllHealth() =>
        _providers.Select(p => (p.Id, GetHealth(p.Id))).ToList();

    /// <summary>
    /// The providers this build ships.
    ///
    /// Registration performs no I/O and touches no network, which is what makes the isolation
    /// guarantee real: an unusable provider cannot stop the others from being constructed.
    /// </summary>
    public static ProviderRegistry CreateDefault()
    {
        var registry = new ProviderRegistry();
        registry.Register(new DlssgSm86Provider());
        registry.Register(new MfgSmoothProvider());
        return registry;
    }
}
