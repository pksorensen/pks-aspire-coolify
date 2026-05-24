using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-007 — TC-011: envvar_sync_service_scope_redaction_and_skip_for_FT008.
public sealed class EnvVarSyncExitCriteriaTests
{
    private const string SentinelApiKey = "SENTINEL_PARAM_DO_NOT_LEAK_envvar_sync";
    private const string SentinelDbPassword = "SENTINEL_PARAM_DB_PASSWORD_DO_NOT_LEAK";

    private static readonly string[] s_sentinels = new[] { SentinelApiKey, SentinelDbPassword };

    // ──────────────────────────────────────────────────────────────────────────
    // Fakes
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeResource : Resource, IResource
    {
        public FakeResource(string name) : base(name) { }
    }

    private sealed record EnvVarRow(string Key, string Value, bool Secret);

    private sealed class RecordingEnvVarsApi : IServiceEnvVarsApi
    {
        public List<(string Op, string ServiceId, string Key)> Calls { get; } = new();

        // Mutable in-memory state per (serviceId, key).
        public Dictionary<(string ServiceId, string Key), EnvVarRow> Store { get; } = new();

        // Overrides per (op, key).
        public Func<string, EnvVarFetchResult>? FetchOverride { get; set; }
        public Func<string, EnvVarWriteResult>? CreateOverride { get; set; }
        public Func<string, EnvVarWriteResult>? PatchOverride { get; set; }

        public Task<EnvVarFetchResult> GetByNameAsync(string serviceId, string key, CancellationToken ct)
        {
            Calls.Add(("GET", serviceId, key));
            if (FetchOverride is not null)
            {
                var forced = FetchOverride(key);
                if (forced is not null) return Task.FromResult(forced);
            }
            if (Store.TryGetValue((serviceId, key), out var row))
            {
                return Task.FromResult(EnvVarFetchResult.FoundWith(row.Value, row.Secret));
            }
            return Task.FromResult(EnvVarFetchResult.NotFoundResult());
        }

        public Task<EnvVarWriteResult> CreateAsync(string serviceId, string key, string value, bool secret, CancellationToken ct)
        {
            Calls.Add(("POST", serviceId, key));
            if (CreateOverride is not null)
            {
                var forced = CreateOverride(key);
                if (forced is not null) return Task.FromResult(forced);
            }
            Store[(serviceId, key)] = new EnvVarRow(key, value, secret);
            return Task.FromResult(EnvVarWriteResult.Ok());
        }

