// Validates TC-017: public With* extensions live in the Aspire.Hosting namespace,
// not Aspire.Hosting.Coolify, so consumer AppHost projects (which already import
// Aspire.Hosting via implicit usings) reach them with no extra using directive.
using System.Linq;
using System.Reflection;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.Coolify.Tests;

public class AspireHostingNamespaceConventionTests
{
    private static readonly Assembly s_extensionAssembly = typeof(CoolifyDeployingPublisher).Assembly;

    [Fact]
    public void CoolifyBuilderExtensions_lives_in_Aspire_Hosting_namespace()
    {
        var type = s_extensionAssembly.GetType("Aspire.Hosting.CoolifyBuilderExtensions", throwOnError: false);
        Assert.NotNull(type);
        Assert.Equal("Aspire.Hosting", type!.Namespace);
    }

    [Theory]
    [InlineData("WithCoolifyDeploy")]
    [InlineData("WithImageRegistry")]
    [InlineData("WithCoolifyDestination")]
    [InlineData("WithVerifyPolling")]
    [InlineData("WithManagedDashboard")]
    public void Public_with_extension_resolves_in_Aspire_Hosting_namespace(string methodName)
    {
        var type = s_extensionAssembly.GetType("Aspire.Hosting.CoolifyBuilderExtensions", throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == methodName);

        Assert.NotNull(method);
        // The declaring type's namespace IS what consumers must import to call this method.
        Assert.Equal("Aspire.Hosting", method!.DeclaringType!.Namespace);
    }

    [Fact]
    public void No_public_With_extension_lives_in_Aspire_Hosting_Coolify_namespace()
    {
        // Catch a regression where a future extension method gets added in the wrong namespace.
        var leakedExtensions = s_extensionAssembly
            .GetTypes()
            .Where(t => t.Namespace == "Aspire.Hosting.Coolify" && t.IsAbstract && t.IsSealed) // static classes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), inherit: false))
            .Where(m => m.Name.StartsWith("With"))
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToList();

        Assert.Empty(leakedExtensions);
    }
}
