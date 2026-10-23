import type { IntakeReceipt } from "../api/contracts";

interface Props {
  authenticated: boolean;
  loading: boolean;
  items: IntakeReceipt[];
  message: string;
}
const label: Record<IntakeReceipt["state"], string> = {
  1: "Прийнято в PostgreSQL",
  2: "Обробляється",
  3: "Опрацьовано"
};

/** Metadata visibility only; NEVER offers ticket actions or message text. */
export function TelegramIntakePanel({ authenticated, loading, items, message }: Props) {
  if (!authenticated) return null;
  return (
    <section className="intake panel" aria-labelledby="intake-title">
      <div className="section-top">
        <div>
          <p className="eyebrow">ТЕСТОВИЙ TELEGRAM → POSTGRESQL</p>
          <h2 id="intake-title">Отримані оновлення <span className="count-total">{items.length}</span></h2>
        </div>
        <span className="status-tag">Лише читання</span>
      </div>
      <p className="muted small">
        Записи відображають тільки факт приймання оновлень. Вони не класифіковані
        Qwen і не є заявками; підтвердити їх як заявку неможливо.
      </p>
      {message && <p role="status" className="muted small">{message}</p>}
      {!message && !loading && items.length === 0 && <p className="muted small">Записів немає.</p>}
      {items.length > 0 && (
        <ul className="intake-list" aria-label="Історія приймання тестових оновлень">
          {items.map(item => (
            <li key={item.updateId}>
              <span className="intake-id">Update #{item.updateId}</span>
              <span>{label[item.state]}</span>
              <time dateTime={item.receivedAt}>
                {new Date(item.receivedAt).toLocaleString("uk-UA")}
              </time>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
