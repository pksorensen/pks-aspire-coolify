using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Implements ADR-002: Coolify API version and client strategy (v1)
// Implements ADR-003: Imperative deploy orchestration with idempotent per-resource upserts (v1)
// Implements ADR-004: Coolify auth model — bearer token via Aspire secret parameters, per-instance (v1)
namespace Aspire.Hosting.Coolify.Http;

/// <summary>
/// FT-013 — concrete HTTP-backed <see cref="ICoolifyClient"/>. One instance per
/// <c>(url, token)</c> pair per <c>aspire deploy</c>. The bearer header is set once on the
/// underlying <see cref="HttpClient"/> so every outbound request carries it (FT-013 §I-1).
/// </summary>
internal sealed class HttpCoolifyClient : ICoolifyClient, IDisposable
{
    // FT-013 §JSON shape per ADR-002 §5–§6:
    //   PropertyNameCaseInsensitive = true        → case-insensitive read
    //   default JsonUnmappedMemberHandling (Skip) → unknown response fields ignored
    //   DefaultIgnoreCondition = WhenWritingNull  → null fields omitted on write,
    //     producing "publisher only sends fields it explicitly set" conservative serialization
    //     (provided publisher DTOs default unmodelled fields to null).
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    private readonly HttpClient _http;

    public HttpCoolifyClient(string baseUrl, string bearerToken)
        : this(baseUrl, bearerToken, handler: null, timeout: null)
    {
    }

    internal HttpCoolifyClient(string baseUrl, string bearerToken, HttpMessageHandler? handler, TimeSpan? timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);

        // Trailing slash ensures relative-path composition works as expected.
        var trimmed = baseUrl.TrimEnd('/') + "/";
        _http.BaseAddress = new Uri(trimmed);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        _http.Timeout = timeout ?? DefaultTimeout;

