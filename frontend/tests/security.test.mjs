import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
const root = new URL("../src/", import.meta.url);
function sources(path) {
  const dir = new URL(path, root);
  return readdirSync(dir, { withFileTypes: true }).flatMap(entry =>
    entry.isDirectory() ? sources(path + entry.name + "/")
      : /\.tsx?$/.test(entry.name)
        ? [readFileSync(new URL(entry.name, dir), "utf8")]
        : []);
}
const src = sources("").join("\n");
test("no credential persistence, remote endpoints, HTML injection or telemetry", () => {
  for (const pattern of [
    /\blocalStorage\s*[.\[]/, /\bsessionStorage\s*[.\[]/,
    /\bdocument\.cookie\b/, /dangerouslySetInnerHTML\s*=/,
    /\binnerHTML\s*=/, /https?:\/\//,
    /\bsendBeacon\s*\(/, /\bOllama\s*\(/
  ]) assert.doesNotMatch(src, pattern);
});
test("explicit approval, no retry of mutations and in-memory JWT", () => {
  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const api = readFileSync(new URL("../src/api/operatorApi.ts", import.meta.url), "utf8");
  assert.match(app, /window\.confirm\(/);
  assert.match(app, /actionInFlight\.current/);
  assert.match(app, /addEventListener\("pagehide", scrubOnPageHide\)/);
  assert.match(app, /const scrubOnPageHide = \(\) => disconnect\(\)/);
  assert.match(app, /setBlockedIds/);
  assert.match(api, /let syntheticJwt = ""/);
  assert.match(api, /credentials: "omit"/);
  assert.match(api, /redirect: "error"/);
  assert.match(api, /cache: "no-store"/);
  assert.match(api, /"APPROVE "/);
  assert.match(api, /"REJECT "/);
  assert.doesNotMatch(api, /\bretry\s*\(/);
});
