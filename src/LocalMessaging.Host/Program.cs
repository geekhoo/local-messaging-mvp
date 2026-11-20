using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text.Json;
using LocalMessaging.Host;
using LocalMessaging.Infrastructure;
using LocalMessaging.Platform.Windows;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var (options,operation,file)=LocalMessaging.Host.HostOptions.Parse(args);
            if (operation=="help")
            {
                Console.WriteLine("""
LocalMessaging.Host — independent per-user Windows messaging host
  --config <absolute-json-file>
  --data-dir <local-directory>
  --state-dir <local-state-directory>
  --messaging-port <port>          default 7443, HTTP/3 only
  --frontend-port <port>           default 7444, HTTP/1.1
  --certificate-thumbprint <id>    existing current-user My certificate
  --backup <new-file>              offline; host must be stopped
  --restore <backup-file>          offline; safety backup and new epoch
Start without options for normal operation. Close frontends without stopping host.
The production runtime requires Windows 11 x64 and genuine QUIC readiness.
""");
                return 0;
            }
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("platform_unsupported: the qualified product target is Windows 11 x64. Portable test fixtures do not qualify the Windows runtime.");
                return 2;
            }
            if (operation=="backup")
            {
                var result=await StoreMaintenance.BackupAsync(options.EffectiveDataDirectory,file!,CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(new { state="backed_up",path=result }));
                return 0;
            }
            if (operation=="restore")
            {
                var result=await StoreMaintenance.RestoreAsync(options.EffectiveDataDirectory,file!,CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(result));
                return 0;
            }
            if (!QuicListener.IsSupported)
            {
                Console.Error.WriteLine("quic_unavailable: this Windows/runtime configuration cannot start the required HTTP/3 endpoint.");
                return 2;
            }
            await using var store=new MessagingStore(new StoreOptions(options.EffectiveDataDirectory,
                options.Limits.WriteWaitSeconds,options.Limits.DatabaseTimeoutSeconds,options.Limits));
            await store.InitializeAsync();
            using var certificate=CertificateProvisioner.LoadOrCreate(options);
            using var images=new WicImageValidator(options.Limits);
            await using var host=new HostRuntime(options,store,images,certificate,requireHttp3:true);
            await host.StartAsync();
            Console.WriteLine($"LocalMessaging ready: {options.FrontendOrigin}; messaging {options.MessagingBaseUrl}");
            await host.App.WaitForShutdownAsync();
            return 0;
        }
        catch (LocalMessaging.Contracts.ApiException e)
        {
            Console.Error.WriteLine($"{e.Code}: {e.Message}");
            return 2;
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException or JsonException)
        {
            Console.Error.WriteLine($"startup_or_maintenance_failed: {e.Message}");
            return 2;
        }
    }
}
