using System.Reflection;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Coolify.Tests
{

// Exit-criteria tests for FT-009 (containerisability filter — skip-with-warning pass for
// non-containerisable Aspire resources). Covers TC-013 in full (assertion groups A–N).
public sealed class ContainerisabilityFilterExitCriteriaTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Test doubles — minimal IResource implementations used to assemble the
    // TC-013 fixture (one resource per classification outcome). Aspire's own
    // ContainerResource / ProjectResource / ParameterResource cover rules 1 + 2.
    // Rules 3 (azure-native) and 4 (dev-only) and the catch-all fallthrough use
    // synthetic resources because the test project does not reference the Azure
    // SDK package and Aspire's EmulatorResourceAnnotation is internal-or-sealed
    // enough that we exercise the rule via name-based annotation detection.
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class PlainResource : Resource, IResource
    {
        public PlainResource(string name) : base(name) { }
    }

    // Resource whose CLR namespace begins with Aspire.Hosting.Azure — matches rule 3.
    // The namespace is what the classifier inspects (I-3 / classification rule 3:
    // "the resource's CLR type originates from Aspire.Hosting.Azure.*").
    private static IResource MakeAzureNative(string name)
        => new Aspire.Hosting.Azure.FakeKeyVaultResource(name);

    // Annotation whose CLR name contains "Emulator" — matches rule 4's annotation-based
    // detection (I-10).
    private sealed class FakeEmulatorAnnotation : IResourceAnnotation { }

    private static IResource MakeDevOnlyEmulator(string name)
    {
        var r = new PlainResource(name);
        r.Annotations.Add(new FakeEmulatorAnnotation());
        return r;
    }

    // Hybrid: container + azure annotation, to assert rule 1 wins (assertion L).
    private sealed class FakeAzureAnnotation : Aspire.Hosting.Azure.FakeAzureAnnotation { }

    // Synthetic ContainerResource subclass that ALSO carries an Azure-namespaced annotation.
    // Used in assertion L (first-match rule order: ContainerResource wins over Azure).
    private sealed class HybridContainer : ContainerResource
    {
        public HybridContainer(string name) : base(name)
        {
            Annotations.Add(new FakeAzureAnnotation());
        }
    }

    // Recording logger that captures every log line for assertion of warn-level skip
    // shapes and the structured filter-summary entry.
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, string? Template, IReadOnlyList<KeyValuePair<string, object?>> Args)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            string? template = null;
            var args = new List<KeyValuePair<string, object?>>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kvp in kvps)
                {
                    if (kvp.Key == "{OriginalFormat}") template = kvp.Value?.ToString();
                    else args.Add(kvp);
                }
            }
            Entries.Add((logLevel, msg, template, args));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Fixture: one resource per classification outcome (TC-013 §Fixture).
    // ──────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<IResource> BuildFixture()
    {
        // Order matters for assertion B (publisher-driver order preserved). The fixture
        // mixes containerisable and non-containerisable resources to assert filtering, not
        // re-ordering.
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        // `api` — ProjectResource (containerisable). Synthesized rather than via AddProject<T>
        // because we do not have a project type to reference inside the test assembly.
        var api = new ProjectResource("api");
        // `redis` — ContainerResource (containerisable).
        var redis = new ContainerResource("redis");
        // `kv` — azure-native fixture resource (CLR namespace Aspire.Hosting.Azure).
        var kv = MakeAzureNative("kv");
        // `apiUrl` — ParameterResource via AddParameter.
        var apiUrlBuilder = b.AddParameter("api-url");
        var apiUrl = apiUrlBuilder.Resource;
        // `cosmosEmu` — dev-only via emulator annotation.
        var cosmosEmu = MakeDevOnlyEmulator("cosmosEmu");
        // `mystery` — fallthrough (no annotations, no special namespace, no ParameterResource).
        var mystery = new PlainResource("mystery");

        return new IResource[] { api, redis, kv, apiUrl, cosmosEmu, mystery };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // A. Total classifier (FT-009 I-1) — every resource produces exactly one outcome.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_Classifier_IsTotal_OneOutcomePerResource()
    {
        var fixture = BuildFixture();

        var classifications = fixture
            .Select(r => (r.Name, c: ContainerisabilityFilter.Classify(r)))
            .ToList();

        Assert.Equal(6, classifications.Count);
        Assert.Equal(2, classifications.Count(x => x.c.Containerisable));
        Assert.Equal(4, classifications.Count(x => !x.c.Containerisable));

        var byName = classifications.ToDictionary(x => x.Name, x => x.c);
        Assert.True(byName["api"].Containerisable);
        Assert.True(byName["redis"].Containerisable);
        Assert.False(byName["kv"].Containerisable);
        Assert.Equal(ContainerisabilityFilter.Reason.AzureNative, byName["kv"].Reason);
        Assert.False(byName["api-url"].Containerisable);
        Assert.Equal(ContainerisabilityFilter.Reason.Parameter, byName["api-url"].Reason);
        Assert.False(byName["cosmosEmu"].Containerisable);
        Assert.Equal(ContainerisabilityFilter.Reason.DevOnly, byName["cosmosEmu"].Reason);
        Assert.False(byName["mystery"].Containerisable);
        Assert.Equal(ContainerisabilityFilter.Reason.Unknown, byName["mystery"].Reason);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // B. Stable filter — input order preserved (FT-009 I-2).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void B_FilteredEnumeration_PreservesInputOrder()
    {
        var fixture = BuildFixture();
        var logger = new RecordingLogger();

        var summary = ContainerisabilityFilter.Run(fixture, logger, CancellationToken.None);

        Assert.Equal(new[] { "api", "redis" }, summary.Containerisable.Select(r => r.Name).ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // C. Fixed four-element reason vocabulary (FT-009 I-3).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void C_ReasonVocabulary_IsExactlyFourLiterals()
    {
        var fixture = BuildFixture();
        var logger = new RecordingLogger();

        var summary = ContainerisabilityFilter.Run(fixture, logger, CancellationToken.None);

        var allowed = new HashSet<string> { "parameter", "azure-native", "dev-only", "unknown" };
        foreach (var line in summary.SkipLines)
        {
            var m = Regex.Match(line, @"reason: (?<r>[^)]+)\)$");
            Assert.True(m.Success, $"skip line did not match: {line}");
            Assert.Contains(m.Groups["r"].Value, allowed);
        }

        // The four enum values map 1:1 to the four literals; no fifth literal exists.
        Assert.Equal(4, Enum.GetValues<ContainerisabilityFilter.Reason>().Length);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // D. Uniform skip-line shape (FT-009 I-4).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void D_SkipLine_MatchesUniformRegex_AndExactStrings()
    {
        var fixture = BuildFixture();
        var logger = new RecordingLogger();

        var summary = ContainerisabilityFilter.Run(fixture, logger, CancellationToken.None);

        var regex = new Regex(@"^skipped: \S+ \(reason: (parameter|azure-native|dev-only|unknown)\)$");
        Assert.All(summary.SkipLines, line => Assert.Matches(regex, line));

        Assert.Equal(new[]
        {
            "skipped: kv (reason: azure-native)",
            "skipped: api-url (reason: parameter)",
            "skipped: cosmosEmu (reason: dev-only)",
            "skipped: mystery (reason: unknown)",
        }, summary.SkipLines.ToArray());

        // Every emitted skip line is at warn level (I-4).
        var warnLines = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(4, warnLines.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // E. Filter-summary log entry — exactly one, with the six counts.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E_FilterSummary_EmittedOnce_WithExpectedCounts()
    {
        var fixture = BuildFixture();
        var logger = new RecordingLogger();

        var summary = ContainerisabilityFilter.Run(fixture, logger, CancellationToken.None);

        Assert.Equal(6, summary.Walked);
        Assert.Equal(2, summary.Containerisable.Count);
        Assert.Equal(1, summary.Parameter);
        Assert.Equal(1, summary.AzureNative);
        Assert.Equal(1, summary.DevOnly);
        Assert.Equal(1, summary.Unknown);

        var infoLines = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        var summaryLines = infoLines
            .Where(e => e.Message.StartsWith("filter-summary:", StringComparison.Ordinal))
            .ToList();
        Assert.Single(summaryLines);
        Assert.Contains("walked=6", summaryLines[0].Message);
        Assert.Contains("containerisable=2", summaryLines[0].Message);
        Assert.Contains("parameter=1", summaryLines[0].Message);
        Assert.Contains("azure-native=1", summaryLines[0].Message);
        Assert.Contains("dev-only=1", summaryLines[0].Message);
        Assert.Contains("unknown=1", summaryLines[0].Message);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // F. Idempotency on unchanged AppHost (FT-009 I-5).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F_TwoConsecutiveRuns_ProduceByteIdenticalSummaryAndSkipLines()
    {
        var fixture = BuildFixture();
        var log1 = new RecordingLogger();
        var log2 = new RecordingLogger();

        var s1 = ContainerisabilityFilter.Run(fixture, log1, CancellationToken.None);
        var s2 = ContainerisabilityFilter.Run(fixture, log2, CancellationToken.None);

        Assert.Equal(s1.Walked, s2.Walked);
        Assert.Equal(s1.Containerisable.Count, s2.Containerisable.Count);
        Assert.Equal(s1.Parameter, s2.Parameter);
        Assert.Equal(s1.AzureNative, s2.AzureNative);
        Assert.Equal(s1.DevOnly, s2.DevOnly);
        Assert.Equal(s1.Unknown, s2.Unknown);
        Assert.Equal(s1.SkipLines, s2.SkipLines);

        // Same set of skip lines in the same order across both runs.
        var skipsRun1 = log1.Entries.Where(e => e.Level == LogLevel.Warning)
            .Select(e => e.Message).ToList();
        var skipsRun2 = log2.Entries.Where(e => e.Level == LogLevel.Warning)
            .Select(e => e.Message).ToList();
        Assert.Equal(skipsRun1, skipsRun2);

        var summary1 = log1.Entries.Where(e => e.Level == LogLevel.Information)
            .Select(e => e.Message).Single(m => m.StartsWith("filter-summary:", StringComparison.Ordinal));
        var summary2 = log2.Entries.Where(e => e.Level == LogLevel.Information)
            .Select(e => e.Message).Single(m => m.StartsWith("filter-summary:", StringComparison.Ordinal));
        Assert.Equal(summary1, summary2);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // G. Filter never fails the deploy (FT-009 I-6, I-11).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void G_Filter_ReturnsCleanly_NoExceptions_NoErrorSymbols()
    {
        var fixture = BuildFixture();
        var logger = new RecordingLogger();

        // No exceptions escape the filter pass under any code path.
        var summary = ContainerisabilityFilter.Run(fixture, logger, CancellationToken.None);
        Assert.NotNull(summary);

        // No log line contains an E_ symbol attributable to FT-009.
        Assert.DoesNotContain(logger.Entries, e =>
            e.Message.Contains("E_FILTER", StringComparison.Ordinal));
    }

    [Fact]
    public void G_EmptyFilteredEnumeration_IsSuccessful_NoErrorSymbol()
    {
        // AppHost containing only non-containerisable resources → empty filtered list.
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var apiUrl = b.AddParameter("api-url").Resource;
        var kv = MakeAzureNative("kv");
        var logger = new RecordingLogger();

        var summary = ContainerisabilityFilter.Run(new IResource[] { kv, apiUrl }, logger, CancellationToken.None);

        Assert.Equal(2, summary.Walked);
        Assert.Empty(summary.Containerisable);
        Assert.DoesNotContain(logger.Entries, e =>
            e.Message.Contains("E_", StringComparison.Ordinal));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // H. Single source of truth for downstream phases (FT-009 I-7).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task H_DownstreamPhases_DefaultToFilteredEnumeration()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var url = b.AddParameter("coolify-url");
        var token = b.AddParameter("coolify-token", secret: true);
        b.WithCoolifyDeploy(url, token);

        var publisher = b.GetRegisteredCoolifyPublisher()!;
        var fixture = BuildFixture();
        publisher.AllResourcesProvider = () => fixture;

        // Drive the filter once (as the configure phase would).
        var summary = publisher.RunContainerisabilityFilter(NullLogger.Instance, CancellationToken.None);
        Assert.NotNull(summary);
        Assert.Equal(new[] { "api", "redis" }, summary!.Containerisable.Select(r => r.Name).ToArray());

        // Build/push/deploy each fall back to the filtered list when their explicit
        // ResourcesTo* delegates are unset. We assert by inspecting the publisher's
        // ContainerisableResources property, which is what the phase bodies read.
        Assert.Equal(
            new[] { "api", "redis" },
            publisher.ContainerisableResources.Select(r => r.Name).ToArray());

        // The non-containerisable resources do not appear in the filtered enumeration.
        Assert.DoesNotContain(publisher.ContainerisableResources, r => r.Name == "kv");
        Assert.DoesNotContain(publisher.ContainerisableResources, r => r.Name == "api-url");
        Assert.DoesNotContain(publisher.ContainerisableResources, r => r.Name == "cosmosEmu");
        Assert.DoesNotContain(publisher.ContainerisableResources, r => r.Name == "mystery");

        await Task.CompletedTask;
    }

    [Fact]
    public void H_FilterRunsExactlyOnce_PerExplicitInvocation()
    {
        // No internal re-classification: each RunContainerisabilityFilter call walks the
        // provider exactly once. Assertion: a counted provider increments once per call.
        var fixture = BuildFixture();
        int calls = 0;
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var url = b.AddParameter("u");
        var token = b.AddParameter("t", secret: true);
        b.WithCoolifyDeploy(url, token);
        var publisher = b.GetRegisteredCoolifyPublisher()!;
        publisher.AllResourcesProvider = () => { calls++; return fixture; };

        publisher.RunContainerisabilityFilter(NullLogger.Instance, CancellationToken.None);
        Assert.Equal(1, calls);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // I. No Coolify call from FT-009 (FT-009 I-8).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void I_FilterPass_DoesNotInvokeCoolifyClient_AndWritesNoFile()
    {
        // The filter takes no ICoolifyClient and no Coolify-namespace state — it is purely
        // an in-memory pass over IResource / IResourceAnnotation. Asserted by signature
        // inspection (no client parameter on Run) and by absence of any client construction.
        var runMethod = typeof(ContainerisabilityFilter).GetMethod(nameof(ContainerisabilityFilter.Run))!;
        var paramTypes = runMethod.GetParameters().Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(paramTypes, t => t == typeof(ICoolifyClient));
        Assert.DoesNotContain(paramTypes, t => t.Name.Contains("Coolify", StringComparison.Ordinal));

        // No filesystem write either — the filter does not open any file handle. We assert
        // this indirectly via the absence of System.IO usage: running the filter must not
        // change the current directory's file count.
        var cwd = Directory.GetCurrentDirectory();
        var before = Directory.GetFiles(cwd).Length;
        ContainerisabilityFilter.Run(BuildFixture(), NullLogger.Instance, CancellationToken.None);
        var after = Directory.GetFiles(cwd).Length;
        Assert.Equal(before, after);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // J. No opt-in / opt-out surface (FT-009 I-9).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void J_NoOptOut_NoOptIn_ExtensionMethod_Exists()
    {
        var extType = typeof(CoolifyBuilderExtensions);
        var publicMethods = extType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("WithoutContainerisabilityFilter", publicMethods);
        Assert.DoesNotContain("WithContainerisabilityFilter", publicMethods);

        // No WithCoolifyDeploy overload accepts a `filter` toggle.
        var withCoolify = extType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "WithCoolifyDeploy");
        foreach (var m in withCoolify)
        {
            Assert.DoesNotContain(m.GetParameters(),
                p => p.Name is { } n && (n.Contains("filter", StringComparison.OrdinalIgnoreCase)));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // K. Annotation-based dev-only classification (FT-009 I-10).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void K_NewEmulatorAnnotation_Classifies_AsDevOnly_WithoutTouchingFilterSource()
    {
        // A future-style "FutureRunModeOnlyAnnotation" — never registered in any allowlist —
        // is picked up by the annotation-based rule.
        var r = new PlainResource("future-emu");
        r.Annotations.Add(new FutureRunModeOnlyAnnotation());

        var c = ContainerisabilityFilter.Classify(r);

        Assert.False(c.Containerisable);
        Assert.Equal(ContainerisabilityFilter.Reason.DevOnly, c.Reason);
    }

    private sealed class FutureRunModeOnlyAnnotation : IResourceAnnotation { }

    // ──────────────────────────────────────────────────────────────────────────
    // L. First-match rule order (FT-009 §Classification rules).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L_ContainerResource_WithAzureAnnotation_ClassifiesAsContainerisable()
    {
        var hybrid = new HybridContainer("hybrid");
        var logger = new RecordingLogger();

        var summary = ContainerisabilityFilter.Run(new IResource[] { hybrid }, logger, CancellationToken.None);

        Assert.Single(summary.Containerisable);
        Assert.Equal("hybrid", summary.Containerisable[0].Name);
        Assert.Empty(summary.SkipLines);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // M. Cancellation honoured at per-resource boundary (FT-009 §Behaviour §2.i).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void M_Cancellation_BetweenResources_AbortsThePass()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var fixture = BuildFixture();

        Assert.Throws<OperationCanceledException>(() =>
            ContainerisabilityFilter.Run(fixture, NullLogger.Instance, cts.Token));
    }

    [Fact]
    public void M_Cancellation_MidWalk_AbortsThePass()
    {
        // Triggering cancellation after the first resource is enumerated still aborts
        // before any subsequent classification — the per-resource boundary check.
        var fixture = BuildFixture();
        var cts = new CancellationTokenSource();

        IEnumerable<IResource> CancellingSource()
        {
            yield return fixture[0];
            cts.Cancel();
            for (int i = 1; i < fixture.Count; i++) yield return fixture[i];
        }

        Assert.Throws<OperationCanceledException>(() =>
            ContainerisabilityFilter.Run(CancellingSource(), NullLogger.Instance, cts.Token));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // N. No persistent state on disk.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void N_FilterPass_WritesNoFileOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ft009-no-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try
        {
            var before = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Count();

            ContainerisabilityFilter.Run(BuildFixture(), NullLogger.Instance, CancellationToken.None);

            var after = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Count();
            Assert.Equal(before, after);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            Directory.Delete(dir, recursive: true);
        }
    }
}

}

// The fixture's azure-native resource lives in a namespace that begins with
// `Aspire.Hosting.Azure` so classification rule 3 (namespace-prefix detection) fires.
// This stays inside the test assembly — no real Azure SDK reference is required.
namespace Aspire.Hosting.Azure
{
    internal sealed class FakeKeyVaultResource : Resource, IResource
    {
        public FakeKeyVaultResource(string name) : base(name) { }
    }

    // Annotation also in the Aspire.Hosting.Azure namespace — used by the rule-1-wins
    // assertion (L) where the annotation is azure-native but the resource is a container.
    internal class FakeAzureAnnotation : IResourceAnnotation { }
}
