import { strict as assert } from "node:assert";
import { readFile } from "node:fs/promises";
import { chromium } from "playwright";

const root = new URL("../../", import.meta.url);
const dist = new URL("apps/www/dist/", root);
const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  await page.route("https://nailbiter.test/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    const file = new URL(path === "/" ? "index.html" : path.slice(1), dist);
    const types = {
      html: "text/html",
      svg: "image/svg+xml",
      ico: "image/x-icon",
      png: "image/png",
      webmanifest: "application/manifest+json",
    };
    await route.fulfill({
      body: await readFile(file),
      contentType: types[file.pathname.split(".").pop()],
    });
  });
  await page.goto("https://nailbiter.test/");
  const expected = [
    [
      'link[rel="icon"][type="image/x-icon"]',
      "/favicon.ico?v=swing-1",
      "16x16 32x32 48x48",
    ],
    ['link[rel="icon"][type="image/svg+xml"]', "/favicon.svg?v=swing-1", "any"],
    [
      'link[rel="apple-touch-icon"]',
      "/apple-touch-icon.png?v=swing-1",
      "180x180",
    ],
    ['link[rel="manifest"]', "/site.webmanifest?v=swing-1", null],
  ];
  for (const [selector, href, sizes] of expected) {
    assert.equal(await page.locator(selector).count(), 1);
    assert.equal(await page.locator(selector).getAttribute("href"), href);
    assert.equal(await page.locator(selector).getAttribute("sizes"), sizes);
  }
  assert.equal(
    await page.locator(".logo").getAttribute("src"),
    "/favicon.svg?v=swing-1",
  );
  assert.equal(await page.locator(".logo").getAttribute("alt"), "");
  await page.locator(".logo").evaluate((image) => image.decode());

  const manifest = JSON.parse(
    await readFile(new URL("site.webmanifest", dist), "utf8"),
  );
  assert.equal(manifest.name, "Nailbiter");
  assert.equal(manifest.start_url, "/");
  assert.equal(manifest.scope, "/");
  assert.equal(manifest.display, "browser");
  assert.deepEqual(
    manifest.icons.map(({ sizes }) => sizes),
    ["192x192", "512x512", "512x512"],
  );
  assert.deepEqual(
    manifest.icons.map(({ purpose }) => purpose),
    ["any", "any", "maskable"],
  );
  for (const { src } of manifest.icons) assert.ok(src.startsWith("/"));
  for (const { src, sizes, purpose } of [
    { src: "/apple-touch-icon.png?v=swing-1", sizes: "180x180" },
    ...manifest.icons,
  ]) {
    const pixels = await page.evaluate(async (src) => {
      const image = new Image();
      image.src = src;
      await image.decode();
      const canvas = document.createElement("canvas");
      canvas.width = image.naturalWidth;
      canvas.height = image.naturalHeight;
      const context = canvas.getContext("2d");
      context.drawImage(image, 0, 0);
      const { data } = context.getImageData(0, 0, canvas.width, canvas.height);
      let opaque = true;
      let foreground = 0;
      let radius = 0;
      for (let i = 0; i < data.length; i += 4) {
        opaque &&= data[i + 3] === 255;
        if (data[i] !== 59 || data[i + 1] !== 130 || data[i + 2] !== 246) {
          foreground++;
          const x = (i / 4) % canvas.width;
          const y = Math.floor(i / 4 / canvas.width);
          radius = Math.max(
            radius,
            Math.hypot(x + 0.5 - canvas.width / 2, y + 0.5 - canvas.height / 2),
          );
        }
      }
      return {
        size: `${canvas.width}x${canvas.height}`,
        opaque,
        foreground,
        radius,
        width: canvas.width,
      };
    }, src);
    assert.equal(pixels.size, sizes, src);
    assert.ok(pixels.opaque, `${src}: phone background must be opaque`);
    assert.ok(pixels.foreground > 0, `${src}: mark must not be blank`);
    if (purpose === "maskable")
      assert.ok(
        pixels.radius <= pixels.width * 0.4,
        "Maskable mark must fit in the central 80% safe circle",
      );
  }

  await page.evaluate(async () => {
    const image = new Image();
    image.src = "/favicon.ico?v=swing-1";
    await image.decode();
  });
  const ico = await readFile(new URL("favicon.ico", dist));
  assert.equal(ico.readUInt16LE(0), 0);
  assert.equal(ico.readUInt16LE(2), 1);
  assert.equal(ico.readUInt16LE(4), 3);
  for (const [index, size] of [16, 32, 48].entries()) {
    const entry = 6 + index * 16;
    assert.equal(ico[entry], size);
    assert.equal(ico[entry + 1], size);
    const offset = ico.readUInt32LE(entry + 12);
    const png = ico.subarray(offset, offset + ico.readUInt32LE(entry + 8));
    assert.equal(png.subarray(0, 8).toString("hex"), "89504e470d0a1a0a");
    assert.equal(png.readUInt32BE(16), size);
    assert.equal(png.readUInt32BE(20), size);
  }
  console.log(
    "PASS: built marketing page declares the shared icons; phone exports are opaque and correctly sized; maskable artwork is safe; ICO contains 16/32/48px fallbacks.",
  );
} finally {
  await browser.close();
}
