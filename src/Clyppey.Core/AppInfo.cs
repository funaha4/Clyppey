namespace Clyppey.Core;

/// <summary>Identity of the running build, shared by the UI and the updater.</summary>
public static class AppInfo
{
    public const string Version = "1.3.0";

    public const string DisplayVersion = "v" + Version;

    public const string Repository = "funaha4/Clyppey";

    public const string ReleasePage = "https://github.com/" + Repository + "/releases/latest";

    public const string ApiLatestRelease = "https://api.github.com/repos/" + Repository + "/releases/latest";
}
