import { useEffect, useState } from "react";
import { ask, open, save } from "@tauri-apps/plugin-dialog";
import { convertFileSrc } from "@tauri-apps/api/core";
import { sidecar } from "./sidecar/client";
import type { CardFolderInfo, DeviceInfo, ImageInfo, MosaicLayout, MosaicOptions } from "./sidecar/protocol";
import { DEFAULT_SETTINGS, loadSettings, saveSettings, type Settings } from "./settings";
import { convertOutputPath, IMAGE_EXTENSIONS, suggestOutputPath } from "./paths";
import { useJob } from "./useJob";
import { applyTheme, loadTheme, type Theme } from "./theme";
import { deviceLabel } from "./devices";
import { ChoiceGroup, FieldLabel, NumberField, PathField, Section } from "./components/ui";
import { LogPanel, RunPanel } from "./components/RunPanel";
import { DropOverlay, Notice } from "./components/DropOverlay";
import { useFileDrop, type DropAction } from "./useFileDrop";

/** Above this many pixels a single PNG gets impractical to open, so suggest the zoomable viewer. */
const LARGE_PNG_PIXELS = 1_000_000_000;

type Loadable<T> =
  | { state: "none" }
  | { state: "loading" }
  | { state: "ok"; value: T }
  | { state: "error"; message: string };

type Engine =
  | { state: "starting" }
  | { state: "ready"; devices: DeviceInfo[] }
  | { state: "error"; message: string };

