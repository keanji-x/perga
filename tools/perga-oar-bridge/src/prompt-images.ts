import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { InputImage } from "@botiverse/oar/browser";

interface PreparedImages {
  readonly images: readonly InputImage[];
  readonly cleanup: () => Promise<void>;
}

const extensions: ReadonlyMap<string, string> = new Map([
  ["image/png", "png"],
  ["image/jpeg", "jpg"],
  ["image/gif", "gif"],
  ["image/webp", "webp"],
]);

export async function preparePromptImages(value: readonly unknown[]): Promise<PreparedImages> {
  // OAR's public InputImage API takes paths on the runtime host, including remote hosts.
  const directory = await mkdtemp(join(tmpdir(), "perga-oar-images-"));
  const cleanup = () => rm(directory, { recursive: true, force: true }).catch(() => {});
  try {
    const images: InputImage[] = [];
    for (const entry of value) {
      const image = entry as { mediaType?: unknown; data?: unknown } | null;
      if (typeof image?.mediaType !== "string" || typeof image.data !== "string" || image.data.length === 0) {
        throw new Error("Invalid image payload.");
      }
      const extension = extensions.get(image.mediaType);
      if (!extension) throw new Error("Unsupported image type.");
      const path = join(directory, `${images.length}.${extension}`);
      await writeFile(path, Buffer.from(image.data, "base64"), { mode: 0o600, flag: "wx" });
      images.push({ path, mediaType: image.mediaType });
    }
    return { images, cleanup };
  } catch (error) {
    await cleanup();
    throw error;
  }
}