        PrivateRegistries = new HttpPrivateRegistriesApi(this);
        Destinations = new HttpDestinationsApi(this);
        Projects = new HttpProjectsApi(this);
        Environments = new HttpEnvironmentsApi(this);
        Services = new HttpServicesApi(this);
        DeployJobs = new HttpDeployJobsApi(this);
        ServiceEnvVars = new HttpServiceEnvVarsApi(this);
    }

    public IPrivateRegistriesApi PrivateRegistries { get; }
    public IDestinationsApi Destinations { get; }
    public IProjectsApi Projects { get; }
    public IEnvironmentsApi Environments { get; }
    public IServicesApi Services { get; }
    public IDeployJobsApi DeployJobs { get; }
    public IServiceEnvVarsApi ServiceEnvVars { get; }

    public async Task<CoolifyProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Coolify v4 returns the version as a bare string ("4.0.0-beta.470") with
            // Content-Type: text/html. ADR-002 §"tolerant deserialization": accept both
            // the bare-string shape AND a forward-compatible {"version":"x"} JSON shape
            // in case the surface evolves.
            using var response = await SendRawAsync(
                HttpMethod.Get, "api/v1/version", body: null, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw TransportFromStatus(response.StatusCode, "api/v1/version");
            }

            var raw = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return CoolifyProbeResult.UnparseableResponse("empty response from /api/v1/version");
            }

            string? version = null;
            if (raw.StartsWith('{'))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<VersionResponse>(raw, JsonOptions);
                    version = parsed?.Version;
                }
                catch (JsonException)
                {
                    // Fall through — try as plain text.
                }
            }
            version ??= raw.Trim('"');

            if (string.IsNullOrEmpty(version))
            {
                return CoolifyProbeResult.UnparseableResponse("version field missing or empty");
            }

            return CoolifyProbeResult.Success(version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoolifyAuthException ex)
        {
            return CoolifyProbeResult.AuthRejected(ex.StatusCode);
        }
        catch (CoolifyUnparseableResponseException ex)
        {
            return CoolifyProbeResult.UnparseableResponse(ex.Message);
        }
        catch (CoolifyTransportException ex)
        {
            return CoolifyProbeResult.TransportFailure(ex.Message, ex.StatusCode);
        }
    }

    /// <summary>
    /// FT-013 transport core: issues one HTTP request, throws typed exceptions on failure
    /// (FT-013 §I-6 / §I-7), and never includes the bearer token in any thrown message.
    /// </summary>
    internal async Task<T?> SendForJsonAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw TransportFromStatus(response.StatusCode, path);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new CoolifyUnparseableResponseException(
                $"Failed to deserialize response from {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Lower-level send. Returns the response so endpoint groups can inspect status
    /// (e.g. to distinguish 404 from a transport failure during an upsert pre-check).
    /// Still throws <see cref="CoolifyAuthException"/> on 401/403 and
    /// <see cref="CoolifyTransportException"/> on transport-level faults.
    /// </summary>
    internal async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            // Serialize via the static type at runtime so derived shapes are handled.
            var json = JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient.Timeout fires TaskCanceledException with no caller cancellation.
            throw new CoolifyTransportException(
                $"HTTP request to {path} timed out: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            // Covers connection refused, DNS failure, TLS handshake failure,
            // socket reset, etc. HttpRequestException carries no headers, so the
            // captured message never contains the bearer token.
            throw new CoolifyTransportException(
                $"HTTP request to {path} failed: {ex.Message}", ex);
        }
        finally
        {
            request.Dispose();
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new CoolifyAuthException(
                $"Coolify rejected the bearer token (HTTP {(int)status} on {path}).", status);
        }

        return response;
    }

    private static CoolifyTransportException TransportFromStatus(HttpStatusCode status, string path) =>
        new($"Coolify returned HTTP {(int)status} on {path}.", inner: null, statusCode: status);

    public void Dispose() => _http.Dispose();

    private sealed record VersionResponse([property: JsonPropertyName("version")] string? Version);

    // ────────────────────────────────────────────────────────────────────────
    // Endpoint groups. Literal paths under /api/v1/... are chosen by the
    // implementer per FT-013 §"Out of scope"; the contract is the methods'
    // observable behaviour, not the path strings.
    // ────────────────────────────────────────────────────────────────────────

    private sealed class HttpPrivateRegistriesApi : IPrivateRegistriesApi
    {
        private readonly HttpCoolifyClient _c;
        public HttpPrivateRegistriesApi(HttpCoolifyClient c) { _c = c; }

        public async Task<PrivateRegistryUpsertResult> UpsertAsync(
            string host, string username, string password, CancellationToken ct)
        {
            try
            {
                var body = new RegistryBody(host, username, password);
                using var resp = await _c.SendRawAsync(
                    HttpMethod.Post, "api/v1/private-registries", body, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return PrivateRegistryUpsertResult.Success();
                }
                return PrivateRegistryUpsertResult.Failure(
                    $"Coolify returned HTTP {(int)resp.StatusCode} upserting private registry.");
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return PrivateRegistryUpsertResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return PrivateRegistryUpsertResult.Failure(ex.Message); }
        }

        private sealed record RegistryBody(
            [property: JsonPropertyName("host")] string Host,
            [property: JsonPropertyName("username")] string Username,
            [property: JsonPropertyName("password")] string Password);
    }

    private sealed class HttpDestinationsApi : IDestinationsApi
    {
        private readonly HttpCoolifyClient _c;
        public HttpDestinationsApi(HttpCoolifyClient c) { _c = c; }

        public async Task<DestinationUpsertResult> LookupOrUpsertAsync(string name, CancellationToken ct)
        {
            try
            {
                // 1. List existing destinations — caller's name may be UUID, name, or the
                //    network name we'll have to create.
                var list = await _c.SendForJsonAsync<List<DestinationListItem>>(
                    HttpMethod.Get, "api/v1/destinations", body: null, ct)
                    .ConfigureAwait(false);

                if (list is not null && list.Count > 0)
                {
                    // Match by UUID first (caller may pass UUID directly), then by name,
                    // then by network (since for many setups name == network).
                    var match = list.FirstOrDefault(d => string.Equals(d.Uuid, name, StringComparison.OrdinalIgnoreCase))
                        ?? list.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? list.FirstOrDefault(d => string.Equals(d.Network, name, StringComparison.OrdinalIgnoreCase));

                    if (match is not null)
                    {
                        return DestinationUpsertResult.Found(match.Uuid ?? name);
                    }
                }

                // 2. Not found — create it. Need a server_uuid; auto-pick if exactly one.
                var servers = await _c.SendForJsonAsync<List<ServerListItem>>(
                    HttpMethod.Get, "api/v1/servers", body: null, ct)
                    .ConfigureAwait(false);

                if (servers is null || servers.Count == 0)
                {
                    return DestinationUpsertResult.Failure(
                        "No Coolify servers visible to this token; cannot create destination.");
                }
                if (servers.Count > 1)
                {
                    var serverList = string.Join(", ", servers.Select(s => $"{s.Name} ({s.Uuid})"));
                    return DestinationUpsertResult.Failure(
                        $"Multiple Coolify servers visible ({serverList}); cannot auto-select for destination creation. " +
                        $"Pass a destination UUID via WithCoolifyDestination(\"<uuid>\") for an existing destination, " +
                        $"or pre-create the destination in the Coolify UI.");
                }

                var server = servers[0];
                if (string.IsNullOrEmpty(server.Uuid))
                {
                    return DestinationUpsertResult.Failure("Coolify server response missing uuid; cannot create destination.");
                }

                // 3. POST /api/v1/servers/{server_uuid}/destinations creates a new
                //    StandaloneDocker destination. Network must match the regex
                //    `[a-zA-Z0-9][a-zA-Z0-9._-]*`; we use the supplied `name` as the network.
                var createBody = new DestinationCreateBody(Network: name, Type: "standalone");
                using var createResp = await _c.SendRawAsync(
                    HttpMethod.Post,
                    $"api/v1/servers/{Uri.EscapeDataString(server.Uuid)}/destinations",
                    body: createBody, ct)
                    .ConfigureAwait(false);

                if (!createResp.IsSuccessStatusCode)
                {
                    var detail = await createResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return DestinationUpsertResult.Failure(
                        $"Coolify returned HTTP {(int)createResp.StatusCode} creating destination '{name}' on server {server.Name ?? server.Uuid}: {detail}");
                }

                var created = await _c.ReadIdAsync(createResp, ct).ConfigureAwait(false);
                return DestinationUpsertResult.Found(created ?? name);
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return DestinationUpsertResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return DestinationUpsertResult.Failure(ex.Message); }
            catch (CoolifyUnparseableResponseException ex) { return DestinationUpsertResult.Failure(ex.Message); }
        }

        private sealed record DestinationListItem(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("name")] string? Name,
            [property: JsonPropertyName("network")] string? Network,
            [property: JsonPropertyName("type")] string? Type,
            [property: JsonPropertyName("server_uuid")] string? ServerUuid);

        private sealed record ServerListItem(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("name")] string? Name);

        private sealed record DestinationCreateBody(
            [property: JsonPropertyName("network")] string Network,
            [property: JsonPropertyName("type")] string Type);
    }

    private sealed class HttpProjectsApi : IProjectsApi
    {
        private readonly HttpCoolifyClient _c;
        public HttpProjectsApi(HttpCoolifyClient c) { _c = c; }

        public async Task<ProjectUpsertResult> UpsertAsync(string name, CancellationToken ct)
        {
            try
            {
                using var get = await _c.SendRawAsync(
                    HttpMethod.Get, $"api/v1/projects/{Uri.EscapeDataString(name)}", body: null, ct)
                    .ConfigureAwait(false);
                if (get.IsSuccessStatusCode)
                {
                    var id = await _c.ReadIdAsync(get, ct).ConfigureAwait(false) ?? name;
                    return ProjectUpsertResult.Unchanged(id);
                }
                if (get.StatusCode == HttpStatusCode.NotFound)
                {
                    var body = new NamedBody(name);
                    var created = await _c.SendForJsonAsync<IdResponse>(
                        HttpMethod.Post, "api/v1/projects", body, ct).ConfigureAwait(false);
                    return ProjectUpsertResult.Created(created?.Id ?? name);
                }
                return ProjectUpsertResult.Failure(
                    $"Coolify returned HTTP {(int)get.StatusCode} fetching project.");
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return ProjectUpsertResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return ProjectUpsertResult.Failure(ex.Message); }
            catch (CoolifyUnparseableResponseException ex) { return ProjectUpsertResult.Failure(ex.Message); }
        }
    }

    private sealed class HttpEnvironmentsApi : IEnvironmentsApi
    {
        private readonly HttpCoolifyClient _c;
        public HttpEnvironmentsApi(HttpCoolifyClient c) { _c = c; }

        public async Task<EnvironmentUpsertResult> UpsertAsync(string projectId, string name, CancellationToken ct)
        {
            try
            {
                var path = $"api/v1/projects/{Uri.EscapeDataString(projectId)}/environments/{Uri.EscapeDataString(name)}";
                using var get = await _c.SendRawAsync(HttpMethod.Get, path, body: null, ct).ConfigureAwait(false);
                if (get.IsSuccessStatusCode)
                {
                    var id = await _c.ReadIdAsync(get, ct).ConfigureAwait(false) ?? name;
                    return EnvironmentUpsertResult.Unchanged(id);
                }
                if (get.StatusCode == HttpStatusCode.NotFound)
                {
                    var body = new NamedBody(name);
                    var created = await _c.SendForJsonAsync<IdResponse>(
                        HttpMethod.Post,
                        $"api/v1/projects/{Uri.EscapeDataString(projectId)}/environments",
                        body, ct).ConfigureAwait(false);
                    return EnvironmentUpsertResult.Created(created?.Id ?? name);
                }
                return EnvironmentUpsertResult.Failure(
                    $"Coolify returned HTTP {(int)get.StatusCode} fetching environment.");
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return EnvironmentUpsertResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return EnvironmentUpsertResult.Failure(ex.Message); }
            catch (CoolifyUnparseableResponseException ex) { return EnvironmentUpsertResult.Failure(ex.Message); }
        }
    }

    private sealed class HttpServicesApi : IServicesApi
    {
        private readonly HttpCoolifyClient _c;
        public HttpServicesApi(HttpCoolifyClient c) { _c = c; }

        public async Task<ServiceUpsertResult> UpsertAsync(
            string projectId, string environmentId, string resourceName,
            ServiceSpec spec, CancellationToken ct)
        {
            try
            {
                var body = new ServiceBody(
                    resourceName, spec.Image, spec.RegistryReference, spec.DestinationBinding);
                using var resp = await _c.SendRawAsync(
                    HttpMethod.Post,
                    $"api/v1/projects/{Uri.EscapeDataString(projectId)}/environments/{Uri.EscapeDataString(environmentId)}/services",
                    body, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var id = await _c.ReadIdAsync(resp, ct).ConfigureAwait(false) ?? resourceName;
                    return ServiceUpsertResult.Created(id);
                }
                return ServiceUpsertResult.Failure(
                    $"Coolify returned HTTP {(int)resp.StatusCode} upserting service.");
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return ServiceUpsertResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return ServiceUpsertResult.Failure(ex.Message); }
            catch (CoolifyUnparseableResponseException ex) { return ServiceUpsertResult.Failure(ex.Message); }
        }

        public async Task<DeployTriggerResult> TriggerDeployAsync(string serviceId, CancellationToken ct)
        {
            try
            {
                using var resp = await _c.SendRawAsync(
                    HttpMethod.Post,
                    $"api/v1/services/{Uri.EscapeDataString(serviceId)}/deploy",
                    body: null, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var id = await _c.ReadIdAsync(resp, ct).ConfigureAwait(false);
                    return DeployTriggerResult.Ok(id);
                }
                return DeployTriggerResult.Failed(
                    $"Coolify returned HTTP {(int)resp.StatusCode} triggering deploy.");
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return DeployTriggerResult.Failed(ex.Message); }
            catch (CoolifyTransportException ex) { return DeployTriggerResult.Failed(ex.Message); }
            catch (CoolifyUnparseableResponseException ex) { return DeployTriggerResult.Failed(ex.Message); }
        }

        private sealed record ServiceBody(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("image")] string Image,
            [property: JsonPropertyName("registry_reference")] string? RegistryReference,
            [property: JsonPropertyName("destination_binding")] string DestinationBinding);
    }

    private sealed class HttpDeployJobsApi : IDeployJobsApi
    {
        private readonly HttpCoolifyClient _c;
        public HttpDeployJobsApi(HttpCoolifyClient c) { _c = c; }

        public async Task<DeployJobStatusResult> GetStatusAsync(string handle, CancellationToken ct)
        {
            try
            {
                var path = $"api/v1/deploy-jobs/{Uri.EscapeDataString(handle)}";
                using var resp = await _c.SendRawAsync(HttpMethod.Get, path, body: null, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    return DeployJobStatusResult.NotFound();
                }
                if (!resp.IsSuccessStatusCode)
                {
                    return DeployJobStatusResult.Transient(
                        $"Coolify returned HTTP {(int)resp.StatusCode} polling deploy-job.");
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var dto = await JsonSerializer.DeserializeAsync<DeployJobBody>(stream, JsonOptions, ct).ConfigureAwait(false);
                var raw = dto?.Status?.ToLowerInvariant();
                return raw switch
                {
                    "queued" => DeployJobStatusResult.Queued(raw),
                    "in_progress" or "running" => DeployJobStatusResult.InProgress(raw),
                    "succeeded" or "success" or "ok" => DeployJobStatusResult.Succeeded(raw),
                    "failed" or "failure" or "error" => DeployJobStatusResult.Failed(raw, dto?.Error),
                    null => DeployJobStatusResult.Transient("missing status field"),
                    _ => DeployJobStatusResult.InProgress(raw),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return DeployJobStatusResult.Transient(ex.Message); }
            catch (CoolifyTransportException ex) { return DeployJobStatusResult.Transient(ex.Message); }
            catch (JsonException ex) { return DeployJobStatusResult.Transient($"unparseable: {ex.Message}"); }
        }

        public string GetHumanUrl(string handle) =>
            new Uri(_c._http.BaseAddress!, $"deploy-jobs/{Uri.EscapeDataString(handle)}").ToString();

        private sealed record DeployJobBody(
            [property: JsonPropertyName("status")] string? Status,
            [property: JsonPropertyName("error")] string? Error);
    }

    private sealed class HttpServiceEnvVarsApi : IServiceEnvVarsApi
    {
        private readonly HttpCoolifyClient _c;
        public HttpServiceEnvVarsApi(HttpCoolifyClient c) { _c = c; }

        public async Task<EnvVarFetchResult> GetByNameAsync(string serviceId, string key, CancellationToken ct)
        {
            try
            {
                var path = $"api/v1/services/{Uri.EscapeDataString(serviceId)}/envs/{Uri.EscapeDataString(key)}";
                using var resp = await _c.SendRawAsync(HttpMethod.Get, path, body: null, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    return EnvVarFetchResult.NotFoundResult();
                }
                if (!resp.IsSuccessStatusCode)
                {
                    return EnvVarFetchResult.FailureWith(
                        $"Coolify returned HTTP {(int)resp.StatusCode} fetching env-var.");
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var dto = await JsonSerializer.DeserializeAsync<EnvVarBody>(stream, JsonOptions, ct).ConfigureAwait(false);
                return EnvVarFetchResult.FoundWith(dto?.Value ?? string.Empty, dto?.Secret ?? false);
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return EnvVarFetchResult.FailureWith(ex.Message); }
            catch (CoolifyTransportException ex) { return EnvVarFetchResult.FailureWith(ex.Message); }
            catch (JsonException ex) { return EnvVarFetchResult.FailureWith($"unparseable: {ex.Message}"); }
        }

        public async Task<EnvVarWriteResult> CreateAsync(
            string serviceId, string key, string value, bool secret, CancellationToken ct) =>
            await WriteAsync(HttpMethod.Post,
                $"api/v1/services/{Uri.EscapeDataString(serviceId)}/envs",
                new EnvVarBody(key, value, secret), ct).ConfigureAwait(false);

        public async Task<EnvVarWriteResult> PatchAsync(
            string serviceId, string key, string value, bool secret, CancellationToken ct) =>
            await WriteAsync(HttpMethod.Patch,
                $"api/v1/services/{Uri.EscapeDataString(serviceId)}/envs/{Uri.EscapeDataString(key)}",
                new EnvVarBody(key, value, secret), ct).ConfigureAwait(false);

        private async Task<EnvVarWriteResult> WriteAsync(HttpMethod method, string path, EnvVarBody body, CancellationToken ct)
        {
            try
            {
                using var resp = await _c.SendRawAsync(method, path, body, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode
                    ? EnvVarWriteResult.Ok()
                    : EnvVarWriteResult.Failure($"Coolify returned HTTP {(int)resp.StatusCode} writing env-var.");
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return EnvVarWriteResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return EnvVarWriteResult.Failure(ex.Message); }
        }

        private sealed record EnvVarBody(
            [property: JsonPropertyName("key")] string Key,
            [property: JsonPropertyName("value")] string Value,
            [property: JsonPropertyName("secret")] bool Secret);
    }

    private async Task<string?> ReadIdAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is 0)
        {
            return null;
        }
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<IdResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
            return dto?.IdOrUuid;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record IdResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("uuid")] string? Uuid)
    {
        // Coolify uses either `id` or `uuid` depending on the endpoint; tolerate both.
        public string? IdOrUuid => Id ?? Uuid;
    }

    private sealed record NamedBody([property: JsonPropertyName("name")] string Name);
}
