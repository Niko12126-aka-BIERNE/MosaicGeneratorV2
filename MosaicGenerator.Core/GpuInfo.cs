using ILGPU;
using ILGPU.Runtime.Cuda;

namespace MosaicGenerator.Core;

public static class GpuInfo
{
    /// <summary>
    /// Names of the CUDA-capable GPUs on this machine. Empty when there are none,
    /// or when no NVIDIA driver is installed.
    /// </summary>
    public static IReadOnlyList<string> FindCudaDevices()
    {
        try
        {
            using var context = Context.Create(b => b.Cuda());

            var names = new List<string>();
            foreach (var device in context.GetCudaDevices())
                names.Add(device.Name);
            return names;
        }
        catch (Exception)
        {
            // A missing or broken CUDA driver can surface as almost any exception type.
            return [];
        }
    }
}
