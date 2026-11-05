# Changelog

All notable changes to ZeepTwitchChat are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.1] - 2026-11-05

### Fixed

- **Emotes never rendered** (`NullReferenceException` logged as "could not build the emote
  atlas"). TMP's `UpdateLookupTables` calls its legacy `UpgradeSpriteAsset` whenever
  `material != null` and the asset's version string is empty — and that method walks
  `spriteInfoList`, which is null on a runtime-created asset. It also cleared the glyph tables
  that had just been filled. Verified against the shipped `Unity.TextMeshPro.dll`. The asset is
  now marked as current-version, given an empty `spriteInfoList`, and only receives its
  material after the lookup tables are built.
- The emote sprite asset is now also attached to the chat's own text components
  (`bigChatField`/`smallChatField`) when they have no sprite asset, because a text with a null
  `spriteAsset` resolves no sprite tags at all. Emote tags use the plain
  `<sprite name="...">` form so they resolve through the asset the chat actually uses, rather
  than TMP's separate asset-name registry.
- Rebuilds swap the previous atlas for the new one in place (keeping the old asset as a
  fallback) so emote glyphs added later are visible instead of pinning the first atlas.
- Building an asset without the game's default sprite asset no longer fails: the material is
  cloned when possible, otherwise built from `TextMeshPro/Sprite`, and face metrics are derived
  from the configured emote size.

### Changed

- Rebuild failures log the exception type, message and first stack frame, and a successful
  rebuild reports the sprite source, shader and how many chat fields were attached — enough to
  diagnose any future TMP change from `LogOutput.log` alone.
- A provider that has no emotes for a channel (HTTP 404) is reported as "no emotes registered"
  instead of looking like a network failure.

## [1.1.0] - 2026-11-05

### Added

- **Emotes as images.** Native Twitch emotes (from the IRC `emotes` tag), plus BetterTTV and
  FrankerFaceZ emotes (global and channel), are downloaded on first use and drawn as sprites
  in the chat line instead of their text names.
  - A sprite asset is built at runtime and registered as a fallback of the game's default
    sprite asset, which is the path `TMP_SpriteAsset.SearchForSpriteByHashCode` walks.
  - An emote is only turned into a sprite once TMP can resolve its glyph; until then, and for
    anything that cannot be decoded, the plain name is shown. A missing image never leaves a
    gap in the line.
  - New `[Emotes]` settings: `RenderEmotes`, `EnableBttv`, `EnableFfz`, `MaxCachedEmotes`,
    `EmoteSize`.
- Test harness coverage for the emote tag parser, the provider JSON parsers, the JSON reader
  and emote substitution/truncation (now 91 offline checks).
- `--providers` probe that runs the parsers against the live BTTV and FFZ APIs.

### Changed

- Message assembly now enforces the length budget while writing, so truncation can never cut
  a TextMeshPro tag in half.
- Sliding-window truncation applies to the message body only; the label, badges and name are
  never truncated by it.

### Notes

- 7TV is deliberately **not** supported: its CDN serves WebP/AVIF/GIF only, and Unity's
  `Texture2D.LoadImage` decodes PNG/JPG only. Shipping it would mean bundling a decoder.
- BetterTTV emotes whose `imageType` is not PNG (animated ones) are skipped for the same
  reason and keep their text name.

## [1.0.0] - 2026-11-05

First release.

### Added

- Twitch chat mirrored into Zeepkist's in-game chat window, locally only: nothing is sent
  to the lobby or the network (`OnlineChatUI.UpdateChatFields` is invoked with steam id 0 so
  Twitch lines render without a clickable Steam profile link).
- `[TWITCH]` label in Twitch purple, plus `[HOST]`, `[MOD]`, `[VIP]` and `[SUB]` badges.
- Chatter names coloured with their Twitch colour, with a stable derived colour as fallback.
- Read-only anonymous Twitch IRC client over TLS with automatic reconnection and backoff.
- IRCv3 tag parsing (colour, display name, badges, mod flag) and CTCP `/me` handling.
- Rich-text sanitiser: angle brackets stripped from every untrusted fragment, control
  characters removed, whitespace collapsed, messages truncated.
- Rate limiting (30 lines per minute, 0.75s floor by default) and a bounded 40-line queue
  so a busy channel cannot flood the chat window.
- Full configuration surface (channel, token, host/port/TLS, colours, badges, limits) with
  live reconfiguration.
- Test harness with 62 offline checks plus a live IRC probe.
- `tools/deploy-plugin.sh` and `tools/package-mod.sh` for local testing and mod.io uploads.
