using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// Windows <see cref="IPinStore"/> backed by DPAPI at
/// <see cref="DataProtectionScope.CurrentUser"/> scope. Copied from
/// <c>Svrn7.Trust.KeyWallet.DpapiPinStore</c> with the entropy tag and default
/// path re-scoped to AgentWallet (docs/AGENTWALLET.md §3, §5).
///
/// The pin file is a DPAPI-protected JSON map of walletId → base64(SHA-256 of
/// the secp256k1 compressed public key). DPAPI seals it to the logged-in Windows
/// account and authenticates on unprotect, so no separate MAC is needed. It
/// lives under %LOCALAPPDATA%, deliberately not next to the wallet file.
///
/// Fail-open, loudly: if the pin file cannot be decrypted (corrupted, truncated,
/// or written by a different Windows user), the constructor throws
/// <see cref="InvalidDataException"/>. The caller catches that, warns, and
/// continues with pinning disabled — the pin store holds only public-key hashes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiPinStore : IPinStore
{
    // Static, app-specific entropy. Not a secret (it ships in the binary), but it
    // scopes the DPAPI blob to AgentWallet so a generic "unprotect everything for
    // this user" pass by another app does not read it.
    private static readonly byte[] Entropy = "Svrn7.Trust.AgentWallet.PinStore.v1"u8.ToArray();

    private readonly string _path;
    private readonly Dictionary<string, string> _pins;

    public bool Enabled => true;

    public DpapiPinStore(string path)
    {
        _path = path;
        _pins = Read(path);
    }

    /// <summary>%LOCALAPPDATA%\Web7Pando\AgentWallet\pin-store.bin</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Web7Pando",
        "AgentWallet",
        "pin-store.bin");

    public byte[]? TryGet(string walletId) =>
        _pins.TryGetValue(walletId, out var b64) ? Convert.FromBase64String(b64) : null;

    public void Set(string walletId, byte[] publicKeyPin)
    {
        _pins[walletId] = Convert.ToBase64String(publicKeyPin);
        Write();
    }

    public void Remove(string walletId)
    {
        if (_pins.Remove(walletId)) Write();
    }

    private static Dictionary<string, string> Read(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>();

        try
        {
            var blob = File.ReadAllBytes(path);
            var clear = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(clear)
                    ?? new Dictionary<string, string>();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidDataException(
                $"Pin store at '{path}' could not be read (corrupted, or created by a different Windows user).", ex);
        }
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var clear = JsonSerializer.SerializeToUtf8Bytes(_pins);
        try
        {
            var blob = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);

            var tempPath = _path + ".tmp";
            File.WriteAllBytes(tempPath, blob);
            if (File.Exists(_path))
                File.Replace(tempPath, _path, null);
            else
                File.Move(tempPath, _path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }
}
