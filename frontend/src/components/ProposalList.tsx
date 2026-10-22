import type { Proposal } from "../api/contracts";
import { ProposalCard } from "./ProposalCard";

interface Props {
  proposals: Proposal[];
  authenticated: boolean;
  loading: boolean;
  busy: boolean;
  blockedIds: ReadonlySet<string>;
  refreshVersion: number;
  sessionVersion: number;
  onRefresh: () => void;
  onAction: (proposal: Proposal, action: "approve" | "reject") => void;
}
export function ProposalList({
  proposals, authenticated, loading, busy, blockedIds,
  refreshVersion, sessionVersion, onRefresh, onAction
}: Props) {
  return (
    <section className="queue" aria-labelledby="queue-title">
      <div className="section-top queue-heading">
        <div>
          <p className="eyebrow">ОПЕРАТОРСЬКА ЧЕРГА</p>
          <h2 id="queue-title">Пропозиції</h2>
        </div>
        <button className="button-secondary" type="button"
          disabled={!authenticated || loading || busy} onClick={onRefresh}>
          {loading ? "Оновлення…" : "Оновити"}
        </button>
      </div>
      {proposals.length === 0
        ? <p className="empty panel">{authenticated
          ? (loading ? "Читаємо чергу…" : "Пропозицій немає або черга ще не завантажена.")
          : "Авторизуйтеся тестовим JWT, щоб побачити лише свій будинок."}</p>
        : <div className="proposal-grid">
          {proposals.map(proposal => (
            <ProposalCard key={sessionVersion + ":" + proposal.caseId}
              proposal={proposal} busy={busy || loading}
              blocked={blockedIds.has(proposal.caseId)}
              refreshVersion={refreshVersion} sessionVersion={sessionVersion}
              onAction={onAction} />
          ))}
        </div>}
    </section>
  );
}
