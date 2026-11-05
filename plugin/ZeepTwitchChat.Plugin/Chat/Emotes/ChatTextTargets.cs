using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>
    /// Gives the chat's own text components access to the emote sprite asset.
    ///
    /// Why this is necessary (all read from the shipped TMP build):
    ///   * A <c>&lt;sprite name="X"&gt;</c> tag resolves against the text's
    ///     <c>spriteAsset</c>, else <c>TMP_Settings.defaultSpriteAsset</c>, else
    ///     <c>Resources.Load("Sprite Assets/Default Sprite Asset")</c>. If all three are null,
    ///     TMP returns false and the tag renders as nothing.
    ///   * The asset-name form <c>&lt;sprite="asset" name="X"&gt;</c> takes a different path
    ///     through <c>MaterialReferenceManager</c>, so it needs separate registration.
    ///
    /// Assigning our asset directly to the chat text components (only when they have none, so
    /// another mod's choice is never overridden) removes both uncertainties: the plain tag form
    /// then resolves against our asset, which we have already filled and verified.
    /// </summary>
    public static class ChatTextTargets
    {
        private static AccessTools.FieldRef<OnlineChatUI, TextMeshProUGUI> bigFieldRef;
        private static AccessTools.FieldRef<OnlineChatUI, TextMeshProUGUI> smallFieldRef;
        private static TMP_SpriteAsset applied;

        /// <summary>
        /// Points the chat text components at <paramref name="asset"/> when they have no sprite
        /// asset of their own. Returns the number of components that were changed.
        /// </summary>
        public static int Apply(OnlineChatUI chatUi, TMP_SpriteAsset asset, Action<string> log)
        {
            if (chatUi == null || asset == null || applied == asset)
            {
                return 0;
            }

            int changed = 0;
            try
            {
                changed += ApplyTo(GetBigField(chatUi), asset);
                changed += ApplyTo(GetSmallField(chatUi), asset);
                applied = asset;

                if (changed > 0)
                {
                    log("Twitch Chat: emote sprite asset attached to " + changed + " chat field(s).");
                }
            }
            catch (Exception exception)
            {
                log("Twitch Chat: could not attach the emote sprite asset (" +
                    exception.GetType().Name + ": " + exception.Message + ").");
            }

            return changed;
        }

        private static int ApplyTo(TMP_Text text, TMP_SpriteAsset asset)
        {
            if (text == null)
            {
                return 0;
            }

            TMP_SpriteAsset current = text.spriteAsset;
            if (current == asset)
            {
                return 0;
            }

            // Only touch the field when it is empty or still holds an asset we set ourselves.
            // Anything else belongs to another mod (or the game) and is left alone.
            if (current != null && current != applied)
            {
                return 0;
            }

            if (current != null && asset.fallbackSpriteAssets != null &&
                !asset.fallbackSpriteAssets.Contains(current))
            {
                // Keep the previous atlas reachable: lines already rendered reference its glyphs.
                asset.fallbackSpriteAssets.Add(current);
            }

            text.spriteAsset = asset;
            return 1;
        }

        private static TextMeshProUGUI GetBigField(OnlineChatUI chatUi)
        {
            if (bigFieldRef == null)
            {
                bigFieldRef = AccessTools.FieldRefAccess<OnlineChatUI, TextMeshProUGUI>("bigChatField");
            }

            return bigFieldRef(chatUi);
        }

        private static TextMeshProUGUI GetSmallField(OnlineChatUI chatUi)
        {
            if (smallFieldRef == null)
            {
                smallFieldRef = AccessTools.FieldRefAccess<OnlineChatUI, TextMeshProUGUI>("smallChatField");
            }

            return smallFieldRef(chatUi);
        }
    }
}
