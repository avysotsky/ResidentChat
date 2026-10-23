/**
 * Strict, minimal shape of the EXISTING ASP.NET Core operator API.
 * The backend remains the only source of truth for tenancy, authorization,
 * approval, persistence and ticket transitions.
 */
export type ProposalState = "Pending" | "Approved" | "Rejected" | "InDoubt";

export interface Proposal {
  caseId: string;
  category: string;
  priority: string;
  state: ProposalState;
  ticketId: string | null;
}
export interface TicketReadback {
  ticketId: string;
  status: "Open" | "Escalated" | "Resolved";
  version: number;
}

function object(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    throw new Error("Некоректна структура відповіді API");
  return value as Record<string, unknown>;
}

const GUID = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const CASE = /^[A-Z0-9-]{1,64}$/;

export function parseProposals(data: unknown): Proposal[] {
  if (!Array.isArray(data) || data.length > 500)
    throw new Error("Некоректний список пропозицій API");
  const seen = new Set<string>();
  return data.map((value: unknown) => {
    const x = object(value);
    if (typeof x.caseId !== "string" || !CASE.test(x.caseId)
      || seen.has(x.caseId)
      || typeof x.category !== "string" || x.category.length > 64
      || typeof x.priority !== "string" || x.priority.length > 64
      || !["Pending", "Approved", "Rejected", "InDoubt"].includes(String(x.state))
      || (x.ticketId != null && (typeof x.ticketId !== "string" || !GUID.test(x.ticketId)))
      || (x.state === "Approved" && x.ticketId == null)) {
      throw new Error("Некоректні дані пропозиції API");
    }
    seen.add(x.caseId);
    return {
      caseId: x.caseId,
      category: x.category,
      priority: x.priority,
      state: x.state as ProposalState,
      ticketId: x.ticketId == null ? null : x.ticketId as string
    };
  });
}

export function parseTicket(data: unknown, expectedId: string): TicketReadback {
  const x = object(data);
  if (typeof x.ticketId !== "string" || !GUID.test(x.ticketId)
    || x.ticketId.toLowerCase() !== expectedId.toLowerCase()
    || typeof x.version !== "number" || !Number.isSafeInteger(x.version)
    || x.version < 1) {
    throw new Error("Невірна ідентичність або версія заявки");
  }
  // TicketStatus C# enum: Open=1, Escalated=2, Resolved=3.
  const statuses: Record<string, TicketReadback["status"]> = {
    "1": "Open", "2": "Escalated", "3": "Resolved",
    "Open": "Open", "Escalated": "Escalated", "Resolved": "Resolved"
  };
  const status = typeof x.status === "number" || typeof x.status === "string"
    ? statuses[String(x.status)] : undefined;
  if (!status) throw new Error("Невідомий стан заявки PostgreSQL");
  return { ticketId: x.ticketId, status, version: x.version };
}

/**
 * Committed Telegram intake metadata, not a classified ticket.
 * Server never exposes chat IDs, chat hashes, residents or message contents.
 */
export interface IntakeReceipt {
  updateId: number;
  state: 1 | 2 | 3;
  receivedAt: string;
}
export function parseIntake(data: unknown): IntakeReceipt[] {
  if (!Array.isArray(data) || data.length > 50)
    throw new Error("Некоректна кількість Telegram-оновлень");
  const seen = new Set<number>();
  return data.map(value => {
    const item = object(value);
    if (typeof item.updateId !== "number" || !Number.isSafeInteger(item.updateId)
      || item.updateId < 0 || seen.has(item.updateId)
      || ![1, 2, 3].includes(item.state as number)
      || typeof item.receivedAt !== "string" || item.receivedAt.length > 48
      || !Number.isFinite(Date.parse(item.receivedAt))) {
      throw new Error("Некоректні метадані Telegram від API");
    }
    seen.add(item.updateId);
    return {
      updateId: item.updateId,
      state: item.state as 1 | 2 | 3,
      receivedAt: item.receivedAt
    };
  });
}

/** An encrypted, expiring review row. No group/user ID, text or chat hash. */
export interface ReviewReceipt {
  updateId: number;
  reason: 1 | 2 | 3 | 4 | 5;
  receivedAt: string;
  expiresAt: string;
}
export interface ReviewDetail {
  updateId: number;
  reason: ReviewReceipt["reason"];
  text: string;
  expiresAt: string;
}
function reviewId(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}
function reviewReason(value: unknown): value is ReviewReceipt["reason"] {
  return value === 1 || value === 2 || value === 3
    || value === 4 || value === 5;
}
function isoDate(value: unknown): value is string {
  return typeof value === "string" && value.length <= 48
    && !Number.isNaN(Date.parse(value));
}
export function parseReviews(data: unknown): ReviewReceipt[] {
  if (!Array.isArray(data) || data.length > 50)
    throw new Error("Неприпустимий список ручного розгляду");
  const seen = new Set<number>();
  return data.map(value => {
    const v = object(value);
    if (!reviewId(v.updateId) || seen.has(v.updateId)
      || !reviewReason(v.reason) || !isoDate(v.receivedAt)
      || !isoDate(v.expiresAt))
      throw new Error("Неприпустимі метадані ручного розгляду");
    seen.add(v.updateId);
    return {
      updateId: v.updateId, reason: v.reason,
      receivedAt: v.receivedAt, expiresAt: v.expiresAt
    };
  });
}
export function parseReviewDetail(value: unknown, expectedId: number): ReviewDetail {
  const v = object(value);
  if (!reviewId(v.updateId) || v.updateId !== expectedId
    || !reviewReason(v.reason) || !isoDate(v.expiresAt)
    || typeof v.text !== "string" || v.text.length > 4096)
    throw new Error("Неприпустимий результат розшифрування");
  return { updateId: v.updateId, reason: v.reason,
    text: v.text, expiresAt: v.expiresAt };
}
