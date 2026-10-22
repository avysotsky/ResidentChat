import { useState } from "react";
import type { Proposal, ProposalState } from "../api/contracts";
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
type Filter = "All" | ProposalState;
const filters: readonly Filter[] = ["All", "Pending", "Approved", "Rejected", "InDoubt"];
const labels: Record<Filter, string> = {
  All: "Усі", Pending: "Очікують", Approved: "Підтверджені",
  Rejected: "Відхилені", InDoubt: "Потребують звірки"
};

export function ProposalList({
  proposals, authenticated, loading, busy, blockedIds,
  refreshVersion, sessionVersion, onRefresh, onAction
}: Props) {
  const [filter, setFilter] = useState<Filter>("All");
  const counts = filters.map(value => ({
    value,
    count: value === "All" ? proposals.length : proposals.filter(item => item.state === value).length
  }));
  const visible = filter === "All" ? proposals : proposals.filter(item => item.state === filter);
  return (
    <section className="queue" aria-labelledby="queue-title">
      <div className="section-top queue-heading">
        <div>
          <p className="eyebrow">ОПЕРАТОРСЬКА ЧЕРГА</p>
          <h2 id="queue-title">Пропозиції <span className="count-total">{proposals.length}</span></h2>
        </div>
        <button className="button-secondary" type="button"
          disabled={!authenticated || loading || busy} onClick={onRefresh}>
          {loading ? "Оновлення…" : "Оновити"}
        </button>
      </div>
      {authenticated && proposals.length > 0 && (
        <div className="queue-filters" aria-label="Фільтр стану пропозицій">
          {counts.map(({ value, count }) => (
            <button key={value} type="button"
              className={"filter-button " + (filter === value ? "selected" : "")}
              aria-pressed={filter === value}
              onClick={() => setFilter(value)}>
              {labels[value]} <span>{count}</span>
            </button>
          ))}
        </div>
      )}
      {visible.length === 0
        ? <p className="empty panel">{!authenticated
          ? "Авторизуйтеся тестовим JWT, щоб побачити лише свій будинок."
          : loading ? "Читаємо чергу…" : proposals.length > 0
            ? "У цій категорії немає пропозицій." : "Пропозицій немає або черга ще не завантажена."}</p>
        : <div className="proposal-grid">
          {visible.map(proposal => (
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
