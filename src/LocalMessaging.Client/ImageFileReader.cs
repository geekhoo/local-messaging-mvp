namespace LocalMessaging.Client;

/// <summary>Copies an upload into an owned buffer without trusting a pre-read file-size check.</summary>
public static class ImageFileReader
{
    public const int MaximumBytes = 8 * 1024 * 1024;

    public static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaximumBytes) throw new InvalidDataException("The image must be at most 8 MiB.");
        using var owned = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return owned.ToArray();
            if (owned.Length + count > MaximumBytes) throw new InvalidDataException("The image grew beyond the 8 MiB limit while it was being read.");
            owned.Write(buffer, 0, count);
        }
    }
}
