import test from "node:test";
import assert from "node:assert/strict";
import { parseProposals, parseTicket } from "../src/api/contracts.ts";

const id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const alpha = { caseId: "ALPHA-WATER", category: "Water", priority: "Urgent",
  state: "Approved", ticketId: id };

test("server proposal contract: accepts real ASP.NET Core camelCase response", () => {
  assert.deepEqual(parseProposals([alpha]), [alpha]);
  assert.deepEqual(parseProposals([{
    caseId: "BETA-ELEVATOR", category: "Elevator", priority: "Routine",
    state: "Rejected", ticketId: null
  }])[0].state, "Rejected");
});
test("rejects forged/ambiguous/unsafe proposal responses", () => {
  for (const value of [
    {}, null, ["not-an-object"], [{ ...alpha, caseId: "../../tickets" }],
    [{ ...alpha, state: "Approved", ticketId: null }],
    [alpha, alpha], [{ ...alpha, state: "Unauthorized" }]
  ]) assert.throws(() => parseProposals(value));
});
test("PG status: numbered System.Text.Json enum and optimistic version", () => {
  assert.deepEqual(parseTicket({ ticketId: id, status: 2, version: 1 }, id),
    { ticketId: id, status: "Escalated", version: 1 });
  assert.equal(parseTicket({ ticketId: id, status: "Resolved", version: 4 }, id).status,
    "Resolved");
});
test("PG readback never accepts another ticket or unknown version/status", () => {
  const other = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
  for (const data of [
    { ticketId: other, status: 2, version: 1 },
    { ticketId: id, status: 4, version: 1 },
    { ticketId: id, status: 2, version: 0 },
    { ticketId: id, status: 2, version: "1" },
    { ticketId: id, status: 2, version: 1.5 }
  ]) assert.throws(() => parseTicket(data, id));
});
