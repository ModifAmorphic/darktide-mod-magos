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
- `.release-please-manifest.json` is the source-of-truth version. It ships as
  bootstrap `{ ".": "0.0.0" }` and stays there until the first release lands;
  there is no manifest seeding (for example `0.0.1`) and no persistent
  `release-as` in the config.
- The first release is cut at `0.1.0` via a one-time `Release-As: 0.1.0`
  directive in the commit body (or PR description footer) of the commit that
  lands on `main`. This is release-please's supported mechanism for overriding
  the next version a single time; afterwards release-please derives versions
  from conventional commits as normal.
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
   requires a Windows runner); the Linux leg runs on `ubuntu-latest`. Each leg:
   - Sets up .NET 10.
   - Publishes the Curator UI framework-dependent into `stage/app/` with
     `-p:Version` + `-p:InformationalVersion`.
   - Publishes the NxmHandler native-AOT into `stage/app/` (`win-x64` on
     Windows, `linux-x64` on Linux).
   - Fetches the latest non-draft Relay release (prereleases included) from
     `ModifAmorphic/darktide-modificus-relay` via `gh release list
     --exclude-drafts` + `gh release download --pattern '*-windows-x64.zip'`,
     and extracts it into `stage/relay/`. Both legs bundle the same Relay
     Windows zip; Linux runs Relay under Proton, so there is no Relay Linux
     asset. The Relay tag is resolved per workflow run; there is no Relay
     version pinning and no Relay provenance sidecar.
   - Builds the release archive from `stage/` so it has a top-level `app/` +
     `relay/` layout: `<tag>-windows-x64.zip` (7z) on Windows,
     `<tag>-linux-x64.tar.gz` (tar) on Linux.
   - Uploads the archive to the release with `gh release upload --clobber`.
   - Attests the uploaded asset with `actions/attest@v4`.
3. **dispatch-av-vt**, gated on both build legs succeeding, fires a
   `repository_dispatch` event (`event_type: curator-release-assets-published`,
   carrying the tag + asset names) to trigger the post-release AV/VT workflow.

### Deployment model

Releases are framework-dependent. Users install the .NET 10 Runtime
themselves; the base `Microsoft.NETCore.App` runtime is sufficient (Curator
uses Avalonia, not WPF/WinForms, so the Windows Desktop Runtime is not
required). Framework-dependent deployment also avoids the single-file
extract-to-temp pattern that attracts dropper heuristics.

Two executables ship together in `app/`: `Modificus.Curator[.exe]` (the
Avalonia UI, `WinExe`) and `Modificus.Curator.NxmHandler[.exe]` (the
OS-registered `nxm://` handler, native AOT, `TrimMode=full`). The NxmHandler
resolves its sibling Curator exe via `AppContext.BaseDirectory`
(`NxmHandlerRelay.ResolveCuratorMainExe`), so both land in one directory in
every bundle.

`CuratorConfig.RelayDir` defaults to
`<app-data>/Modificus Curator/relay/` (`%LOCALAPPDATA%` on Windows,
`~/.local/share` on Linux; see `config/AppPaths.cs`). The bundle seeds that
default location. The only Relay-presence check in the codebase is in
`RelayLaunchService.Launch` (`if (!File.Exists(launcherPath))`), which returns
`LaunchStatus.Error`; there is no first-run Relay provisioning logic. Manual
`RelayDir` overrides via JSON config are the user's responsibility.

`nxm://` registration is done at runtime by Curator, not by an installer.
`WindowsNxmHandlerRegistrar` writes `HKCU\Software\Classes\nxm` (per-user, no
elevation); `LinuxNxmHandlerRegistrar` writes
`~/.local/share/applications/modificus-curator-nxm-handler.desktop` + a
best-effort `xdg-mime default`. Both encode an absolute path to the handler
exe.

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
gh attestation verify <file> --repo ModifAmorphic/darktide-mod-magos
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

The job downloads the published Windows asset and scans those exact bytes
(not a fresh build), so it scans what users receive:

- **Microsoft Defender**: locates `MpCmdRun.exe` and runs
  `MpCmdRun -Scan -ScanType 3 -File <asset> -DisableRemediation` against the
  downloaded zip. The runner is Windows Server with Defender sometimes
  cloud-delivery-off, so this is a coarse signal, not a guarantee of what a
  consumer Windows 11 box sees.
- **VirusTotal**: optional, gated on the `VIRUSTOTAL_API_KEY` repo secret
  (skipped if unset). Submits the zip via the v3 API
  (`POST /api/v3/files`, or the large-upload URL for files over 32MB), polls
  `GET /api/v3/analyses/{id}` until `status: completed`, then reads the
  multi-engine report. The VT permalink is always posted to the workflow run
  summary regardless of verdict.
