# Design notes and verified facts

Everything below was checked against the installed game build on this machine, not
assumed. Decompiled with `ilspycmd` from
`Zeepkist_Data/Managed/Zeepkist.dll`.

## 1. The chat window, as the game implements it

`OnlineChatUI` (global namespace, `ZeepkistClient` assembly) holds the chat window.

| Member | Access in the build | Notes |
| --- | --- | --- |
| `Awake()` | public override | Creates/initialises the window; closes itself when `ZeepkistNetwork.IsConnectedToGame` is false |
| `UpdateChatFields(string, ulong)` | **private** | The only way to add a line |
| `ClearChat()` | public | Resets the buffer |
| `SendChatMessage(string)` | public | Sends to the network — **never used by this mod** |
| `GetChatMessage(ZeepkistChatMessage)` | private | Network handler, registered in `OnOpen` |

`UpdateChatFields` behaviour, verbatim in effect:

1. inserts the line at index 0 of `receivedChatList` and the steam id at index 0 of
   `receivedChatIDs`;
2. trims both lists to 20 entries;
3. rebuilds the small chat field from the first 5 entries and the big field from all 20;
4. wraps lines with a non-zero steam id in `<link="<id>">...</link>` so they can be clicked
   to open a Steam profile.

**Consequence:** writing with steam id `0` gives exactly the "server message" rendering
without a Steam link, which is what a Twitch chatter should get. This mod always passes 0.

## 2. Why Harmony instead of ZeepSDK's `ChatApi`

ZeepSDK 2.7.5 ships `ChatApi.AddLocalMessage(string)` which calls
`onlineChatUI.UpdateChatFields(message, 0)`. The installed ZeepSDK DLL compiles that call
against a build where the method was public, while the installed game declares it
`private`. Cross-assembly access to a private method is not guaranteed to survive JIT
verification (`MethodAccessException` on Mono), so relying on it would be fragile.

This mod instead binds the method itself. It tries `HarmonyLib.MethodInvoker.GetHandler`
(the emitted delegate, present in the BepInEx-bundled `0Harmony.dll`) and falls back to a
plain `MethodInfo.Invoke` if that delegate cannot be created, because reflection with
full trust can call a private method from another assembly on Mono. Both paths are cached
for the process lifetime, and a game update that renames the method produces one clear log
line instead of a silent failure.

The cost is one reflection lookup and, at worst, one reflective call per displayed line —
trivial at the default thirty lines per minute.

## 3. Line format (TextMeshPro rich text)

`GetChatMessage` builds, for a player message:

```csharp
"<#RRGGBB>" + "<b>" + name + "</b></color>: " + message      // colours from Player.chatColor
"sprite/" + name sprite tags for badges                       // "<sprite=\"x\" name=\"y\">"
```

So the window accepts TMP rich text, and `<color=#RRGGBB>` is a valid equivalent of the
game's `<#RRGGBB>` shorthand. This mod emits:

```
<color=#9147FF><b>[TWITCH]</b></color> <color=#00AD03><b>[MOD]</b></color> <color=#1E90FF><b>Name</b></color>: text
```

## 4. Threat model: untrusted text in a rich-text field

