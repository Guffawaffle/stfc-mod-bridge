# STFC Mod Bridge artwork

`stfc-mod-bridge.png` is the canonical application-icon master. Its console-core
mark is derived from the circular control surface in
`portfolio/stfc-mod-bridge-banner.png`, giving Bridge an identity distinct from
the STFC community mod's macOS launcher artwork.

## Application-icon contract

- dark navy rounded-square tile with transparent outer corners;
- segmented console rings, cyan through blue to violet;
- one central hexagonal connection hub with a luminous core;
- strong silhouette at 16, 24, and 32 pixels;
- no text, Starfleet delta, targeting reticle, wrench, ship silhouette,
  franchise insignia, orange substrate, or fine HUD clutter.

The production master was created with Codex's built-in image-generation path
from the approved console-core concept and canonical banner. The final pass
preserved the ring composition and palette, removed the reticle spokes,
simplified the center into a connection hub, and placed the rounded tile on a
flat chroma key. Codex's installed image-generation helper removed that key to
produce the alpha-bearing PNG.

Derived Windows assets are committed in:

- `src/STFCCommunityMod.Launcher/Assets/stfc-mod-bridge.ico` for the executable,
  taskbar, window, and task switcher;
- `packaging/windows/Assets/` for MSIX and App Installer surfaces.
