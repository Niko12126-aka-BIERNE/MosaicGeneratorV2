# MosaicGeneratorGUI

The desktop app for the mosaic generator, built with [Tauri](https://tauri.app/) v2,
React, TypeScript and Tailwind.

## How it fits together

```
React UI (src/)  ──  sidecar client (src/sidecar/client.ts)
                         │  JSON lines over stdin/stdout
                         ▼
                 mosaic-sidecar (../MosaicGenerator.Sidecar)  ──  MosaicGenerator.Core
```

The UI never runs the mosaic engine itself. It starts the C# sidecar as a background
process and sends it requests. See [the sidecar README](../MosaicGenerator.Sidecar/README.md)
for how that works and for the message protocol.

The Rust side (`src-tauri/`) only registers the Tauri plugins the UI uses: `shell` (starts
the sidecar), `dialog` (native file pickers) and `opener` (opens the finished mosaic).

## Requirements

- [Node.js](https://nodejs.org/) and npm
- [Rust](https://www.rust-lang.org/tools/install)
- [.NET 10 SDK](https://dotnet.microsoft.com/download), to build the sidecar
- Optional: a GPU (NVIDIA via CUDA, or AMD/Intel via OpenCL) for faster matching. Without
  one, the app runs the matching on the processor.

## Running

```sh
npm install
npm run tauri dev
```

`tauri dev` first builds the sidecar (`npm run sidecar`), then starts the Vite dev server
and the app. The first run compiles the Rust dependencies and takes a few minutes.

## Project layout

| Path | What it is |
|------|------------|
| `src/App.tsx` | The UI. |
| `src/sidecar/client.ts` | Starts the sidecar and sends requests to it. Use `sidecar.request(...)` and `sidecar.onJobEvent(...)`. |
| `src/sidecar/protocol.ts` | TypeScript types for every sidecar message. Keep in sync with `MosaicGenerator.Sidecar/Protocol.cs`. |
| `scripts/build-sidecar.mjs` | Publishes the sidecar as one self-contained exe into `src-tauri/binaries/`, named with the platform's target triple as Tauri requires. |
| `src-tauri/tauri.conf.json` | App config. `bundle.externalBin` lists the sidecar. |
| `src-tauri/capabilities/default.json` | What the UI is allowed to do: spawn only the sidecar, open dialogs, and so on. |
| `app-icon.svg` | Source for the app icon. After editing it, run `npm run icon` (`scripts/generate-icon.mjs`) to regenerate every size in `src-tauri/icons/`. |

## Notes

- The sidecar is currently built in Debug, because ImageSharp 4.x fails Release builds
  without a Six Labors license. Set `SIDECAR_CONFIGURATION=Release` once that's sorted.
- When the window closes, the app sends the sidecar a `shutdown` request first, so a
  running job can remove its partial output. Tauri force-kills child processes on exit.
