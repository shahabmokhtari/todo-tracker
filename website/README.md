# Todo Tracker website

The public site at <https://todotracker.awesomehq.app>. Plain HTML and CSS with a little JavaScript, no build step.
It's served by Cloudflare Workers static assets (the Worker is named `todotracker-site`).

| Path | What |
| --- | --- |
| `public/index.html` | The page |
| `public/styles.css` | All the styles (light and dark follow the system) |
| `public/main.js` | The small "Do this now" demo card in the hero (the page works without it) |
| `public/404.html` | Shown for any path that doesn't exist |
| `public/img/` | Favicon, Open Graph image (`og.png`) and screenshots (WebP copies of `docs/images/*.png`) |
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

The first deploy attaches `todotracker.awesomehq.app` as a custom domain (Cloudflare creates the DNS record and the
certificate). `npx wrangler deploy --dry-run` checks the config without uploading.

## Updating screenshots

The screenshots come from `docs/images/` (made by `node tests/e2e/screenshots.mjs` from a made-up demo board).
After they change, make the WebP copies again, for example:

```bash
cwebp -q 82 -resize 1600 0 docs/images/today-light.png -o website/public/img/today-light.webp
```

`og.png` is the top 1200×630 of `today-light.png` scaled to 1200 wide (`sips -Z 1200`, then `sips -c 630 1200`).

## Words

Same voice as the app: plain, short, friendly, second person. Only say what the docs in `docs/` say the app does,
and mark anything not shipped yet as coming soon.
