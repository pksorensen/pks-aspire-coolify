# Coolify Destinations REST API — Upstream PR Scope

Target: `coollabsio/coolify` (v4, current `main`, beta.470 timeframe).
Goal: add `/api/v1/destinations` so external integrations (IaC tools, our Aspire
publisher) can list and create destinations without screen-scraping the UI.

---

## 1. Where the Destination model already lives

Destinations are **two sibling Eloquent models** sharing the same shape — there
is no formal `Destination` parent class or interface. Both are owned by a
`Server` (FK `server_id`) and act as morphable parents for Applications,
Services, and all `Standalone*` databases.

| Concern               | File                                                   | Notes                                                     |
| --------------------- | ------------------------------------------------------ | --------------------------------------------------------- |
| Standalone Docker     | `app/Models/StandaloneDocker.php`                      | `BaseModel`. Fillable: `server_id`, `name`, `network`.    |
| Docker Swarm          | `app/Models/SwarmDocker.php`                           | `BaseModel`. Same fillable shape.                         |
| Server linkage        | `app/Models/Server.php` lines 654–668                  | `destinations()` concats both; `standaloneDockers()` + `swarmDockers()` are the hasMany. |
| UI create (Livewire)  | `app/Livewire/Destination/New/Docker.php`              | Validates `network` regex, then `StandaloneDocker::create([...])` or `SwarmDocker::create([...])`. |
| UI list / show        | `app/Livewire/Destination/Index.php`, `Show.php`       | Pulls per-team via `ownedByCurrentTeam()` scopes.         |

Key behavior to mirror in the API:

- Both models expose `ownedByCurrentTeam()` and `ownedByCurrentTeamAPI()` query
  scopes used elsewhere in the codebase — use these for team isolation.
- `StandaloneDocker::boot()` dispatches a job to create the docker overlay
  network on the host on `creating`. Creating a destination via the API will
  therefore actually provision the network — no extra wiring needed.
- `network` validator: `regex:/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/`, `max:255`.

Confirmed kinds: **`StandaloneDocker`** and **`SwarmDocker`** only. No K8s
destination model exists yet in this branch.

---

## 2. Closest existing API surface — Servers

File: `app/Http/Controllers/Api/ServersController.php`.
Route group: `routes/api.php` under
`middleware => ['auth:sanctum', ApiAllowed::class, 'api.sensitive']`, `prefix => 'v1'`.

Pattern used throughout:

- Team is derived via `getTeamIdFromToken()` (small helper from
  `MakesHttpResponses` / token-scoped trait used by every Api controller).
