using System.Runtime.InteropServices;
using System.Text;
using LocalMessaging.Client;
using Microsoft.Win32.SafeHandles;

internal static class WorkspaceImage
{
    private const uint ReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint Overlapped = 0x40000000;

    internal static async Task<byte[]> ReadAsync(string workspace, string requested, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw Invalid();
        var normalized = requested.Replace('/', '\\');
        var absolute = IsDrivePath(normalized);
        if ((!absolute && Path.IsPathRooted(normalized)) || !IsDrivePath(workspace)) throw Invalid();
        var parts = (absolute ? normalized[3..] : normalized).Split('\\');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part))) throw Invalid();

        var held = new List<SafeFileHandle>();
        try
        {
            var full = Path.GetFullPath(normalized, workspace);
            var prefix = Path.TrimEndingDirectorySeparator(workspace);
            if (!Path.EndsInDirectorySeparator(prefix)) prefix += '\\';
            if (full.Length <= prefix.Length || char.ToUpperInvariant(full[0]) != char.ToUpperInvariant(prefix[0])
                || !full.AsSpan(1).StartsWith(prefix.AsSpan(1), StringComparison.Ordinal)
                || new DriveInfo(full[..3]).DriveType != DriveType.Fixed) throw Invalid();
            var path = full[..3];
            var components = full[3..].Split('\\');
            held.Add(OpenChecked(path, directory: true));
            // Hold each no-follow directory handle open without write/delete sharing until the file has been copied.
            for (var i = 0; i < components.Length - 1; i++)
            {
                path = Path.Combine(path, components[i]);
                held.Add(OpenChecked(path, directory: true));
            }
            using var fileHandle = OpenChecked(full, directory: false);
            await using var file = new FileStream(fileHandle, FileAccess.Read, 8192, isAsync: true);
            if (file.Length > ImageFileReader.MaximumBytes) throw new ToolFault("image_too_large", "The image must be at most 8 MiB.");
            using var owned = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await file.ReadAsync(buffer, token);
                if (count == 0) break;
                if (owned.Length + count > ImageFileReader.MaximumBytes) throw new ToolFault("image_too_large", "The image must be at most 8 MiB.");
                owned.Write(buffer, 0, count);
            }
            var bytes = owned.ToArray();
            if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                && !bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
                throw new ToolFault("invalid_image_type", "Select a PNG or JPEG image. The host validates the full image and dimensions.");
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Invalid();
        }
        finally
        {
            for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose();
        }
    }

    private static SafeFileHandle OpenChecked(string path, bool directory)
    {
        var handle = CreateFile(path, directory ? ReadAttributes : GenericRead, (uint)FileShare.Read, IntPtr.Zero,
            OpenExisting, OpenReparsePoint | (directory ? BackupSemantics : Overlapped), IntPtr.Zero);
        var valid = false;
        try
        {
            if (handle.IsInvalid || GetFileType(handle) != 1 // FILE_TYPE_DISK
                || !GetFileInformationByHandleEx(handle, 9, out var info, 8) // FileAttributeTagInfo
                || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory) throw Invalid();
            if (!directory && (!GetStandardInfo(handle, 1, out var standard, 24) || standard.NumberOfLinks != 1))
                throw Invalid(); // A hard link can alias a file outside the workspace.
            // Verify the opened path too: reject DOS aliases and redirected drives.
            var final = new StringBuilder(path.Length + 8);
            var size = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
            if (size == 0 || size >= final.Capacity || !string.Equals(final.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            valid = true;
            return handle;
        }
        finally
        {
            if (!valid) handle.Dispose();
        }
    }

    private static bool IsDrivePath(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
    private static ToolFault Invalid() => new("invalid_image_path", "Select an accessible regular image inside the current workspace on a fixed local drive. Traversal, alternate streams, device paths, hard links and reparse points are not allowed.");

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00b9' or '\u00b2' or '\u00b3'));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { internal uint Attributes; internal uint ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        internal long AllocationSize, EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending, Directory;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle handle, int informationClass, out StandardInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