export default function App() {
  const [settings, setSettings] = useState<Settings>(loadSettings);
  const update = (changes: Partial<Settings>) => setSettings((s) => ({ ...s, ...changes }));
  useEffect(() => saveSettings(settings), [settings]);

  const [engine, setEngine] = useState<Engine>({ state: "starting" });
  const [outputPath, setOutputPath] = useState<string | null>(null);
  // True when the OS save dialog already asked about replacing an existing PNG.
  const [outputConfirmed, setOutputConfirmed] = useState(false);

  const [theme, setTheme] = useState<Theme>(loadTheme);
  useEffect(() => applyTheme(theme), [theme]);

  const job = useJob();
  const running = job.state.status === "running";

  const drag = useFileDrop(handleDrop, running ? "Wait until the mosaic is finished, or cancel it first." : null);
  const [notice, setNotice] = useState<string | null>(null);
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(null), 5000);
    return () => clearTimeout(timer);
  }, [notice]);

  // Devices come best first, so in automatic mode the first one is used. A remembered device
  // that isn't there anymore (e.g. a removed graphics card) also means automatic.
  const devices = engine.state === "ready" ? engine.devices : [];
  const chosenDevice = devices.find((d) => d.id === settings.device);
  const deviceSetting = chosenDevice ? chosenDevice.id : "auto";
  const activeDevice: DeviceInfo | undefined = chosenDevice ?? devices[0];

  const image = useSidecarInfo(settings.inputPath, (path) => sidecar.request("imageInfo", { path }));
  const cards = useSidecarInfo(settings.cardFolderPath, (path) => sidecar.request("cardFolderInfo", { path }));
  const layout = useLayout(image, cards, settings.cardsPerRow, settings.cardWidth);
  // Re-checked when a run ends too, since that's when the output appears.
  const outputKey = outputPath && JSON.stringify({ outputPath, deepZoom: settings.deepZoom, run: job.state.status });
  const outputStatus = useSidecarInfo(outputKey, (key) => sidecar.request("outputExists", JSON.parse(key)));

  // Start the engine and find out which devices can run the matching.
  useEffect(() => {
    sidecar
      .request("systemInfo")
      .then(({ devices }) => setEngine({ state: "ready", devices }))
      .catch((error: Error) => setEngine({ state: "error", message: error.message }));
  }, []);

  // ── Actions ───────────────────────────────────────────────────────────────

  async function pickInput() {
    const path = await open({ title: "Choose the source image", filters: [{ name: "Images", extensions: IMAGE_EXTENSIONS }] });
    if (path) await chooseInput(path);
  }

  async function pickCardFolder() {
    const path = await open({ title: "Choose the folder with your card images", directory: true });
    if (path) chooseCardFolder(path);
  }

  function handleDrop(action: DropAction) {
    setNotice(null);
    if (action.kind === "image") chooseInput(action.path);
    else if (action.kind === "cardFolder") chooseCardFolder(action.path);
    else setNotice(action.reason);
  }

  // Shared by the pickers and drag-and-drop.
  async function chooseInput(path: string) {
    update({ inputPath: path });
    setOutputPath(await suggestOutputPath(path, settings.deepZoom));
    setOutputConfirmed(false);
  }

  function chooseCardFolder(path: string) {
    update({ cardFolderPath: path });
  }

  async function pickOutput() {
    const path = await save({
      title: settings.deepZoom ? "Choose a name for the mosaic folder" : "Save the mosaic as",
      defaultPath: outputPath ?? undefined,
      filters: settings.deepZoom ? undefined : [{ name: "PNG image", extensions: ["png"] }],
    });
    if (!path) return;
    setOutputPath(settings.deepZoom ? convertOutputPath(path, true) : path);
    setOutputConfirmed(!settings.deepZoom);
  }

  function setDeepZoom(deepZoom: boolean) {
    update({ deepZoom });
    setOutputPath((path) => path && convertOutputPath(path, deepZoom));
    setOutputConfirmed(false);
  }

  async function generate() {
    if (!settings.inputPath || !settings.cardFolderPath || !outputPath) return;

    const { exists, path } = await sidecar.request("outputExists", { outputPath, deepZoom: settings.deepZoom });
    if (exists && !outputConfirmed) {
      const replace = await ask(`${path}\n\nalready exists. Do you want to replace it?`, {
        title: "Replace existing mosaic?",
        kind: "warning",
        okLabel: "Replace",
        cancelLabel: "Cancel",
      });
      if (!replace) return;
    }
    // The next run would overwrite this one's result, so ask again then.
    setOutputConfirmed(false);

    const options: MosaicOptions = {
      inputPath: settings.inputPath,
      cardFolderPath: settings.cardFolderPath,
      outputPath,
      cardsPerRow: settings.cardsPerRow,
      cardWidth: settings.cardWidth,
      patchMatch: settings.patchMatch,
      matchWidth: settings.matchWidth,
      matchCandidates: settings.matchCandidates,
      labSsd: settings.labSsd,
      deepZoom: settings.deepZoom,
      backgroundColor: settings.backgroundColor,
      transparencyThreshold: settings.transparencyThreshold,
      device: deviceSetting,
    };
    await job.start(options);
  }

  // ── What's missing before Generate can be pressed ─────────────────────────

  function findBlocker(): string | null {
    if (engine.state === "starting") return "Starting the mosaic engine…";
    if (engine.state === "error") return engine.message;
    if (!settings.inputPath) return "Choose a source image to begin.";
    if (image.state === "error") return "The source image can't be read.";
    if (!settings.cardFolderPath) return "Choose your card folder.";
    if (cards.state === "error") return "The card folder can't be read.";
    if (cards.state === "ok" && cards.value.imageCount === 0) return "The card folder has no images.";
    if (!outputPath) return "Choose where to save the mosaic.";
    if (layout.state === "ok" && layout.value.rows < 1)
      return "With these settings the mosaic has no rows. Use more cards across or wider cards.";
    if (image.state !== "ok" || cards.state !== "ok" || layout.state !== "ok") return "Reading your files…";
    return null;
  }

  // ── Screen ────────────────────────────────────────────────────────────────

  return (
    <div className="min-h-screen bg-slate-100 dark:bg-neutral-950 text-slate-800 dark:text-neutral-200">
      <header className="border-b border-slate-200 dark:border-neutral-800 bg-white dark:bg-neutral-900">
        <div className="mx-auto flex max-w-6xl items-center justify-between gap-4 px-6 py-4">
          <h1 className="text-xl font-semibold text-slate-900 dark:text-neutral-100">Mosaic Generator</h1>
          <div className="flex min-w-0 items-center gap-2">
            <EngineBadge engine={engine} device={activeDevice} automatic={deviceSetting === "auto"} />
            <ThemeToggle theme={theme} onChange={setTheme} />
          </div>
        </div>
      </header>

      <main className="mx-auto grid max-w-6xl gap-6 px-6 py-6 md:grid-cols-[minmax(0,1fr)_340px]">
        <fieldset disabled={running} className="min-w-0 space-y-6">
          <Section title="Your images">
            <PathField
              label="Source image"
              placeholder="The picture to rebuild out of cards"
              path={settings.inputPath}
              onBrowse={pickInput}
              error={image.state === "error" ? image.message : null}
              detail={image.state === "ok" && `${image.value.width.toLocaleString()} × ${image.value.height.toLocaleString()} px`}
            />
            <PathField
              label="Card folder"
              placeholder="The folder with your card images"
              path={settings.cardFolderPath}
              onBrowse={pickCardFolder}
              error={cards.state === "error" ? cards.message : null}
              detail={cards.state === "ok" && <CardFolderDetail info={cards.value} />}
            />
          </Section>

          <Section title="Mosaic">
            <div>
              <FieldLabel hint="More cards across gives more detail, and a bigger output.">Size</FieldLabel>
              <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
                <label className="flex items-center gap-2 text-sm">
                  Cards across
                  <NumberField value={settings.cardsPerRow} min={1} max={10000} onChange={(cardsPerRow) => update({ cardsPerRow })} />
                </label>
                <label className="flex items-center gap-2 text-sm">
                  Card width
                  <NumberField value={settings.cardWidth} min={1} max={4000} suffix="px" onChange={(cardWidth) => update({ cardWidth })} />
                </label>
              </div>
              <SizeSummary layout={layout} deepZoom={settings.deepZoom} />
            </div>

            <div>
              <FieldLabel>Matching</FieldLabel>
              <ChoiceGroup
                value={settings.patchMatch ? "detail" : "color"}
                onChange={(value) => update({ patchMatch: value === "detail" })}
                options={[
                  { value: "color", title: "Colour match", description: "Fast. Picks each card by its average colour." },
                  { value: "detail", title: "Detail match", description: "Slower. Compares what's in each card, so edges and shapes follow the image better." },
                ]}
              />
              {settings.patchMatch && activeDevice?.kind === "cpu" && (
                <p className="mt-2 text-sm text-amber-700 dark:text-amber-400">
                  Detail match will run on the processor, which can be much slower than on a graphics card.
                </p>
              )}
            </div>

            <div>
              <FieldLabel>Output</FieldLabel>
              <ChoiceGroup
                value={settings.deepZoom ? "deepZoom" : "png"}
                onChange={(value) => setDeepZoom(value === "deepZoom")}
                options={[
                  { value: "png", title: "Single image (PNG)", description: "One file you can open in any image viewer." },
                  { value: "deepZoom", title: "Zoomable viewer", description: "A folder with a web page for panning and zooming. Best for very large mosaics." },
                ]}
              />
            </div>

            <PathField
              label={settings.deepZoom ? "Save folder as" : "Save image as"}
              placeholder="Where to save the mosaic"
              path={outputPath}
              onBrowse={pickOutput}
              detail={
                <>
                  {settings.deepZoom && "A folder with this name is created. Open index.html inside it to view the mosaic."}
                  {outputStatus.state === "ok" && outputStatus.value.exists && (
                    <span className="block text-amber-700 dark:text-amber-400">This already exists and will be replaced.</span>
                  )}
                </>
              }
            />

            <AdvancedSettings settings={settings} update={update} devices={devices} deviceSetting={deviceSetting} />
          </Section>
        </fieldset>

        <div className="space-y-6 md:sticky md:top-6 md:self-start">
          <Section title="Source image">
            <div className="checkerboard flex h-60 items-center justify-center overflow-hidden rounded-lg border border-slate-200 dark:border-neutral-800">
              {settings.inputPath && image.state === "ok" ? (
                <img src={convertFileSrc(settings.inputPath)} alt="Source image" className="max-h-full max-w-full object-contain" />
              ) : (
                <span className="rounded bg-white/80 dark:bg-neutral-900/80 px-3 py-2 text-center text-sm text-slate-500 dark:text-neutral-400">
                  No image selected
                  <span className="block text-xs">Tip: drop an image or card folder anywhere on the window</span>
                </span>
              )}
            </div>
          </Section>

          <section className="space-y-4 rounded-xl border border-slate-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 shadow-sm">
            <RunPanel job={job.state} blocker={findBlocker()} onGenerate={generate} onCancel={job.cancel} />
            <LogPanel lines={job.log} />
          </section>
        </div>
      </main>

      <DropOverlay drag={drag} />
      {notice && <Notice message={notice} onClose={() => setNotice(null)} />}
    </div>
  );
}

