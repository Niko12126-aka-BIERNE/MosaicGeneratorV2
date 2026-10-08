// TypeScript versions of the sidecar's messages. These mirror
// MosaicGenerator.Sidecar/Protocol.cs; see MosaicGenerator.Sidecar/README.md for the protocol.

/** Mosaic settings. Anything left out gets the engine's default. */
export interface MosaicOptions {
  inputPath: string;
  cardFolderPath: string;
  /** PNG mode: path to the .png file. Deep Zoom mode: path to the output folder. */
  outputPath: string;
  cardsPerRow?: number;
  cardWidth?: number;
  patchMatch?: boolean;
  matchWidth?: number;
  matchCandidates?: number;
  labSsd?: boolean;
  deepZoom?: boolean;
  /** Hex colour like "FFFFFF" or "#FFFFFF". */
  backgroundColor?: string;
  transparencyThreshold?: number;
  /** "auto", or an `id` from SystemInfo.devices. */
  device?: string;
}

export interface MosaicLayout {
  cols: number;
  rows: number;
  cardWidth: number;
  cardHeight: number;
  width: number;
  height: number;
  tileCount: number;
  pixelCount: number;
}

/** A device that can run the matching. */
export interface DeviceInfo {
  /** e.g. "cuda:0", "opencl:1" or "cpu". */
  id: string;
  name: string;
  kind: "cuda" | "openCL" | "cpu";
  /** "CUDA", "OpenCL" or "CPU", for display. */
  kindName: string;
  memoryBytes: number;
}

export type MosaicStage = "loadingCards" | "analysingImage" | "matching" | "writingOutput";

// Results

export interface SystemInfo {
  /** Every device that can run matching, best first. The processor ("cpu") is always last. */
  devices: DeviceInfo[];
}

export interface CardFolderInfo {
  imageCount: number;
  /** Card height / width. Missing when the folder has no images. */
  aspectRatio?: number;
  /** Whether this folder has been used before (allCardLabData.json exists). */
  hasCache: boolean;
}

export interface ImageInfo {
  width: number;
  height: number;
}

/** Each request type, with the fields it takes and the result it returns. */
export interface Requests {
  systemInfo:     { params: Record<string, never>; result: SystemInfo };
  cardFolderInfo: { params: { path: string }; result: CardFolderInfo };
  imageInfo:      { params: { path: string }; result: ImageInfo };
  layout: {
    params: {
      inputWidth: number;
      inputHeight: number;
      cardsPerRow: number;
      cardWidth: number;
      cardAspectRatio: number;
    };
    result: MosaicLayout;
  };
  outputExists: {
    params: { outputPath: string; deepZoom: boolean };
    /** `path` is what would be written: the PNG file, or the Deep Zoom folder. */
    result: { exists: boolean; path: string };
  };
  pathKind: { params: { path: string }; result: { kind: "file" | "folder" | "missing" } };
  validate: { params: { options: MosaicOptions }; result: { errors: string[] } };
  start:    { params: { options: MosaicOptions }; result: undefined };
  cancel:   { params: Record<string, never>; result: { wasRunning: boolean } };
  shutdown: { params: Record<string, never>; result: undefined };
}

export type RequestType = keyof Requests;

// Messages from the sidecar

export interface ReadyMessage {
  type: "ready";
  protocolVersion: number;
}

export interface ResponseMessage {
  type: "response";
  id?: number;
  ok: boolean;
  result?: unknown;
  error?: string;
}

/** Events from a running job. Every started job ends with exactly one done, cancelled or failed. */
export type JobEvent =
  | { type: "stage"; stage: MosaicStage }
  | { type: "progress"; stage: MosaicStage; fraction: number }
  | { type: "log"; text: string }
  | {
      type: "done";
      outputPath: string;
      layout: MosaicLayout;
      blankTiles: number;
      elapsedSeconds: number;
      /** What matching actually ran on: the processor if a GPU failed in automatic mode. */
      device: DeviceInfo;
    }
  | { type: "cancelled" }
  | { type: "failed"; error: string };

export type SidecarMessage = ReadyMessage | ResponseMessage | JobEvent;
