# MosaicGenerator.Sidecar

The bridge between the GUI (Tauri + React) and the C# mosaic engine (`MosaicGenerator.Core`).

## What is a sidecar?

A sidecar is a separate program that ships inside the app and runs in the background
while the app is open. The Tauri app starts `mosaic-sidecar.exe` as a hidden child
process and talks to it through the child process's standard input and output, the same
two text streams a console program uses when a person types into it and reads what it
prints. Here, the "person" is the app:

```
 Tauri app (React UI)                      mosaic-sidecar
 ────────────────────                      ──────────────
 writes a JSON line to stdin   ───────►   reads the line, handles it
                                           (calls MosaicGenerator.Core)
 reads JSON lines from stdout  ◄───────   writes a response + progress events
```

When the app closes, it sends a `shutdown` request first, because Tauri force-kills child
processes on exit. The sidecar then cancels any running job, cleans up its partial output,
replies, and exits. If stdin is closed without a `shutdown`, the sidecar does the same.

## How the code is organised

| File | What it does |
|------|--------------|
| `Program.cs` | Startup and the main loop: read a line, handle it, send the response. |
| `Handlers.cs` | One method per request type. |
| `MosaicJob.cs` | Runs a mosaic generation on a background thread and sends its events. |
| `Protocol.cs` | The shape of every message, in and out. |
| `Output.cs` | Writes messages to stdout. Nothing else may write there. |

## Trying it by hand

Build and run it, then type requests (one per line) and watch the replies:

```sh
dotnet run --project MosaicGenerator.Sidecar
{"id":1,"type":"systemInfo"}
{"id":2,"type":"cardFolderInfo","path":"D:\\Cards"}
```

Close it with Ctrl+Z then Enter (Windows) or Ctrl+D (macOS/Linux), which closes stdin.

## Protocol

Every message is one line of JSON. Names are camelCase.

### Requests (app to sidecar)

Each request has an `id` (any number the app picks) and a `type`. The sidecar answers
every request with exactly one `response` carrying the same `id`.

| `type` | Fields | `result` on success |
|--------|--------|---------------------|
| `systemInfo` | none | `{ "devices": [{ "id": "cuda:0", "name": "NVIDIA GeForce RTX 3080 Ti", "kind": "cuda", "kindName": "CUDA", "memoryBytes": 12884377600 }, ...] }`: every device that can run matching, best first. The processor (`"id": "cpu"`) is always there, last. |
| `cardFolderInfo` | `path` | `{ "imageCount": 4812, "aspectRatio": 1.3968, "hasCache": true }` (`aspectRatio` is card height / width, left out when the folder has no images; `hasCache` means the folder has been used before) |
| `imageInfo` | `path` | `{ "width": 4000, "height": 3000 }` |
| `layout` | `inputWidth`, `inputHeight`, `cardsPerRow`, `cardWidth`, `cardAspectRatio` | `{ "cols", "rows", "cardWidth", "cardHeight", "width", "height", "tileCount", "pixelCount" }` |
| `outputExists` | `outputPath`, `deepZoom` | `{ "exists": false, "path": "D:\\photo_Mosaic" }` (`path` is what would be written: the PNG file, or the Deep Zoom folder) |
| `pathKind` | `path` | `{ "kind": "folder" }`: `"file"`, `"folder"` or `"missing"`. Used for files dropped on the app window. |
| `validate` | `options` | `{ "errors": ["Card folder not found: ..."] }` (empty list when the options are fine) |
| `start` | `options` | none. The job runs in the background and reports through events. Refused if a job is already running or the options are invalid. |
| `cancel` | none | `{ "wasRunning": true }` |
| `shutdown` | none | none. Cancels any running job and waits for its cleanup, then the sidecar exits after sending this response. |

`options` holds the mosaic settings. Anything left out gets the engine's default:

```json
{
  "inputPath": "D:\\photo.jpg",
  "cardFolderPath": "D:\\Cards",
  "outputPath": "D:\\photo_Mosaic.png",
  "cardsPerRow": 100,
  "cardWidth": 160,
  "patchMatch": false,
  "matchWidth": 64,
  "matchCandidates": 500,
  "labSsd": false,
  "deepZoom": false,
  "backgroundColor": "000000",
  "transparencyThreshold": 70,
  "device": "auto"
}
```

### Messages (sidecar to app)

| `type` | Fields | When |
|--------|--------|------|
| `ready` | `protocolVersion` | Once, at startup. Wait for it before sending requests. |
| `response` | `id`, `ok`, `result` or `error` | Once per request. |
| `stage` | `stage` | A job entered a new stage: `loadingCards`, `analysingImage`, `matching` or `writingOutput`. Its progress isn't known yet. |
| `progress` | `stage`, `fraction` | How far the current stage is, from 0 to 1. At most once per whole percent. |
| `log` | `text` | A log line, the same text the CLI prints. |
| `done` | `outputPath`, `layout`, `blankTiles`, `elapsedSeconds`, `device` | The job finished. `outputPath` is the PNG, or the Deep Zoom `index.html`. `device` is what matching actually ran on: the processor, if a GPU failed in automatic mode. |
| `cancelled` | none | The job stopped after a `cancel`. Partial output has been removed. |
| `failed` | `error` | The job failed. |

Every `start` that succeeds is followed by exactly one `done`, `cancelled` or `failed`.

### Example session

```
→ {"id":1,"type":"start","options":{"inputPath":"D:\\photo.jpg","cardFolderPath":"D:\\Cards","outputPath":"D:\\out.png"}}
← {"type":"response","id":1,"ok":true}
← {"type":"log","text":"Configuration:"}
← {"type":"stage","stage":"loadingCards"}
← {"type":"progress","stage":"loadingCards","fraction":0.42}
   ...
← {"type":"done","outputPath":"D:\\out.png","layout":{...},"blankTiles":0,"elapsedSeconds":12.3}
```
