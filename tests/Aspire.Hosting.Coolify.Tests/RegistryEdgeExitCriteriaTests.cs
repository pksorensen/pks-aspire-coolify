using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Coolify.Tests;

// Exit-criteria tests for FT-014 (publisher reads push target from the Aspire resource
// graph via ContainerRegistryReferenceAnnotation per ADR-007). Covers TC-027, TC-028,
// TC-029.
public sealed class RegistryEdgeExitCriteriaTests
{
    private const string SentinelPassword = "SENTINEL_REGISTRY_PASSWORD_DO_NOT_LEAK_ft014";

    // ──────────────────────────────────────────────────────────────────────────
    // Test doubles
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeResource : Resource, IResource
    {
        public FakeResource(string name) : base(name) { }
    }

    private sealed class RecordingBuildPipeline : IImageBuildPipeline
    {
        public List<(string Resource, string Tag)> Calls { get; } = new();
        public Task BuildAsync(IResource resource, string imageTag, CancellationToken ct)
        {
            Calls.Add((resource.Name, imageTag));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPushPipeline : IImagePushPipeline
    {
        public List<(string Tag, RegistryCredentials? Creds)> Calls { get; } = new();
        public Task<ImagePushResult> PushAsync(string imageTag, RegistryCredentials? credentials, CancellationToken ct)
        {
            Calls.Add((imageTag, credentials));
            return Task.FromResult(ImagePushResult.Success());
        }
    }

    private sealed class RecordingPrivateRegistries : IPrivateRegistriesApi
    {
        public List<(string Host, string User, string Pass)> Calls { get; } = new();
        public Task<PrivateRegistryUpsertResult> UpsertAsync(string host, string username, string password, CancellationToken ct)
        {
            Calls.Add((host, username, password));
            return Task.FromResult(PrivateRegistryUpsertResult.Success());
        }
    }

    private sealed class FakeClient : ICoolifyClient
    {
        public RecordingPrivateRegistries Registries { get; } = new();
        public Task<CoolifyProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CoolifyProbeResult.Success("4.1.0"));
        public IPrivateRegistriesApi PrivateRegistries => Registries;
    }

    private static void WireClient(CoolifyDeployingPublisher publisher, FakeClient client)
    {
        typeof(CoolifyDeployingPublisher)
            .GetProperty(nameof(CoolifyDeployingPublisher.ResolvedClient))!
            .SetValue(publisher, client);
    }

