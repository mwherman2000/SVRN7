using System;
using System.Linq;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class MnemonicTests
{
    [Fact]
    public void GenerateThenValidate_Succeeds()
    {
        var phrase = Mnemonic.Generate();

        Mnemonic.Validate(phrase); // throws on failure

        Assert.Equal(12, phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Validate_WrongWordCount_Throws()
    {
        var tooFew = string.Join(' ', Enumerable.Repeat("abandon", 11));
        Assert.Throws<FormatException>(() => Mnemonic.Validate(tooFew));
    }

    [Fact]
    public void Validate_UnknownWord_Throws()
    {
        var words = Mnemonic.Generate().Split(' ');
        words[0] = "not-a-bip39-word";
        Assert.Throws<FormatException>(() => Mnemonic.Validate(string.Join(' ', words)));
    }

    [Fact]
    public void Validate_TamperedWord_TripsChecksum()
    {
        // The checksum is only 4 bits, so any single tamper attempt has a
        // ~1/16 chance of coincidentally still validating. Try several
        // independently generated phrases so a false pass across all of
        // them is astronomically unlikely, without hardcoding assumptions
        // about the embedded wordlist's exact ordering.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var words = Mnemonic.Generate().Split(' ');
            if (words[0] == words[1]) continue;
            (words[0], words[1]) = (words[1], words[0]);
            var tampered = string.Join(' ', words);

            try
            {
                Mnemonic.Validate(tampered);
            }
            catch (FormatException)
            {
                return; // checksum correctly rejected a tampered phrase
            }
        }

        Assert.Fail("Expected at least one tampered phrase to fail checksum validation across 20 attempts.");
    }

    [Fact]
    public void ToSeed_IsDeterministic()
    {
        var phrase = Mnemonic.Generate();

        var seed1 = Mnemonic.ToSeed(phrase);
        var seed2 = Mnemonic.ToSeed(phrase);

        Assert.Equal(seed1, seed2);
        Assert.Equal(64, seed1.Length);
    }

    [Fact]
    public void ToSeed_DifferentPassphrase_ProducesDifferentSeed()
    {
        var phrase = Mnemonic.Generate();

        var seedNoPassphrase = Mnemonic.ToSeed(phrase);
        var seedWithPassphrase = Mnemonic.ToSeed(phrase, "extra-passphrase");

        Assert.NotEqual(seedNoPassphrase, seedWithPassphrase);
    }
}
