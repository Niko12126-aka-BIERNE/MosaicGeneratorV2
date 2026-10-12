// Small building blocks shared by the main screen.

import { useEffect, useState, type ReactNode } from "react";
import { isAbsolute } from "@tauri-apps/api/path";

/** A card with a title. `action` goes at the right end of the title row. */
export function Section(props: { title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="rounded-xl border border-slate-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 shadow-sm">
      <div className="mb-4 flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-500 dark:text-neutral-400">{props.title}</h2>
        {props.action}
      </div>
      <div className="space-y-5">{props.children}</div>
    </section>
  );
}

export function FieldLabel(props: { children: ReactNode; hint?: ReactNode }) {
  return (
    <div className="mb-1.5">
      <div className="text-sm font-medium text-slate-800 dark:text-neutral-200">{props.children}</div>
      {props.hint && <div className="text-xs text-slate-500 dark:text-neutral-400">{props.hint}</div>}
    </div>
  );
}

/**
 * A path that can be typed or pasted, or chosen with the Browse button. A typed path is used
 * when the field loses focus or Enter is pressed; Escape undoes the typing. Details or an
 * error go underneath.
 */
export function PathField(props: {
  label: string;
  path: string | null;
  placeholder: string;
  onBrowse: () => void;
  /** A typed path, cleaned up and absolute, or null when the field was emptied. */
  onCommit: (path: string | null) => void;
  detail?: ReactNode;
  error?: string | null;
}) {
  const [draft, setDraft] = useState(props.path ?? "");
  const [typingError, setTypingError] = useState<string | null>(null);

  // Follow paths set from outside (Browse, drag-and-drop, switching output type).
  useEffect(() => {
    setDraft(props.path ?? "");
    setTypingError(null);
  }, [props.path]);

  async function commit() {
    // Explorer's "Copy as path" wraps the path in quotes.
    const path = draft.trim().replace(/^"(.*)"$/, "$1").trim();
    if (path === (props.path ?? "")) {
      setDraft(path);
      setTypingError(null);
      return;
    }
    if (path && !(await isAbsolute(path))) {
      setTypingError("Enter the full path, starting from the drive or root folder.");
      return;
    }
    setTypingError(null);
    props.onCommit(path || null);
  }

  const error = typingError ?? props.error;

  return (
    <div>
      <FieldLabel>{props.label}</FieldLabel>
      <div className="flex gap-2">
        <input
          type="text"
          spellCheck={false}
          value={draft}
          placeholder={props.placeholder}
          title={props.path ?? undefined}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === "Enter") commit();
            if (e.key === "Escape") {
              setDraft(props.path ?? "");
              setTypingError(null);
            }
          }}
          className={
            "min-w-0 flex-1 rounded-lg border bg-white dark:bg-neutral-900 px-3 py-2 text-sm placeholder:text-slate-400 dark:placeholder:text-neutral-500 " +
            (error ? "border-red-500 outline-red-500" : "border-slate-300 dark:border-neutral-700")
          }
        />
        <button
          type="button"
          onClick={props.onBrowse}
          className="rounded-lg border border-slate-300 dark:border-neutral-700 bg-white dark:bg-neutral-900 px-4 py-2 text-sm font-medium hover:bg-slate-50 dark:hover:bg-neutral-800 disabled:opacity-50"
        >
          Browse…
        </button>
      </div>
      {error ? (
        <p className="mt-1.5 text-sm text-red-600 dark:text-red-400">{error}</p>
      ) : (
        props.detail && <div className="mt-1.5 text-sm text-slate-600 dark:text-neutral-400">{props.detail}</div>
      )}
    </div>
  );
}

/** A set of large radio buttons with a title and a short description each. */
export function ChoiceGroup<T extends string>(props: {
  value: T;
  onChange: (value: T) => void;
  options: { value: T; title: string; description: string }[];
}) {
  return (
    <div className="grid gap-2 sm:grid-cols-2">
      {props.options.map((option) => {
        const selected = option.value === props.value;
        return (
          <button
            type="button"
            key={option.value}
            onClick={() => props.onChange(option.value)}
            className={
              "rounded-lg border px-3 py-2.5 text-left transition-colors " +
              (selected
                ? "border-indigo-500 dark:border-indigo-400 bg-indigo-50 dark:bg-indigo-500/15 ring-1 ring-indigo-500 dark:ring-indigo-400"
                : "border-slate-300 dark:border-neutral-700 bg-white dark:bg-neutral-900 hover:border-slate-400 dark:hover:border-neutral-500")
            }
          >
            <div className="text-sm font-medium text-slate-900 dark:text-neutral-100">{option.title}</div>
            <div className="text-xs text-slate-600 dark:text-neutral-400">{option.description}</div>
          </button>
        );
      })}
    </div>
  );
}

/**
 * A whole-number input. While the user is typing something invalid (empty, out of range) the
 * last valid value is kept and the field is outlined in red.
 */
export function NumberField(props: {
  value: number;
  onChange: (value: number) => void;
  min: number;
  max: number;
  suffix?: string;
}) {
  const [draft, setDraft] = useState(String(props.value));

  // Follow outside changes (e.g. restored settings) unless they match what's being typed.
  useEffect(() => {
    if (Number(draft) !== props.value) setDraft(String(props.value));
  }, [props.value]);

  const parsed = Number(draft);
  const valid = draft.trim() !== "" && Number.isInteger(parsed) && parsed >= props.min && parsed <= props.max;

  return (
    <div className="flex items-center gap-2">
      <input
        type="number"
        min={props.min}
        max={props.max}
        value={draft}
        onChange={(e) => {
          setDraft(e.target.value);
          const n = Number(e.target.value);
          if (e.target.value.trim() !== "" && Number.isInteger(n) && n >= props.min && n <= props.max) props.onChange(n);
        }}
        className={
          "w-28 rounded-lg border bg-white px-3 py-2 text-sm dark:bg-neutral-900 " +
          (valid ? "border-slate-300 dark:border-neutral-700" : "border-red-500 outline-red-500")
        }
      />
      {props.suffix && <span className="text-sm text-slate-500 dark:text-neutral-400">{props.suffix}</span>}
    </div>
  );
}
