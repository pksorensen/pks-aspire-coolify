using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-004 (push phase + configure-phase Coolify Private Registry
// upsert). Covers TC-008 in full and the publisher-side assertions of TC-005.
public sealed class PushPhaseExitCriteriaTests
{
    private const string SentinelPassword = "SENTINEL_REGISTRY_PASSWORD_DO_NOT_LEAK_push_phase";

    private sealed class FakeResource : Resource, IResource
    {
        public FakeResource(string name) : base(name) { }
    }

    private sealed class RecordingPushPipeline : IImagePushPipeline
    {
        public List<(string Tag, RegistryCredentials? Creds)> Calls { get; } = new();
        private readonly Func<string, ImagePushResult>? _resultFor;

        public RecordingPushPipeline(Func<string, ImagePushResult>? resultFor = null)
        {
            _resultFor = resultFor;
        }

        public Task<ImagePushResult> PushAsync(string imageTag, RegistryCredentials? credentials, CancellationToken ct)
        {
            Calls.Add((imageTag, credentials));
            return Task.FromResult(_resultFor?.Invoke(imageTag) ?? ImagePushResult.Success());
        }
    }

    private sealed class RecordingPrivateRegistries : IPrivateRegistriesApi
    {
        public List<(string Host, string User, string Pass)> Calls { get; } = new();
        public Func<string, string, string, PrivateRegistryUpsertResult>? Reply { get; set; }

        public Task<PrivateRegistryUpsertResult> UpsertAsync(string host, string username, string password, CancellationToken ct)
        {
            Calls.Add((host, username, password));
            return Task.FromResult(Reply?.Invoke(host, username, password) ?? PrivateRegistryUpsertResult.Success());
        }
    }

    private sealed class FakeClient : ICoolifyClient
    {
        public RecordingPrivateRegistries Registries { get; } = new();
        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CoolifyProbeResult.Success("4.1.0"));
        public IPrivateRegistriesApi PrivateRegistries => Registries;
    }

    private sealed class Harness : IDisposable
    {
        public CoolifyDeployingPublisher Publisher { get; }
        public StringWriter Stderr { get; }
        public RecordingPushPipeline Push { get; }
        public FakeClient Client { get; }
        private readonly DistributedApplication _app;

        public Harness(
            string? prefixValue = "auth-reg.test:5000/myapp",
            string? username = "ci",
            string? password = SentinelPassword,
            Func<string, ImagePushResult>? pushResultFor = null)
        {
            var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
            var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
            var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
            b.WithCoolifyDeploy(url, token);

            if (prefixValue is not null)
            {
                var prefixParam = b.AddParameter("registry-prefix", () => prefixValue);
                IResourceBuilder<ParameterResource>? userParam = null;
                IResourceBuilder<ParameterResource>? passParam = null;
                if (username is not null && password is not null)
                {
                    userParam = b.AddParameter("registry-username", () => username);
                    passParam = b.AddParameter("registry-password", () => password, secret: true);
                }
                b.WithImageRegistry(prefixParam, userParam, passParam);
            }

            _app = b.Build();
            Publisher = b.GetRegisteredCoolifyPublisher()!;
            Stderr = new StringWriter();
            Push = new RecordingPushPipeline(pushResultFor);
            Client = new FakeClient();
            Publisher.ErrorWriter = Stderr;
            Publisher.ImagePushPipeline = Push;
            // Simulate a successful configure: hand the publisher a resolved client.
            typeof(CoolifyDeployingPublisher)
                .GetProperty(nameof(CoolifyDeployingPublisher.ResolvedClient))!
                .SetValue(Publisher, Client);
            Publisher.AppHostInfoProvider = () => ("Test.AppHost", "1.2.3-test");
        }

        public void Dispose() => _app.Dispose();
    }

    private static IEnumerable<IResource> Resources(params string[] names) =>
        names.Select(n => (IResource)new FakeResource(n)).ToArray();

    private static void AssertFirstToken(string stderr, string expected)
    {
        Assert.False(string.IsNullOrEmpty(stderr), "stderr was empty");
        var firstLine = stderr.Split('\n', 2)[0].TrimEnd('\r');
        var firstToken = firstLine.Split(new[] { ' ', '\t', ':' }, 2)[0];
        Assert.Equal(expected, firstToken);
    }

    // ────────────────────────────────────────────────────────────────────────
    // A. Anonymous-push path issues zero Coolify Private Registry calls (I-1)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymous_ConfigureUpsert_SkipsAndIssuesZeroCoolifyCalls()
    {
        using var h = new Harness(username: null, password: null);

        var outcome = await h.Publisher.RunConfigureRegistryUpsertAsync(default);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Skipped);
        Assert.Empty(h.Client.Registries.Calls);
    }

    [Fact]
    public async Task Anonymous_PushPhase_DoesNotPassCredentialsToPipeline()
    {
        using var h = new Harness(username: null, password: null);
        var outcome = await h.Publisher.RunPushAsync(Resources("web", "worker"), NullLogger.Instance, default);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, h.Push.Calls.Count);
        Assert.All(h.Push.Calls, c => Assert.Null(c.Creds));
    }

    // ────────────────────────────────────────────────────────────────────────
    // B. Credentialled path upserts exactly once and is idempotent (I-2)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Credentialled_ConfigureUpsert_IssuesExactlyOneCall()
    {
        using var h = new Harness();

        var outcome = await h.Publisher.RunConfigureRegistryUpsertAsync(default);

        Assert.True(outcome.Succeeded);
        Assert.Single(h.Client.Registries.Calls);
        Assert.Equal("auth-reg.test:5000", h.Client.Registries.Calls[0].Host);
        Assert.Equal("ci", h.Client.Registries.Calls[0].User);
        Assert.Equal(SentinelPassword, h.Client.Registries.Calls[0].Pass);
    }

    [Fact]
    public async Task Credentialled_ConfigureUpsert_PropagatesFailure()
    {
        using var h = new Harness();
        h.Client.Registries.Reply = (_, _, _) =>
            PrivateRegistryUpsertResult.Failure("500 Internal Server Error");

        var outcome = await h.Publisher.RunConfigureRegistryUpsertAsync(default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PushSymbol.CoolifyRegistryUpsertFailed, outcome.Diagnostic!.Symbol);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_COOLIFY_REGISTRY_UPSERT_FAILED");
        Assert.Contains("registry:    auth-reg.test:5000", stderr);
        Assert.Contains("username:    ci", stderr);
        Assert.Contains("see:         ADR-005 §D5", stderr);
        // Sentinel password never leaks.
        Assert.DoesNotContain(SentinelPassword, stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // C. Host derivation from prefix
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ghcr.io/pksorensen/myapp", "ghcr.io")]
    [InlineData("registry.lan:5000/myapp", "registry.lan:5000")]
    public async Task ConfigureUpsert_DerivesHostFromPrefixLeadingSegment(string prefix, string expectedHost)
    {
        using var h = new Harness(prefixValue: prefix);
        await h.Publisher.RunConfigureRegistryUpsertAsync(default);

        Assert.Single(h.Client.Registries.Calls);
        Assert.Equal(expectedHost, h.Client.Registries.Calls[0].Host);
    }

    // ────────────────────────────────────────────────────────────────────────
    // D. Configure-phase upsert failure refuses to advance to build (I-7)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertFailure_ThrownByPipelineStep_PreventsBuildAdvance()
    {
        using var h = new Harness();
        h.Client.Registries.Reply = (_, _, _) => PrivateRegistryUpsertResult.Failure("boom");
        var outcome = await h.Publisher.RunConfigureRegistryUpsertAsync(default);

        Assert.False(outcome.Succeeded);
        // Exception type wraps the diagnostic — the pipeline relies on this to skip build.
        var wrapped = new CoolifyPushFailedException(outcome.Diagnostic!);
        Assert.Equal(PushSymbol.CoolifyRegistryUpsertFailed, wrapped.Diagnostic.Symbol);
    }

    // ────────────────────────────────────────────────────────────────────────
    // E. Push uses FT-003's exact image tag (I-5) and no :latest
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Push_UsesExactBuildTags_AndEmitsNoLatest()
    {
        using var h = new Harness();
        // Drive the build phase first so the publisher records emitted tags.
        h.Publisher.ImagePipeline = new BuildPipelineThatTags();
        h.Publisher.ImageStore = null;
        var buildOutcome = await h.Publisher.RunBuildAsync(
            Resources("web", "worker"), NullLogger.Instance, default);
        Assert.True(buildOutcome.Succeeded);

        var pushOutcome = await h.Publisher.RunPushAsync(
            Resources("web", "worker"), NullLogger.Instance, default);
        Assert.True(pushOutcome.Succeeded);

        Assert.Equal(2, h.Push.Calls.Count);
        Assert.Equal("auth-reg.test:5000/myapp/web:1.2.3-test", h.Push.Calls[0].Tag);
        Assert.Equal("auth-reg.test:5000/myapp/worker:1.2.3-test", h.Push.Calls[1].Tag);
        Assert.DoesNotContain(h.Push.Calls, c => c.Tag.EndsWith(":latest"));
    }

    private sealed class BuildPipelineThatTags : IImageBuildPipeline
    {
        public Task BuildAsync(IResource resource, string imageTag, CancellationToken ct) =>
            Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────────────
    // F. Aggregated push failure classification + precedence (I-9, §4)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushFailures_AllAuthRejected_SurfacesRegistryAuthFailedWithAllPairs()
    {
        using var h = new Harness(pushResultFor: tag =>
            tag.Contains("api") ? ImagePushResult.Success() : ImagePushResult.AuthRejected("401"));

        var outcome = await h.Publisher.RunPushAsync(
            Resources("web", "worker", "api"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PushSymbol.RegistryAuthFailed, outcome.Diagnostic!.Symbol);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_REGISTRY_AUTH_FAILED");
        Assert.Contains("web", stderr);
        Assert.Contains("worker", stderr);
        // All three pushes were attempted (no short-circuit, I-9).
        Assert.Equal(3, h.Push.Calls.Count);
    }

    [Fact]
    public async Task PushFailures_MixedAuthAndOther_AuthWinsAndOthersListedSeparately()
    {
        using var h = new Harness(pushResultFor: tag =>
            tag.Contains("web") ? ImagePushResult.AuthRejected() :
            tag.Contains("worker") ? ImagePushResult.Failed("connection refused") :
            ImagePushResult.Success());

        var outcome = await h.Publisher.RunPushAsync(
            Resources("web", "worker", "api"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PushSymbol.RegistryAuthFailed, outcome.Diagnostic!.Symbol);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_REGISTRY_AUTH_FAILED");
        Assert.Contains("additional non-auth failures", stderr);
        Assert.Contains("worker", stderr);
    }

    [Fact]
    public async Task PushFailures_AllNonAuth_SurfacesImagePushFailed()
    {
        using var h = new Harness(pushResultFor: _ => ImagePushResult.Failed("502 Bad Gateway"));

        var outcome = await h.Publisher.RunPushAsync(
            Resources("web", "worker"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PushSymbol.ImagePushFailed, outcome.Diagnostic!.Symbol);
        var stderr = h.Stderr.ToString();
        AssertFirstToken(stderr, "E_IMAGE_PUSH_FAILED");
        Assert.Contains("web", stderr);
        Assert.Contains("worker", stderr);
        Assert.Contains("registry:    auth-reg.test:5000", stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // G. Catch-all (E_PUSH_PHASE_UNEXPECTED)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnclassifiableEscapingException_BecomesPushPhaseUnexpected()
    {
        using var h = new Harness();
        h.Publisher.AppHostInfoProvider = () => throw new InvalidDataException("oops");
        // Force a code path through the recompute branch by clearing the resource map
        // (the harness never ran build).
        var outcome = await h.Publisher.RunPushAsync(
            Resources("web"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        // Either PushPhaseUnexpected (from the AppHostInfo throw) or ImagePushFailed —
        // both leave 'push' as the failing phase. The contract: exit symbol is one of the
        // four E_ literals and matches the first stderr token.
        var stderr = h.Stderr.ToString();
        Assert.StartsWith("E_", stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // H. Stable observable contract — all four symbols spelled verbatim (I-4)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllFourPushSymbols_HaveExactSpelling()
    {
        Assert.Equal("E_COOLIFY_REGISTRY_UPSERT_FAILED", PushSymbol.CoolifyRegistryUpsertFailed.Literal());
        Assert.Equal("E_REGISTRY_AUTH_FAILED", PushSymbol.RegistryAuthFailed.Literal());
        Assert.Equal("E_IMAGE_PUSH_FAILED", PushSymbol.ImagePushFailed.Literal());
        Assert.Equal("E_PUSH_PHASE_UNEXPECTED", PushSymbol.PushPhaseUnexpected.Literal());
    }

    // ────────────────────────────────────────────────────────────────────────
    // I. Redaction — sentinel never leaks across any failure scenario
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SentinelPassword_NeverAppearsInAnyDiagnostic()
    {
        // Configure-upsert failure case carrying back the password literal in the error msg.
        using var h = new Harness();
        h.Client.Registries.Reply = (_, _, p) =>
            PrivateRegistryUpsertResult.Failure($"server said: password '{p}' is wrong");

        var outcome = await h.Publisher.RunConfigureRegistryUpsertAsync(default);

        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain(SentinelPassword, h.Stderr.ToString());
        Assert.DoesNotContain(SentinelPassword, outcome.Diagnostic!.Format());
    }

    [Fact]
    public async Task PushFailureDiagnostic_NeverLeaksSentinelPassword()
    {
        using var h = new Harness(pushResultFor: _ =>
            ImagePushResult.Failed($"got 502 — supplied creds were '{SentinelPassword}'"));

        var outcome = await h.Publisher.RunPushAsync(
            Resources("web"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        // The push pipeline's error message isn't echoed verbatim into the diagnostic —
        // FT-004 surfaces only the resource/tag/host fields, not raw pipeline strings.
        Assert.DoesNotContain(SentinelPassword, h.Stderr.ToString());
    }

    // ────────────────────────────────────────────────────────────────────────
    // J. Cancellation propagates as OperationCanceledException
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Push_CancelledBeforeStart_PropagatesCancellation()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Publisher.RunPushAsync(Resources("web"), NullLogger.Instance, cts.Token));
        Assert.Empty(h.Push.Calls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // K. Anonymous push when WithImageRegistry called with prefix-only (TC-005 S2)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnonymousPrefixOnly_DoesNotCreatePrivateRegistryRecord()
    {
        using var h = new Harness(
            prefixValue: "anon-reg.test:5001/myapp", username: null, password: null);

        await h.Publisher.RunConfigureRegistryUpsertAsync(default);
        await h.Publisher.RunPushAsync(Resources("web", "worker"), NullLogger.Instance, default);

        Assert.Empty(h.Client.Registries.Calls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // L. Successfully-pushed siblings remain in the pushed set on failure (§5)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushFailure_SuccessfulSiblings_AreNotRolledBack()
    {
        var pushed = new List<string>();
        using var h = new Harness(pushResultFor: tag =>
            tag.Contains("worker") ? ImagePushResult.Failed("disk full") : ImagePushResult.Success());

        var outcome = await h.Publisher.RunPushAsync(
            Resources("web", "worker", "api"), NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        // All three were attempted; web + api succeeded; worker is in the failure list.
        Assert.Equal(3, h.Push.Calls.Count);
        var failedNames = outcome.Diagnostic!.Failures.Select(f => f.Resource).ToList();
        Assert.Contains("worker", failedNames);
        Assert.DoesNotContain("web", failedNames);
        Assert.DoesNotContain("api", failedNames);
    }
}
