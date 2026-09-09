using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EmbyClient.MediaTransport.Tests;

/// <summary>Serves one HTTP request per connection on an ephemeral IPv4 loopback port.</summary>
internal sealed class LoopbackServer : IAsyncDisposable
{
    private const int MaximumRequestHeaderBytes = 32 * 1024;
    private readonly Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> _handler;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly List<Task> _connections = [];
    private readonly Task _acceptLoop;
    private readonly Lazy<Task> _dispose;

    internal LoopbackServer(Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUri = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
            _dispose = new Lazy<Task>(DisposeCoreAsync);
            _acceptLoop = AcceptConnectionsAsync(_shutdown.Token);
        }
        catch
        {
            _listener.Stop();
            _shutdown.Dispose();
            throw;
        }
    }

    internal Uri BaseUri { get; }
    internal ConcurrentQueue<LoopbackRequest> Requests { get; } = new();

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                // Only this loop mutates the list. Disposal awaits the loop before reading it.
                _connections.Add(HandleConnectionAsync(client, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _errors.Enqueue(exception);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                LoopbackRequest? request;
                try
                {
                    request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                }
                catch (IOException) { return; }
                catch (SocketException) { return; }

                // An incomplete request followed by EOF is an ordinary client disconnect.
                if (request is null) return;
                Requests.Enqueue(request);

                // Handler and response-validation failures are fixture errors, including IO errors.
                var response = await _handler(request, ct).ConfigureAwait(false);
                var headerBytes = BuildResponseHeaders(response);
                try
                {
                    await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
                    await stream.WriteAsync(response.Body, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    if (response.HoldBodyOpen)
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _errors.Enqueue(exception);
        }
    }

    private static async Task<LoopbackRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new byte[MaximumRequestHeaderBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) return null;
            var searchStart = Math.Max(0, length - 3);
            length += read;
            for (var index = searchStart; index <= length - 4; index++)
            {
                if (bytes[index] == '\r' && bytes[index + 1] == '\n'
                    && bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                    return ParseRequest(Encoding.Latin1.GetString(bytes, 0, index));
            }
        }
        throw new FormatException("The loopback request headers exceeded the fixture limit.");
    }

    private static LoopbackRequest ParseRequest(string headerText)
    {
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !IsHeaderName(requestLine[0])
            || requestLine[2] is not ("HTTP/1.1" or "HTTP/1.0"))
            throw new FormatException("The loopback request line was invalid.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || !IsHeaderName(line[..separator]))
                throw new FormatException("A loopback request header was invalid.");
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim(' ', '\t');
            headers[name] = headers.TryGetValue(name, out var previous) ? $"{previous}, {value}" : value;
        }

        return new LoopbackRequest(requestLine[0], requestLine[1], headers);
    }

    private static byte[] BuildResponseHeaders(LoopbackResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(response.Body);
        if (response.StatusCode is < 100 or > 999)
            throw new ArgumentOutOfRangeException(nameof(response), "The response status must have three digits.");
        if (response.DeclaredContentLength < -1)
            throw new ArgumentOutOfRangeException(nameof(response), "The declared content length was invalid.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Length"] = response.Body.Length.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var header in response.Headers ?? new Dictionary<string, string>())
        {
            if (!IsHeaderName(header.Key) || header.Value is null
                || header.Value.Any(value => value is '\r' or '\n' or '\0' || value > byte.MaxValue))
                throw new ArgumentException("A loopback response header was invalid.", nameof(response));
            headers[header.Key] = header.Value;
        }

        // Explicit length settings win over header overrides; -1 selects close-delimited content.
        if (response.DeclaredContentLength == -1) headers.Remove("Content-Length");
        else if (response.DeclaredContentLength is { } declaredLength)
            headers["Content-Length"] = declaredLength.ToString(CultureInfo.InvariantCulture);
        headers["Connection"] = "close";

        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(response.StatusCode.ToString(CultureInfo.InvariantCulture))
            .Append(" Test Response\r\n");
        foreach (var header in headers)
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        builder.Append("\r\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static bool IsHeaderName(string name)
        => !string.IsNullOrEmpty(name) && name.All(value => char.IsAsciiLetterOrDigit(value)
            || "!#$%&'*+-.^_`|~".Contains(value));

    public ValueTask DisposeAsync() => new(_dispose.Value);

    private async Task DisposeCoreAsync()
    {
        try { _shutdown.Cancel(); }
        catch (Exception exception) { _errors.Enqueue(exception); }
        try { _listener.Stop(); }
        catch (Exception exception) { _errors.Enqueue(exception); }

        // Preserve faults without allowing one task to prevent the remaining sockets from draining.
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (Exception exception) { _errors.Enqueue(exception); }
        try { await Task.WhenAll(_connections).ConfigureAwait(false); }
        catch (Exception exception) { _errors.Enqueue(exception); }
        _shutdown.Dispose();

        if (!_errors.IsEmpty)
            throw new AggregateException("The loopback HTTP fixture failed.", _errors.ToArray());
    }
}

internal sealed record LoopbackRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers);

internal sealed record LoopbackResponse(int StatusCode, byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null, int? DeclaredContentLength = null,
    bool HoldBodyOpen = false);
