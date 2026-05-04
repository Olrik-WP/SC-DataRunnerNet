# Documentation images

Drop README screenshots / GIFs here. They are referenced from the root
`README.md` via relative paths, e.g.:

```md
![Inbox view](docs/images/inbox.png)
```

## Conventions

| Filename                  | What it should show                                       |
|---------------------------|-----------------------------------------------------------|
| `hero.png`                | Wide shot of the main window — Inbox + editor side-by-side|
| `inbox.png`               | The Inbox tab with a few cards (Ready / Review / Pending) |
| `editor-side-by-side.png` | The validation editor with the screenshot panel docked    |
| `targets.png`             | The Targets tab (stale-target view)                       |
| `diagnostics.png`         | The Diagnostics tab with the bug-report button            |
| `wizard.png`              | The first-run wizard (step 1 — secret key)                |

## Recommended specs

- **Format**: `.png` for static UI shots, `.gif` for short workflows
  (≤ 8 s, ≤ 5 MB).
- **Resolution**: capture at 1080p or 1440p; GitHub will downscale anyway.
- **Window chrome**: include the Mica title bar — it's part of the design.
- **Privacy**: blur or crop out your UEX secret key, screen names, etc.

## How to add one

1. Save your image here under the conventional name (or invent a new one).
2. Reference it from the root `README.md` with a relative path:
   `![Alt text](docs/images/your-image.png)`.
3. Open a PR — that's it.
