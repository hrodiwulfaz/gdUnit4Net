# Outpostia Fork Changelog

This changelog records the changes carried by the Outpostia gdUnit4Net fork and
why they exist. Upstream gdUnit4Net release notes remain in the package-specific
release-note files.

Base: gdUnit4Net `v5.0.0` (`39f836558cbfbd10e8038effb0ac6a4a71ca1629`)

The API and adapter revisions are released together from the same source commit.

## Outpostia.6

Date: 2026-10-01

Source: `960a8f79cbe584b694c4b73a475defb9270fdae7`

Packages:

- `gdUnit4.api` `5.0.0-outpostia.6`
- `gdUnit4.test.adapter` `3.0.0-outpostia.6`

Compatibility baseline: Godot `4.7.1-outpostia.2`

- `GDUNIT-RUNTIME-004` moved the project setup coordination below the Godot
  project data directory, so every runner of one worktree shares it whatever
  its `LogFileRoot`:

  ```text
  .godot/gdunit4/setup.lock               exclusive-handle lock, the file name alone means nothing
  .godot/gdunit4/setup-v1.json            success stamp of a reusable preparation
  .godot/gdunit4/setup-in-progress.json   journal of a preparation that has not finished
  .godot/gdunit4/<name>.<pid>.<guid>.tmp  atomic publication, discarded when abandoned
  ```

  Every editor preparation removes the stamp, journals its launch intent and
  then the started editor's process id, start time and executable path. The
  journal is removed only once that editor is verified gone. A later runner
  that finds a journal waits for exactly that process up to
  `CompileProcessTimeout`, never kills it, discards its output and prepares
  again. A journal without a verifiable editor identity, an editor that
  outlives the wait and an identity that can not be read fail the setup with a
  remediation message instead of starting a second editor. The editor
  preparation also observes cancellation and terminates its own process tree.
  Test hosts of older packages use the former
  `<LogFileRoot>/gdunit4-setup.lock` and must be stopped before these packages
  are adopted (`960a8f7`).
- `GDUNIT-RUNTIME-005` added the opt-in `ProjectSetupCache` setting (default
  `false`; `<ProjectSetupCache>true</ProjectSetupCache>` under `<GdUnit4>`,
  `dotnet test ... -- GdUnit4.ProjectSetupCache=false` for one run). When it is
  enabled the runner still installs its generated runner first, then captures
  a content fingerprint under the setup lock and skips the headless editor
  pass when the stamp records exactly that state. Only the editor preparation
  is reused: every runner starts its own Godot runtime with its current
  environment, so coverage, profiler and debugger runs stay eligible, and no
  runtime, discovery result or test result is ever shared.

  The fingerprint is SHA-256 over ordinally sorted relative paths, file
  lengths and file bytes; timestamps are never read. Inputs: `project.godot`,
  the scanner-visible project tree (directories starting with a dot, holding
  `.gdignore` or a nested `project.godot` are skipped), the Godot executable
  with the other files of its directory and its `GodotSharp` tree, the gdUnit4 API and adapter
  module identities, the preparation command, the generated runner and the
  complete `.godot/mono/temp/bin/Debug` output. Outputs: `.godot/uid_cache.bin`,
  `.godot/imported/**`, the `.import`/`.uid` sidecars with import results
  written next to their source, and the presence and content of the global
  class, scene group and extension list caches. Test assemblies outside the
  scanned tree are not part of it; each fresh runtime loads them itself.

  A stamp is published only after one successful editor pass whose inputs
  equal the pre-setup capture in two captures taken after the editor is gone
  and whose outputs are equal in both, so a fresh worktree publishes in its
  first invocation. Nothing is published when the editor fails, times out, is
  canceled, reports `Scan thread aborted` (its `--quit-after` budget ended
  before the scan, still exit code 0), leaves a required output missing or
  changes an input. Projects with enabled editor plugins, GDExtensions, a
  non-hidden project data directory, linked directories in the scanned tree
  or import metadata leaving the project are always prepared and never
  stamped. Every `source_file` and `dest_files` path must be a plain
  `res://a/b/c` path that still resolves to the same location below the
  project root; drive-rooted (`res://C:/x`), drive-relative (`res://C:x`),
  UNC, traversal, stream, backslash and otherwise malformed paths are rejected
  before they reach the file system. Keep `LogFileRoot` outside the scanned
  tree, for example below a `.gdignore` directory, otherwise every run changes
  the fingerprint.

  The runner logs the lock wait, the validation time, the miss reason, the
  preparation time and the publication: `GdUnit4 project setup is up to date`,
  `GdUnit4 project setup required: <reason>`,
  `Published GdUnit4 project setup stamp` and
  `GdUnit4 project setup stamp not published: <reason>`. Deleting
  `setup-v1.json` is always safe and costs one preparation (`960a8f7`).