// ── Pieces of the screen ────────────────────────────────────────────────────

function ThemeToggle({ theme, onChange }: { theme: Theme; onChange: (theme: Theme) => void }) {
  const next: Theme = theme === "dark" ? "light" : "dark";
  return (
    <button
      type="button"
      onClick={() => onChange(next)}
      title={`Switch to ${next} mode`}
      aria-label={`Switch to ${next} mode`}
      className="shrink-0 rounded-full border border-slate-200 p-1.5 text-slate-600 hover:bg-slate-50 dark:border-neutral-800 dark:text-neutral-400 dark:hover:bg-neutral-800"
    >
      {theme === "dark" ? (
        // Sun: switch to light
        <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
          <circle cx="12" cy="12" r="4" />
          <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
        </svg>
      ) : (
        // Moon: switch to dark
        <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />
        </svg>
      )}
    </button>
  );
}

/** Shows which device will run the matching: green for a graphics card, amber for the processor. */
function EngineBadge(props: { engine: Engine; device: DeviceInfo | undefined; automatic: boolean }) {
  const { engine, device } = props;
  const [dot, text] =
    engine.state === "starting" || !device ? ["bg-slate-400", "Starting engine…"]
    : engine.state === "error" ? ["bg-red-500", "Engine error"]
    : [device.kind === "cpu" ? "bg-amber-500" : "bg-emerald-500", deviceLabel(device)];

  const tooltip =
    engine.state === "error" ? engine.message
    : device ? `Matching runs on ${deviceLabel(device)}. ${props.automatic ? "Chosen automatically" : "Chosen in Advanced settings"}.`
    : undefined;

  return (
    <div
      className="flex min-w-0 items-center gap-2 rounded-full border border-slate-200 dark:border-neutral-800 px-3 py-1 text-sm text-slate-600 dark:text-neutral-400"
      title={tooltip}
    >
      <span className={`h-2 w-2 shrink-0 rounded-full ${dot}`} />
      <span className="truncate">{text}</span>
    </div>
  );
}

