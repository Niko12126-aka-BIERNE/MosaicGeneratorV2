// Builds the C# sidecar (MosaicGenerator.Sidecar) as one self-contained executable and copies
// it to src-tauri/binaries/ under the name Tauri expects for a bundled sidecar:
//   mosaic-sidecar-<target triple>[.exe], e.g. mosaic-sidecar-x86_64-pc-windows-msvc.exe
//
// Runs automatically before `npm run tauri dev` and `npm run tauri build` (see tauri.conf.json).
// Run it on its own with `npm run sidecar`.

import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const guiDir     = join(dirname(fileURLToPath(import.meta.url)), "..");
const project    = join(guiDir, "..", "MosaicGenerator.Sidecar", "MosaicGenerator.Sidecar.csproj");
const publishDir = join(guiDir, "src-tauri", "target", "sidecar-publish");
const binDir     = join(guiDir, "src-tauri", "binaries");

// Debug for now: ImageSharp 4.x fails Release builds until a Six Labors license is set up.
const configuration = process.env.SIDECAR_CONFIGURATION ?? "Debug";

// The Rust target triple of this machine, e.g. "x86_64-pc-windows-msvc".
const rustcInfo = execFileSync("rustc", ["-vV"], { encoding: "utf8" });
const triple    = rustcInfo.match(/^host: (\S+)$/m)?.[1];
if (!triple) throw new Error("Could not read the host target triple from `rustc -vV`.");

// The matching .NET runtime identifier.
const runtimeIds = {
  "x86_64-pc-windows-msvc":    "win-x64",
  "aarch64-pc-windows-msvc":   "win-arm64",
  "x86_64-unknown-linux-gnu":  "linux-x64",
  "aarch64-unknown-linux-gnu": "linux-arm64",
  "x86_64-apple-darwin":       "osx-x64",
  "aarch64-apple-darwin":      "osx-arm64",
};
const rid = runtimeIds[triple];
if (!rid) throw new Error(`No .NET runtime identifier known for target triple '${triple}'.`);

const ext = triple.includes("windows") ? ".exe" : "";

console.log(`Building sidecar (${configuration}, ${rid})...`);
execFileSync("dotnet", [
  "publish", project,
  "-c", configuration,
  "-r", rid,
  "--self-contained",             // users don't need .NET installed
  "-p:PublishSingleFile=true",    // Tauri bundles a sidecar as a single file
  "-o", publishDir,
  "--nologo", "-v", "quiet",
], { stdio: "inherit" });

mkdirSync(binDir, { recursive: true });
const target = join(binDir, `mosaic-sidecar-${triple}${ext}`);
copyFileSync(join(publishDir, `mosaic-sidecar${ext}`), target);
console.log(`Sidecar ready: ${target}`);
