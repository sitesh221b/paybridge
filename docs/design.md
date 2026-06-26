# PayBridge — Design Document

## 1. Architecture overview

PayBridge is decomposed into independently deployable services that
communicate over the protocol most natural to each boundary: REST for the
public merchant-facing API, gRPC for the internal fraud RPC, HTTP for the
third-party provider integration, an HTTP webhook for the asynchronous
provider callback, AMQP for downstream event distribution, and SQL for
durable settlement persistence. The Payment API is the orchestrator and
owns correctness — validation, idempotency, the status lifecycle, and
the integrity guarantees that prevent double-charging under retry. The
Settlement Consumer owns durable settlement persistence and is idempotent
against duplicate delivery.

Two services (Fraud, Provider) are deliberate stubs — real containers
returning random or scripted responses. The assignment explicitly allows
this, and the observability story is the same regardless of business
logic depth. The architecture choices that demonstrate seniority — trace
propagation across protocol boundaries, idempotency design, resilience
patterns, cost-aware observability — are the same in either case.

All services emit OTLP telemetry to an OpenTelemetry Collector that
forwards to a backend (Aspire Dashboard for this submission). The
Collector is an intentional layer: it concentrates telemetry policy
(sampling, attribute scrubbing, multi-backend routing) so that apps stay
dumb and policy can change without app redeploys. This is the
production-shaped abstraction even when overkill for a take-home.

## 2. Service Level Objectives

Three SLOs cover the dimensions an on-call engineer cares about: can we
serve traffic, are we serving it fast enough, and is the downstream
settlement keeping up.

### SLO 1 — Payment success rate (availability)

- **SLI**: `successful_payments / valid_payment_requests`. Source: the
  `paybridge.payments.total` counter, filtered to exclude client 4xx.
- **Target**: 99.9% over a rolling 30-day window. Error budget: 0.1% =
  ~43 minutes of full-outage equivalent per month.
- **Alerting**: multi-window burn-rate alert. Page when the 1-hour AND
  6-hour burn rates both exceed 14.4× (fast burn — budget exhausted in
  ~2 days if continued). Ticket on 6-hour burn alone (slow burn).

### SLO 2 — End-to-end latency

- **SLI**: P99 of the `paybridge.payments.duration` histogram for
  successful payments.
- **Target**: P99 < 2 seconds, P50 < 300 ms.
- **Alerting**: page when P99 exceeds 2s for 10 consecutive minutes. The
  10-minute window prevents pager noise from single-request anomalies.

### SLO 3 — Settlement freshness

- **SLI**: time between `PaymentCompleted` event publication and the
  settlement row appearing in Postgres. Source: difference between the
  webhook handler's `payment.completed publish` span end-time and the
  consumer's `INSERT INTO settlements` span end-time, surfaced as a
  derived metric.
- **Target**: 99% of settlements persisted within 60 seconds of the
  triggering event.
- **Alerting**: page when consumer lag exceeds threshold for 5 minutes;
  also page on consumer process down.

## 3. Incident runbook — "payment success rate dropped"

**Detection**: SLO 1 fast-burn alert fires.

**Triage order:**

1. **Dashboard → Metrics → `paybridge.payments.total`**. Filter by `status`,
   `method`, `currency`, `merchant`. Is the drop **scoped** to one
   dimension (one merchant, one card type) or global? Scope determines
   whether this is a customer issue, a partner issue, or a system issue.
2. **Dashboard → Metrics → `paybridge.resilience.breaker_events`**. Is
   the fraud breaker open? Most likely cause of a system-wide drop.
3. **Dashboard → Traces, filter HTTP 5xx**. Click into a failing trace.
   The span that errored will tell us which dependency is implicated.
4. **Dashboard → Logs, filter Level=Error**. Correlate with recent
   deploys, infra changes, breaker events.
5. **`docker compose ps`**. Postgres or RabbitMQ degraded?

**Likely root causes, in order of probability:**

- Fraud service is degraded → breaker is open → payments fail with
  `fraud_service_unavailable`. Mitigation: confirm via fraud-stub
  `/health/live`. If upstream-owned, escalate. Our fail-closed posture
  is intentional — we don't process payments when fraud cannot be
  evaluated. Switching to fail-open is a policy decision, not an
  engineering one.
- Provider integration broken → trace will show the provider HTTP call
  failing. Mitigation: route to alternate provider (merchant config); if
  no alternate, communicate maintenance window.
- Postgres saturation → DB spans show high latency or timeouts.
  Mitigation: scale connection pool; identify slow queries; consider
  read replica for idempotency lookup path.
- Bad deploy → error spike correlates with deploy time. Mitigation:
  roll back.
