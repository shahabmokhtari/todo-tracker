# One Thing website

The public site at <https://onething.awesomehq.app>. Plain HTML and CSS with a little JavaScript, no build step.
It's served by Cloudflare Workers static assets (the Worker is named `onething-site`).

| Path | What |
| --- | --- |
| `public/index.html` | The page |
| `public/styles.css` | All the styles (light and dark follow the system) |
| `public/main.js` | The small "Do this now" demo card in the hero (the page works without it) |
| `public/404.html` | Shown for any path that doesn't exist |
| `public/privacy.html` | Privacy: what Send feedback sends and where (the same as `docs/privacy.md`); served at `/privacy` |
| `public/img/` | Favicon, Open Graph image (`og.png`) and screenshots (WebP copies of `docs/images/*.png`), all made by tools (see below) |
| `public/_headers` | Security headers and caching |
| `public/robots.txt`, `public/sitemap.xml` | For search engines |
| `wrangler.jsonc` | Worker name, the assets folder and the custom domain. No account id or token. |

## Preview locally

```bash
cd website/public && python3 -m http.server 8080      # then open http://localhost:8080
# or, closer to production (404 page and _headers applied):
cd website && npx wrangler dev
```

## Deploy

Set a Cloudflare API token in your shell (it needs *Workers Scripts: Edit* and, for the custom domain, *Workers
Routes: Edit* and *Zone: Read* on `awesomehq.app`), then deploy:

```bash
cd website
export CLOUDFLARE_API_TOKEN=...          # never commit it
export CLOUDFLARE_ACCOUNT_ID=...         # only if the token can see more than one account
npx wrangler deploy
```

Deploying attaches `onething.awesomehq.app` and the old `todotracker.awesomehq.app` as custom domains (Cloudflare creates
the DNS records and certificates); `src/worker.js` sends the old address to the same page at the new one. `npx wrangler deploy --dry-run` checks the config without uploading.

## Updating screenshots

Every picture here is made by a tool, from a made-up demo board:

- `node tests/e2e/screenshots.mjs` (in `tests/e2e`, after `npm install` and `npx playwright install chromium`)
  saves the screenshots to `docs/images/`, draws `og.png` (the link preview: the name, the icon and the Today view),
  and writes the WebP copies here (at most 1600 wide, quality 82, encoded by the browser, so no `cwebp` needed). It
  also keeps the `width` and `height` of those pictures in `index.html` right. `--web-only` skips the Windows
  sidebar; `--only today-light,og` makes just those.
- The iPhone pictures (`macos-ios-light`/`-dark`) come from the **Screenshots** workflow
  (`.github/workflows/screenshots.yml`, run it by hand or push to a branch named `screenshots`): unzip its artifact
  at the root of the repository. To redo only their WebP copies:
  `node tests/e2e/screenshots.mjs --webp-only --only macos-ios-light,macos-ios-dark`.
- `favicon.svg` and `apple-touch-icon.png` come from `build/icons.py`, with the app icon.

## Words

Same voice as the app: plain, short, friendly, second person. Only say what the docs in `docs/` say the app does,
and mark anything not shipped yet as coming soon.
