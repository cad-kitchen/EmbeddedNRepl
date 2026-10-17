using NUnit.Framework;

namespace EmbeddedNRepl.ClientTests;

public sealed class AcceptanceAvailabilityTests
{
    [Test]
    public void RequiredAcceptanceIsUnverified()
        => Assert.Inconclusive("Installed-client eval, session reuse, error/retry, restart and timeout cases are executable. Full acceptance still requires identity-mismatch connection policy, normalized traces and the remaining release gates.");
}
