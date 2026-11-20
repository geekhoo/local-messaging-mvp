using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using LocalMessaging.Contracts;

namespace LocalMessaging.Platform.Windows;

/// <summary>Restricted headless WIC decoder. It never asks the system to discover third-party codecs.</summary>
public sealed class WicImageValidator : IImageValidator, IDisposable
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly ProductLimits limits;
    private readonly SemaphoreSlim slots;

    public WicImageValidator(ProductLimits? limits = null)
    {
        this.limits = limits ?? new ProductLimits();
        if (this.limits.ImageValidations is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(limits));
        slots = new SemaphoreSlim(this.limits.ImageValidations);
    }

    public async Task<ValidatedImage> ValidateAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > limits.ImageBytes) throw new ApiException(413, "image_too_large", "The image exceeds the configured byte limit.");
        if (bytes.Length < 4) throw new ApiException(422, "invalid_image", "The image is empty or truncated.");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Complete image validation requires Windows WIC; a header check is not a decoder substitute.");
        if (!await slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new ApiException(429, "resource_limit_reached", "Both image validation slots are currently in use. Retry shortly.");
        try
        {
            var owned = bytes.ToArray();
            return await Task.Run(() =>
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                    throw new PlatformNotSupportedException("Windows WIC is required.");
                return Decode(owned, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { slots.Release(); }
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private ValidatedImage Decode(byte[] bytes, CancellationToken cancellationToken)
    {
        bool png = bytes.AsSpan().StartsWith(PngSignature);
        bool jpeg = bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
        if (!png && !jpeg) throw new ApiException(415, "unsupported_image_type", "Only decoded PNG and JPEG images are supported.");
        if (png) InspectPng(bytes);
        if (jpeg && (bytes[^2] != 0xff || bytes[^1] != 0xd9))
            throw new ApiException(422, "invalid_image", "The JPEG container is truncated or has trailing non-image data.");
        var initialized = Native.CoInitializeEx(IntPtr.Zero, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        IStream? stream = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapSource? frame = null;
        IWICImagingFactory? factory = null;
        IWICFormatConverter? converter = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream = Native.SHCreateMemStream(bytes, checked((uint)bytes.Length))
                ?? throw new OutOfMemoryException("WIC memory stream could not be created.");
            var decoderClass = png
                ? new Guid("389ea17b-5078-4cde-b6ef-25c15175c751")
                : new Guid("9456a480-e88b-43ea-9e73-0b2d9b71b1ca");
            decoder = (IWICBitmapDecoder)Activator.CreateInstance(Type.GetTypeFromCLSID(decoderClass, true)!)!;
            decoder.Initialize(stream, 0);
            decoder.GetFrameCount(out var frames);
            if (frames != 1) throw new ApiException(422, "invalid_image", "Only a single non-animated image frame is supported.");
            decoder.GetFrame(0, out frame);
            frame.GetSize(out var width, out var height);
            if (width == 0 || height == 0 || width > limits.ImageSide || height > limits.ImageSide
                || checked((ulong)width * height) > (ulong)limits.ImagePixels)
                throw new ApiException(422, "image_dimensions_exceeded", "Image dimensions or total decoded pixels exceed the configured limit.");

            factory = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a"), true)!)!;
            factory.CreateFormatConverter(out converter);
            // GUID_WICPixelFormat32bppBGRA: an explicit fixed-width target bounds stride arithmetic.
            var format = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f");
            converter.Initialize(frame, ref format, 0, IntPtr.Zero, 0, 0);
            var stride = checked(width * 4);
            var rowsPerCopy = Math.Min(64u, height);
            var buffer = new byte[checked((int)(stride * rowsPerCopy))];
            for (uint y = 0; y < height; y += rowsPerCopy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = Math.Min(rowsPerCopy, height - y);
                var rectangle = new WicRect(0, checked((int)y), checked((int)width), checked((int)rows));
                converter.CopyPixels(ref rectangle, stride, checked(stride * rows), buffer);
            }
            return new ValidatedImage(bytes, png ? "image/png" : "image/jpeg",
                checked((int)width), checked((int)height), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (COMException)
        {
            throw new ApiException(422, "invalid_image", "The complete image could not be decoded as valid PNG or JPEG.");
        }
        catch (OverflowException)
        {
            throw new ApiException(422, "image_dimensions_exceeded", "Image dimensions overflow the supported decode bounds.");
        }
        finally
        {
            Release(converter);
            Release(factory);
            Release(frame);
            Release(decoder);
            Release(stream);
            if (initialized >= 0) Native.CoUninitialize();
        }
    }

    private static void InspectPng(ReadOnlySpan<byte> bytes)
    {
        int offset = 8;
        bool first = true;
        bool ended = false;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12) throw InvalidPng();
            uint unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (unsignedLength > int.MaxValue || unsignedLength > bytes.Length - offset - 12) throw InvalidPng();
            int length = (int)unsignedLength;
            var type = bytes.Slice(offset + 4, 4);
            if (first && (!type.SequenceEqual("IHDR"u8) || length != 13)) throw InvalidPng();
            first = false;
            if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
                throw new ApiException(422, "invalid_image", "Animated PNG images are not supported.");
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + length, 4));
            if (Crc32(bytes.Slice(offset + 4, length + 4)) != expectedCrc) throw InvalidPng();
            offset += length + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || offset != bytes.Length) throw InvalidPng();
                ended = true;
                break;
            }
        }
        if (!ended) throw InvalidPng();
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes)
        {
            value ^= b;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1));
        }
        return ~value;
    }
    private static ApiException InvalidPng() => new(422, "invalid_image", "PNG structure or checksum is invalid.");
    [SupportedOSPlatform("windows")]
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
    public void Dispose() => slots.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WicRect(int x, int y, int width, int height)
    {
        public readonly int X = x;
        public readonly int Y = y;
        public readonly int Width = width;
        public readonly int Height = height;
    }

    private static class Native
    {
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("shlwapi.dll")]
        [return: MarshalAs(UnmanagedType.Interface)]
        internal static extern IStream? SHCreateMemStream([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] bytes, uint size);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        void QueryCapability(IStream stream, out uint capability);
        void Initialize(IStream stream, uint options);
        void GetContainerFormat(out Guid format);
        void GetDecoderInfo(out IntPtr info);
        void CopyPalette(IntPtr palette);
        void GetMetadataQueryReader(out IntPtr reader);
        void GetPreview(out IntPtr preview);
        void GetColorContexts(uint count, IntPtr contexts, out uint actual);
        void GetThumbnail(out IntPtr thumbnail);
        void GetFrameCount(out uint count);
        void GetFrame(uint index, [MarshalAs(UnmanagedType.Interface)] out IWICBitmapSource source);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSource
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double x, out double y);
        void CopyPalette(IntPtr palette);
        void CopyPixels(ref WicRect rectangle, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
    }

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double x, out double y);
        void CopyPalette(IntPtr palette);
        void CopyPixels(ref WicRect rectangle, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
        void Initialize([MarshalAs(UnmanagedType.Interface)] IWICBitmapSource source, ref Guid destination,
            uint dither, IntPtr palette, double alphaThreshold, uint paletteTranslate);
        void CanConvert(ref Guid source, ref Guid destination, [MarshalAs(UnmanagedType.Bool)] out bool canConvert);
    }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename, IntPtr vendor, uint access, uint options, out IntPtr decoder);
        void CreateDecoderFromStream(IStream stream, IntPtr vendor, uint options, out IntPtr decoder);
        void CreateDecoderFromFileHandle(nuint handle, IntPtr vendor, uint options, out IntPtr decoder);
        void CreateComponentInfo(ref Guid component, out IntPtr info);
        void CreateDecoder(ref Guid container, IntPtr vendor, out IntPtr decoder);
        void CreateEncoder(ref Guid container, IntPtr vendor, out IntPtr encoder);
        void CreatePalette(out IntPtr palette);
        void CreateFormatConverter(out IWICFormatConverter converter);
    }
}
