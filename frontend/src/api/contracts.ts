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
