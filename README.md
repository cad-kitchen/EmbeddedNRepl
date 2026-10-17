# EmbeddedNRepl

An embeddable ClojureCLR nREPL server for .NET applications. This repository builds
one NuGet package, `EmbeddedNRepl`, targeting .NET Standard 2.0.

The library embeds its Clojure implementation but has no compiled dependency on
ClojureCLR, CAD SDKs, WPF or WebView. The application selects and initializes
ClojureCLR, owns its lifetime, and controls the execution thread. Stopping nREPL
does not shut down the application's language runtime.

## Embedding

Reference `EmbeddedNRepl` and a compatible `Clojure` package in the application.
The executable tests use Clojure `1.12.6` on .NET Framework 4.7.2,
.NET 8 and .NET 10. Other runtime/package combinations require verification.

On the initialized runtime's owner thread:

```csharp
using EmbeddedNRepl;
using EmbeddedNRepl.Hosting;

var adapter = ClojureAdapter.Attach(runtimeGroupId,
    source => RT.var("clojure.core", "load-string").invoke(source),
    initialNamespace: "user");
var context = new ApplicationReplContext(runtimeGroupId);
var services = new ReplServices(identity, adapter.Evaluator, adapter.Sessions,
    scheduler, context);
var server = new ReplServer(services);
var endpoint = server.Start(true, new IPEndPoint(IPAddress.Loopback, 7888));
```

`identity` is the application's `ReplHostIdentity`; its runtime group must match
the adapter and context. Create the initial namespace before attaching. The
callback invokes `clojure.core/load-string` in the existing runtime. Attachment
loads the embedded implementation once and preserves owner thread bindings.
Serialize attachment with other work in that runtime.

Use `DedicatedThreadScheduler` when the application can own a dedicated language
thread. Its initialization callback runs on that thread before it accepts work.
For an existing UI/event loop, implement `IReplExecutionScheduler`, or pump
`RuntimeCoordinator` from the host's permitted context. The scheduler's completion
must include actual callback completion and exit from the host execution context.
The dedicated scheduler serializes work but does not install a synchronization
context for arbitrary asynchronous user callbacks; the supplied Clojure evaluator
performs its language work synchronously on the owner thread.

`ApplicationReplContext.Publish` replaces the current snapshot; `Invalidate`
rejects queued work captured before an application context closes. `HostState`
is opaque to the server. The original document-oriented `RuntimeContext` and
five-argument `ReplExecutionContext` remain available for existing hosts.

Shutdown order: await `server.StopAsync()` and inspect its result, then dispose
an application-owned scheduler and await its `Completion`. Cancellation is
cooperative; a running evaluation is not proven stopped by a cancellation request.
Never tear down the runtime while an evaluation or printer is still executing.

Runnable examples:

- [Console host](samples/EmbeddedNRepl.ConsoleHost/README.md): a dedicated runtime thread.
- [Microsoft Word](samples/EmbeddedNRepl.OfficeAddIn/README.md#word-example): create a document, append and read text through nREPL.
- [Microsoft Excel](samples/EmbeddedNRepl.OfficeAddIn/README.md#excel-example): write cells and calculate formulas through nREPL.

The Office guide covers building and registering the add-ins, connecting clients,
UI-thread scheduling, shutdown, verification and removal. Both Office add-ins are
samples in one non-packable project; the library still produces one NuGet package.

## Protocol and access

The server supports `describe`, `clone`, `close`, `ls-sessions`, and `eval`.
Persistent sessions retain Clojure bindings and history. Values and output are
bounded. The existing Calva pprint compatibility is retained; this is not the
complete JVM CIDER middleware suite.

Access is explicitly enabled, unauthenticated loopback TCP for trusted local
development. A connection can execute code in the host process. Hosts decide
when to enable the endpoint and retain their own document/transaction policies.

Protocol extensions use the `embedded-nrepl/` prefix, and `describe` includes
the `embedded-nrepl` version entry. The endpoint profile is
`embedded-nrepl-clojureclr-v1`. The console host reports its bound port as
`EMBEDDED_NREPL_PORT` and its shutdown status as `EMBEDDED_NREPL_STOP`.

## Build and test

Install the SDK pinned in `global.json`; Windows tests also require .NET 8 SDK
8.0.420 and .NET Framework 4.7.2 or later. Build helpers use `dotnet` on PATH,
`artifacts/dependencies/dotnet`, or the executable supplied by `EMBEDDED_NREPL_DOTNET`.
The SDK must also be visible to your editor. In VS Code, a solution-level NuGet
restore failure can mean the pinned SDK is missing from the system installation.
Check `dotnet --version` in this directory and reload the editor after installing it.

```powershell
dotnet restore EmbeddedNRepl.sln --configfile build/ci/NuGet.Config --locked-mode
./build/Run-Tests.ps1 -Layer Independent -Framework net472,net8.0,net10.0
./build/Run-Tests.ps1 -Layer ClojureClr -Filter 'FullyQualifiedName!~AcceptanceAvailabilityTests'
./build/Run-Tests.ps1 -Layer Client -Filter 'FullyQualifiedName~DisposableHostTests'
./build/Run-Coverage.ps1 -Framework net10.0
./build/Pack.ps1
./build/Test-Package.ps1 -Framework net10.0
```

The package consumer check creates an isolated application with a PackageReference,
starts nREPL, evaluates through TCP, stops it, and verifies the runtime still works.
External installed-client checks require `EMBEDDED_NREPL_CLIENT` and Babashka and
remain opt-in. Application-specific acceptance belongs to consuming applications.

`VERSION` controls package releases. Packages are produced in `artifacts/nuget`;
publication is a separate release operation. Published versions are immutable.

Source is MIT licensed; see [LICENSE](LICENSE). Applications distributing ClojureCLR
must retain its dependency license notices separately.
