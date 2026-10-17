using NUnit.Framework;

namespace EmbeddedNRepl.ClojureClr.Tests;

public sealed class AcceptanceAvailabilityTests
{
    [Test]
    public void RequiredAcceptanceIsUnverified()
        => Assert.Inconclusive("Standalone evaluation, output, printing, sessions and cooperative cancellation have executable checks. Acceptance of application-specific host integrations must be verified by each consuming application.");
}
