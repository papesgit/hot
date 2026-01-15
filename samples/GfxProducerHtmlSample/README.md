# GfxProducerHtmlSample

Renders `Assets/index.html` with CEF off-screen and publishes a shared D3D11 texture
for `mirv_image` to consume.

Usage:
1) Build/run `GfxProducerHtmlSample`.
2) In CS2 console:
   - `mirv_image use gfx1 sample_atlas full`
   - `mirv_image place gfx1 <x> <y> <z>`
   - `mirv_image scale gfx1 64 32`

Notes:
- Uses `BGRA8` with premultiplied alpha.
- Requires the CefSharp runtime files (pulled by NuGet restore).