function CardFolderDetail({ info }: { info: CardFolderInfo }) {
  return (
    <>
      {info.imageCount.toLocaleString()} card images
      {info.imageCount > 0 && !info.hasCache && (
        <span className="block text-slate-500 dark:text-neutral-400">
          First time using this folder: every card is analysed once on the first run, which can take a while for big folders.
        </span>
      )}
    </>
  );
}

function SizeSummary({ layout, deepZoom }: { layout: Loadable<MosaicLayout>; deepZoom: boolean }) {
  if (layout.state !== "ok")
    return <p className="mt-2 text-sm text-slate-500 dark:text-neutral-400">Choose an image and a card folder to see the output size.</p>;

  const l = layout.value;
  const megapixels = l.pixelCount / 1_000_000;
  return (
    <div className="mt-3 rounded-lg bg-slate-50 dark:bg-neutral-800/60 px-3 py-2 text-sm">
      <p>
        Output: <span className="font-medium">{l.width.toLocaleString()} × {l.height.toLocaleString()} px</span>{" "}
        <span className="text-slate-500 dark:text-neutral-400">({megapixels < 10 ? megapixels.toFixed(1) : Math.round(megapixels).toLocaleString()} megapixels)</span>
      </p>
      <p className="text-slate-600 dark:text-neutral-400">
        {l.cols.toLocaleString()} × {l.rows.toLocaleString()} = {l.tileCount.toLocaleString()} cards, each {l.cardWidth} × {l.cardHeight} px
      </p>
      {!deepZoom && l.pixelCount > LARGE_PNG_PIXELS && (
        <p className="mt-1 text-amber-700 dark:text-amber-400">
          That's a very large PNG. Many image viewers can't open images this big. The zoomable viewer handles any size.
        </p>
      )}
    </div>
  );
}

