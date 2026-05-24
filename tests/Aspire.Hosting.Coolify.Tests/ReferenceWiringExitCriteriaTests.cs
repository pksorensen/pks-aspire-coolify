using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-008 — TC-012: reference_wiring_endpoint_and_connection_string_envvars.
public sealed class ReferenceWiringExitCriteriaTests
{
    private const string SentinelDbPassword = "SENTINEL_DB_PASSWORD_DO_NOT_LEAK_ref";
    private const string SentinelOther = "SENTINEL_OTHER_DO_NOT_LEAK_ref";
    private static readonly string[] s_sentinels = new[] { SentinelDbPassword, SentinelOther };

    // ──────────────────────────────────────────────────────────────────────────
    // Fakes (mirror EnvVarSyncExitCriteriaTests so endpoint-group path equality
    // — TC-012 §B3 — is asserted by both suites pointing at the same surface).
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeResource : Resource, IResource
    {
        public FakeResource(string name) : base(name) { }
    }

    private sealed record EnvVarRow(string Key, string Value, bool Secret);

    private sealed class RecordingEnvVarsApi : IServiceEnvVarsApi
    {
        public List<(string Op, string ServiceId, string Key)> Calls { get; } = new();
        public Dictionary<(string ServiceId, string Key), EnvVarRow> Store { get; } = new();
        public Func<string, EnvVarFetchResult?>? FetchOverride { get; set; }
        public Func<string, EnvVarWriteResult?>? CreateOverride { get; set; }
        public Func<string, EnvVarWriteResult?>? PatchOverride { get; set; }

        public Task<EnvVarFetchResult> GetByNameAsync(string serviceId, string key, CancellationToken ct)
        {
            Calls.Add(("GET", serviceId, key));
            var forced = FetchOverride?.Invoke(key);
            if (forced is not null) return Task.FromResult(forced);
            if (Store.TryGetValue((serviceId, key), out var row))
                return Task.FromResult(EnvVarFetchResult.FoundWith(row.Value, row.Secret));
            return Task.FromResult(EnvVarFetchResult.NotFoundResult());
        }

        public Task<EnvVarWriteResult> CreateAsync(string serviceId, string key, string value, bool secret, CancellationToken ct)
        {
            Calls.Add(("POST", serviceId, key));
            var forced = CreateOverride?.Invoke(key);
            if (forced is not null) return Task.FromResult(forced);
            Store[(serviceId, key)] = new EnvVarRow(key, value, secret);
            return Task.FromResult(EnvVarWriteResult.Ok());
        }

        public Task<EnvVarWriteResult> PatchAsync(string serviceId, string key, string value, bool secret, CancellationToken ct)
        {
            Calls.Add(("PATCH", serviceId, key));
            var forced = PatchOverride?.Invoke(key);
            if (forced is not null) return Task.FromResult(forced);
            Store[(serviceId, key)] = new EnvVarRow(key, value, secret);
            return Task.FromResult(EnvVarWriteResult.Ok());
        }
    }

