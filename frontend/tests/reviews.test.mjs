import test from "node:test";
import assert from "node:assert/strict";
import { parseReviews, parseReviewDetail } from "../src/api/contracts.ts";
import {
  useSyntheticToken, clearSyntheticToken,
  getReviews, getReviewDetail
} from "../src/api/operatorApi.ts";

const row = {
  updateId: 94001,
  reason: 2,
  receivedAt: "2026-10-23T10:00:00Z",
  expiresAt: "2026-10-30T10:00:00Z"
};
const detail = {
  updateId: 94001,
  reason: 2,
  text: "Тестове повідомлення: прорвало трубу.",
  expiresAt: row.expiresAt
};

test("rejects forged or over-broad encrypted review metadata", () => {
  assert.deepEqual(parseReviews([row]), [row]);
  for (const bad of [
    null, {}, [row, row], [{ ...row, reason: 9 }],
    [{ ...row, updateId: -1 }], [{ ...row, expiresAt: "garbage" }],
    Array.from({ length: 51 }, (_, i) => ({ ...row, updateId: i }))
  ]) assert.throws(() => parseReviews(bad));
});

test("decrypted single-review readback verifies ID and bounded text", () => {
  assert.deepEqual(parseReviewDetail(detail, 94001), detail);
  for (const bad of [
    { ...detail, updateId: 94002 },
    { ...detail, reason: "2" },
    { ...detail, text: 42 },
    { ...detail, text: "x".repeat(4097) }
  ]) assert.throws(() => parseReviewDetail(bad, 94001));
});

test("operator lists reviews without auto-decrypting and explicitly reads one", async () => {
  const prior = globalThis.fetch;
  const seen = [];
  globalThis.fetch = async (url, options) => {
    seen.push({ url, options });
    if (url === "/operator/console/v1/reviews")
      return { ok: true, json: async () => [row] };
    if (url === "/operator/console/v1/reviews/94001")
      return { ok: true, json: async () => detail };
    throw new Error("unexpected cross-origin review route");
  };
  try {
    useSyntheticToken("signed.synthetic.review.jwt");
    assert.deepEqual(await getReviews(), [row]);
    assert.deepEqual(await getReviewDetail(94001), detail);
    assert.equal(seen.length, 2);
    for (const call of seen) {
      assert.equal(call.options.method, "GET");
      assert.equal(call.options.credentials, "omit");
      assert.equal(call.options.cache, "no-store");
      assert.equal(call.options.redirect, "error");
      assert.equal(call.options.headers.Authorization,
        "Bearer signed.synthetic.review.jwt");
    }
    await assert.rejects(getReviewDetail(-1), /Некоректний/);
    await assert.rejects(getReviewDetail(Number.MAX_SAFE_INTEGER + 1), /Некоректний/);
    assert.equal(seen.length, 2);
  } finally {
    clearSyntheticToken();
    globalThis.fetch = prior;
  }
});
