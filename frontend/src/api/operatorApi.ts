import { parseProposals, parseTicket, parseIntake, parseReviews, parseReviewDetail } from "./contracts.ts";
import type { Proposal, TicketReadback, IntakeReceipt, ReviewReceipt, ReviewDetail } from "./contracts";

// A fictional short-lived lab JWT lives ONLY in JS module memory.
// Never use cookie, localStorage, sessionStorage, URL, telemetry or logs.
let syntheticJwt = "";

export function useSyntheticToken(jwt: string): void {
  if (!jwt || jwt.length > 4096 || /\s/.test(jwt))
    throw new Error("Некоректний формат тестового JWT");
  syntheticJwt = jwt;
}
export function clearSyntheticToken(): void { syntheticJwt = ""; }
export function hasSyntheticToken(): boolean { return syntheticJwt.length !== 0; }

// Clear on navigation away, including bfcache pagehide. No browser persistence.
if (typeof window !== "undefined") {
  window.addEventListener("pagehide", clearSyntheticToken);
}

export class OperatorHttpError extends Error {
  readonly status: number;
  constructor(status: number) {
    super(status === 401 ? "HTTP 401: потрібен дійсний JWT"
      : status === 403 ? "HTTP 403: немає доступу до цього будинку"
      : status === 404 ? "HTTP 404: ресурс недоступний"
      : status === 409 ? "HTTP 409: заявку вже опрацьовано"
      : "HTTP " + status);
    this.status = status;
  }
}
async function json(path: string, signal?: AbortSignal, confirmation?: string): Promise<unknown> {
  if (!syntheticJwt) throw new Error("Авторизацію скинуто");
  const headers: Record<string, string> = {
    Authorization: "Bearer " + syntheticJwt,
    Accept: "application/json"
  };
  if (confirmation !== undefined) headers["Content-Type"] = "application/json";
  const response = await fetch(path, {
    method: confirmation === undefined ? "GET" : "POST",
    headers,
    body: confirmation === undefined ? undefined : JSON.stringify({ confirmation }),
    credentials: "omit",
    redirect: "error",
    cache: "no-store",
    signal
  });
  if (!response.ok) throw new OperatorHttpError(response.status);
  return await response.json() as unknown;
}
/** Fail closed to read-only when the host does not supply a valid mode flag. */
export async function getOperatorReadOnlyMode(signal?: AbortSignal): Promise<boolean> {
  const value = await json("/operator/console/v1/mode", signal);
  if (value === null || typeof value !== "object" || Array.isArray(value)
    || typeof (value as { readOnly?: unknown }).readOnly !== "boolean")
    throw new Error("Невідома політика змін оператора");
  return (value as { readOnly: boolean }).readOnly;
}
export async function getProposals(signal?: AbortSignal): Promise<Proposal[]> {
  return parseProposals(await json("/operator/console/v1/proposals", signal));
}
export async function changeProposal(
  proposal: Proposal, action: "approve" | "reject"
): Promise<void> {
  // Intentionally never retry this POST. Failed/ambiguous commits need
  // read-only reconciliation by the operator, not another submission.
  const verb = action === "approve" ? "APPROVE " : "REJECT ";
  const x = await json(
    "/operator/console/v1/proposals/" + encodeURIComponent(proposal.caseId) + "/" + action,
    undefined, verb + proposal.caseId
  );
  if (x === null || typeof x !== "object" || Array.isArray(x)
    || (x as Record<string, unknown>).status !==
      (action === "approve" ? "Approved" : "Rejected")) {
    throw new Error("Неочікуване підтвердження операції API");
  }
}
export async function getTicket(id: string, signal?: AbortSignal): Promise<TicketReadback> {
  return parseTicket(await json("/operator/v1/tickets/" + encodeURIComponent(id), signal), id);
}

/** Read-only, signed and tenant-scoped. The endpoint exists only in test mode. */
export async function getIntake(signal?: AbortSignal): Promise<IntakeReceipt[]> {
  return parseIntake(await json("/operator/console/v1/intake", signal));
}

/** No review text is fetched on page load; detail is explicit operator action. */
export async function getReviews(signal?: AbortSignal): Promise<ReviewReceipt[]> {
  return parseReviews(await json("/operator/console/v1/reviews", signal));
}
export async function getReviewDetail(id: number, signal?: AbortSignal): Promise<ReviewDetail> {
  if (!Number.isSafeInteger(id) || id < 0)
    throw new Error("Некоректний Telegram update ID");
  return parseReviewDetail(await json(
    "/operator/console/v1/reviews/" + String(id), signal), id);
}
