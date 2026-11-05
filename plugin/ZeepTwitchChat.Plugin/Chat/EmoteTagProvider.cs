namespace ZeepTwitchChat.Chat
{
    /// <summary>
    /// Supplies the TextMeshPro sprite tag for an emote name.
    ///
    /// Implementations return null whenever an emote cannot be rendered (not downloaded
    /// yet, not in the atlas, or unresolved by TMP). Returning null is important: the
    /// formatter then keeps the plain emote name, so a missing sprite shows the word
    /// instead of leaving a hole in the line.
    /// </summary>
    public interface IEmoteTagProvider
    {
        /// <summary>Sprite tag such as <c>&lt;sprite name="Kappa"&gt;</c>, or null.</summary>
        string GetSpriteTag(string emoteName);
    }
}
