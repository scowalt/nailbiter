import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      entry: "src/index.js",
      formats: ["es"],
      fileName: "nailbiter-components",
    },
    outDir: "../backend/wwwroot/js",
    emptyOutDir: false,
    rollupOptions: {
      output: {
        entryFileNames: "nailbiter-components.js",
      },
    },
  },
});
