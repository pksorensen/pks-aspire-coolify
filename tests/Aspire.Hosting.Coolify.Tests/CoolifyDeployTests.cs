using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;

namespace Aspire.Hosting.Coolify.Tests;

public class CoolifyDeployTests
{
    private static IDistributedApplicationBuilder NewBuilder()
        => DistributedApplication.CreateBuilder(Array.Empty<string>());

    private static (IResourceBuilder<ParameterResource> url, IResourceBuilder<ParameterResource> token) AddParams(
        IDistributedApplicationBuilder b, string prefix = "coolify-homelab")
    {
        var url = b.AddParameter($"{prefix}-url");
        var token = b.AddParameter($"{prefix}-token", secret: true);
        return (url, token);
    }

    // I-6 / null arg behaviour
    [Fact]
    public void WithCoolifyDeploy_NullUrl_Throws()
    {
        var b = NewBuilder();
        var token = b.AddParameter("t", secret: true);
        Assert.Throws<ArgumentNullException>(() => b.WithCoolifyDeploy(null!, token));
    }

    [Fact]
    public void WithCoolifyDeploy_NullToken_Throws()
    {
        var b = NewBuilder();
        var url = b.AddParameter("u");
        Assert.Throws<ArgumentNullException>(() => b.WithCoolifyDeploy(url, null!));
    }

    [Fact]
    public void WithCoolifyDeploy_NullBuilder_Throws()
    {
        var b = NewBuilder();
        var (url, token) = AddParams(b);
        Assert.Throws<ArgumentNullException>(
            () => CoolifyBuilderExtensions.WithCoolifyDeploy(null!, url, token));
    }

    // I-4: idempotent at the builder level — first call wins, second call is a no-op.
    [Fact]
    public void WithCoolifyDeploy_IsIdempotent_FirstCallWins()
    {
        var b = NewBuilder();
        var (url1, token1) = AddParams(b, "coolify-a");
        var (url2, token2) = AddParams(b, "coolify-b");

        b.WithCoolifyDeploy(url1, token1);
        var first = b.GetRegisteredCoolifyPublisher();
        Assert.NotNull(first);

        b.WithCoolifyDeploy(url2, token2);
        var second = b.GetRegisteredCoolifyPublisher();

        Assert.Same(first, second);
        Assert.Same(url1, second!.Url);
        Assert.Same(token1, second.Token);
    }

    // I-7: skeleton captures the parameter handles only — same references stored, not values.
    [Fact]
    public void WithCoolifyDeploy_StoresParameterHandlesByReference()
    {
        var b = NewBuilder();
        var (url, token) = AddParams(b);

        b.WithCoolifyDeploy(url, token);

        var publisher = b.GetRegisteredCoolifyPublisher();
        Assert.NotNull(publisher);
        Assert.Same(url, publisher!.Url);
        Assert.Same(token, publisher.Token);
    }

    // I-1 / I-2: the five fixed phases in fixed order — vocabulary matches ADR-003 verbatim.
    [Fact]
    public void PhaseEnum_NamesAndOrderMatchAdr003()
    {
        var ordered = Enum.GetValues<CoolifyPhase>()
                          .OrderBy(p => (int)p)
                          .Select(p => p.PhaseName())
                          .ToArray();

        Assert.Equal(
            new[] { "configure", "prereq", "build", "push", "deploy", "verify" },
            ordered);
    }

    // Second registration must not collide on step names when the host is built.
    [Fact]
    public void WithCoolifyDeploy_TwiceDoesNotDoubleRegisterPipelineSteps()
    {
        var b = NewBuilder();
        var (url, token) = AddParams(b);

        b.WithCoolifyDeploy(url, token);
        b.WithCoolifyDeploy(url, token); // no-op per I-4

        using var app = b.Build(); // would throw on duplicate step name
    }
}
