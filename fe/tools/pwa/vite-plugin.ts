import type { Plugin } from 'vite';

// Links the PWA install assets from web/public under the resolved base.
export function pwaManifestLink(): Plugin {
  let base = '/';
  return {
    name: 'neige-pwa-manifest-link',
    configResolved(config) {
      base = config.base;
    },
    transformIndexHtml: () => [
      { tag: 'link', attrs: { rel: 'manifest', href: `${base}manifest.webmanifest` }, injectTo: 'head' },
      {
        tag: 'link',
        attrs: { rel: 'apple-touch-icon', sizes: '180x180', href: `${base}icons/apple-touch-icon.png` },
        injectTo: 'head',
      },
    ],
  };
}
