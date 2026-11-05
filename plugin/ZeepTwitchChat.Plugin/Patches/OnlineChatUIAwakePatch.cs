using HarmonyLib;
using ZeepTwitchChat.Chat;

namespace ZeepTwitchChat.Patches
{
    /// <summary>
    /// Captures the chat window instance when an online lobby scene builds it.
    /// <c>OnlineChatUI.Awake</c> is public and exists in every build that has lobby chat,
    /// so patching it is safe (unlike patching a method that may not exist).
    /// </summary>
    [HarmonyPatch(typeof(OnlineChatUI), "Awake")]
    internal static class OnlineChatUIAwakePatch
    {
        [HarmonyPostfix]
        private static void Postfix(OnlineChatUI __instance)
        {
            ChatInjector.Instance = __instance;
#pragma warning disable 618
            UnityEngine.Debug.Log("[Twitch Chat] chat window found; Twitch lines can be shown.");
#pragma warning restore 618
        }
    }
}
