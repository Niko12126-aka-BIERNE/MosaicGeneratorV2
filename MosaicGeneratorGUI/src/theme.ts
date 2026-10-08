// Dark (the default) or light mode, remembered between sessions.
// index.html applies the saved theme before the page first draws, to avoid a white flash.

import { getCurrentWindow } from "@tauri-apps/api/window";

export type Theme = "dark" | "light";

// Also read by the inline script in index.html.
const STORAGE_KEY = "mosaic-generator.theme";

export function loadTheme(): Theme {
  try {
    return localStorage.getItem(STORAGE_KEY) === "light" ? "light" : "dark";
  } catch {
    return "dark";
  }
}

/** Switches the page and the window's title bar to `theme`, and remembers it. */
export function applyTheme(theme: Theme): void {
  document.documentElement.classList.toggle("dark", theme === "dark");
  void getCurrentWindow().setTheme(theme).catch(() => {});
  try {
    localStorage.setItem(STORAGE_KEY, theme);
  } catch {
    // Not remembering the theme isn't worth bothering the user about.
  }
}
