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

export type MosaicStage = "loadingCards" | "analysingImage" | "matching" | "writingOutput";

// Results

export interface SystemInfo {
  /** Empty when there's no CUDA-capable GPU. */
  cudaDevices: string[];
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
  | { type: "done"; outputPath: string; layout: MosaicLayout; blankTiles: number; elapsedSeconds: number }
  | { type: "cancelled" }
  | { type: "failed"; error: string };

export type SidecarMessage = ReadyMessage | ResponseMessage | JobEvent;
