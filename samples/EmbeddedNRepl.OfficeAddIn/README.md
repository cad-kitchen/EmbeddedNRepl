# Microsoft Word and Excel: embedded ClojureCLR nREPL

This sample loads ClojureCLR and EmbeddedNRepl **inside Word or Excel**. Connect an
nREPL client to that application's loopback endpoint and evaluate Clojure that
reads or changes the active document or worksheet. Word and Excel have separate
processes, runtimes and ports.

The project builds one sample DLL with two COM add-in entry points. It does not
produce another NuGet package. The library remains the single `EmbeddedNRepl`
package and has no dependency on Office.

## Requirements

- Windows and desktop Microsoft Word or Excel. Office for the web and macOS cannot
  load these COM add-ins.
- .NET Framework 4.7.2 or later and the .NET SDK selected by the repository's
  `global.json`. The SDK builds the example; .NET Framework runs it inside Office.
- PowerShell for setup. The optional end-to-end checks use Windows PowerShell 5.1.

Neither VSTO nor Visual Studio is required. The sample uses late-bound Office COM
calls, so Office PIAs are not needed to build it. The DLL is AnyCPU; choose the
registry view that matches **Office**, which can differ from Windows' bitness.
Find it under File > Account > About Word / About Excel.

Verified locally with Word and Excel from Office LTSC Professional Plus 2021,
64-bit, version `16.0.14334.20468`, and Clojure `1.12.6`. The registration
script also supports 32-bit Office; that configuration has not been exercised here.

## Build and register

Run these commands from the repository root, with Word and Excel closed:

```powershell
dotnet restore samples/EmbeddedNRepl.OfficeAddIn/EmbeddedNRepl.OfficeAddIn.csproj --locked-mode
dotnet build samples/EmbeddedNRepl.OfficeAddIn/EmbeddedNRepl.OfficeAddIn.csproj -c Release --no-restore
./samples/EmbeddedNRepl.OfficeAddIn/Set-OfficeRegistration.ps1 -OfficeBitness x64
```

Use `-OfficeBitness x86` for 32-bit Office. Add `-Application Word` or
`-Application Excel` to register just one example. Registration is for the current
user only; administrator rights are not required. It writes the sample's ProgID,
CLSID and Office Addins entries under `HKEY_CURRENT_USER`.

By default the sample loads on demand (`LoadBehavior=8`). Open Office normally,
then go to File > Options > Add-ins > Manage: COM Add-ins > Go. Enable
**EmbeddedNRepl Word sample** or **EmbeddedNRepl Excel sample**. To enable startup
loading instead, close Office and rerun registration with `-LoadAtStartup`:

```powershell
./samples/EmbeddedNRepl.OfficeAddIn/Set-OfficeRegistration.ps1 -OfficeBitness x64 -LoadAtStartup
```

Loading the add-in enables unauthenticated code execution on `127.0.0.1` for local
development. Use it with trusted clients. No firewall changes or external network
binding are needed.

Keep the **entire** `bin/Release/net472` directory together: it includes the sample,
EmbeddedNRepl, ClojureCLR and their dependencies. Close Office before rebuilding.
If moving the DLL, unregister the old location first and register the new one with
`-AssemblyPath <absolute-path-to-EmbeddedNRepl.OfficeAddIn.dll>`. Copying the sample
DLL alone is insufficient. This is a development sample, not a production installer.

Within this repository the sample uses a `ProjectReference` to the library. In an
independent application replace that reference with `PackageReference` to
`EmbeddedNRepl` from your package feed, and retain the application's `Clojure`
reference. Nothing needs to be added to the core library to support Office.

## Find the endpoint and connect

Each add-in requests a free port, avoiding conflicts between Word, Excel and
multiple application instances. After initialization it writes the actual port to:

```text
%LOCALAPPDATA%\EmbeddedNRepl\Office\word-<process-id>.port
%LOCALAPPDATA%\EmbeddedNRepl\Office\excel-<process-id>.port
```

The neighboring `.log` file records startup, the owner thread and shutdown. Find
the process ID and read the matching file, for example:

```powershell
Get-Process WINWORD,EXCEL -ErrorAction SilentlyContinue | Select-Object Id,ProcessName
Get-ChildItem "$env:LOCALAPPDATA/EmbeddedNRepl/Office/*.port"
# Replace 12345 with the Word process ID listed above.
$wordPort = [int](Get-Content "$env:LOCALAPPDATA/EmbeddedNRepl/Office/word-12345.port")
```

