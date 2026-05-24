using System.Text;

// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// The four configure-phase fail-fast symbols (FT-002 §Outputs, I-5). These literals are part
/// of the publisher's observable CLI contract and may not change without a new ADR.
/// </summary>
public enum ConfigureSymbol
{
    AuthTokenMissing,
    AuthTokenInvalid,
    CoolifyVersionBelowFloor,
    CoolifyUnreachable,
}

internal static class ConfigureSymbolExtensions
{
    public static string Literal(this ConfigureSymbol s) => s switch
    {
        ConfigureSymbol.AuthTokenMissing => "E_AUTH_TOKEN_MISSING",
        ConfigureSymbol.AuthTokenInvalid => "E_AUTH_TOKEN_INVALID",
        ConfigureSymbol.CoolifyVersionBelowFloor => "E_COOLIFY_VERSION_BELOW_FLOOR",
        ConfigureSymbol.CoolifyUnreachable => "E_COOLIFY_UNREACHABLE",
        _ => throw new InvalidOperationException($"Unknown ConfigureSymbol: {s}"),
    };
}

/// <summary>
/// Structured fail-fast diagnostic produced by the configure phase (FT-002 §Outputs / §"Diagnostic content").
/// The <see cref="Format"/> output is written verbatim to stderr; its first whitespace-delimited
/// token is the literal symbol that TC-006 / TC-004 / TC-002 grep for.
/// </summary>
public sealed class ConfigureDiagnostic
{
    public required ConfigureSymbol Symbol { get; init; }
    public required string ParameterName { get; init; }
    public string? Url { get; init; }
    public string? Observed { get; init; }
    public string? Required { get; init; }
    public string? Detail { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(Symbol.Literal());
        sb.Append(": ");
        sb.AppendLine(HumanLine());

        switch (Symbol)
        {
            case ConfigureSymbol.AuthTokenMissing:
                sb.AppendLine($"  parameter: {ParameterName}");
                sb.AppendLine("  remediation:");
                sb.AppendLine($"    dotnet user-secrets set Parameters:{ParameterName} <value>");
                sb.AppendLine($"    or set Parameters__{ParameterName.Replace('-', '_')}=<value>");
                break;

            case ConfigureSymbol.AuthTokenInvalid:
                sb.AppendLine($"  parameter: {ParameterName}");
                sb.AppendLine($"  url:       {Url}");
                break;

            case ConfigureSymbol.CoolifyVersionBelowFloor:
                sb.AppendLine($"  url:       {Url}");
                sb.AppendLine($"  observed:  {Observed}");
                sb.AppendLine($"  required:  >= {Required}");
                sb.AppendLine("  see:       SUPPORTED_COOLIFY_VERSIONS.md");
                break;

            case ConfigureSymbol.CoolifyUnreachable:
                sb.AppendLine($"  url:       {Url}");
                if (!string.IsNullOrEmpty(Detail))
                {
                    sb.AppendLine($"  detail:    {Detail}");
                }
                break;
        }

        return sb.ToString();
    }

    private string HumanLine() => Symbol switch
    {
        ConfigureSymbol.AuthTokenMissing =>
            "Coolify API token parameter is unset or empty.",
        ConfigureSymbol.AuthTokenInvalid =>
            "Coolify rejected the API token.",
        ConfigureSymbol.CoolifyVersionBelowFloor =>
            "Coolify version is below the supported floor.",
        ConfigureSymbol.CoolifyUnreachable =>
            "Coolify could not be reached or did not return a usable response.",
        _ => "",
    };
}

/// <summary>
/// Outcome of the configure phase. On success carries the resolved <see cref="ICoolifyClient"/>
/// for downstream phases; on failure carries the <see cref="ConfigureDiagnostic"/> already
/// written to stderr.
/// </summary>
public sealed class ConfigureOutcome
{
    private ConfigureOutcome() { }

    public bool Succeeded { get; private init; }
    public ICoolifyClient? Client { get; private init; }
    public string? Version { get; private init; }
    public ConfigureDiagnostic? Diagnostic { get; private init; }

    public static ConfigureOutcome Ok(ICoolifyClient client, string version) =>
        new() { Succeeded = true, Client = client, Version = version };

    public static ConfigureOutcome Fail(ConfigureDiagnostic diagnostic) =>
        new() { Succeeded = false, Diagnostic = diagnostic };
}
