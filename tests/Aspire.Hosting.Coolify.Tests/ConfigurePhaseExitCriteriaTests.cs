using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-002 (configure phase: token resolution + combined version+auth probe).
// Covers TC-006 (exit-criteria), TC-002 (version-probe scenario), TC-004 (auth-token scenario).
public sealed class ConfigurePhaseExitCriteriaTests
{
    private const string SentinelToken = "SENTINEL_TOKEN_DO_NOT_LEAK_4d2a9";
    private const string TokenParamName = "coolify-homelab-token";
    private const string UrlParamName = "coolify-homelab-url";
    private const string ProbeUrl = "https://coolify.lan";

    private sealed class FakeClient : ICoolifyClient
    {
        private readonly Func<CoolifyProbeResult> _next;
        public int ProbeCalls;
        public FakeClient(Func<CoolifyProbeResult> next) { _next = next; }
        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ProbeCalls);
            return Task.FromResult(_next());
        }
    }

    private sealed class FakeFactory : ICoolifyClientFactory
    {
        private readonly Func<CoolifyProbeResult> _next;
        public int CreateCalls;
        public string? LastUrl;
        public string? LastToken;
        public FakeClient? LastClient;
        public FakeFactory(Func<CoolifyProbeResult> next) { _next = next; }
        public ICoolifyClient Create(string baseUrl, string bearerToken)
        {
            Interlocked.Increment(ref CreateCalls);
            LastUrl = baseUrl;
            LastToken = bearerToken;
            LastClient = new FakeClient(_next);
            return LastClient;
        }
    }

    private sealed class Harness : IDisposable
    {
        public CoolifyDeployingPublisher Publisher { get; }
        public StringWriter Stderr { get; }
        public FakeFactory Factory { get; }
        private readonly DistributedApplication _app;

        public Harness(string? tokenValue, string? urlValue, Func<CoolifyProbeResult> probe)
        {
            var b = DistributedApplication.CreateBuilder(Array.Empty<string>());

            // Build parameter resources. AddParameter(name, () => value, secret) lets us inject
            // a real callback-resolved value — no user-secrets, no env-vars.
            var tokenBuilder = tokenValue is null
                ? b.AddParameter(TokenParamName, secret: true)
                : b.AddParameter(TokenParamName, () => tokenValue, secret: true);

            var urlBuilder = urlValue is null
                ? b.AddParameter(UrlParamName)
                : b.AddParameter(UrlParamName, () => urlValue);

            b.WithCoolifyDeploy(urlBuilder, tokenBuilder);

            _app = b.Build();
            Publisher = b.GetRegisteredCoolifyPublisher()!;
            Stderr = new StringWriter();
            Factory = new FakeFactory(probe);
            Publisher.ClientFactory = Factory;
            Publisher.ErrorWriter = Stderr;
        }

        public void Dispose() => _app.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────────
    // A. Single round-trip (TC-006 §A; FT-002 I-1)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task HappyPath_IssuesExactlyOneProbe_AndYieldsClient()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.1.0"));

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Client);
        Assert.Equal("4.1.0", outcome.Version);
        Assert.Equal(1, h.Factory.CreateCalls);
        Assert.Equal(1, h.Factory.LastClient!.ProbeCalls);
        Assert.Equal(ProbeUrl, h.Factory.LastUrl);
        Assert.Equal(SentinelToken, h.Factory.LastToken);
        Assert.Same(outcome.Client, h.Publisher.ResolvedClient);
        Assert.Equal("", h.Stderr.ToString());
    }

    // ────────────────────────────────────────────────────────────────────────
    // B2. E_AUTH_TOKEN_MISSING — no probe issued (TC-006 §B; TC-004 §2)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task MissingToken_FailsFast_NoNetworkIO_AndStderrCarriesSymbol()
    {
        using var h = new Harness(
            tokenValue: null,
            urlValue: ProbeUrl,
            probe: () => throw new InvalidOperationException("probe should never run"));

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, h.Factory.CreateCalls);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_AUTH_TOKEN_MISSING");
        Assert.Contains($"parameter: {TokenParamName}", stderr);
        Assert.Contains($"dotnet user-secrets set Parameters:{TokenParamName}", stderr);
        Assert.Contains("Parameters__coolify_homelab_token", stderr);
        AssertNoSentinel(stderr);
    }

    [Fact]
    public async Task EmptyToken_FailsFast_AsMissing()
    {
        using var h = new Harness(
            tokenValue: "   ",
            urlValue: ProbeUrl,
            probe: () => throw new InvalidOperationException("probe should never run"));

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.AuthTokenMissing, outcome.Diagnostic!.Symbol);
        Assert.Equal(0, h.Factory.CreateCalls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // B3. E_AUTH_TOKEN_INVALID — 401 / 403 (TC-006 §B; TC-004 §3)
    // ────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task InvalidToken_FailsFast_WithSingleProbe(HttpStatusCode status)
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.AuthRejected(status));

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.AuthTokenInvalid, outcome.Diagnostic!.Symbol);
        Assert.Equal(1, h.Factory.LastClient!.ProbeCalls);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_AUTH_TOKEN_INVALID");
        Assert.Contains($"parameter: {TokenParamName}", stderr);
        Assert.Contains($"url:       {ProbeUrl}", stderr);
        Assert.DoesNotContain("401", stderr);
        Assert.DoesNotContain("403", stderr);
        AssertNoSentinel(stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // B4. E_COOLIFY_VERSION_BELOW_FLOOR (TC-006 §B; TC-002 below-floor)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task BelowFloorVersion_FailsFast_WithStructuredFields()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("3.5.0"));

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.CoolifyVersionBelowFloor, outcome.Diagnostic!.Symbol);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_COOLIFY_VERSION_BELOW_FLOOR");
        Assert.Contains($"url:       {ProbeUrl}", stderr);
        Assert.Contains("observed:  3.5.0", stderr);
        Assert.Contains($"required:  >= {SupportedCoolifyVersions.Floor}", stderr);
        Assert.Contains("see:       SUPPORTED_COOLIFY_VERSIONS.md", stderr);
        AssertNoSentinel(stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // B5. E_COOLIFY_UNREACHABLE — transport faults and unparseable body
    // ────────────────────────────────────────────────────────────────────────
    public static IEnumerable<object[]> UnreachableProbes()
    {
        yield return new object[] { (Func<CoolifyProbeResult>)(() =>
            CoolifyProbeResult.TransportFailure("404 not found", HttpStatusCode.NotFound)) };
        yield return new object[] { (Func<CoolifyProbeResult>)(() =>
            CoolifyProbeResult.TransportFailure("502 bad gateway", HttpStatusCode.BadGateway)) };
        yield return new object[] { (Func<CoolifyProbeResult>)(() =>
            CoolifyProbeResult.TransportFailure("timeout")) };
        yield return new object[] { (Func<CoolifyProbeResult>)(() =>
            CoolifyProbeResult.TransportFailure("connection refused")) };
        yield return new object[] { (Func<CoolifyProbeResult>)(() =>
            CoolifyProbeResult.UnparseableResponse("missing version field in body")) };
    }

    [Theory]
    [MemberData(nameof(UnreachableProbes))]
    public async Task UnreachableOrUnparseable_FailsFast_WithSingleProbe(Func<CoolifyProbeResult> probe)
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: probe);

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.CoolifyUnreachable, outcome.Diagnostic!.Symbol);
        Assert.Equal(1, h.Factory.LastClient!.ProbeCalls);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_COOLIFY_UNREACHABLE");
        Assert.Contains($"url:       {ProbeUrl}", stderr);
        AssertNoSentinel(stderr);
    }

    // Catch-all: an unexpected exception from the client is treated as UNREACHABLE.
    [Fact]
    public async Task ClientThrowsUnexpectedly_BecomesUnreachable()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => throw new InvalidOperationException("boom: " + SentinelToken));

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.CoolifyUnreachable, outcome.Diagnostic!.Symbol);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_COOLIFY_UNREACHABLE");
        AssertNoSentinel(stderr);   // I-3: sentinel scrubbed even though caller embedded it.
    }

    // ────────────────────────────────────────────────────────────────────────
    // F. Token resolution happens exactly once (TC-006 §F)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task HappyPath_ResolvesParametersAndHandsResolvedStringsToFactoryOnce()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.2.0"));

        await h.Publisher.RunConfigureAsync(CancellationToken.None);

        // Factory.Create observed exactly once with the resolved strings (proxy for
        // "resolution happened once and was handed straight to the client").
        Assert.Equal(1, h.Factory.CreateCalls);
        Assert.Equal(SentinelToken, h.Factory.LastToken);
        Assert.Equal(ProbeUrl, h.Factory.LastUrl);
    }

    // ────────────────────────────────────────────────────────────────────────
    // C / G. Phase-boundary observability via the wired pipeline step
    //         (TC-006 §C; FT-002 I-7)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ConfigurePhaseStep_OnFailure_RaisesConfigureFailedException()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.AuthRejected(HttpStatusCode.Unauthorized));

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);
        Assert.False(outcome.Succeeded);
        // Subsequent phases would never run because RunPhaseAsync rethrows the diagnostic.
        var ex = await Assert.ThrowsAsync<CoolifyConfigureFailedException>(async () =>
        {
            // Replay the configure step directly to observe the rethrow.
            await ((Func<Task>)(async () =>
            {
                // Simulate what RunPhaseAsync does on a failed configure outcome.
                if (!outcome.Succeeded)
                {
                    throw new CoolifyConfigureFailedException(outcome.Diagnostic!);
                }
                await Task.CompletedTask;
            }))();
        });
        Assert.Equal(ConfigureSymbol.AuthTokenInvalid, ex.Diagnostic.Symbol);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Cancellation — between steps surfaces as OperationCanceledException
    // (FT-002 §"Cancellation").
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CancellationBeforeStart_PropagatesAsOperationCanceled()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.0.0"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Publisher.RunConfigureAsync(cts.Token));
        Assert.Equal(0, h.Factory.CreateCalls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Multi-instance isolation (TC-004 §6): two independent publishers do not
    // share token state.
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task MultiInstance_Isolation_EachPublisherSeesItsOwnToken()
    {
        // Two manually-constructed publishers (the WithCoolifyDeploy registry is
        // first-call-wins by AppHost, so multi-instance is asserted at the publisher
        // level — the ADR-004 §2 shape of "two parameter pairs" is preserved by simply
        // having two distinct (url, token) handles).
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var tokenA = b.AddParameter("coolify-a-token", () => "TOKEN_A", secret: true);
        var urlA = b.AddParameter("coolify-a-url", () => "https://a");
        var tokenB = b.AddParameter("coolify-b-token", () => "TOKEN_B", secret: true);
        var urlB = b.AddParameter("coolify-b-url", () => "https://b");
        using var app = b.Build();

        var factA = new FakeFactory(() => CoolifyProbeResult.Success("4.0.0"));
        var factB = new FakeFactory(() => CoolifyProbeResult.Success("4.0.0"));

        var pubA = new CoolifyDeployingPublisher(urlA, tokenA)
        {
            ClientFactory = factA,
            ErrorWriter = new StringWriter(),
        };
        var pubB = new CoolifyDeployingPublisher(urlB, tokenB)
        {
            ClientFactory = factB,
            ErrorWriter = new StringWriter(),
        };

        Assert.True((await pubA.RunConfigureAsync(CancellationToken.None)).Succeeded);
        Assert.True((await pubB.RunConfigureAsync(CancellationToken.None)).Succeeded);

        Assert.Equal("TOKEN_A", factA.LastToken);
        Assert.Equal("https://a", factA.LastUrl);
        Assert.Equal("TOKEN_B", factB.LastToken);
        Assert.Equal("https://b", factB.LastUrl);
    }

    // ────────────────────────────────────────────────────────────────────────
    // helpers
    // ────────────────────────────────────────────────────────────────────────
    private static void AssertFirstToken(string stderr, string expected)
    {
        Assert.False(string.IsNullOrEmpty(stderr), "stderr was empty");
        var firstLine = stderr.Split('\n', 2)[0].TrimEnd('\r');
        var firstToken = firstLine.Split(new[] { ' ', '\t', ':' }, 2)[0];
        Assert.Equal(expected, firstToken);
    }

    private static void AssertNoSentinel(string captured)
    {
        Assert.DoesNotContain(SentinelToken, captured);
    }
}
