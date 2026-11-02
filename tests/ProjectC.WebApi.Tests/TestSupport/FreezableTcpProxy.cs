using System.Net;
using System.Net.Sockets;

namespace ProjectC.WebApi.Tests.TestSupport;

// 模擬 DB 行程凍結：連線與 TCP 都還在，但不再有任何回應。docker stop 式的故障會立刻拒絕連線，驗不到這種情況。
public sealed class FreezableTcpProxy : IAsyncDisposable
{
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _disposal = new();
    private readonly List<TcpClient> _connections = new();
    private readonly Task _acceptLoop;
    private volatile bool _isFrozen;

    public FreezableTcpProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptConnectionsAsync();
    }

    public int Port { get; }

    public void Freeze() => _isFrozen = true;

    public async ValueTask DisposeAsync()
    {
        await _disposal.CancelAsync();
        _listener.Stop();
        lock (_connections)
        {
            foreach (var connection in _connections)
            {
                connection.Dispose();
            }
        }

        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
            // 預期的結束方式：Dispose 取消了 accept。
        }

        _disposal.Dispose();
    }

    private async Task AcceptConnectionsAsync()
    {
        while (!_disposal.IsCancellationRequested)
        {
            var downstream = await _listener.AcceptTcpClientAsync(_disposal.Token);
            Track(downstream);
            if (_isFrozen)
            {
                continue;
            }

            var upstream = new TcpClient();
            Track(upstream);
            await upstream.ConnectAsync(_targetHost, _targetPort, _disposal.Token);
            _ = PumpAsync(downstream, upstream);
            _ = PumpAsync(upstream, downstream);
        }
    }

    private void Track(TcpClient connection)
    {
        lock (_connections)
        {
            _connections.Add(connection);
        }
    }

    private async Task PumpAsync(TcpClient source, TcpClient destination)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var byteCount = await source.GetStream().ReadAsync(buffer, _disposal.Token);
                if (byteCount == 0)
                {
                    return;
                }

                // 凍結後資料被丟棄但連線不關閉，呼叫端只會等不到回應。
                if (!_isFrozen)
                {
                    await destination.GetStream().WriteAsync(buffer.AsMemory(0, byteCount), _disposal.Token);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 連線結束是 proxy 的正常生命週期；這裡沒有呼叫端可回報，轉發就此停止即可。
        }
    }
}
