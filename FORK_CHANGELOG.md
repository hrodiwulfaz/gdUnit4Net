# Outpostia Fork Changelog

This changelog records the changes carried by the Outpostia gdUnit4Net fork and
why they exist. Upstream gdUnit4Net release notes remain in the package-specific
release-note files.

Base: gdUnit4Net `v5.0.0` (`39f836558cbfbd10e8038effb0ac6a4a71ca1629`)

The API and adapter revisions are released together from the same source commit.

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
