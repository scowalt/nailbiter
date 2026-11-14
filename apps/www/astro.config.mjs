import { defineConfig } from "astro/config";

const portlessTailscaleDevHost = {
  name: "portless-tailscale-dev-host",
  hooks: {
    "astro:config:setup": ({ command, updateConfig }) => {
      const tailscaleUrl = process.env.PORTLESS_TAILSCALE_URL;
      if (command === "dev" && tailscaleUrl) {
        updateConfig({
          server: { allowedHosts: [new URL(tailscaleUrl).hostname] },
        });
      }
    },
  },
};

export default defineConfig({
  output: "static",
  outDir: "./dist",
  integrations: [portlessTailscaleDevHost],
});
