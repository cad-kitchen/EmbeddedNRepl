using System;

namespace EmbeddedNRepl.Hosting;

public sealed class ReplHostIdentity
{
    public ReplHostIdentity(string productId, string buildId, string hostSessionId, string runtimeGroupId,
        string hostProduct, string hostVersion, string hostBuild, string adapterTarget,
        string adapterVersion, string languageVersion)
    {
        ProductId = productId;
        BuildId = buildId;
        HostSessionId = hostSessionId;
        RuntimeGroupId = runtimeGroupId;
        HostProduct = hostProduct;
        HostVersion = hostVersion;
        HostBuild = hostBuild;
        AdapterTarget = adapterTarget;
        AdapterVersion = adapterVersion;
        LanguageVersion = languageVersion;
    }
    public string ProductId
    {
        get;
    }
    public string BuildId
    {
        get;
    }
    public string HostSessionId
    {
        get;
    }
    public string RuntimeGroupId
    {
        get;
    }
    public string HostProduct
    {
        get;
    }
    public string HostVersion
    {
        get;
    }
    public string HostBuild
    {
        get;
    }
    public string AdapterTarget
    {
        get;
    }
    public string AdapterVersion
    {
        get;
    }
    public string LanguageVersion
    {
        get;
    }
}

public sealed class ReplServices
{
    public ReplServices(ReplHostIdentity identity, IReplEvaluator evaluator,
        IReplSessionStateFactory sessions, IReplExecutionScheduler scheduler, IReplHostContext host)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        Sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        Host = host ?? throw new ArgumentNullException(nameof(host));
    }
    public ReplHostIdentity Identity
    {
        get;
    }
    public IReplEvaluator Evaluator
    {
        get;
    }
    public IReplSessionStateFactory Sessions
    {
        get;
    }
    public IReplExecutionScheduler Scheduler
    {
        get;
    }
    public IReplHostContext Host
    {
        get;
    }
}
