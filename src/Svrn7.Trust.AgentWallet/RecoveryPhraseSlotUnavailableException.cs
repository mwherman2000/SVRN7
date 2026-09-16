namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// A recovery-phrase-based unlock or password reset was attempted against a
/// wallet with no <c>"recoveryPhrase"</c> key slot — either a pre-DEK-envelope
/// (v1) wallet that has not yet been migrated by a successful normal password
/// unlock, or (should not occur via <see cref="AgentWalletService.Create"/>,
/// which always stores a phrase) a wallet with none stored.
/// </summary>
public sealed class RecoveryPhraseSlotUnavailableException : InvalidOperationException
{
    public RecoveryPhraseSlotUnavailableException()
        : base("This wallet does not support recovery-phrase unlock yet. Unlock it once with the password to enable it.")
    {
    }
}
