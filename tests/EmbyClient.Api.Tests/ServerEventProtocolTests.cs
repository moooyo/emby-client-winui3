using System.Text;
using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class ServerEventProtocolTests
{
    private const string InvalidMessage = "The server returned an invalid event message.";

    [Fact]
    public void Library_changes_keep_opaque_ids_and_deduplicate_each_category_in_order()
    {
        var message = Parse("""
            {"MessageType":"LibraryChanged","MessageId":"event-a","Data":{
                "ItemsAdded":["alpha","ALPHA","alpha"," /opaque,?id=1 ","beta"],
                "ItemsUpdated":["alpha","alpha"],
                "ItemsRemoved":["ALPHA","ALPHA"],
                "FutureField":{"Enabled":true}
            },"FutureEnvelopeField":true}
            """);

        Assert.Equal("event-a", message.MessageId);
        Assert.Equal(new[] { "alpha", "ALPHA", " /opaque,?id=1 ", "beta" }, message.ItemsAdded);
        Assert.Equal(new[] { "alpha" }, message.ItemsUpdated);
        Assert.Equal(new[] { "ALPHA" }, message.ItemsRemoved);
        Assert.True(message.HasChanges);
        Assert.False(message.IsTruncated);
    }

    [Theory]
    [InlineData("""{"MessageType":"LibraryChanged","Data":{}}""")]
    [InlineData("""{"MessageType":"LibraryChanged","MessageId":null,"Data":{"ItemsAdded":null,"ItemsUpdated":[],"ItemsRemoved":null}}""")]
    public void Missing_or_null_change_arrays_produce_an_empty_event(string json)
    {
        var message = Parse(json);

        Assert.Null(message.MessageId);
        Assert.Empty(message.ItemsAdded);
        Assert.Empty(message.ItemsUpdated);
        Assert.Empty(message.ItemsRemoved);
        Assert.False(message.HasChanges);
        Assert.False(message.IsTruncated);
    }

    [Theory]
    [InlineData("""{"MessageType":"KeepAlive"}""")]
    [InlineData("""{"MessageType":"FutureEvent","MessageId":42,"Data":42}""")]
    [InlineData("""{"MessageType":"librarychanged","Data":{"ItemsAdded":42}}""")]
    public void Unknown_message_types_are_ignored_without_interpreting_their_data(string json)
    {
        Assert.Null(ServerEventProtocol.ParseLibraryChanged(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json-secret")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"LibraryChanged\"")]
    [InlineData("""{}""")]
    [InlineData("""{"MessageType":null}""")]
    [InlineData("""{"MessageType":42}""")]
    [InlineData("""{"MessageType":[],"Data":{}}""")]
    [InlineData("""{"MessageType":"LibraryChanged"}""")]
    [InlineData("""{"MessageType":"LibraryChanged","Data":null}""")]
    [InlineData("""{"MessageType":"LibraryChanged","Data":[]}""")]
    [InlineData("""{"MessageType":"LibraryChanged","Data":"secret"}""")]
    [InlineData("""{"MessageType":"LibraryChanged","Data":{},"MessageId":42}""")]
    [InlineData("""{"MessageType":"KeepAlive","MessageType":"LibraryChanged","Data":{}}""")]
    [InlineData("""{"MessageType":"LibraryChanged","Data":{},"Data":{}}""")]
    [InlineData("""{"MessageType":"LibraryChanged","MessageId":"a","MessageId":"b","Data":{}}""")]
    [InlineData("""{"MessageType":"LibraryChanged","Data":{"ItemsAdded":[],"ItemsAdded":[]}}""")]
    [InlineData("""{"MessageType":"LibraryChanged","Data":{"ItemsAdded":["secret"],}}""")]
    public void Invalid_envelopes_fail_with_a_sanitized_protocol_error(string json)
    {
        AssertInvalid(Encoding.UTF8.GetBytes(json));
    }

    [Theory]
    [InlineData("ItemsAdded")]
    [InlineData("ItemsUpdated")]
    [InlineData("ItemsRemoved")]
    public void Every_change_category_rejects_non_array_values_and_malformed_ids(string category)
    {
        var invalidValues = new[] { "{}", "42", "\"secret\"", "[null]", "[42]", "[{}]", "[\"\"]",
            "[\" \"]", "[\"\\u2003\"]", "[\"secret\\nvalue\"]", "[\"secret\\u007fvalue\"]" };
        foreach (var value in invalidValues)
            AssertInvalid(Encoding.UTF8.GetBytes($"{{\"MessageType\":\"LibraryChanged\",\"Data\":{{\"{category}\":{value}}}}}"));
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Item_ids_respect_the_length_bound(int length, bool accepted)
    {
        var bytes = CreateMessage("ItemsAdded", new[] { new string('a', length) });
        if (accepted)
            Assert.Equal(length, Assert.Single(Assert.IsType<LibraryChangedEventArgs>(
                ServerEventProtocol.ParseLibraryChanged(bytes)).ItemsAdded).Length);
        else
            AssertInvalid(bytes);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Message_ids_are_bounded_without_rejecting_the_empty_string(int length, bool accepted)
    {
        var messageId = new string('a', length);
        var bytes = CreateMessage("ItemsAdded", Array.Empty<string>(), messageId: messageId);
        if (accepted)
            Assert.Equal(messageId, Assert.IsType<LibraryChangedEventArgs>(
                ServerEventProtocol.ParseLibraryChanged(bytes)).MessageId);
        else
            AssertInvalid(bytes);
    }

    [Theory]
    [InlineData("secret\nvalue")]
    [InlineData("secret\u007fvalue")]
    public void Message_ids_with_control_characters_fail_with_a_sanitized_error(string messageId)
    {
        AssertInvalid(CreateMessage("ItemsAdded", Array.Empty<string>(), messageId: messageId));
    }

    [Theory]
    [InlineData(512, false)]
    [InlineData(513, true)]
    public void Each_category_has_an_independent_unique_id_bound(int uniqueCount, bool truncated)
    {
        var ids = Enumerable.Range(0, uniqueCount).Select(index => $"item-{index}").ToArray();
        var bytes = CreateMessage("ItemsAdded", ids, includeOtherCategories: true);
        var message = Assert.IsType<LibraryChangedEventArgs>(ServerEventProtocol.ParseLibraryChanged(bytes));
        var retained = ids.Take(ServerEventProtocol.MaximumItemsPerCategory).ToArray();

        Assert.Equal(retained, message.ItemsAdded);
        Assert.Equal(retained, message.ItemsUpdated);
        Assert.Equal(retained, message.ItemsRemoved);
        Assert.Equal(truncated, message.IsTruncated);
        Assert.True(message.HasChanges);
    }

    [Fact]
    public void Duplicates_after_the_category_bound_do_not_mark_the_message_as_truncated()
    {
        var ids = Enumerable.Range(0, ServerEventProtocol.MaximumItemsPerCategory).Select(index => $"item-{index}").ToArray();
        var bytes = CreateMessage("ItemsAdded", ids.Concat(ids.Reverse()));
        var message = Assert.IsType<LibraryChangedEventArgs>(ServerEventProtocol.ParseLibraryChanged(bytes));

        Assert.Equal(ids, message.ItemsAdded);
        Assert.False(message.IsTruncated);
    }

    [Theory]
    [InlineData("ItemsAdded")]
    [InlineData("ItemsUpdated")]
    [InlineData("ItemsRemoved")]
    public void Invalid_items_after_truncation_still_reject_the_entire_message(string category)
    {
        var ids = Enumerable.Range(0, ServerEventProtocol.MaximumItemsPerCategory + 1).Select(index => $"item-{index}");
        AssertInvalid(CreateMessage(category, ids.Append(string.Empty)));
    }

    [Fact]
    public void Reassembled_fragments_preserve_utf8_ids_and_respect_the_memory_slice()
    {
        const string itemId = "opaque-\u6620\u753b-\U0001F680";
        var bytes = Encoding.UTF8.GetBytes($"{{\"MessageType\":\"LibraryChanged\",\"Data\":{{\"ItemsAdded\":[\"{itemId}\"]}}}}");
        var split = Array.IndexOf(bytes, (byte)0xe6) + 1;
        Assert.InRange(split, 1, bytes.Length - 1);
        var firstFragment = bytes.AsMemory(0, split);
        var finalFragment = bytes.AsMemory(split);
        var reassembled = new byte[bytes.Length + 2];
        reassembled[0] = reassembled[^1] = 0xff;
        firstFragment.CopyTo(reassembled.AsMemory(1));
        finalFragment.CopyTo(reassembled.AsMemory(1 + firstFragment.Length));

        var message = Assert.IsType<LibraryChangedEventArgs>(
            ServerEventProtocol.ParseLibraryChanged(reassembled.AsMemory(1, bytes.Length)));

        Assert.Equal(itemId, Assert.Single(message.ItemsAdded));
    }

    [Fact]
    public void Invalid_utf8_has_the_same_sanitized_error_as_invalid_json()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"MessageType":"LibraryChanged","Data":{"ItemsAdded":["secret"]}}""");
        bytes[Array.IndexOf(bytes, (byte)'s')] = 0xff;

        AssertInvalid(bytes);
    }

    [Fact]
    public void The_message_byte_limit_is_inclusive_and_checked_before_parsing()
    {
        var bytes = new byte[ServerEventProtocol.MaximumMessageBytes];
        Array.Fill(bytes, (byte)' ');
        var json = Encoding.UTF8.GetBytes("""{"MessageType":"LibraryChanged","Data":{}}""");
        json.CopyTo(bytes, 0);

        Assert.False(Assert.IsType<LibraryChangedEventArgs>(ServerEventProtocol.ParseLibraryChanged(bytes)).HasChanges);
        AssertInvalid(new byte[ServerEventProtocol.MaximumMessageBytes + 1]);
    }

    [Fact]
    public void Returned_changes_are_read_only_and_independent_of_the_input_buffer()
    {
        var bytes = CreateMessage("ItemsAdded", new[] { "retained-item" });
        var message = Assert.IsType<LibraryChangedEventArgs>(ServerEventProtocol.ParseLibraryChanged(bytes));
        Array.Fill(bytes, (byte)' ');

        Assert.Equal("retained-item", Assert.Single(message.ItemsAdded));
        var items = Assert.IsAssignableFrom<IList<string>>(message.ItemsAdded);
        Assert.True(items.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => items[0] = "changed");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(7, 30)]
    [InlineData(100, 30)]
    [InlineData(int.MaxValue, 30)]
    public void Retry_delays_increase_exponentially_and_remain_capped(int consecutiveFailures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ServerEventRetryPolicy.GetDelay(consecutiveFailures));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Retry_delays_reject_nonpositive_failure_counts(int consecutiveFailures)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ServerEventRetryPolicy.GetDelay(consecutiveFailures));

        Assert.Equal("consecutiveFailures", error.ParamName);
    }

    private static LibraryChangedEventArgs Parse(string json) =>
        Assert.IsType<LibraryChangedEventArgs>(ServerEventProtocol.ParseLibraryChanged(Encoding.UTF8.GetBytes(json)));

    private static void AssertInvalid(byte[] bytes)
    {
        var error = Assert.Throws<EmbyProtocolException>(() => ServerEventProtocol.ParseLibraryChanged(bytes));
        Assert.Equal(InvalidMessage, error.Message);
        Assert.Null(error.InnerException);
    }

    private static byte[] CreateMessage(string category, IEnumerable<string> ids, bool includeOtherCategories = false,
        string? messageId = null)
    {
        var values = ids.ToArray();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("MessageType", "LibraryChanged");
            if (messageId is not null) writer.WriteString("MessageId", messageId);
            writer.WriteStartObject("Data");
            foreach (var name in includeOtherCategories ? new[] { "ItemsAdded", "ItemsUpdated", "ItemsRemoved" } : new[] { category })
            {
                writer.WriteStartArray(name);
                foreach (var id in values) writer.WriteStringValue(id);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
