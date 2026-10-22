import { defineConfig } from "vite";

// No reverse-proxy, no permissive CORS, no CDN, no telemetry.
// The release bundle must be served by the *same* loopback-only .NET origin.
export default defineConfig({
  base: "/operator/console/",
  esbuild: { jsx: "automatic" },
  server: { host: "127.0.0.1", strictPort: true },
  preview: { host: "127.0.0.1", strictPort: true },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsDir: "assets",
    sourcemap: false,
    manifest: false
  }
});
