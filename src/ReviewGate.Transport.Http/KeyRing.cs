using System.Text.RegularExpressions;
using ReviewGate.Contracts;

namespace ReviewGate.Transport.Http;

// Named keys per provider over a raw vault. Targets:
//   {prefix}/{provider}/key/{id}  – one key; the vault user name holds the display name
//   {prefix}/{provider}/active    – the active key id (not secret)
//   {prefix}/{provider}           – a key saved by v0.2 (before names); listed as "기존 키" and never moved silently
public sealed class KeyRing(ICredentialVault vault, string prefix = "ReviewGate/live") : ICredentialStore
{
    public const string LegacyId = "legacy";
    string Legacy(string provider) => $"{prefix}/{ProviderCatalog.Get(provider).Id}";
    string KeyTarget(string provider, string id) => $"{Legacy(provider)}/key/{id}";
    string Pointer(string provider) => $"{Legacy(provider)}/active";
    public IReadOnlyList<CredentialEntry> List(string provider)
    {
        string keyPrefix = $"{Legacy(provider)}/key/";
        var keys = vault.Enumerate(keyPrefix).Select(x => (Id: x.Target[keyPrefix.Length..], Name: x.UserName, x.Written)).ToList();
        if (vault.Read(Legacy(provider)) is { Length: > 0 }) keys.Insert(0, (LegacyId, "기존 키", null));
        string? active = ActiveId(provider, keys.Select(k => k.Id).ToList());
        return keys.OrderBy(k => k.Written ?? DateTimeOffset.MinValue).Select(k => new CredentialEntry(k.Id, k.Name, k.Id == active, k.Written)).ToList();
    }
    // The pointer wins; without one, a legacy key stays usable and a single named key is used.
    string? ActiveId(string provider, IReadOnlyList<string> ids)
    {
        string? pointed = vault.Read(Pointer(provider));
        if (pointed is not null && ids.Contains(pointed)) return pointed;
        if (ids.Contains(LegacyId)) return LegacyId;
        return ids.Count == 1 ? ids[0] : null;
    }
    public string? Read(string provider)
    {
        var active = List(provider).SingleOrDefault(k => k.Active);
        return active is null ? null : vault.Read(active.Id == LegacyId ? Legacy(provider) : KeyTarget(provider, active.Id));
    }
    public string Add(string provider, string name, string secret)
    {
        name = name.Trim();secret = secret.Trim();
        Data.Require(name.Length is > 0 and <= 64 && !name.Any(char.IsControl), "invalid_key_name");
        Data.Require(secret.Length is > 0 and < 1024 && !secret.Any(char.IsWhiteSpace), "invalid_credential_value");
        Data.Require(List(provider).All(k => !k.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), "duplicate_key_name");
        string id = "k" + Guid.NewGuid().ToString("N")[..10];
        vault.Write(KeyTarget(provider, id), name, secret);
        // The newest key becomes active so that "add a key" immediately takes effect.
        vault.Write(Pointer(provider), "active", id);
        return id;
    }
    public bool Remove(string provider, string id)
    {
        Data.Require(Regex.IsMatch(id, "^[a-z0-9]{1,32}$"), "unknown_key");
        bool removed = vault.Delete(id == LegacyId ? Legacy(provider) : KeyTarget(provider, id));
        if (vault.Read(Pointer(provider)) == id) vault.Delete(Pointer(provider));
        return removed;
    }
    public void Activate(string provider, string id)
    {
        Data.Require(List(provider).Any(k => k.Id == id), "unknown_key");
        vault.Write(Pointer(provider), "active", id);
    }
}
