namespace ValkyrieID.Server.Pages;

/// <summary>
/// The entry animation of the sign-in layout (layout.md, Phase 11b). Entry pages set it on a fresh GET only, never on
/// a POST re-render or a later step, and the layout then adds the <c>entry</c> class to <c>&lt;html&gt;</c>.
/// </summary>
internal static class EntryAnimation
{
    public const string Key = "EntryAnimation";
}