Connect an nREPL client to `127.0.0.1:<port>`. The repository also provides a small
PowerShell client, so the examples below require no editor extension or JVM:

```powershell
./samples/EmbeddedNRepl.OfficeAddIn/Invoke-Repl.ps1 -Port $wordPort -Code '(+ 1 2)'
# 3
```

`Invoke-Repl.ps1` sends one evaluation per connection and prints values/output. It
does not create a persistent session. For binding/history persistence use a client
that supports nREPL `clone` and `session`. The supported operations are `describe`,
`clone`, `close`, `ls-sessions` and `eval`; full JVM CIDER middleware is not included.

## Word example

Enable the Word add-in and use its port. These forms create a new unsaved document,
append text and read the resulting content:

```clojure
(word/new-document!)
;; => document name, e.g. "Document1"

(word/append! "Hello from EmbeddedNRepl!\r\n")
(word/append! (str "2 + 3 = " (+ 2 3) "\r\n"))
(word/text)
```

The same example from PowerShell:

```powershell
./samples/EmbeddedNRepl.OfficeAddIn/Invoke-Repl.ps1 -Port $wordPort -Code '(do (word/new-document!) (word/append! "Hello from EmbeddedNRepl!\r\n") (word/text))'
```

`word/append!` adds text to the end of the active document, independently of the
selection. `word/text` returns Word's content text, including its paragraph marks.
Reading or appending without an active document produces an evaluation error.
Save the document normally in Word when you want to retain it.

## Excel example

Enable the Excel add-in and read its own port file:

```powershell
# Replace 23456 with the Excel process ID.
$excelPort = [int](Get-Content "$env:LOCALAPPDATA/EmbeddedNRepl/Office/excel-23456.port")
```

Evaluate these forms in the Excel connection:

```clojure
(excel/new-workbook!)
;; => workbook name, e.g. "Book1"

(excel/set-value! "A1" 20.0)
(excel/set-value! "A2" 22.0)
(excel/set-formula! "A3" "=SUM(A1:A2)")
(excel/value "A3")
;; => 42.0

(excel/set-value! "B1" "Calculated from ClojureCLR")
(excel/value "B1")
```

Or run the complete calculation from PowerShell:

```powershell
./samples/EmbeddedNRepl.OfficeAddIn/Invoke-Repl.ps1 -Port $excelPort -Code '(do (excel/new-workbook!) (excel/set-value! "A1" 20.0) (excel/set-value! "A2" 22.0) (excel/set-formula! "A3" "=SUM(A1:A2)") (excel/value "A3"))'
# 42.0
```

The helpers operate on the active worksheet and accept one cell at a time; a range
such as `A1:A3` is rejected. Values use Excel's `Value2`; numeric results are
typically doubles. Formula writes use `Formula`, with English function names such
as `SUM`, and explicitly calculate the target cell. A chart sheet or no active
worksheet produces an evaluation error. Save the workbook normally in Excel.

## How embedding works

| File | Responsibility |
| --- | --- |
| `OfficeAddIns.cs` | Word/Excel COM identities and `IDTExtensibility2` lifecycle |
| `OfficeReplHost.cs` | Initialize ClojureCLR, attach the adapter, start/stop TCP and pump the owner queue |
| `OfficeApi.cs` | Guard the Office thread, perform COM calls, return managed values |
| `word.cljr`, `excel.cljr` | Small Clojure APIs exposed to the client |
| `Set-OfficeRegistration.ps1` | Register/unregister only these sample add-ins |
| `Invoke-Repl.ps1` | Minimal loopback nREPL client |
| `Test-Office.ps1` | Opt-in checks against installed Word or Excel |

Office supplies the running `Application` object to `OnConnection`. During normal
startup the host waits for `OnStartupComplete`; enabling an add-in in an already
running Office application starts it immediately. Initialization and
`ClojureAdapter.Attach` run on that same main STA thread.

TCP requests enqueue work in `RuntimeCoordinator`. A Windows Forms timer pumps
the queue on the Office thread. The Clojure evaluator and printer execute there
synchronously; the coordinator prevents nested dispatch from running another
evaluation while the current one owns the runtime. A dedicated worker scheduler
would not provide the required thread for these Office calls.

