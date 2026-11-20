using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;

namespace LocalMessaging.Host;

internal static class CertificateProvisioner
{
    [SupportedOSPlatform("windows")]
    public static X509Certificate2 LoadOrCreate(HostOptions options)
    {
        Directory.CreateDirectory(options.StateDirectory);
        var pointer = Path.Combine(options.StateDirectory, "certificate.thumbprint");
        var thumbprint = options.CertificateThumbprint ?? (File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : null);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (!string.IsNullOrEmpty(thumbprint))
        {
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (matches.Count != 1) throw new InvalidOperationException("certificate_invalid: configured current-user certificate was not found uniquely.");
            var certificate = matches[0];
            ValidateServer(certificate);
            return certificate;
        }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pfx = temporary.Export(X509ContentType.Pfx, password);
        try
        {
            var persisted = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            ValidateServer(persisted);
            store.Add(persisted);
            AtomicWrite(pointer, persisted.Thumbprint);
            return persisted;
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    public static void ValidateServer(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey || !certificate.MatchesHostname("localhost", allowWildcards:false, allowCommonName:false)
            || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("certificate_invalid: localhost SAN, valid dates, and an accessible persisted private key are required.");
        if (!certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(x => x.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1")))
            throw new InvalidOperationException("certificate_invalid: Server Authentication EKU is required.");
    }

    public static bool ValidatePeer(X509Certificate? peer, SslPolicyErrors errors, string pin)
    {
        if (peer is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        using var certificate = X509CertificateLoader.LoadCertificate(peer.GetRawCertData());
        return certificate.MatchesHostname("localhost", allowWildcards:false, allowCommonName:false)
            && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow
            && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pin, StringComparison.OrdinalIgnoreCase);
    }

    public static void AtomicWrite(string path, string contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(file))
            {
                writer.Write(contents);
                writer.Flush();
                file.Flush(flushToDisk:true);
            }
            File.Move(temporary, path, overwrite:true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
