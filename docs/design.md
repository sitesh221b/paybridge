# PayBridge — Design Document

## 1. Architecture overview

PayBridge is decomposed into independently deployable services communicating
over the protocol best suited to each boundary: a public REST API for
merchant integration, an HTTP client for the (stubbed) fraud detection
service, PostgreSQL as the durable system of record, and Redis for
idempotency. The Payment API owns correctness — validation, idempotency,
status lifecycle. The Fraud Stub exists as a real networked container so
distributed tracing is genuine, not simulated.

Cross-cutting observability is wired uniformly: every service exports OTLP
traces, metrics, and logs to the OpenTelemetry runtime, terminated at the
Aspire Dashboard for this submission. The OpenTelemetry Collector is an
intentional choice over direct-to-backend export so that telemetry policy
(sampling, redaction, routing) can be centralized in production.

Two services were fully built and instrumented in depth; the wider pipeline
described in the original brief (provider, webhook, Kafka, consumer) is
discussed below as production extensions.

## 2. Service Level Objectives

### SLO 1 — Payment success rate (availability)

- **SLI**: `successful payment requests / total valid payment requests`,
  excluding client 4xx errors. Source: `paybridge.payments.total` counter
  filtered by `status`.
- **Target**: 99.9% over a rolling 30-day window.
- **Alert**: multi-window burn-rate alert. Page when the 1-hour AND 6-hour
  burn rates both exceed 14.4× (fast burn). Ticket on 6-hour burn alone
  (slow burn).

### SLO 2 — End-to-end latency

- **SLI**: P99 of `paybridge.payments.duration` histogram for successful
  payments.
- **Target**: P99 < 2s; P50 < 300ms over the same window.
- **Alert**: page when P99 exceeds 2s for 10 consecutive minutes.

### SLO 3 — Fraud service availability (downstream-dependency SLO)

- **SLI**: `paybridge.fraud.outcomes` success rate vs.
  `paybridge.resilience.breaker_events{transition="opened"}` rate.
- **Target**: ≤ 2 breaker-open events per week; <1% of payments fail with
  `fraud_service_unavailable`.
- **Alert**: page on any breaker-opened event during business hours; ticket
  off-hours unless rate exceeds threshold.

## 3. Incident runbook — "payment success rate dropped"

**Detection.** The SLO 1 burn-rate alert fires.

**Triage order.**

1. Dashboard → Metrics → `paybridge.payments.total`. Filter by `status`,
   `method`, `currency`. Is the drop **scoped** to one dimension or global?
2. Dashboard → Metrics → `paybridge.resilience.breaker_events`. Is the
   fraud breaker **open**?
3. Dashboard → Traces. Filter by HTTP 5xx. Click into one — which span
   failed and with what?
4. Dashboard → Logs. Filter by `Level=Error`. Recent deploys?
5. Infrastructure: `docker compose ps`. Are Postgres/Redis healthy?

**Likely root causes (in order of probability).**

- Fraud service degraded → breaker open → payments failing with
  `fraud_service_unavailable`.
- Postgres saturation (slow queries, connection pool exhaustion) → DB spans
  in traces show high latency.
- Bad deploy → error rate spike correlates with deploy time.
- Cache outage (only causes degradation, not outage — DB fallback path).

**Mitigation.**

- Fraud service down: confirm via the fraud-stub's `/health/live`; if
  upstream-owned, escalate to that team. The breaker is already shedding
  load; payments fail-closed (returning 503) — this is intentional.
- Postgres saturation: scale the connection pool, identify and kill the
  slow query, consider a read replica for the idempotency lookup path.
- Bad deploy: roll back via the deployment system.
- Throttling: use the kill switch (config flag, hot-reloadable) to shed
  non-critical merchants.

**Verify recovery.** Watch the SLO burn rate return below 1×. Capture the
incident timeline and root cause for the postmortem.

## 4. PII & data governance

The service handles customer emails, payment amounts, and merchant
identifiers. Controls applied:

- **Logs**: customer emails are not logged. Logged identifiers are
  `MerchantId` (business identifier, not personal) and `PaymentId` (system
  identifier). Amounts are logged for business observability but are not
  personal data in isolation. Card data and CVV are never present in this
  service (out of PCI scope by design — providers handle that).
- **Traces**: span attributes carry the same identifiers. The
  `db.statement` attribute can include parameter values; in production
  this would be scrubbed by a Collector processor for tables with
  sensitive columns.
- **Metrics**: labels are bounded categorical values (status, method,
  currency, outcome). PII and high-cardinality identifiers (PaymentId,
  email, raw amount) are never used as labels — both for privacy and to
  prevent cardinality explosion.
- **In transit / at rest**: TLS between services in production; encrypted
  database columns for sensitive fields; least-privilege DB roles;
  tenant-scoped queries so cross-tenant leakage is impossible.
- **Retention & erasure**: telemetry retention windows are configured at
  the backend (not in the apps) to enable centralized policy enforcement.
  Erasing a customer means deleting from the DB; because PII never lands
  in logs or metrics, no telemetry purge is needed.

## 5. Cost awareness at 1,000 payments/minute

At ~1.4M payments/day, naive "trace and log everything at full fidelity"
becomes expensive. Levers:

- **Tail-based sampling** at the OpenTelemetry Collector. Keep 100% of
  errors and slow traces (P95+); sample 5–10% of successful fast traces.
  Avoids paying to store millions of identical happy paths while keeping
  the interesting ones.
- **Metric cardinality discipline.** Already enforced by design: labels
  are closed sets, never IDs or free-form text. The dominant cost in
  metrics backends is unique time series, not data volume.
- **Log levels.** Info for lifecycle events and decisions; debug suppressed
  in production. Framework log noise (HttpClient, EF Core) is dampened to
  Warning by configuration (see `appsettings.json`).
- **Retention tiering.** Hot storage (queryable) for 7 days; cold/aggregate
  for longer. Sampling exemplars preserved for representative traces.
- **The Collector as a cost-control point.** Filter, batch, drop, and route
  telemetry centrally so each service stays dumb and policy lives in one
  place that can be tuned without redeploying apps.

The trade-off: observability cost vs debuggability. Keep enough fidelity on
errors and tails to debug incidents; sample the boring majority.

## 6. Production extensions (out of scope here)

- **Provider service + webhook + trace relinking via span links.** Webhooks
  arrive as fresh HTTP requests with no trace context. Store the original
  TraceId on the payment row at provider-call time; the webhook entry creates
  a new span and uses `Activity.AddLink` to causally connect back. This is
  the senior-signal piece of the original brief and the first thing I would
  add.
- **Message queue + Outbox pattern + Settlement consumer.** Avoid the
  dual-write problem (payment persisted but event not published) by writing
  the event to an `outbox` table in the same DB transaction. A relay polls
  and publishes. Settlement consumer reads events and persists; consumer
  is idempotent against duplicate delivery.
- **Migration runner**: separate job in CI/CD or a `dotnet ef migrations
bundle` sidecar, not on app startup.
- **Kill switch**: config flag (hot-reloadable via `IOptionsMonitor`) to
  disable payment processing without restart, observable as a gauge.
- **gRPC for fraud**: typed contract via protobuf, proper internal RPC.