- **Issue creation**: opens a GitHub issue when Defender is missing or flags
  the asset, when VirusTotal submission or polling fails, or when the VT
  detection count meets or exceeds the threshold (`VT_THRESHOLD_ENGINES`, set
  to 3). The issue body carries the release tag, the Defender output, the VT
  permalink, and a small playbook with the Microsoft Security Intelligence
  submission portal link
  (https://www.microsoft.com/en-us/wdsi/filesubmission) and a note that
  non-Microsoft engines have their own developer portals. The issue
  deduplicates against an existing open issue for the same tag.

The workflow is signal-only, not a release gate. It runs after the release is
already live; nothing blocks the release. The operator reads the issue (if
any) and decides whether to file a manual submission before announcing the
release elsewhere.

### Why AV scanning exists

Curator's behavior profile reads as malware-shaped regardless of intent:
launching a process that injects a DLL (Relay), enumerating processes
(SingleInstanceGuard), writing URL-handler registry keys, running a named-pipe
server, downloading and extracting archives that contain executables (Nexus
acquisition, DMF install), and creating symlinks (ProfileService.PrepareModRoot).
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
(MpCmdRun, VirusTotal) or a manual portal submission.

## PR gate

`.github/workflows/curator-build.yml` runs on `push` and `pull_request`
against `main` (plus `workflow_dispatch`).

- **Format job** (Ubuntu only): same-repo PRs run `dotnet format` and
  auto-commit any changes as `style: dotnet format [skip ci]`. Fork PRs and
  pushes to `main` run `dotnet format --verify-no-changes` (no commit).
- **Build + test matrix** (Windows + Ubuntu), depends on the format job.
- No `actions/upload-artifact` step. Release assets are produced by the
  release workflow.

## Linux installer

`scripts/install.sh` is served from `raw/main`:

```sh
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-mod-magos/main/scripts/install.sh | sh
```

The script installs the latest release visible to an unauthenticated request
(prereleases included). It queries the GitHub releases list endpoint (which
excludes drafts and returns newest first, so the first `tag_name` is the
latest release; `/releases/latest` is not used because it skips prereleases),
downloads `<tag>-linux-x64.tar.gz`, extracts to a temp dir, validates
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
`CURATOR_REPO`, `CURATOR_ARCHIVE`) so extraction + install can be exercised
against a fake archive in a temp dir without touching real user data or
hitting the network. The script in `main` is authoritative; users always get
the current version, and it always installs the latest release. Users wanting
a specific release manage the install themselves.

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
3. Push the branch and land a conventional commit whose body carries
   `Release-As: 0.1.0` so release-please opens a `0.1.0` release PR against
   the sandbox branch.
4. Merge the release PR on the sandbox branch and let the release workflow
   run end to end: release-please cuts the release, the Windows + Linux
   archives are built and attested, and the AV/VT dispatch fires.
5. Inspect the produced release, archives, attestations, and AV/VT result.
6. Delete the sandbox release and its tag.
7. Strip the sandbox settings (revert `target-branch` and the workflow
   trigger-branch changes) before opening the PR to `main`, so the production
   config stays normal and persistent.

This is an operator rehearsal procedure, not part of the user-facing release
path.

## Scope

What the release pipeline provides today, and what it does not:

- **Code signing.** Releases are unsigned. SmartScreen reputation is not
  established.
- **Windows installer.** The Windows asset is a portable zip; there is no
  Inno Setup / WiX / Velopack installer.
- **In-app auto-update.** Neither Curator nor Relay is auto-updated in-app;
  Relay is bundled as latest-per-release instead.
- **Linux arm64 / Steam Deck builds.** Only `win-x64` and `linux-x64` are
  published.
- **Relay pinning or Relay provenance sidecar.** The bundled Relay is whatever
  was latest (non-draft, prerelease-inclusive) at workflow-run time.

## Sources

- Workflows: `.github/workflows/release.yml` (release-please + asset build +
  attestation + dispatch), `.github/workflows/curator-post-release-av.yml`
  (repository_dispatch AV/VT scan), `.github/workflows/curator-build.yml`
  (PR gate).
- `scripts/install.sh` (the Linux installer).
- Release-please config: `.release-please-config.json` +
  `.release-please-manifest.json`.
- GitHub Artifact Attestations (`actions/attest@v4`):
  https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations.
- Architectural details from the codebase: `src/nxm/NxmHandlerRelay.cs`
  (sibling-exe resolution), `src/nxm/{Windows,Linux}NxmHandlerRegistrar.cs`
  (runtime scheme registration), `src/relay-client/RelayLaunchService.cs`
  (launch-time Relay presence check, no first-run provisioning),
  `src/config/AppPaths.cs` (default `RelayDir` in app-data).
