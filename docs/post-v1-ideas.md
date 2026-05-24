# Post-v1 ideas

Ideas captured during v1 spec authoring that are **explicitly out of v1
scope** but worth re-evaluating when v2 work starts. Each entry is sized
roughly as a future feature; the corresponding `product author feature`
session would do real discovery, name the FT-XXX, and produce the typed
spec.

When v2 starts, drop these into the brief's "sketched capability areas"
section (or into a v2 brief) and run `product author feature` on each.

---

## Idea — Project-scoped private registry (using `pks-agent-registry`)

**Source:** captured 2026-05-24 during v1 authoring.

Today v1 ADR-005 punts on the registry: the developer brings their own
(ghcr.io, lan registry, etc.), the publisher just pushes there, and a
Coolify Private Registry record is upserted by `(host, username)`. The
developer is on the hook for owning the registry.

**The idea:** treat `pks-agent-registry` (Docker registry sub-project) as
a deployable that the publisher provisions into the Coolify project on the
developer's behalf — mirroring how Azure container deployments stand up an
ACR alongside the workload.

Concretely, a future `WithProjectRegistry()` opt-in would:
- Deploy a `pks-agent-registry` container as a service inside the same
  Coolify project (alongside the workload, alongside the managed
  dashboard if opted in).
- Register it as a Coolify Private Registry pointing at its own
  in-project hostname (so Coolify pulls from the project-internal
  registry).
- Have the publisher push every workload image there instead of needing
  an external registry to be configured.
- Idempotent / lazy-upsert per ADR-001 (same name-keyed discipline as the
  rest of the deploy walk).

**Composes with what already exists in v1:**
- ADR-005's "developer-chosen, publisher-push" stays the default; this is
  a new opt-in path that lets the publisher *be* the chooser.
- The Private Registry upsert FT-004 already does — same code path, just
  pointed at the in-project hostname.
- The managed dashboard pattern from FT-010 (deploy-time upsert of an
  auxiliary service into the same project) is the exact shape this would
  follow.

**Open questions for the future author session:**
- Image persistence: does the in-project registry need a persistent
  volume? (Yes for any non-toy use, but Coolify volume management is its
  own surface.) Garbage collection?
- Network: registry is reachable only inside the destination's network
  by default, or also exposed externally? FQDN?
- Auth: registry credentials need to flow into both the publisher (for
  push) and Coolify (for pull) — at minimum an auto-generated
  per-deploy token, persisted somehow so re-deploys reuse it.
- Bootstrapping: the very first deploy has nowhere to pull the registry
  image from itself (chicken-and-egg). Pull from a public registry on
  first deploy, then self-hosted thereafter?

**Likely FT shape:** new feature `FT-0XX — Project-scoped private
registry`, new domain `project-registry`, depends on FT-005 (deploy
walk) and FT-004 (registry-upsert code path).

---

## Idea — Source-build path (Coolify builds the image from a git repo)

**Source:** captured 2026-05-24 during v1 authoring as the alternative to
the in-project registry above.

v1 is **push-based**: the publisher builds + pushes images locally, then
tells Coolify to pull and run. This is the right default for the
local-developer story (no source code needs to be checked in, no git
remote is required, you can deploy from a dirty working tree).

**The alternative:** lean into Coolify's native ability to build from a
git repo. Instead of building locally and pushing an image, the publisher
would tell Coolify "here's the git URL, branch, and Dockerfile path for
this service; you build and deploy it." This is more CI/CD-shaped:
**requires** the code to be checked in and reachable from Coolify.

**Trade-offs vs v1's push-based default:**
- ✅ No registry needed — Coolify owns the build + pull lifecycle.
- ✅ Build runs on Coolify's host, not the developer's laptop — faster
  for slow-laptop / big-image scenarios.
- ✅ Deploys are reproducible from `git sha` rather than from "whatever
  was on my laptop at 14:32."
- ❌ Forces a git-pushed workflow; you can't deploy from a dirty tree.
- ❌ Coolify needs network access to the git remote (private repo →
  needs deploy keys configured in Coolify).
- ❌ Build failures surface in Coolify, not in the local `aspire deploy`
  output — diagnostics story is messier.
- ❌ Forces a Dockerfile per service in source (today Aspire's container
  build can synthesise one from a project; the source-build path can't
  use that).

**Likely FT shape:** new feature `FT-0YY — Source-build deploy path`
as an opt-in **alternative** to FT-003/FT-004 (build + push). When
opted in, FT-003 and FT-004 are skipped for the affected resources;
FT-005's service upsert provides Coolify with `(git_url, ref,
dockerfile_path)` instead of an `image` tag. New domain
`source-build`. ADR likely needed to record the trade-off
(push-default-with-source-opt-in vs the inverse) and to clarify per-AppHost
vs per-resource granularity.

---

## How to promote one of these to v2

1. Open or create the v2 brief; copy the idea into the "sketched
   capability areas" section, expanding the open questions.
2. Run `product author feature` — the session will read the brief, do
   discovery against the current graph, ask clarifying questions, and
   produce a real FT-XXX with full preflight.
3. If the idea requires settling a cross-cutting trade-off (e.g.
   push-default vs source-default), run `product author adr` first to
   record the decision before authoring the feature.
