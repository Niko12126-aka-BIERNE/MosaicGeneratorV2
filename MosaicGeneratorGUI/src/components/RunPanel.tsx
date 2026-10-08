// The Generate button, live progress while a mosaic is being made, and the result afterwards.

import { useEffect, useRef, useState } from "react";
import { openPath, revealItemInDir } from "@tauri-apps/plugin-opener";
import type { JobState } from "../useJob";
import type { MosaicStage } from "../sidecar/protocol";

const STAGES: { stage: MosaicStage; label: string }[] = [
  { stage: "loadingCards", label: "Loading cards" },
  { stage: "analysingImage", label: "Analysing the image" },
  { stage: "matching", label: "Matching cards to the image" },
  { stage: "writingOutput", label: "Writing the mosaic" },
];

export function RunPanel(props: {
  job: JobState;
  /** Why Generate can't be pressed yet, or null when it can. */
  blocker: string | null;
  onGenerate: () => void;
  onCancel: () => void;
}) {
  const { job } = props;
  const running = job.status === "running";

  return (
    <div className="space-y-4">
      <button
        type="button"
        onClick={props.onGenerate}
        disabled={running || props.blocker !== null}
        className="w-full rounded-lg bg-indigo-600 px-4 py-3 text-base font-semibold text-white shadow-sm hover:bg-indigo-500 disabled:cursor-not-allowed disabled:bg-slate-300 dark:disabled:bg-neutral-700"
      >
        {running ? "Generating…" : "Generate mosaic"}
      </button>
      {!running && props.blocker && <p className="text-center text-sm text-slate-500 dark:text-neutral-400">{props.blocker}</p>}

      {job.status === "running" && <Progress job={job} onCancel={props.onCancel} />}

      {job.status === "done" && (
        <div className="rounded-lg border border-emerald-200 dark:border-emerald-800 bg-emerald-50 dark:bg-emerald-950/50 p-4">
          <p className="font-medium text-emerald-900 dark:text-emerald-200">Your mosaic is ready</p>
          <p className="mb-3 text-sm text-emerald-800 dark:text-emerald-300">
            {job.layout.width.toLocaleString()} × {job.layout.height.toLocaleString()} px, made in{" "}
            <span className="whitespace-nowrap">{formatDuration(job.elapsedSeconds)}</span>
          </p>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void openPath(job.outputPath)}
              className="rounded-lg bg-emerald-600 px-3 py-2 text-sm font-medium text-white hover:bg-emerald-500"
            >
              Open mosaic
            </button>
            <button
              type="button"
              onClick={() => void revealItemInDir(job.outputPath)}
              className="rounded-lg border border-emerald-300 dark:border-emerald-700 bg-white dark:bg-neutral-900 px-3 py-2 text-sm font-medium text-emerald-900 dark:text-emerald-200 hover:bg-emerald-100 dark:hover:bg-emerald-900/50"
            >
              Show in folder
            </button>
          </div>
        </div>
      )}

      {job.status === "cancelled" && (
        <p className="rounded-lg bg-slate-100 dark:bg-neutral-800 p-3 text-sm text-slate-700 dark:text-neutral-300">
          Cancelled. Any partly written output was removed.
        </p>
      )}

      {job.status === "failed" && (
        <div className="rounded-lg border border-red-200 dark:border-red-900 bg-red-50 dark:bg-red-950/50 p-3 text-sm text-red-800 dark:text-red-200">
          <p className="font-medium">Something went wrong</p>
          <p className="whitespace-pre-line">{job.error}</p>
        </div>
      )}
    </div>
  );
}

function Progress(props: { job: Extract<JobState, { status: "running" }>; onCancel: () => void }) {
  const { job } = props;
  const elapsed = useElapsedSeconds(job.startedAt);
  const index = job.stage ? STAGES.findIndex((s) => s.stage === job.stage) : -1;
  const label = index >= 0 ? STAGES[index].label : "Starting…";

  return (
    <div className="space-y-2">
      <div className="flex items-baseline justify-between gap-2 text-sm">
        <span className="min-w-0 font-medium text-slate-800 dark:text-neutral-200">
          {index >= 0 && <span className="text-slate-500 dark:text-neutral-400">Step {index + 1} of 4 · </span>}
          {label}
        </span>
        {/* Never wraps or shrinks, so "1 min 34 s" always stays on one line. */}
        <span className="shrink-0 whitespace-nowrap tabular-nums text-slate-500 dark:text-neutral-400">{formatDuration(elapsed)}</span>
      </div>

      <div className="h-2.5 overflow-hidden rounded-full bg-slate-200 dark:bg-neutral-700">
        {job.fraction === null ? (
          <div className="progress-indeterminate h-full w-1/3 rounded-full bg-indigo-500" />
        ) : (
          <div
            className="h-full rounded-full bg-indigo-500 transition-[width] duration-200"
            style={{ width: `${Math.round(job.fraction * 100)}%` }}
          />
        )}
      </div>

      <div className="flex items-center justify-between">
        <span className="text-xs text-slate-500 dark:text-neutral-400">
          {job.fraction !== null ? `${Math.round(job.fraction * 100)}%` : ""}
          {job.stage === "matching" && " The GPU part of this step can take a while."}
        </span>
        <button
          type="button"
          onClick={props.onCancel}
          disabled={job.cancelling}
          className="rounded-lg border border-slate-300 dark:border-neutral-700 bg-white dark:bg-neutral-900 px-3 py-1.5 text-sm hover:bg-slate-50 dark:hover:bg-neutral-800 disabled:opacity-60"
        >
          {job.cancelling ? "Cancelling…" : "Cancel"}
        </button>
      </div>
    </div>
  );
}

/** The engine's log lines, collapsed by default. Scrolls to the newest line. */
export function LogPanel(props: { lines: string[] }) {
  const [open, setOpen] = useState(false);
  const end = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (open) end.current?.scrollIntoView({ block: "nearest" });
  }, [open, props.lines]);

  if (props.lines.length === 0) return null;

  return (
    <div>
      <button
        type="button"
        onClick={() => setOpen(!open)}
        className="text-sm text-slate-600 dark:text-neutral-400 underline-offset-2 hover:underline"
      >
        {open ? "Hide details" : "Show details"}
      </button>
      {open && (
        <pre className="mt-2 max-h-72 overflow-auto rounded-lg bg-slate-900 p-3 text-xs dark:bg-neutral-950 dark:ring-1 dark:ring-neutral-800 leading-relaxed text-slate-100">
          {props.lines.join("\n")}
          <div ref={end} />
        </pre>
      )}
    </div>
  );
}

function useElapsedSeconds(since: number): number {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  return Math.max(0, (now - since) / 1000);
}

function formatDuration(seconds: number): string {
  if (seconds < 10) return `${seconds.toFixed(1)} s`;
  const s = Math.round(seconds);
  const m = Math.floor(s / 60);
  return m > 0 ? `${m} min ${String(s % 60).padStart(2, "0")} s` : `${s} s`;
}
