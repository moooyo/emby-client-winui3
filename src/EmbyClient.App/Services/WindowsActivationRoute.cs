namespace EmbyClient.App.Services;

public enum WindowsActivationDestination { Home, Library, Search, ContinueWatching, Settings, SwitchAccount }

/// <summary>Contains only a known destination and account-scoped media identity, never credentials or URLs.</summary>
public sealed record WindowsActivationRoute(WindowsActivationDestination Destination,
    string? AccountKey = null, string? ItemId = null)
{
    public const string ProtocolScheme = "lumen-emby";
    private const int MaximumArguments = 6;

    public bool IsValid => Destination is WindowsActivationDestination.ContinueWatching
        ? IsAccountKey(AccountKey) && IsItemId(ItemId)
        : Destination is WindowsActivationDestination.Home or WindowsActivationDestination.Library
            or WindowsActivationDestination.Search or WindowsActivationDestination.Settings
            or WindowsActivationDestination.SwitchAccount && AccountKey is null && ItemId is null;

    public bool MatchesAccount(string? accountKey) => Destination != WindowsActivationDestination.ContinueWatching
        || string.Equals(AccountKey, accountKey, StringComparison.OrdinalIgnoreCase);

    public string ToArguments()
    {
        if (!IsValid) throw new InvalidOperationException("The activation route is invalid.");
        var destination = Destination switch
        {
            WindowsActivationDestination.Library => "library",
            WindowsActivationDestination.Search => "search",
            WindowsActivationDestination.ContinueWatching => "continue",
            WindowsActivationDestination.Settings => "settings",
            WindowsActivationDestination.SwitchAccount => "switch-account",
            _ => "home"
        };
        return Destination == WindowsActivationDestination.ContinueWatching
            ? $"--lumen-route {destination} --lumen-account {AccountKey!.ToUpperInvariant()} --lumen-item {ItemId}"
            : $"--lumen-route {destination}";
    }

    /// <summary>Parses the complete argument list after the executable. Unknown or duplicate fields are rejected.</summary>
    public static bool TryParseArguments(IReadOnlyList<string> arguments, out WindowsActivationRoute? route)
    {
        route = null;
        if (arguments.Count is < 2 or > MaximumArguments || arguments.Count % 2 != 0) return false;
        string? destination = null;
        string? accountKey = null;
        string? itemId = null;
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var value = arguments[index + 1];
            switch (arguments[index])
            {
                case "--lumen-route" when destination is null: destination = value; break;
                case "--lumen-account" when accountKey is null: accountKey = value; break;
                case "--lumen-item" when itemId is null: itemId = value; break;
                default: return false;
            }
        }
        var target = destination switch
        {
            "home" => WindowsActivationDestination.Home,
            "library" => WindowsActivationDestination.Library,
            "search" => WindowsActivationDestination.Search,
            "continue" => WindowsActivationDestination.ContinueWatching,
            "settings" => WindowsActivationDestination.Settings,
            "switch-account" => WindowsActivationDestination.SwitchAccount,
            _ => (WindowsActivationDestination?)null
        };
        if (!target.HasValue) return false;
        if (accountKey is not null && !IsAccountKey(accountKey)) return false;
        var parsed = new WindowsActivationRoute(target.Value, accountKey?.ToUpperInvariant(), itemId);
        if (!parsed.IsValid) return false;
        route = parsed;
        return true;
    }

    /// <summary>Handles a registered protocol without accepting arbitrary server addresses or query fields.</summary>
    public static bool TryParseUri(Uri uri, out WindowsActivationRoute? route)
    {
        route = null;
        if (!uri.IsAbsoluteUri || uri.AbsoluteUri.Length > 1024 || !string.Equals(uri.Scheme, ProtocolScheme, StringComparison.Ordinal)
            || uri.Host != "open" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort) return false;
        var arguments = new List<string> { "--lumen-route", uri.AbsolutePath.Trim('/') };
        if (uri.Query.Length > 1)
        {
            foreach (var parameter in uri.Query[1..].Split('&'))
            {
                var split = parameter.IndexOf('=');
                if (split <= 0) return false;
                var key = parameter[..split] switch
                {
                    "account" => "--lumen-account",
                    "item" => "--lumen-item",
                    _ => null
                };
                if (key is null) return false;
                arguments.Add(key);
                arguments.Add(Uri.UnescapeDataString(parameter[(split + 1)..]));
            }
        }
        return TryParseArguments(arguments, out route);
    }

    public static bool IsAccountKey(string? value) => value is { Length: 64 }
        && value.All(char.IsAsciiHexDigit);

    public static bool IsItemId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
