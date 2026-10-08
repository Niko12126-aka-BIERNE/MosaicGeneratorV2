import { useEffect, useRef, useState } from "react";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { sidecar } from "./sidecar/client";
import { IMAGE_EXTENSIONS, isImagePath } from "./paths";

/** What dropping the dragged files does. */
export type DropAction =
  | { kind: "image"; path: string }
  | { kind: "cardFolder"; path: string }
  | { kind: "rejected"; reason: string };

/** Nothing being dragged, still working out what a drop would do, or what it will do. */
export type DragState = null | "checking" | DropAction;

/**
 * Files dragged onto the window. An image becomes the source image and a folder becomes the
 * card folder. Tauri hands us the real paths, which a plain browser drop wouldn't.
 *
 * Returns the current drag, for showing an overlay, and calls `onDrop` when something is dropped.
 * While `blockedReason` is set, every drop is rejected with it.
 */
export function useFileDrop(onDrop: (action: DropAction) => void, blockedReason: string | null): DragState {
  const [drag, setDrag] = useState<DragState>(null);

  // The listener is registered once, so it reads the latest values through refs.
  const onDropRef = useRef(onDrop);
  onDropRef.current = onDrop;
  const blockedRef = useRef(blockedReason);
  blockedRef.current = blockedReason;

  useEffect(() => {
    // Each drag gets a number, so a slow answer for an earlier drag can't overwrite a later one.
    let dragId = 0;

    const unlisten = getCurrentWebview().onDragDropEvent(async ({ payload }) => {
      switch (payload.type) {
        case "enter": {
          const id = ++dragId;
          setDrag("checking");
          const action = await classify(payload.paths, blockedRef.current);
          if (id === dragId) setDrag(action);
          break;
        }
        case "drop":
          dragId++;
          setDrag(null);
          onDropRef.current(await classify(payload.paths, blockedRef.current));
          break;
        case "leave":
          dragId++;
          setDrag(null);
          break;
      }
    });

    return () => {
      unlisten.then((stop) => stop());
    };
  }, []);

  return drag;
}

async function classify(paths: string[], blockedReason: string | null): Promise<DropAction> {
  if (blockedReason) return { kind: "rejected", reason: blockedReason };
  if (paths.length === 0) return { kind: "rejected", reason: "Only files and folders can be dropped here." };
  if (paths.length > 1) return { kind: "rejected", reason: "Drop one image or one folder at a time." };

  const path = paths[0];
  try {
    const { kind } = await sidecar.request("pathKind", { path });
    if (kind === "folder") return { kind: "cardFolder", path };
    if (kind === "file" && isImagePath(path)) return { kind: "image", path };
    if (kind === "file") {
      const types = IMAGE_EXTENSIONS.filter((e) => e !== "jpeg").map((e) => e.toUpperCase()).join(", ");
      return { kind: "rejected", reason: `That's not an image the generator can read (${types}).` };
    }
    return { kind: "rejected", reason: "That file or folder can't be found." };
  } catch (error) {
    return { kind: "rejected", reason: (error as Error).message };
  }
}
