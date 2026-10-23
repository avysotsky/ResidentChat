import { useEffect, useRef, useState } from "react";
import type { ReviewReceipt } from "../api/contracts";
import { getReviewDetail } from "../api/operatorApi";

const reasons: Record<ReviewReceipt["reason"], string> = {
  1: "Невизначена класифікація",
  2: "Можлива аварія / загроза",
  3: "Не є заявкою на ремонт",
  4: "Локальна модель недоступна",
  5: "Непідтримуваний формат"
};

interface Props {
  items: ReviewReceipt[];
  message: string;
}

/** No auto-decrypt, no caching, no browser storage or automatic action. */
export function TelegramReviewsPanel({ items, message }: Props) {
  const [selected, setSelected] = useState<number | null>(null);
  const [detail, setDetail] = useState("");
  const [feedback, setFeedback] = useState("");
  const [busy, setBusy] = useState(false);
  const active = useRef<AbortController | null>(null);

  useEffect(() => {
    const clear = () => {
      active.current?.abort();
      active.current = null;
      setSelected(null);
      setDetail("");
    };
    window.addEventListener("pagehide", clear);
    return () => {
      window.removeEventListener("pagehide", clear);
      clear();
    };
  }, []);

  function close() {
    active.current?.abort();
    active.current = null;
    setSelected(null);
    setDetail("");
    setFeedback("");
    setBusy(false);
  }
  async function open(id: number) {
    close();
    const request = new AbortController();
    active.current = request;
    setBusy(true);
    try {
      const read = await getReviewDetail(id, request.signal);
      if (request.signal.aborted) return;
      setSelected(id);
      setDetail(read.text);
      setFeedback("");
    } catch {
      if (!request.signal.aborted)
        setFeedback("Не вдалося отримати текст; перевірте JWT і PostgreSQL.");
    } finally {
      if (!request.signal.aborted) setBusy(false);
    }
  }

  return (
    <section className="panel review-panel" aria-labelledby="review-title">
      <div className="section-top">
        <div>
          <p className="eyebrow">РУЧНИЙ РОЗГЛЯД · ШИФРОВАНИЙ POSTGRESQL</p>
          <h2 id="review-title">Потребують уваги <span className="count-total">{items.length}</span></h2>
        </div>
        <span className="status-tag warning">Не створює заявку</span>
      </div>
      <p className="muted small">
        Текст відкривається тільки за вашою дією, після перевірки JWT.
        Кожне розшифрування реєструється в аудиті. Тестові дані.
      </p>
      {message && <p className="muted small" role="status">{message}</p>}
      {!message && items.length === 0 && <p className="muted small">Повідомлень немає.</p>}
      {items.length > 0 && (
        <ul className="review-list">
          {items.map(item => (
            <li key={item.updateId}>
              <div>
                <strong>Update #{item.updateId}</strong>
                <span>{reasons[item.reason]}</span>
                <small>Доступно до {new Date(item.expiresAt).toLocaleString("uk-UA")}</small>
              </div>
              <button className="button-secondary" type="button" disabled={busy}
                onClick={() => void open(item.updateId)}>Переглянути</button>
            </li>
          ))}
        </ul>
      )}
      {feedback && <p role="alert" className="error">{feedback}</p>}
      {selected !== null && (
        <section className="review-detail" aria-label="Розшифроване тестове повідомлення">
          <div className="section-top">
            <strong>Update #{selected}</strong>
            <button type="button" className="button-secondary" onClick={close}>
              Приховати текст
            </button>
          </div>
          <p>{detail || "Повідомлення без тексту (наприклад, вкладення)."}</p>
        </section>
      )}
    </section>
  );
}
