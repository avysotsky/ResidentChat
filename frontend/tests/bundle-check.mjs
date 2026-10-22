// Post-build integrity guard. Executed only AFTER Vite build under a
// networkless pinned-SHA offline test environment.
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

const dist = new URL("../dist/", import.meta.url);
const html = readFileSync(new URL("index.html", dist), "utf8");
const js = [...html.matchAll(/<script\b[^>]*\bsrc="([^"]+)"/g)].map(m => m[1]);
const css = [...html.matchAll(/<link\b[^>]*\bhref="([^"]+\.css)"/g)].map(m => m[1]);
assert.equal(js.length, 1, "exactly one bundled local module script");
assert.ok(css.length >= 1, "Vite should create bundled local CSS");
assert.match(js[0], /^\/operator\/console\/assets\/index-[a-zA-Z0-9_-]+\.js$/);
for (const item of [...js, ...css]) {
  assert.match(item, /^\/operator\/console\/assets\/index-[a-zA-Z0-9_-]+\.(?:js|css)$/);
  const file = new URL(item.split("/").at(-1), new URL("assets/", dist));
  assert.ok(readFileSync(file).length > 0, "each referenced bundle asset exists");
}
assert.doesNotMatch(html, /(?:https?:)?\/\//i, "no remote script, style, font or CDN");
assert.doesNotMatch(html, /\bon\w+\s*=/i, "no inline HTML event handlers");
assert.doesNotMatch(html, /<script\b(?![^>]*\bsrc=)[^>]*>/i, "no inline scripts");
const actual = readdirSync(new URL("assets/", dist));
assert.ok(actual.every(name => /^index-[a-zA-Z0-9_-]+\.(js|css)$/.test(name)),
  "bundle contains only static asset types served by the restricted .NET endpoint");
console.log("RC-09 VITE SELF-HOSTED BUNDLE PASS (static integrity; not HTTP acceptance)");
