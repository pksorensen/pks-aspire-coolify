// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify;

/// <summary>
/// Per-deploy interactivity + parameter-prompt surface (FT-012). The publisher consults
/// <see cref="IsInteractive"/> when a parameter handle resolves to an empty value: on the
/// interactive branch it calls <see cref="PromptAsync"/> to obtain the value; on the
/// non-interactive branch it falls back to the matching <c>E_…</c> fail-fast symbol verbatim
/// (FT-012 I-1).
/// </summary>
/// <remarks>
/// FT-012 I-6: the property-only contract. The publisher does not name the Aspire 13 API
/// that realises the prompt; consumers / tests bind <see cref="CoolifyDeployingPublisher.Prompter"/>
/// to whatever Aspire surface they have. The default (<see cref="NonInteractivePrompter"/>)
/// is CI-safe — interactivity is opt-in, never inferred.
/// </remarks>
public interface IParameterPrompter
{
    /// <summary>True iff the current deploy is interactive (TTY available, no
    /// <c>--non-interactive</c>, host signals interactivity). False is the CI-safe default.</summary>
    bool IsInteractive { get; }

    /// <summary>
    /// Prompt the user for a parameter value. Implementations must honour
    /// <paramref name="cancellationToken"/> by throwing <see cref="OperationCanceledException"/>
    /// when the deploy is cancelled mid-prompt (FT-012 §Behaviour, scenario 7 of TC-019).
    /// Implementations should redact secret input (no echo) when
    /// <see cref="ParameterPromptRequest.IsSecret"/> is true.
    /// </summary>
    /// <returns>The prompted value, or <c>null</c> / empty string when the user supplied no value.</returns>
    Task<string?> PromptAsync(ParameterPromptRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Describes a single parameter the publisher needs the user to supply. FT-012 I-2: the
/// prompter renders <see cref="IsSecret"/>-true parameters with no-echo input; FT-012 I-6
/// keeps the API surface intentionally minimal.
/// </summary>
public sealed record ParameterPromptRequest(string ParameterName, bool IsSecret);

/// <summary>
/// Default prompter — never interactive. Selected when the publisher is constructed against
/// a host that does not surface an interactivity signal (or when running under
/// <c>aspire deploy --non-interactive</c> / CI). FT-012 I-1: a deploy that has not opted in
/// to interactivity preserves the pre-FT-012 fail-fast contract verbatim.
/// </summary>
public sealed class NonInteractivePrompter : IParameterPrompter
{
    public static readonly NonInteractivePrompter Instance = new();

    public bool IsInteractive => false;

    public Task<string?> PromptAsync(ParameterPromptRequest request, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
