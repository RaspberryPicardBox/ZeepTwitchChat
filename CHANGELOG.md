# Changelog

All notable changes to ZeepTwitchChat are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
