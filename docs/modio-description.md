# mod.io listing copy

Use this to fill in the mod.io page for ZeepTwitchChat.

## Name

`Twitch Chat`

## Summary (short description, ~200 chars)

Shows your own Twitch chat inside Zeepkist's in-game chat, colour-coded and labelled
[TWITCH] so it is never confused with lobby chat. Local only: nothing is sent to the lobby.

## Description

**Read your Twitch chat without leaving the game.**

ZeepTwitchChat mirrors your own channel into Zeepkist's chat window. Each line gets a
Twitch-purple `[TWITCH]` label, the chatter's name in their own Twitch colour, and optional
`[HOST]` `[MOD]` `[VIP]` `[SUB]` badges — so a Twitch message can never be mistaken for a
lobby player talking.

```
[TWITCH] [HOST] SomeStreamer: race starts in 30
[TWITCH] [SUB]  a_viewer: that shortcut was criminal
```

### Local only

Messages are written into your own chat UI and never sent to the lobby, so nobody else in
the race sees them and you cannot accidentally talk to the lobby with a Twitch message.

### Setup

1. Install BepInEx 5.4.x x64 (Modkist does this for you).
2. Install this mod.
3. Set your channel in `BepInEx/config/com.nyxeonfox.zeepkist.twitchchat.cfg`:

```ini
[Twitch]
Channel = your_twitch_name
```

No OAuth token, account or extra software is needed: the mod reads chat anonymously and
read-only over Twitch's IRC interface, encrypted with TLS.

### Features

- Colour-coded, labelled lines in the game's own chat window
- Twitch colours and moderator/subscriber/VIP/host badges
- Rate limiting and truncation so a busy channel cannot flood the 20-line chat buffer
- Automatic reconnection
- Everything configurable in-game (Configuration Manager) or in the config file
- Works without ZeepSDK, so it does not break when the SDK's chat binding does

### Notes

- Chat is only visible in **online lobbies** — that is the only place Zeepkist has a chat
  window.
- Requires BepInEx. Tested on the current Steam build with Proton on Linux.

## Tags

`chat`, `twitch`, `streaming`, `utility`, `client`

## Changelog

See `CHANGELOG.md`.
