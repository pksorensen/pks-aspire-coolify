// Validates TC-018: WithCoolifyDestination(string) overload (FT-005 §0 amendment).
using System;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Coolify;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.Coolify.Tests;

public class CoolifyDestinationStringOverloadTests
{
    private static (IDistributedApplicationBuilder builder, CoolifyDeployingPublisher publisher) NewWired()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var url = b.AddParameter("u");
        var token = b.AddParameter("t", secret: true);
        b.WithCoolifyDeploy(url, token);
        var publisher = b.Services.BuildServiceProvider().GetRequiredService<CoolifyDeployingPublisher>();
        return (b, publisher);
    }

    [Fact]
    public void StringOverload_SetsLiteralAndClearsHandle()
    {
        var (b, publisher) = NewWired();
        var handle = b.AddParameter("dest-handle");
        b.WithCoolifyDestination(handle);   // handle path first
        Assert.NotNull(publisher.DestinationName);
        Assert.Null(publisher.DestinationLiteralName);

        b.WithCoolifyDestination("homelab"); // string overrides — last-call-wins
        Assert.Null(publisher.DestinationName);
        Assert.Equal("homelab", publisher.DestinationLiteralName);
    }

    [Fact]
    public void HandleOverload_ClearsLiteralLastCallWins()
    {
        var (b, publisher) = NewWired();
        b.WithCoolifyDestination("homelab");
        Assert.Equal("homelab", publisher.DestinationLiteralName);

        var handle = b.AddParameter("dest-handle");
        b.WithCoolifyDestination(handle);    // handle overrides
        Assert.Null(publisher.DestinationLiteralName);
        Assert.NotNull(publisher.DestinationName);
    }

    [Fact]
    public void StringOverload_NullName_ThrowsArgumentNullException()
    {
        var (b, _) = NewWired();
        Assert.Throws<ArgumentNullException>(() => b.WithCoolifyDestination((string)null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void StringOverload_EmptyOrWhitespace_ThrowsArgumentException(string bad)
    {
        var (b, _) = NewWired();
        Assert.Throws<ArgumentException>(() => b.WithCoolifyDestination(bad));
    }

    [Fact]
    public void StringOverload_NullBuilder_ThrowsArgumentNullException()
    {
        IDistributedApplicationBuilder? b = null;
        Assert.Throws<ArgumentNullException>(() => b!.WithCoolifyDestination("homelab"));
    }

    [Fact]
    public void StringOverload_BeforeWithCoolifyDeploy_ThrowsInvalidOperationException()
    {
        var b = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var ex = Assert.Throws<InvalidOperationException>(() => b.WithCoolifyDestination("homelab"));
        Assert.Contains("WithCoolifyDeploy", ex.Message);
    }

    [Fact]
    public void StringOverload_Idempotent_LastCallWins()
    {
        var (b, publisher) = NewWired();
        b.WithCoolifyDestination("first");
        b.WithCoolifyDestination("second");
        Assert.Equal("second", publisher.DestinationLiteralName);
        Assert.Null(publisher.DestinationName);
    }
}
