using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Infrastructure.Persistence;
using BodyLimits = ProjectC.WebApi.Common.RequestBodyLimits;
using ProjectC.WebApi.ExceptionHandling;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.RequestBodyLimits;

public class RequestBodyLimitsKestrelTests : IClassFixture<KestrelWebApplicationFactory>
{
    private const string LoginPath = "/api/auth/login";
    private const string OrdersPath = "/api/orders";
    private const string RequestLoggingSourceContext = "Serilog.AspNetCore.RequestLoggingMiddleware";
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(10);

    private readonly KestrelWebApplicationFactory _factory;

    public RequestBodyLimitsKestrelTests(KestrelWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // tasks 1.2：確認 factory 真的走 Kestrel 且用隨機 port，沒有沿用容器的 ASPNETCORE_URLS=8080。
    [Fact]
    public void Factory_UsesKestrelOnRandomLoopbackPort()
    {
        var client = _factory.CreateClient();

        // Kestrel 註冊到 IServer 的實作是 internal 的 KestrelServerImpl，以組件判定，排除 TestServer。
        _factory.Services.GetRequiredService<IServer>().GetType().Assembly.Should().BeSameAs(typeof(KestrelServerOptions).Assembly);
        client.BaseAddress.Should().NotBeNull();
        client.BaseAddress!.Scheme.Should().Be("http");
        client.BaseAddress.Host.Should().BeOneOf("127.0.0.1", "localhost");
        client.BaseAddress.Port.Should().NotBe(8080);
    }

    // spec 以字面值規定上限；其他測試都引用常數計算邊界，常數被改時會跟著移動，所以這裡釘住字面值。
    [Fact]
    public void Limits_MatchSpecifiedValues()
    {
        BodyLimits.GlobalBytes.Should().Be(1_048_576);
        BodyLimits.PlaceOrderBytes.Should().Be(16_384);
        BodyLimits.CreateSeatMapBytes.Should().Be(10_485_760);
    }

    [Fact]
    public async Task Login_WhenBodyExceedsGlobalLimit_Returns413()
    {
        var client = _factory.CreateClient();
        var body = CreatePaddedJson("""{"email":"a@example.com"}""", BodyLimits.GlobalBytes + 1);

        using var response = await client.PostAsync(LoginPath, CreateJsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Login_WhenBodyEqualsGlobalLimit_IsNotRejectedForSize()
    {
        var client = _factory.CreateClient();
        var body = CreatePaddedJson("""{"email":"a@example.com"}""", BodyLimits.GlobalBytes);

        using var response = await client.PostAsync(LoginPath, CreateJsonContent(body));

        ((int)response.StatusCode).Should().BeInRange(400, 499);
        response.StatusCode.Should().NotBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task PlaceOrder_WhenBodyExceedsEndpointLimit_Returns413AndCreatesNoOrder()
    {
        var seededEvent = await SeedEventAsync("RBL-003");
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var countTicketTypeId = seededEvent.CountTicketTypeIds[0];
        var body = CreatePaddedJson(SerializeCountOrder(countTicketTypeId), BodyLimits.PlaceOrderBytes + 1);

        using var response = await buyerClient.PostAsync(OrdersPath, CreateJsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await CountOrdersAsync(seededEvent.EventId)).Should().Be(0);
        (await GetAvailableQuantityAsync(countTicketTypeId)).Should().Be(100);
    }

    [Fact]
    public async Task PlaceOrder_WhenBodyEqualsEndpointLimit_CreatesOrder()
    {
        var controlEvent = await SeedEventAsync("RBL-004 control");
        var targetEvent = await SeedEventAsync("RBL-004 target");
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);

        using var controlResponse = await buyerClient.PostAsync(
            OrdersPath, CreateJsonContent(SerializeCountOrder(controlEvent.CountTicketTypeIds[0])));
        controlResponse.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the unpadded control order must succeed; otherwise the seed data is wrong, not the body size limit: {0}",
            await controlResponse.Content.ReadAsStringAsync());

        var targetTicketTypeId = targetEvent.CountTicketTypeIds[0];
        var body = CreatePaddedJson(SerializeCountOrder(targetTicketTypeId), BodyLimits.PlaceOrderBytes);
        using var response = await buyerClient.PostAsync(OrdersPath, CreateJsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await CountOrdersAsync(targetEvent.EventId)).Should().Be(1);
        (await GetAvailableQuantityAsync(targetTicketTypeId)).Should().Be(99);
    }

    [Fact]
    public async Task PlaceOrder_WhenUnauthenticatedAndBodyTooLarge_Returns401()
    {
        var client = _factory.CreateClient();
        var body = CreatePaddedJson(SerializeCountOrder(Guid.NewGuid()), BodyLimits.PlaceOrderBytes + 1);

        using var response = await client.PostAsync(OrdersPath, CreateJsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PlaceOrder_WhenBodyTooLarge_ReturnsProblemDetails()
    {
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var body = CreatePaddedJson(SerializeCountOrder(Guid.NewGuid()), BodyLimits.PlaceOrderBytes + 1);

        using var response = await buyerClient.PostAsync(OrdersPath, CreateJsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var problem = ParseProblemDetails(await response.Content.ReadAsStringAsync());
        problem.Status.Should().Be(413);
        problem.Title.Should().Be("Request body too large.");
        problem.TraceId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PlaceOrder_WhenBodyTooLarge_LogsWarningWithoutBody()
    {
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var sentinel = NewSentinel();
        var body = CreatePaddedJson($$"""{"selections":[],"note":"{{sentinel}}"}""", BodyLimits.PlaceOrderBytes + 1);
        _factory.LogSink.Clear();

        using var response = await buyerClient.PostAsync(OrdersPath, CreateJsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var problem = ParseProblemDetails(await response.Content.ReadAsStringAsync());
        AssertLoggedSingleHandlerWarningWithoutSentinel(problem.TraceId, sentinel);
        // 路徑樣板須是比對到的 endpoint，而不是 UseExceptionHandler 清除 endpoint 後的 "(unmatched)"。
        var handlerWarning = _factory.LogSink.Events.Single(e =>
            ReadScalar(e, "SourceContext") == typeof(GlobalExceptionHandler).FullName && e.Level == LogEventLevel.Warning);
        ReadScalar(handlerWarning, "RouteTemplate").Should().Be("api/orders");
    }

    [Fact]
    public async Task CreateSeatMap_WhenBodyBetweenGlobalAndEndpointLimit_Returns201()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var venueId = await CreateVenueAsync(organizerClient);
        // 20,000 席約 1.4MB，再以空白補到恰好端點上限：證明大於全域 1MB 的本文被接受，
        // 且端點上限不低於 CreateSeatMapBytes（上限若被改小，這個恰好上限的本文會得到 413）。
        const int seatCount = 20_000;
        var seats = Enumerable.Range(1, seatCount)
            .Select(i => new SeatRequest("A", i.ToString().PadLeft(40, '0')))
            .ToList();
        var body = CreatePaddedJson(
            JsonSerializer.Serialize(new CreateSeatMapRequest(seats), JsonSerializerOptions.Web), BodyLimits.CreateSeatMapBytes);
        seatCount.Should().BeLessThanOrEqualTo(SeatMapLimits.MaxSeats);

        using var response = await organizerClient.PostAsync(
            $"/api/admin/venues/{venueId}/seat-maps", CreateJsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var seatMapId = (await response.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.Seats.CountAsync(s => s.SeatMapId == seatMapId)).Should().Be(seatCount);
    }

    [Fact]
    public async Task CreateSeatMap_WhenBodyExceedsEndpointLimit_Returns413AndCreatesNoSeatMap()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var venueId = await CreateVenueAsync(organizerClient);
        var body = CreatePaddedJson(
            """{"seats":[{"zoneCode":"A","seatNumber":"1"}]}""", BodyLimits.CreateSeatMapBytes + 1);

        // Kestrel 依 Content-Length 提前回 413 並關閉連線，10MB 本文來不及送完時 HttpClient 會得到 broken pipe 而讀不到回應；
        // 用 Expect: 100-continue 讓 HttpClient 先等伺服器決定，再送本文。等待時間預設只有 1 秒，負載高時
        // 驗證／限流耗時超過 1 秒就會開始上傳而重現 broken pipe，所以明確放長。
        using var handler = new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(30) };
        using var expectContinueClient = new HttpClient(handler) { BaseAddress = organizerClient.BaseAddress };
        expectContinueClient.DefaultRequestHeaders.Authorization = organizerClient.DefaultRequestHeaders.Authorization;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/venues/{venueId}/seat-maps")
        {
            Content = CreateJsonContent(body),
        };
        request.Headers.ExpectContinue = true;
        using var response = await expectContinueClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.SeatMaps.CountAsync(s => s.VenueId == venueId)).Should().Be(0);
    }

    [Fact]
    public async Task Login_WhenDeclaredContentLengthExceedsLimit_Returns413BeforeBodySent()
    {
        using var connection = await ConnectAsync();
        var stream = connection.GetStream();

        await stream.WriteAsync(CreateRequestHead("Content-Length: 2097152"));
        var response = await ReadResponseAsync(stream, DefaultReadTimeout);

        response.StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task Login_WhenChunkedBodyExceedsLimit_Returns413()
    {
        using var connection = await ConnectAsync();
        var stream = connection.GetStream();
        var body = Encoding.UTF8.GetBytes(CreatePaddedJson("""{"email":"a@example.com"}""", BodyLimits.GlobalBytes + 1));

        // 伺服器超過上限時會在本文送完前回應並關閉連線，寫入與讀取同時進行，避免讀取前就被 reset 中斷。
        var writeTask = WriteIgnoringConnectionClosedAsync(stream, async () =>
        {
            await stream.WriteAsync(CreateRequestHead("Transfer-Encoding: chunked"));
            foreach (var chunk in body.Chunk(64 * 1024))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:X}\r\n"));
                await stream.WriteAsync(chunk);
                await stream.WriteAsync("\r\n"u8.ToArray());
            }
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
        });
        var response = await ReadResponseAsync(stream, DefaultReadTimeout);
        await writeTask;

        response.StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task Login_WhenContentLengthSmallerThanSentBytes_TreatsOnlyDeclaredBytesAsBody()
    {
        using var connection = await ConnectAsync();
        var stream = connection.GetStream();
        // 'A' 重複不構成合法請求行，額外資料不會被當成第二個合法請求。
        var extraBytes = Enumerable.Repeat((byte)'A', (int)BodyLimits.GlobalBytes + 1).ToArray();
        _factory.LogSink.Clear();

        var writeTask = WriteIgnoringConnectionClosedAsync(stream, async () =>
        {
            await stream.WriteAsync(CreateRequestHead("Content-Length: 2"));
            await stream.WriteAsync("{}"u8.ToArray());
            await stream.WriteAsync(extraBytes);
        });
        var firstResponse = await ReadResponseAsync(stream, DefaultReadTimeout);
        var followingStatusCode = await TryReadFollowingStatusCodeAsync(stream);
        await writeTask;

        firstResponse.StatusCode.Should().BeInRange(400, 499);
        firstResponse.StatusCode.Should().NotBe(413);
        followingStatusCode.Should().NotBe(500);
        var requestSummaries = await WaitForRequestSummariesAsync();
        requestSummaries.Should().ContainSingle();
        ReadScalar(requestSummaries[0], "RequestPath").Should().Be(LoginPath);
        ReadScalar(requestSummaries[0], "StatusCode").Should().NotBe("413").And.NotBe("500");
        _factory.LogSink.Events.Should().NotContain(e => e.Level >= LogEventLevel.Error);
    }

    [Fact]
    public async Task Login_WhenClientStallsBeforeDeclaredLength_EndsRequestWithin30Seconds()
    {
        using var connection = await ConnectAsync();
        var stream = connection.GetStream();
        var sentinel = NewSentinel();
        var prefix = Encoding.UTF8.GetBytes($$"""{"email":"{{sentinel}}""");
        prefix.Length.Should().BeLessThan(1000);
        _factory.LogSink.Clear();

        await stream.WriteAsync(CreateRequestHead("Content-Length: 1000"));
        await stream.WriteAsync(prefix);
        var response = await ReadResponseAsync(stream, TimeSpan.FromSeconds(30));

        response.StatusCode.Should().Be(408);
        var problem = ParseProblemDetails(response.Body);
        problem.TraceId.Should().NotBeNullOrWhiteSpace();
        AssertLoggedSingleHandlerWarningWithoutSentinel(problem.TraceId, sentinel);
    }

    [Theory]
    [InlineData("invalid-chunk-size")]
    [InlineData("missing-chunk-crlf")]
    public async Task Login_WhenChunkedEncodingMalformed_Returns400ProblemDetailsAndLogsWarning(string malformation)
    {
        using var connection = await ConnectAsync();
        var stream = connection.GetStream();
        var sentinel = NewSentinel();
        var chunkData = $$"""{"email":"{{sentinel}}"}""";
        var chunkedBody = malformation == "invalid-chunk-size"
            ? $"ZZ\r\n{chunkData}\r\n0\r\n\r\n"
            : $"{Encoding.UTF8.GetByteCount(chunkData):X}\r\n{chunkData}XX0\r\n\r\n";
        _factory.LogSink.Clear();

        var writeTask = WriteIgnoringConnectionClosedAsync(stream, async () =>
        {
            await stream.WriteAsync(CreateRequestHead("Transfer-Encoding: chunked"));
            await stream.WriteAsync(Encoding.UTF8.GetBytes(chunkedBody));
        });
        var response = await ReadResponseAsync(stream, DefaultReadTimeout);
        await writeTask;

        response.StatusCode.Should().Be(400);
        var problem = ParseProblemDetails(response.Body);
        problem.Status.Should().Be(400);
        problem.Title.Should().Be("Bad Request");
        problem.TraceId.Should().NotBeNullOrWhiteSpace();
        AssertLoggedSingleHandlerWarningWithoutSentinel(problem.TraceId, sentinel);
    }

    // design 決策 5 的日誌範圍：只有 GlobalExceptionHandler 的一筆 Warning，任何 category 都沒有 Error／Fatal，
    // 且本文內容（哨兵）不出現在任何日誌的渲染文字、屬性值或例外中。
    private void AssertLoggedSingleHandlerWarningWithoutSentinel(string? traceId, string sentinel)
    {
        var events = _factory.LogSink.Events.ToList();
        var handlerWarnings = events
            .Where(e => ReadScalar(e, "SourceContext") == typeof(GlobalExceptionHandler).FullName && e.Level == LogEventLevel.Warning)
            .ToList();
        handlerWarnings.Should().ContainSingle();
        ReadScalar(handlerWarnings[0], "TraceId").Should().Be(traceId);
        events.Should().NotContain(e => e.Level >= LogEventLevel.Error);
        events.Should().NotContain(e =>
            e.RenderMessage(null).Contains(sentinel)
            || e.Properties.Values.Any(v => v.ToString().Contains(sentinel))
            || (e.Exception != null && e.Exception.ToString().Contains(sentinel)));
    }

    private async Task<IReadOnlyList<LogEvent>> WaitForRequestSummariesAsync()
    {
        // 請求摘要日誌在回應送出後才寫入，輪詢等到出現；再多等一段時間，讓額外資料若被當成第二個請求也有機會被記錄。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !FindRequestSummaries().Any())
        {
            await Task.Delay(50);
        }
        await Task.Delay(500);
        return FindRequestSummaries();
    }

    private List<LogEvent> FindRequestSummaries() =>
        _factory.LogSink.Events.Where(e => ReadScalar(e, "SourceContext") == RequestLoggingSourceContext).ToList();

    private static string? ReadScalar(LogEvent logEvent, string propertyName) =>
        logEvent.Properties.TryGetValue(propertyName, out var value) && value is ScalarValue { Value: not null } scalar
            ? scalar.Value.ToString()
            : null;

    private async Task<OrderDisplayTestData.SeededEvent> SeedEventAsync(string title)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        return await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, title, seatCount: 1, countTicketTypeCount: 1);
    }

    private async Task<int> CountOrdersAsync(Guid eventId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Orders.CountAsync(o => o.EventId == eventId);
    }

    private async Task<int?> GetAvailableQuantityAsync(Guid ticketTypeId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await dbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId)).AvailableQuantity;
    }

