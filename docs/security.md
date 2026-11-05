# Security review

Scope: ZeepTwitchChat 1.3.4, audited against the question *can anything arriving from the
network cause code to run inside Zeepkist?* The short answer is no, and this document shows
what was checked, what was changed to make it true, and what risks remain.

## Threat model

Untrusted input, in order of how much of it an attacker controls:

| Input | Who controls it | Reaches |
| --- | --- | --- |
| Chat message text | any chatter, via the channel | chat line body |
| IRC tags (`display-name`, `color`, `badges`, `emotes`, `login`) | the Twitch IRC server (or a MITM if TLS is broken) | name, colour, badges, emote ids |
| Provider lists (BTTV / FFZ / 7TV JSON) | the provider, or anyone who can impersonate it | emote names, emote **urls** |
| Emote images (PNG/GIF bytes) | the provider CDN | the image decoder |
| Config file | the user (accidents, not attacks) | sizes, limits, host names |

Assets worth protecting, in order: the integrity of the game process (code execution), the
stability of the game (a crash is a failed session), the user's machine (local file reads), and
the integrity of what other players appear to say.

## Verdict

| Risk | Finding |
| --- | --- |
| Code execution from a message | **Not possible.** No dynamic loading, no deserialisation, no scripting, no process spawn, no reflection on attacker-supplied names. See *Code execution surface*. |
| Code execution from a provider response | **Not possible.** Same reasons; JSON is parsed by a hand-written depth-capped reader into plain dictionaries and lists. |
| Rich text (TMP) injection | **Prevented structurally**, and regression-tested with a tag allow list. See *Markup injection*. |
| Local file read via a crafted URL | **Fixed.** Emote urls are https-only, host allow-listed, redirect-disabled (see *Urls*). |
| Memory exhaustion / crash | **Bounded** at every growth point (line, JSON, image, frame, atlas, queue, name registry). |
| Name spoofing | **Hardened**: bidi overrides and zero width characters are removed from untrusted text. |
| Reading chat (privacy) | Read-only and anonymous; nothing is ever sent except IRC login/PONG and the JOIN. |

## Code execution surface

The .NET mechanisms that can turn data into code, and what this mod actually contains:

| Mechanism | Present? | Evidence |
| --- | --- | --- |
| `Assembly.Load` / `LoadFrom` / `AssemblyBuilder` | no | grep over all sources |
| `Type.GetType(string)` / `Activator.CreateInstance` | no | grep |
| Reflection with attacker-chosen type or member names | no | the only `MethodInfo` is `AccessTools.Method(typeof(OnlineChatUI), "UpdateChatFields", new[] { typeof(string), typeof(ulong) })` - type and name are compile-time constants |
| Harmony patch targets from data | no | `[HarmonyPatch(typeof(OnlineChatUI), "Awake")]` and the method above; both literal |
| `BinaryFormatter` / `XmlSerializer` / `DataContractSerializer` / Json.NET type handling | no | grep; JSON goes through `Util/MiniJson.cs` |
| JSON that builds objects | no | MiniJson returns `Dictionary<string, object>`, `List<object>`, `string`, `double`, `bool` only |
| Scripting (`Lua`, `JavaScript`, `CSharpCodeProvider`) | no | grep |
| `Process.Start` / shell | no | grep; the only `StreamWriter` is the IRC socket |
| File writes | no | grep; the mod writes nothing but BepInEx config values it owns |
| `DllImport` / native code | no | grep |
| Dynamic invocation of Unity events (`SendMessage`, `UnityEvent.Invoke`) | no | the chat window is called through the fixed method above |

What a message *can* do is reach `OnlineChatUI.UpdateChatFields(string, ulong)` - the same entry
point the game uses for its own chat lines - with a string this mod built itself and steam id `0`
(so no clickable Steam profile is created). That is a data write, not execution.

## Markup injection

The chat field is TextMeshPro rich text, so the real risk class is markup: `<size=200>` to wreck
the layout, `<link>` to create clickable regions, `<sprite=...>`/`<font=...>` to pull in other
assets. Three independent layers stop it:

1. **Untrusted text never contains `<` or `>`.** `ChatLineFormatter.SanitiseText` and the message
   body loop drop both characters, along with control characters, bidi overrides and zero width
   characters.
2. **Emote tags are built from aliases.** A spoken emote name is only ever *matched*; the glyph
   stored in the atlas, and therefore the string in `<sprite name=...>`, is an alias
   (`[A-Za-z0-9_-]{1,48}`, or `e` + eight hex digits for names with punctuation, plus `~N` for
   animation frames). A provider cannot name an emote into markup.