- `GDUNIT-BUILD-003` added the `GdUnit4ApiTestHost` console project, the
  editor stand-in and standalone setup owner the project setup tests start as
  real processes. It is not packaged (`960a8f7`).
- `GDUNIT-BUILD-004` removed two null suppressions in
  `GodotRuntimeRequireAnalyzer` that current SDK analyzers report as
  unnecessary, so the whole solution passes the warnings-as-errors build again
  (`8b2cc79`).

| Package | SHA-256 |
| --- | --- |
| `gdUnit4.api.5.0.0-outpostia.6.nupkg` | `3d492f14cf7b51810bc081e66a15ed46182cfe5524f5d25eb3c79bf267da3328` |
| `gdUnit4.test.adapter.3.0.0-outpostia.6.nupkg` | `2544308b3cf64327b3be6b1633d12368a35a6e92197ac3cc7493ec2caba2fdbc` |

## Outpostia.5

Date: 2026-09-28

Source: `7357f5ff3fe6211d882d64a38fca7a7c94b27aa6`

Packages:

- `gdUnit4.api` `5.0.0-outpostia.5`
- `gdUnit4.test.adapter` `3.0.0-outpostia.5`

Compatibility baseline: Godot `4.7.1-outpostia.2`

- `GDUNIT-ADAPTER-003` derived the outcome of synthetic `[Before]`/`[After]`
  suite rows from the stage's own reports instead of the recursive suite
  statistics. A harmless stage warning, such as orphan nodes, next to one real
  failure no longer turns every test of the class red; failure, terminated,
  interrupted and abort reports, including stage timeouts, still fail the rows
  (`e00172e`).
- `GDUNIT-RUNTIME-003` added runner-folder retention: before project setup each
  runner deletes older `<LogFileRoot>/<runner-id>/` folders, keeping the newest
  `RunnerRetentionCount` (default 150, 0 disables), the current runner's folder
  and folders of still running test hosts (`b28af7c`).
- `GDUNIT-INPUT-001` stopped `SceneRunner` from warping the OS cursor for
  simulated mouse events; the runner tracks the simulated mouse position
  internally and reports it from `GetMousePosition`/`GetGlobalMousePosition`
  (`194646b`).
- `GDUNIT-BUILD-002` removed null suppressions that newer SDK analyzers report
  as unnecessary, so the warnings-as-errors build passes on current SDKs
  (`e3c9b52`).

| Package | SHA-256 |
| --- | --- |
| `gdUnit4.api.5.0.0-outpostia.5.nupkg` | `d749cb242862d47ff98dccb4f49e11802f0757f7d63dfd0ec505e30849dab23a` |
| `gdUnit4.test.adapter.3.0.0-outpostia.5.nupkg` | `4b0da9b60b8fb11a6201f0dbb626c2cd6318b0ccdbb1271a70f7a624ebb57ec3` |

## Outpostia.4

Date: 2026-08-15

Source: `7cb67438263848c4ff26e04ef8f9c4ac008a8221`

Packages:

