# AI Ask Project Rules

## C# development and verification

- This local environment has **no C# compiler, .NET SDK, Visual Studio, or MSBuild**. Do not claim that C# code compiled locally.
- Every C# implementation subagent must add or update focused test cases for the behavior they change.
- C# compilation and test validation must happen through the repository's **GitHub Actions CI**.
- Before modifying shared integration code, an implementation subagent should work on its own branch/worktree and push that branch to GitHub.
- The subagent must use `gh` to monitor the matching CI run and report the actual conclusion/logs. A successful CI run is required before the change can be integrated.
- A failed CI run must be diagnosed and fixed through new commits; do not use `--no-verify`, skip tests, or claim success without a green GitHub Action.
- C# review agents review the commit actually validated by CI. If review requires fixes, the implementation agent makes the fix, pushes it, waits for CI again, then the same reviewer re-verifies the new committed revision.

## Task planning records

- `task/` holds local planning and migration documents. It is intentionally gitignored and must never be committed or packaged in a release.

## Release discipline

- Do not create or replace a Release until implementation tests, GitHub CI, and reviewer re-verification have all completed with no remaining confirmed findings.
- A public tag/release must point at the reviewed, CI-validated commit.
