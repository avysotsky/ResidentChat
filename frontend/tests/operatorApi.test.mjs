import test from "node:test";
import assert from "node:assert/strict";
import {
  useSyntheticToken, clearSyntheticToken, hasSyntheticToken,
  getProposals, changeProposal, getTicket, OperatorHttpError
} from "../src/api/operatorApi.ts";

const id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const alpha = {
  caseId: "ALPHA-WATER", category: "Water", priority: "Urgent",
  state: "Approved", ticketId: id
};
const pending = { ...alpha, state: "Pending", ticketId: null };

test("synthetic operator requests remain same-origin, signed and omit cookies", async () => {
  const originalFetch = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url, options) => {
    calls.push({ url, options });
    if (url === "/operator/console/v1/proposals")
      return { ok: true, json: async () => [alpha] };
    if (url === "/operator/v1/tickets/" + id)
      return { ok: true, json: async () => ({ ticketId: id, status: 2, version: 1 }) };
    if (url === "/operator/console/v1/proposals/ALPHA-WATER/reject")
      return { ok: true, json: async () => ({ status: "Rejected" }) };
    throw new Error("Unexpected synthetic API route");
  };
  try {
    useSyntheticToken("signed.synthetic.jwt");
    assert.equal(hasSyntheticToken(), true);
    assert.equal((await getProposals())[0].caseId, "ALPHA-WATER");
    assert.equal((await getTicket(id)).status, "Escalated");
    await changeProposal(pending, "reject");
    assert.equal(calls.length, 3);
    for (const call of calls) {
      assert.match(call.url, /^\/operator\//);
      assert.equal(call.options.headers.Authorization, "Bearer signed.synthetic.jwt");
      assert.equal(call.options.credentials, "omit");
      assert.equal(call.options.redirect, "error");
      assert.equal(call.options.cache, "no-store");
    }
    assert.equal(calls[0].options.method, "GET");
    assert.equal(calls[1].options.method, "GET");
    assert.equal(calls[2].options.method, "POST");
    assert.deepEqual(JSON.parse(calls[2].options.body), {
      confirmation: "REJECT ALPHA-WATER"
    });
  } finally {
    clearSyntheticToken();
    globalThis.fetch = originalFetch;
  }
  assert.equal(hasSyntheticToken(), false);
  await assert.rejects(getProposals(), /Авторизацію скинуто/);
});

test("ambiguous POST failure is not retried by the API client", async () => {
  const old = globalThis.fetch;
  let attempts = 0;
  globalThis.fetch = async () => {
    attempts++;
    throw new TypeError("synthetic connection interrupted after write");
  };
  try {
    useSyntheticToken("signed.synthetic.jwt");
    await assert.rejects(changeProposal(pending, "approve"), /interrupted/);
    assert.equal(attempts, 1);
  } finally {
    clearSyntheticToken();
    globalThis.fetch = old;
  }
});

test("HTTP 401/403/409 are classified without leaking response data", async () => {
  const old = globalThis.fetch;
  let status = 401;
  globalThis.fetch = async () => ({ ok: false, status });
  try {
    useSyntheticToken("signed.synthetic.jwt");
    for (const code of [401, 403, 409]) {
      status = code;
      await assert.rejects(getProposals(), error => error instanceof OperatorHttpError
        && error.status === code && error.message.includes(String(code)));
    }
  } finally {
    clearSyntheticToken();
    globalThis.fetch = old;
  }
});
