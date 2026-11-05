# Changelog

All notable changes to ZeepTwitchChat are recorded here.

> **1.4.0 is the first public release.** The entries below it record the work done before the
> repository was published; they are kept because they explain why the code looks the way it
> does, not because earlier builds were ever distributed.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.0] - 2026-11-05

### Added

- **Deleted messages and bans are mirrored.** When a moderator deletes a message, times a user
  out, bans them, or clears the chat, the matching lines are handled: a line still waiting for
  its emote image is cancelled and never appears, and a line already on screen is removed from the
  game's chat window. Every removal is logged with the line it removed, so a session can be
  audited afterwards.
- `Moderation/MirrorDeletions` (default `true`) and `Moderation/RemoveShownLines` (default
  `true`, which does the on-screen removal; turn it off to only cancel lines not yet posted).
- `ChatLineLedger`, `ChatBufferPlan` and `ChatBufferEditor`, each unit tested where it can be:
  Twitch's `CLEARMSG`/`CLEARCHAT` parsing, cancel-before-post, remove-after-post, per-user and
  chat-wide clears, and which buffer entries a removal applies to.

### How the on-screen removal works

Zeepkist keeps its chat in `receivedChatList` (rendered lines, newest first, twenty max) and
`receivedChatIDs`, and rebuilds both text fields from them on every message. So erasing a line
means dropping that entry from both lists and replaying the survivors through the game's own
`UpdateChatFields(string, ulong)` - oldest first, so the order and the twenty line cap come out
exactly as the game would have made them. Nothing about the game's rendering is re-implemented.
If the replay ever throws, the original entries are put back so a chat window can never be left
empty; only rendered strings this mod recorded are ever named for removal, so lobby chat is not
touched.

### Notes

- Twitch sends `CLEARMSG`/`CLEARCHAT` to moderator and broadcaster connections. This mod reads
  chat anonymously by default, so it may simply never be told; a moderator OAuth token in
  `Twitch/OAuthToken` is what makes the feature do anything.
- AutoMod is unaffected: a held message never reaches chat in the first place, and an approved one
  arrives (and is mirrored) when it is approved.

## [1.3.4] - 2026-11-05

### Security

A full review of what network input can do, written up in [docs/security.md](docs/security.md).
No code execution path was found, and the findings below were closed anyway:

- **Emote urls are now validated.** Twitch emote ids come off the IRC tag and 7TV supplies a
  per-emote host in its json, so urls were only as trustworthy as the remote data: a `file://`
  uri would have made `WebRequest` read a local file. Urls must now be https, on an allow-listed
  host for that purpose, without user info, fragment or `..`, and redirects are disabled.
- **Every growth point has a ceiling:** IRC lines (8 KiB, longer ones skipped rather than
  buffered), provider lists (8 MiB), emote images (4 MiB), GIF frames and decoded pixels, and a
  5000 entry cap on remembered emote names (the IRC emotes tag lets a hostile server invent a name
  per message).
- **Config values are clamped** into the ranges the mod is designed for, so a typo cannot become a
  huge atlas or a stalled frame.
- **Invisible characters are stripped** from untrusted text: bidirectional overrides, zero width
  joiners and the byte order mark could make one name render as another.
- Log lines no longer include a raw login straight off the wire.

### Added

- A security section in the test suite: a hostile corpus (TMP tag injection in the message, the
  display name, the label and the colour) is rendered and every tag in the result is checked
  against the exact set this mod can produce, plus url policy checks for `file://`, `http://`,
  unknown hosts, `user@host` tricks, traversal, `javascript:` and `data:` uris.

## [1.3.3] - 2026-11-05

### Fixed

- **Emotes whose names contain an angle bracket, like Twitch's `<3`, stayed text.** The name rule
  still refused `<` and `>` (a leftover from when a name was written straight into a sprite tag).
  It now only refuses whitespace and control characters, so any punctuation a provider allows can
  be matched.

### Security

- Keeping the raw name out of markup is what makes that safe, and it still holds: the sprite tag
  is built from the aliased glyph name, never the emote's own name. An emote that is not renderable
  (image not in yet, or over the cache cap) falls through to the message sanitiser, which removes
  angle brackets, so `<3` degrades to `3` rather than to markup. A name containing whitespace is
  still rejected, because it could never be matched as one word anyway.

## [1.3.2] - 2026-11-05

### Fixed

- **Emotes whose names contain punctuation were never drawn.** The name whitelist was
  `[A-Za-z0-9_-]`, so BetterTTV emotes like `:tf:`, `D:`, `c!` fell back to their text name, and
  so did 7TV emotes like `:)`. The whitelist is now only "no whitespace, no control characters,
  no angle brackets", because a name is never written into markup (see below).
