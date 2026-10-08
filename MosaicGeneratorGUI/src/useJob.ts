// Tracks the one mosaic job: starting it, its progress events, and how it ended.

import { useCallback, useEffect, useState } from "react";
import { sidecar } from "./sidecar/client";
import type { DeviceInfo, JobEvent, MosaicLayout, MosaicOptions, MosaicStage } from "./sidecar/protocol";

export type JobState =
  | { status: "idle" }
  | {
      status: "running";
      /** null until the first stage starts. */
      stage: MosaicStage | null;
      /** How far the current stage is (0 to 1), or null while unknown. */
      fraction: number | null;
      startedAt: number;
      cancelling: boolean;
    }
  | { status: "done"; outputPath: string; layout: MosaicLayout; elapsedSeconds: number; device: DeviceInfo }
  | { status: "cancelled" }
  | { status: "failed"; error: string };

const MAX_LOG_LINES = 2000;

export function useJob() {
  const [state, setState] = useState<JobState>({ status: "idle" });
  const [log, setLog] = useState<string[]>([]);

  useEffect(
    () =>
      sidecar.onJobEvent((event: JobEvent) => {
        switch (event.type) {
          case "log":
            setLog((lines) => [...lines.slice(-(MAX_LOG_LINES - 1)), event.text]);
            break;
          case "stage":
            setState((s) => (s.status === "running" ? { ...s, stage: event.stage, fraction: null } : s));
            break;
          case "progress":
            setState((s) => (s.status === "running" ? { ...s, stage: event.stage, fraction: event.fraction } : s));
            break;
          case "done":
            setState({
              status: "done",
              outputPath: event.outputPath,
              layout: event.layout,
              elapsedSeconds: event.elapsedSeconds,
              device: event.device,
            });
            break;
          case "cancelled":
            setState({ status: "cancelled" });
            break;
          case "failed":
            setState({ status: "failed", error: event.error });
            break;
        }
      }),
    [],
  );

  const start = useCallback(async (options: MosaicOptions) => {
    setLog([]);
    // Set "running" before sending: the first events can arrive before the response does.
    setState({ status: "running", stage: null, fraction: null, startedAt: Date.now(), cancelling: false });
    try {
      await sidecar.request("start", { options });
    } catch (error) {
      setState({ status: "failed", error: (error as Error).message });
    }
  }, []);

  const cancel = useCallback(async () => {
    setState((s) => (s.status === "running" ? { ...s, cancelling: true } : s));
    await sidecar.request("cancel");
  }, []);

  return { state, log, start, cancel };
}
