import { readFile, writeFile } from "node:fs/promises";
import { chromium } from "playwright";

const root = new URL("../", import.meta.url);
const source = await readFile(
  new URL("assets/brand/nailbiter.svg", root),
  "utf8",
);
const check = process.argv.includes("--check");
const version = "swing-1";
const browser = await chromium.launch();
const assets = new Map([["favicon.svg", Buffer.from(source)]]);

function ico(images) {
  const header = Buffer.alloc(6 + images.length * 16);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  for (const [index, { size, png }] of images.entries()) {
    const entry = 6 + index * 16;
    header[entry] = size;
    header[entry + 1] = size;
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(png.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  }
  return Buffer.concat([header, ...images.map(({ png }) => png)]);
}

try {
  const page = await browser.newPage();
  async function png(size, square = false, maskable = false) {
    const base64 = await page.evaluate(
      async ({ source, size, square, maskable }) => {
        const svg = new DOMParser().parseFromString(source, "image/svg+xml");
        if (square) svg.querySelector("rect").removeAttribute("rx");
        if (maskable)
          svg
            .querySelector("path")
            .setAttribute("transform", "translate(6.4 6.4) scale(0.8)");
        const image = new Image();
        image.src = `data:image/svg+xml,${encodeURIComponent(new XMLSerializer().serializeToString(svg))}`;
        await image.decode();
        const canvas = document.createElement("canvas");
        canvas.width = canvas.height = size;
        canvas.getContext("2d").drawImage(image, 0, 0, size, size);
        return canvas.toDataURL("image/png").split(",")[1];
      },
      { source, size, square, maskable },
    );
    return Buffer.from(base64, "base64");
  }

  const faviconImages = [];
  for (const size of [16, 32, 48])
    faviconImages.push({ size, png: await png(size) });
  assets.set("favicon.ico", ico(faviconImages));
  assets.set("apple-touch-icon.png", await png(180, true));
  assets.set("icon-192.png", await png(192, true));
  assets.set("icon-512.png", await png(512, true));
  assets.set("icon-maskable-512.png", await png(512, true, true));
} finally {
  await browser.close();
}

assets.set(
  "site.webmanifest",
  Buffer.from(
    `${JSON.stringify(
      {
        name: "Nailbiter",
        short_name: "Nailbiter",
        id: "/",
        start_url: "/",
        scope: "/",
        display: "browser",
        background_color: "#3b82f6",
        theme_color: "#09090b",
        icons: [
          {
            src: `/icon-192.png?v=${version}`,
            sizes: "192x192",
            type: "image/png",
            purpose: "any",
          },
          {
            src: `/icon-512.png?v=${version}`,
            sizes: "512x512",
            type: "image/png",
            purpose: "any",
          },
          {
            src: `/icon-maskable-512.png?v=${version}`,
            sizes: "512x512",
            type: "image/png",
            purpose: "maskable",
          },
        ],
      },
      null,
      2,
    )}\n`,
  ),
);

for (const directory of ["apps/www/public", "apps/backend/wwwroot"]) {
  for (const [name, expected] of assets) {
    const path = `${directory}/${name}`;
    const file = new URL(path, root);
    if (check) {
      const actual = await readFile(file).catch((error) => {
        if (error.code === "ENOENT") return Buffer.alloc(0);
        throw error;
      });
      if (!actual.equals(expected)) {
        console.error(
          `Outdated icon asset: ${path}. Run bun run icons:generate.`,
        );
        process.exitCode = 1;
      }
    } else {
      await writeFile(file, expected);
      console.log(path);
    }
  }
}
if (check && !process.exitCode)
  console.log(
    "PASS: both apps have identical, current assets from the approved Swing source.",
  );
