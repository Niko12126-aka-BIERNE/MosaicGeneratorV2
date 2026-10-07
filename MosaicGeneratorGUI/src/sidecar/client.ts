// Starts the C# sidecar and talks to it: requests go out as JSON lines on its stdin, and
// responses and job events come back as JSON lines on its stdout.
//
// Usage:
//   const info = await sidecar.request("imageInfo", { path });
//   const unsubscribe = sidecar.onJobEvent((event) => { ... });

import { Command, type Child } from "@tauri-apps/plugin-shell";
import type { JobEvent, RequestType, Requests, SidecarMessage } from "./protocol";

/** The protocol version this frontend was written for (Protocol.Version in the sidecar). */
const PROTOCOL_VERSION = 1;

type Pending = { resolve: (result: unknown) => void; reject: (error: Error) => void };

class SidecarClient {
  /** Resolves once the sidecar has started and said it's ready; rejects if it couldn't start. */
  readonly ready: Promise<void>;

  private child: Child | null = null;
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private readonly listeners = new Set<(event: JobEvent) => void>();
  private resolveReady!: () => void;
  private rejectReady!: (error: Error) => void;
  private exited = false;

  constructor() {
    this.ready = new Promise((resolve, reject) => {
      this.resolveReady = resolve;
      this.rejectReady = reject;
    });
    void this.spawn();
  }

  /** Sends a request and resolves with its result, or rejects with the sidecar's error message. */
  async request<T extends RequestType>(
    type: T,
    ...[params]: Requests[T]["params"] extends Record<string, never> ? [] : [Requests[T]["params"]]
  ): Promise<Requests[T]["result"]> {
    await this.ready;
    if (this.exited) throw new Error("The mosaic engine has stopped.");

    const id = this.nextId++;
    const response = new Promise<unknown>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
    });
    await this.child!.write(JSON.stringify({ id, type, ...params }) + "\n");
    return response as Promise<Requests[T]["result"]>;
  }

  /** Calls `listener` for every job event (progress, log lines, done...). Returns an unsubscribe function. */
  onJobEvent(listener: (event: JobEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /**
   * Asks the sidecar to stop any running job, clean up and exit. Gives up after `timeoutMs`,
   * e.g. while a GPU kernel that can't be interrupted is still running.
   */
  async shutdown(timeoutMs: number): Promise<void> {
    if (this.exited || !this.child) return;
    const timeout = new Promise<void>((resolve) => setTimeout(resolve, timeoutMs));
    await Promise.race([this.request("shutdown").catch(() => {}), timeout]);
  }

  /** Stops the sidecar immediately, without cleanup. */
  kill(): void {
    void this.child?.kill();
  }

  private async spawn() {
    // "binaries/mosaic-sidecar" matches bundle.externalBin in tauri.conf.json.
    const command = Command.sidecar("binaries/mosaic-sidecar");

    command.stdout.on("data", (line) => this.handleLine(line));
    command.stderr.on("data", (line) => console.warn("[sidecar stderr]", line));
    command.on("error", (error) => console.error("[sidecar error]", error));
    command.on("close", ({ code }) => this.handleExit(code));

    try {
      this.child = await command.spawn();
    } catch (error) {
      this.exited = true;
      this.rejectReady(new Error(`Could not start the mosaic engine: ${error}`));
    }
  }

  private handleLine(line: string) {
    if (!line.trim()) return;

    let message: SidecarMessage;
    try {
      message = JSON.parse(line);
    } catch {
      console.warn("[sidecar] not a JSON message:", line);
      return;
    }

    switch (message.type) {
      case "ready":
        if (message.protocolVersion !== PROTOCOL_VERSION) {
          this.rejectReady(new Error(
            `The mosaic engine speaks protocol version ${message.protocolVersion}, ` +
            `but this app expects version ${PROTOCOL_VERSION}.`,
          ));
          this.kill();
        } else {
          this.resolveReady();
        }
        break;

      case "response": {
        const pending = message.id !== undefined ? this.pending.get(message.id) : undefined;
        if (!pending) {
          console.warn("[sidecar] response to an unknown request:", message);
          return;
        }
        this.pending.delete(message.id!);
        if (message.ok) pending.resolve(message.result);
        else pending.reject(new Error(message.error ?? "Unknown error"));
        break;
      }

      default:
        this.listeners.forEach((listener) => listener(message));
    }
  }

  private handleExit(code: number | null) {
    this.exited = true;
    const error = new Error(`The mosaic engine stopped (exit code ${code}).`);
    this.rejectReady(error); // no effect if it was already ready
    this.pending.forEach(({ reject }) => reject(error));
    this.pending.clear();
  }
}

/** The one sidecar for the whole app. Created when this module is first imported. */
export const sidecar = new SidecarClient();

// During development, a page reload or a hot update of this file would otherwise leave the
// old sidecar running next to the new one.
window.addEventListener("beforeunload", () => sidecar.kill());
import.meta.hot?.dispose(() => sidecar.kill());
