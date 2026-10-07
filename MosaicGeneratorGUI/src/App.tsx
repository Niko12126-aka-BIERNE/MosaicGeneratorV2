// Step 4 test screen: checks that the sidecar starts and answers, and that the native
// file dialogs work. Replaced by the real layout in step 5.

import { useEffect, useState } from "react";
import { open, save } from "@tauri-apps/plugin-dialog";
import { basename, dirname, join } from "@tauri-apps/api/path";
import { sidecar } from "./sidecar/client";

const IMAGE_EXTENSIONS = ["png", "jpg", "jpeg", "bmp", "gif", "webp"];

type Status =
  | { state: "idle" }
  | { state: "loading" }
  | { state: "ok"; text: string }
  | { state: "error"; text: string };

export default function App() {
  const [engine, setEngine] = useState<Status>({ state: "loading" });

  const [inputPath, setInputPath] = useState<string | null>(null);
  const [inputStatus, setInputStatus] = useState<Status>({ state: "idle" });

  const [cardFolder, setCardFolder] = useState<string | null>(null);
  const [cardStatus, setCardStatus] = useState<Status>({ state: "idle" });

  const [outputPath, setOutputPath] = useState<string | null>(null);

  useEffect(() => {
    sidecar
      .request("systemInfo")
      .then(({ cudaDevices }) =>
        setEngine(
          cudaDevices.length > 0
            ? { state: "ok", text: `Engine ready. GPU: ${cudaDevices.join(", ")}` }
            : { state: "error", text: "Engine ready, but no NVIDIA GPU with CUDA support was found." },
        ),
      )
      .catch((error: Error) => setEngine({ state: "error", text: error.message }));
  }, []);

  async function pickInput() {
    const path = await open({ filters: [{ name: "Images", extensions: IMAGE_EXTENSIONS }] });
    if (!path) return;
    setInputPath(path);
    setInputStatus({ state: "loading" });
    try {
      const { width, height } = await sidecar.request("imageInfo", { path });
      setInputStatus({ state: "ok", text: `${width} × ${height} px` });
    } catch (error) {
      setInputStatus({ state: "error", text: (error as Error).message });
    }
  }

  async function pickCardFolder() {
    const path = await open({ directory: true });
    if (!path) return;
    setCardFolder(path);
    setCardStatus({ state: "loading" });
    try {
      const info = await sidecar.request("cardFolderInfo", { path });
      const parts = [`${info.imageCount.toLocaleString()} card images`];
      if (info.aspectRatio !== undefined) parts.push(`card shape ${info.aspectRatio.toFixed(3)} (height / width)`);
      parts.push(info.hasCache ? "used before" : "first use: cards will be analysed on the first run");
      setCardStatus({ state: "ok", text: parts.join(" · ") });
    } catch (error) {
      setCardStatus({ state: "error", text: (error as Error).message });
    }
  }

  async function pickOutput() {
    // Suggest <input name>_Mosaic.png next to the input image, like the CLI does.
    let defaultPath: string | undefined;
    if (inputPath) {
      const stem = (await basename(inputPath)).replace(/\.[^.]+$/, "");
      defaultPath = await join(await dirname(inputPath), `${stem}_Mosaic.png`);
    }
    const path = await save({ defaultPath, filters: [{ name: "PNG image", extensions: ["png"] }] });
    if (path) setOutputPath(path);
  }

  return (
    <main className="min-h-screen bg-slate-100 p-8 text-slate-800">
      <div className="mx-auto max-w-3xl space-y-6">
        <header>
          <h1 className="text-2xl font-semibold">Mosaic Generator</h1>
          <p className="text-sm text-slate-500">Connection test: sidecar and file dialogs</p>
        </header>

        <section className="rounded-lg bg-white p-4 shadow-sm">
          <h2 className="mb-1 font-medium">Engine</h2>
          <StatusText status={engine} loadingText="Starting the mosaic engine..." />
        </section>

        <section className="space-y-4 rounded-lg bg-white p-4 shadow-sm">
          <PathRow label="Source image" path={inputPath} status={inputStatus} onBrowse={pickInput} />
          <PathRow label="Card folder" path={cardFolder} status={cardStatus} onBrowse={pickCardFolder} />
          <PathRow label="Save to" path={outputPath} status={{ state: "idle" }} onBrowse={pickOutput} />
        </section>
      </div>
    </main>
  );
}

function PathRow(props: { label: string; path: string | null; status: Status; onBrowse: () => void }) {
  return (
    <div>
      <div className="flex items-center gap-3">
        <span className="w-28 shrink-0 text-sm font-medium">{props.label}</span>
        <span className="min-w-0 flex-1 truncate rounded border border-slate-200 bg-slate-50 px-2 py-1 text-sm">
          {props.path ?? <span className="text-slate-400">Nothing selected</span>}
        </span>
        <button
          onClick={props.onBrowse}
          className="rounded bg-slate-800 px-3 py-1 text-sm text-white hover:bg-slate-700"
        >
          Browse
        </button>
      </div>
      <div className="ml-31 mt-1">
        <StatusText status={props.status} loadingText="Reading..." />
      </div>
    </div>
  );
}

function StatusText({ status, loadingText }: { status: Status; loadingText: string }) {
  switch (status.state) {
    case "idle":
      return null;
    case "loading":
      return <p className="text-sm text-slate-500">{loadingText}</p>;
    case "ok":
      return <p className="text-sm text-emerald-700">{status.text}</p>;
    case "error":
      return <p className="text-sm text-red-700">{status.text}</p>;
  }
}
