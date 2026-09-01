using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// Windows <see cref="IPinStore"/> backed by DPAPI at
/// <see cref="DataProtectionScope.CurrentUser"/> scope.
///
/// The pin file is a DPAPI-protected JSON map of walletId -> base64(SHA-256
/// of the SPKI public key). DPAPI seals it to the logged-in Windows
/// account: a process not running as that user can neither read it nor
/// forge a valid replacement, and <see cref="ProtectedData.Unprotect"/>
/// authenticates -- a tampered blob throws rather than returning altered
/// bytes -- so no separate MAC is needed.
///
/// The file lives under %LOCALAPPDATA%, deliberately NOT next to
/// <c>wallet.json</c>, so an attacker overwriting everything in the
/// wallet's directory doesn't also land on the pin store.
///
/// FAIL-OPEN, LOUDLY: if the pin file can't be decrypted (corrupted,
/// truncated, or written by a different Windows user), the constructor
/// throws <see cref="InvalidDataException"/>. The caller
/// (<c>Program.CreatePinStore</c>) catches that, prints a warning, and
/// continues this session with pinning disabled rather than bricking the
/// CLI over a non-secret file -- the pin store holds only public-key
/// hashes. This mirrors <see cref="UnlockThrottle"/>'s stance that a
/// corrupted sidecar must never lock a user out of their own wallet.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiPinStore : IPinStore
{
    // Static, app-specific entropy. Not a secret (it ships in the binary),
    // but it scopes the DPAPI blob to KeyWallet so a generic "unprotect
    // everything for this user" pass by another app doesn't read it.
    private static readonly byte[] Entropy = "KeyWallet.PinStore.v1"u8.ToArray();

    private readonly string _path;
    private readonly Dictionary<string, string> _pins;

    public bool Enabled => true;

    public DpapiPinStore(string path)
    {
        _path = path;
        _pins = Read(path);
    }

    /// <summary>%LOCALAPPDATA%\KeyWallet\pin-store.bin</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KeyWallet",
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
            // Unlike the throttle sidecar, this does NOT silently fall back
            // to "nothing pinned" -- a pin store we can't verify must be
            // surfaced, not quietly ignored.
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

            // Same temp-file-then-swap shape as WalletFile.Save: a crash
            // mid-write can't leave a half-written pin store.
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
