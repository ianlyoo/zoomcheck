# Owner Actions — Social Preview and Profile

## 1. Repository social preview (Settings → General → Social preview)

- **Committed image:** `docs/assets/social-preview.png` — 1280×640 PNG, <1MiB, solid `#0f172a` background, high contrast, deterministic SHA (rebuild via `node scripts/build-social-preview.mjs`).
- **Pages canonical URL:** `https://ianlyoo.github.io/zoomcheck/assets/social-preview.png`
- **Remaining owner step:** Repository → **Settings → General → Social preview → Edit → Upload an image** → upload `docs/assets/social-preview.png`.

### Verification

```bash
curl -fsSL https://ianlyoo.github.io/zoomcheck/ | grep -o 'og:image[^>]*content="[^"]*"'
curl -fsSL https://ianlyoo.github.io/zoomcheck/assets/social-preview.png -o /tmp/social.png
python -c "import struct; b=open('/tmp/social.png','rb').read(); print(struct.unpack('>II', b[16:24]))"
```

## 2. Optional: Profile pin

- Pin the repository to your GitHub profile: Profile → **Customize your pins** → select `ianlyoo/zoomcheck`.
