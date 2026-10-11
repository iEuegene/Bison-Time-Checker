# Bison Time Checker — rules for working in this repo

## Every user-visible change ships with updated help (non-negotiable)

The owner requires the in-app guide to match every release. A feature, fix or behaviour
change is not done until all of these are updated in the same commit:

1. `BisonTime.App/HelpContent.cs` — the guide sections (`Guide`) describe the new behaviour,
   and `Changes` has a new top line for the version, written for colleagues, in Ukrainian.
2. `README.md` — the public page (features, the "Як BT визначає проєкт" table, privacy).
3. `<Version>` in `BisonTime.App/BisonTime.App.csproj` — bumped; CI publishes the release
   `v<Version>` from `main`.

## Public repository: no client data

The repo is public. Never commit real client domains, names, titles or screenshots from
the user's work — not in code, tests, comments, README or release notes. Use fictional
domains (shop.com.ua, newclient.ua, alpha-med.ua, coffee-shop.com.ua, …). It is fine to
say the tool is made for Inweb employees; nothing about Inweb's clients or internals.

## Build and test

- Core tests run anywhere: `dotnet run --project BisonTime.Tests` (no NuGet packages).
- The WPF app builds only on Windows: GitHub Actions (`.github/workflows/build.yml`).
  Compile errors show up as check-run annotations:
  `gh api repos/iEuegene/Bison-Time-Checker/check-runs/<job id>/annotations`.
- Distribution is a self-contained folder, not a single-file exe (packed single-file
  .NET exes trigger antivirus false positives).

## Style

UI follows the Inweb palette and fonts already in `App.xaml` / `Ui.cs` (lime #F0FC7E,
green #003B29, orange #EF583D only for warnings and pause, Geist + Unbounded, -14° parallelogram).
