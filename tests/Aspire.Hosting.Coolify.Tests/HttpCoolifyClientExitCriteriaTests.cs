using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Coolify;
using Aspire.Hosting.Coolify.Http;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-013 (concrete HTTP implementation of ICoolifyClient).
// Covers TC-022 (probe happy path), TC-023 (transport-failure shape), TC-024 (tolerant
// deserialization), TC-025 (bearer header on every endpoint group), TC-026 (no token leak).
public sealed class HttpCoolifyClientExitCriteriaTests
{
    private const string SentinelToken = "SENTINEL_TOKEN_DO_NOT_LEAK_8a9c2e";
    private const string BaseUrl = "https://coolify.lan";

    // ────────────────────────────────────────────────────────────────────────
    // Captures every outbound request the client issues.
    // ────────────────────────────────────────────────────────────────────────
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<byte[]> RequestBodies { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            }
            else
            {
                RequestBodies.Add(Array.Empty<byte>());
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Responder(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ────────────────────────────────────────────────────────────────────────
    // TC-022 — version-probe happy path
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC022_VersionProbe_HappyPath_IssuesOneAuthenticatedGet_AndReturnsVersion()
    {
        var handler = new CapturingHandler
        {
            Responder = _ => Json(HttpStatusCode.OK, """{"version":"4.1.0"}""")
        };
        using var client = new HttpCoolifyClient(BaseUrl, SentinelToken, handler, timeout: TimeSpan.FromSeconds(5));

        var result = await client.ProbeAsync(CancellationToken.None);

        Assert.Equal(CoolifyProbeKind.Success, result.Kind);
        Assert.Equal("4.1.0", result.Version);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Null(req.Content);
        Assert.NotNull(req.Headers.Authorization);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(SentinelToken, req.Headers.Authorization.Parameter);
        Assert.Equal(new Uri(BaseUrl + "/").Host, req.RequestUri!.Host);

        // No token in the result.
        Assert.DoesNotContain(SentinelToken, result.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelToken, result.Version ?? string.Empty, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-023 — transport-failure shape (one uniform exception across modes)
    // ────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> TransportFailureModes()
    {
        // status-only failure modes synthesised via the handler
        yield return new object[] { "http-500", (Func<HttpResponseMessage>)(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)) };
        yield return new object[] { "http-502", (Func<HttpResponseMessage>)(() => new HttpResponseMessage(HttpStatusCode.BadGateway)) };
        yield return new object[] { "http-404", (Func<HttpResponseMessage>)(() => new HttpResponseMessage(HttpStatusCode.NotFound)) };
    }

    [Theory]
    [MemberData(nameof(TransportFailureModes))]
    public async Task TC023_TransportFailures_ThrowCoolifyTransportException(string label, Func<HttpResponseMessage> respond)
    {
        _ = label;
        var handler = new CapturingHandler { Responder = _ => respond() };
        using var client = new HttpCoolifyClient(BaseUrl, SentinelToken, handler, timeout: TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<CoolifyTransportException>(() =>
            client.SendForJsonAsync<object>(HttpMethod.Get, "api/v1/version", body: null, CancellationToken.None));

        Assert.NotNull(ex);
        // Sentinel must never appear in any thrown text (TC-026 §3).
        Assert.DoesNotContain(SentinelToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelToken, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC023_HttpRequestException_From_Transport_Surfaces_As_CoolifyTransportException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        using var client = new HttpCoolifyClient(BaseUrl, SentinelToken, handler, timeout: TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<CoolifyTransportException>(() =>
            client.SendForJsonAsync<object>(HttpMethod.Get, "api/v1/version", body: null, CancellationToken.None));

        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.DoesNotContain(SentinelToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelToken, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC023_RequestTimeout_Surfaces_As_CoolifyTransportException()
    {
        // TaskCanceledException with no caller-cancellation == HttpClient.Timeout firing.
        var handler = new ThrowingHandler(new TaskCanceledException("timeout"));
        using var client = new HttpCoolifyClient(BaseUrl, SentinelToken, handler, timeout: TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<CoolifyTransportException>(() =>
            client.SendForJsonAsync<object>(HttpMethod.Get, "api/v1/version", body: null, CancellationToken.None));

        Assert.IsType<TaskCanceledException>(ex.InnerException);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TC023_AuthFailure_Is_Distinct_Shape_From_Transport(HttpStatusCode status)
    {
        var handler = new CapturingHandler { Responder = _ => new HttpResponseMessage(status) };
        using var client = new HttpCoolifyClient(BaseUrl, SentinelToken, handler, timeout: TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<CoolifyAuthException>(() =>
            client.SendForJsonAsync<object>(HttpMethod.Get, "api/v1/version", body: null, CancellationToken.None));

        Assert.Equal(status, ex.StatusCode);
        // Must NOT be a transport exception (mutually exclusive shapes per FT-013 §I-7).
        Assert.IsNotType<CoolifyTransportException>(ex);
        Assert.DoesNotContain(SentinelToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelToken, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC023_Cancellation_Surfaces_As_OperationCanceledException_NotTransport()
    {
        // Handler observes the token and throws OCE when cancelled.
        var handler = new CapturingHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        };
        using var client = new HttpCoolifyClient(BaseUrl, SentinelToken, handler, timeout: TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendForJsonAsync<object>(HttpMethod.Get, "api/v1/version", body: null, cts.Token));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) { _ex = ex; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_ex);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-024 — tolerant deserialization
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void TC024_JsonOptions_IgnoreUnknownMembers_OnRead()
    {
        // Body covers: new top-level scalar, new top-level object with nested members,
        // new array of objects, and a known field with a new enum-like value variant
        // ("status":"observed-but-new-variant") — none of which the publisher DTO models.
        var body = """
        {
          "version": "4.1.0",
          "newScalar": 42,
          "newObject": { "a": 1, "b": "two" },
          "newArray": [ { "x": 1 }, { "x": 2 } ],
          "status": "observed-but-new-variant"
        }
        """;

        // Deserializing into a partial DTO must not throw.
        var dto = JsonSerializer.Deserialize<PartialDto>(body, HttpCoolifyClient.JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal("4.1.0", dto!.Version);
    }

    [Fact]
    public async Task TC024_VersionProbe_Tolerates_Additive_Response()
    {
        var handler = new CapturingHandler
        {
            Responder = _ => Json(HttpStatusCode.OK, """
                {"version":"4.1.0","newScalar":42,"newObject":{"a":1},"newArray":[{"x":1}]}
                """)
        };
        using var client = new HttpCoolifyClient(BaseUrl, SentinelToken, handler, timeout: TimeSpan.FromSeconds(5));

        var result = await client.ProbeAsync(CancellationToken.None);

        Assert.Equal(CoolifyProbeKind.Success, result.Kind);
        Assert.Equal("4.1.0", result.Version);
    }

    [Fact]
    public void TC024_JsonOptions_ConservativeSerialization_OmitsNulls()
    {
        var payload = new PartialDto("4.1.0", ExtraOptional: null);
        var json = JsonSerializer.Serialize(payload, HttpCoolifyClient.JsonOptions);

        Assert.Contains("4.1.0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtraOptional", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PartialDto(string? Version, string? ExtraOptional = null);

    // ────────────────────────────────────────────────────────────────────────
    // TC-025 — bearer header on every endpoint group's outbound request
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC025_BearerHeader_On_Every_Endpoint_Group()
    {
        var handler = new CapturingHandler
        {
            Responder = req => req.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{"id":"x","version":"4.1.0","status":"succeeded"}""")
                : Json(HttpStatusCode.OK, """{"id":"x"}""")
        };
        using var client = new HttpCoolifyClient(BaseUrl, SentinelToken, handler, timeout: TimeSpan.FromSeconds(5));

        // Exercise every endpoint group.
        _ = await client.ProbeAsync(CancellationToken.None);                                            // version
        _ = await client.Destinations.LookupOrUpsertAsync("dest", CancellationToken.None);              // destinations
        _ = await client.Projects.UpsertAsync("proj", CancellationToken.None);                          // projects
        _ = await client.Environments.UpsertAsync("proj-id", "Production", CancellationToken.None);     // environments
        _ = await client.Services.UpsertAsync("p", "e", "svc",                                          // services
            new ServiceSpec("img", "ref", "dest"), CancellationToken.None);
        _ = await client.Services.TriggerDeployAsync("svc-id", CancellationToken.None);                 // services (trigger)
        _ = await client.PrivateRegistries.UpsertAsync("h", "u", "p", CancellationToken.None);          // private-registries
        _ = await client.DeployJobs.GetStatusAsync("job-1", CancellationToken.None);                    // deploy-jobs
        _ = await client.ServiceEnvVars.GetByNameAsync("svc", "K", CancellationToken.None);             // env-vars (GET)
        _ = await client.ServiceEnvVars.CreateAsync("svc", "K", "V", false, CancellationToken.None);   // env-vars (POST)
        _ = await client.ServiceEnvVars.PatchAsync("svc", "K", "V2", true, CancellationToken.None);    // env-vars (PATCH)

        Assert.NotEmpty(handler.Requests);
        foreach (var req in handler.Requests)
        {
            Assert.True(req.Headers.Contains("Authorization") || req.Headers.Authorization is not null,
                $"missing Authorization on {req.Method} {req.RequestUri}");
            var auth = req.Headers.Authorization!;
            Assert.Equal("Bearer", auth.Scheme);
            Assert.Equal(SentinelToken, auth.Parameter);
        }

        // At least 11 captured requests (one per endpoint exercise above).
        Assert.True(handler.Requests.Count >= 11,
            $"expected at least 11 outbound requests, got {handler.Requests.Count}");
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-026 — sentinel token never leaks into bodies, exceptions, or returns
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC026_SentinelToken_NeverAppears_In_Bodies_Exceptions_Or_Returns()
    {
        var capturedExceptionText = new List<string>();
        var capturedReturns = new List<string>();

        // ── Run a: happy version probe + every endpoint group with a serialized body ──
        var okHandler = new CapturingHandler
        {
            Responder = req => req.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{"id":"x","version":"4.1.0","status":"succeeded"}""")
                : Json(HttpStatusCode.OK, """{"id":"x"}""")
        };
        using (var c = new HttpCoolifyClient(BaseUrl, SentinelToken, okHandler, timeout: TimeSpan.FromSeconds(5)))
        {
            capturedReturns.Add(JsonSerializer.Serialize(await c.ProbeAsync(CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.Destinations.LookupOrUpsertAsync("d", CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.Projects.UpsertAsync("p", CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.Environments.UpsertAsync("p", "e", CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.Services.UpsertAsync(
                "p", "e", "s", new ServiceSpec("img", "ref", "dest"), CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.Services.TriggerDeployAsync("s", CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.PrivateRegistries.UpsertAsync("h", "u", "pw", CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.DeployJobs.GetStatusAsync("j", CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.ServiceEnvVars.GetByNameAsync("s", "K", CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.ServiceEnvVars.CreateAsync("s", "K", "V", false, CancellationToken.None)));
            capturedReturns.Add(JsonSerializer.Serialize(await c.ServiceEnvVars.PatchAsync("s", "K", "V", true, CancellationToken.None)));
        }

        // Bodies from run a — token must never appear in any body, only in Authorization headers.
        foreach (var body in okHandler.RequestBodies)
        {
            var s = Encoding.UTF8.GetString(body);
            Assert.DoesNotContain(SentinelToken, s, StringComparison.Ordinal);
        }

        // ── Run b: transport failure ──
        var transportHandler = new ThrowingHandler(new HttpRequestException("connection refused"));
        using (var c = new HttpCoolifyClient(BaseUrl, SentinelToken, transportHandler, timeout: TimeSpan.FromSeconds(5)))
        {
            try { _ = await c.SendForJsonAsync<object>(HttpMethod.Get, "api/v1/version", null, CancellationToken.None); }
            catch (Exception ex) { capturedExceptionText.Add(FullText(ex)); }
        }

        // ── Run c: auth failure ──
        var authHandler = new CapturingHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        using (var c = new HttpCoolifyClient(BaseUrl, SentinelToken, authHandler, timeout: TimeSpan.FromSeconds(5)))
        {
            try { _ = await c.SendForJsonAsync<object>(HttpMethod.Get, "api/v1/version", null, CancellationToken.None); }
            catch (Exception ex) { capturedExceptionText.Add(FullText(ex)); }
        }
        foreach (var body in authHandler.RequestBodies)
        {
            Assert.DoesNotContain(SentinelToken, Encoding.UTF8.GetString(body), StringComparison.Ordinal);
        }

        // ── Run d: deserialization failure ──
        var malformedHandler = new CapturingHandler { Responder = _ => Json(HttpStatusCode.OK, "not-json{") };
        using (var c = new HttpCoolifyClient(BaseUrl, SentinelToken, malformedHandler, timeout: TimeSpan.FromSeconds(5)))
        {
            try { _ = await c.SendForJsonAsync<PartialDto>(HttpMethod.Get, "api/v1/version", null, CancellationToken.None); }
            catch (Exception ex) { capturedExceptionText.Add(FullText(ex)); }
        }

        // Sentinel must never appear in any captured exception text.
        foreach (var text in capturedExceptionText)
        {
            Assert.DoesNotContain(SentinelToken, text, StringComparison.Ordinal);
        }

        // Sentinel must never appear in any returned DTO.
        foreach (var ret in capturedReturns)
        {
            Assert.DoesNotContain(SentinelToken, ret, StringComparison.Ordinal);
        }
    }

    private static string FullText(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ex.Message);
        sb.AppendLine(ex.ToString());
        var inner = ex.InnerException;
        while (inner is not null)
        {
            sb.AppendLine(inner.Message);
            sb.AppendLine(inner.ToString());
            inner = inner.InnerException;
        }
        return sb.ToString();
    }
}
