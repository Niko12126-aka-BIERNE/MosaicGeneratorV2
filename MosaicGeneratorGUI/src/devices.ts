import type { DeviceInfo } from "./sidecar/protocol";

/** How a device is shown: "NVIDIA GeForce RTX 3080 Ti · CUDA", or just "Processor (24 threads)". */
export function deviceLabel(device: DeviceInfo): string {
  return device.kind === "cpu" ? device.name : `${device.name} · ${device.kindName}`;
}
