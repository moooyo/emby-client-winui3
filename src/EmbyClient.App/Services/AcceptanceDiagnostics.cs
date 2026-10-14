namespace EmbyClient.App.Services;

/// <summary>Records code-only failures for explicitly isolated acceptance profiles.</summary>
internal static class AcceptanceDiagnostics
{
    internal static void Trace(string phase)
    {
        var root = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            File.AppendAllText(Path.Combine(root, "ui-review-phases.log"), $"{DateTimeOffset.UtcNow:O} {phase}\n");
        }
        catch (Exception) { }
    }

    internal static void Record(Exception exception, string phase)
    {
        var root = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            File.AppendAllText(Path.Combine(root, "callback-failures.log"),
                $"{DateTimeOffset.UtcNow:O} {phase} {exception.GetType().Name} 0x{exception.HResult:X8}\n{exception.StackTrace}\n");
        }
        catch (Exception) { }
    }
}
