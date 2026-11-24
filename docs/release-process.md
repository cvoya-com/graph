---
---

# Release Process

CVOYA graph releases are tag-triggered and fully automated by
[`.github/workflows/release.yml`](https://github.com/cvoya-com/graph/blob/main/.github/workflows/release.yml). There is
no manual publish step and no CI stamping of version numbers: releases are
**tag-authoritative** — the tag *is* the version. `release.yml` only ever reads
it, and rejects any tag that doesn't match the version scheme below.

`./scripts/release.sh` is the operator entry point and the only supported way to
cut a tag. It computes the version, runs the pre-flight safety checks, pushes the
tag, watches the workflow to completion, and verifies the published packages are
actually resolvable on nuget.org.

## Version scheme

```text
MAJOR.MINOR.PATCH[-(alpha|beta|rc).YYYYMMDD[.N]]
```

Stable releases are plain `MAJOR.MINOR.PATCH`. Pre-releases are date-anchored, and
a second pre-release cut on the same day gets a `.1`, `.2`, … counter — so
`1.0.0-alpha.20260716` is followed by `1.0.0-alpha.20260716.1`. These order
correctly under SemVer pre-release precedence. This matches the scheme Spring
Voyage uses.

## Cutting a release

1. **Preview the plan.** `--plan` computes the tag and prints what would happen
   without pushing anything.

   ```bash
   ./scripts/release.sh 1.2.3 --pre alpha --plan
   ```

2. **Cut it.** One command does the rest — the base version is positional, and
   pre-release suffixes come from `--pre`, never typed by hand.

   ```bash
   ./scripts/release.sh 1.2.3               # stable  -> v1.2.3
   ./scripts/release.sh 1.2.3 --pre alpha   # alpha   -> v1.2.3-alpha.20260716
   ./scripts/release.sh 1.2.3 --pre rc      # rc      -> v1.2.3-rc.20260716
   ```

   Before pushing anything, the script verifies its tools and GitHub
   authentication, checks that both fetch and push URLs for `origin` resolve to
   `cvoya-com/graph`, requires a clean checkout exactly at the current
   `origin/main` commit, and performs a dry-run push. Existing tags are immutable:
   rerun their workflow after a transient failure, or choose a new version.

3. **Pushing the tag triggers the release workflow**, which:
   - Reads the version off the tag and verifies it matches the scheme above
     (the workflow never writes a version anywhere — that is what prevents the
     double-date-suffix class of bugs).
   - Runs the full release-relevant test suite, including the Neo4j and Apache
     AGE provider tests against service containers (the same pattern as
     `ci.yml`).
   - Packs the provider and shared packages, including `Cvoya.Graph.Neo4j`,
     `Cvoya.Graph.Age`, `Cvoya.Graph.InMemory`, `Cvoya.Graph`,
     `Cvoya.Graph.Cypher`, serialization, analyzer, code-generation, and
     compatibility-test packages.
   - Verifies the exact package inventory and inspects every packaged
     `Cvoya.Graph*.dll`: the NuGet manifest, filename, `AssemblyVersion`,
     `FileVersion`, and `InformationalVersion` must all match the tag-derived
     version before anything can publish.
   - Verifies a clean example consumer can restore and build against the exact
     package set assembled for publication.
   - Generates a build provenance attestation for every package, symbol
     package, and the source archive (`actions/attest-build-provenance`),
     verifiable with `gh attestation verify <file> --repo cvoya-com/graph`.
   - Publishes to nuget.org using **Trusted Publishing** (OIDC) — no API key
     is stored anywhere, long-lived or otherwise.
   - Creates a fixed-name `cvoya-graph-source.zip` archive from the tagged
     commit and attaches it to the GitHub Release alongside the NuGet packages.
   - Creates a CVOYA-branded GitHub Release for the tag. When
     `developer/release-notes/<TAG>.md` exists, that reviewed version-specific
     introduction is prepended to GitHub's auto-generated notes; otherwise the
     workflow uses the generic product, publisher, download, and package-ID
     migration introduction.
   - Builds the complete documentation site from that same tagged commit, records
     the release tag, tag-derived version, and commit in `/release.json`, and
     deploys the resulting immutable artifact only after package publication and
     GitHub Release creation succeed.

4. **`release.sh` then verifies the release is real.** A green workflow only
   means `dotnet nuget push` was accepted — nuget.org indexes asynchronously, and
   a package can be accepted but held by validation. The script reads the `.nupkg`
   asset names off the GitHub Release and polls nuget.org until every one of those
   package IDs resolves anonymously at that exact version. Reading the IDs off the
   release rather than hardcoding them means this can never drift from what
   `release.yml` packs.

### Dry runs

Running `release.yml` manually via `workflow_dispatch` (Actions tab → Release
→ Run workflow, on any branch) is always a dry run: it executes the full
build/test/pack/attest pipeline but always skips the NuGet publish and GitHub
Release steps. Use it to validate the pipeline end-to-end — including on a
throwaway branch — without touching nuget.org or creating a release. Real
publishing only ever happens from an actual `v*` tag push.

With no tag to read, a dispatched run versions itself from the `VERSION` file's
development default.

### Documentation publication

Pull requests and main-branch pushes run `.github/workflows/docs.yml` with
`contents: read` permission. That workflow builds both the Jekyll conceptual
site and DocFX API reference, assembles the production-shaped site, and checks
internal links in the Jekyll-owned tree while DocFX treats API-reference warnings
as errors. It has no `pages: write` or `id-token: write` permission and contains
no deployment action, so ordinary documentation validation cannot publish.

A tag-triggered `release.yml` run calls that same workflow after the package
artifact job succeeds. It checks out the exact `github.sha` used by the package
jobs, verifies the checkout, adds deterministic `/release.json` provenance, and
uploads a uniquely named Pages artifact. The final `deploy-docs` job waits for
both that artifact and the successful GitHub Release job. It is the only job
with `pages: write` and `id-token: write`, and the only job that targets the
`github-pages` environment. Failed or cancelled package publication and manual
dry runs therefore cannot update the site.

Repository administrators must configure Pages to use **GitHub Actions** as its
source and protect the `github-pages` environment so only tags matching `v*`
may deploy. `@savasp`, the repository and workflow owner recorded in
`.github/CODEOWNERS`, owns that environment policy and release recovery.
Before creating or pushing the immutable release tag, `release.sh` reads both
settings and requires that exact configuration. Its authenticated GitHub account
therefore needs repository administration read access in addition to the
repository and workflow access used for the rest of the release.

To recover from a transient documentation build or Pages failure, open the
existing tag-triggered Release run and use **Re-run failed jobs**. Do not move or
recreate the tag and do not start `release.yml` with **Run workflow**, because
manual dispatch is intentionally only a package-pipeline dry run. A failed
documentation build can be retried without republishing packages, and a failed
deployment reuses the already-uploaded immutable artifact. Re-running the full
tag run is also safe: the artifact name is unique per run attempt, while Jekyll's
generated time and every file timestamp are pinned to the tagged commit, making
the site output reproducible. If a newer release has already deployed, recover
by cutting a new release rather than republishing an older site's artifact.

### Promoting a prerelease to Latest

`release.yml` marks any semver prerelease as a GitHub prerelease, which keeps it
off `releases/latest`. During a prerelease line there is still a "current" build
that public download links should resolve to, so pass `--latest` to promote it:

```bash
./scripts/release.sh 1.2.3 --pre alpha --latest
```

Once the workflow succeeds and the packages verify, the script clears the
prerelease flag on the GitHub Release record and marks it **Latest**. The tag and
the NuGet package version remain semantic prereleases — only the GitHub Release
badge moves. `--latest` is redundant for a stable release, which is always Latest.

Afterwards, confirm that
`https://github.com/cvoya-com/graph/releases/latest/download/cvoya-graph-source.zip`
downloads the tagged archive before updating public links.

## NuGet Trusted Publishing — one-time portal setup

Trusted Publishing exchanges a short-lived GitHub OIDC token for a temporary
(~1 hour) nuget.org API key at publish time, so no NuGet API key is ever
stored as a GitHub secret. This must be configured once on nuget.org by an
owner of the `Cvoya.Graph*` package IDs (or, before the first publish,
the account/org that will own them):

1. Sign in to [nuget.org](https://www.nuget.org), open your username menu, and
   choose **Trusted Publishing**.
2. Add a new trusted publishing policy with:
   - **Repository Owner:** `cvoya-com`
   - **Repository:** `graph`
   - **Workflow File:** `release.yml` (the file name only, not the
     `.github/workflows/` path)
   - **Environment:** leave empty (the workflow does not use a GitHub
     Environment)
3. Repeat step 2 for each package owner scope if the package IDs aren't all
   covered by a single policy (a policy applies to all packages owned by
   the selected owner).
4. The policy is valid for 7 days pending its first successful publish (this
   is a GitHub anti-resurrection safeguard for the repository identity), after
   which it's tied to the repository permanently.
5. Set the `NUGET_TRUSTED_PUBLISHING_USER` **repository variable** (Settings →
   Secrets and variables → Actions → Variables — not a secret, since it's just
   a username) to the nuget.org account or organization name that owns the
   policy. `release.yml` reads it as the `user` input to `NuGet/login@v1`.

No further workflow changes are needed — `release.yml` already requests
`id-token: write` and uses `NuGet/login@v1` to exchange the OIDC token for the
push.

## Design decisions

- **The tag is authoritative; nothing stamps a version in CI.** An old,
  disabled `release.yml` produced the mangled
  `1.0.0-alpha.20251014.0.20251014.0` version because a workflow step appended a
  date suffix to an already-suffixed `VERSION`. No step writes a version now:
  `release.sh` computes it once, the tag carries it, and `release.yml` only reads
  and validates it.
- **`VERSION` is a development default, not the release source of truth.**
  Untagged local and CI builds version themselves from it. `release.yml` passes
  the tag's version to the pack job as `GRAPH_RELEASE_VERSION`, which
  `Directory.Build.props` prefers over the file. Editing `VERSION` does not cut a
  release, and cutting a release does not edit `VERSION`.
- **`AssemblyVersion`/`FileVersion` are derived, not tracked.** They must be four
  numeric parts and cannot carry a semver prerelease suffix, so
  `Directory.Build.props` strips the suffix and appends `.0`
  (`1.2.3-rc.20260716` → `1.2.3.0`). This replaced a checked-in `VERSION.ASSEMBLY`
  file holding a `Major.Minor.YYDDD.HHMM` stamp, which embedded the build *minute*
  — so rebuilding one tag produced a different `FileVersion`. The full version,
  prerelease suffix and commit sha included, still ships via
  `InformationalVersion` (e.g. `1.2.3-rc.20260716+cba4d4f…`), which is what to
  inspect for provenance.
- **Release builds and packing are separate.** `dotnet pack -c Release` follows
  the standard SDK contract and builds before packing; only an explicit
  `--no-build` reuses earlier output. The release workflow deliberately builds
  once and then packs with `--no-build`, while the package verifier checks the
  DLL metadata so stale or mismatched output cannot publish. The release build also restores
  and compiles the package consumer for .NET 10 and .NET 11.
- **No `CHANGELOG.md`.** A reviewed version-specific introduction under
  `developer/release-notes/` is used when present, with a short CVOYA-branded
  fallback for other tags. The introduction is prepended to GitHub's
  auto-generated notes (derived from merged PR titles since the previous tag).
- **No NuGet API key anywhere**, cleartext or secret-stored — Trusted
  Publishing is the only supported publish mechanism.
