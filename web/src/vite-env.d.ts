/// <reference types="vite/client" />

/** The newest version in `public/release-notes.json` at build time (set by `define` in `vite.config.ts`). */
declare const __EMBER_VERSION__: string;

interface ImportMetaEnv {
  readonly VITE_NATIVE?: string;
  readonly VITE_API_BASE?: string;
  readonly VITE_WEB_ORIGIN?: string;
}
