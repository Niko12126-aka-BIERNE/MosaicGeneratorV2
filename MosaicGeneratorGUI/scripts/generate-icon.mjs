// Regenerates every app icon size in src-tauri/icons/ from app-icon.svg.
//
// Run it with `npm run icon` after editing app-icon.svg.

import { execSync } from "node:child_process";
import { rmSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const guiDir   = join(dirname(fileURLToPath(import.meta.url)), "..");
const tauriDir = join(guiDir, "src-tauri");

execSync("npx tauri icon app-icon.svg", { cwd: guiDir, stdio: "inherit" });

// `tauri icon` also makes Android and iOS icons, which this desktop app doesn't use.
for (const folder of ["android", "ios"]) {
  rmSync(join(tauriDir, "icons", folder), { recursive: true, force: true });
}

// The icon is embedded into the exe when it's compiled, but Cargo only recompiles when Rust
// code changes, so the old icon would stay. Cleaning the app crate (not its dependencies)
// makes the next `tauri dev` or `tauri build` rebuild it with the new icon.
execSync("cargo clean -p mosaic-generator-gui", { cwd: tauriDir, stdio: "inherit" });
