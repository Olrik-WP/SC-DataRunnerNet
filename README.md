# SC-DataRunner

> An open-source Star Citizen commodity terminal scanner that submits trade
> data to [UEX Corp](https://uexcorp.space) — fast OCR, five-layer validation,
> one click. Built in .NET 9 / WPF, ships as a single Windows installer that
> auto-updates itself silently.

[![Release](https://img.shields.io/github/v/release/Olrik-WP/SC-DataRunnerNet?label=release&color=2EA043)](https://github.com/Olrik-WP/SC-DataRunnerNet/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Olrik-WP/SC-DataRunnerNet/total?label=downloads&color=2EA043)](https://github.com/Olrik-WP/SC-DataRunnerNet/releases)
[![CI](https://github.com/Olrik-WP/SC-DataRunnerNet/actions/workflows/release.yml/badge.svg)](https://github.com/Olrik-WP/SC-DataRunnerNet/actions/workflows/release.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![Windows](https://img.shields.io/badge/platform-Windows-0078D6)](#install)

---

## What it does

You take a screenshot of a commodity terminal in Star Citizen → SC-DataRunner
parses it locally, lets you review every value, then submits the result to
UEX Corp where the entire SC trading community benefits from up-to-date prices.

```
[Print Screen in SC]      [SC-DataRunner picks it up]         [you click Send]
        ↓                          ↓                                ↓
ScreenShot-2026-...   →   Inbox card · OCR pipeline    →     UEX /data_submit
                          (~2-30s on CPU)                     (LIVE prices)
```

> ℹ️ In Star Citizen the screenshot key is **Print Screen** (`Impr. écran`
> on FR keyboards), **not** F12. Files land in
> `<SC install>\LIVE\Screenshots\` by default — that's the folder
> SC-DataRunner watches.

![SC-DataRunner main window](docs/images/hero.png)

### Why use this over the existing tools?

- **Open-source under AGPL-3.0** — you can audit every byte, fork it, run
  your own UEX app, or ship a private build. No closed binaries from a
  random Discord download.

- **Built so bad data is harder to send than good data.** Five independent
  validation gates sit between every screenshot and the UEX API — live editor
  validation, static payload checks, live price-drift comparison against UEX,
  a mandatory confirmation dialog with exact JSON preview, and per-item
  validation required before batch send. See [Validation pipeline](#validation-pipeline--5-gates) for the full breakdown.

- **Batch submission with mandatory preview.** Queue up multiple captures,
  validate each one individually, then send them all. A preview dialog shows
  exactly which lines survive deduplication, the JSON body per terminal, the
  estimated duration, and the throttle delay — before a single POST goes out.

- **Trade Route Planner built in.** A dedicated tab pulls live routes from
  the UEX API, filters them by your cargo vehicle's container sizes, ranks
  them by profit, ETA, ROI, or a blended Trader ↔ Datarunner score, and links
  directly to each origin/destination on UEX.

- **Stale-target view** — built-in tab that pulls commodity prices from UEX
  and shows which `(terminal, commodity)` pairs are most outdated, so you can
  plan trips that actually help the database.

- **Side-by-side review** — toggle a docked screenshot panel next to the
  validation form, with click-and-drag pan and mouse-wheel zoom; great
  on ultrawide monitors, still usable on 1080p once the inbox is collapsed.

- **Diagnostics tab + one-click bug report** — copies your version, recent
  log tail, last 5 submissions and prefs to the clipboard, ready to paste
  into a GitHub issue. No screenshots of logs, no zip-and-attach.

- **No Python runtime to install.** Single signed Windows installer, ~50 MB.
  .NET 9 runtime is bundled, you don't need to install anything else.

- **Silent auto-updates** via [Velopack](https://github.com/velopack/velopack) —
  every fix lands in your build the same week it ships, with a discreet
  pill in the status bar so you stay in control of when to apply it.

- **Privacy-first**: OCR runs entirely on your machine, the screenshot file
  is only attached to a UEX submission AFTER you click Send (and you can
  disable that attachment in Settings).

---

## System requirements

- **Windows 10 21H2** or newer (Windows 11 recommended for Mica titlebar).
- **~250 MB free disk** (~50 MB installer + ~150 MB OCR models downloaded
  on first OCR run + a few MB of cache & history).
- **No GPU required.** OCR runs on CPU; ~2-30 s per screenshot depending
  on your CPU. An NVIDIA GPU edition is on the roadmap.
- **Star Citizen LIVE** screenshots in PNG or JPG (the in-game default).
- A **UEX user secret-key** ([uexcorp.space → Account → Secret Key](https://uexcorp.space/account/home)).

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
     location, including `D:\Jeux\StarCitizen\LIVE\Screenshots` and friends)
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

### Uninstall / reset

- **Uninstall the app**: `Settings → Apps → SC-DataRunner → Uninstall`
  (standard Windows uninstaller — it removes the binaries but **keeps your
  data**).
- **Wipe local data too** (history DB, prefs, cached catalog, encrypted
  secret-key, logs): delete the folder
  `%LOCALAPPDATA%\SC-DataRunnerNet\`.
- **Revoke API access on UEX** if you're done with it: regenerate your
  secret-key from [uexcorp.space → Account](https://uexcorp.space/account/home).

---

## Your first upload (60-second tour)

1. Take a screenshot of any commodity terminal in Star Citizen with
   **Print Screen** (`Impr. écran`).
2. Alt-tab to SC-DataRunner — the new file pops in the **Inbox** within a
   second, OCR runs (~2-30 s), the card turns green when ready.
3. Click the card. The validation editor opens. Cross-check SCU / price /
   status against the screenshot — toggle **Side-by-side** if you want
   the image docked next to the form.
4. Fix any orange/red row (orange = warning, red = blocking; the override
   checkbox lets you ship anyway after manual review).
5. Click **Validate**, then **Send**. The confirmation dialog shows the live
   diff vs UEX prices and the exact JSON about to be POSTed. Confirm → done.
6. The card moves to **History**, the screenshot is auto-deleted (if
   that preference is on), and your submission counts toward UEX
   datarunner stats.

> 💡 **Tip**: open the **Targets** tab first to see which terminals are
> the most stale — you'll farm the most useful submissions in the same
> trip.

> 💡 **Batch tip**: validate several captures in a row, then click
> **Send batch** in the Inbox toolbar. A preview shows you exactly what
> will be sent and deduplicates overlapping reports automatically.

---

## How it works (quick tour for contributors)

### Pipeline

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
│ + TabDetector           │  colour-based BUY/SELL detection (hue/saturation),
│                         │  independent of OCR text — most reliable guard
│ + aggressive retry      │  re-OCR with ×3 upscale + stronger CLAHE if a
└────────────┬────────────┘  terminal name or status came back empty/Unknown
             ↓
┌─────────────────────────┐
│ CommodityParser         │  regex + fuzzy match against the UEX catalog,
│ + FuzzyMatcher          │  length penalty, status barriers, container UNION,
│                         │  OutOfStock → SCU=0 fallback
└────────────┬────────────┘
             ↓ ParsedSubmission + confidence scores
┌─────────────────────────┐
│ ScreenshotEditViewModel │  GATE 1: live validation (terminal, tab, commodity
│ + ScreenshotPanel       │  match %, SCU/price/status); override checkbox;
│                         │  optional side-by-side screenshot panel
└────────────┬────────────┘
             ↓ user clicks Validate, then Send (or Send batch)
             │
             │  Individual path                Batch path
             ├──────────────────────┐   ┌──────────────────────────┐
             │ GATE 2: PayloadValidator │ │ BatchPlanner: dedup by   │
             │ GATE 3: DuplicateChecker │ │ (commodity, BUY/SELL)    │
             │ GATE 4: ConfirmSubmit    │ │ per terminal, latest wins│
             │         Dialog (JSON     │ │                          │
             │         preview)         │ │ GATE 4b: BatchPreview    │
             └──────────┬───────────┘   │ Dialog — table of kept /  │
                        │               │ deduped lines + JSON per  │
                        │               │ terminal — mandatory, not  │
                        │               │ skippable                  │
                        │               │                            │
                        │               │ Per-item: Gates 2+3 before │
                        │               │ each POST; backoff on 429; │
                        └───────────────┘ Stop/Retry controls        │
                                          └──────────────────────────┘
             ↓
┌─────────────────────────┐
│ UexApiClient            │  POST /data_submit with secret-key + bearer
│ + SqliteSubmissionHistory  audit log of every request/response, locally
└─────────────────────────┘
```

---

## Validation pipeline — 5 gates

Every submission passes through all of these before a byte reaches UEX.
**It is deliberately harder to send bad data than to send good data.**

### Gate 1 — Live editor validation

Runs on every keystroke in the editor.

| Condition | Effect |
|---|---|
| Unknown BUY/SELL tab | **Hard block** |
| Ambiguous terminal (same name, multiple systems) | **Hard block** until user explicitly picks the right one |
| Commodity OCR match < 85 % | **Hard block** |
| SCU = 0 on a non-Out-of-Stock line | **Hard block** |
| Missing price or inventory status | **Hard block** |
| Terminal match < 80 % | Warning |
| Commodity match 85–99 % | Warning |
| Image aspect ratio outside ~1.6–2.4 | Warning (OCR quality risk) |

Sending despite errors requires checking **"I have reviewed everything"** —
a hard, visible override that resets on every new capture load.

### Gate 2 — Static payload validator

Applied to the serialised JSON before any network call:

- Valid terminal ID present in the UEX catalogue
- No duplicate commodities in one payload
- No mixed `buy_*` / `sell_*` fields on the same line
- Inventory statuses within the accepted 1–7 range
- Price > 50 M → warning; price < 0 → error
- SCU > 100k → warning; SCU < 0 → error
- Empty line without `is_missing` flag → warning

### Gate 3 — Live price-drift checker (DuplicateChecker)

1. **Local history check**: same `(terminal, commodity)` submitted
   successfully within the last 5 minutes → **hard block** (avoids UEX
   duplicate-rejection errors).
2. **Live UEX fetch** (`/commodities_raw_prices` for the target terminal):
   - Network failure → warning, user can proceed blind
   - Prices identical within 1 % and last remote update < 5 min → **hard block**
   - Price drift > 30 % vs live UEX value → **hard block**
   - Drift 5–30 % → warning requiring explicit acknowledgement
   - Drift < 5 % → informational only

### Gate 4 — Confirmation dialog (individual) / Batch preview dialog (batch)

**Individual send** — `ConfirmSubmitDialog` shows:
- Full list of validation issues and duplicate-check findings
- **Exact JSON body** that will be POSTed
- Production / test mode, game branch (LIVE/PTU), screenshot attachment toggle
- Send only enabled when all blocks are cleared or explicitly overridden and all warnings acknowledged

**Batch send** — `BatchPreviewDialog` (mandatory, cannot be skipped) shows:
- Table of **kept vs deduplicated lines** with the reason for each removal (dedup by `(commodity, BUY/SELL)` per terminal — BUY and SELL are never merged; latest capture wins per combination)
- **Exact JSON per terminal POST**
- Estimated total duration, configurable throttle delay between POSTs
- Production mode, game branch, screenshot attachment, delete-after-send toggles

### Gate 5 — UEX rejection mapping

HTTP responses from UEX are parsed and mapped to specific, actionable
help text: expired token, screenshot older than 90 days, PTU rejection,
`invalid_game_version`, line count limits, rate-limit (1 000 reports /
30 min), and more.

---

## Batch submission

The **Send batch** button in the Inbox toolbar is disabled until every queued
capture has been individually validated — you cannot skip the per-item review
step.

Once enabled:

1. `BatchPlanner` groups by terminal, deduplicates by `(commodity, BUY/SELL)`,
   keeping the latest capture per combination.
2. The mandatory **BatchPreviewDialog** presents the full plan. No POST happens
   until you confirm.
3. `BatchSubmitter` sends each item in sequence with the configured inter-POST
   delay, running Gate 2 + Gate 3 again immediately before each POST.
4. On HTTP 429 / `too_many_reports` / 5xx, it backs off and retries up to
   3 times per item.
5. **Stop batch** cancels remaining items. Items already in-flight finish
   their current POST gracefully.
6. **Retry failed** re-queues Failed items as Validated and restarts the
   batch for those items only.

---

## Trade Route Planner

The **Trade routes** tab pulls live data from the UEX `/commodities_routes`
endpoint and lets you find the most profitable runs for your ship.

**Filtering**

| Filter | What it does |
|---|---|
| Cargo vehicle | Shows only routes whose container sizes fit your ship's cargo grid |
| Loading dock | Both endpoints must have a loading dock |
| Auto-load | Both endpoints must have a freight elevator |
| Legal | Hides routes involving commodities flagged as illegal in the UEX catalogue |
| Monitored | Both endpoints must be monitored terminals |
| Space / Ground | Restricts to space-only or ground-only terminal pairs |
| Refuel | At least one endpoint has a refuelling station |
| Predicted | At least one price in the route is a predicted (not live-reported) value |
| Min profit (aUEC) | Hard floor on budget-capped effective profit |
| Min profit/min (aUEC/min) | Hard floor on profit per minute of quantum travel |

Routes with effective profit ≤ 0 are **always hidden** — this is not a
toggle, it's a hard rule.

**Columns**

| Column | Notes |
|---|---|
| Origin / Destination | Terminal name, station, system |
| Commodity | The traded good |
| SCU | Effective SCU given your budget cap |
| Invested (aUEC) | Capital required |
| Profit (aUEC) | Budget-capped effective profit |
| aUEC/min | Profit ÷ quantum-travel ETA — the real efficiency metric. Displayed in compact K/M format; exact value in tooltip |
| ROI | Return on investment % |
| Distance | Quantum distance in Gm |
| ETA | Estimated travel time (distance ÷ 175 Mm/s baseline + 10 s spool) |
| Containers | Supported container sizes at origin |
| Score | UEX route score |
| Stale | Oldest price age at either endpoint |

**Sorting**

Click any column header to sort. Click the **★ icon** in a column header to
**favourite** it as the persistent default sort (saved across sessions). The
sort direction is also persisted. When a favourite sort is active, the
Trader ↔ Datarunner slider is disabled (shown with a red warning banner) since
the grid is no longer sorting by the DatarunnerScore.

**Trader ↔ Datarunner slider**

When no favourite sort is set, routes are ranked by a composite
`DatarunnerScore`:

- **Trader side (0%)**: pure budget-capped profit (5M aUEC = full saturation)
- **Datarunner side (100%)**: 70 % weight on the age of the oldest stale price
  at either endpoint (180 days = full saturation) + 30 % weight on the count
  of stale rows (10+ = saturation)

The default is 30 % — profit-first, but stale routes bubble up. The slider
value is persisted across sessions.

**Other route features**

- Origin and destination search with tokenised multi-field matching (name,
  station, system, orbit, planet)
- Investment budget cap (recalculates effective SCU and profit)
- Manual refresh with throttle guard (warns if re-requested too soon)
- Data cached locally with TTL to minimise API calls
- Direct UEX links for origin terminal, destination terminal, and commodity
  (buying vs selling context)
- All filters, budget cap, vehicle selection, slider value, and default sort
  are **persisted across sessions**

---

## Stale Targets

The **Targets** tab fetches commodity prices from UEX and surfaces the
`(terminal, commodity)` pairs with the oldest data — the submissions that
help the database most.

- Filterable by text, age threshold (> 30 days), BUY/SELL, and with
  unreachable or inaccessible terminals hidden by default
- Right-click → **Open in Trade Routes** pre-fills the origin in the Trade
  Route Planner
- Right-click → **Open on UEX** links to the terminal page

---

## How it works (quick tour for contributors)

### Six tabs in the UI

| Tab | What's in it |
|---|---|
| **Inbox** | Auto-imported screenshots, OCR status, per-item validation editor, batch send, multi-select delete |
| **Targets** | Stale `(terminal, commodity)` pairs from UEX, sortable by age — plan a route |
| **Trade routes** | Live UEX route planner: vehicle filter, 8 pill filters, ETA, profit/min, ★ sort, Trader↔Datarunner slider |
| **History** | Local audit log of every submission (request + response JSON) |
| **Diagnostics** | App version, log viewer, submission inspector, **one-click bug report** to clipboard |
| **Settings** | Secret key, bearer token override, screenshots folder, default mode (production/test), preferences, updates |

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

### Auto-update flow (Velopack)

1. App launches → silent background `CheckForUpdatesAsync` against the
   GitHub Releases feed.
2. If a newer version exists, the status bar shows a small **"Update
   available"** pill. Click it to open Settings.
3. From Settings you choose Download → Apply. The installer streams the
   delta, replaces the binaries on the next restart, no UAC prompt, no
   re-installation, your data dir is preserved.
4. When no update is pending, the current installed version is always visible
   as a quiet label in the centre of the status bar.

You can also force a check from `Settings → Updates`. Velopack handles
rollback if the new build fails to start.

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
  vs. "Stanton Gateway") — confirmed by the `RichDisplayName` warning in
  the editor.
- Commodity name matching: ~95-99% with the length-penalty fuzzy matcher
  protecting against false positives.
- Container sizes: 1, 2, 4, 8, 16, 24, 32 picked up with UNION across rows
  (so the smaller sizes that the OCR sometimes misses on one row are
  recovered from another).
- Status detection (`MAX INVENTORY`, `LOW INVENTORY`, ...) tolerates the
  most common OCR errors (M↔N, 0↔O, period inserted between words). When
  status is `OUT OF STOCK` and SCU isn't recognised, SCU is set to 0
  automatically — a missing 0 is no longer a blocking error.
- Aggressive retry pass (CLAHE clipLimit 4.0, ×3 upscale, unsharp mask)
  re-runs whenever the first pass returned an empty terminal name or any
  Unknown status, and merges results back without overwriting good values.
- **Colour-based BUY/SELL tab detection**: hue/saturation analysis of the
  tab area runs independently of OCR text — this is the most reliable guard
  against the most dangerous class of misreport (sending SELL data as BUY).

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
  the screenshot — toggle the **Side-by-side** screenshot panel in the
  editor for a docked, zoomable view that stays in sync with the form.

---

## Roadmap

### Done

- ✅ End-to-end OCR pipeline with manual review UI
- ✅ DPAPI-encrypted credential storage
- ✅ SQLite submission audit log
- ✅ Velopack silent auto-update + signed installer
- ✅ Five-gate validation pipeline (live editor, static payload, live price-drift, confirmation dialog, batch preview)
- ✅ Colour-based BUY/SELL tab detection (independent of OCR text)
- ✅ Multi-screenshot merge (one terminal, many screenshots → one submission)
- ✅ Built-in app token embedded at build, override possible in Settings
- ✅ Stale-target view (Targets tab) — commodity prices sorted by age, with Trade Routes integration
- ✅ Side-by-side screenshot panel (toggle) + collapsible inbox
- ✅ Diagnostics tab — log viewer, submission inspector, one-click bug report
- ✅ Aggressive OCR retry pass for missing terminal names / Unknown status
- ✅ OutOfStock → SCU=0 inference (no longer a blocking error)
- ✅ Terminal disambiguation across star systems (e.g. Pyro vs. Stanton)
- ✅ **Batch submission** — BatchPlanner (dedup by commodity+tab), mandatory BatchPreviewDialog with JSON per terminal, per-item Gate 2+3, backoff on rate limits, Stop / Retry failed controls
- ✅ **Trade Route Planner** — UEX API integration, cargo vehicle filter, 8 pill filters (Loading, Auto-load, Legal, Monitored, Space, Ground, Refuel, Predicted), Min profit / Min profit-per-minute thresholds, ETA column, aUEC/min column (compact K/M format), ★ favourite sort (persisted), Trader ↔ Datarunner blended scoring slider (persisted), direct UEX links
- ✅ Hard rule: routes with effective profit ≤ 0 always hidden
- ✅ Installed version always shown in status bar (even when no update is pending)
- ✅ All filter, sort, vehicle, budget, and slider preferences persisted across sessions

### Next — extend coverage to other UEX submission types

The UEX `data_submit` endpoint actually accepts **7 categories** of pricing
data via its `type` field. Today we only cover **commodities**. The other
six are submission types where community coverage is much weaker — and
they all use a similar in-game UI we could OCR with the same pipeline.

| Phase | Type | In-game source | OCR effort | Why it matters |
|---|---|---|---|---|
| **🔜 Phase 1** | `item` | Shop terminals (Cubby Blast, Centermass, Garrity, OmegaPro, medical / food vendors, sub-gear bornes) | Low — same SCU/price layout, parser ~90 % reusable | Coverage is **catastrophic** today. Biggest single quality-of-life win for UEX. |
| **🔜 Phase 2** | `fuel` | Refuel terminals (Quantum + Hydrogen) at any station | Low — 2-line panel, trivial parsing | Critical for long-distance route planning; prices vary a lot per station. |
| **🔜 Phase 3** | `vehicle_purchase` + `vehicle_rental` | ASOP terminals (Lorville, Area18, ...) + New Deal Showroom | Medium — different layout, ship list view | Many bornes uncovered; ASOP rental prices barely tracked. |
| **🔜 Phase 4** | `ore` | Refinery sell terminals (ARC-L1, HUR-L1, MAG-L4, CRU-L1, ...) | Medium-Heavy — dense table, multi-column | Direct raw-ore sales; medium coverage today. |

Each phase reuses the existing pipeline (folder watcher, OCR queue,
PaddleOCR, fuzzy matching against the UEX catalog, validation editor,
live diff, audit log) — only the per-type parser, the catalog lookup
(`/items`, `/fuel_prices`, `/vehicles`, ...) and a few new validation
rules need to change.

> Out of scope for this client: **`vehicle_pledge`** prices come from the
> RSI Pledge Store website, not from in-game UI, so they don't match the
> screenshot-driven workflow.

### Also considered (no commitment yet)

- **Refinery yields** (`data_submit_refinery`) — submit refining-job
  results (input ore → output, method, duration). Niche but very valuable
  for miners. Different workflow (track a job over time, not a one-shot
  screenshot).

### Maybe later

- ⏳ GPU edition (PaddleOCR on CUDA) for power users with NVIDIA GPUs.

---

## Reporting bugs

Open the **Diagnostics tab → "Copy bug report"**, then paste into a new
[GitHub issue](https://github.com/Olrik-WP/SC-DataRunnerNet/issues/new).
The clipboard payload contains:

- App version + .NET runtime + OS
- Data folder location and DB size
- Catalog state (commodities/terminals counts, last refresh time)
- Non-secret preferences (your bearer/secret keys are **never** copied)
- Last 5 submissions: terminal, HTTP status, API status, message
- Last 50 lines of the current log file

If you can attach the SC terminal screenshot that triggered the bug,
even better — but the report alone is usually enough to reproduce.

---

## Contributing

PRs welcome. The codebase is heavily commented because OCR is full of
non-obvious trade-offs — every "weird" decision should have a comment
explaining why and what was tried before. Please keep that convention if
you submit changes.

Useful starting points:

- Want to improve OCR accuracy? `src/DataRunner.Ocr/Pipeline/CommodityParser.cs`
  and `src/DataRunner.Ocr/Matching/FuzzyMatcher.cs` are where most of the
  heuristics live.
- Want to add a new validation rule? `ScreenshotEditViewModel.RecomputeValidation`
  is the entry point.
- Want to tweak the screenshot panel UX? `Views/ScreenshotPanel.xaml(.cs)` —
  it powers both the side-by-side view and the standalone window.
- Want to add a Trade Route filter or column? `ViewModels/RoutesViewModel.cs`
  (filter predicate + persistence) and `Views/RoutesView.xaml` (pill toggle
  or column definition).
- Want to change batch behaviour? `Services/BatchPlanner.cs` (deduplication
  logic) and `Services/BatchSubmitter.cs` (execution loop + backoff).

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

## Community

- 🐛 **Bug?** [Open a GitHub issue](https://github.com/Olrik-WP/SC-DataRunnerNet/issues/new)
  with the output of `Diagnostics → Copy bug report`.
- 💬 **UEX datarunner Discord channel** — best place to chat about
  data-quality, terminal coverage, and trade-route planning.
- ⭐ **Star the repo** if it helps you. It's the only way I know the tool
  is useful to people outside my own crew.

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
