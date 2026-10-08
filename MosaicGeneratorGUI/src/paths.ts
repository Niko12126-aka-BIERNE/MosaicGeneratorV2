import { basename, dirname, join } from "@tauri-apps/api/path";
import { sidecar } from "./sidecar/client";

/** Image types the engine can read, as file extensions. */
export const IMAGE_EXTENSIONS = ["png", "jpg", "jpeg", "bmp", "gif", "webp"];

export function isImagePath(path: string): boolean {
  const extension = path.match(/\.([^.\\/]+)$/)?.[1]?.toLowerCase();
  return extension !== undefined && IMAGE_EXTENSIONS.includes(extension);
}

/**
 * Suggests "<input name>_Mosaic.png" (or a "<input name>_Mosaic" folder for Deep Zoom) next to
 * the input image, numbered "_Mosaic1", "_Mosaic2"... if taken, like the CLI.
 */
export async function suggestOutputPath(inputPath: string, deepZoom: boolean): Promise<string> {
  const dir  = await dirname(inputPath);
  const stem = (await basename(inputPath)).replace(/\.[^.]+$/, "") + "_Mosaic";
  const ext  = deepZoom ? "" : ".png";

  for (let n = 0; n < 1000; n++) {
    const candidate = await join(dir, `${stem}${n === 0 ? "" : n}${ext}`);
    const { exists } = await sidecar.request("outputExists", { outputPath: candidate, deepZoom });
    if (!exists) return candidate;
  }
  return join(dir, stem + ext);
}

/** Converts an output path when switching between PNG (a .png file) and Deep Zoom (a folder). */
export function convertOutputPath(path: string, deepZoom: boolean): string {
  const withoutPng = path.replace(/\.png$/i, "");
  return deepZoom ? withoutPng : withoutPng + ".png";
}
