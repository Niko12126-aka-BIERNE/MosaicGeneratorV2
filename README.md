# MosaicGeneratorV2

A GPU-accelerated photo mosaic generator. Give it a source image and a folder of
tile images (e.g. a Pokémon card collection), and it rebuilds the source image out of
those tiles, either by matching average color or by matching actual patch content, and composites the result into a single PNG or a pannable/zoomable Deep Zoom viewer.

## Features

- **Two matching modes**
  - *Color match* (default): matches each tile by perceptual color distance (CIEDE2000).
  - *Patch match* (`--patch-match`): matches each tile by actual pixel content (SSD), for
    results that follow image detail rather than just average color.
- **GPU-accelerated matching** via [ILGPU](https://github.com/m4rs-mt/ILGPU) on CUDA. Brute-force
  color matching, or a CPU color pre-filter + GPU patch SSD for patch matching.
- **Transparency-aware**: transparent regions of the input image are left blank in the
  output (real alpha channel) instead of being treated as solid black, and cards near
  the edge of a transparent region are placed as whole cards rather than being clipped
  to the silhouette.
- **Two output formats**
  - A single streaming PNG, written directly to disk without ever holding the full
    image in memory (so output size is limited only by disk space).
  - Deep Zoom tiles + an `index.html` viewer ([OpenSeadragon](https://openseadragon.github.io/))
    for mosaics too large to view or export as one flat image.
- **Incremental card database**: each card's average color is cached to
  `allCardLabData.json` in the card folder, so only newly added cards are reprocessed
  on subsequent runs.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- An NVIDIA GPU with CUDA support and up-to-date drivers (matching is GPU-only)
- A folder of tile images to build mosaics from (JPG, PNG, BMP, GIF, or WEBP)

## Building

```sh
dotnet build MosaicGenerator.slnx -c Release
```

The solution is split into:

- `MosaicGenerator.Core`: the mosaic engine (card database, GPU matching, compositing, PNG/Deep Zoom writers).
- `MosaicGeneratorCLI`: the command-line front end described below.
- `MosaicGenerator.Sidecar`: a background process the GUI talks to over JSON lines on
  stdin/stdout. See its [README](MosaicGenerator.Sidecar/README.md).
- `MosaicGeneratorGUI`: the desktop app (Tauri + React). See its [README](MosaicGeneratorGUI/README.md).

## Usage

```
MosaicGeneratorCLI.exe <input-image> <card-folder> [output] [options]
```

### Arguments

| Argument         | Description |
|------------------|-------------|
| `<input-image>`  | Path to the source image to recreate as a mosaic. |
| `<card-folder>`  | Path to the folder of tile/card images. A cache file (`allCardLabData.json`) is created here on first run. |
| `[output]`       | Optional output path. For PNG mode, a path to a `.png` file; for Deep Zoom mode, a path to an output folder. If omitted, defaults to `<input-name>_Mosaic.png` (or a numbered variant if that already exists) in the current directory. |

### Options

| Flag | Default | Description |
|------|---------|-------------|
| `--cards-per-row <n>` | `100` | Number of tile columns across the mosaic. Together with `--card-width`, this sets the overall output resolution. |
| `--card-width <n>` | `160` | Width of each tile in the output, in pixels. Tile height is derived from the card folder's average aspect ratio. |
| `--patch-match` | off | Match tile *content* (SSD) instead of average color. |
| `--match-width <n>` | `64` | Resolution (in pixels) used for patch matching. Higher values improve match quality at the cost of VRAM and processing time. Only used with `--patch-match`. |
| `--match-candidates <n>` | `500` | Number of color-prefiltered candidate cards considered per tile before the GPU patch comparison. Higher values improve match quality at the cost of VRAM and processing time. Only used with `--patch-match`. |
| `--lab-ssd` | off | Use perceptually uniform LAB SSD instead of RGB SSD for patch matching. Slower, but can give better color/contrast results. Only used with `--patch-match`. |
| `--deep-zoom` | off | Output a Deep Zoom tile pyramid + HTML viewer instead of a single PNG. Recommended for large mosaics. |
| `--background-color <hex>` | `black` | Background color used to flatten transparent pixels *for matching purposes only* (e.g. `FFFFFF` for white). Doesn't affect the final output's transparency. |
| `--transparency-threshold <pct>` | `70` | Tiles more transparent than this (on average) are left blank in the output rather than getting a card. |

### Output modes

- **PNG** (default): a single flat `.png` file containing the whole mosaic.
- **Deep Zoom** (`--deep-zoom`): a folder containing a Deep Zoom tile pyramid, an
  `index.dzi` descriptor, and an `index.html` viewer. Open `index.html` in a browser to
  pan and zoom. Better suited to very large mosaics that would be impractical to view or
  export as a single image.

## Examples

Basic color-matched mosaic, 100 cards wide:

```sh
MosaicGeneratorCLI.exe photo.jpg .\Cards photo_Mosaic.png
```

Patch-matched mosaic, wider grid, viewed as a Deep Zoom pyramid:

```sh
MosaicGeneratorCLI.exe photo.jpg .\Cards output --cards-per-row 300 --patch-match --deep-zoom
```

Transparent input image, only placing cards where the image is at least half opaque:

```sh
MosaicGeneratorCLI.exe logo.png .\Cards logo_Mosaic.png --transparency-threshold 50
```

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.
