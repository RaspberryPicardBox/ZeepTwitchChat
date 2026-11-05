# Continuous integration and security scanning

Every branch push and every pull request runs the checks below. To make them block a merge,
see *Requiring the checks* at the end.

## What runs, and when

| Workflow | Trigger | What it does |
| --- | --- | --- |
| `CI` | every push, every pull request | Runs the whole check suite (logic, emotes, GIF decoding, moderation, and the security section), then the forbidden-API gate and the personal-data gate |
| `Security` | every push, every pull request, weekly | `gitleaks` over the full history for credentials, and a NuGet audit (`--vulnerable --include-transitive`) that fails on a known-vulnerable package |
| `CodeQL` | push and pull request to `main`, weekly | GitHub's static analysis for C#, built from the shared logic the test harness compiles |
| `Dependency review` | pull requests | Fails a PR that adds or updates a dependency with a moderate or worse advisory, and comments the summary |
| `Dependabot` | weekly | Opens PRs for NuGet and GitHub Actions updates, which then run the checks above |

Two local gates run both in CI and by hand:

```bash
./tools/check-dangerous-apis.sh   # the automated half of docs/security.md
./tools/check-personal-data.sh    # machine paths, personal emails, credentials
```

## Why the plugin itself is not built in CI

The plugin targets `net472` and compiles against the *installed* game: `Zeepkist.dll`,
`Assembly-CSharp.dll`, Unity and BepInEx, referenced in place through `GameDir`. A public CI
runner has no Zeepkist, and redistributing those assemblies is not an option, so CI builds the
logic under test instead - `tests/ZeepTwitchChat.LogicTests` compiles the Unity-free files, which
is most of the code and all of the parsing, formatting, decoding and policy logic. The
Unity-facing files (`Plugin.cs`, `ChatInjector.cs`, `ChatBufferEditor.cs`, `EmoteSpriteAsset.cs`
and the Harmony patch) are covered by the review in `docs/security.md` and by running the mod.

A local build is unaffected:

```bash
ZEEPKIST_GAME_DIR=/path/to/Zeepkist dotnet build plugin/ZeepTwitchChat.Plugin -c Release
```

## Requiring the checks

CI runs on its own, but nothing *blocks* a merge until the branch is protected. In the GitHub
repository: **Settings -> Branches -> Add branch protection rule** for `main`, then:

* Require a pull request before merging.
* Require status checks to pass, and select these as required:
  `Logic and security checks`, `Secret scan`, `Package audit`, `Analyze C#`, `Review dependency changes`.
* Require branches to be up to date before merging (so the checks run against the merge result).

Also worth turning on in **Settings -> Code security and analysis**: *Dependabot alerts*,
*Dependabot security updates*, *Secret scanning* with *Push protection*, and *Private vulnerability
reporting* (which pairs with `.github/SECURITY.md`).

`SECURITY.md` tells a researcher how to report something privately instead of opening a public
issue, which matters for a mod that people install into their game.
