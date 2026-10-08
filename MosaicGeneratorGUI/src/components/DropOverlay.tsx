// What you see while dragging files onto the window, and the notice after a drop that can't be used.

import type { DragState } from "../useFileDrop";

/** Covers the window while something is dragged over it, saying what dropping it will do. */
export function DropOverlay({ drag }: { drag: DragState }) {
  if (!drag) return null;

  const rejected = drag !== "checking" && drag.kind === "rejected";
  const [icon, title, detail] =
    drag === "checking"          ? [null, "Checking…", null]
    : drag.kind === "image"      ? [<ImageIcon />, "Use as source image", fileName(drag.path)]
    : drag.kind === "cardFolder" ? [<FolderIcon />, "Use as card folder", fileName(drag.path)]
    : [<BlockedIcon />, "Can't use this", drag.reason];

  return (
    // pointer-events-none: the overlay must never get in the way of the drop itself.
    <div className="pointer-events-none fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 p-8 backdrop-blur-sm dark:bg-neutral-950/70">
      <div
        className={`flex w-full max-w-md flex-col items-center gap-2 rounded-2xl border-2 border-dashed bg-white px-8 py-10 text-center shadow-xl dark:bg-neutral-900 ${
          rejected ? "border-red-400 dark:border-red-500/70" : "border-indigo-400 dark:border-indigo-500/70"
        }`}
      >
        {icon && <div className={rejected ? "text-red-500 dark:text-red-400" : "text-indigo-500 dark:text-indigo-400"}>{icon}</div>}
        <p className="text-lg font-semibold text-slate-900 dark:text-neutral-100">{title}</p>
        {detail && <p className="break-all text-sm text-slate-500 dark:text-neutral-400">{detail}</p>}
      </div>
    </div>
  );
}

/** A short message at the bottom of the window, e.g. why a drop was refused. */
export function Notice({ message, onClose }: { message: string; onClose: () => void }) {
  return (
    <div className="fixed inset-x-0 bottom-6 z-50 flex justify-center px-6">
      <div
        role="status"
        className="flex max-w-lg items-start gap-3 rounded-lg border border-red-200 bg-white px-4 py-3 text-sm shadow-lg dark:border-red-500/40 dark:bg-neutral-900"
      >
        <span className="text-red-600 dark:text-red-400">{message}</span>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close"
          className="text-slate-400 hover:text-slate-600 dark:text-neutral-500 dark:hover:text-neutral-300"
        >
          ✕
        </button>
      </div>
    </div>
  );
}

function fileName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).pop() ?? path;
}

const iconProps = {
  viewBox: "0 0 24 24",
  className: "h-10 w-10",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.75,
  strokeLinecap: "round",
  strokeLinejoin: "round",
} as const;

function ImageIcon() {
  return (
    <svg {...iconProps}>
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <circle cx="9" cy="9" r="2" />
      <path d="m21 15-5-5L5 21" />
    </svg>
  );
}

function FolderIcon() {
  return (
    <svg {...iconProps}>
      <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
    </svg>
  );
}

function BlockedIcon() {
  return (
    <svg {...iconProps}>
      <circle cx="12" cy="12" r="9" />
      <path d="m5.6 5.6 12.8 12.8" />
    </svg>
  );
}
