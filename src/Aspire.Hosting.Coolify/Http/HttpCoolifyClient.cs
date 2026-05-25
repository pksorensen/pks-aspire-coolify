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
        Applications = new HttpApplicationsApi(this);
    }

    public IPrivateRegistriesApi PrivateRegistries { get; }
    public IDestinationsApi Destinations { get; }
    public IProjectsApi Projects { get; }
    public IEnvironmentsApi Environments { get; }
    public IServicesApi Services { get; }
    public IDeployJobsApi DeployJobs { get; }
    public IServiceEnvVarsApi ServiceEnvVars { get; }
    public IApplicationsApi Applications { get; }

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
                // Coolify v4's GET /api/v1/projects/{name} actually expects a UUID, not a
                // name — so the by-name GET always 404s and the old code POSTed a fresh
                // project on every deploy, producing duplicates. List + filter by name (or
                // uuid, in case the caller passed a UUID) instead.
                var list = await _c.SendForJsonAsync<List<ProjectListItem>>(
                    HttpMethod.Get, "api/v1/projects", body: null, ct)
                    .ConfigureAwait(false);

                if (list is not null)
                {
                    var match = list.FirstOrDefault(p => string.Equals(p.Uuid, name, StringComparison.OrdinalIgnoreCase))
                        ?? list.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !string.IsNullOrEmpty(match.Uuid))
                    {
                        return ProjectUpsertResult.Unchanged(match.Uuid);
                    }
                }

                var body = new NamedBody(name);
                var created = await _c.SendForJsonAsync<IdResponse>(
                    HttpMethod.Post, "api/v1/projects", body, ct).ConfigureAwait(false);
                return ProjectUpsertResult.Created(created?.Id ?? name);
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return ProjectUpsertResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return ProjectUpsertResult.Failure(ex.Message); }
            catch (CoolifyUnparseableResponseException ex) { return ProjectUpsertResult.Failure(ex.Message); }
        }

        private sealed record ProjectListItem(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("name")] string? Name);
    }

    private sealed class HttpEnvironmentsApi : IEnvironmentsApi
    {
        private readonly HttpCoolifyClient _c;
        public HttpEnvironmentsApi(HttpCoolifyClient c) { _c = c; }

        public async Task<EnvironmentUpsertResult> UpsertAsync(string projectId, string name, CancellationToken ct)
        {
            try
            {
                // Coolify v4 auto-creates a lowercase "production" environment on project
                // creation, but the create-listener that does it is async — a GET that
                // races the listener will return an empty list, and a subsequent POST
                // with the same name (case-insensitively) 500s when the listener lands.
                // Strategy: list envs, case-insensitive match against requested name;
                // retry the list up to a few times if the project was just created
                // and the listener hasn't completed yet.
                List<EnvironmentListItem>? envs = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    envs = await _c.SendForJsonAsync<List<EnvironmentListItem>>(
                        HttpMethod.Get,
                        $"api/v1/projects/{Uri.EscapeDataString(projectId)}/environments",
                        body: null, ct)
                        .ConfigureAwait(false);

                    if (envs is { Count: > 0 })
                    {
                        break;
                    }
                    // Back off briefly to give Coolify's project-create listener time to
                    // materialise the default environment. 200ms x 5 = up to 1s of wait,
                    // which is well within an `aspire deploy` budget and far longer than
                    // a synchronous create takes.
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }

                if (envs is { Count: > 0 })
                {
                    var match = envs.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? envs.FirstOrDefault(e => string.Equals(e.Uuid, name, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        return EnvironmentUpsertResult.Unchanged(match.Uuid ?? match.Name ?? name);
                    }
                }

                // Genuinely no env matches → POST to create one. (Rare in v4: every
                // project gets a default env via the listener. But preserved for older
                // Coolify versions and for explicit non-default env names.)
                var body = new NamedBody(name);
                var created = await _c.SendForJsonAsync<IdResponse>(
                    HttpMethod.Post,
                    $"api/v1/projects/{Uri.EscapeDataString(projectId)}/environments",
                    body, ct).ConfigureAwait(false);
                return EnvironmentUpsertResult.Created(created?.Id ?? name);
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return EnvironmentUpsertResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return EnvironmentUpsertResult.Failure(ex.Message); }
            catch (CoolifyUnparseableResponseException ex) { return EnvironmentUpsertResult.Failure(ex.Message); }
        }

        private sealed record EnvironmentListItem(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("name")] string? Name);
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
                // Coolify v4 uses /api/v1/applications/dockerimage for image-based workloads
                // — NOT a per-project/per-env services subpath. See routes/api.php line 117 in
                // coollabsio/coolify. Body fields required by ApplicationsController::create_application:
                //   project_uuid (req), server_uuid (req), destination_uuid, environment_uuid|name,
                //   name, docker_registry_image_name, docker_registry_image_tag, ports_exposes
                //
                // 1. Look up existing application with this name to make the upsert idempotent.
                var existing = await _c.SendForJsonAsync<List<AppListItem>>(
                    HttpMethod.Get, "api/v1/applications", body: null, ct)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    var match = existing.FirstOrDefault(a => string.Equals(a.Name, resourceName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !string.IsNullOrEmpty(match.Uuid))
                    {
                        return ServiceUpsertResult.Unchanged(match.Uuid);
                    }
                }

                // 2. Need a server_uuid. List servers and auto-pick if exactly one — matches
                //    the destination-create flow's discovery.
                var servers = await _c.SendForJsonAsync<List<ServerListItem2>>(
                    HttpMethod.Get, "api/v1/servers", body: null, ct)
                    .ConfigureAwait(false);
                if (servers is null || servers.Count == 0)
                {
                    return ServiceUpsertResult.Failure("No Coolify servers visible; cannot create application.");
                }
                if (servers.Count > 1)
                {
                    return ServiceUpsertResult.Failure(
                        "Multiple Coolify servers visible; auto-select not supported for application creation. " +
                        "Single-server homelab setups work; multi-server needs an explicit WithCoolifyServer.");
                }
                var serverUuid = servers[0].Uuid;
                if (string.IsNullOrEmpty(serverUuid))
                {
                    return ServiceUpsertResult.Failure("Coolify server response missing uuid.");
                }

                // 3. Parse image tag `<registry>/<name>:<tag>` into (name, tag) for the body.
                //    Last colon separates tag from name (registry hostnames don't contain colons
                //    in the rightmost segment after a `/`).
                string imageName = spec.Image, imageTag = "latest";
                var slashIdx = spec.Image.LastIndexOf('/');
                var colonIdx = spec.Image.LastIndexOf(':');
                if (colonIdx > slashIdx)
                {
                    imageName = spec.Image[..colonIdx];
                    imageTag = spec.Image[(colonIdx + 1)..];
                }

                var createBody = new DockerImageAppBody(
                    Name: resourceName,
                    ProjectUuid: projectId,
                    ServerUuid: serverUuid!,
                    DestinationUuid: spec.DestinationBinding,
                    EnvironmentUuid: environmentId,
                    DockerRegistryImageName: imageName,
                    DockerRegistryImageTag: imageTag,
                    PortsExposes: "80",
                    InstantDeploy: false);

                using var resp = await _c.SendRawAsync(
                    HttpMethod.Post,
                    "api/v1/applications/dockerimage",
                    createBody, ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    var id = await _c.ReadIdAsync(resp, ct).ConfigureAwait(false) ?? resourceName;
                    return ServiceUpsertResult.Created(id);
                }

                var detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ServiceUpsertResult.Failure(
                    $"Coolify returned HTTP {(int)resp.StatusCode} creating application: {detail}");
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex) { return ServiceUpsertResult.Failure(ex.Message); }
            catch (CoolifyTransportException ex) { return ServiceUpsertResult.Failure(ex.Message); }
            catch (CoolifyUnparseableResponseException ex) { return ServiceUpsertResult.Failure(ex.Message); }
        }

        private sealed record AppListItem(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("name")] string? Name);

        private sealed record ServerListItem2(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("name")] string? Name);

        private sealed record DockerImageAppBody(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("project_uuid")] string ProjectUuid,
            [property: JsonPropertyName("server_uuid")] string ServerUuid,
            [property: JsonPropertyName("destination_uuid")] string? DestinationUuid,
            [property: JsonPropertyName("environment_uuid")] string EnvironmentUuid,
            [property: JsonPropertyName("docker_registry_image_name")] string DockerRegistryImageName,
            [property: JsonPropertyName("docker_registry_image_tag")] string DockerRegistryImageTag,
            [property: JsonPropertyName("ports_exposes")] string PortsExposes,
            [property: JsonPropertyName("instant_deploy")] bool InstantDeploy);

        public async Task<DeployTriggerResult> TriggerDeployAsync(string serviceId, CancellationToken ct)
        {
            try
            {
                // Coolify v4 trigger endpoint for image-based applications is
                //   POST /api/v1/applications/{uuid}/start  (see routes/api.php line 134).
                // Not /api/v1/services/{id}/deploy — that path doesn't exist.
                using var resp = await _c.SendRawAsync(
                    HttpMethod.Post,
                    $"api/v1/applications/{Uri.EscapeDataString(serviceId)}/start",
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

    // Implements FT-016: HTTP-backed IApplicationsApi for the coolify-prereq phase.
    // Provisions the in-project registry's Coolify Application + triggers/awaits its deploy.
    private sealed class HttpApplicationsApi : IApplicationsApi
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PollBudget = TimeSpan.FromMinutes(5);

        private readonly HttpCoolifyClient _c;
        public HttpApplicationsApi(HttpCoolifyClient c) { _c = c; }

        public async Task<ApplicationProvisionResult> ProvisionRegistryAsync(
            ApplicationProvisionRequest request, CancellationToken ct)
        {
            try
            {
                // 1. Resolve project_uuid via list-by-name idempotency, creating if absent.
                var projects = await _c.SendForJsonAsync<List<NamedUuid>>(
                    HttpMethod.Get, "api/v1/projects", body: null, ct).ConfigureAwait(false);

                string? projectUuid = projects?
                    .FirstOrDefault(p => string.Equals(p.Name, request.ApphostProjectName, StringComparison.OrdinalIgnoreCase))
                    ?.Uuid;

                if (string.IsNullOrEmpty(projectUuid))
                {
                    var created = await _c.SendForJsonAsync<IdResponse>(
                        HttpMethod.Post, "api/v1/projects",
                        new NamedBody(request.ApphostProjectName), ct).ConfigureAwait(false);
                    projectUuid = created?.IdOrUuid;
                }

                if (string.IsNullOrEmpty(projectUuid))
                {
                    return new ApplicationProvisionResult(false, null, null, null, null,
                        $"could not resolve or create Coolify project '{request.ApphostProjectName}'");
                }

                // 2. List existing applications and match by name for idempotency.
                var existing = await _c.SendForJsonAsync<List<AppListItem>>(
                    HttpMethod.Get, "api/v1/applications", body: null, ct).ConfigureAwait(false);

                string? appUuid = existing?
                    .FirstOrDefault(a => string.Equals(a.Name, request.RegistryResourceName, StringComparison.OrdinalIgnoreCase))
                    ?.Uuid;

                if (string.IsNullOrEmpty(appUuid))
                {
                    // 2b. Ensure the target environment exists (Coolify creates `production`
                    // asynchronously when a project is created — the env upsert handles the race
                    // and returns the UUID we need).
                    var envResult = await _c.Environments
                        .UpsertAsync(projectUuid, request.EnvironmentName, ct).ConfigureAwait(false);
                    if (envResult.Kind == UpsertKind.Failure || string.IsNullOrEmpty(envResult.Id))
                    {
                        return new ApplicationProvisionResult(false, null, null, projectUuid, null,
                            $"could not resolve Coolify environment '{request.EnvironmentName}' for project {projectUuid}: {envResult.ErrorMessage ?? "(no detail)"}");
                    }

                    // 3. Discover the single visible server.
                    var servers = await _c.SendForJsonAsync<List<NamedUuid>>(
                        HttpMethod.Get, "api/v1/servers", body: null, ct).ConfigureAwait(false);
                    if (servers is null || servers.Count == 0)
                    {
                        return new ApplicationProvisionResult(false, null, null, projectUuid, null,
                            "no Coolify servers visible; cannot create registry application");
                    }
                    if (servers.Count > 1)
                    {
                        return new ApplicationProvisionResult(false, null, null, projectUuid, null,
                            "multiple Coolify servers visible; auto-select not supported for registry application");
                    }
                    var serverUuid = servers[0].Uuid;
                    if (string.IsNullOrEmpty(serverUuid))
                    {
                        return new ApplicationProvisionResult(false, null, null, projectUuid, null,
                            "Coolify server response missing uuid");
                    }

                    // 4. Parse `<image>:<tag>` (mirror HttpServicesApi.UpsertAsync).
                    string imageName = request.Image, imageTag = "latest";
                    var slashIdx = request.Image.LastIndexOf('/');
                    var colonIdx = request.Image.LastIndexOf(':');
                    if (colonIdx > slashIdx)
                    {
                        imageName = request.Image[..colonIdx];
                        imageTag = request.Image[(colonIdx + 1)..];
                    }

                    // 4b. Resolve destination UUID when WithCoolifyDestination(literal) was set —
                    // Coolify rejects POST /applications/dockerimage with HTTP 400 when the server
                    // has multiple destinations and the body omits destination_uuid.
                    string? destinationUuid = null;
                    if (!string.IsNullOrWhiteSpace(request.DestinationName))
                    {
                        var destResult = await _c.Destinations
                            .LookupOrUpsertAsync(request.DestinationName!, ct).ConfigureAwait(false);
                        if (destResult.Kind == UpsertKind.Failure || string.IsNullOrEmpty(destResult.Id))
                        {
                            return new ApplicationProvisionResult(false, null, null, projectUuid, null,
                                $"could not resolve Coolify destination '{request.DestinationName}': {destResult.ErrorMessage ?? "(no detail)"}");
                        }
                        destinationUuid = destResult.Id;
                    }

                    var createBody = new DockerImageAppBody2(
                        Name: request.RegistryResourceName,
                        ProjectUuid: projectUuid,
                        ServerUuid: serverUuid!,
                        DestinationUuid: destinationUuid,
                        EnvironmentUuid: envResult.Id!,
                        DockerRegistryImageName: imageName,
                        DockerRegistryImageTag: imageTag,
                        PortsExposes: request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        InstantDeploy: false);

                    using var resp = await _c.SendRawAsync(
                        HttpMethod.Post, "api/v1/applications/dockerimage", createBody, ct)
                        .ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        return new ApplicationProvisionResult(false, null, null, projectUuid, null,
                            $"Coolify returned HTTP {(int)resp.StatusCode} creating registry application: {detail}");
                    }
                    appUuid = await _c.ReadIdAsync(resp, ct).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(appUuid))
                {
                    return new ApplicationProvisionResult(false, null, null, projectUuid, null,
                        "Coolify did not return a UUID for the registry application");
                }

                // 5. Read back the application to discover the assigned FQDN.
                string? fqdn = null;
                try
                {
                    var detail = await _c.SendForJsonAsync<AppDetailBody>(
                        HttpMethod.Get,
                        $"api/v1/applications/{Uri.EscapeDataString(appUuid)}",
                        body: null, ct).ConfigureAwait(false);
                    fqdn = NormalizeFqdn(detail?.Fqdn);
                }
                catch (CoolifyTransportException) { /* leave fqdn null */ }
                catch (CoolifyUnparseableResponseException) { /* leave fqdn null */ }

                return new ApplicationProvisionResult(
                    Succeeded: true,
                    ApplicationUuid: appUuid,
                    PrivateRegistryUuid: null,
                    ProjectUuid: projectUuid,
                    AutoDiscoveredFqdn: fqdn,
                    ErrorMessage: null);
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex)
            { return new ApplicationProvisionResult(false, null, null, null, null, ex.Message); }
            catch (CoolifyTransportException ex)
            { return new ApplicationProvisionResult(false, null, null, null, null, ex.Message); }
            catch (CoolifyUnparseableResponseException ex)
            { return new ApplicationProvisionResult(false, null, null, null, null, ex.Message); }
        }

        public async Task<ApplicationDeployResult> TriggerAndAwaitAsync(
            string applicationUuid, CancellationToken ct)
        {
            string? deploymentUuid = null;
            try
            {
                using var startResp = await _c.SendRawAsync(
                    HttpMethod.Post,
                    $"api/v1/applications/{Uri.EscapeDataString(applicationUuid)}/start",
                    body: null, ct).ConfigureAwait(false);

                if (!startResp.IsSuccessStatusCode)
                {
                    var detail = await startResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return new ApplicationDeployResult(false, null,
                        $"Coolify returned HTTP {(int)startResp.StatusCode} triggering registry deploy: {detail}");
                }

                // Coolify's /start returns `{"deployment_uuid":"…"}`; tolerate `id`/`uuid` as well.
                await using (var stream = await startResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        var dto = await JsonSerializer.DeserializeAsync<StartResponse>(stream, JsonOptions, ct)
                            .ConfigureAwait(false);
                        deploymentUuid = dto?.DeploymentUuid ?? dto?.Uuid ?? dto?.Id;
                    }
                    catch (JsonException) { /* fall through; deploymentUuid stays null */ }
                }

                if (string.IsNullOrEmpty(deploymentUuid))
                {
                    return new ApplicationDeployResult(false, null,
                        "Coolify /start did not return a deployment_uuid to poll");
                }

                // Poll /api/v1/deployments/{uuid} for a terminal status.
                var deadline = DateTime.UtcNow + PollBudget;
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();

                    using var poll = await _c.SendRawAsync(
                        HttpMethod.Get,
                        $"api/v1/deployments/{Uri.EscapeDataString(deploymentUuid)}",
                        body: null, ct).ConfigureAwait(false);

                    if (poll.IsSuccessStatusCode)
                    {
                        DeploymentBody? dto = null;
                        try
                        {
                            await using var stream = await poll.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                            dto = await JsonSerializer.DeserializeAsync<DeploymentBody>(stream, JsonOptions, ct)
                                .ConfigureAwait(false);
                        }
                        catch (JsonException) { /* treat as transient */ }

                        var status = dto?.Status?.Trim().ToLowerInvariant();
                        switch (status)
                        {
                            case "finished":
                            case "success":
                            case "succeeded":
                            case "ok":
                                return new ApplicationDeployResult(true, deploymentUuid, null);
                            case "failed":
                            case "failure":
                            case "error":
                            case "cancelled-by-user":
                            case "cancelled":
                                return new ApplicationDeployResult(false, deploymentUuid,
                                    $"deploy-action terminal state: {status}");
                        }
                    }
                    else if (poll.StatusCode != HttpStatusCode.NotFound)
                    {
                        // Non-404 non-success: surface immediately.
                        return new ApplicationDeployResult(false, deploymentUuid,
                            $"Coolify returned HTTP {(int)poll.StatusCode} polling deployment");
                    }

                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                }

                return new ApplicationDeployResult(false, deploymentUuid,
                    $"deploy did not reach a terminal state within {PollBudget}");
            }
            catch (OperationCanceledException) { throw; }
            catch (CoolifyAuthException ex)
            { return new ApplicationDeployResult(false, deploymentUuid, ex.Message); }
            catch (CoolifyTransportException ex)
            { return new ApplicationDeployResult(false, deploymentUuid, ex.Message); }
            catch (CoolifyUnparseableResponseException ex)
            { return new ApplicationDeployResult(false, deploymentUuid, ex.Message); }
        }

        private static string? NormalizeFqdn(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var first = raw.Split(',')[0].Trim();
            if (first.Length == 0) return null;
            if (first.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                first = first["https://".Length..];
            else if (first.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                first = first["http://".Length..];
            var slash = first.IndexOf('/');
            if (slash >= 0) first = first[..slash];
            return first.Length == 0 ? null : first;
        }

        private sealed record NamedUuid(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("name")] string? Name);

        private sealed record AppListItem(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("name")] string? Name);

        private sealed record DockerImageAppBody2(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("project_uuid")] string ProjectUuid,
            [property: JsonPropertyName("server_uuid")] string ServerUuid,
            [property: JsonPropertyName("destination_uuid")] string? DestinationUuid,
            [property: JsonPropertyName("environment_uuid")] string EnvironmentUuid,
            [property: JsonPropertyName("docker_registry_image_name")] string DockerRegistryImageName,
            [property: JsonPropertyName("docker_registry_image_tag")] string DockerRegistryImageTag,
            [property: JsonPropertyName("ports_exposes")] string PortsExposes,
            [property: JsonPropertyName("instant_deploy")] bool InstantDeploy);

        private sealed record AppDetailBody(
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("fqdn")] string? Fqdn);

        private sealed record StartResponse(
            [property: JsonPropertyName("deployment_uuid")] string? DeploymentUuid,
            [property: JsonPropertyName("uuid")] string? Uuid,
            [property: JsonPropertyName("id")] string? Id);

        private sealed record DeploymentBody(
            [property: JsonPropertyName("status")] string? Status);
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
