using System.Security.Cryptography;
using LocalMessaging.Contracts;
using LocalMessaging.Platform.Windows;

if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
{
    Console.WriteLine("{\"suite\":\"WindowsWicCorpus\",\"result\":\"blocked\",\"reason\":\"Actual Windows WIC execution is required. No decoder substitute was used.\"}");
    return 2;
}
using var validator = new WicImageValidator();
var checks = new List<object>();
var failed = 0;
async Task Check(string name, Func<Task> action)
{
    try { await action(); checks.Add(new { name, result = "passed" }); }
    catch (Exception e) { failed++; checks.Add(new { name, result = "failed", detail = e.ToString() }); }
}
static void Assert(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException(detail);
}
byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
foreach (var (file, mime) in new[] { ("valid.png", "image/png"), ("valid.jpg", "image/jpeg") })
{
    await Check(file + " completes headless built-in decode and verifies immutable metadata", async () =>
    {
        var bytes = Fixture(file);
        var result = await validator.ValidateAsync(bytes);
        Assert(result.ContentType == mime && result.Width == 32 && result.Height == 16, "Incorrect decoded dimensions/type.");
        Assert(result.Sha256 == Convert.ToHexStringLower(SHA256.HashData(bytes)) && result.Bytes.SequenceEqual(bytes), "Bytes or digest changed.");
        bytes[0] ^= 1;
        Assert(!result.Bytes.SequenceEqual(bytes), "Validator returned caller-owned mutable bytes.");
    });
}
foreach (var (file, code) in new[]
{
    ("corrupt-crc.png", "invalid_image"), ("animated.png", "invalid_image"),
    ("dimensions.png", "image_dimensions_exceeded"), ("truncated.jpg", "invalid_image")
})
{
    await Check(file + " rejects before persistence", async () =>
    {
        try { await validator.ValidateAsync(Fixture(file)); throw new Exception("Malformed image was accepted."); }
        catch (ApiException e) { Assert(e.Code == code, "Unexpected image error code: " + e.Code); }
    });
}
await Check("Unsupported bytes reject independently of filename/MIME claim", async () =>
{
    try { await validator.ValidateAsync("not an image"u8.ToArray()); throw new Exception("Unsupported input accepted."); }
    catch (ApiException e) { Assert(e.Status == 415, "Wrong rejection."); }
});
await Check("Binary byte limit is enforced", async () =>
{
    try { await validator.ValidateAsync(new byte[8 * 1024 * 1024 + 1]); throw new Exception("Oversize input accepted."); }
    catch (ApiException e) { Assert(e.Status == 413 && e.Code == "image_too_large", "Wrong byte limit response."); }
});
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
{
    suite = "WindowsWicCorpus", platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    passed = checks.Count - failed, failed, checks
}, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
