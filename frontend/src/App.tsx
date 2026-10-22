import { useCallback, useEffect, useRef, useState } from "react";
import type { Proposal } from "./api/contracts";
import {
  changeProposal, clearSyntheticToken, getProposals,
  hasSyntheticToken, useSyntheticToken
} from "./api/operatorApi";
import { OperatorLogin } from "./components/OperatorLogin";
import { ProposalList } from "./components/ProposalList";

export default function App() {
  const [authenticated, setAuthenticated] = useState(false);
  const [proposals, setProposals] = useState<Proposal[]>([]);
  const [feedback, setFeedback] = useState("Очікуємо тестову авторизацію оператора.");
  const [loading, setLoading] = useState(false);
  const [busyCase, setBusyCase] = useState<string | null>(null);
  const [blockedIds, setBlockedIds] = useState<ReadonlySet<string>>(new Set());
  const [refreshVersion, setRefreshVersion] = useState(0);
  const [sessionVersion, setSessionVersion] = useState(0);
  const session = useRef(0);
  const activeRead = useRef<AbortController | null>(null);
  const actionInFlight = useRef(false);

  const disconnect = useCallback(() => {
    session.current++;
    activeRead.current?.abort();
    activeRead.current = null;
    clearSyntheticToken();
    setAuthenticated(false);
    setProposals([]);
    setBlockedIds(new Set());
    setBusyCase(null);
    setLoading(false);
    setSessionVersion(session.current);
    setFeedback("Доступ скинуто; локальний тестовий JWT видалено з пам'яті.");
  }, []);

  // A React unmount/navigation must not retain the token or remote readbacks.
  useEffect(() => () => {
    activeRead.current?.abort();
    clearSyntheticToken();
  }, []);

  const refresh = useCallback(async (expectedSession = session.current) => {
    if (!hasSyntheticToken()) {
      setFeedback("Спочатку введіть тестовий JWT.");
      return false;
    }
    activeRead.current?.abort();
    const controller = new AbortController();
    activeRead.current = controller;
    setLoading(true);
    try {
      const items = await getProposals(controller.signal);
      if (session.current !== expectedSession || controller.signal.aborted) return false;
      setProposals(items);
      setAuthenticated(true);
      setRefreshVersion(n => n + 1);
      return true;
    } catch (error: unknown) {
      if (session.current !== expectedSession || controller.signal.aborted) return false;
      // Never retain a prior tenant's queue when a signed JWT fails.
      setProposals([]);
      setFeedback("Не вдалося прочитати чергу: " +
        (error instanceof Error ? error.message : "невідома помилка"));
      return false;
    } finally {
      if (session.current === expectedSession && !controller.signal.aborted) setLoading(false);
    }
  }, []);

  const connect = useCallback((token: string) => {
    if (actionInFlight.current) {
      setFeedback("Дочекайтеся завершення поточної операції перед зміною сесії.");
      return;
    }
    disconnect();
    try { useSyntheticToken(token); }
    catch (error: unknown) {
      setFeedback(error instanceof Error ? error.message : "Невірний JWT");
      return;
    }
    const current = session.current;
    setFeedback("Перевіряємо доступ до черги…");
    void refresh(current).then(ok => {
      if (ok && session.current === current)
        setFeedback("Чергу завантажено. Дані лише синтетичні.");
    });
  }, [disconnect, refresh]);

  const act = useCallback((proposal: Proposal, action: "approve" | "reject") => {
    if (actionInFlight.current || !hasSyntheticToken() || blockedIds.has(proposal.caseId)
      || proposal.state !== "Pending") return;
    const message = action === "approve"
      ? "Створити синтетичну заявку PostgreSQL для " + proposal.caseId + "?"
      : "Відхилити " + proposal.caseId + " без запису до PostgreSQL?";
    if (!window.confirm(message)) return;

    actionInFlight.current = true;
    const expectedSession = session.current;
    setBusyCase(proposal.caseId);
    void (async () => {
      let confirmed = false;
      try {
        await changeProposal(proposal, action);
        confirmed = true;
        if (session.current !== expectedSession) return;
        const updated = await refresh(expectedSession);
        if (session.current !== expectedSession) return;
        setFeedback(updated
          ? action === "approve"
            ? "Оператор підтвердив заявку. Стан PostgreSQL перевіряється в картці."
            : "Пропозицію відхилено. Запис до PostgreSQL не створювався."
          : "Операцію підтверджено сервером, але оновлення черги не вдалося. Виконайте лише GET.");
      } catch (error: unknown) {
        if (session.current !== expectedSession) return;
        // Even if HTTP 409/503 or the connection failed after a commit,
        // do not silently retry a state-changing request.
        setBlockedIds(previous => new Set([...previous, proposal.caseId]));
        setFeedback((confirmed ? "Операцію прийнято, але стан не прочитано. " :
          "Результат дії не підтверджено. ")
          + (error instanceof Error ? error.message : "невідома помилка")
          + " Повторний POST заблоковано; звірте серверний стан.");
        await refresh(expectedSession); // GET only, no second mutation.
      } finally {
        actionInFlight.current = false;
        if (session.current === expectedSession) setBusyCase(null);
      }
    })();
  }, [blockedIds, refresh]);

  return (
    <main className="shell">
      <header className="hero">
        <div className="hero-top">
          <p className="eyebrow">RESIDENTCHAT · OFFLINE SYNTHETIC LAB</p>
          <span className="lab-mark">Локальна лабораторія</span>
        </div>
        <h1>Панель диспетчера</h1>
        <p>Перевірка пропозицій, ручне рішення та контроль стану заявки у PostgreSQL.
          Дані лише вигадані; жодного реального Telegram або зовнішнього AI.</p>
      </header>
      <OperatorLogin authenticated={authenticated} busy={busyCase !== null}
        onConnect={connect} onDisconnect={() => {
          if (actionInFlight.current) {
            setFeedback("Триває запит; змінювати сесію зараз не можна.");
            return;
          }
          disconnect();
        }} />
      <p className="feedback" role="status" aria-live="polite">{feedback}</p>
      <ProposalList proposals={proposals} authenticated={authenticated}
        loading={loading} busy={busyCase !== null} blockedIds={blockedIds}
        refreshVersion={refreshVersion} sessionVersion={sessionVersion}
        onRefresh={() => void refresh().then(ok => {
          if (ok) setFeedback("Чергу оновлено; статуси перевіряються окремо.");
        })}
        onAction={act} />
      <footer>Доступ визначає серверний підписаний JWT та tenant-scope. Підтвердження
        і відхилення не автоматизовані. Qwen3 не має прямого доступу до браузера.
        Для production потрібна окрема архітектура автентифікації та безпеки.</footer>
    </main>
  );
}
