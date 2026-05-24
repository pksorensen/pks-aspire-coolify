# product-cli — operating notes for pks-aspire-coolify

These notes summarise the reference implementation in `.product-cli-reference/`
(read-only clone of https://github.com/Hafeok/product-cli). They cover how to
install and run `product`, the seven curated authoring prompts that encode the
methodology, the two distinct write paths (authored vs. raw request), and the
hard gates that make the typed graph load-bearing.

## Install

Prebuilt binary (preferred — no Rust toolchain needed):

```bash
curl --proto '=https' --tlsv1.2 -LsSf \
  https://github.com/Hafeok/product-cli/releases/latest/download/product-installer.sh | sh
# drops `product` into ~/.cargo/bin — add to PATH if missing
product --version
```

From source (needs Rust): `cargo install --git https://github.com/Hafeok/product-cli`.
Via MCP registry: `claude mcp install io.github.Hafeok/product-cli` — this also
writes a `.mcp.json` so Claude Code can drive the graph through the MCP server.

Run inside the project (after `product init`):

- `product author feature` / `product author adr` / `product author review` —
  guided authoring (spawns Claude with one of the seven prompts pre-loaded and
  the MCP server attached).
- `product implement FT-XXX` — assemble bundle, spawn agent, verify.
- `product verify FT-XXX` — run TC runners, update status.
- `product status` / `product feature next` / `product context FT-XXX --depth 2`
  — read-only navigation.
- `product gap check` / `product drift check` / `product graph check` /
  `product adr conflict-check` — health gates.
- `product request apply <file.yaml>` — atomic raw write (bypasses methodology).
- `product mcp` — stdio MCP server (used by editors / Claude Code).

## The seven curated prompts

All seven live at `.product-cli-reference/src/author/prompts/*.txt`. They are
the actual product — the CLI is mostly a shell that loads the right prompt and
the right MCP toolset for each authoring mode.

### 1. `author_feature.txt` — guided feature authoring

Pre-flight (mandatory before writing): `product_feature_list` to see what
exists, `product_graph_central` for the top-5 foundational ADRs,
`product_context` on the most-related existing feature, then clarifying
questions. Scaffolding only happens after those tool calls. **Close gate:**
must call `product_graph_check` and then `product_preflight` for every touched
FT-XXX; `warnings` is not advisory — you must either link the missing ADR/TC
or set `domains-acknowledged.<domain>` with a written reason. The host process
re-runs preflight on session exit and refuses to auto-commit if any feature is
not clean.

### 2. `author_adr.txt` — guided decision authoring

Pre-flight: `product_graph_central`, `product_adr_list`, `product_impact` on
the area being decided, then contradiction scan against linked ADRs. Every ADR
must contain the five sections **Context, Decision, Rationale, Rejected
alternatives, Test coverage** — the prompt refuses to close without all five.

### 3. `author_review.txt` — spec gardening

Adds nothing new. Steps: `product_graph_check` first (fix structural issues),
then walk features by lowest test coverage and propose TCs, find orphaned ADRs
and propose feature links, find features with no exit-criteria TC. Will not
create new features or ADRs except to fill a specifically identified gap.

### 4. `conflict_check.txt` — ADR-vs-ADR review

Run during `product adr conflict-check` against the corpus of accepted ADRs
(every cross-cutting ADR, every same-domain ADR, top-5 by betweenness
centrality). Checks **C001** direct contradiction, **C002** scope overlap,
**C003** missed supersession marker, **C004** rationale conflict on a shared
constraint. Returns JSON-only findings.

### 5. `gap_analysis.txt` — ADR specification review

Run during `product gap check`. Depth-2 bundle around each ADR (linked
features, their TCs, neighbouring ADRs). Checks **G001** testable claim with
no TC, **G002** invariant block with no scenario/chaos TC, **G003** missing
rejected-alternatives section, **G004** uncaptured external constraint,
**G005** logical inconsistency with a linked ADR, **G006** feature aspect not
addressed by any linked ADR, **G007** rationale references a superseded
decision, **G008** dependency used with no governing ADR. JSON-only output.

### 6. `drift_analysis.txt` — spec-vs-implementation review

Run during `product drift check` using the completion anchor (tag + timestamp),
the git diff since that tag, and the depth-2 ADR bundle. Checks **D001** ADR
mandates X but no code implements it, **D002** code overrides the ADR,
**D003** partial implementation, **D004** undocumented behaviour (code does X
with no governing ADR). JSON-only output.

### 7. `implement.txt` — implementation session

System prompt for `product implement FT-XXX`. The pipeline appends a dynamic
suffix per invocation: feature header, current TC status table, the hard
constraints (including the exact `product verify FT-XXX` to run on
completion), and the full context bundle. The prompt is the smallest of the
seven on purpose — the bundle does the work.

## `product author <kind>` vs. `product request apply`

These are the **two** ways content reaches the typed files, and they exist for
different audiences.

`product author <feature|adr|review>` is the **methodology path**: it spawns
an agent with one of the curated prompts above, attaches the MCP server so the
agent can read the full graph, forces the discovery + clarifying-question step,
and on close runs `product_graph_check` + `product_preflight` as a hard gate.
The session host refuses to auto-commit unclean features. This is how new
features and decisions are supposed to enter the graph.

`product request apply <file.yaml>` is the **typed-write path**: it takes a
pre-formed YAML request (create / link / status-change of features, ADRs, TCs
in one atomic batch) and applies it to disk. No discovery, no preflight, no
agent — just validation against the schema and an atomic batched write. Use it
for scripted edits, bulk imports, or when an authoring agent has already
agreed on the shape and is committing the result. It is the right primitive
underneath `author`, not a replacement for it.

Rule of thumb: humans and ideating agents use `author`; tools and finalised
plans use `request apply`.

## The hard gates

These are the four checks the methodology will not let you skip:

- **`product_preflight` on feature close** — invoked by `author_feature.txt`
  before the session ends, and re-invoked by the host on session exit. Blocks
  on missing ADR/TC links unless the gap is explicitly acknowledged in
  front-matter (`domains-acknowledged.<domain>: <reason>`).
- **`product graph check`** — structural health of the typed graph: no broken
  refs, no dangling links, supersession cycles, orphaned domains. First step
  of `author_review` and a close-gate of `author_feature`.
- **`product gap check`** — runs the gap-analysis prompt against ADRs and
  reports G001–G008 findings as JSON. Used both interactively and in
  `product implement`'s Step-0 preflight.
- **`product drift check`** — runs the drift-analysis prompt against the
  current diff since the completion anchor and reports D001–D004.
- **`product adr conflict-check`** — runs the conflict-check prompt against
  the accepted-ADR corpus before a new ADR is sealed; reports C001–C004.

`product implement FT-XXX` chains these: Step 0 hard-blocks on the same
preflight gaps the authoring session would have flagged, so a feature that
slipped through with warnings cannot be implemented until the gaps are filled
or acknowledged. The graph is therefore load-bearing — every command honours
the same constraints, and the typed YAML front-matter is the single source of
truth (per ADR-002 of the reference).
