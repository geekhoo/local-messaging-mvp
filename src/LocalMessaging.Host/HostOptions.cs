using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMessaging.Contracts;

namespace LocalMessaging.Host;

public sealed record HostOptions
{
    public string StateDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMessaging");
    public string? DataDirectory { get; init; }
    public string? WebRoot { get; init; }
    public int MessagingPort { get; init; } = 7443;
    public int FrontendPort { get; init; } = 7444;
    public string? CertificateThumbprint { get; init; }
    public ProductLimits Limits { get; init; } = new();
    [JsonIgnore] public string EffectiveDataDirectory => Path.GetFullPath(DataDirectory ?? Path.Combine(StateDirectory, "data"));
    [JsonIgnore] public string DescriptorPath => Path.Combine(StateDirectory, "endpoint.json");
    [JsonIgnore] public string FrontendOrigin => $"http://127.0.0.1:{FrontendPort}";
    [JsonIgnore] public string MessagingBaseUrl => $"https://localhost:{MessagingPort}/messaging/v1/";
    [JsonIgnore] public string FrontendBaseUrl => FrontendOrigin + "/api/v1/";

    public void Validate()
    {
        if (!Path.IsPathFullyQualified(StateDirectory) || !Path.IsPathFullyQualified(EffectiveDataDirectory))
            throw new ArgumentException("State/data directories must be absolute local paths.");
        if (StateDirectory.StartsWith(@"\\", StringComparison.Ordinal) || EffectiveDataDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("The active store must be on a local filesystem, not a UNC share.");
        if (MessagingPort is < 1024 or > 65535 || FrontendPort is < 1024 or > 65535 || MessagingPort == FrontendPort)
            throw new ArgumentException("Use two distinct ports in the range 1024–65535.");
        var maximum = new ProductLimits();
        foreach (var property in typeof(ProductLimits).GetProperties())
        {
            var value = Convert.ToInt64(property.GetValue(Limits), System.Globalization.CultureInfo.InvariantCulture);
            var ceiling = Convert.ToInt64(property.GetValue(maximum), System.Globalization.CultureInfo.InvariantCulture);
            if (value < 1 || value > ceiling) throw new ArgumentException($"Limit {property.Name} must be between 1 and {ceiling}.");
        }
        if (Limits.MultipartBytes <= Limits.ImageBytes || Limits.EventPayloadBytes < Limits.EventFrameBytes)
            throw new ArgumentException("Multipart budget must exceed image budget; event batch budget must cover one allowed frame.");
        if (Limits.ClientIdleSeconds <= Limits.HeartbeatSeconds)
            throw new ArgumentException("Client idle timeout must exceed stream heartbeat interval.");
    }

    public static (HostOptions Options, string Operation, string? File) Parse(string[] args)
    {
        var values = new Dictionary<string,string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h") return (new(), "help", null);
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException("Every option needs a value. Use --help.");
            if (!values.TryAdd(args[i], args[++i])) throw new ArgumentException("Duplicate option.");
        }
        string[] allowed = ["--config","--data-dir","--state-dir","--messaging-port","--frontend-port","--certificate-thumbprint","--backup","--restore"];
        if (values.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal))) throw new ArgumentException("Unknown option. Use --help.");
        var options = values.TryGetValue("--config", out var config)
            ? JsonSerializer.Deserialize<HostOptions>(System.IO.File.ReadAllText(config), Wire.RequestOptions) ?? throw new ArgumentException("Invalid config.")
            : new HostOptions();
        if (values.TryGetValue("--state-dir", out var state)) options = options with { StateDirectory = Path.GetFullPath(state) };
        if (values.TryGetValue("--data-dir", out var data)) options = options with { DataDirectory = Path.GetFullPath(data) };
        if (values.TryGetValue("--messaging-port", out var messaging)) options = options with { MessagingPort = int.Parse(messaging, System.Globalization.CultureInfo.InvariantCulture) };
        if (values.TryGetValue("--frontend-port", out var frontend)) options = options with { FrontendPort = int.Parse(frontend, System.Globalization.CultureInfo.InvariantCulture) };
        if (values.TryGetValue("--certificate-thumbprint", out var thumbprint)) options = options with { CertificateThumbprint = thumbprint };
        options.Validate();
        if (values.ContainsKey("--backup") && values.ContainsKey("--restore")) throw new ArgumentException("Choose backup or restore, not both.");
        if (values.TryGetValue("--backup", out var backup)) return (options, "backup", Path.GetFullPath(backup));
        if (values.TryGetValue("--restore", out var restore)) return (options, "restore", Path.GetFullPath(restore));
        return (options, "run", null);
    }
}
