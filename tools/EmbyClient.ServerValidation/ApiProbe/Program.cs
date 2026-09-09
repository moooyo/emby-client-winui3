using System.Text.Json;

namespace EmbyClient.ServerValidation.ApiProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("Usage: ApiProbe --credentials-file <path> [--output <path>] [--item-id <id>] [--expected-version 4.9.5.0]");
            Console.WriteLine("Only http://127.0.0.1:19096 is allowed. Credentials: {ServerUrl, Username, Password}. Results never assert native UI playback.");
            return 0;
        }

        Options options;
        Credentials credentials;
        try
        {
            options = Options.Parse(args);
            var info = new FileInfo(options.CredentialsFile);
            if (!info.Exists || info.Length > 65536) throw new ArgumentException();
            await using var input = File.OpenRead(info.FullName);
            credentials = await JsonSerializer.DeserializeAsync(input, ProbeJsonContext.Default.Credentials)
                ?? throw new ArgumentException();
            if (string.IsNullOrWhiteSpace(credentials.Username) || credentials.Password is null
                || !Uri.TryCreate(credentials.ServerUrl, UriKind.Absolute, out var server)
                || server.Scheme != "http" || server.Host != "127.0.0.1" || server.Port != 19096
                || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0
                || server.AbsolutePath is not ("/" or "/emby" or "/emby/"))
                throw new ArgumentException();
            if (Path.GetFullPath(options.OutputFile).Equals(info.FullName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException();
        }
        catch
        {
            Console.Error.WriteLine("INVALID_INPUT: Supply a readable credentials JSON file and the allowed loopback endpoint. Use --help for syntax. No credential contents were logged.");
            return 2;
        }

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };
        var probe = new ApiContractProbe(options, credentials);
        var report = await probe.RunAsync(lifetime.Token);
        try
        {
            var output = Path.GetFullPath(options.OutputFile);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var stream = File.Create(output);
            await JsonSerializer.SerializeAsync(stream, report, ProbeJsonContext.Default.ProbeReport);
        }
        catch
        {
            Console.Error.WriteLine("REPORT_WRITE_FAILED: The sanitized report could not be written.");
            return 1;
        }
        Console.WriteLine($"{report.Outcome}: {report.Steps.Count(step => step.Status == "Passed")} passed, {report.Steps.Count(step => step.Status == "Failed")} failed, {report.Steps.Count(step => step.Status == "Blocked")} blocked. Native UI and first frame: NOT RUN.");
        return report.Outcome == "Passed" ? 0 : 1;
    }
}

internal sealed record Options(string CredentialsFile, string OutputFile, string? ItemId, string ExpectedVersion)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--credentials-file" or "--output" or "--item-id" or "--expected-version")
                || !values.TryAdd(args[index], args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException();
        }
        if (!values.TryGetValue("--credentials-file", out var credentials)) throw new ArgumentException();
        var expected = values.GetValueOrDefault("--expected-version", "4.9.5.0");
        if (!Version.TryParse(expected, out var version) || version.Revision < 0) throw new ArgumentException();
        var output = values.GetValueOrDefault("--output")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(credentials))!, "api-probe-report.json");
        return new(credentials, output, values.GetValueOrDefault("--item-id"), expected);
    }
}
