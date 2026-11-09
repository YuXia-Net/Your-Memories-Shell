using Microsoft.UI.Xaml;

namespace MemoryShell;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        UnhandledException += (_, e) => LogFailure(e.Exception);
        L10n.Initialize();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (!L10n.HasPreference)
            {
                window = new FirstLanguageWindow(() =>
                {
                    window = new MainWindow();
                    window.Activate();
                });
                window.Activate();
                return;
            }
            window = new MainWindow();
            window.Activate();
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            throw;
        }
    }

    private static void LogFailure(Exception exception)
    {
        try
        {
            var folder = Path.Combine(WorkspaceStore.Folder, "..", "Logs");
            Directory.CreateDirectory(folder);
            var lines = new List<string> { DateTimeOffset.Now.ToString("O") };
            for (var failure = exception; failure is not null; failure = failure.InnerException)
            {
                lines.Add($"{failure.GetType().FullName} 0x{failure.HResult:X8}");
                lines.Add(failure.StackTrace ?? "");
            }
            File.WriteAllLines(Path.Combine(folder, "startup-error.log"), lines);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
