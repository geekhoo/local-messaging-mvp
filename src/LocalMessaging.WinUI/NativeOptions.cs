namespace LocalMessaging.WinUI;

public sealed record NativeOptions(string EndpointPath, bool CustomEndpoint, bool LaunchHost)
{
    public static NativeOptions Parse(string[] arguments)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMessaging", "endpoint.json");
        bool custom = false, launch = true;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--no-launch-host") { launch = false; continue; }
            if (arguments[i] == "--endpoint" && !custom && i + 1 < arguments.Length)
            {
                path = arguments[++i];
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("--endpoint must be an absolute descriptor file path.");
                path = Path.GetFullPath(path); custom = true; continue;
            }
            throw new ArgumentException("Use --endpoint <absolute-file> and optionally --no-launch-host.");
        }
        return new(path, custom, launch);
    }
}
