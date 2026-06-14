using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using NetBinder.Shared.Protocol;

namespace NetBinder.UI.Services;
/// <summary>
/// Named Pipe client that communicates with the NetBinder Service.
/// Connects to the "NetBinderService" pipe and sends/receives JSON messages.
/// Protocol: 4-byte length prefix (little-endian) + JSON payload.
/// </summary>
public class PipeClient : IDisposable
{
    private const string PipeName = "NetBinderService";
    private const string ServerName = ".";
    private const int BufferSize = 65536;
    private const int ConnectTimeoutMs = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    /// <summary>Check if currently connected to the service.</summary>
    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Connects to the NetBinder Service pipe.
    /// Throws if the service is not running or connection times out.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _pipe?.Dispose();
        _pipe = new NamedPipeClientStream(ServerName, PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await _pipe.ConnectAsync(ConnectTimeoutMs, ct);
    }

    /// <summary>
    /// Sends a request and receives the response.
    /// Handles reconnection if the pipe is broken.
    /// </summary>
    public async Task<TResponse> SendRequestAsync<TResponse>(PipeRequest request, CancellationToken ct = default)
        where TResponse : PipeResponse
    {
        if (_pipe == null || !_pipe.IsConnected)
        {
            await ConnectAsync(ct);
        }

        // Send request
        var json = JsonSerializer.Serialize(request, request.GetType(), JsonOptions);
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var lengthPrefix = BitConverter.GetBytes(payload.Length);

        await _pipe!.WriteAsync(lengthPrefix, ct);
        await _pipe.WriteAsync(payload, ct);
        await _pipe.FlushAsync(ct);

        // Read response
        var lengthBuffer = new byte[4];
        int read = await _pipe.ReadAsync(lengthBuffer.AsMemory(0, 4), ct);
        if (read < 4)
            throw new IOException("Failed to read response length from pipe");

        int responseLength = BitConverter.ToInt32(lengthBuffer, 0);
        if (responseLength <= 0 || responseLength > 10 * 1024 * 1024)
            throw new IOException($"Invalid response length: {responseLength}");

        var responseBuffer = new byte[responseLength];
        int totalRead = 0;
        while (totalRead < responseLength)
        {
            read = await _pipe.ReadAsync(responseBuffer.AsMemory(totalRead, responseLength - totalRead), ct);
            if (read == 0) throw new IOException("Pipe closed while reading response");
            totalRead += read;
        }

        var responseJson = System.Text.Encoding.UTF8.GetString(responseBuffer);
        var response = JsonSerializer.Deserialize<TResponse>(responseJson, JsonOptions);
        return response ?? throw new IOException("Failed to deserialize response");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _pipe?.Dispose();
        }
    }
}
