import { useCallback, useEffect, useState } from "react";
import type { Proposal, TicketReadback } from "../api/contracts";
import { getTicket } from "../api/operatorApi";

interface Props {
  proposal: Proposal;
  busy: boolean;
  blocked: boolean;
  refreshVersion: number;
  sessionVersion: number;
  onAction: (proposal: Proposal, action: "approve" | "reject") => void;
}
export function ProposalCard({
  proposal, busy, blocked, refreshVersion, sessionVersion, onAction
}: Props) {
  const [readback, setReadback] = useState<TicketReadback | null>(null);
  const [readError, setReadError] = useState("");
  const [checking, setChecking] = useState(false);
  const [readVersion, setReadVersion] = useState(0);

  const ticketId = proposal.state === "Approved" ? proposal.ticketId : null;
  useEffect(() => {
    setReadback(null);
    setReadError("");
    if (!ticketId) return;
    const controller = new AbortController();
    setChecking(true);
    void getTicket(ticketId, controller.signal).then(ticket => {
      if (!controller.signal.aborted) setReadback(ticket);
    }).catch((error: unknown) => {
      if (!controller.signal.aborted) setReadError(
        error instanceof Error ? error.message : "Помилка читання PostgreSQL"
      );
    }).finally(() => {
      if (!controller.signal.aborted) setChecking(false);
    });
    return () => controller.abort();
  }, [ticketId, refreshVersion, sessionVersion, readVersion]);

  const recheck = useCallback(() => setReadVersion(v => v + 1), []);
  const pending = proposal.state === "Pending";
  return (
    <article className="proposal panel" aria-label={"Пропозиція " + proposal.caseId}>
      <div className="section-top">
        <h3>{proposal.caseId}</h3>
        <span className={"status-tag " + (
          proposal.state === "Approved" ? "ok" :
          proposal.state === "InDoubt" ? "warning" : ""
        )}>{proposal.state}</span>
      </div>
      <dl className="metadata">
        <div><dt>Категорія</dt><dd>{proposal.category}</dd></div>
        <div><dt>Пріоритет</dt><dd>{proposal.priority}</dd></div>
      </dl>
      {ticketId && (
        <div className="ticket-readback" aria-live="polite">
          <p className="eyebrow">POSTGRESQL — ПІДТВЕРДЖЕНИЙ СТАН</p>
          <p className="ticket-id">Ticket ID: {ticketId}</p>
          {checking && <p role="status">Читаємо статус заявки…</p>}
          {readError && <p role="alert" className="error">Не вдалося прочитати стан: {readError}</p>}
          {readback && !checking && !readError && (
            <p role="status" className="db-status">
              {readback.status} <span>· версія {readback.version}</span>
            </p>
          )}
          <button className="button-secondary" type="button"
            onClick={recheck} disabled={checking || busy}>
            Перевірити статус
          </button>
        </div>
      )}
      {proposal.state === "InDoubt" && (
        <p className="warning-text" role="alert">
          Результат запису невідомий. Потрібна ручна звірка БД; повторне підтвердження заборонено.
        </p>
      )}
      {pending && blocked && (
        <p className="warning-text" role="alert">
          Попередня операція могла завершитися. Повторний POST у цій сесії заблоковано;
          перевірте серверний стан без повторного надсилання.
        </p>
      )}
      {pending && (
        <div className="actions">
          <button type="button" disabled={busy || blocked}
            onClick={() => onAction(proposal, "approve")}>Підтвердити</button>
          <button type="button" className="button-danger" disabled={busy || blocked}
            onClick={() => onAction(proposal, "reject")}>Відхилити</button>
        </div>
      )}
      {proposal.state === "Rejected" && (
        <p className="muted small">Відхилено: PostgreSQL-заявка не створювалася.</p>
      )}
    </article>
  );
}
