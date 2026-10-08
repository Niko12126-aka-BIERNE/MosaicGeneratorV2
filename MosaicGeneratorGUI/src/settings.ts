// The user's settings, remembered between sessions in the webview's local storage.

export interface Settings {
  inputPath: string | null;
  cardFolderPath: string | null;
  cardsPerRow: number;
  cardWidth: number;
  patchMatch: boolean;
  deepZoom: boolean;
  matchWidth: number;
  matchCandidates: number;
  labSsd: boolean;
  /** "#rrggbb", as used by <input type="color">. */
  backgroundColor: string;
  transparencyThreshold: number;
}

/** Same defaults as the engine (MosaicOptions.cs). */
export const DEFAULT_SETTINGS: Settings = {
  inputPath: null,
  cardFolderPath: null,
  cardsPerRow: 100,
  cardWidth: 160,
  patchMatch: false,
  deepZoom: false,
  matchWidth: 64,
  matchCandidates: 500,
  labSsd: false,
  backgroundColor: "#000000",
  transparencyThreshold: 70,
};

const STORAGE_KEY = "mosaic-generator.settings.v1";

// The source image is never remembered: each session starts without one, since you rarely
// make a mosaic of the same image twice. Everything else is kept.

export function loadSettings(): Settings {
  try {
    const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}");
    return { ...DEFAULT_SETTINGS, ...saved, inputPath: null };
  } catch {
    return DEFAULT_SETTINGS;
  }
}

export function saveSettings(settings: Settings): void {
  try {
    const { inputPath: _notRemembered, ...remembered } = settings;
    localStorage.setItem(STORAGE_KEY, JSON.stringify(remembered));
  } catch {
    // Not being able to remember settings isn't worth bothering the user about.
  }
}
