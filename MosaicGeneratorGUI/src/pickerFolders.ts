// The last folder each file picker was used in, remembered between sessions in local storage.
// Each picker has its own, so choosing a card folder doesn't change where the source image
// picker opens. Drag-and-drop counts as using the picker it stands in for.

export type Picker = "input" | "cardFolder" | "output";

const STORAGE_KEY = "mosaic-generator.picker-folders.v1";

export function lastFolder(picker: Picker): string | undefined {
  return load()[picker];
}

export function rememberFolder(picker: Picker, folder: string): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...load(), [picker]: folder }));
  } catch {
    // Not being able to remember a folder isn't worth bothering the user about.
  }
}

function load(): Partial<Record<Picker, string>> {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}");
  } catch {
    return {};
  }
}
