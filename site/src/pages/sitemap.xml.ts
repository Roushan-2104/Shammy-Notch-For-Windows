// The site's pages for search engines. Built with the site, so it never lists a page that isn't there.
import type { APIRoute } from 'astro';

const pages = ['', 'whats-new/', 'privacy/', 'terms/'];

export const GET: APIRoute = ({ site }) => {
  const base = import.meta.env.BASE_URL.replace(/\/?$/, '/');
  const urls = pages.map((p) => `  <url><loc>${new URL(base + p, site).href}</loc></url>`).join('\n');
  return new Response(`<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`, { headers: { 'Content-Type': 'application/xml' } });
};