    private static async Task<Guid> CreateVenueAsync(HttpClient organizerClient)
    {
        var response = await organizerClient.PostAsJsonAsync(
            "/api/admin/venues", new CreateVenueRequest($"Body Limit Venue {Guid.NewGuid():N}"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
    }

    private static string SerializeCountOrder(Guid ticketTypeId) =>
        JsonSerializer.Serialize(
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 1)]), JsonSerializerOptions.Web);

    private static string NewSentinel() => $"BODY-SENTINEL-{Guid.NewGuid():N}";

    // 有效 JSON 後以空白補足到精確長度，排除 JSON 格式錯誤造成的 400 干擾（design 決策 5）。
    private static string CreatePaddedJson(string json, long totalBytes)
    {
        var padded = json + new string(' ', (int)totalBytes - Encoding.UTF8.GetByteCount(json));
        Encoding.UTF8.GetByteCount(padded).Should().Be((int)totalBytes);
        return padded;
    }

    private static StringContent CreateJsonContent(string body) => new(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

    private static ProblemDetailsBody ParseProblemDetails(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new ProblemDetailsBody(
            root.TryGetProperty("status", out var status) ? status.GetInt32() : null,
            root.TryGetProperty("title", out var title) ? title.GetString() : null,
            root.TryGetProperty("traceId", out var traceId) ? traceId.GetString() : null);
    }

    private sealed record ProblemDetailsBody(int? Status, string? Title, string? TraceId);

    // ---- 原始 TCP（HttpClient 無法送出宣告長度與實際長度不一致、或格式錯誤的 chunked 本文，design 決策 5） ----

    private async Task<TcpClient> ConnectAsync()
    {
        var baseAddress = _factory.CreateClient().BaseAddress!;
        var connection = new TcpClient();
        await connection.ConnectAsync(baseAddress.Host, baseAddress.Port);
        return connection;
    }

    private static byte[] CreateRequestHead(string bodyFramingHeader) =>
        Encoding.ASCII.GetBytes(
            $"POST {LoginPath} HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n{bodyFramingHeader}\r\n\r\n");

    // 伺服器拒絕後關閉連線時，尚未送完的寫入會得到 IOException／reset；這是預期行為，結果以讀到的回應判定。
    private static async Task WriteIgnoringConnectionClosedAsync(NetworkStream stream, Func<Task> write)
    {
        try
        {
            await Task.Run(write);
        }
        catch (IOException)
        {
            // 預期：伺服器已回應並關閉連線，剩餘資料不必送達。
        }
        catch (ObjectDisposedException)
        {
            // 預期：測試讀完回應後已釋放連線。
        }
    }

    private static async Task<int?> TryReadFollowingStatusCodeAsync(NetworkStream stream)
    {
        try
        {
            return (await ReadResponseAsync(stream, DefaultReadTimeout)).StatusCode;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            // 預期之一：Kestrel 因額外資料解析失敗直接關閉連線，沒有第二個回應。
            return null;
        }
    }

    private sealed record RawHttpResponse(int StatusCode, string Body);

    private static async Task<RawHttpResponse> ReadResponseAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        var cancellationToken = timeoutSource.Token;

        var statusLine = await ReadLineAsync(stream, cancellationToken);
        var statusCode = int.Parse(statusLine.Split(' ')[1]);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string line;
        while ((line = await ReadLineAsync(stream, cancellationToken)).Length > 0)
        {
            var separatorIndex = line.IndexOf(':');
            headers[line[..separatorIndex].Trim()] = line[(separatorIndex + 1)..].Trim();
        }

        byte[] body;
        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedBodyAsync(stream, cancellationToken);
        }
        else
        {
            var contentLength = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value) : 0;
            body = new byte[contentLength];
            await stream.ReadExactlyAsync(body, cancellationToken);
        }

        return new RawHttpResponse(statusCode, Encoding.UTF8.GetString(body));
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, cancellationToken);
            var chunkSize = Convert.ToInt32(sizeLine.Split(';')[0], 16);
            if (chunkSize == 0)
            {
                while ((await ReadLineAsync(stream, cancellationToken)).Length > 0)
                {
                }
                return body.ToArray();
            }

            var chunk = new byte[chunkSize];
            await stream.ReadExactlyAsync(chunk, cancellationToken);
            body.Write(chunk);
            await ReadLineAsync(stream, cancellationToken);
        }
    }

    // 逐位元組讀取，確保只讀到本回應的位元組，不把後續回應併入。
    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(buffer, cancellationToken) == 0)
            {
                throw new EndOfStreamException("Connection closed before a complete HTTP response line was received.");
            }
            if (buffer[0] == '\n' && bytes.Count > 0 && bytes[^1] == '\r')
            {
                bytes.RemoveAt(bytes.Count - 1);
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(buffer[0]);
        }
    }
}
