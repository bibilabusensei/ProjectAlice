using System.Runtime.InteropServices;
using AliceVision.Core.Models;

namespace AliceVision.iOS;

/// <summary>Advisory app headroom, not total device RAM. Never silently lowers texture quality.</summary>
internal static class ModelMemoryGuard
{
    private const long MiB = 1024 * 1024;
    private const long MinimumReserveBytes = 256 * MiB;

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "os_proc_available_memory")]
    private static extern nuint AvailableProcessMemory();

    internal static void EnsureCanAllocate(long requiredBytes, string stage)
    {
        if (requiredBytes < 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        long available;
        try { available = checked((long)AvailableProcessMemory()); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            throw new InvalidOperationException("系统无法提供应用内存余量，已安全停止导入；不会自动降低贴图画质。", error);
        }
        // Available memory is advisory and can shrink at any moment. Reserve
        // room for AR camera/depth frames, animation and the OS; recheck each step.
        long reserve = Math.Max(MinimumReserveBytes, available / 4);
        long usable = Math.Max(0, available - reserve);
        if (requiredBytes > usable)
            throw new InvalidOperationException($"{stage}：原画质预计还需 {ToMiB(requiredBytes)} MB，当前保护后可用约 {ToMiB(usable)} MB。\n" +
                "已停止导入以保护内存，未降低分辨率或修改源文件。可关闭其他应用、重启本应用后重试；模型超出设备容量时仍会拒绝。");
    }

    internal static long PendingTextureUploadBytes(IEnumerable<GltfImage> images) =>
        images.Sum(image => checked(ModelTextureMemory.TextureResidentBytes(image) - ModelTextureMemory.DecodedBytes(image)));

    private static long ToMiB(long bytes) => bytes / MiB + (bytes % MiB == 0 ? 0 : 1);
}