3. **The finished line is checked against an allow list in the tests.** `SecurityChecks` scans
   every `<...>` in the output of a hostile corpus and fails unless the tag is exactly one of
   `<b>`, `</b>`, `</color>`, `<color=#RRGGBB>` (validated hex) or `<sprite name="glyph">` with a
   glyph-safe name. The corpus includes `<sprite name="Kappa">` typed into chat, `<sprite=...
   name="...">`, `</b></color><size=200>`, `<link="javascript:...">`, `<font>`/`<material>`,
   `<align>/<page>/<style>`, `<<<>>>`, an unterminated `<`, and the same attacks in the display
   name, the label and the colour.

## Urls

Emote urls are partly attacker-influenced: Twitch emote ids arrive in the IRC tag, provider ids
come from JSON, and 7TV supplies a **host** per emote. `WebRequest` would happily read a
`file://` uri, so `Chat/Emotes/EmoteUrlPolicy.cs` gates every fetch:

* scheme must be `https` (protocol relative `//host/path` is normalised to https),
* host must be on the list for that purpose - images: `static-cdn.jtvnw.net`,
  `cdn.betterttv.net`, `cdn.frankerfacez.com`, `cdn.7tv.app`; lists: `api.betterttv.net`,
  `api.frankerfacez.com`, `7tv.io`,
* no user info, no fragment, no `..` anywhere (checked before parsing, because `Uri` normalises
  dot segments away),
* 512 character ceiling, and `AllowAutoRedirect = false` so a 30x cannot move the request
  off the allow list.

Twitch emote ids additionally go through `EmoteUrlPolicy.IsSafeId` (alphanumeric, 32 max) before
a url is built. Tests cover `file://`, `http://`, `ftp://`, `javascript:`, `data:`, an unknown
host, `cdn.betterttv.net@evil.example.com`, traversal, fragments, an empty url and an over-long
one - all refused - and the four real CDNs, all accepted.

## Resource limits

Every point where remote data grows something has a ceiling. These are the numbers, all
constants in code (config values are clamped into the same ranges):

| Limit | Value | Where |
| --- | --- | --- |
| IRC line length | 8 KiB, longer lines skipped | `TwitchIrcClient.ReadLineCapped` |
| Provider list size | 8 MiB (a busy 7TV set is ~2.4 MB) | `EmoteService.MaxJsonCharacters` |
| Emote image size | 4 MiB | `EmoteImageStore.MaxImageBytes` |
| GIF canvas | 512 x 512 | `GifDecoder.MaxPixels` |
| GIF frames | 512, and 32 MiB decoded total | `GifDecoder.MaxFrames`, `MaxDecodedBytes` |
| Remembered emote names | 5000 | `EmoteService.MaxKnownNames` |
| Emote atlas | 200 emotes, 40 animated, 24 frames each | config, clamped |
| Queued chat lines | 40, then oldest dropped | `ChatInjector.MaxQueuedLines` |
| Lines held for emote images | 20, 30 s at most | `DeferredChatQueue` |
| Messages per minute | configurable, 600 max | `MessageRateLimiter` |
| JSON nesting | 64 levels | `MiniJson.MaxDepth` |
| Network timeouts | 10 s connect and read | both fetchers |

The name registry cap matters: the IRC `emotes` tag lets a hostile server invent a new emote name
on every message, which would otherwise grow a dictionary without limit.

## Privacy

The mod reads chat anonymously (`justinfanNNNNN`) with no OAuth token by default, sends nothing
but `CAP REQ`, `NICK`, `JOIN` and `PONG`, never posts to the channel, writes no files, and has no
telemetry. Lines are rendered locally with steam id `0`, so Twitch lines are not clickable and do
not link to anyone's profile.

## Residual risks

* **TLS trust.** `Twitch/IgnoreCertificateErrors` (default `false`) would let a MITM into the IRC
  stream. The audit assumes it stays off; even if it is on, the impact is bounded by everything
  above (markup can still not be injected, urls are still allow-listed).
* **A bug in our own tag construction** would be the only remaining route to markup injection;
  that is exactly what the allow-list test exists to catch, and it fails the build if it ever
  happens.
* **Hostile images** (a compromised CDN) are decoded by a bounded, hand-written decoder that
  produces pixels only. Worst case is a wrong or missing picture.
* **Spoofing display names** is possible to the same degree as normal Twitch chat; what is
  prevented is using invisible characters to make a name *render* as another.
* **The config file** is trusted input. A malicious config is a local attack, out of scope.

## How to re-run the audit

```bash
# everything, including the security section
dotnet run --project tests/ZeepTwitchChat.LogicTests -c Release

# live provider probe: real json, a real animated GIF, real gif round trip
dotnet run --project tests/ZeepTwitchChat.LogicTests -c Release -- --providers

# the dangerous-API sweep that this document is based on
grep -rn 'Assembly\.Load\|Type\.GetType\|Activator\.\|Process\.\|BinaryFormatter\|DllImport' \
  plugin/ZeepTwitchChat.Plugin --include=*.cs
```