The wrapper checks the owner thread before touching COM. `future`, tasks or other
background threads must not call `word/*` or `excel/*` helpers. For example,
`@(future (word/text))` fails instead of silently making an off-thread Office call.
Keep evaluations short: a long calculation occupies Office's UI thread, and modal
dialogs or cell editing can delay the queue. A client timeout does not undo edits
or forcibly stop code that is already executing.

The context belongs to the application, not a fixed document. The **active**
document/sheet at execution time is the target. If the user switches windows
between requests, subsequent requests target the newly active object. For an
integration that must pin a document, add document-specific context invalidation
and validation before exposing editing commands.

The sample releases COM references it acquires locally and returns strings,
numbers or null, avoiding COM objects in REPL history and printing. It does not
release Office's supplied `Application` reference with `FinalReleaseComObject`.
Edits are direct Office edits; the sample does not add transactions or save files.

Disconnection invalidates the application context, stops the timer, cancels queued
work, detaches the wrapper and closes the listener. Its `.port` file is removed.
ClojureCLR stays loaded in Office; re-enabling the add-in attaches again and starts
a new endpoint. Cancellation remains cooperative. The host does not unload the
runtime or abort a running evaluation during shutdown.

## Verify with real Office

Register the sample, then close all instances of the application being tested.
From the repository root:

```powershell
powershell.exe -NoProfile -STA -File ./samples/EmbeddedNRepl.OfficeAddIn/Test-Office.ps1 -Application Word
powershell.exe -NoProfile -STA -File ./samples/EmbeddedNRepl.OfficeAddIn/Test-Office.ps1 -Application Excel
```

Each check starts a separate Office process normally, waits for its port, verifies
the process and STA owner thread, creates disposable content and tests background
access rejection. It then disconnects/reconnects the add-in, verifies the old
listener is closed and the document content is retained, and closes its own Office
instance without saving. Startup loading is enabled for the duration of the check;
the original `LoadBehavior` is restored in `finally`, including after a failure.
Do not use that test instance for other work. Normal
solution builds do not launch Office or modify registration.

The scripts deliberately use normal Office startup: creating an application solely
through COM automation can omit registered add-ins. The test verifies the PID
before using the automation object for cleanup.

## Stop, unregister and troubleshoot

Disable the sample in the COM Add-ins dialog to stop nREPL immediately. After
disconnecting, reread the port file when re-enabling; the port can change. To remove
the sample registration completely, close Office and run:

```powershell
./samples/EmbeddedNRepl.OfficeAddIn/Set-OfficeRegistration.ps1 -OfficeBitness x64 -Unregister
```

Use the same bitness, application selection and DLL location as registration. This
removes only the sample's Office Addins, ProgID and CLSID entries; it does not
delete the build directory, logs or any documents.

- **No add-in in the list:** check Office bitness and the account used for setup.
  Run Office normally and refresh/reopen the COM Add-ins dialog.
- **Add-in does not load / no port file:** inspect the matching `.log`, confirm all
  DLL dependencies remain together, and check File > Options > Add-ins > Disabled
  Items. Office may disable an add-in after a crash; re-enable this sample through
  the Office UI once its cause has been corrected. Do not disable Office security
  policies to load it.
- **No log at all:** failure occurred before managed initialization. Check the DLL
  path, .NET Framework installation, COM registration and Office's load error.
- **Connection refused:** a port file left after a process crash is stale. Match
  it to a currently running PID, restart the add-in, and reread the port.
- **Requests wait:** leave modal dialogs or Excel cell editing, then retry. Avoid
  retrying edits blindly if a previous evaluation may still be running.
- **Assembly/version conflicts with another managed add-in:** the sample uses the
  Office process's .NET Framework runtime. Production hosts should define their
  own dependency loading and isolation strategy.

Microsoft references: [Office threading](https://learn.microsoft.com/en-us/visualstudio/vsto/threading-support-in-office?view=visualstudio),
[COM add-in lifecycle and registration](https://learn.microsoft.com/en-us/previous-versions/office/troubleshoot/office-developer/office-com-add-in-using-visual-c),
[registering .NET Framework assemblies with COM](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/registering-assemblies-with-com).
