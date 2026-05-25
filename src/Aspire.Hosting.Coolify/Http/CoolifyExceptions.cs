using System.Net;

// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify.Http;

/// <summary>
/// FT-013 §I-6: uniform transport-failure shape. Connection refused, DNS failure, TLS
/// handshake failure, request timeout, and any HTTP <c>5xx</c> (and HTTP <c>404</c> on
/// the probe path) surface as this single exception so FT-002's configure-phase
/// classifier maps them to <c>E_COOLIFY_UNREACHABLE</c> with one catch.
/// </summary>
internal sealed class CoolifyTransportException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public CoolifyTransportException(string message, Exception? inner = null, HttpStatusCode? statusCode = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// FT-013 §I-7: auth-failure shape, distinguishable from <see cref="CoolifyTransportException"/>.
/// <c>401</c> and <c>403</c> both surface as this; ADR-004 §"one opaque error path for auth"
/// keeps the distinction inside the status code, not the shape.
/// </summary>
internal sealed class CoolifyAuthException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public CoolifyAuthException(string message, HttpStatusCode statusCode, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// FT-013 §Error handling: HTTP 200 OK with a body that cannot be deserialized into the
/// expected DTO surfaces as this distinct shape (not a transport failure). FT-002's
/// configure-phase classifier maps this to <c>E_COOLIFY_UNREACHABLE</c> as well, but the
/// distinct type prevents callers from silently swallowing parse errors.
/// </summary>
internal sealed class CoolifyUnparseableResponseException : Exception
{
    public CoolifyUnparseableResponseException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
