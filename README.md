# PayBridge — Observable Payment Processing Service

A multi-service payment orchestrator built to demonstrate production-grade
observability, distributed tracing across six protocol boundaries, and
race-proof correctness under async failure modes.

This is a take-home for an SDE2 .NET role. The scope deliberately favors
depth on the observability and correctness story over breadth.

---

## Quick start

**Requirements:** Docker Desktop with ≥6 GB RAM allocated.

```bash
git clone <repo-url>
cd paybridge
docker compose up -d --build
```

First build takes ~3 minutes. Then:

- **Send a test payment**: open [`requests.http`](./requests.http) in VS Code with the REST Client extension and click "Send Request" above the first block. Or `curl`:

```bash
  curl -X POST http://localhost:5001/api/payments \
    -H "Content-Type: application/json" \
    -d '{"merchantId":"acme","idempotencyKey":"k1","amount":49.99,"currency":"USD","customerEmail":"[email protected]","method":"CreditCard","metadata":null}'
```

- **See the trace**: open [http://localhost:18888](http://localhost:18888), click "Traces"
- **See the queue**: open [http://localhost:15672](http://localhost:15672) (guest / guest)
- **Query persisted state**:

```bash
  docker exec -it paybridge-postgres psql -U paybridge -d paybridge \
    -c "select * from payments order by created_at desc;"
```

---

## Architecture

![Payment request lifecycle](docs/architecture-flow.png)

The diagram shows two distinct phases:

1. **Synchronous request path** (top of diagram) — Payment API validates the request,
   checks Redis + Postgres for idempotency, calls Fraud over gRPC, persists, and
   submits to the Provider over HTTP. All in one trace.
2. **Async settlement path** (below the dashed boundary) — Provider fires a webhook
   callback as a fresh HTTP request with no trace context. The webhook handler
   creates a span with a **span link** back to the original payment trace,
   publishes a `PaymentEvent` to RabbitMQ with **traceparent manually injected
   into message headers**, and the Settlement Consumer extracts it on the other
   side. The consumer's span is a child of the publisher's span across the queue.

![Observability layer](docs/architecture-observability.png)

All four .NET services export OTLP telemetry to an OpenTelemetry Collector,
which forwards to the Aspire Dashboard. Telemetry policy (sampling, PII scrubbing,
multi-backend routing) lives in the Collector configuration, not in the app code.

---

## What you'll see in the dashboard

| Demo                                  | Where                                                                   | Caption                                                                                    |
| ------------------------------------- | ----------------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| End-to-end synchronous trace          | Traces → first row                                                      | One trace, three services (payment-api, fraud-stub, provider-stub), Postgres + Redis spans |
| Webhook trace relinked via span links | Traces → second row → click `webhook.process_provider_callback` → Links | The webhook is a separate trace, linked back to its originating payment                    |
| Trace propagation across the queue    | Webhook trace waterfall → settlement-consumer span                      | Consumer span is a child of publisher span — manual traceparent inject/extract             |
| Custom business metrics               | Metrics → payment-api → `paybridge.payments.*`                          | Counter, histogram, in-flight gauge with cardinality-bounded labels                        |
| Log/trace correlation                 | Logs → any row → TraceId column                                         | Every log line carries the trace id; click to pivot to the trace                           |
| Circuit breaker                       | Stop fraud-stub, send 6 payments                                        | Polly breaker opens, subsequent failures are instant; emits a metric event                 |
| Idempotency                           | Send the same idempotencyKey twice                                      | Second response in <10ms, no re-processing                                                 |

Screenshots in [`docs/screenshots/`](docs/screenshots/).

---

## Tech stack and key decisions

- **.NET 10 LTS** (Nov 2025) — current long-term support
- **OpenTelemetry .NET SDK** with auto-instrumentation for ASP.NET Core,
  HttpClient, gRPC, EF Core, Redis — auto-coverage on the easy boundaries,
  manual propagation for the hard ones (webhook span links, queue headers)
- **OpenTelemetry Collector (contrib distribution)** as the central telemetry
  pipeline — apps stay dumb, policy lives in `docker/otel-collector-config.yaml`
- **Aspire Dashboard** as the development backend — three pillars in one UI
  with zero config. In production this would be Tempo/Mimir/Loki or a managed
  offering reached via Collector exporters
- **PostgreSQL** as the system of record; **Redis** for idempotency fast path;
  **RabbitMQ** for event publishing
- **Microsoft.Extensions.Http.Resilience** (Polly v8) — retry with jittered
  backoff, per-attempt timeout, observable circuit breaker
- **Testcontainers** for integration tests against real Postgres + Redis

### Important decisions and their trade-offs

| Decision                                                                                    | Why                                                                                                                                                                                                  | Production answer                                                                                                |
| ------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| HTTP for fraud is gRPC — yes, the assignment-prescribed protocol                            | Strongly-typed contract via protobuf, lower latency on internal RPC                                                                                                                                  | Same                                                                                                             |
| Webhook trace relinking via `Activity.AddLink`                                              | Webhook is causally related but on a different "thread of execution" — span links express that more honestly than forcing it into a parent-child relationship in the same trace                      | Same                                                                                                             |
| Manual trace propagation across RabbitMQ headers via `Propagators.DefaultTextMapPropagator` | Made the inject/extract path explicit rather than hiding it behind a library like MassTransit, both for transparency and to demonstrate understanding                                                | Use a high-level library (MassTransit, Wolverine) that wraps the same pattern                                    |
| Idempotency at three layers: Redis fast path, DB unique constraint, consumer-side dedup     | Each layer has a different failure mode — cache loss is OK, DB constraint is durable truth, consumer dedup handles at-least-once delivery                                                            | Same                                                                                                             |
| Outbox pattern not implemented; webhook publishes directly                                  | Dual-write risk acknowledged — see design doc                                                                                                                                                        | Outbox table written in same DB transaction as the status update; relay polls and publishes                      |
| Database migrations applied at app startup                                                  | Single-replica simplicity for the take-home                                                                                                                                                          | Migration job in CI, or `dotnet ef migrations bundle` sidecar                                                    |
| `Activity.Current = null` in Provider Stub's background task                                | Provider's `Task.Run` would otherwise inherit the inbound request's trace context via `AsyncLocal<T>`. Clearing it accurately simulates a real provider calling our webhook from a different process | N/A — real providers don't have access to our trace context                                                      |
| Aspire Dashboard (in-memory, dev-only)                                                      | One container, zero config, three pillars in one UI                                                                                                                                                  | Collector exports to durable backends (Tempo for traces, Mimir for metrics, Loki for logs, or Honeycomb/Datadog) |
| Apple Silicon platform pinning (`linux/amd64`) on .NET service containers                   | Grpc.Tools 2.81 arm64 protoc segfaults in Docker — Rosetta emulation works around it cleanly                                                                                                         | Build on amd64 CI runners natively, or pre-generate proto code outside Docker                                    |
| Two stubs (Fraud, Provider) rather than full implementations                                | The assignment explicitly allows stubs; the observability story is identical regardless of business logic depth                                                                                      | Real fraud engine, real provider integration                                                                     |

---

## Resilience and correctness story

- **Polly v8 resilience pipeline** on the fraud gRPC client: per-attempt timeout
  (2s), retry with exponential backoff + jitter (3 attempts), circuit breaker
  (opens at 50% failure rate over a 10s window, 15s break duration)
- **Circuit breaker is observable**: `paybridge.resilience.breaker_events`
  counter increments on every state transition; a domain warning log fires on
  `OnOpened`
- **Idempotency** is the precondition that makes retries safe: Redis fast path
  for sub-ms duplicate detection, Postgres unique constraint as durable truth,
  Settlement Consumer dedupes on `PaymentId` for at-least-once delivery
- **Webhook handler is idempotent**: duplicate provider callbacks no-op rather
  than re-processing

### Resilience demo

To see the breaker open in real time:

```bash
docker compose stop fraud-stub

# Send 6 payments rapidly — keys must be different to avoid dedup short-circuit
for i in $(seq 1 6); do
  curl -X POST http://localhost:5001/api/payments \
    -H "Content-Type: application/json" \
    -d "{\"merchantId\":\"acme\",\"idempotencyKey\":\"brk-$i\",\"amount\":1,\"currency\":\"USD\",\"customerEmail\":\"[email protected]\",\"method\":\"CreditCard\",\"metadata\":null}"
  echo
done
```

First 4–5 fail slowly (~6s each, three retries with backoff). The breaker
trips, subsequent requests fail in <100ms. The dashboard's Logs tab will
show the "Fraud circuit breaker OPENED" warning. Restart the fraud stub,
wait 20s, and the breaker closes.

---

## Health checks

| Endpoint            | Purpose                            | Behavior                                                                                                                                                                                |
| ------------------- | ---------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GET /health/live`  | Liveness — "is the process alive?" | Always 200 if the process responds. No external dependency checks — a transient DB blip should not cause a restart loop                                                                 |
| `GET /health/ready` | Readiness — "can I serve traffic?" | 200 if all critical dependencies (Postgres, RabbitMQ) are healthy. 200 with `Degraded` if non-critical dependencies fail (Redis falls back to DB). 503 if a critical dependency is down |

Try it: `docker compose stop redis` → `/health/ready` returns 200 with `Degraded`. `docker compose stop postgres` → 503 Unhealthy.

---

## Project layout

paybridge/

├── src/

│ ├── PayBridge.PaymentApi/ # Real REST orchestrator

│ ├── PayBridge.FraudStub/ # gRPC stub service

│ ├── PayBridge.ProviderStub/ # HTTP stub + async webhook

│ ├── PayBridge.SettlementConsumer/ # Background worker

│ └── PayBridge.Common/ # Shared DTOs + proto file

├── tests/

│ └── PayBridge.PaymentApi.Tests/ # Integration tests with Testcontainers

├── docs/

│ ├── design.md # SLOs, runbook, PII, cost

│ ├── architecture-flow.png

│ ├── architecture-observability.png

│ └── screenshots/

├── docker/

│ └── otel-collector-config.yaml

├── docker-compose.yml

├── requests.http

└── README.md

---

## Testing

```bash
dotnet test
```

Integration tests boot Postgres + Redis containers via Testcontainers and
exercise the payment pipeline against real infrastructure. Coverage is
deliberately focused on the highest-leverage paths — happy-path persistence
and idempotency under duplicate-key races. Broader coverage (status
transitions, fraud rejection path, queue propagation contract tests) is the
next investment.

---

## Design document

See [`docs/design.md`](docs/design.md) for:

1. Architecture overview
2. Three SLOs (availability, latency, consumer freshness) with alerting strategy
3. Incident runbook for "payment success rate dropped"
4. PII and data governance
5. Cost awareness at 1,000 payments/minute

---

## Notes on AI tool usage

I used Claude (Anthropic) throughout the build — as a directed accelerator,
not an autopilot. Specifically:

**Speed work I delegated:**

- Initial scaffolding (Dockerfiles, docker-compose skeleton, OTel
  registration boilerplate, sample request files, README structure)
- Translating the assignment's proto schema into wired-up code
- The standard Polly v8 resilience pipeline shape
- Debugging support when I hit specific errors (a Grpc.Tools arm64
  segfault, an `Activity.StartActivity` overload-resolution ambiguity, an
  EF Core casing issue in Postgres)

**What I directed and verified, not delegated:**

- Architecture decisions — which services to fully build vs. stub, where to
  spend depth vs. breadth
- The trace-relinking strategy (span links vs. parent-child)
- The decision to detach trace context in the provider stub's background
  task with `Activity.Current = null` to accurately simulate a real provider
  webhook
- Idempotency design at three layers (Redis fast path, DB unique constraint,
  consumer dedup)
- Health check critical-vs-non-critical distinctions
- SLO definitions and alerting strategy
- PII handling model

**What broke and how I caught it:** the model initially missed that
OpenTelemetry's `EntityFrameworkInstrumentationOptions.SetDbStatementForText`
was removed in 1.13 — I caught it at build time. The model initially suggested
a 1.15 Aspire Dashboard image tag that didn't exist — I caught it from the
pull error. These are exactly the kinds of staleness issues you trade off for
the speed AI gives you, and you watch for at every step.

Net effect: AI compressed maybe a day of mechanical work so my time went
into the observability and correctness story the assignment is actually
testing. Every code path I include I can explain and defend.
