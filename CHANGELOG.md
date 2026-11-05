# Changelog

All notable changes to ZeepTwitchChat are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