- RabbitMQ down → publishes fail; payments still succeed
  synchronously but settlement freshness degrades. Mitigation: restart
  broker; if cluster, fail over.

**Verify recovery**: SLO burn rate returns below 1×. Sample traces
through the dashboard. Write postmortem; add an alert or guardrail for
the failure class that slipped through.

## 4. PII and data governance

The service handles customer emails, payment amounts, and merchant
identifiers. The model: minimize PII in telemetry, never log it, scrub
on the way out as a defense in depth.

**Logs**: customer emails are never logged. Logged identifiers are
business-safe — `MerchantId` (a business identifier, not a personal one)
and `PaymentId` (a system identifier). Amounts are logged for
business-observability reasons but are not personal data in isolation.
Card data and CVV never enter the service — they go directly to providers,
which keeps us out of PCI scope by design.

**Traces**: span attributes follow the same rule. The
`db.statement` attribute (the SQL EF Core executed) can contain
parameter values. In production this would be scrubbed by a Collector
attributes-processor for tables with sensitive columns. Same for any
inbound webhook attributes.

**Metrics**: labels are bounded categorical values (status, method,
currency, breaker transition state). High-cardinality identifiers
(PaymentId, email, raw amount) are never used as labels — both for
privacy and to prevent cardinality explosion that would inflate cost
and make the metrics backend unstable.

**At rest and in transit**: TLS between services in production (within
the Docker network here, we use plaintext for simplicity — explicitly
called out as a dev choice). Encrypted database columns for sensitive
fields in production. Least-privilege DB roles. Tenant-scoped queries
prevent cross-tenant data leakage.

**Retention and right-to-erasure**: telemetry retention windows are
configured at the backend, not in the apps, so policy can change
centrally. Because PII does not land in logs, traces, or metrics,
erasing a customer is a Postgres operation — no telemetry purge
required.

## 5. Cost awareness at 1,000 payments/minute

At ~1.4M payments per day, naive "trace and log everything at full
fidelity" becomes uneconomic quickly. Levers, in order of impact:

**Tail-based sampling at the Collector.** Keep 100% of error traces and
slow traces (>P95 duration). Sample 5–10% of successful, fast traces.
The Collector's tail-sampling processor decides after seeing all spans
in a trace, so you never lose the interesting ones while drastically
reducing the boring majority. Configured in
`docker/otel-collector-config.yaml` in production.

**Metric cardinality discipline.** Already enforced by design — labels
are closed sets, never identifiers. The dominant cost driver in
metrics backends is unique time series. Adding a single high-cardinality
label can blow up the cost budget; that's why every metric we emit has
been audited for label bounds.

**Log levels.** INFO for lifecycle events and decisions; DEBUG
suppressed in production. Framework log noise (HttpClient request
start/end, EF Core SQL commands) is dampened to Warning by
configuration. Logs flow through the same OTLP pipeline as traces, so
the Collector can sample them too.

**Retention tiering.** Hot storage (queryable in real-time) for 7 days;
cold/aggregate for longer (90 days). Exemplar traces preserved at full
fidelity in a separate tier so historical incident debugging stays
possible.

**The Collector as a cost-control point.** Filter, batch, drop, route —
all centrally. The apps stay dumb and policy is tuneable without
redeploys. Adding a new backend or changing sampling becomes a Collector
config change.

The trade-off is observability cost vs. debuggability: keep enough
fidelity on errors and tails to debug incidents; sample the boring
majority to control spend.

## 6. Production extensions (out of scope here)

- **Outbox pattern.** Avoid the dual-write inconsistency between
  Postgres status update and RabbitMQ publish: write the event to an
  `outbox` table inside the same database transaction as the status
  update; a relay polls the outbox and publishes. Guarantees
  "persisted ⇔ will-be-published" without a distributed transaction.
- **Dead-letter queue + retry topology.** Failed consumer messages go
  to a DLQ instead of the current `requeue: false` drop. DLQ depth is a
  metric with an alert.
- **Migration runner job.** Migrations on startup is fine for one
  replica; for rolling deploys, a dedicated migration job in CI is the
  production answer. `dotnet ef migrations bundle` produces a runnable
  executable that can ship as a sidecar.
- **Kill switch.** A config flag, hot-reloadable via `IOptionsMonitor`,
  to disable payment processing without restart. Observable as a gauge.
- **Multi-tenancy.** `TenantId` propagated through every entity, event,
  and log line; per-tenant SLOs; per-tenant rate limits.
- **Real fraud and provider integrations.** The stubs are scope-limited
  but the integration boundaries (gRPC contract, HTTP webhook contract,
  resilience pipeline) would not change.