- Sensitive fields are filtered by `can_read_sensitive` token permission.
- Validation is inline `validator(...)` calls (no FormRequest classes — Coolify
  doesn't use `app/Http/Requests` for the v1 API).
- Whitelisting: requests with unknown fields are explicitly rejected (returns
  `422` listing the extras).
- Responses are plain `response()->json($model->only([...]))` arrays — no API
  Resource transformers.
- Create endpoints return `{ uuid }` with HTTP 201.

For routes/api.php the existing servers block looks like:

```php
Route::get('/servers', [ServersController::class, 'servers']);
Route::get('/servers/{uuid}', [ServersController::class, 'server_by_uuid']);
Route::post('/servers', [ServersController::class, 'create_server']);
Route::patch('/servers/{uuid}', [ServersController::class, 'update_server']);
Route::delete('/servers/{uuid}', [ServersController::class, 'delete_server']);
```

We will graft `DestinationsController` into the same group.

---

## 3. Proposed API surface

Minimal-useful set (consumer is our publisher which needs *list-by-server* and
*create*; the rest round it out so the PR isn't a one-off):

| Method | Path                                          | Purpose                            |
| ------ | --------------------------------------------- | ---------------------------------- |
| GET    | `/api/v1/destinations`                        | List all destinations on the team. |
| GET    | `/api/v1/servers/{server_uuid}/destinations`  | List destinations on a server.     |
| POST   | `/api/v1/servers/{server_uuid}/destinations`  | Create a destination on a server.  |
| GET    | `/api/v1/destinations/{uuid}`                 | Show one destination.              |
| DELETE | `/api/v1/destinations/{uuid}`                 | Delete one (only if not attached). |

**POST body**

```json
{
  "name":    "string (optional, auto-generated if empty)",
  "network": "string (required, docker-network regex)",
  "type":    "standalone|swarm (optional, default standalone)"
}
```

**Response shape (list & show)** — flat, mirrors the servers endpoint style:

```json
{
  "id": 12,
  "uuid": "abc...",
  "name": "coolify",
  "network": "coolify",
  "type": "standalone",                  // synthesized from model class
  "server_uuid": "xxx",
  "created_at": "...",
  "updated_at": "..."
}
```

Auth: same middleware chain as servers
(`auth:sanctum`, `ApiAllowed`, `api.sensitive`). Permissions: `view` to read,
`write` to create/delete (matches `ServersController` checks).

---

## 4. PR-shaped diff

### `app/Http/Controllers/Api/DestinationsController.php` (new)

```php
<?php

namespace App\Http\Controllers\Api;

use App\Http\Controllers\Controller;
use App\Models\Server;
use App\Models\StandaloneDocker;
use App\Models\SwarmDocker;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\Validator;

class DestinationsController extends Controller
{
    private function transform($d): array
    {
        return [
            'id'          => $d->id,
            'uuid'        => $d->uuid,
            'name'        => $d->name,
            'network'     => $d->network,
            'type'        => $d instanceof SwarmDocker ? 'swarm' : 'standalone',
            'server_uuid' => $d->server?->uuid,
            'created_at'  => $d->created_at,
            'updated_at'  => $d->updated_at,
        ];
    }

    public function index(Request $request)
    {
        $teamId = auth()->user()->currentTeam()->id;
        $standalone = StandaloneDocker::ownedByCurrentTeamAPI($teamId)->get();
        $swarm = SwarmDocker::ownedByCurrentTeamAPI($teamId)->get();
        return response()->json($standalone->concat($swarm)->map(fn ($d) => $this->transform($d))->values());
    }

    public function index_by_server(Request $request, string $server_uuid)
    {
        $teamId = auth()->user()->currentTeam()->id;
        $server = Server::ownedByCurrentTeamAPI($teamId)->whereUuid($server_uuid)->firstOrFail();
        $list = $server->standaloneDockers->concat($server->swarmDockers);
        return response()->json($list->map(fn ($d) => $this->transform($d))->values());
    }

    public function show(Request $request, string $uuid)
    {
        $teamId = auth()->user()->currentTeam()->id;
        $d = StandaloneDocker::ownedByCurrentTeamAPI($teamId)->whereUuid($uuid)->first()
            ?? SwarmDocker::ownedByCurrentTeamAPI($teamId)->whereUuid($uuid)->firstOrFail();
        return response()->json($this->transform($d));
    }

    public function create(Request $request, string $server_uuid)
    {
        $teamId = auth()->user()->currentTeam()->id;
        $server = Server::ownedByCurrentTeamAPI($teamId)->whereUuid($server_uuid)->firstOrFail();

        $allowed = ['name', 'network', 'type'];
        $extra = array_diff(array_keys($request->all()), $allowed);
        if (! empty($extra)) {
            return response()->json(['message' => 'Unknown fields', 'fields' => array_values($extra)], 422);
        }

        $validator = Validator::make($request->all(), [
            'name'    => 'nullable|string|max:255',
            'network' => ['required', 'string', 'max:255', 'regex:/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/'],
            'type'    => 'nullable|in:standalone,swarm',
        ]);
        if ($validator->fails()) {
            return response()->json(['message' => 'Validation failed', 'errors' => $validator->errors()], 422);
        }

        $type = $request->input('type', 'standalone');
        $name = $request->input('name') ?: ($server->name . '-' . $request->input('network'));
        $class = $type === 'swarm' ? SwarmDocker::class : StandaloneDocker::class;

        $exists = $class::where('server_id', $server->id)->where('network', $request->input('network'))->exists();
        if ($exists) {
            return response()->json(['message' => 'A destination with this network already exists on the server.'], 409);
        }

        $d = $class::create([
            'name'      => $name,
            'network'   => $request->input('network'),
            'server_id' => $server->id,
        ]);

        return response()->json(['uuid' => $d->uuid], 201);
    }

    public function delete(Request $request, string $uuid)
    {
        $teamId = auth()->user()->currentTeam()->id;
        $d = StandaloneDocker::ownedByCurrentTeamAPI($teamId)->whereUuid($uuid)->first()
            ?? SwarmDocker::ownedByCurrentTeamAPI($teamId)->whereUuid($uuid)->firstOrFail();
        if ($d->attachedTo()) {
            return response()->json(['message' => 'Destination has attached resources, detach first.'], 409);
        }
        $d->delete();
        return response()->json(['message' => 'Deleted.']);
    }
}
```

### `routes/api.php` — additions

Inside the existing `Route::group(['middleware' => ['auth:sanctum', ApiAllowed::class, 'api.sensitive'], 'prefix' => 'v1'], function () { ... })` block:

```php
Route::get('/destinations',                            [DestinationsController::class, 'index']);
Route::get('/destinations/{uuid}',                     [DestinationsController::class, 'show']);
Route::delete('/destinations/{uuid}',                  [DestinationsController::class, 'delete']);
Route::get('/servers/{server_uuid}/destinations',      [DestinationsController::class, 'index_by_server']);
Route::post('/servers/{server_uuid}/destinations',     [DestinationsController::class, 'create']);
```

And add the `use App\Http\Controllers\Api\DestinationsController;` import at the top of the file.

### Migrations

**None required.** Both tables (`standalone_dockers`, `swarm_dockers`) already
have `uuid`, `name`, `network`, `server_id`, timestamps.

### Form Request classes

Coolify's v1 API uses inline `Validator::make(...)` — no Form Request classes.
Do not introduce `app/Http/Requests/...` for this PR; it would be inconsistent
with the surrounding controllers and reviewers will push back.

### Tests — `tests/Feature/Api/DestinationsApiTest.php`

Coolify ships minimal feature tests; the few that exist follow this skeleton
(`tests/Feature/Api/ServersApiTest.php` is the closest precedent — Pest, not
PHPUnit). Suggested cases:

- `it lists destinations scoped to the team`
- `it creates a standalone docker destination on a server`
- `it rejects creation when network name is invalid`
- `it returns 409 when destination has attached resources`
- `it returns 404 for destinations belonging to a different team`

If `ServersApiTest.php` does not exist (Coolify historically light on tests),
match whatever lightweight pattern is in place rather than introducing a new
test framework setup in this PR.

---

## 5. Patching a running Coolify locally

Source root inside the container: `/var/www/html`.

```bash
# 1. Drop the new controller in
docker cp ./DestinationsController.php \
  coolify:/var/www/html/app/Http/Controllers/Api/DestinationsController.php

# 2. Edit routes/api.php in-place (or cp a patched copy)
docker exec -it coolify bash -lc '
  cd /var/www/html
  # add the import + the 5 Route:: lines inside the v1 group
  # use your editor of choice (vim/nano present in image)
'

# 3. Clear route + config caches so Laravel picks up the new endpoints
docker exec coolify php /var/www/html/artisan route:clear
docker exec coolify php /var/www/html/artisan config:clear
docker exec coolify php /var/www/html/artisan cache:clear
docker exec coolify php /var/www/html/artisan optimize

# 4. Verify
docker exec coolify php /var/www/html/artisan route:list | grep destinations
curl -H "Authorization: Bearer $COOLIFY_TOKEN" http://localhost:8000/api/v1/destinations
```

No php-fpm or process restart needed — Laravel rereads the route cache after
`route:clear`. If routes still 404, check that `APP_ENV` is `local` (route
caching disabled) or run `route:cache` after editing.

**Rollback**: `docker restart coolify` does **not** revert file edits (image
layer is the same volume). To roll back, either:

```bash
docker cp coolify:/var/www/html/routes/api.php ./api.php.backup   # before editing
# ...later:
docker cp ./api.php.backup coolify:/var/www/html/routes/api.php
docker exec coolify rm /var/www/html/app/Http/Controllers/Api/DestinationsController.php
docker exec coolify php /var/www/html/artisan route:clear
```

Or pull the original files from the upstream image:

```bash
docker run --rm coollabsio/coolify:<your-tag> cat /var/www/html/routes/api.php > ./api.php.upstream
```

---

## 6. PR submission steps

1. Fork `coollabsio/coolify` to your GitHub account (`pksorensen/coolify`).
2. Branch: `feat/api-destinations`.
3. Commits (one or two is fine — Coolify squashes on merge):
   ```
   feat(api): add /api/v1/destinations endpoints

   Expose StandaloneDocker and SwarmDocker destinations via the public REST API.
   Mirrors the ServersController shape: token-scoped, team-isolated, inline
   validation. Adds list (global + per-server), show, create, delete.

   Needed for IaC / external integrations (e.g. .NET Aspire publishers) that
   currently must screen-scrape the UI to upsert destinations.
   ```
4. PR title: **`feat(api): add destinations endpoints under /api/v1`**
5. PR body template:

   ```markdown
   ## What
   Adds REST API endpoints for destinations (StandaloneDocker + SwarmDocker):
   - `GET    /api/v1/destinations`
   - `GET    /api/v1/destinations/{uuid}`
   - `DELETE /api/v1/destinations/{uuid}`
   - `GET    /api/v1/servers/{server_uuid}/destinations`
   - `POST   /api/v1/servers/{server_uuid}/destinations`

   ## Why
   The Coolify UI exposes destinations under each server, and `destination_uuid`
   is already required when calling `POST /api/v1/applications/{type}`, but there
   is currently no way to list or create destinations via the API. External
   tooling (IaC, custom dashboards, our .NET Aspire publisher) cannot bootstrap a
   server-to-destination wiring without UI scraping.

   Related: #8645 (destination_uuid handling bug in app create) — same surface,
   different angle.

   ## How
   - New `DestinationsController` mirroring the style of `ServersController`
     (inline validator, token-scoped via `ownedByCurrentTeamAPI`, plain JSON
     responses, no API Resources).
   - Five routes added inside the existing `auth:sanctum + ApiAllowed + api.sensitive`
     v1 group.
   - No DB changes.

   ## Checklist
   - [x] Routes guarded by `auth:sanctum` + `ApiAllowed` + `api.sensitive`.
   - [x] Team isolation via `ownedByCurrentTeamAPI`.
   - [x] Validation matches the existing Livewire form
         (`/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/`).
   - [x] Refuses delete when attached resources exist.
   - [ ] Tests (will add if a precedent exists for `tests/Feature/Api/*`).
   ```

6. Related issues to cite:
   - **#8645** (open) — "Wrong destination when creating applications or service
     with API". Different bug, but proves the API surface around destinations
     is under-specified.
   - Coolify docs: <https://coolify.io/docs/knowledge-base/destinations/> —
     reference for terminology.

---

## 7. Effort estimate

| Dimension                | Estimate                                                                     |
| ------------------------ | ---------------------------------------------------------------------------- |
| Lines added              | ~120 (controller ~95, routes 6, imports 1, optional tests 30–50).            |
| Lines modified           | ~2 (routes/api.php import + route block).                                    |
| Local dev time           | 1–2 h to write + smoke-test in the running container.                        |
| Reviewer surface         | One controller + one route file. Low cognitive load.                        |
| Maintainer merge odds    | **Moderate-to-good** for small additive API PRs. Coolify merges API surface PRs regularly (e.g. #8646 destination-uuid fix; PR #9651 tightening team scoping shipped). Risk is style/Pest-test bikeshedding rather than rejection on principle. |
| Conflict risk            | Low. `routes/api.php` and the Api controllers directory churn but additions rarely collide. Watch PR #9651-style refactors that touch team scoping — rebase if one lands during review. |
| Time-to-merge (guess)    | 1–3 weeks if maintainers engage; longer if they ask for OpenAPI annotations (Coolify uses `@OA\` blocks on every existing endpoint — **strongly consider adding them** before opening, it materially speeds merge). |

**Go / no-go**: **Go.** Additive, low-risk, copies a well-established pattern,
and unblocks our publisher even if upstream stalls (we can ship the patched
container in the meantime).
