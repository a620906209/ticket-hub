using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace ProjectC.WebApi.Tests.TestSupport;

// 本機假 Seq ingestion 端點：收下 Seq sink 實際送出的 request body，供測試斷言 Seq 輸出內容
// （exception-log-masking design.md 決策 7）。不依賴 Seq 容器，也不碰共用的開發用 Seq 服務。
public sealed class FakeSeqIngestionServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<string> _bodies = new();
    private readonly Task _loop;

    public FakeSeqIngestionServer()
    {
        var port = ReserveFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl { get; }

    public IReadOnlyCollection<string> Bodies => _bodies.ToArray();

    public void Dispose()
    {
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // 接受迴圈在 listener 關閉時以例外結束，屬預期。
        }
    }

    private static int ReserveFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            _bodies.Enqueue(await reader.ReadToEndAsync());
            context.Response.StatusCode = 201;
            context.Response.Close();
        }
    }
}
