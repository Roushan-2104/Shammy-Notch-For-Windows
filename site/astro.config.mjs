import { defineConfig } from 'astro/config';

// GitHub Pages serves the site from the repo's own path.
export default defineConfig({
  site: 'https://roushan-2104.github.io',
  base: '/Shammy-Notch-For-Windows',
  trailingSlash: 'ignore',
  build: { assets: 'assets' },
});
