namespace ResidentChat.Api.Operator;

/// <summary>
/// Static synthetic-lab operator UI assets. No third-party scripts, fonts,
/// telemetry, storage, cookies, dynamic HTML interpolation or remote resources.
/// </summary>
public static class SyntheticConsolePage
{
    public const string Html = """
        <!doctype html>
        <html lang="ru">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="robots" content="noindex, nofollow, noarchive">
          <title>ResidentChat — диспетчер (тест)</title>
          <link rel="stylesheet" href="/operator/console/app.css">
          <script src="/operator/console/app.js" defer></script>
        </head>
        <body>
          <main class="shell">
            <header>
              <div class="eyebrow">RESIDENTCHAT · LOCAL LAB ONLY</div>
              <h1>Панель диспетчера</h1>
              <p class="muted">Только вымышленные заявки. Доступ с локального компьютера.
              Действия требуют подписанный JWT оператора; пароли и реальные сообщения здесь не используются.</p>
            </header>
            <section class="login" aria-label="Доступ оператора">
              <label for="jwt">Тестовый JWT оператора</label>
              <div class="login-controls">
                <input id="jwt" type="password" autocomplete="off"
                  autocapitalize="off" spellcheck="false" placeholder="Вставьте локальный тестовый токен">
                <button id="connect" type="button">Открыть мою очередь</button>
                <button id="clear" class="quiet" type="button">Сбросить доступ</button>
              </div>
              <small class="muted">Токен хранится только в памяти вкладки, не в localStorage/cookies.
              Не вводите реальные учётные данные в эту лабораторную панель.</small>
            </section>
            <p id="feedback" role="status" aria-live="polite">Ожидается вход оператора.</p>
            <section class="content" aria-label="Предложения заявок">
              <div class="section-heading">
                <h2>Предложения</h2>
                <button id="refresh" class="quiet" type="button">Обновить</button>
              </div>
              <div id="proposals" class="grid">
                <p class="empty">После входа здесь появятся предложения вашего тестового дома.</p>
              </div>
            </section>
            <footer>Нет подключения к Telegram. Нет облачного AI. Любая запись в PostgreSQL требует действия
            авторизованного оператора. Отклонение отмечается только во временной памяти процесса.</footer>
          </main>
        </body>
        </html>
        """;

    public const string Css = """
        :root{color-scheme:light;font-family:system-ui,-apple-system,"Segoe UI",sans-serif;
        background:#f4f6f9;color:#1c2535;font-size:15px}
        *{box-sizing:border-box}body{margin:0}.shell{max-width:1100px;margin:auto;padding:38px 26px}
        h1{font-size:2rem;letter-spacing:-.045em;margin:.4rem 0 .65rem}
        h2{font-size:1.2rem;margin:0}.eyebrow{color:#526378;font-weight:750;font-size:.72rem;letter-spacing:.14em}
        .muted,.empty{color:#627084;line-height:1.6}header{margin-bottom:26px}
        header p{max-width:70ch}section.login{padding:21px;border:1px solid #dce1e9;
        border-radius:16px;background:#fff;display:grid;gap:10px}
        label{font-weight:650}.login-controls{display:flex;flex-wrap:wrap;gap:10px}
        input{min-width:280px;flex:1;padding:12px 14px;border:1px solid #a8b4c7;
        border-radius:9px;font:inherit}
        button{border:1px solid #253a62;background:#253a62;color:#fff;padding:11px 16px;
        border-radius:9px;font:inherit;font-weight:650;cursor:pointer}
        button.quiet{background:#fff;color:#253a62}
        button:disabled{opacity:.45;cursor:not-allowed}
        button.danger{color:#a03d37;border-color:#a03d37;background:#fff}
        button:focus-visible,input:focus-visible{outline:3px solid #81abd8;outline-offset:2px}
        #feedback{min-height:24px;padding:4px 1px;color:#415b77}
        .content{margin-top:16px}.section-heading{display:flex;justify-content:space-between;
        align-items:center;margin-bottom:16px}.grid{display:grid;
        grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:15px}
        article{background:#fff;border:1px solid #dce1e9;border-radius:14px;padding:20px}
        article h3{font-size:1rem;margin:0 0 12px}
        .pill{font-size:.75rem;background:#edf2f7;padding:5px 8px;border-radius:50px;
        display:inline-block;margin-bottom:12px}
        .summary{margin:14px 0;color:#4a5669;line-height:1.7}
        .actions{display:flex;gap:9px;flex-wrap:wrap}
        .ticket{font-size:.82rem;word-break:break-word;color:#49647f}
        footer{margin-top:42px;padding-top:16px;border-top:1px solid #d5dce6;
        color:#687689;font-size:.8rem;line-height:1.6}
        @media(max-width:550px){.shell{padding:22px 15px}.login-controls>*{width:100%}}
        """;