    private sealed class FakeClient : ICoolifyClient
    {
        public RecordingEnvVarsApi EnvVarsApi { get; } = new();
        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(CoolifyProbeResult.Success("4.1.0"));
        public IServiceEnvVarsApi ServiceEnvVars => EnvVarsApi;
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }
    }

    private static DeployHookContext CtxFor(string resource, string serviceId) =>
        new(ProjectId: "proj-1", EnvironmentId: "env-1", ServiceId: serviceId, Resource: new FakeResource(resource));

    /// <summary>
    /// TC-012 fixture: db (CS target) + cache (endpoint target, http@0 + tcp@0) + api
    /// (consumer with one CS ref to db and one endpoint ref to cache). The api service's
    /// hook invocation is the unit under test.
    /// </summary>
    private static (ReferenceWiringHook Hook, FakeClient Client, ListLogger Log, StringWriter Err,
            Dictionary<string, DeployedTarget> Map)
        BuildFixture(
            IReadOnlyList<EndpointReference> endpointRefs,
            IReadOnlyList<ConnectionStringReference> csRefs,
            Dictionary<string, DeployedTarget>? customMap = null,
            ConnectionStringValueComposer? composerOverride = null)
    {
        var map = customMap ?? new Dictionary<string, DeployedTarget>(StringComparer.Ordinal)
        {
            ["db"] = new DeployedTarget("db", "svc-db", "db-internal.coolify",
                new[] { new EndpointDescriptor("tcp", 0, 5432) }),
            ["cache"] = new DeployedTarget("cache", "svc-cache", "cache-internal.coolify",
                new[]
                {
                    new EndpointDescriptor("http", 0, 6379),
                    new EndpointDescriptor("tcp", 0, 6379),
                }),
        };

        var client = new FakeClient();
        var log = new ListLogger();
        var err = new StringWriter();

        ConnectionStringValueComposer composer = composerOverride ?? ((targetName, target) =>
        {
            if (targetName == "db")
            {
                // Resolved template carries an Aspire sentinel (secret param contributed).
                return new ConnectionStringWriteValue(
                    $"Host={target.Hostname};Port=5432;Pwd={SentinelDbPassword}",
                    Secret: true);
            }
            return null;
        });

        var hook = new ReferenceWiringHook(
            clientProvider: () => client,
            endpointRefsProvider: _ => endpointRefs,
            connectionStringRefsProvider: _ => csRefs,
            targetLookup: name => map.TryGetValue(name, out var t) ? t : null,
            connectionStringComposer: composer,
            sentinels: s_sentinels)
        {
            ErrorWriter = err,
            Logger = log,
        };
        return (hook, client, log, err, map);
    }

    private static readonly EndpointReference[] s_cacheEndpointRef = new[] { new EndpointReference("cache") };
    private static readonly ConnectionStringReference[] s_dbCsRef = new[] { new ConnectionStringReference("db") };

    // ──────────────────────────────────────────────────────────────────────────
    // A — invocation count discipline (hook is callable once per service).
    // C/D — single-writer-per-key partition + Aspire-consumer-side key naming.
    // E — Coolify-internal hostname resolution property (value contains hostname).
    // F — rule-based secret-flag policy.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiConsumer_WritesEndpointAndConnectionString_VerbatimNames_HostnameValues_SecretFlagsPerRule()
    {
        var (hook, client, log, err, _) = BuildFixture(s_cacheEndpointRef, s_dbCsRef);

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.True(result.Succeeded, $"hook failed: {err}");
        Assert.Equal("", err.ToString());
        Assert.Equal(1, hook.InvocationCount);

        // D6/D7 — verbatim Aspire-consumer-side names.
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "services__cache__http__0")));
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "services__cache__tcp__0")));
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "ConnectionStrings__db")));
        Assert.False(client.EnvVarsApi.Store.ContainsKey(("svc-api", "services__Cache__http__0"))); // lowercase

        // E8 — Coolify-internal hostname appears in the value.
        var http0 = client.EnvVarsApi.Store[("svc-api", "services__cache__http__0")];
        Assert.Equal("http://cache-internal.coolify:6379", http0.Value);
        var tcp0 = client.EnvVarsApi.Store[("svc-api", "services__cache__tcp__0")];
        Assert.Equal("tcp://cache-internal.coolify:6379", tcp0.Value);
        var dbCs = client.EnvVarsApi.Store[("svc-api", "ConnectionStrings__db")];
        Assert.Contains("db-internal.coolify", dbCs.Value);

        // F9 — secret-flag policy:
        //   ConnectionStrings__db SET (sentinel + secret param contributed),
        //   services__cache__*    CLEAR (plain endpoint URL).
        Assert.True(dbCs.Secret);
        Assert.False(http0.Secret);
        Assert.False(tcp0.Secret);

        // B3 — every POST preceded by GET-by-name on same key.
        foreach (var key in new[] { "services__cache__http__0", "services__cache__tcp__0", "ConnectionStrings__db" })
        {
            var seq = client.EnvVarsApi.Calls.Where(c => c.Key == key).Select(c => c.Op).ToList();
            Assert.Equal("GET", seq[0]);
            Assert.Equal("POST", seq[1]);
        }
    }

    // D6 — endpoint key regex.
    [Fact]
    public async Task EndpointKeys_MatchAspireConsumerRegex()
    {
        var (hook, client, _, _, _) = BuildFixture(s_cacheEndpointRef, Array.Empty<ConnectionStringReference>());
        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        var rx = new System.Text.RegularExpressions.Regex(@"^services__[A-Za-z0-9_-]+__[a-z]+__\d+$");
        foreach (var (_, _, key) in client.EnvVarsApi.Calls.Where(c => c.Op == "POST"))
        {
            Assert.Matches(rx, key);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // G — Target identity is resolved against the in-phase map; zero GETs are
    // issued against Coolify to discover the target serviceId or hostname.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TargetIdentity_ResolvedFromInPhaseMap_NotFromCoolify()
    {
        var lookupCount = 0;
        var map = new Dictionary<string, DeployedTarget>
        {
            ["cache"] = new DeployedTarget("cache", "svc-cache", "cache-host",
                new[] { new EndpointDescriptor("http", 0, 80) }),
        };
        var client = new FakeClient();
        var hook = new ReferenceWiringHook(
            clientProvider: () => client,
            endpointRefsProvider: _ => s_cacheEndpointRef,
            connectionStringRefsProvider: _ => Array.Empty<ConnectionStringReference>(),
            targetLookup: name => { lookupCount++; return map.TryGetValue(name, out var t) ? t : null; },
            connectionStringComposer: (_, _) => null,
            sentinels: s_sentinels);

        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.True(lookupCount >= 1, "target lookup was never consulted");
        // Coolify-side calls are scoped to the consumer service-scope env-var endpoint group only.
        Assert.All(client.EnvVarsApi.Calls, c => Assert.Equal("svc-api", c.ServiceId));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // H — WithReference(parameter) skipped silently — modelled here as: when no
    // endpoint- or connection-string-ref edges are supplied for the resource,
    // FT-008 issues zero env-var calls and emits zero log lines about parameter refs.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParameterReferences_AreSkippedSilently_NoFT008LogLines()
    {
        var (hook, client, log, _, _) = BuildFixture(
            Array.Empty<EndpointReference>(),
            Array.Empty<ConnectionStringReference>());

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(client.EnvVarsApi.Calls);
        Assert.Empty(log.Lines);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // I — TARGET_NOT_DEPLOYED defensive symbol when walk-order regresses.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingTarget_SurfacesTargetNotDeployed_NamesTarget()
    {
        // Empty map → cache is "not yet upserted" → defensive symbol fires.
        var (hook, client, _, err, _) = BuildFixture(
            s_cacheEndpointRef,
            Array.Empty<ConnectionStringReference>(),
            customMap: new Dictionary<string, DeployedTarget>());

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("E_REFERENCE_TARGET_NOT_DEPLOYED", result.FailureSymbol);
        Assert.StartsWith("E_REFERENCE_TARGET_NOT_DEPLOYED", err.ToString());
        Assert.Contains("cache", err.ToString());
        // No env-var work was attempted for the missing-target ref.
        Assert.Empty(client.EnvVarsApi.Calls);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // J — Aggregated per-key failure (loop attempts every key; values omitted).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PerKeyFailures_AreAggregated_KeysOnly_NoValues()
    {
        var (hook, client, _, err, _) = BuildFixture(s_cacheEndpointRef, s_dbCsRef);
        client.EnvVarsApi.CreateOverride = key =>
            key == "services__cache__http__0" || key == "services__cache__tcp__0"
                ? EnvVarWriteResult.Failure("HTTP 500 generic")
                : null;

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("E_REFERENCE_ENVVAR_UPSERT_FAILED", result.FailureSymbol);
        Assert.Contains("services__cache__http__0", err.ToString());
        Assert.Contains("services__cache__tcp__0", err.ToString());
        // Values (hostnames / connection-string text) NOT in the diagnostic.
        Assert.DoesNotContain("6379", err.ToString());
        Assert.DoesNotContain(SentinelDbPassword, err.ToString());
        // Third key (ConnectionStrings__db) was still attempted and remains in Coolify.
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "ConnectionStrings__db")));
        // All three keys saw a GET attempt.
        Assert.Equal(3, client.EnvVarsApi.Calls.Count(c => c.Op == "GET"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // K — Precedence: TARGET_NOT_DEPLOYED > UPSERT_FAILED. A scenario with both
    // a missing target AND a failing upsert surfaces TARGET_NOT_DEPLOYED.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BothMissingTargetAndUpsertFailure_TargetNotDeployedWins()
    {
        var (hook, client, _, err, _) = BuildFixture(
            s_cacheEndpointRef, s_dbCsRef,
            customMap: new Dictionary<string, DeployedTarget>
            {
                // db present, cache missing → mixed scenario.
                ["db"] = new DeployedTarget("db", "svc-db", "db-host",
                    new[] { new EndpointDescriptor("tcp", 0, 5432) }),
            });
        client.EnvVarsApi.CreateOverride = _ => EnvVarWriteResult.Failure("HTTP 500");

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("E_REFERENCE_TARGET_NOT_DEPLOYED", result.FailureSymbol);
        Assert.StartsWith("E_REFERENCE_TARGET_NOT_DEPLOYED", err.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // L — Sentinel-leak escalation uses the SHARED FT-007 symbol literal.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CoolifyResponseExcerptContainingSentinel_EscalatesToSharedEnvVarSecretLeaked()
    {
        var (hook, client, _, err, _) = BuildFixture(s_cacheEndpointRef, Array.Empty<ConnectionStringReference>());
        client.EnvVarsApi.FetchOverride = _ =>
            EnvVarFetchResult.FailureWith($"upstream error excerpt {SentinelOther} appears");

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("E_ENVVAR_SECRET_LEAKED", result.FailureSymbol);
        Assert.StartsWith("E_ENVVAR_SECRET_LEAKED", err.ToString());
        Assert.DoesNotContain(SentinelOther, err.ToString());
        // Not bifurcated into a separate E_REFERENCE_SECRET_LEAKED.
        Assert.DoesNotContain("E_REFERENCE_SECRET_LEAKED", err.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // M — Redaction across all FT-008 surfaces (the connection-string template
    // resolved value carries the sentinel; the env-var is written with secret=true
    // and the sentinel string never appears in any FT-008 surface FT-008 controls).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoSurfaceLeaksTheSentinel_OnHappyPath()
    {
        var (hook, _, log, err, _) = BuildFixture(s_cacheEndpointRef, s_dbCsRef);

        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.DoesNotContain(SentinelDbPassword, err.ToString());
        foreach (var line in log.Lines)
        {
            Assert.DoesNotContain(SentinelDbPassword, line);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // N — Orphan reference env-vars left in place: removing an endpoint ref does
    // not produce a DELETE / PATCH on the orphan key.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrphanReferenceEnvVar_IsNotTouched()
    {
        var (hook, client, _, _, _) = BuildFixture(
            Array.Empty<EndpointReference>(), Array.Empty<ConnectionStringReference>());
        client.EnvVarsApi.Store[("svc-api", "services__cache__http__0")] =
            new EnvVarRow("services__cache__http__0", "http://stale:80", false);

        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.DoesNotContain(client.EnvVarsApi.Calls, c => c.Key == "services__cache__http__0");
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "services__cache__http__0")));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // O — Idempotency on unchanged AppHost: second invocation issues GETs only.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecondInvocation_OnUnchangedAppHost_HitsUnchangedBranchOnly()
    {
        var (hook, client, log, _, _) = BuildFixture(s_cacheEndpointRef, s_dbCsRef);

        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);
        var afterFirst = client.EnvVarsApi.Calls.Count;
        log.Lines.Clear();
        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        var afterSecond = client.EnvVarsApi.Calls.Skip(afterFirst).ToList();
        Assert.All(afterSecond, c => Assert.Equal("GET", c.Op));
        Assert.Contains(log.Lines, l => l.Contains("envvar-unchanged") && l.Contains("services__cache__http__0"));
        Assert.Contains(log.Lines, l => l.Contains("envvar-unchanged") && l.Contains("ConnectionStrings__db"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // P — Managed-field discipline on PATCH (value drift triggers PATCH + warning).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValueDrift_TriggersPatch_WithDriftWarningEmitted()
    {
        var (hook, client, log, _, _) = BuildFixture(s_cacheEndpointRef, Array.Empty<ConnectionStringReference>());
        client.EnvVarsApi.Store[("svc-api", "services__cache__http__0")] =
            new EnvVarRow("services__cache__http__0", "http://out-of-band:99", false);

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(client.EnvVarsApi.Calls, c => c.Op == "PATCH" && c.Key == "services__cache__http__0");
        Assert.Equal("http://cache-internal.coolify:6379",
            client.EnvVarsApi.Store[("svc-api", "services__cache__http__0")].Value);
        Assert.Contains(log.Lines, l => l.Contains("drift-overwritten") && l.Contains("services__cache__http__0"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // R — Stable observable contract: three FT-008 E_… literals + shared symbol.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThreeReferenceSymbols_HaveExactObservableLiterals()
    {
        Assert.Equal("E_REFERENCE_TARGET_NOT_DEPLOYED", ReferenceSymbol.ReferenceTargetNotDeployed.Literal());
        Assert.Equal("E_REFERENCE_ENVVAR_UPSERT_FAILED", ReferenceSymbol.ReferenceEnvVarUpsertFailed.Literal());
        Assert.Equal("E_REFERENCE_PHASE_UNEXPECTED", ReferenceSymbol.ReferencePhaseUnexpected.Literal());
        // The shared leak symbol is byte-identical to FT-007's.
        Assert.Equal("E_ENVVAR_SECRET_LEAKED", EnvVarSymbol.EnvVarSecretLeaked.Literal());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // E_REFERENCE_PHASE_UNEXPECTED — catch-all on provider exception (non-sentinel).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderException_WithoutSentinel_SurfacesReferencePhaseUnexpected()
    {
        var client = new FakeClient();
        var err = new StringWriter();
        var hook = new ReferenceWiringHook(
            clientProvider: () => client,
            endpointRefsProvider: _ => throw new InvalidOperationException("graph walk kaput"),
            connectionStringRefsProvider: _ => Array.Empty<ConnectionStringReference>(),
            targetLookup: _ => null,
            connectionStringComposer: (_, _) => null,
            sentinels: s_sentinels) { ErrorWriter = err };

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("E_REFERENCE_PHASE_UNEXPECTED", result.FailureSymbol);
        Assert.StartsWith("E_REFERENCE_PHASE_UNEXPECTED", err.ToString());
    }

    [Fact]
    public async Task ProviderException_WithSentinel_EscalatesToSharedLeak()
    {
        var client = new FakeClient();
        var err = new StringWriter();
        var hook = new ReferenceWiringHook(
            clientProvider: () => client,
            endpointRefsProvider: _ => throw new InvalidOperationException($"oops {SentinelDbPassword} leaked"),
            connectionStringRefsProvider: _ => Array.Empty<ConnectionStringReference>(),
            targetLookup: _ => null,
            connectionStringComposer: (_, _) => null,
            sentinels: s_sentinels) { ErrorWriter = err };

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("E_ENVVAR_SECRET_LEAKED", result.FailureSymbol);
        Assert.DoesNotContain(SentinelDbPassword, err.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Hook is addressable as ICoolifyDeployHook so FT-005 can wire it after FT-007.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hook_IsAddressableAsICoolifyDeployHook()
    {
        var (hook, _, _, _, _) = BuildFixture(
            Array.Empty<EndpointReference>(), Array.Empty<ConnectionStringReference>());
        ICoolifyDeployHook asHook = hook;
        var result = await asHook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }
}