        public Task<EnvVarWriteResult> PatchAsync(string serviceId, string key, string value, bool secret, CancellationToken ct)
        {
            Calls.Add(("PATCH", serviceId, key));
            if (PatchOverride is not null)
            {
                var forced = PatchOverride(key);
                if (forced is not null) return Task.FromResult(forced);
            }
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

    private static (EnvVarSyncHook Hook, FakeClient Client, ListLogger Log, StringWriter Err)
        BuildHook(
            ParameterReferenceProvider paramRefs,
            ConnectionStringNameProvider? csNames = null,
            ConnectionStringResolver? csResolver = null)
    {
        var client = new FakeClient();
        var log = new ListLogger();
        var err = new StringWriter();
        var hook = new EnvVarSyncHook(
            clientProvider: () => client,
            parameterRefsProvider: paramRefs,
            connectionStringNamesProvider: csNames ?? (_ => Array.Empty<string>()),
            connectionStringResolver: csResolver,
            sentinels: s_sentinels)
        {
            ErrorWriter = err,
            Logger = log,
        };
        return (hook, client, log, err);
    }

    private static DeployHookContext CtxFor(string resource, string serviceId) =>
        new(ProjectId: "proj-1", EnvironmentId: "env-1", ServiceId: serviceId, Resource: new FakeResource(resource));

    // ──────────────────────────────────────────────────────────────────────────
    // A / B / D / E. Happy path: hook invoked once, only service-scope writes, key naming
    // verbatim, secret flag round-trips, every POST preceded by GET-by-name.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiResource_WritesParameterEnvVars_VerbatimNames_AndSecretFlagsRoundTrip()
    {
        var paramRefs = new ParameterReferenceProvider((res, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            res.Name == "api"
                ? new[]
                {
                    new ParameterReference("app-greeting", "hello", Secret: false),
                    new ParameterReference("api-key", SentinelApiKey, Secret: true),
                }
                : Array.Empty<ParameterReference>()));

        var csNames = new ConnectionStringNameProvider(res =>
            res.Name == "api" ? new[] { "ConnectionStrings__db" } : Array.Empty<string>());

        var (hook, client, log, err) = BuildHook(paramRefs, csNames, csResolver: null);

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("", err.ToString());
        Assert.Equal(1, hook.InvocationCount);

        // GET-by-name precedes POST for every key written.
        var apiKeyCalls = client.EnvVarsApi.Calls.Where(c => c.Key == "api-key").ToList();
        Assert.Equal("GET", apiKeyCalls[0].Op);
        Assert.Equal("POST", apiKeyCalls[1].Op);

        // Verbatim key naming — no APP_GREETING upper-snake.
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "app-greeting")));
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "api-key")));
        Assert.False(client.EnvVarsApi.Store.ContainsKey(("svc-api", "APP_GREETING")));
        Assert.False(client.EnvVarsApi.Store.ContainsKey(("svc-api", "ConnectionStrings__db")));

        // Secret flag round-trips: api-key SET, app-greeting CLEAR.
        Assert.True(client.EnvVarsApi.Store[("svc-api", "api-key")].Secret);
        Assert.False(client.EnvVarsApi.Store[("svc-api", "app-greeting")].Secret);

        // Skip-with-log line for unresolved connection-string (FT-008 absent).
        Assert.Contains(log.Lines, l => l.Contains("envvar-skipped") && l.Contains("ConnectionStrings__db") && l.Contains("awaiting-FT-008"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // F. Redaction across all surfaces: stderr / log / store-value-write never echoes a sentinel
    //    string into surfaces FT-007 controls.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoSurfaceLeaksTheSentinel_OnHappyPath()
    {
        var paramRefs = new ParameterReferenceProvider((res, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            new[]
            {
                new ParameterReference("app-greeting", "hello", false),
                new ParameterReference("api-key", SentinelApiKey, true),
                new ParameterReference("db-password", SentinelDbPassword, true),
            }));

        var (hook, _, log, err) = BuildHook(paramRefs);

        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.DoesNotContain(SentinelApiKey, err.ToString());
        Assert.DoesNotContain(SentinelDbPassword, err.ToString());
        foreach (var line in log.Lines)
        {
            Assert.DoesNotContain(SentinelApiKey, line);
            Assert.DoesNotContain(SentinelDbPassword, line);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // F (cont). Sentinel injected into a Coolify mock response excerpt → SECRET_LEAKED,
    // original line suppressed.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CoolifyResponseExcerptContainingSentinel_EscalatesToSecretLeaked()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            new[] { new ParameterReference("app-greeting", "hello", false) }));

        var (hook, client, _, err) = BuildHook(paramRefs);

        // Force the GET-by-name to return a failure with a sentinel-laced excerpt.
        client.EnvVarsApi.FetchOverride = _ =>
            EnvVarFetchResult.FailureWith($"upstream error excerpt {SentinelApiKey} returned");

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("E_ENVVAR_SECRET_LEAKED", err.ToString());
        Assert.DoesNotContain(SentinelApiKey, err.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // G. Pre-flight sentinel scan on a deliberately-bugged non-secret value → SECRET_LEAKED
    //    BEFORE any env-var call.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NonSecretValueCarryingSentinel_FailsBeforeAnyEnvVarCall()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            new[]
            {
                // Simulate redaction-pipeline bug: secret: false but value carries a sentinel.
                new ParameterReference("oops", $"non-secret-{SentinelApiKey}-value", Secret: false),
            }));

        var (hook, client, _, err) = BuildHook(paramRefs);

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("E_ENVVAR_SECRET_LEAKED", err.ToString());
        Assert.Empty(client.EnvVarsApi.Calls); // zero env-var endpoint requests
        Assert.DoesNotContain(SentinelApiKey, err.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // H. Orphan env-vars left in place — re-running over an existing key set does not
    //    delete the orphan-key Coolify holds.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrphanEnvVar_IsNotTouchedByHook()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            new[] { new ParameterReference("app-greeting", "hello", false) }));

        var (hook, client, _, _) = BuildHook(paramRefs);

        // Pre-seed an orphan key that no AppHost reference produces.
        client.EnvVarsApi.Store[("svc-api", "orphan-key")] = new EnvVarRow("orphan-key", "v", false);

        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        // No GET / PATCH / DELETE issued against orphan-key.
        Assert.DoesNotContain(client.EnvVarsApi.Calls, c => c.Key == "orphan-key");
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "orphan-key")));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // I. Aggregated per-key failure — the loop attempts every key before surfacing.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PerKeyFailures_AreAggregatedIntoOneSymbol_KeysOnly_NoValues()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            new[]
            {
                new ParameterReference("k1", "v1", false),
                new ParameterReference("k2", "v2", false),
                new ParameterReference("k3", "v3", false),
            }));

        var (hook, client, _, err) = BuildHook(paramRefs);
        client.EnvVarsApi.CreateOverride = key =>
            key == "k1" || key == "k2" ? EnvVarWriteResult.Failure("HTTP 500 generic") : null!;

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("E_ENVVAR_UPSERT_FAILED", result.FailureSymbol);
        Assert.StartsWith("E_ENVVAR_UPSERT_FAILED", err.ToString());
        Assert.Contains("k1", err.ToString());
        Assert.Contains("k2", err.ToString());
        // Values are NOT included.
        Assert.DoesNotContain("v1", err.ToString());
        Assert.DoesNotContain("v2", err.ToString());

        // All three keys were attempted.
        Assert.Equal(3, client.EnvVarsApi.Calls.Count(c => c.Op == "GET"));

        // Third (succeeding) key remained in Coolify.
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "k3")));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // J. Idempotency on unchanged AppHost — second invocation takes 'unchanged' branch
    //    on every key.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecondInvocation_OnUnchangedAppHost_HitsUnchangedBranchOnly()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            new[]
            {
                new ParameterReference("app-greeting", "hello", false),
                new ParameterReference("api-key", "secret-value", true),
            }));

        var (hook, client, log, _) = BuildHook(paramRefs);

        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);
        // Snapshot calls after first run.
        var afterFirst = client.EnvVarsApi.Calls.Count;
        log.Lines.Clear();
        await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        // Second invocation issued GETs but zero POST/PATCH calls.
        var afterSecond = client.EnvVarsApi.Calls.Skip(afterFirst).ToList();
        Assert.All(afterSecond, c => Assert.Equal("GET", c.Op));
        // All keys logged as unchanged in second run.
        Assert.Contains(log.Lines, l => l.Contains("envvar-unchanged") && l.Contains("app-greeting"));
        Assert.Contains(log.Lines, l => l.Contains("envvar-unchanged") && l.Contains("api-key"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // K. Managed-field discipline on PATCH — drift causes PATCH with value/secret-flag only.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValueDrift_TriggersPatch_WithDriftWarningEmitted()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            new[] { new ParameterReference("app-greeting", "hello", false) }));

        var (hook, client, log, _) = BuildHook(paramRefs);

        // Pre-seed a drifted value on Coolify's side.
        client.EnvVarsApi.Store[("svc-api", "app-greeting")] = new EnvVarRow("app-greeting", "out-of-band", false);

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(client.EnvVarsApi.Calls, c => c.Op == "PATCH" && c.Key == "app-greeting");
        Assert.Equal("hello", client.EnvVarsApi.Store[("svc-api", "app-greeting")].Value);
        Assert.Contains(log.Lines, l => l.Contains("drift-overwritten") && l.Contains("app-greeting"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // L. SECRET_LEAKED dominates UPSERT_FAILED when a failing-key response excerpt
    //    contains a sentinel.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FailingKeyExcerptCarryingSentinel_EscalatesToSecretLeaked()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            new[] { new ParameterReference("k1", "v1", false) }));

        var (hook, client, _, err) = BuildHook(paramRefs);
        client.EnvVarsApi.CreateOverride = _ =>
            EnvVarWriteResult.Failure($"422 body: {SentinelApiKey} appears here");

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("E_ENVVAR_SECRET_LEAKED", err.ToString());
        Assert.Equal("E_ENVVAR_SECRET_LEAKED", result.FailureSymbol);
        Assert.DoesNotContain(SentinelApiKey, err.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // N. Stable observable contract — three E_… symbols' exact spellings.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThreeSymbols_HaveExactObservableLiterals()
    {
        Assert.Equal("E_ENVVAR_UPSERT_FAILED", EnvVarSymbol.EnvVarUpsertFailed.Literal());
        Assert.Equal("E_ENVVAR_SECRET_LEAKED", EnvVarSymbol.EnvVarSecretLeaked.Literal());
        Assert.Equal("E_ENVVAR_PHASE_UNEXPECTED", EnvVarSymbol.EnvVarPhaseUnexpected.Literal());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // E_ENVVAR_PHASE_UNEXPECTED — catch-all on provider exception (non-sentinel).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderException_WithoutSentinel_SurfacesPhaseUnexpected()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) =>
            throw new InvalidOperationException("resolver kaput"));

        var (hook, _, _, err) = BuildHook(paramRefs);

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("E_ENVVAR_PHASE_UNEXPECTED", err.ToString());
        Assert.Equal("E_ENVVAR_PHASE_UNEXPECTED", result.FailureSymbol);
    }

    [Fact]
    public async Task ProviderException_WithSentinel_SurfacesSecretLeakedInsteadOfUnexpected()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) =>
            throw new InvalidOperationException($"resolver inner: {SentinelDbPassword} leaked"));

        var (hook, _, _, err) = BuildHook(paramRefs);

        var result = await hook.InvokeAsync(CtxFor("db", "svc-db"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("E_ENVVAR_SECRET_LEAKED", err.ToString());
        Assert.DoesNotContain(SentinelDbPassword, err.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // C / I composition with FT-005 — when hook surfaces a fail-fast result, the deploy
    // phase surfaces that symbol verbatim (not E_DEPLOY_PHASE_UNEXPECTED). This is
    // already exercised by DeployPhaseExitCriteriaTests.HookFailure_SurfacesHookSymbolNotDeployUnexpected;
    // here we sanity-check that an FT-007 hook plugs into the publisher cleanly.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hook_IsAddressableAsICoolifyDeployHook()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            Array.Empty<ParameterReference>()));
        var (hook, _, _, _) = BuildHook(paramRefs);
        ICoolifyDeployHook asHook = hook;
        var result = await asHook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FT-008 forward-compat — connection-string with a supplied value is written.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectionString_WithSuppliedValue_IsWrittenAsEnvVar()
    {
        var paramRefs = new ParameterReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<ParameterReference>>(
            Array.Empty<ParameterReference>()));
        var csNames = new ConnectionStringNameProvider(_ => new[] { "ConnectionStrings__db" });
        var csResolver = new ConnectionStringResolver(name =>
            name == "ConnectionStrings__db"
                ? new ConnectionStringValue("Host=db;Pwd=x", Secret: true)
                : null);

        var (hook, client, _, _) = BuildHook(paramRefs, csNames, csResolver);

        var result = await hook.InvokeAsync(CtxFor("api", "svc-api"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(client.EnvVarsApi.Store.ContainsKey(("svc-api", "ConnectionStrings__db")));
        Assert.True(client.EnvVarsApi.Store[("svc-api", "ConnectionStrings__db")].Secret);
    }
}
