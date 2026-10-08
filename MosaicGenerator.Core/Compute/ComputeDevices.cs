using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;

namespace MosaicGenerator.Core.Compute;

/// <summary>Finds the devices that can run matching, and opens one.</summary>
public static class ComputeDevices
{
    /// <summary>Device setting meaning "pick the best one available".</summary>
    public const string Auto = "auto";

    /// <summary>
    /// Every usable device, best first: CUDA GPUs, then OpenCL GPUs, then the processor,
    /// which is always there. ILGPU leaves out OpenCL devices it can't use (it needs
    /// OpenCL C 2.0 or newer, which current AMD and Intel drivers have).
    /// </summary>
    public static IReadOnlyList<DeviceInfo> List()
    {
        var devices = new List<DeviceInfo>();
        devices.AddRange(ListGpus(DeviceKind.Cuda));
        devices.AddRange(ListGpus(DeviceKind.OpenCL));
        devices.Add(CpuBackend.Info);
        return devices;
    }

    /// <summary>
    /// Opens a device for matching. <paramref name="deviceId"/> is one of:
    /// <list type="bullet">
    ///   <item>"auto": the best device. If a GPU fails later, matching continues on the processor.</item>
    ///   <item>"cuda", "opencl" or "cpu": the first device of that kind.</item>
    ///   <item>An exact id from <see cref="List"/>, such as "cuda:0".</item>
    /// </list>
    /// A device chosen by name or id is used as-is: if it fails, the error is reported.
    /// </summary>
    public static IMatchBackend Open(string deviceId, ProgressReporter? log = null)
    {
        if (string.Equals(deviceId, Auto, StringComparison.OrdinalIgnoreCase))
            return OpenAuto(log);

        var devices = List();
        var info = devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => string.Equals(d.Kind.ToString(), deviceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                       $"Device '{deviceId}' was not found. Available: auto, {string.Join(", ", devices.Select(d => d.Id))}.");

        return info.Kind == DeviceKind.Cpu ? new CpuBackend() : OpenGpu(info, log);
    }

    private static IMatchBackend OpenAuto(ProgressReporter? log)
    {
        foreach (var info in List().Where(d => d.Kind != DeviceKind.Cpu))
        {
            try
            {
                return new FallbackBackend(OpenGpu(info, log), log);
            }
            catch (Exception ex)
            {
                log?.Log($"Could not start {info}: {ex.Message}");
            }
        }

        return new CpuBackend();
    }

    // GPUs through ILGPU

    private static IEnumerable<DeviceInfo> ListGpus(DeviceKind kind)
    {
        try
        {
            using var context = CreateContext(kind);
            return DevicesOfKind(context, kind)
                .Select((device, index) => new DeviceInfo(
                    $"{kind.ToString().ToLowerInvariant()}:{index}", device.Name, kind, device.MemorySize))
                .ToArray();
        }
        catch (Exception)
        {
            // No driver or runtime for this kind of GPU. That can surface as almost any exception.
            return [];
        }
    }

    private static IlgpuBackend OpenGpu(DeviceInfo info, ProgressReporter? log)
    {
        int index   = int.Parse(info.Id[(info.Id.IndexOf(':') + 1)..]);
        var context = CreateContext(info.Kind);
        try
        {
            return new IlgpuBackend(context, DevicesOfKind(context, info.Kind).ElementAt(index), info, log);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static Context CreateContext(DeviceKind kind) =>
        Context.Create(builder => (kind == DeviceKind.Cuda ? builder.Cuda() : builder.OpenCL()).EnableAlgorithms());

    private static IEnumerable<Device> DevicesOfKind(Context context, DeviceKind kind)
    {
        var type = kind == DeviceKind.Cuda ? AcceleratorType.Cuda : AcceleratorType.OpenCL;
        return context.Devices.Where(d => d.AcceleratorType == type);
    }
}
