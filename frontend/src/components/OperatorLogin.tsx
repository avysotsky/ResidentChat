import { useRef } from "react";
import type { FormEvent } from "react";

interface Props {
  authenticated: boolean;
  busy: boolean;
  onConnect: (token: string) => void;
  onDisconnect: () => void;
}
export function OperatorLogin({ authenticated, busy, onConnect, onDisconnect }: Props) {
  const input = useRef<HTMLInputElement>(null);
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const token = input.current?.value.trim() ?? "";
    if (input.current) input.current.value = "";
    if (token) onConnect(token);
  }
  return (
    <section className="login panel" aria-labelledby="access-title">
      <div className="section-top">
        <div>
          <p className="eyebrow">ДОСТУП ОПЕРАТОРА</p>
          <h2 id="access-title">Синтетична сесія</h2>
        </div>
        <span className={authenticated ? "status-tag ok" : "status-tag"}>
          {authenticated ? "Сесію відкрито" : "Не авторизовано"}
        </span>
      </div>
      <form onSubmit={submit} autoComplete="off">
        <label htmlFor="operator-jwt">Тестовий JWT оператора</label>
        <div className="login-controls">
          <input id="operator-jwt" ref={input} type="password" autoComplete="off"
            spellCheck={false} aria-describedby="login-note"
            placeholder="Вставте короткочасний токен локальної лабораторії"
            disabled={busy} maxLength={4096} />
          <button disabled={busy} type="submit">Відкрити чергу</button>
          <button disabled={!authenticated && !busy} type="button"
            className="button-secondary" onClick={() => {
              if (input.current) input.current.value = "";
              onDisconnect();
            }}>Скинути доступ</button>
        </div>
      </form>
      <p id="login-note" className="muted small">
        Лише вигадані будинки ALPHA/BETA. JWT зберігається в пам'яті вкладки
        та видаляється під час виходу; не вводьте реальні облікові дані.
      </p>
    </section>
  );
}