- `gdUnit4.api` `5.0.0-outpostia.4`
- `gdUnit4.test.adapter` `3.0.0-outpostia.4`

Compatibility baseline: Godot `4.7.1-outpostia.2`

- `GDUNIT-TIMEOUT-001` added a configurable default timeout for every test and
  lifecycle stage. Explicit positive per-test timeouts still take precedence,
  and a timeout terminates the incomplete Godot batch instead of allowing it to
  continue in an unknown state (`7cb6743`).

| Package | SHA-256 |
| --- | --- |
| `gdUnit4.api.5.0.0-outpostia.4.nupkg` | `3870833f1561c5668ab1cdb760c91352659782a216ce36c0ebe67d27b433d506` |
| `gdUnit4.test.adapter.3.0.0-outpostia.4.nupkg` | `333f0cead1174cfbfd57a9bcdab8d25c120258c5543a95208d59955b7093e84b` |

## Outpostia.3

Date: 2026-08-14

Source: `3c94ecbb0450dffb29e90a1e4eeeb88e6984031e`

Packages:

- `gdUnit4.api` `5.0.0-outpostia.3`
- `gdUnit4.test.adapter` `3.0.0-outpostia.3`

Compatibility baseline: Godot `4.7.1-outpostia.2`

- `GDUNIT-GODOT-002` added Godot 4.7.1 compatibility and ignored generated
  importer artifacts (`ddeb966`, `9554560`).
- `GDUNIT-STDOUT-001` isolated stdout-capture ownership and added an end-to-end
  fixture so concurrent runners cannot restore or close another runner's
  capture (`3eeb080`, `e64cec3`).
- `GDUNIT-FAILURE-001` preserved setup, runtime, transport, and server failures
  instead of replacing them with later communication errors (`9f59d44`).
- `GDUNIT-ADAPTER-002` reported every test result independently and ignored
  framework-rejected messages without losing accepted results (`0dfe898`,
  `bd616b8`).
- `GDUNIT-RUNTIME-002` always terminated the spawned Godot runtime during final
  cleanup (`8f237f5`).

## Outpostia.2

Date: 2026-06-27

Source: `5fa574463d596e799473163996376114d425f7c3`

Packages:

- `gdUnit4.api` `5.0.0-outpostia.2`
- `gdUnit4.test.adapter` `3.0.0-outpostia.2`

Compatibility baseline: Godot `4.6.0-outpostia`

- `GDUNIT-RUNTIME-001` isolated runner pipes, logs, and generated source
  directories so separate test shards can run safely (`c0a012e`, `06e0dee`,
  `0d16084`).
- `GDUNIT-PROJECT-001` resolved Godot project roots explicitly, loaded external
  test assemblies, and hardened parallel runner tracking (`9c51abf`,
  `5909fbd`, `191e1d4`).
- `GDUNIT-ADAPTER-001` added Outpostia settings, managed-type suite grouping,
  and fully qualified display names (`4387d72`, `8cce539`).
- `GDUNIT-GODOT-001` added Godot 4.6 compatibility (`2ae1e73`).
- `GDUNIT-BUILD-001` built the target Godot project before execution and kept
  the generated runner when setup failed so its diagnostics remain available
  (`88241c8`, `d18cdd6`).
- `GDUNIT-FAILURE-002` reported setup failures as test failures rather than
  aborting without the responsible test result (`a5b75e8`).

## Outpostia.1

Date: 2026-06-22

Source: `155ed3f5d60bca507bef8572cb351f5c46ca69f9`

Packages:

- `gdUnit4.api` `5.0.0-outpostia.1`
- `gdUnit4.test.adapter` `3.0.0-outpostia.1`

Compatibility baseline: Godot `4.6.0-outpostia`

This revision established the fork package identity, pointed package metadata
and restore configuration at the Outpostia fork, and moved the Godot build
baseline from official 4.4 to the Outpostia 4.6 fork. It contained no functional
test-runner divergence beyond upstream `v5.0.0`.
