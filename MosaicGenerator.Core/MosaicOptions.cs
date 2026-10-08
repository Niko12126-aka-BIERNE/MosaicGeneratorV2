using MosaicGenerator.Core.Compute;
using SixLabors.ImageSharp;

namespace MosaicGenerator.Core;

/// <summary>
/// Every setting for one mosaic run. Defaults match the CLI's defaults.
/// </summary>
public sealed record MosaicOptions
{
    public required string InputPath      { get; init; }
    public required string CardFolderPath { get; init; }

    /// <summary>PNG mode: path to the .png file. Deep Zoom mode: path to the output folder.</summary>
    public required string OutputPath     { get; init; }

    public int  CardsPerRow     { get; init; } = 100;
    public int  CardWidth       { get; init; } = 160;
    public bool PatchMatch      { get; init; } = false;
    public int  MatchWidth      { get; init; } = 64;
    public int  MatchCandidates { get; init; } = 500;
    public bool LabSsd          { get; init; } = false;
    public bool DeepZoom        { get; init; } = false;

    /// <summary>Background used to flatten transparent pixels for matching only.</summary>
    public Color Background     { get; init; } = Color.Black;

    /// <summary>Tiles more transparent than this percentage are left blank in the output.</summary>
    public int TransparencyThreshold { get; init; } = 70;

    /// <summary>
    /// Which device runs the matching: "auto" (the best one, falling back to the processor if a
    /// GPU fails), "cuda", "opencl", "cpu", or an exact id. See <see cref="ComputeDevices.Open"/>.
    /// </summary>
    public string Device { get; init; } = ComputeDevices.Auto;

    /// <summary>
    /// Checks the options for mistakes that can be caught before starting a run.
    /// Returns a list of human-readable problems; an empty list means the options are usable.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(InputPath))
            errors.Add("No input image selected.");
        else if (!File.Exists(InputPath))
            errors.Add($"Input image not found: {InputPath}");

        if (string.IsNullOrWhiteSpace(CardFolderPath))
            errors.Add("No card folder selected.");
        else if (!Directory.Exists(CardFolderPath))
            errors.Add($"Card folder not found: {CardFolderPath}");

        if (string.IsNullOrWhiteSpace(OutputPath))
            errors.Add("No output path selected.");

        if (CardsPerRow < 1)
            errors.Add("Cards per row must be at least 1.");
        if (CardWidth < 1)
            errors.Add("Card width must be at least 1 pixel.");

        if (PatchMatch)
        {
            if (MatchWidth < 1)
                errors.Add("Match width must be at least 1 pixel.");
            if (MatchCandidates < 1)
                errors.Add("Match candidates must be at least 1.");
        }

        if (TransparencyThreshold is < 0 or > 100)
            errors.Add("Transparency threshold must be between 0 and 100.");

        if (string.IsNullOrWhiteSpace(Device))
            errors.Add("No device selected.");

        return errors;
    }
}
