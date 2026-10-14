using System.Text;
using System.Text.Json;

namespace EmbyClient.Api;

/// <summary>Provides an immutable snapshot of a server's library invalidation message.</summary>
public sealed class LibraryChangedEventArgs : EventArgs
{
    internal LibraryChangedEventArgs(string? messageId, IReadOnlyList<string> itemsAdded,
        IReadOnlyList<string> itemsUpdated, IReadOnlyList<string> itemsRemoved, bool isTruncated)
    {
        MessageId = messageId;
        ItemsAdded = itemsAdded;
        ItemsUpdated = itemsUpdated;
        ItemsRemoved = itemsRemoved;
        IsTruncated = isTruncated;
    }

    public string? MessageId { get; }
    public IReadOnlyList<string> ItemsAdded { get; }
    public IReadOnlyList<string> ItemsUpdated { get; }
    public IReadOnlyList<string> ItemsRemoved { get; }
    public bool IsTruncated { get; }
    public bool HasChanges => ItemsAdded.Count > 0 || ItemsUpdated.Count > 0 || ItemsRemoved.Count > 0;
}

/// <summary>Parses complete UTF-8 server event messages without JSON reflection.</summary>
/// <remarks>
/// The envelope follows https://dev.emby.media/doc/restapi/Web-Socket.html.
/// LibraryChanged data uses the string arrays described by
/// https://dev.emby.media/reference/pluginapi/MediaBrowser.Model.Entities.LibraryUpdateInfo.html.
/// The caller must reassemble WebSocket fragments before parsing a message.
/// </remarks>
public static class ServerEventProtocol
{
    public const int MaximumMessageBytes = 256 * 1024;
    public const int MaximumItemsPerCategory = 512;
    private const int MaximumIdentifierLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static LibraryChangedEventArgs? ParseLibraryChanged(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumMessageBytes) throw InvalidMessage();
        try
        {
            // Validate encoding before JsonDocument lazily materializes string values.
            _ = StrictUtf8.GetCharCount(utf8Json.Span);
            using var document = JsonDocument.Parse(utf8Json);
            var envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object
                || !TryGetSingleProperty(envelope, "MessageType", out var messageType)
                || messageType.ValueKind != JsonValueKind.String)
                throw InvalidMessage();

            if (!string.Equals(messageType.GetString(), "LibraryChanged", StringComparison.Ordinal)) return null;
            if (!TryGetSingleProperty(envelope, "Data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw InvalidMessage();

            string? messageId = null;
            if (TryGetSingleProperty(envelope, "MessageId", out var id))
            {
                if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw InvalidMessage();
                messageId = id.GetString();
                if (messageId is not null && (messageId.Length > MaximumIdentifierLength || messageId.Any(char.IsControl)))
                    throw InvalidMessage();
            }

            var isTruncated = false;
            var added = ReadItems(data, "ItemsAdded", ref isTruncated);
            var updated = ReadItems(data, "ItemsUpdated", ref isTruncated);
            var removed = ReadItems(data, "ItemsRemoved", ref isTruncated);
            return new(messageId, added, updated, removed, isTruncated);
        }
        catch (JsonException) { throw InvalidMessage(); }
        catch (DecoderFallbackException) { throw InvalidMessage(); }
        catch (InvalidOperationException) { throw InvalidMessage(); }
    }

    private static IReadOnlyList<string> ReadItems(JsonElement data, string name, ref bool isTruncated)
    {
        if (!TryGetSingleProperty(data, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array) throw InvalidMessage();

        var items = new List<string>();
        var retainedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) throw InvalidMessage();
            var itemId = element.GetString();
            // Preserve opaque IDs, including punctuation and surrounding spaces, in UTF-16 length units.
            if (string.IsNullOrWhiteSpace(itemId) || itemId.Length > MaximumIdentifierLength || itemId.Any(char.IsControl))
                throw InvalidMessage();
            if (retainedIds.Contains(itemId)) continue;
            if (items.Count == MaximumItemsPerCategory)
            {
                // Keep validating later elements even after the retained snapshot reaches its bound.
                isTruncated = true;
                continue;
            }
            retainedIds.Add(itemId);
            items.Add(itemId);
        }
        return Array.AsReadOnly(items.ToArray());
    }

    private static bool TryGetSingleProperty(JsonElement element, string name, out JsonElement value)
    {
        var found = false;
        value = default;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) throw InvalidMessage();
            found = true;
            value = property.Value;
        }
        return found;
    }

    private static EmbyProtocolException InvalidMessage() => new("The server returned an invalid event message.");
}

/// <summary>Provides bounded exponential delays for reconnecting a failed event connection.</summary>
public static class ServerEventRetryPolicy
{
    public static TimeSpan GetDelay(int consecutiveFailures)
    {
        if (consecutiveFailures < 1)
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures), "The failure count must be positive.");
        return TimeSpan.FromSeconds(consecutiveFailures switch
        {
            1 => 1,
            2 => 2,
            3 => 4,
            4 => 8,
            5 => 16,
            _ => 30
        });
    }
}