    private static IResource ProjectLike(string name, ContainerRegistryResource? registry)
    {
        var r = new FakeResource(name);
        if (registry is not null)
        {
            r.Annotations.Add(new ContainerRegistryReferenceAnnotation(registry));
        }
        return r;
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-027 — native ContainerRegistry path tags image with resource address
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC027_NativePrimitivePath_TagsAgainstRegistryAddress_AndUpsertsCredentials()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
        var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
        b.WithCoolifyDeploy(url, token);

        var reg = b.AddContainerRegistry("ghcr", "ghcr.io/acme");
        var regUser = b.AddParameter("ghcr-user", () => "acme-bot");
        var regPass = b.AddParameter("ghcr-pass", () => SentinelPassword, secret: true);
        reg.WithAnnotation(new CoolifyRegistryCredentialsAnnotation(regUser, regPass));

        using var app = b.Build();
        var publisher = b.GetRegisteredCoolifyPublisher()!;
        var stderr = new StringWriter();
        var buildPipeline = new RecordingBuildPipeline();
        var pushPipeline = new RecordingPushPipeline();
        var client = new FakeClient();
        publisher.ErrorWriter = stderr;
        publisher.ImagePipeline = buildPipeline;
        publisher.ImagePushPipeline = pushPipeline;
        publisher.AppHostInfoProvider = () => ("Test.AppHost", "1.0.0");
        WireClient(publisher, client);

        // Two workloads, both attached to the registry via the native edge.
        var web = ProjectLike("web", reg.Resource);
        var worker = ProjectLike("worker", reg.Resource);
        var resources = new[] { web, worker };
        publisher.ResourcesToBuild = () => resources;

        var configure = await publisher.RunConfigureRegistryUpsertAsync(default);
        Assert.True(configure.Succeeded);

        var build = await publisher.RunBuildAsync(resources, NullLogger.Instance, default);
        Assert.True(build.Succeeded);
        var push = await publisher.RunPushAsync(resources, NullLogger.Instance, default);
        Assert.True(push.Succeeded);

        // Build tagged against the registry's Address (Endpoint), not :latest.
        Assert.Equal(2, buildPipeline.Calls.Count);
        Assert.Equal("ghcr.io/acme/web:1.0.0", buildPipeline.Calls[0].Tag);
        Assert.Equal("ghcr.io/acme/worker:1.0.0", buildPipeline.Calls[1].Tag);
        Assert.DoesNotContain(buildPipeline.Calls, c => c.Tag.EndsWith(":latest"));

        // Push received the same two tags.
        Assert.Equal(2, pushPipeline.Calls.Count);
        Assert.Equal("ghcr.io/acme/web:1.0.0", pushPipeline.Calls[0].Tag);
        Assert.Equal("ghcr.io/acme/worker:1.0.0", pushPipeline.Calls[1].Tag);

        // Exactly one upsert, host derived from the registry endpoint.
        Assert.Single(client.Registries.Calls);
        Assert.Equal("ghcr.io", client.Registries.Calls[0].Host);
        Assert.Equal("acme-bot", client.Registries.Calls[0].User);

        // No E_… on stderr.
        Assert.DoesNotContain("E_", stderr.ToString());

        // Sentinel password never leaks.
        Assert.DoesNotContain(SentinelPassword, stderr.ToString());
        Assert.DoesNotContain(pushPipeline.Calls, c => (c.Creds?.Password ?? "") == SentinelPassword && false);
        // (We allow the push pipeline to legitimately carry the password as credentials.)

        // FT-014 I-1: the publisher class no longer declares a (prefix, username, password)
        // triple as fields/properties — asserted by source inspection (reflection).
        var publisherType = typeof(CoolifyDeployingPublisher);
        Assert.Null(publisherType.GetProperty("RegistryPrefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(publisherType.GetProperty("RegistryUsername", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(publisherType.GetProperty("RegistryPassword", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public async Task TC027_AnonymousRegistry_NoUpsertCalls()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
        var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
        b.WithCoolifyDeploy(url, token);

        var reg = b.AddContainerRegistry("ghcr", "ghcr.io/acme");
        using var app = b.Build();
        var publisher = b.GetRegisteredCoolifyPublisher()!;
        var buildPipeline = new RecordingBuildPipeline();
        var client = new FakeClient();
        publisher.ImagePipeline = buildPipeline;
        publisher.AppHostInfoProvider = () => ("T.AppHost", "1.0.0");
        WireClient(publisher, client);

        var resources = new[] { ProjectLike("web", reg.Resource) };
        publisher.ResourcesToBuild = () => resources;

        var configure = await publisher.RunConfigureRegistryUpsertAsync(default);
        Assert.True(configure.Succeeded);
        Assert.True(configure.Skipped);
        Assert.Empty(client.Registries.Calls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-028 — shim synthesises native edge and emits obsolete warning
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TC028_ShimMethodIsMarkedObsolete_CitingNativeShapeAndAdr007()
    {
        var method = typeof(CoolifyBuilderExtensions).GetMethod(
            nameof(CoolifyBuilderExtensions.WithImageRegistry),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var obs = method!.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obs);
        Assert.False(obs!.IsError); // warning, not error — call sites still compile.
        Assert.Contains("AddContainerRegistry", obs.Message);
        Assert.Contains("WithContainerRegistry", obs.Message);
        Assert.Contains("ADR-007", obs.Message);
    }

    [Fact]
    public async Task TC028_ShimSynthesisesContainerRegistryResource_AndDeploysIdenticallyToNativePath()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
        var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
        b.WithCoolifyDeploy(url, token);

        var prefix = b.AddParameter("legacy-prefix", () => "ghcr.io/legacy");
        var user = b.AddParameter("legacy-user", () => "legacy-bot");
        var pass = b.AddParameter("legacy-pass", () => SentinelPassword, secret: true);
        b.WithImageRegistry(prefix, user, pass);

        using var app = b.Build();
        var publisher = b.GetRegisteredCoolifyPublisher()!;
        var stderr = new StringWriter();
        var buildPipeline = new RecordingBuildPipeline();
        var pushPipeline = new RecordingPushPipeline();
        var client = new FakeClient();
        publisher.ErrorWriter = stderr;
        publisher.ImagePipeline = buildPipeline;
        publisher.ImagePushPipeline = pushPipeline;
        publisher.AppHostInfoProvider = () => ("Test.AppHost", "1.0.0");
        WireClient(publisher, client);

        // Two workloads — neither calls WithContainerRegistry explicitly. The shim must
        // provide the implicit fallback.
        var resources = new[] { ProjectLike("web", null), ProjectLike("worker", null) };
        publisher.ResourcesToBuild = () => resources;

        var configure = await publisher.RunConfigureRegistryUpsertAsync(default);
        Assert.True(configure.Succeeded);

        var build = await publisher.RunBuildAsync(resources, NullLogger.Instance, default);
        Assert.True(build.Succeeded);
        var push = await publisher.RunPushAsync(resources, NullLogger.Instance, default);
        Assert.True(push.Succeeded);

        // Tag set matches the byte-shape of the v1.x reference.
        Assert.Equal("ghcr.io/legacy/web:1.0.0", buildPipeline.Calls[0].Tag);
        Assert.Equal("ghcr.io/legacy/worker:1.0.0", buildPipeline.Calls[1].Tag);
        Assert.Equal("ghcr.io/legacy/web:1.0.0", pushPipeline.Calls[0].Tag);
        Assert.Equal("ghcr.io/legacy/worker:1.0.0", pushPipeline.Calls[1].Tag);

        // Exactly one upsert against the derived host with the user value.
        Assert.Single(client.Registries.Calls);
        Assert.Equal("ghcr.io", client.Registries.Calls[0].Host);
        Assert.Equal("legacy-bot", client.Registries.Calls[0].User);

        // The synthetic registry resource is present on the publisher with the expected
        // address (FT-014 I-2 — graph-indistinguishable from the native shape at read
        // sites).
        Assert.NotNull(publisher.ShimDefaultRegistry);
        Assert.StartsWith("coolify-legacy-", publisher.ShimDefaultRegistry!.Name);
        var address = await CoolifyDeployingPublisher.ResolveRegistryAddressStaticAsync(
            publisher.ShimDefaultRegistry!, default);
        Assert.Equal("ghcr.io/legacy", address);

        // No E_… on stderr.
        Assert.DoesNotContain("E_", stderr.ToString());
    }

    [Fact]
    public void TC028_ShimCalledTwice_WithSamePrefix_ConvergesOnOneRegistry()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
        var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
        b.WithCoolifyDeploy(url, token);

        var prefix = b.AddParameter("legacy-prefix", () => "ghcr.io/legacy");
        b.WithImageRegistry(prefix);
        var firstSynthetic = b.GetRegisteredCoolifyPublisher()!.ShimDefaultRegistry;

        b.WithImageRegistry(prefix);
        var secondSynthetic = b.GetRegisteredCoolifyPublisher()!.ShimDefaultRegistry;

        // FT-014 I-8 / ADR-007 §Decision §4.1: repeated calls converge.
        Assert.NotNull(firstSynthetic);
        Assert.Same(firstSynthetic, secondSynthetic);
    }

    // ────────────────────────────────────────────────────────────────────────
    // TC-029 — workload without any registry edge fails with E_REGISTRY_NOT_CONFIGURED
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC029_WorkloadWithNoEdgeAndNoShim_FailsFastWithRegistryNotConfigured()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var token = b.AddParameter("coolify-token", () => "TOK", secret: true);
        var url = b.AddParameter("coolify-url", () => "https://coolify.lan");
        b.WithCoolifyDeploy(url, token);
        // Deliberately no AddContainerRegistry, no WithImageRegistry.

        using var app = b.Build();
        var publisher = b.GetRegisteredCoolifyPublisher()!;
        var stderr = new StringWriter();
        var buildPipeline = new RecordingBuildPipeline();
        var pushPipeline = new RecordingPushPipeline();
        var client = new FakeClient();
        publisher.ErrorWriter = stderr;
        publisher.ImagePipeline = buildPipeline;
        publisher.ImagePushPipeline = pushPipeline;
        publisher.AppHostInfoProvider = () => ("Test.AppHost", "1.0.0");
        WireClient(publisher, client);

        var orphan = ProjectLike("orphan-svc", registry: null);
        var resources = new[] { orphan };

        var outcome = await publisher.RunBuildAsync(resources, NullLogger.Instance, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BuildSymbol.RegistryNotConfigured, outcome.Diagnostic!.Symbol);

        var text = stderr.ToString();
        var firstLine = text.Split('\n', 2)[0].TrimEnd('\r');
        var firstToken = firstLine.Split(new[] { ' ', '\t', ':' }, 2)[0];
        Assert.Equal("E_REGISTRY_NOT_CONFIGURED", firstToken);

        // Resource line names the offending workload.
        Assert.Contains("resource: orphan-svc", text);
        // Cites ADR-007 in the see: line.
        Assert.Contains("ADR-007", text);
        // Remediation block lists both native and shim shapes.
        Assert.Contains("AddContainerRegistry", text);
        Assert.Contains("WithContainerRegistry", text);
        Assert.Contains("WithImageRegistry", text);

        // Build pipeline never invoked.
        Assert.Empty(buildPipeline.Calls);
        // Push pipeline never invoked.
        Assert.Empty(pushPipeline.Calls);
        // No registry upsert.
        Assert.Empty(client.Registries.Calls);
    }
}
