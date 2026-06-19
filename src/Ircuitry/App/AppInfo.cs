namespace Ircuitry.App;

/// <summary>Build identity, used by the in-app updater to compare against the latest GitHub release.</summary>
public static class AppInfo
{
    /// <summary>Current version. Bump this with each release tag (the tag is "v" + this).</summary>
    public const string Version = "0.15.0";

    public const string Repo = "ircuitry/ircuitry";
}
