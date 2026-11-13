using Segra.Backend.Core.Models;

namespace Segra.Backend.Games.Wardogs
{
    /// <summary>
    /// Bookmarks kills, assists and deaths from the markers WARDOGS sends to the Steam timeline since 0.3.0:
    /// <list type="bullet">
    /// <item>"Killed" (wd-kill), described by weapon and distance.</item>
    /// <item>"Kill Assist" (wd-assist).</item>
    /// <item>"Killed by" (wd-killed), described by weapon.</item>
    /// <item>"Revived" (wd-respawn), not bookmarked.</item>
    /// </list>
    /// Events are told apart by icon, the titles are display text.
    ///
    /// The timeline arrives when the game closes, so the bookmarks are added at that point.
    /// </summary>
    internal class WardogsIntegration : SteamTimelineIntegration
    {
        protected override string LogPrefix => "WARDOGS";

        protected override BookmarkType? GetBookmarkType(SteamTimelineEvent timelineEvent) => timelineEvent.Icon switch
        {
            "wd-kill" => BookmarkType.Kill,
            "wd-assist" => BookmarkType.Assist,
            "wd-killed" => BookmarkType.Death,
            _ => null,
        };
    }
}
