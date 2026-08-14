# Stdout capture end to end fixture

This fixture reproduces the stdout capture and failure reporting behavior across the process, named pipe protocol
and test adapter boundaries. It is intentionally **not** part of `GdUnit4Net.sln` and is therefore never discovered
or executed by a normal solution build or test run.

## Layout

| Path                     | Purpose                                                                                                    |
|--------------------------|------------------------------------------------------------------------------------------------------------|
| `StdOutCaptureChild`     | Godot test project executed as a child VSTest run. Every test case is one stdout capture lifetime.           |
| `StdOutCaptureRegression`| MSTest parent that starts the child run, asserts on its output and repeats the failing and clean run pair.   |

`StdOutCaptureChild` references `Api` and `TestAdapter` as projects, so the fixture always runs against the current
fork sources and does not require a packaging step.

### Child test suites

- `CaptureHeavyTestSuite` — 32 passing capture lifetimes producing multiline managed output and native Godot output.
- `IntentionalFailureTestSuite` — one deliberate assertion failure after capture heavy output. The clean follow up run
  excludes it by test filter, it is never made conditional in code.

## Prerequisites

- `GODOT_BIN` pointing at the Godot executable matching `ProjectVersions.props`.
- The .NET SDK pinned by `global.json`.

## Running

```bash
dotnet test E2E/StdOutCaptureRegression/StdOutCaptureRegression.csproj --settings E2E/StdOutCaptureRegression/.runsettings
```

One parent run executes one failing and one clean child run, which covers 65 aggregate capture lifetimes.
Set `GDUNIT4_E2E_REPEAT` to soak the fixture, `16` repeats cover more than the 1,000 aggregate capture lifetimes
required by the verification plan:

```bash
GDUNIT4_E2E_REPEAT=16 dotnet test E2E/StdOutCaptureRegression/StdOutCaptureRegression.csproj --settings E2E/StdOutCaptureRegression/.runsettings
```

The child run alone can be executed directly for debugging:

```bash
dotnet test E2E/StdOutCaptureChild/StdOutCaptureChild.csproj --settings E2E/StdOutCaptureChild/.runsettings
```

## What the parent asserts

1. The failing child run reports **every** test, not only the ones before the failure.
2. Its output contains the deliberate assertion text.
3. Its output contains neither `The server returned an unexpected status code` nor `No test matches`.
4. The captured managed and native output of passing tests is attached to their results.
5. The immediately following clean child run passes and contains no token of the previous run.

## Keeping the expected counts in sync

`StdOutCaptureRegressionTest` asserts the reported test totals. When test cases are added to or removed from the child
project, update `ExpectedTotalWithFailure` and `ExpectedTotalWithoutFailure`.
