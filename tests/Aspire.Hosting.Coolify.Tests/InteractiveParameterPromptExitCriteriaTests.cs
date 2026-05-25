using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-012 (interactive parameter prompting in the configure phase).
// Covers TC-019 (interactive-and-unset → prompt-then-proceed, redaction, one-prompt invariant,
// interactive-but-set regression guard, cancellation), TC-020 (non-interactive fail-fast
// preserved with < 30s wall-clock bound, no prompt, precedence rule), and TC-021 (exit-criteria
// roll-up: conjunction of TC-019 + TC-020).
public sealed class InteractiveParameterPromptExitCriteriaTests
{
    private const string SentinelToken = "SENTINEL_TOKEN_INTERACTIVE_a91f3";
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

    private sealed class ScriptedPrompter : IParameterPrompter
    {
        public bool IsInteractive { get; init; } = true;
        public Dictionary<string, string?> Replies { get; } = new(StringComparer.Ordinal);
        public List<string> Prompted { get; } = new();
        public bool ThrowOnPrompt { get; init; } = false;
        public CancellationTokenSource? CancelOnPrompt { get; init; }

        public async Task<string?> PromptAsync(ParameterPromptRequest request, CancellationToken cancellationToken)
        {
            Prompted.Add(request.ParameterName);
            if (CancelOnPrompt is not null)
            {
                CancelOnPrompt.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (ThrowOnPrompt)
            {
                throw new InvalidOperationException("prompt subsystem failure");
            }
            await Task.Yield();
            return Replies.TryGetValue(request.ParameterName, out var v) ? v : null;
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
    // TC-019 §1 — Interactive + unset token → prompt fires, configure proceeds
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC019_S1_InteractiveAndUnsetToken_PromptsAndProceeds()
    {
        using var h = new Harness(
            tokenValue: null,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.1.0"));
        var prompter = new ScriptedPrompter
        {
            IsInteractive = true,
            Replies = { [TokenParamName] = SentinelToken },
        };
        h.Publisher.Prompter = prompter;

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("4.1.0", outcome.Version);
        // Prompt fired exactly once for the token parameter.
        Assert.Single(prompter.Prompted, TokenParamName);
        // Combined probe carried the prompted value as the bearer.
        Assert.Equal(SentinelToken, h.Factory.LastToken);
        Assert.Equal(1, h.Factory.LastClient!.ProbeCalls);
        // FT-012 I-5: no E_AUTH_TOKEN_MISSING on the interactive happy path.
        var stderr = h.Stderr.ToString();
        Assert.DoesNotContain("E_AUTH_TOKEN_MISSING", stderr);
        // FT-012 I-2: prompted secret value is never logged or echoed on the diagnostic surface.
        Assert.DoesNotContain(SentinelToken, stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-019 §2 — Interactive + unset url → prompt fires, configure proceeds
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC019_S2_InteractiveAndUnsetUrl_PromptsAndProceeds()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: null,
            probe: () => CoolifyProbeResult.Success("4.1.0"));
        var prompter = new ScriptedPrompter
        {
            IsInteractive = true,
            Replies = { [UrlParamName] = ProbeUrl },
        };
        h.Publisher.Prompter = prompter;

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Single(prompter.Prompted, UrlParamName);
        Assert.Equal(ProbeUrl, h.Factory.LastUrl);
        var stderr = h.Stderr.ToString();
        Assert.DoesNotContain("E_COOLIFY_UNREACHABLE", stderr);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-019 §4 — Redaction: prompted secret value never appears in any sink
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC019_S4_PromptedSecretValue_NeverAppearsInStderr()
    {
        using var h = new Harness(
            tokenValue: null,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.1.0"));
        var prompter = new ScriptedPrompter
        {
            IsInteractive = true,
            Replies = { [TokenParamName] = SentinelToken },
        };
        h.Publisher.Prompter = prompter;

        await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.DoesNotContain(SentinelToken, h.Stderr.ToString());
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-019 §5 — At most one prompt per parameter per deploy (FT-012 I-3)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC019_S5_AtMostOnePromptPerParameter_AcrossMultipleResolutions()
    {
        using var h = new Harness(
            tokenValue: null,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.1.0"));
        var prompter = new ScriptedPrompter
        {
            IsInteractive = true,
            Replies = { [TokenParamName] = SentinelToken },
        };
        h.Publisher.Prompter = prompter;

        // Configure runs once and prompts once. Asking the publisher to re-resolve the same
        // handle a second time (simulating any later phase that captures the value) must NOT
        // produce a second prompt.
        await h.Publisher.RunConfigureAsync(CancellationToken.None);
        var second = await h.Publisher.ResolveOrPromptAsync(
            h.Publisher.Token, isSecret: true, CancellationToken.None);

        Assert.Equal(SentinelToken, second);
        Assert.Equal(1, prompter.Prompted.Count(p => p == TokenParamName));
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-019 §6 — Interactive + set → no prompt fires (regression guard, FT-012 I-4)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC019_S6_InteractiveAndSet_NoPromptFires()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.1.0"));
        var prompter = new ScriptedPrompter { IsInteractive = true };
        h.Publisher.Prompter = prompter;

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Empty(prompter.Prompted);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-019 §7 — Prompt cancellation → cancellation diagnostic, not E_…
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC019_S7_PromptCancellation_PropagatesAsOperationCanceled()
    {
        using var h = new Harness(
            tokenValue: null,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.1.0"));
        using var cts = new CancellationTokenSource();
        var prompter = new ScriptedPrompter
        {
            IsInteractive = true,
            CancelOnPrompt = cts,
        };
        h.Publisher.Prompter = prompter;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Publisher.RunConfigureAsync(cts.Token));
        // FT-012 I-5 + I-8: no E_… symbol emitted on cancellation; no Coolify side effect.
        Assert.Equal(0, h.Factory.CreateCalls);
        Assert.DoesNotContain("E_AUTH_TOKEN_MISSING", h.Stderr.ToString());
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-020 §1 — Non-interactive + unset token → E_AUTH_TOKEN_MISSING, no prompt, <30s
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC020_S1_NonInteractiveAndUnsetToken_FailsFast_NoPromptNoHang()
    {
        using var h = new Harness(
            tokenValue: null,
            urlValue: ProbeUrl,
            probe: () => throw new InvalidOperationException("probe must not run"));
        // Default prompter is NonInteractivePrompter; assert behaviour explicitly.
        Assert.False(h.Publisher.Prompter.IsInteractive);

        var sw = Stopwatch.StartNew();
        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "non-interactive fail-fast must not hang");
        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.AuthTokenMissing, outcome.Diagnostic!.Symbol);
        Assert.Equal(0, h.Factory.CreateCalls);
        AssertFirstToken(h.Stderr.ToString(), "E_AUTH_TOKEN_MISSING");
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-020 §2 — Non-interactive + unset url → E_COOLIFY_UNREACHABLE, no prompt, <30s
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC020_S2_NonInteractiveAndUnsetUrl_FailsFast_NoPromptNoHang()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: null,
            probe: () => throw new InvalidOperationException("probe must not run"));

        var sw = Stopwatch.StartNew();
        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.CoolifyUnreachable, outcome.Diagnostic!.Symbol);
        Assert.Equal(0, h.Factory.CreateCalls);
        AssertFirstToken(h.Stderr.ToString(), "E_COOLIFY_UNREACHABLE");
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-020 §5 — Non-interactive + set → unchanged happy path (regression guard)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC020_S5_NonInteractiveAndSet_HappyPath_NoPromptObserved()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.1.0"));
        // Default prompter (NonInteractive) — but instrumented to catch any accidental call.
        var prompter = new ScriptedPrompter { IsInteractive = false };
        h.Publisher.Prompter = prompter;

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Empty(prompter.Prompted);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-020 §6 — Precedence preserved on the non-interactive path: token-missing
    //              wins over url-unreachable (FT-012 I-5: no symbol churn).
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC020_S6_PrecedencePreserved_TokenMissingWinsOverUnreachable()
    {
        // Token unset AND url unset → existing precedence rule says E_AUTH_TOKEN_MISSING.
        using var h = new Harness(
            tokenValue: null,
            urlValue: null,
            probe: () => throw new InvalidOperationException("probe must not run"));

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.AuthTokenMissing, outcome.Diagnostic!.Symbol);
        AssertFirstToken(h.Stderr.ToString(), "E_AUTH_TOKEN_MISSING");
    }

    // ────────────────────────────────────────────────────────────────────────
    // FT-012 §"Error handling": prompt subsystem itself faults → CI-safe fail-fast
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task PromptSubsystemFault_TreatedAsNonInteractive_FailsFastWithSymbol()
    {
        using var h = new Harness(
            tokenValue: null,
            urlValue: ProbeUrl,
            probe: () => throw new InvalidOperationException("probe must not run"));
        h.Publisher.Prompter = new ScriptedPrompter
        {
            IsInteractive = true,
            ThrowOnPrompt = true,
        };

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.AuthTokenMissing, outcome.Diagnostic!.Symbol);
        AssertFirstToken(h.Stderr.ToString(), "E_AUTH_TOKEN_MISSING");
    }

    // ────────────────────────────────────────────────────────────────────────
    // FT-012 §Behaviour: empty reply to a prompt → matching E_… symbol.
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task EmptyReplyToPrompt_TreatedAsUnset_FailsFastWithSymbol()
    {
        using var h = new Harness(
            tokenValue: null,
            urlValue: ProbeUrl,
            probe: () => throw new InvalidOperationException("probe must not run"));
        h.Publisher.Prompter = new ScriptedPrompter
        {
            IsInteractive = true,
            Replies = { [TokenParamName] = "" },
        };

        var outcome = await h.Publisher.RunConfigureAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ConfigureSymbol.AuthTokenMissing, outcome.Diagnostic!.Symbol);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Default prompter is the CI-safe NonInteractivePrompter (FT-012 I-1)
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void DefaultPrompter_IsNonInteractive()
    {
        using var h = new Harness(
            tokenValue: SentinelToken,
            urlValue: ProbeUrl,
            probe: () => CoolifyProbeResult.Success("4.1.0"));

        Assert.IsType<NonInteractivePrompter>(h.Publisher.Prompter);
        Assert.False(h.Publisher.Prompter.IsInteractive);
    }

    private static void AssertFirstToken(string stderr, string expected)
    {
        Assert.False(string.IsNullOrEmpty(stderr), "stderr was empty");
        var firstLine = stderr.Split('\n', 2)[0].TrimEnd('\r');
        var firstToken = firstLine.Split(new[] { ' ', '\t', ':' }, 2)[0];
        Assert.Equal(expected, firstToken);
    }
}
