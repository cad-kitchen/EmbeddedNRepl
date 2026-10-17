using System.Linq;
using EmbeddedNRepl;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class DependencyBoundaryTests
{
    [Test]
    public void SharedLibraryReferencesOnlyTheStandardLibrary()
    {
        var references = typeof(ReplLimits).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.That(references, Is.EquivalentTo(new[] { "netstandard" }));
    }
}