- **Third-party emotes were not looked up in messages at all.** Only Twitch's own `emotes` tag
  produced emote spans, so a BTTV/7TV/FFZ emote had to also be a native Twitch emote to render.
  Message words are now checked against the known provider names (`EmoteWordScanner`), exactly
  and case sensitively, as those providers themselves do.

### Security

- Emote names are matched, never emitted. A glyph is stored under an alias: names that are
  already tag safe keep their name, anything else becomes `e` plus a stable FNV-1a hash, and a
  collision (or an emote literally named like an alias) gets a numeric suffix. The raw name
  therefore cannot reach TextMeshPro markup whatever a provider calls its emote, which is what
  makes relaxing the whitelist safe. Angle brackets are still stripped from message text.

### Changed

- BetterTTV parsing now keeps 12 more of its global emotes (the punctuated ones) — visible in the
  provider probe.

## [1.3.1] - 2026-11-05

### Fixed

- **Animated emotes showed only their first frame.** The animation advanced a private frame
  counter, but Zeepkist keeps its own twenty line buffer and rebuilds the chat field from those
  stored strings every time a new message arrives. That reset the line to frame 0 while the
  counter kept moving, so the swap could never match again and the emote stayed still for good
  in any chat with traffic. The animator now asks for an absolute frame from the emote's own
  clock and rewrites *any* frame tag to it, so a rebuilt line is corrected on the next tick
  (checked in the shipped `Zeepkist.dll`: `OnlineChatUI.UpdateChatFields` writes
  `bigChatField.text` / `smallChatField.text` from `receivedChatList`; `Update` only touches the
  input boxes).
- Animated emotes no longer depend on a provider's `animated` flag. A GIF that actually carries
  several frames is animated, whatever the list said; the flag now only keeps animations out of
  the pre-warm pass. A missing or wrong flag can no longer cost the animation.
- The `emote animation active` log line could be skipped entirely: it read the animator's frame
  list, which is only refreshed on the animator's next tick, so it was empty at the moment the
  atlas was rebuilt.

### Added

- `emote decoded <name>: N frame(s)` for the first few emotes of each kind, and
  `emote animation is rewriting the chat text` the first time a frame swap lands. Both only ever
  print once or twice, and they make a log enough to tell "no frames" from "frames but no
  rewrite".

## [1.3.0] - 2026-11-05

### Added

- **Animated emotes.** Animated 7TV and BetterTTV emotes now play frame by frame. Zeepkist
  stores rendered chat text, so there is nothing to animate directly: every GIF frame becomes
  its own sprite glyph and the visible lines have their tags rewritten. Each emote plays at
  the speed its GIF declares; `Emotes/AnimationFps` (15 by default) only sets how smoothly
  that is sampled.
- **7TV support.** 7TV was previously unsupported because its CDN serves WebP/AVIF/GIF; it also
  offers a GIF of every emote, so the mod now reads that instead (global and channel sets).
- A dependency-free GIF decoder (`Util/GifDecoder.cs`): LZW, transparency, frame delays and
  disposal, verified byte for byte against PIL on real 7TV files.
- `Emotes/EnableSevenTv`, `AnimateEmotes`, `AnimationFps`, `MaxAnimationFrames` and
  `MaxAnimatedEmotes`.

### Changed

- BetterTTV emotes reported as `imageType: gif` (its animated ones) are no longer skipped.
  WebP remains the one container that is dropped, since nothing in the mod decodes it.
- Emote log lines now report how many provider emotes are animated, and say when animation is
  active.

### Notes

- FrankerFaceZ animated emotes are drawn as their first frame: FFZ has no GIF endpoint.
- Animated emotes are never pre-warmed (an animation is many frames to decode), so their first
  use relies on the `EmoteWaitSeconds` window like a native Twitch emote does.

## [1.2.0] - 2026-11-05

### Fixed

- **First use of an emote still showed its text name.** The sprite pipeline was working
  (`source=EmojiOne; shader=TextMeshPro/Sprite`, glyphs resolvable, asset attached), but a line
  is formatted the moment it arrives, while the emote image still needs a few hundred
  milliseconds — and Zeepkist stores the *rendered string* in its twenty-line buffer, so that
  line kept the plain name for as long as it was on screen. Emote-bearing lines are now held
  until their images are drawable, or until `Emotes/EmoteWaitSeconds` expires.

### Added

- `Emotes/EmoteWaitSeconds` (default `3`): how long a line may wait for its emote images. `0`
  restores the old behaviour of writing the name immediately.
- `Emotes/PrewarmEmotes` (default `true`): downloads BetterTTV and FrankerFaceZ emote images in
  the background after connecting (a few per frame), so their first use needs no wait. Native
  Twitch emotes cannot be pre-warmed — their ids are only known from the messages themselves —
  so those rely on the wait window.
- `DeferredChatQueue` with unit tests: release-when-ready, release-on-timeout, oldest-first
  ordering, and a bounded backlog of 20 lines.

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
