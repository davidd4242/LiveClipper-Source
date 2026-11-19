import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';
import { readFileSync } from 'node:fs';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const webRoot = fileURLToPath(new URL('.', import.meta.url));
const legacyRoot = new URL('../src/LiveClipper.App/wwwroot/', import.meta.url);
const legacyFiles = [
  ['legacy.html', 'index.html', 'text/html; charset=utf-8'],
  ['style.css', 'style.css', 'text/css; charset=utf-8'],
  ['features.css', 'features.css', 'text/css; charset=utf-8'],
  ['studio.css', 'studio.css', 'text/css; charset=utf-8'],
  ['app.js', 'app.js', 'text/javascript; charset=utf-8'],
  ['studio.js', 'studio.js', 'text/javascript; charset=utf-8'],
  ['timeline.js', 'timeline.js', 'text/javascript; charset=utf-8'],
  ['online-audio.js', 'online-audio.js', 'text/javascript; charset=utf-8'],
  ['advanced-editor.js', 'advanced-editor.js', 'text/javascript; charset=utf-8'],
  ['editor-preview.js', 'editor-preview.js', 'text/javascript; charset=utf-8'],
  ['panel-tools.js', 'panel-tools.js', 'text/javascript; charset=utf-8'],
  ['music.js', 'music.js', 'text/javascript; charset=utf-8'],
  ['factory-reset.js', 'factory-reset.js', 'text/javascript; charset=utf-8'],
  ['installation.js', 'installation.js', 'text/javascript; charset=utf-8'],
  ['icons/live.svg', 'icons/live.svg', 'image/svg+xml'],
  ['icons/highlights.svg', 'icons/highlights.svg', 'image/svg+xml'],
  ['icons/recordings.svg', 'icons/recordings.svg', 'image/svg+xml'],
  ['icons/jobs.svg', 'icons/jobs.svg', 'image/svg+xml'],
  ['icons/settings.svg', 'icons/settings.svg', 'image/svg+xml'],
  ['icons/danmaku.svg', 'icons/danmaku.svg', 'image/svg+xml'],
  ['assets/liveclipper.ico', 'assets/liveclipper.ico', 'image/x-icon'],
  ['assets/liveclipper-logo.png', 'assets/liveclipper-logo.png', 'image/png'],
  ['lib/hls.min.js', 'lib/hls.min.js', 'text/javascript; charset=utf-8'],
  ['lib/hls.min.js.LICENSE.txt', 'lib/hls.min.js.LICENSE.txt', 'text/plain; charset=utf-8'],
] as const;

function legacyAssets(): Plugin {
  return {
    name: 'liveclipper-legacy-assets',
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        if (req.method !== 'GET' && req.method !== 'HEAD') return next();
        const pathname = req.url?.split('?')[0];
        const file = legacyFiles.find(([fileName]) => pathname === `/${fileName}`);
        if (!file) return next();
        const source = readFileSync(new URL(file[1], legacyRoot));
        res.statusCode = 200;
        res.setHeader('Content-Type', file[2]);
        res.end(req.method === 'HEAD' ? undefined : source);
      });
    },
    generateBundle() {
      for (const [fileName, sourceName] of legacyFiles) {
        this.emitFile({
          type: 'asset',
          fileName,
          source: readFileSync(new URL(sourceName, legacyRoot)),
        });
      }
    },
  };
}

export default defineConfig({
  // The app is served by the desktop backend at the site root (/api, /live,
  // /live/monitor, /settings ...). Absolute asset URLs are required so nested
  // SPA routes such as /live/monitor also resolve ./assets/* correctly.
  base: '/',
  plugins: [react(), legacyAssets()],
  server: {
    port: 5173,
    strictPort: true,
    fs: {
      // The canonical design tokens live in ../design/ui and are imported
      // into web/src/styles/tokens.css.
      allow: [webRoot, repoRoot],
    },
    proxy: {
      '/api': {
        // 后端默认 5179，但 Windows 自带保留段会占住 5114–5213（5179 就在里面，
        // bind 会报 EACCES）。开发时用 LIVECLIPPER_PORT 让前后端对齐，生产不受影响。
        target: `http://127.0.0.1:${process.env.LIVECLIPPER_PORT ?? '5255'}`,
        changeOrigin: true,
        // The desktop backend rejects cross-origin writes. In development the
        // browser's Origin is the Vite port, so strip it before forwarding.
        configure(proxy) {
          proxy.on('proxyReq', (proxyReq) => proxyReq.removeHeader('origin'));
        },
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
    // vendor 拆包：把体积稳定、跨页面复用的依赖从入口 chunk 中拆出，
    // 配合 App.tsx 的页面级 React.lazy，首屏只下载入口 + 当前页面 chunk。
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes('/src/api/') && /[\\/]src[\\/]api[\\/](client|music|settings|studio)\.ts$/.test(id)) {
            // Route-shared API clients: rolldown would otherwise emit one tiny chunk per file
            // (client/music/settings/studio). The publish gate sums gzip per emitted file, so
            // four ~1.3 kB chunks cost ~1.4 kB more than one 4 kB chunk with the same modules.
            return 'shared-api';
          }
          if (!id.includes('node_modules')) return undefined;
          if (/[\\/]node_modules[\\/](react|react-dom|scheduler)[\\/]/.test(id)) return 'react-vendor';
          // Icons are imported by every route, so they share react-vendor's load moment; two
          // chunks here only paid a second gzip dictionary and a second preload entry.
          if (/[\\/]node_modules[\\/]lucide-react[\\/]/.test(id)) return 'react-vendor';
          if (/[\\/]node_modules[\\/]mpegts\.js[\\/]/.test(id)) return 'media-vendor';
          return 'vendor';
        },
      },
    },
  },
});
