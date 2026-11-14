import { defineConfig } from "astro/config";

const exactTailscaleHost = (tailscaleUrl) => {
  const { hostname } = new URL(tailscaleUrl);
  if (hostname === "" || hostname.startsWith(".")) {
    throw new Error(
      `PORTLESS_TAILSCALE_URL must be an absolute URL with one exact hostname, but it is "${tailscaleUrl}". Unset it, or start the dev server with "portless run --tailscale astro dev".`,
    );
  }
  return hostname;
};

const portlessTailscaleDevHost = {
  name: "portless-tailscale-dev-host",
  hooks: {
    "astro:config:setup": ({ command, updateConfig }) => {
      const tailscaleUrl = process.env.PORTLESS_TAILSCALE_URL;
      if (command === "dev" && tailscaleUrl) {
        updateConfig({
          server: { allowedHosts: [exactTailscaleHost(tailscaleUrl)] },
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
