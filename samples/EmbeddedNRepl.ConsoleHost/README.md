# Standalone nREPL quickstart

The console host owns one ClojureCLR runtime and one serialized execution thread. It is a runnable example and does not load a CAD application.

```powershell
. ./build/Use-WorkspaceSdk.ps1
& $nreplSdk restore EmbeddedNRepl.sln --configfile NuGet.Config --locked-mode
./build/Start-NRepl.ps1 -Port 7888
```

In another terminal:

```powershell
./build/Invoke-NRepl.ps1 -Port 7888 -Code '(def answer 41) (+ answer 1)'
./build/Invoke-NRepl.ps1 -Port 7888 -Code '(println "Hello") (inc answer)'
```

The endpoint listens only on loopback and has no authentication. It is explicitly enabled developer access and can execute arbitrary code in its host process.

Supported operations are `describe`, `clone`, `close`, `ls-sessions`, and `eval`. Sessions preserve `*ns*`, `*1`, `*2`, `*3`, `*e*`, and printing settings. `*out*`, `*err*`, values, and structured evaluation errors are returned after evaluation. `read-line` sees EOF. Printed values and total collected output are bounded.

An embedding host supplies its initialized runtime, evaluator, session factory, scheduler, current execution context, and identity. The CAD adapters use their existing owner-thread scheduler.
