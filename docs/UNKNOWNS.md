# 7Record — Known unknowns

> Gaps get written down **before** implementation, then closed by research with a citation, or by
> an explicitly labelled assumption with its blast radius. Guessing silently is the single most
> expensive failure mode in AI-assisted coding.
>
> States: `OPEN` -> `RESEARCHED` / `ASSUMED` -> `RESOLVED`

| ID | Packet | What we did not know | State | Closed by | Blast radius if wrong |
|---|---|---|---|---|---|
| U-1 | P-1 | Does WinUI 3 `Application.UnhandledException` actually let the app survive, or does the process terminate regardless? | RESOLVED | Research — see below | The whole crash-barrier design |
| U-2 | P-1 | Where can diagnostics be written so that it works in **both** packaged (MSIX) and unpackaged runs? `ApplicationData.Current.LocalFolder` throws when unpackaged. | RESOLVED | Reuse of existing convention — see below | Crash logs silently unwritable in one of the two shipping modes |
| U-3 | P-3 | Can `AppWindow.Closing` await asynchronous shutdown work, or does it force a synchronous decision? | RESEARCHED | Existing code in this repo — see below | Shutdown barrier could deadlock the close path |

---

## U-1 — `Application.UnhandledException` and process survival

**Resolved: the app can survive.** The Windows App SDK 2.0 reference for
`Microsoft.UI.Xaml.Application.UnhandledException` states:

> "Occurs when an exception can be handled by app code, as forwarded from a native-level Windows
> Runtime error. **Apps can mark the occurrence as handled in event data.**"

Source: <https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.unhandledexception?view=windows-app-sdk-2.0>
(moniker `windows-app-sdk-2.0`, retrieved 2026-07-31).

Setting `UnhandledExceptionEventArgs.Handled = true` therefore suppresses the default termination
for exceptions forwarded through the XAML framework — which is exactly the class of fault an
`async void` handler produces, because its exception is rethrown on the XAML
`SynchronizationContext`.

**Deliberate limits of this mechanism, stated rather than assumed:**

- It is a **backstop, not a strategy.** Surviving an arbitrary exception leaves the app in an
  unknown state. P-1 therefore also routes handlers through an explicit guard that catches at a
  known point, where the surrounding state is still understood. The global handler exists to
  guarantee a *recorded* failure, not to make faults acceptable.
- It does **not** catch exceptions from fire-and-forget `Task`s (`_ = FooAsync()`). Those become
  unobserved task exceptions and are swallowed by the .NET Core default. P-1 covers that channel
  separately via `TaskScheduler.UnobservedTaskException`.
- Corrupted-state exceptions and stack overflow are not recoverable by any of this and are out of
  scope.

## U-2 — Diagnostics location across packaged and unpackaged

**Resolved by reuse (Ironclad rule 11), not by new research.** This repository already solved the
identical problem: `SevenRecord.Infrastructure/CameraStudioSettingsStore.cs:19-25` resolves its
settings path as

```csharp
Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "7Record", "Settings", "camera-studio.json");
```

`Environment.SpecialFolder.LocalApplicationData` is a plain Win32 known-folder lookup, so unlike
`ApplicationData.Current.LocalFolder` it does not require package identity and does not throw when
unpackaged. It is already proven in this app across both shipping modes.

**Decision:** diagnostics are written to `%LOCALAPPDATA%\7Record\Diagnostics\`, sitting alongside
the existing `Settings` folder. No new dependency, no new convention, and one obvious place for a
user to find a crash report when we ask them for one.

## U-3 — Awaiting asynchronous work from the window close path

**Researched against this repository's own working code, which is stronger evidence than docs.**
`MainPage.ShutdownAsync()` is already invoked from `AppWindow.Closing` (established in the
Windows Studio Effects work, where camera background-effect restore *had* to complete before the
window went away). The established pattern is: cancel the close, run the asynchronous work, then
close for real once it completes.

P-3 extends that existing mechanism with a job registry rather than inventing a second close
protocol. **Remaining risk, explicitly labelled:** a job that never completes would hang the close.
P-3 must therefore bound the shutdown wait and proceed after the timeout, recording any job that
failed to drain — a hung export must not become an unclosable window.
