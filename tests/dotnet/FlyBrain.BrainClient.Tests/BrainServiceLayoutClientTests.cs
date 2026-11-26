using System.Net;
using System.Text;
using System.Web;
using FlyBrain.BrainClient;
using Xunit;

namespace FlyBrain.BrainClient.Tests;

public sealed class BrainServiceLayoutClientTests
{
    [Fact]
    public async Task GetSessionLayoutAsync_DeserializesEntriesWithNullableClasses()
    {
        const long rootId = 720575940602977248;
        using var httpClient = CreateHttpClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/brain/sessions/test%20session/layout", request.RequestUri!.AbsolutePath);
            Assert.Equal("?offset=4&limit=2", request.RequestUri.Query);
            return JsonResponse(HttpStatusCode.OK, $$"""
                {"session_id":"test session","offset":4,"limit":2,"returned_count":2,"total_root_count":6,"coordinate_unit":"um","entries":[
                 {"root_id":{{rootId}},"x":512.25,"y":201.5,"z":140.125,"has_position":true,"super_class":"central","cell_class":"ALPN","cell_type":"DA1_lPN","side":"right"},
                 {"root_id":{{rootId + 1}},"x":1.0,"y":2.0,"z":3.0,"has_position":false,"super_class":null,"cell_class":null,"cell_type":null,"side":null}]}
                """);
        });
        var client = new BrainServiceClient(httpClient);

        var page = await client.GetSessionLayoutAsync("test session", offset: 4, limit: 2);

        Assert.Equal("um", page.CoordinateUnit);
        Assert.Equal(6, page.TotalRootCount);
        Assert.Equal(2, page.ReturnedCount);
        var real = page.Entries[0];
        Assert.Equal(rootId, real.RootId);
        Assert.Equal((512.25f, 201.5f, 140.125f), (real.X, real.Y, real.Z));
        Assert.True(real.HasPosition);
        Assert.Equal(("central", "ALPN", "DA1_lPN", "right"), (real.SuperClass, real.CellClass, real.CellType, real.Side));
        var placeholder = page.Entries[1];
        Assert.False(placeholder.HasPosition);
        Assert.Null(placeholder.SuperClass);
        Assert.Null(placeholder.CellType);
    }

    [Fact]
    public async Task GetSessionLayoutAsync_ExposesLayoutNotReady()
    {
        using var httpClient = CreateHttpClient(_ => JsonResponse(HttpStatusCode.ServiceUnavailable, """
            {"error":{"code":"LAYOUT_NOT_READY","message":"Neuron positions are not available.","details":{}}}
            """));
        var client = new BrainServiceClient(httpClient);

        var exception = await Assert.ThrowsAsync<BrainServiceException>(() => client.GetSessionLayoutAsync("s"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("LAYOUT_NOT_READY", exception.Code);
    }

    [Fact]
    public async Task GetFullSessionLayoutAsync_PagesThroughAllRootsInOrder()
    {
        var requested = new List<(int Offset, int Limit)>();
        using var httpClient = CreateHttpClient(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var offset = int.Parse(query["offset"]!);
            var limit = int.Parse(query["limit"]!);
            requested.Add((offset, limit));
            return JsonResponse(HttpStatusCode.OK, Page(offset, limit, total: 5));
        });
        var client = new BrainServiceClient(httpClient);

        var entries = await client.GetFullSessionLayoutAsync("s", pageSize: 2);

        Assert.Equal(new[] { (0, 2), (2, 2), (4, 2) }, requested);
        Assert.Equal(new long[] { 100, 101, 102, 103, 104 }, entries.Select(e => e.RootId));
    }

    [Fact]
    public async Task GetFullSessionLayoutAsync_HandlesAnEmptySessionWithOneRequest()
    {
        var calls = 0;
        using var httpClient = CreateHttpClient(_ =>
        {
            calls++;
            return JsonResponse(HttpStatusCode.OK, Page(0, 4096, total: 0));
        });

        var entries = await new BrainServiceClient(httpClient).GetFullSessionLayoutAsync("s");

        Assert.Empty(entries);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetFullSessionLayoutAsync_RejectsAnEmptyPageBeforeTheEnd()
    {
        using var httpClient = CreateHttpClient(request =>
        {
            var offset = int.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["offset"]!);
            return JsonResponse(HttpStatusCode.OK, offset == 0 ? Page(0, 2, total: 5) : Page(offset, 0, total: 5));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BrainServiceClient(httpClient).GetFullSessionLayoutAsync("s", pageSize: 2));
    }

    [Fact]
    public async Task GetFullSessionLayoutAsync_RejectsAChangingRootCount()
    {
        using var httpClient = CreateHttpClient(request =>
        {
            var offset = int.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["offset"]!);
            return JsonResponse(HttpStatusCode.OK, Page(offset, 2, total: offset == 0 ? 5 : 6));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BrainServiceClient(httpClient).GetFullSessionLayoutAsync("s", pageSize: 2));
    }

    [Fact]
    public async Task GetFullSessionLayoutAsync_RejectsAnInvalidPageSize()
    {
        using var httpClient = CreateHttpClient(_ => throw new InvalidOperationException("no request expected"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new BrainServiceClient(httpClient).GetFullSessionLayoutAsync("s", pageSize: 0));
    }

    private static string Page(int offset, int count, int total)
    {
        var returned = Math.Max(0, Math.Min(count, total - offset));
        var entries = Enumerable.Range(offset, returned).Select(index =>
            $$"""{"root_id":{{100 + index}},"x":{{index}}.5,"y":0.0,"z":0.0,"has_position":true,"super_class":"central","cell_class":null,"cell_type":null,"side":"left"}""");
        return $$"""{"session_id":"s","offset":{{offset}},"limit":{{Math.Max(count, 1)}},"returned_count":{{returned}},"total_root_count":{{total}},"coordinate_unit":"um","entries":[{{string.Join(",", entries)}}]}""";
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new StubHandler(handler)) { BaseAddress = new Uri("http://127.0.0.1:8765/") };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
