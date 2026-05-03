# SC-DataRunner

> An open-source Star Citizen commodity terminal scanner that submits trade
> data to [UEX Corp](https://uexcorp.space) — fast OCR, manual review, one
> click. Built in .NET 9 / WPF, ships with a single Windows installer that
> auto-updates itself.

[![Release](https://img.shields.io/github/v/release/Olrik-WP/SC-DataRunnerNet?label=release&color=2EA043)](https://github.com/Olrik-WP/SC-DataRunnerNet/releases/latest)
[![CI](https://github.com/Olrik-WP/SC-DataRunnerNet/actions/workflows/release.yml/badge.svg)](https://github.com/Olrik-WP/SC-DataRunnerNet/actions/workflows/release.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![Windows](https://img.shields.io/badge/platform-Windows-0078D6)](#install)

---

## What it does

You take a screenshot of a commodity terminal in Star Citizen → SC-DataRunner
parses it locally, lets you review every value, then submits the result to
UEX Corp where the entire SC trading community benefits from up-to-date
prices.

```
[F12 in-game]            [SC-DataRunner picks it up]         [you click Send]
        ↓                          ↓                                ↓
ScreenShot-2026-...   →   Inbox card · OCR pipeline    →     UEX /data_submit
                          (~2-30s on CPU)                     (LIVE prices)
```

### Why use this over the existing tools?

- **Open-source under AGPL-3.0** — you can audit every byte, fork it, run
  your own UEX app, or ship a private build. No closed binaries from a
  random Discord download.
- **Multilingual fonts handled out of the box** thanks to PaddleOCR (vs.
  Tesseract-only in older tools).
- **Real validation pipeline** — fuzzy match against the UEX catalog, length
  penalty to kill the classic "Stims → Tin" false positive, status-leak
  protection, container-size union across rows, live diff vs. the current
  UEX price before submission.
- **No Python runtime to install.** Single signed Windows installer, ~50 MB.
  .NET 9 runtime is bundled, you don't need to install anything else.
- **Auto-updates** via [Velopack](https://github.com/velopack/velopack) —
  you get every fix the same week it ships.
- **Privacy-first**: OCR runs entirely on your machine, the screenshot file
  is only attached to a UEX submission AFTER you click Send.

### What it doesn't do (yet)

- Linux / macOS support — it's WPF so today the binary is Windows-only.
  Platform-specific bits live in `DpapiSecretKeyStore` and could be
  swapped behind the existing `ISecretKeyStore` interface for a Mono /
  Avalonia port. PRs welcome.
- In-game capture — for now it watches your `Screenshots` folder. You
  press F12 in SC, the watcher picks the file up. Native screen capture
  via `Windows.Graphics.Capture` is on the roadmap.

---

## Install

### End user (recommended)

1. Download the latest `SC-DataRunner-win-Setup.exe` from
   [Releases](https://github.com/Olrik-WP/SC-DataRunnerNet/releases/latest).
2. Run it. The installer is signed and self-contained (~50 MB).
3. The first launch shows a 3-step wizard:
   - paste your **UEX user secret-key** (from
     [uexcorp.space → Account → Secret Key](https://uexcorp.space/account/home))
   - point to your SC `Screenshots` folder (the wizard pre-fills the standard
     location)
   - that's it — auto-update is on by default.

You do **NOT** need to register your own UEX application. The official build
ships with one embedded so the tool works out of the box.

### Self-build (developer / fork)

```pwsh
git clone https://github.com/Olrik-WP/SC-DataRunnerNet
cd SC-DataRunnerNet
dotnet build SC-DataRunnerNet.sln -c Release
```

If you want your own UEX app token baked in (so you control the rate-limit
quota of your fork), set the MSBuild property at build time:

```pwsh
$env:UexAppBearerToken = 'paste-your-token-here'
dotnet publish src/DataRunner.App.Wpf/DataRunner.App.Wpf.csproj -c Release -r win-x64 --self-contained
```

Without that property, the wizard adds a 4th step to ask the user for their
own UEX app token (`Settings → App bearer token (advanced)` lets them
override at any time).

---

## How it works (quick tour for contributors)

```
┌─────────────────────────┐
│ ScreenshotFolderWatcher │  watches the SC Screenshots folder, debounces
└────────────┬────────────┘  partial writes, dedupes already-submitted files
             ↓
┌─────────────────────────┐
│ OcrCoordinator (queue)  │  serialises OCR jobs, marshals UI updates
└────────────┬────────────┘
             ↓
┌─────────────────────────┐
│ PaddleOcrPipeline       │  3 passes: top banner / left header / right panel
│ + ImagePreprocessor     │  CLAHE + ×2 upscale + horizontal-region grouping
│ + RegionLayout          │  reconstruct 2-D layout from per-region bboxes
└────────────┬────────────┘
             ↓
┌─────────────────────────┐
│ CommodityParser         │  regex + fuzzy match against the UEX catalog,
│ + FuzzyMatcher          │  length penalty, status barriers, container UNION
└────────────┬────────────┘
             ↓ ParsedSubmission + UexDataSubmitPayload
┌─────────────────────────┐
│ ScreenshotEditViewModel │  live validation, override checkbox, manual edits
└────────────┬────────────┘
             ↓ user clicks Send
┌─────────────────────────┐
│ ConfirmSubmitDialog     │  validation report + LIVE diff vs UEX prices +
│ + DuplicateChecker      │  exact wire-JSON preview, FINAL gate before POST
└────────────┬────────────┘
             ↓
┌─────────────────────────┐
│ UexApiClient            │  POST /data_submit with secret-key + bearer
│ + SqliteSubmissionHistory  audit log of every request/response, locally
└─────────────────────────┘
```

### Project layout

```
src/
├── DataRunner.Core/          ← models, abstractions, no third-party deps
├── DataRunner.UexClient/     ← UEX API + DPAPI secret store + SQLite history
├── DataRunner.Ocr/           ← PaddleOCR pipeline + parsers + matcher
└── DataRunner.App.Wpf/       ← the WPF / MVVM UI (only platform-specific module)

spike/
└── DataRunner.OcrSpike/      ← throw-away CLI for OCR experiments

scripts/
└── release.ps1               ← interactive release helper (bump → tag → push)

.github/workflows/
└── release.yml               ← CI build + Velopack pack + GitHub Release
```

### Building from source

| Need | Command |
|---|---|
| Restore + build | `dotnet build SC-DataRunnerNet.sln` |
| Run the WPF app | `dotnet run --project src/DataRunner.App.Wpf` |
| Pack a self-contained installer | `pwsh ./scripts/release.ps1 -Mode local` |
| Release a public version | `pwsh ./scripts/release.ps1 -Bump patch -Mode ci` |

The release script asks you what kind of bump (`patch` / `minor` / `major`),
commits the new `<Version>` in `Directory.Build.props`, and pushes the
matching `v1.2.3` tag. The CI workflow does the rest — Velopack packs +
uploads to GitHub Releases, existing users auto-update within hours.

### Bearer-token security model (important)

The official CI build embeds the UEX app bearer token via
`[AssemblyMetadata("UexAppBearerToken", ...)]` so end users never have to
register their own UEX app. The token is **extractable** from the binary
(strings.exe, ILSpy, ...) — same trade-off every datarunner tool on the
market makes.

If the token ever leaks publicly:

1. Go to [uexcorp.space/api/apps](https://uexcorp.space/api/apps), click
   "Regenerate" on the SC-DataRunner app.
2. Update the GitHub Actions secret `UEX_APP_BEARER_TOKEN`.
3. `pwsh ./scripts/release.ps1 -Bump patch` → push the tag.
4. Velopack delivers the new build to all users on next launch (typically
   within a few hours).

End-user data (their UEX secret-key, their submission history) is encrypted
locally with Windows DPAPI and never leaves the machine — even if the bearer
token leaks, no user data is at risk.

---

## OCR accuracy & known limits

### What works well

- Terminal name recognition: ~99% on clean screenshots, with disambiguation
  for terminals that share a name across star systems (e.g. "Pyro Gateway"
  vs. "Stanton Gateway").
- Commodity name matching: ~95-99% with the length-penalty fuzzy matcher
  protecting against false positives.
- Container sizes: 1, 2, 4, 8, 16, 24, 32 picked up with UNION across rows
  (so the smaller sizes that the OCR sometimes misses on one row are
  recovered from another).
- Status detection (`MAX INVENTORY`, `LOW INVENTORY`, ...) tolerates the
  most common OCR errors (M↔N, 0↔O, period inserted between words).

### What still needs your eye

- Status labels are sometimes missed entirely on rows where an icon abuts
  the text (the typical "bag-icon" rows on Daekens Research Outpost). The
  Status combobox is tinted **orange** when this happens — pick the right
  value before clicking Send.
- Match scores below 100% always trigger a warning, even when the right
  commodity was picked. The override checkbox in the validation footer
  lets you ship anyway after manual review.
- Numeric typography in SC is glow-heavy at 1080p; values >9 999 999
  occasionally lose a digit. Always cross-check the SCU and price against
  the screenshot (the "Show screenshot" button opens a zoom viewer).

The roadmap section below tracks the OCR improvements we've explored
(row segmentation, MKLDNN backend, fine-tune) and why they are or aren't
on the critical path.

---

## Roadmap

### Done
- ✅ End-to-end OCR pipeline with manual review UI
- ✅ DPAPI-encrypted credential storage
- ✅ SQLite submission audit log
- ✅ Velopack auto-update + signed installer
- ✅ Validation footer with live diff vs UEX prices
- ✅ Override checkbox to bypass blocking warnings after manual review
- ✅ Multi-screenshot merge (one terminal, many screenshots → one submission)
- ✅ Built-in app token + override (this PR)

### Next
- 🔜 Native screen capture (`Windows.Graphics.Capture`) so users can press
  a hotkey from inside the game instead of using F12 + folder watcher.
- 🔜 Stale-target view: pull `commodities_prices_all`, surface terminals
  whose data is most outdated so the user knows where to go contribute.
- 🔜 OCR fine-tuning on a SC-specific dataset (would push commodity-name
  accuracy from ~95% to ~99%+; ~1 week of effort).

### Maybe later
- ⏳ Linux / macOS port (Avalonia rewrite of the WPF layer).
- ⏳ GPU edition (PaddleOCR on CUDA) for power users with NVIDIA GPUs.
- ⏳ Cloud sync of the submission history across multiple PCs.

---

## Contributing

PRs welcome. The codebase is heavily commented because OCR is full of
non-obvious trade-offs — every "weird" decision should have a comment
explaining why and what was tried before. Please keep that convention if
you submit changes.

Useful starting points:

- Bug? Open an [issue](https://github.com/Olrik-WP/SC-DataRunnerNet/issues)
  with a screenshot of the SC terminal + the OCR fragment shown in the
  editor (`Right-panel OCR (...)` in the log file at
  `%LOCALAPPDATA%\SC-DataRunnerNet\logs\app-*.log`).
- Want to improve OCR accuracy? `src/DataRunner.Ocr/Pipeline/CommodityParser.cs`
  and `src/DataRunner.Ocr/Matching/FuzzyMatcher.cs` are where most of the
  heuristics live.
- Want to add a new validation rule? `ScreenshotEditViewModel.RecomputeValidation`
  is the entry point.

Code style: standard `dotnet format`. The CI runs it on every push.

---

## License

This project is licensed under the **GNU Affero General Public License v3.0
or later** — see [LICENSE](LICENSE) for the full text.

In short: you can use, modify, and redistribute it freely, but if you run
a modified version as a network service (or distribute it), you must make
your modifications available under the same license. This is intentional:
the SC datarunner ecosystem benefits from improvements being shared back.

---

## Acknowledgments

- [UEX Corporation](https://uexcorp.space) for hosting the API and
  curating the catalog of commodities and terminals.
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) /
  [Sdcb.PaddleSharp](https://github.com/sdcb/PaddleSharp) for the OCR
  engine.
- [WPF-UI](https://github.com/lepoco/wpfui) for the Fluent design system
  components.
- [Velopack](https://github.com/velopack/velopack) for the silent auto-update
  story.
- The SC datarunner Discord community for the original Python reference
  implementation that proved the workflow viable, and for the patient
  feedback during development.

⚠️ This is an unofficial fan-made tool. Star Citizen and the Roberts Space
Industries logo are trademarks of Cloud Imperium Rights LLC. This project is
not affiliated with, endorsed by, or sponsored by Cloud Imperium.
