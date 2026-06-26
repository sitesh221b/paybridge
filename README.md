# PayBridge — Observable Payment Processing Service

A payment processing service demonstrating production-grade observability and
resilience patterns across a distributed, multi-service .NET architecture.

> **Take-home scope note.** This is a focused submission. I built two
> fully-instrumented services (Payment API, Fraud Detection) with a complete
> observability and correctness story end-to-end, rather than a wider but
> thinner pipeline. The provider service, webhook callback, message queue,
> and settlement consumer are described as production extensions in the
> [design document](#design-decisions--trade-offs), with the path I'd take
> to implement them.

## Quick start

Requirements: Docker Desktop with ≥6 GB RAM allocated.

```bash
git clone <repo-url>
cd paybridge
docker compose up -d --build
```

Wait ~30 seconds for the stack to come up, then:

- **Send a payment** — open `requests.http` in VS Code (with REST Client) or
  curl: `curl -X POST http://localhost:5001/api/payments -H "Content-Type: application/json" -d @sample-payment.json`
- **See the trace** — open the Aspire Dashboard at <http://localhost:18888>,
  click Traces

## What you'll see

| Demo                         | Where                                           | Caption                                                |
| ---------------------------- | ----------------------------------------------- | ------------------------------------------------------ |
| End-to-end distributed trace | Dashboard → Traces                              | Single trace across both services, Postgres, and Redis |
| Idempotency                  | Send the same `idempotencyKey` twice            | Second response is fast (<10ms), no re-processing      |
| Custom business metrics      | Dashboard → Metrics → `payment-api`             | `paybridge.payments.*`, `paybridge.fraud.*`            |
| Health checks                | `GET /health/live` and `/health/ready`          | Critical/non-critical distinction                      |
| Circuit breaker              | `docker compose stop fraud-stub`, send payments | Slow failures → instant fail after threshold           |

[Screenshot: trace waterfall]
[Screenshot: metrics dashboard]
[Screenshot: circuit breaker logs]

## Architecture

[Architecture diagram — link to docs/architecture.png]

Two services run in containers and communicate over the network:

- **Payment API** — REST endpoint, orchestrates the payment lifecycle, owns
  correctness (validation, idempotency, status transitions).
- **Fraud Stub** — second service simulating a fraud-detection downstream;
  returns randomized approve/reject so traces are real but logic is canned.

Infrastructure: PostgreSQL (system of record), Redis (idempotency fast path),
OpenTelemetry → Aspire Dashboard (traces + metrics + logs in one UI).

## Tech stack & key decisions

- **.NET 10 LTS** (Nov 2025) — current long-term support.
- **OpenTelemetry .NET SDK** with auto-instrumentation for ASP.NET Core,
  HttpClient, EF Core, Redis — minimal manual span code, maximum coverage.
- **Aspire Dashboard (standalone)** — chosen over Jaeger + Prometheus + Grafana
  for time-to-value: one container, three pillars in one UI. Dev-grade
  (in-memory storage); production would route the Collector to durable
  backends (Tempo / Mimir / Loki, or a managed offering).
- **Postgres** chosen over SQL Server — first-class OTel/Npgsql
  instrumentation, native ARM64 image runs on Apple Silicon without emulation.
- **`Microsoft.Extensions.Http.Resilience`** (Polly v8) — HTTP-aware
  retry/timeout/circuit breaker, integrated cleanly with `IHttpClientFactory`.
- **HTTP for fraud (not gRPC)** — deliberate scope trade-off. The observability
  story (traces, propagation, instrumentation) is the same; gRPC would add
  proto-file setup without changing the demo. Production answer: switch to
  gRPC for strongly-typed contracts on internal RPCs.

See [`docs/design.md`](docs/design.md) for SLOs, runbook, PII, cost.

## Repository layout

paybridge/

├─ src/

│ ├─ PayBridge.PaymentApi/ # Real business logic

│ ├─ PayBridge.FraudStub/ # Stub service

│ └─ PayBridge.Common/ # Shared DTOs

├─ tests/

│ └─ PayBridge.PaymentApi.Tests/

├─ docs/

│ ├─ design.md

│ └─ architecture.png

├─ docker-compose.yml

├─ requests.http # Sample requests for all paths

└─ README.md

## Testing

```bash
dotnet test
```

Unit tests cover idempotency logic and status transitions. An integration test
boots Postgres + Redis with Testcontainers, posts a payment, and asserts a row
is persisted. Tests favor the high-leverage paths (correctness, dedupe race)
over coverage breadth, justified given the time budget.

## Design decisions & trade-offs

| Decision                               | Why                                           | What I'd do with more time                                         |
| -------------------------------------- | --------------------------------------------- | ------------------------------------------------------------------ |
| Two services instead of full pipeline  | Depth over breadth on the observability story | Add provider + webhook + Kafka + consumer per the original brief   |
| HTTP for fraud, not gRPC               | Same instrumentation story, faster to build   | gRPC for the typed contract on internal RPCs                       |
| Aspire Dashboard                       | One container, three pillars                  | Jaeger + Tempo + Grafana + Loki + Mimir for durable backends       |
| Migrations on app startup              | Single-replica simplicity                     | Migration runner job or `dotnet ef migrations bundle` sidecar      |
| Inline JSON config                     | YAGNI; only two services                      | Extract `AddPayBridgeObservability(name)` extension at 3+ services |
| Webhook trace relinking via span links | Out of scope this round                       | Stored TraceId on payment + `Activity.AddLink` on webhook entry    |

## Notes on AI tool usage

I used Claude throughout, but as a directed accelerator rather than an
autopilot:

- **Speed**: scaffolding (Dockerfiles, docker-compose skeleton, OTel setup
  boilerplate), README structure, sample request files.
- **Pressure-testing the hard decisions**: span links vs parent-child for
  webhook relinking (descoped), outbox vs dual-write, fail-open vs fail-closed
  fraud policy, where to draw the critical/non-critical line in health checks.
- **What I did not delegate**: architecture choices, idempotency design,
  resilience boundaries, SLO definitions. Every code path I include I can
  defend; every snippet was verified against current OTel/.NET docs (the
  OpenTelemetry packages had a breaking change in 1.13 that the model
  initially missed, which I caught at build time).

Net effect: AI compressed the mechanical work so my limited evenings went
into the observability and correctness story the assignment is actually
testing.
