using System.Text.Json;
using System.Text.Json.Serialization;
using MosaicGenerator.Core;
using MosaicGenerator.Core.Compute;

namespace MosaicGenerator.Sidecar;

// The shapes of every message going in and out of the sidecar. See README.md in this
// folder for the protocol with examples.

public static class Protocol
{
    /// <summary>Bumped when messages change in a way the app needs to know about.</summary>
    public const int Version = 2;   // 2: compute devices (systemInfo, options.device, done.device)

    /// <summary>camelCase names, enums as camelCase strings, nulls left out.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Thrown for requests the sidecar understands but refuses, e.g. a second "start".</summary>
public sealed class RequestException(string message) : Exception(message);

// ── Requests (app to sidecar) ─────────────────────────────────────────────────
// Every request also has "id" and "type", which Program.cs reads before these are used.

public sealed record PathRequest(string? Path);

public sealed record LayoutRequest(
    int InputWidth, int InputHeight, int CardsPerRow, int CardWidth, float CardAspectRatio);

public sealed record OptionsRequest(OptionsDto? Options);

public sealed record OutputExistsRequest(string? OutputPath, bool DeepZoom);

/// <summary>
/// Mosaic settings as the app sends them. Anything left out gets the engine's default.
/// The background colour is a hex string like "FFFFFF" or "#FFFFFF". The device is "auto"
/// or an id from systemInfo's device list.
/// </summary>
public sealed record OptionsDto(
    string? InputPath,
    string? CardFolderPath,
    string? OutputPath,
    int?    CardsPerRow,
    int?    CardWidth,
    bool?   PatchMatch,
    int?    MatchWidth,
    int?    MatchCandidates,
    bool?   LabSsd,
    bool?   DeepZoom,
    string? BackgroundColor,
    int?    TransparencyThreshold,
    string? Device)
{
    private static readonly MosaicOptions Defaults =
        new() { InputPath = "", CardFolderPath = "", OutputPath = "" };

    /// <summary>
    /// Converts to engine options. Problems that stop the conversion itself (an unreadable
    /// colour) are added to <paramref name="errors"/>; call <see cref="MosaicOptions.Validate"/>
    /// on the result for the rest.
    /// </summary>
    public MosaicOptions ToOptions(List<string> errors)
    {
        var background = Defaults.Background;
        if (!string.IsNullOrWhiteSpace(BackgroundColor))
        {
            try   { background = ImageIO.ParseBackgroundColor(BackgroundColor); }
            catch { errors.Add($"Background colour '{BackgroundColor}' is not a valid hex colour."); }
        }

        return new MosaicOptions
        {
            InputPath             = InputPath      ?? "",
            CardFolderPath        = CardFolderPath ?? "",
            OutputPath            = OutputPath     ?? "",
            CardsPerRow           = CardsPerRow           ?? Defaults.CardsPerRow,
            CardWidth             = CardWidth             ?? Defaults.CardWidth,
            PatchMatch            = PatchMatch            ?? Defaults.PatchMatch,
            MatchWidth            = MatchWidth            ?? Defaults.MatchWidth,
            MatchCandidates       = MatchCandidates       ?? Defaults.MatchCandidates,
            LabSsd                = LabSsd                ?? Defaults.LabSsd,
            DeepZoom              = DeepZoom              ?? Defaults.DeepZoom,
            Background            = background,
            TransparencyThreshold = TransparencyThreshold ?? Defaults.TransparencyThreshold,
            Device                = string.IsNullOrWhiteSpace(Device) ? Defaults.Device : Device,
        };
    }
}

// ── Messages (sidecar to app) ─────────────────────────────────────────────────

public abstract record Message([property: JsonPropertyOrder(-1)] string Type);

/// <summary>Sent once at startup, when the sidecar is ready for requests.</summary>
public sealed record ReadyEvent(int ProtocolVersion) : Message("ready");

/// <summary>The answer to one request, matched to it by <see cref="Id"/>.</summary>
public sealed record Response(int? Id, bool Ok, object? Result, string? Error) : Message("response")
{
    public static Response Success(int? id, object? result) => new(id, true, result, null);
    public static Response Failure(int? id, string error)   => new(id, false, null, error);
}

// Events from a running job. Not tied to a request id; there is only ever one job.
public sealed record StageEvent(MosaicStage Stage) : Message("stage");
public sealed record ProgressEvent(MosaicStage Stage, double Fraction) : Message("progress");
public sealed record LogEvent(string Text) : Message("log");
/// <param name="Device">The device matching actually ran on (the processor if a GPU failed).</param>
public sealed record DoneEvent(
    string OutputPath, MosaicLayout Layout, int BlankTiles, double ElapsedSeconds, DeviceInfo Device) : Message("done");
public sealed record CancelledEvent() : Message("cancelled");
public sealed record FailedEvent(string Error) : Message("failed");

// ── Results (the "result" field of a successful response) ───────────────────

/// <param name="Devices">Every device that can run matching, best first. The processor is always last.</param>
public sealed record SystemInfoResult(IReadOnlyList<DeviceInfo> Devices);

/// <param name="AspectRatio">Card height / width, or null when the folder has no images.</param>
/// <param name="HasCache">Whether allCardLabData.json exists, i.e. this folder was used before.</param>
public sealed record CardFolderInfoResult(int ImageCount, float? AspectRatio, bool HasCache);

public sealed record ImageInfoResult(int Width, int Height);

public sealed record ValidateResult(IReadOnlyList<string> Errors);

/// <param name="Path">What would be written: the PNG file, or the Deep Zoom folder.</param>
public sealed record OutputExistsResult(bool Exists, string Path);

public sealed record CancelResult(bool WasRunning);