Anything a chatter types would otherwise be interpreted as TMP markup — `<size=200>`,
`<sprite="achievement 2" name="host_client">` (the game's own host badge), `<link="...">`,
`<cspace>`, and so on. TMP has no sanitiser, so the formatter strips `<` and `>` from every
untrusted fragment (message, display name, label) before composing a line, maps
`\r`/`\n`/`\t` to a single space, drops other control characters, collapses whitespace and
truncates. Colours are only emitted after validating the value against `^#?[0-9A-Fa-f]{6}$`.

Unit tests assert that injected `<size>`, `<sprite>` and `<link>` tags never reach the
composed line.

## 5. Twitch side

- Anonymous read-only access: `NICK justinfan#####`, then `JOIN #channel`. No token, no
  scopes; `CAP REQ :twitch.tv/tags twitch.tv/commands` provides `color`, `display-name`,
  `badges`, `mod`.
- `/me` arrives as CTCP: `\u0001ACTION text\u0001`. `TwitchIrcClient.NormaliseChatText`
  turns it into `* text`.
- Verified live: joining a live channel (TLS, port 6697) returns `001`, `CAP ACK`,
  `JOIN`, `ROOMSTATE`, `353`/`366` and stays connected; test traffic on busy channels
  confirmed colour tags, badges and CTCP handling.

## 6. Threading

`TwitchIrcClient` runs on a background thread and raises events there. Lines are pushed
into a `ConcurrentQueue`; `Plugin.Update()` drains it on the Unity main thread, where the
chat UI is touched. The queue is bounded (40 lines, oldest dropped) so a flood cannot grow
memory, and the rate limiter decides what actually gets displayed.

## 7. Emotes as sprites

The chat's TMP components have no sprite asset of their own: player badges
(`<sprite="achievement 2" name="host_client">`) resolve because
`TMP_SpriteAsset.SearchForSpriteByHashCode(spriteAsset, hash, includeFallbacks: true, ...)`
walks `spriteAsset.fallbackSpriteAssets` and then `TMP_Settings.defaultSpriteAsset`.

Two traps were found by testing against the live game, and both are now handled:

1. **`UpdateLookupTables()` runs a legacy upgrade.** It calls `UpgradeSpriteAsset()` when
   `material != null && string.IsNullOrEmpty(m_Version)`. That method walks
   `spriteInfoList`, which is `null` on a runtime-created asset, so it throws a
   `NullReferenceException` — and it clears the glyph tables *before* throwing. The asset is
   therefore given an empty `spriteInfoList`, marked with TMP's current version string, and
   only receives its material after the lookup tables are built.
2. **A text with a null `spriteAsset` resolves nothing.** `TMP_Text.ParseText` picks the
   starting asset from `m_spriteAsset`, then `m_defaultSpriteAsset`, then
   `TMP_Settings.defaultSpriteAsset`, then `Resources.Load(...)`, and returns false if all are
   null. So the mod attaches its asset directly to `OnlineChatUI.bigChatField` /
   `smallChatField` when those have no sprite asset (never overwriting another mod's choice),
   and emits the plain `<sprite name="Kappa">` form, which resolves against that asset. The
   asset-name form (`<sprite="asset" name="x">`) goes through
   `MaterialReferenceManager` instead and would need separate registration.

The asset is also appended to `TMP_Settings.defaultSpriteAsset.fallbackSpriteAssets` when that
exists, so lookups from other text components still find the emotes.

Mechanics that were checked in the shipped assemblies rather than guessed:

| Detail | Finding |
| --- | --- |
| `TMP_Asset.material` | public field, assignable |
| `TMP_SpriteAsset.spriteCharacterTable` / `spriteGlyphTable` | public getters over initialised lists; `internal` setters, so the lists are filled in place |
| `TMP_SpriteAsset.faceInfo` | `internal` setter; written through `AccessTools.FieldRefAccess<TMP_SpriteAsset, FaceInfo>("m_FaceInfo")` |
| `FaceInfo.pointSize` | `int`, and TMP scales a sprite by `fontSize / pointSize`, so it is set to the configured emote size (24 by default) |
| `TMP_SpriteAsset.GetSpriteIndexFromName` | used as the verification step before a name is ever emitted as a tag |
| `Texture2D.LoadImage` | PNG/JPG only |

That last row is why only PNG emotes are supported: BTTV is filtered on its `imageType`
field, native Twitch emotes are PNG at every scale, FFZ serves PNG for static emotes, and
7TV is unsupported entirely because it serves WebP/AVIF/GIF.

Two deliberately defensive choices:

1. **Verify before advertising.** A name is added to the renderable snapshot only when
   `GetSpriteIndexFromName` finds it after `UpdateLookupTables()`. If TMP cannot resolve a
   glyph, the formatter keeps the emote's text name, so the failure mode is "you see `Kappa`"
   rather than a hole in the line.
2. **Immutable snapshot for other threads.** Message formatting runs on the IRC thread, so
   `GetSpriteTag` only reads a `HashSet<string>` that is replaced wholesale on rebuild; no
   TMP object is touched off the main thread.

The atlas is a fixed 8-column grid of 32px cells, rebuilt (throttled to once every 1.5s) as
new images arrive. Old atlases are kept alive for a while because chat lines already rendered
reference them, and are destroyed a rebuild or two later.

## 8. Deliberate limitations

- Local display only: `UpdateChatFields` never sends anything to the network.
- No lobby means no chat window, so messages are dropped rather than replayed later.
- No emotes/avatars: Twitch emotes would require downloaded sprite atlases; the text form
  (`Kappa`) is kept.
