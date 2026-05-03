---
paths:
  - "scripts/**"
  - "src/livepaper/Assets/**"
---

## Distribution

### Scripts (`scripts/`)
- `install.sh` — self-contained single binary (`PublishSingleFile=true`), installs to `~/.local/bin/`, desktop entry to `~/.local/share/applications/`, icon to `~/.local/share/icons/hicolor/512x512/apps/`
- `build-appimage.sh` — same build, packages into `livepaper-x86_64.AppImage` via `appimagetool` (auto-downloaded if not in PATH)
- `PKGBUILD` + `.SRCINFO` — AUR package `livepaper-git` at `aur.archlinux.org/packages/livepaper-git`

When updating AUR:
```bash
cd scripts && makepkg --printsrcinfo > .SRCINFO
cp PKGBUILD .SRCINFO /tmp/aur-livepaper/
cd /tmp/aur-livepaper && git add PKGBUILD .SRCINFO && git commit -m "..." && git push
```

### Assets (`src/livepaper/Assets/`)
- `livepaper.svg` — source icon (monitor + play button, transparent background)
- `livepaper.png` — 512×512 PNG exported from SVG

To regenerate PNG after editing SVG:
```bash
rsvg-convert -w 512 -h 512 src/livepaper/Assets/livepaper.svg -o src/livepaper/Assets/livepaper.png
```
