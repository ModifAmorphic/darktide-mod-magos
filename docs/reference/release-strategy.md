# Modificus Curator release strategy

Reference for how Modificus Curator releases are produced, attested, scanned,
and installed. Covers the release workflow, the post-release AV/VT scan, the
PR gate, and the Linux installer.

## Versioning

Releases are cut by
[release-please](https://github.com/googleapis/release-please)
(`googleapis/release-please-action@v5`), configured as a root, single-component
product:

- `.release-please-config.json` sets `release-type: simple`,
  `include-component-in-tag: false`, and `prerelease: true`. Tags follow the
  `v0.1.0` style (no component prefix), and GitHub releases are marked as
  prereleases (no suffix-style `v0.1.0-rc.1` tags).
- `.release-please-manifest.json` is the source-of-truth version. The first
  release (v0.1.0) has already shipped, so the manifest now reflects the
  current release version. Steady-state is derived from conventional commits.
- No Curator version is paired to a Relay version. Any Curator version is
  expected to work with the latest Relay.

The version is injected at publish time via `-p:Version` and
`-p:InformationalVersion` on `dotnet publish ui` (both set from the
release-please version). `InformationalVersion` is what an About box shows.
`AssemblyVersion` and `FileVersion` are not passed explicitly.

## Release workflow

`.github/workflows/release.yml` is triggered on `push` to `main` and via
`workflow_dispatch`. It chains release-please, the asset build, attestation,
and the AV/VT dispatch in a single workflow. In-workflow chaining is used
because events created with `GITHUB_TOKEN` do not trigger other workflows, so
a `release: published` trigger would never fire.

Repository Actions settings must allow the workflow token to write and to
create pull requests, because release-please creates and updates release PRs
with `GITHUB_TOKEN`. In GitHub, this is **Actions > General > Workflow
permissions > Read and write permissions** plus **Allow GitHub Actions to
create and approve pull requests**. The workflow still declares least-privilege
job permissions, so this setting only makes those declared permissions
available to the token.

Jobs:

1. **release-please** runs on `ubuntu-latest` against
   `.release-please-config.json` + `.release-please-manifest.json`. It cuts
   release PRs and publishes releases.
2. **build-windows** and **build-linux** run in parallel, each gated on
   `releases_created == 'true'` and checked out at the release commit. The
   Windows leg runs on `windows-latest` (native AOT for the `nxm://` handler
   requires a Windows runner); the Linux leg runs on `ubuntu-latest`.

   **Common to both legs:**
   - Sets up .NET 10.
   - Publishes the Curator UI framework-dependent into `stage/app/` with
     `-p:Version` + `-p:InformationalVersion`, targeting `win-x64` or `linux-x64`
     RIDs with `--self-contained false` to filter native libraries to one platform.
     The Windows publish additionally sets `-p:CuratorUseVelopack=true`, which
     adds the Velopack package reference and the `CURATOR_VELOPACK` compilation
     symbol that wires the `VelopackApp.Build().Run()` lifecycle hook in
     `Program.cs`.
   - Publishes the NxmHandler native-AOT into `stage/app/` (`win-x64` on
     Windows, `linux-x64` on Linux).

   **build-windows** then produces a Velopack installer (the Windows asset is
   no longer a portable zip):
   - Extracts the latest stable, non-draft Relay release from
     `ModifAmorphic/darktide-modificus-relay` into `stage/app/relay/` so Relay
     ships app-local inside the payload. (Fetched via
     `gh release list --exclude-drafts --exclude-pre-releases` +
     `gh release download --pattern '*-windows-x64.zip'`. The Relay tag is
     resolved per workflow run; there is no Relay version pinning and no
     Relay provenance sidecar.)
   - Runs `vpk pack` (Velopack 1.2.0) with `--packId ModifAmorphic.ModificusCurator`,
     the release version, `--packDir stage/app`, `--mainExe Modificus.Curator.exe`,
     `--packTitle "Modificus Curator"`, the app icon, and
     `--framework net10.0-x64-runtime` (the installer bootstraps the .NET 10
     runtime if it is missing). Output goes to `stage/releases/`: a `Setup.exe`,
     a `*-full.nupkg`, and a `releases.win.json` (plus a `*-delta.nupkg` on
     non-first releases).
   - Renames `Setup.exe` to `modificus-curator-setup.exe`.
   - Uploads `modificus-curator-setup.exe`, the `*-full.nupkg`, and
     `releases.win.json` to the release with `gh release upload --clobber`
     (plus the delta nupkg when present).
   - Attests `modificus-curator-setup.exe` and the `*-full.nupkg` with
     `actions/attest@v4`.

   **build-linux** keeps the portable-archive flow:
   - Fetches the same Relay Windows zip (Linux runs Relay under Proton, so
     there is no Relay Linux asset) and extracts it into `stage/relay/`.
   - Builds the release archive from `stage/` so it has a top-level `app/` +
     `relay/` layout: `curator-<tag>-linux-x64.tar.gz` (tar).
   - Uploads the archive to the release with `gh release upload --clobber`.
   - Attests the uploaded asset with `actions/attest@v4`.
3. **dispatch-av-vt**, gated on both build legs succeeding, fires a
   `repository_dispatch` event (`event_type: curator-release-assets-published`,
   carrying the tag + asset names) to trigger the post-release AV/VT workflow.
4. **update-manifest**, gated on `releases_created == 'true'` and `build-linux`
   success, rewrites `scripts/release.env` (the install manifest the Linux
   installer consumes) to point at the new Linux tar.gz. It resolves the asset
   from the release by `content_type=="application/x-gtar"` via
   `gh release view`, selects `RELEASE_URL` (stable) or `PRE_RELEASE_URL`
   (prerelease) from the release's `prerelease` flag, and commits the change as
   `chore(release): update install manifest [skip ci]`. The `[skip ci]` in the
   commit message suppresses re-triggering this workflow, and the `chore` type
   keeps release-please from picking it up. Only the one matching var line is
   rewritten; the other (and the comment header) is left untouched.

### Deployment model

Windows and Linux now ship differently.

**Windows** ships as a [Velopack](https://github.com/velopack/velopack) installer
plus auto-update payload. The release produces a one-click `Setup.exe` (renamed
`modificus-curator-setup.exe`): no wizard, installs to
`%LOCALAPPDATA%\ModifAmorphic.ModificusCurator\` (the Velopack pack id is
`ModifAmorphic.ModificusCurator`), creates Start Menu and desktop shortcuts, and
registers in Apps & Features for uninstall. It bootstraps the .NET 10 runtime if
missing (`--framework net10.0-x64-runtime` baked into the pack). The Velopack
lifecycle hook (`VelopackApp.Build().Run()` in `Program.cs`, compiled in under
`CURATOR_VELOPACK`) fires install/update/uninstall hooks and applies any pending
update on startup. The in-app update-check UI (`UpdateManager`) is not
implemented yet, so discovering updates still goes through GitHub releases;
Relay and the app payload are delivered by Velopack once a new release is
installed. Relay ships app-local inside the payload at `current\relay\`.

The Windows user-data root (profiles, mods, config, logs) is deliberately
separate from the install root, at `%LOCALAPPDATA%\ModifAmorphic\Modificus Curator\`
(see `config/AppPaths.cs`). The install root
(`...\ModifAmorphic.ModificusCurator\`) is owned by Velopack and replaced in
place on update, so user data must not live there.

**Linux** is unchanged: a framework-dependent tar.gz. Users install the .NET 10
Runtime themselves; the base `Microsoft.NETCore.App` runtime is sufficient
(Curator uses Avalonia, not WPF/WinForms, so the Windows Desktop Runtime is not
required). Framework-dependent deployment also avoids the single-file
extract-to-temp pattern that attracts dropper heuristics. The tar.gz has a
top-level `app/` + `relay/` layout extracted into `~/.local/share/Modificus Curator/`.

Two executables ship together in `app/`: `Modificus.Curator[.exe]` (the
Avalonia UI, `WinExe`) and `Modificus.Curator.NxmHandler[.exe]` (the
OS-registered `nxm://` handler, native AOT, `TrimMode=full`). The NxmHandler
resolves its sibling Curator exe via `AppContext.BaseDirectory`
(`NxmHandlerRelay.ResolveCuratorMainExe`), so both land in one directory in
every bundle.

`CuratorConfig.RelayDir` defaults to `<app-data>/relay/` (the Windows data root
or `~/.local/share/Modificus Curator/` on Linux; see `config/AppPaths.cs`).
`RelayLaunchService.ResolveLauncherPath` looks there first, then (Windows only)
falls back to the app-local `relay/` shipped inside the Velopack payload. There
is no first-run Relay provisioning logic; a missing launcher maps to
`LaunchStatus.Error`. Manual `RelayDir` overrides via JSON config are the
user's responsibility.

`nxm://` registration is done at runtime by Curator, not by the Windows
installer or the Linux install script. `WindowsNxmHandlerRegistrar` writes
`HKCU\Software\Classes\nxm` (per-user, no elevation); `LinuxNxmHandlerRegistrar`
writes `~/.local/share/applications/modificus-curator-nxm-handler.desktop` + a
best-effort `xdg-mime default`. Both encode an absolute path to the handler
exe. On Windows the Velopack `current\` directory is replaced in place on
update, so the registered path stays stable.

## Supply-chain integrity (artifact attestations)

Each release asset is attested with `actions/attest@v4` in the release
workflow immediately after `gh release upload`. The attestation records which
repo, workflow, commit, and runner produced the asset. Attestation lives in
the release workflow (the workflow that built the artifact) because the
provenance value comes from "this artifact was produced by this workflow run
on this commit"; moving attestation to a separate workflow that just
downloads the artifact would attest to nothing useful.

Anyone can verify an asset:

```
gh attestation verify <file> --repo ModifAmorphic/darktide-modificus-curator
```

What attestation does not do: bypass SmartScreen or Defender, help
non-technical end users (who do not run `gh attestation verify`), or replace
code signing for runtime trust.

## Post-release AV/VT scan

`.github/workflows/curator-post-release-av.yml` is triggered by the
`repository_dispatch` event `curator-release-assets-published` from the
release workflow, and also runs on manual `workflow_dispatch` (which takes
`tag_name` + `windows_asset` inputs). It runs as a single job on
`windows-latest`.

The job downloads the published Windows asset (`modificus-curator-setup.exe`,
the Velopack installer) and scans those exact bytes (not a fresh build), so it
scans what users receive:

- **Microsoft Defender**: runs `Start-MpScan -ScanType CustomScan -ScanPath
  <full asset path>` against the downloaded installer. The scan is performed
  using PowerShell cmdlets designed for programmatic use, which work on the
  Windows Server runner. The result is explicitly classified as `clean`,
  `detection`, or `tool_error`. A `tool_error` indicates the scan command
  failed or Defender is unavailable. The runner is Windows Server with Defender
  sometimes cloud-delivery-off, so this is a coarse signal, not a guarantee of
  what a consumer Windows 11 box sees.
- **VirusTotal**: required, gated on the `VIRUSTOTAL_API_KEY` repo secret.
  The workflow fails if the secret is not configured. Submits the installer via the
  pinned Marketplace action `crazy-max/ghaction-virustotal@936d8c5c00afe97d3d9a1af26d017cfdf26800a2`
  with `request_rate: 4` to respect the VirusTotal public API quota (4 requests/minute).
  The action handles large uploads automatically by using the `/files/upload_url`
  endpoint for files 32MB or larger. It returns analysis links in the `analysis` output.
  The workflow does not poll the VirusTotal API for final results or verdicts.
- **Issue creation**: opens a GitHub issue with the title "AV manual review
  for release <tag>" when VirusTotal upload succeeds and returns analysis links.
  The issue is created regardless of VirusTotal detection results, since CI does not
  poll the API to determine them. The issue body carries the release tag, the asset
  name, the Defender output, the VirusTotal analysis links, and a note that the
  operator should open the links and review the results manually. The issue deduplicates
  against an existing open issue for the same title. No issue is created if VirusTotal
  upload fails or returns no analysis links.

The workflow fails (red) when:
- Defender status is `tool_error` (scan command failed or Defender unavailable)
- Defender status is `detection` (threat detected)
- Defender status is empty (scan did not run)
- `VIRUSTOTAL_API_KEY` is not configured
- VirusTotal action upload fails
- VirusTotal analysis output is missing
- A required GitHub issue for manual review could not be created, and no
  existing open issue matched

The workflow passes (green) when both scanners run successfully with no Defender
detections and VirusTotal returns analysis links. Note that the workflow does not
fail on VirusTotal detections, since CI does not poll the API to know them. The
workflow is still post-release and non-gating for publication, but red means the
scan signal is invalid or VirusTotal upload failed.

### Why AV scanning exists

Curator's behavior profile reads as malware-shaped regardless of intent:
launching a process that injects a DLL (Relay), enumerating processes
(SingleInstanceGuard), writing URL-handler registry keys, running a named-pipe
server, downloading and extracting archives that contain executables (Nexus
acquisition, DMF install), and creating staging links
(`ProfileService.PrepareModRoot`: NTFS junctions on Windows, symlinks on Linux).
Each is legitimate for a mod manager and each matches a malware heuristic.
False positives are expected on early releases.

What is automatable in CI is the Defender + VirusTotal scan above. What is
not automatable:

- **Defender whitelist submission.** Microsoft has no public API for
  "submit my app for trust". The path is the manual Security Intelligence
  submission portal (https://www.microsoft.com/en-us/wdsi/filesubmission) per
  file (or per certificate, when cert-based reputation is established), with
  a short written justification.
- **SmartScreen reputation** is built automatically by Microsoft as real
  downloads accumulate against a signed binary; there is no submission form
  that accelerates it for non-EV certs.

The only thing close to "automated Defender trust" is EV code signing plus
accumulated download reputation. Everything else is either a CI signal
(Defender PowerShell scan, VirusTotal) or a manual portal submission.

## PR gate

`.github/workflows/curator-build.yml` runs on `pull_request` targeting `main`
(and `workflow_dispatch`). There is intentionally no `push` trigger; the release
workflow handles push-to-main.

- **Format job** (Ubuntu only): same-repo PRs run `dotnet format` and
  auto-commit any changes as `style: dotnet format [skip ci]`. Fork PRs and
  `workflow_dispatch` invocations run `dotnet format --verify-no-changes` (no commit).
- **Build + test matrix** (Windows + Ubuntu), depends on the format job.
- **paths-ignore** skips release-please's bot-authored release PRs (they only
  touch `CHANGELOG.md` and `.release-please-manifest.json`), so those do not
  run the build gate.
- No `actions/upload-artifact` step. Release assets are produced by the
  release workflow.

## Linux installer

`scripts/install.sh` is served from `raw/main`. By default it installs the
latest STABLE release; pass `--prerelease` (or set `CURATOR_PRERELEASE=1`) to
install the latest prerelease:

```sh
# stable (default)
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh
# prerelease
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh -s -- --prerelease
```

The script does NOT query the GitHub API or infer the asset filename. It
resolves the archive from `scripts/release.env`, a small `KEY=value` manifest
the release pipeline maintains on every release (see the `update-manifest` job
above). It fetches the manifest from
`https://raw.githubusercontent.com/<repo>/main/scripts/release.env` (CDN, no
auth, no rate limit), parses `RELEASE_URL` and `PRE_RELEASE_URL` line by line
(not sourced, for safety even though the file is ours), and downloads the URL
matching the selected channel. If the chosen URL is empty (for example,
`RELEASE_URL` is empty until the first stable release ships), the script exits
with a message pointing the user at `--prerelease`.

After downloading, the script extracts to a temp dir, validates
`app/Modificus.Curator` + `relay/modificus_relay.exe` before touching the
install root, then:

- Installs into `${XDG_DATA_HOME:-$HOME/.local/share}/Modificus Curator/`
  (the space is intentional; it matches `AppPaths.AppDataDir`).
- Replaces only `app/` and `relay/` under that root, never the root itself
  (it also holds `profiles/`, `mods/`, `logs/`, `config.json`).
- Marks `app/Modificus.Curator` and `app/Modificus.Curator.NxmHandler`
  executable.
- Symlinks `Modificus.Curator` into `~/.local/bin/modificus-curator` (creating
  `~/.local/bin` if needed, no sudo); on failure, warns and prints the
  executable path.
- Warns (non-fatal) if `dotnet --list-runtimes` does not list
  `Microsoft.NETCore.App 10.`.

Curator's own first run handles `.desktop` registration for the `nxm://`
handler; the script does not duplicate that.

The script supports testing overrides (`INSTALL_ROOT`, `BIN_LINK`,
`CURATOR_REPO`, `CURATOR_ARCHIVE`, `CURATOR_PRERELEASE`) so extraction +
install can be exercised against a fake archive in a temp dir without touching
real user data or hitting the network. The script in `main` is authoritative;
users always get the current version, installing the latest stable by default
or the latest prerelease on request. Users wanting a specific release manage
the install themselves.

## Sandbox rehearsal (operator procedure)

The release pipeline can be rehearsed end to end against a throwaway branch so
no real release or tag lands on `main` until the pipeline is verified. The
rehearsal is temporary and leaves `main` untouched:

1. Create a throwaway target branch (for example `release-sandbox`) from the
   current `main`.
2. Temporarily point release-please at the sandbox branch by setting
   `target-branch` in `.release-please-config.json`, and point the release
   workflow's `on.push.branches` (and `workflow_dispatch`) at the same target
   branch.
3. For a fresh repo (not applicable to Curator, which has already shipped
   v0.1.0), use a one-shot config-level `release-as` override in
   `.release-please-config.json` to force the first release version, then remove
   it immediately after shipping. Curator has already passed this bootstrap
   phase.
4. Push the branch and land a conventional commit so release-please opens a
   release PR against the sandbox branch.
5. Merge the release PR on the sandbox branch and let the release workflow
   run end to end: release-please cuts the release, the Windows + Linux
   archives are built and attested, and the AV/VT dispatch fires.
6. Inspect the produced release, archives, attestations, and AV/VT result.
7. Delete the sandbox release and its tag.
8. Strip the sandbox settings (revert `target-branch` and the workflow
   trigger-branch changes) before opening the PR to `main`, so the production
   config stays normal and persistent.

To test `curator-post-release-av.yml` after it exists on the default branch:
- Run it via `workflow_dispatch` against an existing release, providing the
   `tag_name` and `windows_asset` inputs manually.

This is an operator rehearsal procedure, not part of the user-facing release
path.

## Scope

What the release pipeline provides today, and what it does not:

- **Code signing.** Releases are unsigned, so Windows SmartScreen warns on the
  installer's first run. SmartScreen reputation is not established.
- **In-app update-check UI.** Velopack's lifecycle hook is wired (pending
  updates auto-apply on the next startup), but the `UpdateManager`-driven in-app
  update check is not implemented yet; discovering updates still goes through
  GitHub releases. Relay is bundled as latest-per-Curator-release and is not
  updated independently in-app.
