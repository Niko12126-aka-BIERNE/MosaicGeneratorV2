// The user's settings, remembered between sessions in the webview's local storage.

import recommended from "./recommendedSettings.json";

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
  colorWeightEnabled: boolean;
  /** Kept while the option is off, so turning it back on restores it. */
  colorWeight: number;
  /** "#rrggbb", as used by <input type="color">. */
  backgroundColor: string;
  transparencyThreshold: number;
  /** "auto" or a device id. Falls back to "auto" if that device isn't there anymore. */
  device: string;
}

/**
 * Same defaults as the engine (MosaicOptions.cs), except that color weight is on, since the
 * GUI is meant to give a good result without tweaking.
 */
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
  colorWeightEnabled: true,
  colorWeight: 4, // MosaicOptions.RecommendedColorWeight
  backgroundColor: "#000000",
  transparencyThreshold: 70,
  device: "auto",
};

/**
 * What the "Use recommended settings" button sets, from recommendedSettings.json. Edit that
 * file to change them. Settings left out of it (paths, device, background) are kept as they are.
 */
export const RECOMMENDED_SETTINGS = onlySettings(recommended);

/** Makes a misspelled or unknown setting name in the JSON file a type error. */
function onlySettings<T extends Partial<Settings> & Record<Exclude<keyof T, keyof Settings>, never>>(values: T): Partial<Settings> {
  return values;
}

export function matchesRecommended(settings: Settings): boolean {
  return (Object.keys(RECOMMENDED_SETTINGS) as (keyof Settings)[]).every((key) => settings[key] === RECOMMENDED_SETTINGS[key]);
}

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
