using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class PinStoresFactoryTests
{
    [Fact]
    public void CreateDefault_AlwaysReturnsAUsableStore()
    {
        var result = PinStores.CreateDefault();

        Assert.NotNull(result.Store);

        // Fail-open contract: if a real store could not be opened, the caller
        // gets a disabled (no-op) store plus the reason, never an exception.
        if (result.UnavailableReason is not null)
            Assert.False(result.Store.Enabled);
    }
}
