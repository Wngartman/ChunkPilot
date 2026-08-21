# WebUI assets and licenses

The WebUI bundles no remote assets, competitor artwork, Minecraft promotional art, analytics, or CDN content. The application shell uses the existing repository-owned Lift brand assets at `public/brand/chunkpilot-24.png` and `public/brand/chunkpilot-64.png`; the fixture-only crop source is derived from the same first-party 256 px asset. Server atmosphere is generated from local CSS and confirmed server metadata.

Inter Variable 5.3.0 is bundled locally from `@fontsource-variable/inter` under the SIL Open Font License 1.1. Only the Latin weight-variable WOFF2 is packaged; unsupported glyphs fall back to Segoe UI Variable/Segoe UI so broad Unicode server names remain readable. No font is requested from the network.

The Minecraft server-list preview uses only the Windows-local Cascadia Mono/Consolas/system monospace stack and explicitly identifies itself as an approximation. It bundles no Minecraft font or artwork.

Modrinth project icons are runtime provider metadata, not bundled application assets. They are loaded
only on demand through the native allowlisted image bridge, size/dimension checked, resized and
re-encoded before WebView2 receives them. Offline or rejected images fall back to the local pack icon.

Player heads are also runtime identity data, not bundled assets. The native bridge resolves a known
online player's authoritative Mojang session profile, accepts only the signed texture URL for
`textures.minecraft.net`, downloads and decodes a bounded image, composites the face and hat layers,
and returns a small local data URL to the WebUI. Results are held in a bounded in-memory cache for the
current process. Missing, legacy, invalid, offline, or rejected identities use the local initials
fallback; ChunkPilot never sends player identity to a third-party avatar service.

Runtime frontend packages are React, React DOM, Zustand, TanStack Virtual, Lucide React, Radix Dropdown Menu, and the locally packaged Inter font asset. Build/test packages are TypeScript, Vite, Vitest, Testing Library, user-event, and jsdom. Exact versions and integrity hashes are pinned by `package-lock.json`; third-party notices must be produced from that lock before release acceptance. These packages use permissive MIT licenses; Inter remains SIL OFL 1.1. Microsoft WebView2 uses the official stable NuGet package and the installed Evergreen Runtime. No fixed WebView2 runtime is bundled.
