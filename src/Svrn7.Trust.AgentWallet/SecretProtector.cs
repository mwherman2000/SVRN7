using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// Optional at-rest protection for a small secret — the wallet password, or a
/// key derived from it — so a supervised restart need not re-prompt. This is a
/// convenience cache, never required for operation (docs/AGENTWALLET.md §D15).
///
/// <see cref="Protect"/>/<see cref="Unprotect"/> are inverse. <see cref="Unprotect"/>
/// throws <see cref="CryptographicException"/> if the sealed blob was tampered
/// with or was produced under a different user/machine scope.
/// </summary>
public interface ISecretProtector
{
    /// <summary>False for the no-op protector (unsupported OS). Callers skip caching entirely when this is false.</summary>
    bool Enabled { get; }

    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] sealedBlob);
}

/// <summary>
/// No-op protector for platforms with no wired-up OS keystore. <see cref="Enabled"/>
/// is false; the methods throw if called anyway, so a caller that ignored
/// <see cref="Enabled"/> fails loudly rather than storing plaintext.
/// </summary>
public sealed class NullSecretProtector : ISecretProtector
{
    public bool Enabled => false;

    public byte[] Protect(byte[] plaintext) =>
        throw new PlatformNotSupportedException("No secret protector is available on this platform; check Enabled first.");

    public byte[] Unprotect(byte[] sealedBlob) =>
        throw new PlatformNotSupportedException("No secret protector is available on this platform; check Enabled first.");
}

/// <summary>
/// Windows <see cref="ISecretProtector"/> backed by DPAPI at
/// <see cref="DataProtectionScope.CurrentUser"/> scope, with app-specific
/// entropy so an unrelated app's blanket unprotect pass cannot read the blob.
/// DPAPI authenticates on unprotect, so a tampered blob throws.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Svrn7.Trust.AgentWallet.SecretProtector.v1"u8.ToArray();

    public bool Enabled => true;

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] sealedBlob) =>
        ProtectedData.Unprotect(sealedBlob, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Picks the right <see cref="ISecretProtector"/> for the current machine.
/// Windows → <see cref="DpapiSecretProtector"/>; any other OS →
/// <see cref="NullSecretProtector"/> (Keychain / systemd-creds / libsecret
/// implementations are deferred — docs/AGENTWALLET.md §D15).
/// </summary>
public static class SecretProtectors
{
    public static ISecretProtector CreateDefault() =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new NullSecretProtector();
}
