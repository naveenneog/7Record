using Microsoft.UI.Xaml;
using SevenRecord.Infrastructure.Diagnostics;

namespace SevenRecord.App;

public partial class App : Application
{
    private readonly FileDiagnosticLog _diagnostics = new();

    public App()
    {
        InitializeComponent();

        // Installed before anything else can run. Until these are attached, a fault in
        // startup itself would vanish with no record.
        //
        // Why this is necessary at all: the only UnhandledException subscription this app
        // had came from the generated App.g.i.cs, and it is wrapped in `#if DEBUG` and only
        // acts when a debugger is attached. A Release build therefore had *no* handler, so
        // any exception escaping one of the UI layer's async void handlers terminated the
        // process silently - and took the in-progress recording with it.
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _diagnostics.Write(
            DiagnosticSeverity.Info,
            nameof(App),
            "7Record started.");
    }

    public Window MainWindow { get; private set; } = null!;

    /// <summary>
    /// The process-wide diagnostic sink. Shared so that every layer records to one file
    /// rather than inventing its own.
    /// </summary>
    public IDiagnosticLog Diagnostics => _diagnostics;

    /// <summary>Where a user can find the logs when support asks for them.</summary>
    public static string DiagnosticsDirectory => FileDiagnosticLog.DefaultDirectory;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Set FIRST, before anything that could itself fail. If logging threw here and
        // this had not run yet, the process would terminate - handing back the exact
        // crash this handler exists to prevent, on the exact path it exists to protect.
        //
        // Documented behaviour: "Apps can mark the occurrence as handled in event data."
        // learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.unhandledexception
        //
        // For a screen recorder this trade is not close. Staying alive in a possibly
        // degraded state lets the user stop the recording and keep their file; terminating
        // guarantees they lose it. See docs/UNKNOWNS.md U-1 for the limits of this.
        e.Handled = true;

        _diagnostics.Write(
            DiagnosticSeverity.Fault,
            "Application.UnhandledException",
            "An exception reached the XAML framework and was contained.",
            e.Exception);
    }

    private void OnDomainUnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs e) =>
        // Last chance. This one cannot be cancelled - the process is going down either
        // way - so the only job here is to make sure it does not go down silently.
        _diagnostics.Write(
            DiagnosticSeverity.Fault,
            "AppDomain.UnhandledException",
            e.IsTerminating
                ? "The process is terminating on an unhandled exception."
                : "An unhandled exception escaped a background thread.",
            e.ExceptionObject as Exception);

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        // Also set first, and for a sharper reason: this event is raised on the finalizer
        // thread, so an exception escaping here is an immediate unhandled crash that would
        // additionally skip SetObserved.
        e.SetObserved();

        // Fire-and-forget work (`_ = SomethingAsync()`) never reaches the XAML handler.
        // .NET discards these by default, which is how a failed export or a failed
        // post-processing run can currently disappear without a trace.
        _diagnostics.Write(
            DiagnosticSeverity.Fault,
            "TaskScheduler.UnobservedTaskException",
            "A fire-and-forget task faulted and was never observed.",
            e.Exception);
    }
}
