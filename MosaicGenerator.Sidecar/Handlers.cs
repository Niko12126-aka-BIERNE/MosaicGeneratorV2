using System.Text.Json;
using MosaicGenerator.Core;
using MosaicGenerator.Core.Compute;
using SixLabors.ImageSharp;

namespace MosaicGenerator.Sidecar;

/// <summary>
/// One method per request type. Each returns the "result" for the response, or throws;
/// Program.cs turns an exception into an error response.
/// </summary>
public static class Handlers
{
    /// <summary>Set by a "shutdown" request; Program.cs exits after sending its response.</summary>
    public static bool ShutdownRequested { get; private set; }

    public static object? Handle(string type, JsonElement request) => type switch
    {
        "systemInfo"     => SystemInfo(),
        "cardFolderInfo" => CardFolderInfo(Read<PathRequest>(request)),
        "imageInfo"      => ImageInfo(Read<PathRequest>(request)),
        "layout"         => Layout(Read<LayoutRequest>(request)),
        "outputExists"   => OutputExists(Read<OutputExistsRequest>(request)),
        "validate"       => Validate(Read<OptionsRequest>(request)),
        "start"          => Start(Read<OptionsRequest>(request)),
        "cancel"         => new CancelResult(MosaicJob.Cancel()),
        "shutdown"       => Shutdown(),
        _                => throw new RequestException($"Unknown request type '{type}'."),
    };

    // The app sends this before it closes. Tauri force-kills child processes on exit, so this
    // is our chance to stop a running job cleanly and remove its partial output first.
    private static object? Shutdown()
    {
        MosaicJob.CancelAndWait();
        ShutdownRequested = true;
        return null;
    }

    private static SystemInfoResult SystemInfo() =>
        new(ComputeDevices.List());

    private static CardFolderInfoResult CardFolderInfo(PathRequest request)
    {
        string folder = RequirePath(request);
        int    count  = CardDatabase.FindCardImages(folder).Length;

        return new CardFolderInfoResult(
            ImageCount:  count,
            AspectRatio: count > 0 ? CardDatabase.SampleAspectRatio(folder) : null,
            HasCache:    File.Exists(Path.Combine(folder, "allCardLabData.json")));
    }

    private static ImageInfoResult ImageInfo(PathRequest request)
    {
        var info = Image.Identify(RequirePath(request));
        return new ImageInfoResult(info.Width, info.Height);
    }

    private static MosaicLayout Layout(LayoutRequest r) =>
        MosaicLayout.Calculate(r.InputWidth, r.InputHeight, r.CardsPerRow, r.CardWidth, r.CardAspectRatio);

    // Where the output would be written, and whether something is already there. For Deep Zoom
    // that's a folder named after the output path, so the app can't easily work it out itself.
    private static OutputExistsResult OutputExists(OutputExistsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            throw new RequestException("Request is missing \"outputPath\".");

        if (request.DeepZoom)
        {
            string folder = DeepZoomWriter.GetOutputFolder(request.OutputPath);
            return new OutputExistsResult(Directory.Exists(folder), folder);
        }

        string file = Path.GetFullPath(request.OutputPath);
        return new OutputExistsResult(File.Exists(file), file);
    }

    private static ValidateResult Validate(OptionsRequest request)
    {
        var errors  = new List<string>();
        var options = RequireOptions(request).ToOptions(errors);
        errors.AddRange(options.Validate());
        return new ValidateResult(errors);
    }

    // Checks the options first, so mistakes come back as an error response straight away
    // instead of as a "failed" event after the job has started.
    private static object? Start(OptionsRequest request)
    {
        var errors  = new List<string>();
        var options = RequireOptions(request).ToOptions(errors);
        errors.AddRange(options.Validate());
        if (errors.Count > 0)
            throw new RequestException(string.Join("\n", errors));

        MosaicJob.Start(options);
        return null;
    }

    // Helpers

    private static T Read<T>(JsonElement request) =>
        request.Deserialize<T>(Protocol.Json)
            ?? throw new RequestException($"Request is missing its fields.");

    private static string RequirePath(PathRequest request) =>
        string.IsNullOrWhiteSpace(request.Path)
            ? throw new RequestException("Request is missing \"path\".")
            : request.Path;

    private static OptionsDto RequireOptions(OptionsRequest request) =>
        request.Options ?? throw new RequestException("Request is missing \"options\".");
}
