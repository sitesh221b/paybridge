# PayBridge — Design Document

## 1. Architecture

PayBridge is a payment orchestrator split across four .NET services that
talk to each other over the protocol that makes sense for each boundary —
REST for the merchant API, gRPC for the internal fraud RPC, HTTP for the
provider, HTTP webhook for the async callback, AMQP for the settlement
event, and SQL for persistence.

The Payment API owns correctness — validation, idempotency, status
transitions, the integrity guarantees that stop double-charging. The
Settlement Consumer owns durable settlement and is idempotent against
duplicate delivery so RabbitMQ's at-least-once semantics don't corrupt
state. Fraud and Provider are stubs but run as real containers because the
assignment is about the integration boundaries, not the business logic
behind them.

All services emit OTLP to an OpenTelemetry Collector, which forwards to the
Aspire Dashboard. The Collector isn't strictly needed for this demo — apps
could export to the Dashboard directly — but having it in the path matches
the production shape, where sampling and scrubbing live in one configurable
layer instead of scattered through service code.

See the README diagrams for the request lifecycle and the observability layer.

## 2. SLOs

Three SLOs, covering availability, latency, and downstream freshness.

**Payment success rate.** `successful_payments / valid_payment_requests`,
excluding client 4xx. Target 99.9% over 30 days. Alert on a multi-window
burn rate — page when both 1-hour and 6-hour burn rates exceed 14.4× (fast
burn), ticket on 6-hour alone (slow burn). Two windows filters out
single-request anomalies while catching real drops fast.

**End-to-end latency.** P99 of `paybridge.payments.duration`. Target P99
under 2s, P50 under 300ms. Page when P99 exceeds 2s for 10 minutes
straight. Histograms not averages because the tail is what users feel.

**Settlement freshness.** Time between the `PaymentCompleted` publish and
the settlement row landing in Postgres. Target 99% within 60 seconds.
Page when consumer lag exceeds threshold for 5 minutes, or if the consumer
process dies.

## 3. Incident runbook — "payment success rate dropped"

Alert fires. First five minutes:

1. Filter `paybridge.payments.total` by `status`, `method`, `currency`,
   `merchant`. Is the drop scoped to one dimension or global?
2. Check `paybridge.resilience.breaker_events`. Is the fraud breaker open?
   That's the most common cause.
3. Open a failing trace from the dashboard. The errored span names the
   bad dependency.
4. Filter Logs for `Level=Error` and check for recent deploys.
5. `docker compose ps` — Postgres or RabbitMQ degraded?

Common causes, ranked by what I'd actually expect:

- **Fraud service degraded.** Breaker opens, payments fail with
  `fraud_service_unavailable`. Our fail-closed posture is intentional;
  flipping it to fail-open is a risk-team call, not an engineering one.
- **Provider broken.** The failing trace shows the outbound HTTP call
  failing. Reroute via merchant config if there's an alternate provider.
- **Postgres saturation.** DB spans show high latency. Scale the pool,
  find the slow query via `db.statement`, consider a read replica for
  idempotency lookups.
- **Bad deploy.** Error spike lines up with the deploy timestamp on the
  dashboard — roll back.
- **RabbitMQ down.** Payments still succeed synchronously, but settlement
  freshness degrades. Restart the broker or fail over the cluster.

Verify recovery by watching the burn rate come back below 1×, then write
the postmortem and add a guardrail for whatever slipped through.

## 4. PII and data governance

Approach: minimize PII in telemetry, never log it, scrub on the way out.

Logs and trace attributes carry `MerchantId` and `PaymentId` — system
identifiers, not personal data. Customer emails are never logged. Card
data and CVV don't enter the service at all by design — providers handle
them, which keeps us out of PCI scope. Metric labels are bounded
categorical values; high-cardinality identifiers go on traces and logs
where they can be queried, not on metrics where they'd blow up cost.

In production: TLS between services (plaintext here for the demo, called
out explicitly), encrypted columns for sensitive fields, least-privilege
DB roles, tenant-scoped queries. Telemetry retention is configured at the
backend so policy can change centrally. Because PII doesn't land in
telemetry, GDPR-style erasure is a Postgres operation only — no telemetry
purge required, which was a deliberate consequence of the
"don't log it" choice.

## 5. Cost at 1,000 payments/minute

About 1.4M payments a day. "Trace and log everything at full fidelity" gets
expensive fast. The levers, in rough order of impact:

**Tail-based sampling at the Collector** is the big one. Keep 100% of error
traces and slow traces (above P95). Sample 5–10% of successful, fast
traces. The Collector decides after seeing the whole trace, so I never
drop the interesting ones.

**Metric cardinality discipline** is the silent killer. The dominant cost
driver in metrics backends is the number of unique time series, not data
volume. Adding a single high-cardinality label can multiply cost by orders
of magnitude — which is why every metric I emit has been audited for
bounded labels.

**Log volume** comes down to log levels and framework noise. INFO for
lifecycle and decisions, DEBUG off in production, framework chatter
(HttpClient, EF Core) dampened to Warning. Logs flow through the same OTLP
pipeline as traces, so the Collector can sample them too.

**Retention tiering** — hot/queryable for 7 days, cold/aggregate for 90,
exemplars preserved at full fidelity for historical debugging.

The honest trade-off is observability cost vs. debuggability: full fidelity
on errors and tails to debug incidents fast, sample the rest to control
spend. The Collector is where you adjust this knob without redeploying apps.
