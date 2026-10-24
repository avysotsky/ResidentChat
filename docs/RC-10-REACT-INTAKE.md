
## RC-10: read-only React metadata visibility

The signed \`GET /operator/console/v1/intake\` endpoint is exposed ONLY
when both synthetic operator lab and explicitly mapped metadata-test intake
are ready. It uses PostgreSQL transaction-local RLS and a bound tenant ID,
limits results to the newest 50, returns only update ID/state/timestamp.
The API never returns message body, group identity or chat hash.

The React frontend renders this separately as a read-only panel. These are
NOT classified maintenance proposals and have no Approve/Reject buttons.
The existing fictional ALPHA/BETA proposal queue is deliberately unchanged;
no live action is generated from unclassified Telegram intake. The
independent PostgreSQL acceptance suite verifies JWT protection,
cross-tenant isolation, real database readback and no text leakage.

This is an observability step, not the full Telegram → Qwen → actionable
PostgreSQL proposal pipeline. Real group activation remains prohibited in
this temporary metadata-only test mode.