- **Linux arm64 / Steam Deck builds.** Only `win-x64` and `linux-x64` are
  published.
- **Relay pinning or Relay provenance sidecar.** The bundled Relay is whatever
  was latest stable (non-draft, non-prerelease) at workflow-run time.

## Sources

- Workflows: `.github/workflows/release.yml` (release-please + asset build +
  attestation + dispatch), `.github/workflows/curator-post-release-av.yml`
  (repository_dispatch AV/VT scan), `.github/workflows/curator-build.yml`
  (PR gate).
- `scripts/install.sh` (the Linux installer).
- `scripts/release.env` (the install manifest the installer reads and the
  `update-manifest` job maintains).
- Release-please config: `.release-please-config.json` +
  `.release-please-manifest.json`.
- GitHub Artifact Attestations (`actions/attest@v4`):
  https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations.
- Architectural details from the codebase: `src/ui/Program.cs`
  (the Velopack `VelopackApp.Build().Run()` lifecycle hook, compiled under
  `CURATOR_VELOPACK`), `src/nxm/NxmHandlerRelay.cs`
  (sibling-exe resolution), `src/nxm/{Windows,Linux}NxmHandlerRegistrar.cs`
  (runtime scheme registration), `src/relay-client/RelayLaunchService.cs`
  (launch-time Relay resolution: configured `RelayDir` first, then the Windows
  app-local `relay/` fallback; no first-run provisioning),
  `src/config/AppPaths.cs` (the app-data root and default `RelayDir`).