function AdvancedSettings(props: {
  settings: Settings;
  update: (changes: Partial<Settings>) => void;
  devices: DeviceInfo[];
  /** "auto" or the id of a device in `devices`. */
  deviceSetting: string;
}) {
  const { settings, update, devices } = props;
  return (
    <details className="group rounded-lg border border-slate-200 dark:border-neutral-800">
      <summary className="cursor-pointer select-none px-3 py-2 text-sm font-medium text-slate-700 dark:text-neutral-300">
        Advanced settings
      </summary>
      <div className="space-y-5 border-t border-slate-200 dark:border-neutral-800 px-3 py-4">
        <div>
          <FieldLabel hint="Automatic picks the fastest one available, and switches to the processor if the graphics card runs into a problem.">
            Run matching on
          </FieldLabel>
          <select
            value={props.deviceSetting}
            onChange={(e) => update({ device: e.target.value })}
            className="w-full max-w-sm rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-900"
          >
            <option value="auto">Automatic{devices[0] ? ` (${deviceLabel(devices[0])})` : ""}</option>
            {devices.map((device) => (
              <option key={device.id} value={device.id}>
                {deviceLabel(device)}
              </option>
            ))}
          </select>
        </div>

        {settings.patchMatch && (
          <>
            <div>
              <FieldLabel hint="Size each card is compared at. Higher catches finer detail, but uses more memory and time.">
                Detail resolution
              </FieldLabel>
              <NumberField value={settings.matchWidth} min={1} max={1024} suffix="px" onChange={(matchWidth) => update({ matchWidth })} />
            </div>
            <div>
              <FieldLabel hint="How many of the closest-coloured cards are compared in detail for each spot. Higher can find better matches, but is slower.">
                Cards compared per spot
              </FieldLabel>
              <NumberField value={settings.matchCandidates} min={1} max={100000} onChange={(matchCandidates) => update({ matchCandidates })} />
            </div>
            <label className="flex items-start gap-2 text-sm">
              <input
                type="checkbox"
                checked={settings.labSsd}
                onChange={(e) => update({ labSsd: e.target.checked })}
                className="mt-0.5"
              />
              <span>
                <span className="font-medium">Compare colours the way eyes see them</span>
                <span className="block text-xs text-slate-500 dark:text-neutral-400">Slower, but can give better colours and contrast. (LAB colour space)</span>
              </span>
            </label>
          </>
        )}

        <div>
          <FieldLabel hint="Transparent parts of the source image are treated as this colour while matching. They stay transparent in the mosaic.">
            Background for transparent areas
          </FieldLabel>
          <div className="flex items-center gap-2">
            <input
              type="color"
              value={settings.backgroundColor}
              onChange={(e) => update({ backgroundColor: e.target.value })}
              className="h-9 w-14 cursor-pointer rounded border border-slate-300 dark:border-neutral-700"
            />
            <span className="font-mono text-sm uppercase text-slate-600 dark:text-neutral-400">{settings.backgroundColor}</span>
          </div>
        </div>

        <div>
          <FieldLabel hint="Spots of the source image that are more transparent than this are left empty instead of getting a card.">
            Leave spots empty when more than {settings.transparencyThreshold}% transparent
          </FieldLabel>
          <input
            type="range"
            min={0}
            max={100}
            value={settings.transparencyThreshold}
            onChange={(e) => update({ transparencyThreshold: Number(e.target.value) })}
            className="w-full max-w-sm accent-indigo-600"
          />
        </div>

        <button
          type="button"
          onClick={() =>
            update({
              matchWidth: DEFAULT_SETTINGS.matchWidth,
              matchCandidates: DEFAULT_SETTINGS.matchCandidates,
              labSsd: DEFAULT_SETTINGS.labSsd,
              backgroundColor: DEFAULT_SETTINGS.backgroundColor,
              transparencyThreshold: DEFAULT_SETTINGS.transparencyThreshold,
              device: DEFAULT_SETTINGS.device,
            })
          }
          className="text-sm text-indigo-700 dark:text-indigo-300 hover:underline"
        >
          Reset advanced settings
        </button>
      </div>
    </details>
  );
}

// ── Hooks ───────────────────────────────────────────────────────────────────

/** Asks the sidecar about `key` whenever it changes. Answers to outdated keys are ignored. */
function useSidecarInfo<T>(key: string | null, fetch: (key: string) => Promise<T>): Loadable<T> {
  const [info, setInfo] = useState<Loadable<T>>({ state: "none" });

  useEffect(() => {
    if (!key) {
      setInfo({ state: "none" });
      return;
    }
    let current = true;
    setInfo({ state: "loading" });
    fetch(key).then(
      (value) => current && setInfo({ state: "ok", value }),
      (error: Error) => current && setInfo({ state: "error", message: error.message }),
    );
    return () => {
      current = false;
    };
  }, [key]);

  return info;
}

/** The output size, recalculated by the engine whenever the image, cards or size settings change. */
function useLayout(
  image: Loadable<ImageInfo>,
  cards: Loadable<CardFolderInfo>,
  cardsPerRow: number,
  cardWidth: number,
): Loadable<MosaicLayout> {
  const ready = image.state === "ok" && cards.state === "ok" && cards.value.aspectRatio !== undefined;
  const key = ready
    ? JSON.stringify({
        inputWidth: image.value.width,
        inputHeight: image.value.height,
        cardsPerRow,
        cardWidth,
        cardAspectRatio: cards.value.aspectRatio!,
      })
    : null;

  return useSidecarInfo(key, (k) => sidecar.request("layout", JSON.parse(k)));
}
