# ZeepTwitchChat

Your **Twitch chat, inside Zeepkist's in-game chat** — colour-coded, labelled `[TWITCH]`,
and shown **only to you**.

Zeepkist's chat window is where the lobby talks during a race. This mod mirrors your own
Twitch channel into that same window, so you can read chat without a second monitor,
without an overlay, and without anything appearing on stream. Nothing is ever sent to the
lobby: the lines are written straight into your local chat UI.

```
[TWITCH] [HOST] SomeStreamer: race starts in 30
[TWITCH] [SUB]  a_viewer: that shortcut was criminal
[TWITCH] [MOD]  a_mod: !clip
```

| Part | What it is |
| --- | --- |
| `[TWITCH]` | Label in Twitch purple, so a Twitch line can never be mistaken for a lobby message |
| `[HOST]` `[MOD]` `[VIP]` `[SUB]` | Optional badges, colour-coded, from Twitch's own tags |
| Name | In the colour the chatter picked in Twitch (or a stable colour derived from their name) |
| Message | The message text, with any rich-text markup stripped |

---

## What it does and does not do

**Does**

- Reads your channel with Twitch's IRC interface over TLS — read-only, no OAuth token required.
- Writes each message into the game's own chat window with TextMeshPro rich text, exactly the
  way the game renders its own chat lines.
- Rate-limits and truncates so a fast channel cannot flood the chat.
- Reconnects on its own if the connection drops.

**Does not**

- Send anything to the lobby, to Photon, or to Twitch. You cannot chat back through the game.
- Show messages when you are not in an online lobby: that is the only place Zeepkist has a
  chat window.
- Show avatars or emotes; `/me` lines are shown with a leading `* `.

---

## Requirements

| Item | Notes |
| --- | --- |
| Zeepkist | Steam appid `1440670` |
| BepInEx | 5.4.x x64 (Mono). Installed by Modkist, or manually |
| .NET | Nothing to install; the mod targets `net472` like every other Zeepkist mod |

No other mod is required. It does not depend on ZeepSDK, so it keeps working even when the
SDK's chat binding breaks against a new game build (see [docs/design.md](docs/design.md)).

## Install

### Modkist (recommended)

1. Install [Modkist](https://github.com/Thundernerd/ModkistMKII) and point it at your
   Zeepkist folder.
2. Find **Twitch Chat** in the mod.io browser and press install.

### Manual

1. Install BepInEx 5.4.x x64 into your Zeepkist folder if you have not already.
2. Copy `ZeepTwitchChat.dll` into `BepInEx/plugins/`.
3. Start the game once. The mod creates
   `BepInEx/config/com.nyxeonfox.zeepkist.twitchchat.cfg`.

## Configure

Open `BepInEx/config/com.nyxeonfox.zeepkist.twitchchat.cfg` (or use Configuration Manager
in game) and set **at least** your channel:

```ini
[Twitch]
## Twitch channel to read, for example 'nyxeonfox'. No '#', no twitch.tv link.
Channel = nyxeonfox
```

Changes are applied immediately: the mod reconnects with the new settings.

### All settings

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| Twitch | `Enabled` | `true` | Turn the mod off without uninstalling it |
| Twitch | `Channel` | *(empty)* | Your channel login; empty means "do not connect" |
| Twitch | `OAuthToken` | *(empty)* | Optional `chat:read` token. Anonymous read-only works without it |
| Display | `ShowLabel` | `true` | Show the `[TWITCH]` label |
| Display | `LabelText` | `TWITCH` | Label text; empty hides it |
| Display | `LabelColour` | `#9147FF` | Label colour (Twitch purple) |
| Display | `UseTwitchColours` | `true` | Use the chatter's Twitch colour, else a stable derived colour |
| Display | `ShowBadges` | `true` | Show `[HOST]` `[MOD]` `[VIP]` `[SUB]` |
| Limits | `MaxMessagesPerMinute` | `30` | Insertion budget; `0` = unlimited |
| Limits | `MinimumSecondsBetweenMessages` | `0.75` | Hard floor between insertions; `0` = none |
| Limits | `MaxMessageLength` | `180` | Longer messages are truncated |
| Advanced | `IrcHost` | `irc.chat.twitch.tv` | Twitch IRC host |
| Advanced | `IrcPort` | `6697` | `6697` = TLS, `6667` = plain text |
| Advanced | `UseTls` | `true` | Encrypt the connection |
| Advanced | `IgnoreCertificateErrors` | `true` | Tolerate a stale Mono trust store (common in Proton prefixes) |

## Things worth knowing

- **Only in an online lobby.** `OnlineChatUI` is part of the online gameplay scene. In free
  play or the menus there is no chat window, and the mod drops messages (it logs that at
  most once every 30 seconds).
- **The window is small.** Zeepkist keeps 20 chat lines and shows five in the compact view,
  so a busy channel scrolls quickly. The rate limit exists for that reason; 30 per minute
  is a sane default for reading chat mid-race.
- **Twitch lines have no Steam link.** Real lobby messages are wrapped in a `<link>` to the
  player's Steam profile; Twitch lines are written without one, so clicking them does
  nothing. That is deliberate: a Twitch name is not a Steam id.
- **Chat is public.** The connection is anonymous and read-only, and every message read is
  already public. TLS is on by default; the certificate-tolerance option exists only
  because Mono's trust store inside Proton prefixes is often stale.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Nothing appears, log says "no channel configured" | Set `Twitch/Channel` |
| Nothing appears, no log about the channel | You are not in an online lobby |
| Auth errors | Clear `OAuthToken`, or check that it has the `chat:read` scope |
| TLS errors | Set `Advanced/UseTls = false` (chat is public either way) |
| Chat scrolls too fast | Lower `Limits/MaxMessagesPerMinute` |
| Log says `UpdateChatFields ... not found` | A game update changed the chat UI; please open an issue |

Log: `BepInEx/LogOutput.log`.

## Development

```bash
# Build the plugin (needs a local Zeepkist + BepInEx install; see Directory.Build.props)
dotnet build plugin/ZeepTwitchChat.Plugin -c Release

# Logic checks: parser, formatter, rate limiter (no game needed)
dotnet run --project tests/ZeepTwitchChat.LogicTests -c Release

# Live probe: joins a real channel and prints the formatted TMP lines
dotnet run --project tests/ZeepTwitchChat.LogicTests -c Release -- --live nyxeonfox 20

# Copy the DLL into the game, or build a zip for mod.io
tools/deploy-plugin.sh
tools/package-mod.sh
```

Game and BepInEx assemblies are referenced in place via `HintPath` and are never committed
or redistributed; only `ZeepTwitchChat.dll` is shipped.

## Verified facts and design notes

Everything this mod relies on was checked against the installed game build rather than
assumed — the private chat method, the rich-text format the game uses, the twenty-line
buffer, and the CTCP framing Twitch uses for `/me`. See [docs/design.md](docs/design.md).

## Licence

MIT. See [LICENSE](LICENSE).
