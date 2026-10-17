using System.Runtime.InteropServices;
using NUnit.Framework;

[SetUpFixture]
public sealed class RuntimeReport
{
    [OneTimeSetUp]
    public void ReportEnvironment()
    {
        TestContext.Progress.WriteLine("ACTUAL_RUNTIME=" + RuntimeInformation.FrameworkDescription);
        TestContext.Progress.WriteLine("PROCESS_ARCHITECTURE=" + RuntimeInformation.ProcessArchitecture);
    }
}