    public const string JavaScript = """
        "use strict";
        (() => {
          const tokenInput = document.getElementById("jwt");
          const status = document.getElementById("feedback");
          const proposals = document.getElementById("proposals");
          let sessionJwt = "";
          let busy = false;

          const setStatus = (message) => { status.textContent = message; };
          const addText = (container, tag, value, className) => {
            const node = document.createElement(tag);
            node.textContent = String(value);
            if (className) node.className = className;
            container.append(node);
            return node;
          };
          async function api(path, method = "GET", confirmation) {
            const options = {
              method,
              headers: {
                "Authorization": "Bearer " + sessionJwt,
                "Accept": "application/json"
              },
              cache: "no-store",
              credentials: "omit",
              redirect: "error"
            };
            if (confirmation !== undefined) {
              options.headers["Content-Type"] = "application/json";
              options.body = JSON.stringify({ confirmation });
            }
            const result = await fetch(path, options);
            if (!result.ok) throw new Error("HTTP " + result.status);
            return await result.json();
          }

          function makeButton(label, css, callback) {
            const button = document.createElement("button");
            button.type = "button";
            button.textContent = label;
            if (css) button.className = css;
            button.disabled = busy;
            button.addEventListener("click", callback);
            return button;
          }

          async function doAction(item, approve) {
            if (busy || !sessionJwt) return;
            const verb = approve ? "APPROVE " : "REJECT ";
            const confirmMessage = approve
              ? "Создать реальную ТЕСТОВУЮ запись в PostgreSQL для " + item.caseId + "?"
              : "Отклонить предложение " + item.caseId + " (без записи в PostgreSQL)?";
            if (!window.confirm(confirmMessage)) return;
            busy = true;
            try {
              await api("/operator/console/v1/proposals/" + encodeURIComponent(item.caseId)
                + "/" + (approve ? "approve" : "reject"), "POST", verb + item.caseId);
              // Release the action guard BEFORE refresh() renders its buttons.
              // Otherwise makeButton() permanently marks "Проверить статус"
              // disabled, even after the request has completed.
              busy = false;
              await refresh();
              setStatus(approve ? "Тестовая заявка создана по подтверждению оператора."
                : "Предложение отклонено. Запись в базу не создавалась.");
            } catch (error) {
              setStatus("Действие не выполнено. " + error.message
                + ". Если запись могла быть создана, не повторяйте подтверждение до проверки.");
            } finally { busy = false; }
          }

          async function showTicket(item, statusLine) {
            if (!sessionJwt || !item.ticketId) {
              statusLine.textContent = "Статус в PostgreSQL: требуется авторизация.";
              return;
            }
            const requestedWith = sessionJwt;
            statusLine.textContent = "Статус в PostgreSQL: проверяется…";
            try {
              const ticket = await api("/operator/v1/tickets/" + encodeURIComponent(item.ticketId));
              if (requestedWith !== sessionJwt || !statusLine.isConnected) return;
              if (!ticket || ticket.ticketId !== item.ticketId)
                throw new Error("неожиданный ответ API");
              const statuses = {1:"Open", 2:"Escalated", 3:"Resolved"};
              const code = String(ticket.status);
              const current = statuses[code] || (
                ["Open", "Escalated", "Resolved"].includes(code) ? code : "Unknown");
              const version = Number.isInteger(ticket.version) ? ticket.version : "?";
              const summary = "Заявка " + item.caseId + ": " + current +
                " (версия " + version + ")";
              statusLine.textContent = "Статус в PostgreSQL: " +
                current + " · версия " + version;
              setStatus(summary);
            } catch (error) {
              if (requestedWith !== sessionJwt || !statusLine.isConnected) return;
              statusLine.textContent = "Ошибка чтения статуса: " + error.message;
              setStatus("Не удалось получить состояние заявки: " + error.message);
            }
          }

                    function render(items) {
            proposals.replaceChildren();
            if (!Array.isArray(items) || items.length === 0) {
              addText(proposals, "p", "Предложений для этого оператора нет.", "empty");
              return;
            }
            for (const item of items) {
              const card = document.createElement("article");
              addText(card, "h3", item.caseId);
              addText(card, "span", item.state, "pill");
              addText(card, "p", "Категория: " + item.category +
                " · Приоритет: " + item.priority, "summary");
              const actions = document.createElement("div");
              actions.className = "actions";
              if (item.state === "Pending") {
                actions.append(makeButton("Подтвердить", "", () => doAction(item, true)));
                actions.append(makeButton("Отклонить", "danger", () => doAction(item, false)));
              } else if (item.state === "Approved" && item.ticketId) {
                addText(card, "p", "Ticket ID: " + item.ticketId, "ticket");
                const statusLine = addText(card, "p",
                  "Статус в PostgreSQL: ещё не проверен", "ticket");
                statusLine.setAttribute("role", "status");
                actions.append(makeButton("Проверить статус", "quiet",
                  () => showTicket(item, statusLine)));
                // A confirmed ticket is read immediately on load and after approval.
                // The button remains available for another explicit status check.
                void showTicket(item, statusLine);
              } else if (item.state === "InDoubt") {
                addText(card, "p", "Требуется ручная сверка БД; повтор запрещён.", "ticket");
              }
              card.append(actions);
              proposals.append(card);
            }
          }

          async function refresh() {
            if (!sessionJwt) { setStatus("Сначала введите тестовый JWT."); return; }
            try {
              const data = await api("/operator/console/v1/proposals");
              render(data);
              setStatus("Очередь загружена. Только синтетические предложения.");
            } catch (error) {
              proposals.replaceChildren();
              setStatus("Нет доступа к очереди: " + error.message);
            }
          }

          document.getElementById("connect").addEventListener("click", async () => {
            sessionJwt = tokenInput.value.trim();
            tokenInput.value = "";
            if (sessionJwt.length > 4096) sessionJwt = "";
            await refresh();
          });
          document.getElementById("clear").addEventListener("click", () => {
            sessionJwt = "";
            tokenInput.value = "";
            proposals.replaceChildren();
            setStatus("Доступ сброшен. Данные токена забыты вкладкой.");
          });
          document.getElementById("refresh").addEventListener("click", refresh);
          window.addEventListener("pagehide", () => { sessionJwt = ""; });
        })();
        """;
}
