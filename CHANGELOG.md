# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- RabbitMQ: a receive endpoint whose consumer waits for automatic connection recovery for 30 seconds or more is now reported as `Degraded` by the bus health check, with a description of how long it has waited and whether the connection is still down or open without the consumer; a `Warning` is logged once when the limit is reached and an `Information` entry when the consumer recovers after it. This covers a connection that never comes back and a consumer that the client cannot re-register (for example because its queue was deleted). The endpoint is not restarted automatically

### Fixed

- RabbitMQ: stopping a receive endpoint or disposing the transport could hang while a consumer channel or the connection was being closed, because the broker never answered the close handshake and the close had no time limit of its own (the endpoint runner released the channel with a token that can never be cancelled, and the client's own limits are 20 to 30 seconds and apply only partly). Closing a consumer channel, closing the connection and disposing either of them are now each limited to 5 seconds (6 seconds for the connection close token, so the client's own 5-second limit ends it first), whatever token the caller passes; a cancelled caller token still ends the close at once. A step that runs out of time is logged at `Warning` and the remaining resources are still released, and a failure while disposing the connection no longer escapes the transport's `DisposeAsync`. Worst-case time of the transport's `DisposeAsync` is now, per open consumer channel, up to 10 seconds, plus up to 11 seconds for the connection
- Kafka and in-memory transports: calling the transport adapter's `ConsumeAsync` directly with a `FullMode` other than `Wait` was accepted. On Kafka, a message rejected by a full internal buffer never reached the handler and held the partition commit at its offset, so the partition stopped committing until a restart or rebalance. **Behavior change:** both transports now throw `ArgumentOutOfRangeException` when `FullMode` is not `Wait` or `InternalQueueCapacity` is not positive, before any broker call (as RabbitMQ, Amazon SQS, Google Cloud Pub/Sub and Azure Service Bus already do); receive endpoints configured through the bus are unaffected.
- Receive endpoints without per-key ordering: on shutdown, `Ack`/`Nack`/`Reject` were issued with the already-cancelled endpoint token, so the broker SDK rejected the settlement and the message was redelivered after the lock or visibility timeout with a raised delivery count. They now settle with a token that is cancelled 5 seconds after stop (the same budget ordered lanes use); a settlement still running when the budget ends is logged at `Debug`. After any settlement the loop no longer pulls another message once stop is requested, and a failed settlement logs only the exception type. Worst-case shutdown time grows by up to 5 seconds
- Receive endpoints with per-key ordering: a lane whose worker failed (or whose durable park was cancelled) left the messages queued behind it unsettled, undisposed and holding their flow-control credit, and the reader could block on that full lane until the endpoint stopped. The lane now drains its queue: each message is requeued, its credit released and its buffer returned. While the endpoint is running each requeue waits 100 ms, so a failed lane slows the endpoint to about 10 messages per second instead of redelivering in a tight loop (a `Warning` is logged every 100 drained messages and when the drain ends); after stop the drain runs without delay within the 5-second settlement budget. Requeued messages may be redelivered out of order relative to the failed head message
- RabbitMQ: after the connection dropped and the client recovered it, the consumer could keep handing the runner messages it had buffered before the channel closed (the broker redelivers them, so handlers ran twice), and the endpoint stopped receiving once the buffer was drained. Buffered deliveries of a closed channel are now discarded without being settled, and when the client is going to recover the channel (automatic recovery enabled, connection lost, not closed by the application or refused with 403) the consumer keeps receiving from the recovered channel; otherwise the stream ends as before. A message already being handled when the channel closes may still be processed twice (at-least-once). If recovery never succeeds, or succeeds without re-registering the consumer (for example the queue was deleted), the endpoint keeps waiting for deliveries and is reported by the health check (see Added). Known limit: with a `NetworkRecoveryInterval` close to zero, a fresh delivery of the recovered channel can be dropped without settlement and is redelivered by the broker once the channel closes or the consumer times out.
- RabbitMQ: consumer channels are no longer leaked when consumer setup (`basic.qos` or `basic.consume`) fails, when the transport is disposed while a consumer is starting, or when a consumer stops before receiving any message.
- Azure Service Bus with sessions: when a session went idle, the session consumer released it immediately — it stopped forwarding, removed the session's in-flight entries and closed the session receiver — even though the handler could still be processing messages of that session. Their late `Ack`/`Nack` then failed with `BareWireTransportException`, the message lock expired and the message was redelivered with a raised delivery count; the released session could also be accepted by another consumer while older messages were still in the handler, breaking per-session ordering. An idle session now stops receiving and stays locked until every message it forwarded is settled, at most until its session lock ends (with auto-renewal: up to `MaxAutoLockRenewDuration` plus one lock period); a lost session lock, consumer stop or adapter disposal ends the wait earlier, and a session released with unsettled messages logs a warning. While it waits, the session keeps its `MaxConcurrentSessions` slot, so with the default of 1 a hung handler on an idle session blocks the endpoint until the session lock ends
- Amazon SQS, Google Cloud Pub/Sub and Azure Service Bus: received messages are no longer discarded when the consumer's internal buffer is full. SQS and Pub/Sub lost messages from later receives when both the endpoint `PrefetchCount` and the transport's `MaxInFlightMessages` exceeded 1000 (or, when `ConsumeAsync` was called directly, when `InternalQueueCapacity` was below both limits); they came back only after the visibility timeout or acknowledgement deadline with a raised delivery count. The internal buffer is now sized to hold at least `MaxInFlightMessages`, so its worst-case memory grows with the smaller of the endpoint and transport in-flight limits multiplied by the message body size. Azure Service Bus with sessions (`UseSessions` with two or more concurrent sessions): when several sessions waited for room in the shared buffer, the session that lost the race for a freed slot had its message discarded, and it was redelivered only after the session was released or its lock expired, out of order and with a raised delivery count; sessions now wait for room in turn. **Behavior change:** calling the transport adapter's `ConsumeAsync` directly on these transports with a `FullMode` other than `Wait`, or with a non-positive `MaxInFlightMessages` / `InternalQueueCapacity`, now throws `ArgumentOutOfRangeException` before any broker call (as RabbitMQ does); receive endpoints configured through the bus are unaffected
- Receive endpoints with per-key ordering: on shutdown, lanes settled their messages only after Azure Service Bus (with and without sessions), Kafka and in-memory consumers had already been unregistered and closed, so a late `Ack`/`Nack` was lost and the message was redelivered after the lock or visibility timeout with a raised delivery count; lanes also settled with the already-cancelled endpoint token, so the broker SDK rejected the settlement. Lanes now settle with a token that is cancelled 5 seconds after stop, and those consumers, after they stop receiving and release their buffered messages, wait up to 5 seconds until every message handed to the endpoint is settled (including settlements still in progress, such as a Kafka retry or dead-letter republish) before they unregister and close. Disposing the transport adapter skips this wait. The ordered path no longer pulls another message after stop is requested, and a lane fault is logged once. Worst-case consumer shutdown time grows by up to 5 seconds (about 20 seconds for Azure Service Bus sessions), so keep `HostOptions.ShutdownTimeout` at or above it. Amazon SQS, Google Cloud Pub/Sub, RabbitMQ and endpoints without per-key ordering are unchanged
- Amazon SQS and Google Cloud Pub/Sub: a full shared in-flight registry no longer causes messages to be received and then skipped until the visibility timeout or acknowledgement deadline expired (raising the delivery count towards the dead-letter queue). In-flight slots are now reserved atomically before each receive, a receive never asks the broker for more messages than there are free slots, each endpoint buffers at most `PrefetchCount` messages, each receive is capped at an equal share of the limit across the adapter's active endpoints (so one idle endpoint can no longer hold every slot during a long poll), and receiving pauses while no slot is free. A message that still cannot be registered is returned to the broker immediately in a single batch call (visibility timeout / acknowledgement deadline 0)
- RabbitMQ: settling a message whose `BW-ConsumerChannelId` header was missing or named a channel that had already been released fell back to the only active consumer channel, or to any open channel. A delivery tag is valid only on the channel that issued it, so this could acknowledge another consumer's message or close a healthy channel with `PRECONDITION_FAILED`. Settlement and durable parking now use only the channel named by the header; when it cannot be found, settlement logs a warning and throws `BareWireTransportException`, durable parking reports failure, and the message stays unacknowledged until the broker returns it when its channel closes. Manually constructed messages without that header can no longer be settled through the RabbitMQ transport
- RabbitMQ: a consumer whose prefetch count exceeded the adapter's internal queue capacity nack-requeued deliveries during normal operation, causing needless redeliveries and raising `x-delivery-count` on quorum queues. The internal buffer is now sized to hold at least a full prefetch window, so its worst-case memory now scales with the prefetch count (`PrefetchCount` multiplied by the message body size; `MaxInFlightBytes` does not bound it). Calling the transport adapter's `ConsumeAsync` directly with a `FullMode` other than `Wait`, or with a non-positive `MaxInFlightMessages` / `InternalQueueCapacity`, now throws `ArgumentOutOfRangeException` before a channel is opened; `MaxInFlightMessages = 0` previously produced an unlimited AMQP prefetch and is now rejected
- Consumer-level retry: a retry policy set on a consumer with `consumer.Retry(...)` (inline or in a `ConsumerDefinition<TConsumer>`) was stored but never applied at runtime, so the first exception dead-lettered the message. The policy is now built when the endpoint starts and wraps that consumer's invocation; deserialization failures are not retried by it
- SQLite: the transactional outbox/inbox middleware no longer relies on a `TransactionScope` that Microsoft.Data.Sqlite cannot enlist in. On SQLite each consume now runs in an explicit local transaction (deferred `BEGIN`), so the outbox messages and the inbox processed marker commit or roll back atomically and the `AmbientTransactionWarning` suppression is no longer needed. PostgreSQL and SQL Server keep using a `TransactionScope`
- Documentation: the getting-started guide, the transport articles and the package READMEs no longer show APIs that do not exist (`bus.AddConsumer`, `wire.AddConsumer`, `UseJsonSerializer`, `builder.AddBareWire(wire => ...)`). RabbitMQ examples register consumers via `ReceiveEndpoint(...).Consumer<TConsumer, TMessage>()` and declare a default exchange, the getting-started guide notes `amqps://` for production
- Kafka, Amazon SQS, Azure Service Bus and Google Cloud Pub/Sub: consumers can now be registered declaratively with `ReceiveEndpoint(name, e => e.Consumer<TConsumer, TMessage>())` on the transport configurator. Previously these transports registered no receive endpoints, so the bus never started a consume loop for them. Endpoint entities (topics, queues, subscriptions) are provisioned out of band. `IBus.ConnectReceiveEndpoint` is still not supported
- Amazon SQS and Google Cloud Pub/Sub: the in-flight registry is created once per adapter instead of lazily, so several receive endpoints on one adapter no longer race and leave messages unsettled (redelivered as duplicates). `MaxInFlightMessages` is shared by all endpoints of the adapter; sizing it to at least the sum of their `PrefetchCount` is a performance hint, not a correctness requirement
- Receive endpoints: an exception other than cancellation thrown after a message was read but before it reached its handler (credit wait, ordering-key resolution, lane write) left the message undisposed, unsettled and holding its flow-control credit. The message is now settled, its credit released and its buffer returned. Faults caused by the message itself (headers, ordering key) settle with `Nack` (dead-lettered when a dead-letter exchange is configured, otherwise logged as lost), record a warning and a failure metric (`error_type` `HandoffFault`), and the endpoint keeps consuming; it stops only after 10 such faults on different messages in a row (redeliveries of the same message are not counted), so a misclassified infrastructure failure cannot spin the loop. Infrastructure faults settle with `Requeue` and still stop the consume loop. What `Nack` does depends on the transport: RabbitMQ and in-memory reject the delivery, Azure Service Bus, SQS, Pub/Sub and Kafka redeliver it up to the broker limit, so configure a dead-letter policy on the broker (SQS redrive policy, Pub/Sub dead-letter topic, Service Bus max delivery count) to stop a poison message from being redelivered indefinitely.
- Amazon SQS, Google Cloud Pub/Sub and Azure Service Bus: stopping a consumer no longer leaves its buffered messages invisible until the visibility timeout, ack deadline or lock expiry. After cancellation the consumer hands no further buffered message to the endpoint, stops receiving, removes buffered messages from the in-flight registry, returns their buffers and releases them to the broker in batches (SQS `ChangeMessageVisibilityBatch`, Pub/Sub `ModifyAckDeadline(0)`, Service Bus `Abandon`) within a 5-second shutdown budget per consumer; messages received after cancellation are released the same way. On Service Bus a `Requeue`/`Nack` issued while the consumer stops waits until receiving has stopped, so the message is not redelivered to the stopping consumer, and session receivers stay open until in-flight messages are settled. Each message is redelivered at most once per shutdown. Shutdown and settlement logs no longer include exception messages, receipt handles, ack ids or lock tokens
- Amazon SQS: sending a batch threw `NullReferenceException` with AWSSDK.SQS 4.x, which returns `null` instead of empty response collections; batch sends, the receive loop and dead-letter topology deployment now handle `null` collections
- Amazon SQS: with a custom `ServiceUrl` the region is now used only for request signing (`AuthenticationRegion`) instead of also setting `RegionEndpoint`, which removes about 1.5 s of client-side overhead per call against LocalStack and other custom endpoints

### Changed

- **Behavior change:** the bus health description (`IBusControl.CheckHealth` and the `barewire` health check) mentions capacity only when flow control causes the worst status. When the worst status comes from the transport alone — a RabbitMQ consumer waiting for automatic recovery or an in-memory queue at or above 90% of capacity — the summary is now `One or more endpoints are degraded.` (previously `One or more endpoints are approaching capacity.`) or `One or more endpoints are unhealthy.` (previously `One or more endpoints are at capacity.`), followed by the transport's description as before. Health statuses are unchanged; update alerts or dashboards that match on the description text
- Bumped the transitive MessagePack pin to 2.5.307 to clear the NuGetAudit advisory GHSA-qhrr-8q5h-9q3h (NU1902), which failed `dotnet restore` under warnings-as-errors
- **Behavior change:** registering a second, different transport in one service collection (for example `AddBareWireRabbitMq` followed by `AddBareWireKafka`) now throws `BareWireConfigurationException`. Previously the second transport's adapter and receive endpoints were silently ignored. Register exactly one transport per service collection; support for multiple transports is planned.
- **Behavior change:** calling the same transport registration method (`AddBareWireRabbitMq`, `AddBareWireInMemory`, `AddBareWireKafka`, `AddBareWireSqs`, `AddBareWireAzureServiceBus` or `AddBareWirePubSub`) a second time on one service collection now throws `BareWireConfigurationException` before the second configuration delegate runs. This also applies to the bundle methods `AddBareWireWithRabbitMq`, `AddBareWireWithKafka`, `AddBareWireWithSqs`, `AddBareWireWithAzureServiceBus` and `AddBareWireWithPubSub`: calling one of them twice, or combining it with the transport method it wraps, now throws as well. Previously the second call's receive endpoints, topology and options — including its TLS settings and credentials — were silently ignored, and with RabbitMQ its exchange and routing-key mappings replaced those of the first call. Declare every receive endpoint, topology and mapping in a single call.
- Kafka: declaring a receive endpoint without `ConsumerGroup` now fails at configuration time with `BareWireConfigurationException` instead of in the consume loop
- **Behavior change:** retry settings now have upper bounds. A retry count above 100 or a single retry delay above 1 hour fails `StartAsync` with `BareWireConfigurationException` before the bus is marked started — for consumer policies (`Interval`, `Incremental`, `Exponential`, including the largest computed `Incremental` delay) and for endpoint `RetryCount`/`RetryInterval` (the interval is checked when `RetryCount` is greater than zero; negative values are rejected too). Previously such values were accepted and failed or overflowed only during delivery. Retry delays are computed without overflow and clamped before waiting, and the retry log reports the delay that is actually awaited
- **Behavior change:** consumers configured with `consumer.Retry(...)` are now actually retried. The consumer policy replaces the endpoint `RetryCount`/`RetryInterval` for that consumer (attempts are not multiplied); consumers without their own policy, raw consumers and sagas keep the endpoint retry. An empty retry configuration now fails `StartAsync` with `BareWireConfigurationException` before the bus is marked started. Item keys prefixed with `retry:` and `inbox:` in `MessageContext.Items` are reserved for the framework
- **Breaking for SQLite users:** a consumer `DbContext` that shares `IOutboxConnectionAccessor.Current` must call `Database.UseTransaction(accessor.CurrentTransaction)`. `IOutboxConnectionAccessor.CurrentTransaction` is new (default `null` on providers that use a `TransactionScope`). The transaction is owned by the middleware: consumers must not commit, roll back or dispose it, otherwise the middleware throws `InvalidOperationException`
- SQLite limitations documented: WAL read-then-write transactions can fail with `SQLITE_BUSY_SNAPSHOT` (start with a write), shared-cache SQLite is not supported for consume transactions, and suppressing `AmbientTransactionWarning` on a non-SQLite provider silently disables atomicity and is unsupported

## [2.0.4] - 2026-06-29

### Changed

- `IRequestClient<T>` now extends `IAsyncDisposable`, so request clients returned by `IBus.CreateRequestClientAsync` can be released with `await using`. Source-compatible for callers; breaking only for external implementations of `IRequestClient<T>`, which must now provide `DisposeAsync` (#41)

### Fixed

- Surfaced disposability on the `IRequestClient<T>` contract so callers get a compile-time signal to dispose request clients. The disposable implementation was previously `internal`-only, so undisposed clients left their exclusive auto-delete response queue (plus channel and consumer) on the broker until the shared connection closed on restart, causing temporary queues to accumulate (#41)

## [2.0.3] - 2026-06-28

### Added

- Single-commit connection sharing in the outbox — consumers reuse the outbox's pinned connection so processed-record writes commit atomically without two-phase commit (2PC)

### Fixed

- Provider-aware outbox `PerKey` guard, with drain-loop pacing and startup gating

### Changed

- `TransactionalOutbox` and `InboxDeduplication` samples now share the outbox connection and drop 2PC

### Documentation

- Documented the single-commit vs 2PC consumer pattern for the outbox

## [2.0.2] - 2026-06-28

### Fixed

- Persist the inbox `ProcessedAt` marker inside the business transaction, closing a crash window between commit and the separate marker write that allowed duplicate handler execution despite the exactly-once-processing guarantee (#36)
- Fail fast at startup when the EF Core outbox runs on a provider with no matching atomic SQL dialect, instead of silently degrading to a non-atomic claim/relock; added the `AllowNonAtomicProvider` opt-out for single-instance/testing (#37)

## [2.0.1] - 2026-06-28

### Added

- Opt-in guaranteed-routing for unroutable RabbitMQ publishes

### Fixed

- Atomic outbox inbox re-lock and a resilient publisher loop that survives failures

### Documentation

- Documented all transports and serializers, added a dedicated RabbitMQ transport page, and rewrote the saga guide
- Added per-type MassTransit producer routing to the MassTransit interop guide
- Dropped internal ADR references from public XML doc comments and fixed a deprecated example and the README
- Fixed article inaccuracies

## [2.0.0] - 2026-06-27

### Added

- Single-call transport bundle packages (`AddBareWireWithRabbitMq`/`Kafka`/`AzureServiceBus`/`AwsSqs`/`GooglePubSub`) for one-line registration
- Ergonomic per-type send mapping: generic `DeclareExchange<T>` and grouped `Publish<T>` block (`IPublishConfigurator<T>`)
- Publish-style request/response (`PublishRequest<T>`) with per-type fanout routing, fail-fast exchange-mapping validation, and first-in-wins reply handling
- Consume-time routing-key dispatch: multi-consumer routing, topic wildcards, most-specific-wins matching, and type-less raw-first interop via `AcceptUntyped()` (secure-by-default)
- Per-consumer MassTransit envelope opt-in (`UseMassTransitEnvelope()`) with per-consumer deserializer selection and reply-envelope emission from `RespondAsync`
- Per-key message ordering: `OrderedBy`/`OrderedByHeader` API, `ExchangeType.ConsistentHash`, RabbitMQ single-active-consumer affinity, bounded per-lane depth, and per-key poison handling (park/DLQ)
- Optional per-key head-of-line ordering for the EF Core outbox (`OrderingMode.PerKey`, default OFF)
- Zero-allocation `TopicPatternMatcher` with an internal `ITopicMatcher` seam
- Showcase samples (`ConsumerRoutingKeys`, `OrderedConsumers`, `CompetingResponders`, mixed-consumer MassTransit demo), each with E2E smoke tests

### Changed

- Transport registration: `Use{Transport}` methods are now `[Obsolete]` no-ops, superseded by the `AddBareWireWith{Transport}` bundles
- Outbox retry releases nacked rows immediately for low-latency retry
- Improved startup diagnostics: advisories for envelope opt-in with `AcceptUntyped`, type-less trust-boundary without schema validation, and divergent double-write detection
- Improved ordering-key secrecy: keys are kept out of exception text, logs, and metrics (hashed token)

### Deprecated

- `AddPartitionerMiddleware` is deprecated in favor of `OrderedByHeader`

### Fixed

- Duplicate delivery under horizontal scaling — `EfCoreOutboxStore` now claims rows per-instance (#24)

### Documentation

- Documented per-consumer MassTransit envelope, consume-time routing keys, ergonomic per-type send mapping, publish-style request/response, single-call registration, per-key ordering, and the README-per-package requirement

## [1.5.1] - 2026-06-23

### Fixed

- Added the missing `BareWire.Saga.Redis` package `README.md` that caused `dotnet pack` to fail with `NU5019` during the v1.5.0 release
- Added a ContractTests guard asserting every packable `src/` package ships a `README.md`, so a missing readme fails in CI before tagging

## [1.5.0] - 2026-06-23

### Added

- Redis SAGA persistence (`BareWire.Saga`): `RedisSagaRepository<T>` with Lua-script optimistic concurrency
- Redis connection configuration with TLS, mutual-TLS (client PFX), Sentinel, and Cluster support
- Roslyn analyzer that enforces `CancellationToken` propagation through async call chains
- Cross-transport header-mapping benchmark suite
- `BareWire.Samples.MassTransitRequestResponse` sample demonstrating BareWire → MassTransit request/response interop
- MassTransit v8.5.10 API and wire-format reference documentation

### Fixed

- BareWire → MassTransit request/response interop: MassTransit requesters now receive replies — completed the request/response envelope mapping and `RespondAsync` reply delivery (#18, #19, #22)
- Pinned `SQLitePCLRaw` to the patched 3.x line to resolve security advisory GHSA-2m69-gcr7-jv3q

### Changed

- Translated remaining Polish code comments and analyzer messages to English

## [1.4.0] - 2026-06-18

### Added

- Azure Service Bus transport (`BareWire.Transport.AzureServiceBus`): `AzureServiceBusTransportAdapter` with native sessions (per-session FIFO, lock auto-renewal), scheduled messages (schedule + cancel), and Entra ID + SAS authentication — tasks R2.1–R2.5
- MessagePack serialization (`BareWire.Serialization.MsgPack`): `MessagePackSerializer` with zero-copy pipeline and Content-Type deserializer routing — tasks R3.1–R3.3
- AWS SQS transport (`BareWire.Transport.AWS.SQS`): `SqsTransportAdapter` with batch producer and long-polling consumer, FIFO queues (MessageGroupId + deduplication), IAM instance-profile auth, SSE encryption at rest, and RedrivePolicy DLQ — tasks R4.1–R4.4
- Google Pub/Sub transport (`BareWire.Transport.Google.PubSub`): Pub/Sub transport adapter with ordering keys and dead-letter topics — tasks R5.1–R5.4

### Fixed

- RabbitMQ request clients now honour per-type serializer/exchange configuration (#13)

## [1.3.1] - 2026-06-16

### Fixed

- Added the missing `BareWire.Transport.Kafka` package `README.md` that caused `dotnet pack` to fail with `NU5019` during the v1.3.0 release

## [1.3.0] - 2026-06-16

### Added

- Kafka transport (`BareWire.Transport.Kafka`): `KafkaTransportAdapter` with idempotent producer, consumer-group consume with partition assignment, retry-topic and DLQ-topic pattern, and `KafkaTopologyConfigurator` — tasks R1.1–R1.4
- CloudEvents support (`BareWire.CloudEvents`): dual-mode (binary + structured) CloudEvents 1.0.2 envelope (de)serialization with zero per-message allocation in binary mode, fail-fast mandatory-attribute validation, Content-Type routing, and DI activation helpers (`AddCloudEvents` / `AddCloudEventsEnvelope`) — tasks 13.1–13.16
- `BareWire.Samples.CloudEventsInterop` sample demonstrating binary, structured, and raw read-side interop

### Fixed

- RabbitMQ integration tests now publish via an explicit `BW-Exchange=""` header — task B1
- Hardened structured CloudEvents envelope deserialization against untrusted input (SEC-1)

### Changed

- Bumped OpenTelemetry to 1.16.0
- CI skips GitHub Release creation when a release already exists, and cleared pre-existing NuGetAudit advisories (NU1902 OpenTelemetry, NU1903 MessagePack)

## [1.2.7] - 2026-04-10

### Added

- `IQueueConfigurator` fluent API for queue arguments (e.g., TTL, max-length, dead-letter exchange) — task 3.14

### Fixed

- Race condition in E2E019/E2E020 tests and flaky timeout in E2E004

### Changed

- Mobile-responsive styles for hero, features, concepts, and stats sections on docs landing page

## [1.2.6] - 2026-04-09

### Added

- `MapExchange<T>` for type-to-exchange routing — task 3.13

## [1.2.5] - 2026-04-08

### Changed

- Package project URLs point to docs site (barewire.wizardsoftware.pl)
- GitHub Packages push owner fixed to Wizard-Software

### Added

- Hero landing page, Wizard-Software branding, and sidebar nav for DocFX site
- DocFX site bootstrap with GitHub Pages deployment

## [1.2.4] - 2026-04-07

### Added

- Publish-side serializer override for MassTransit publish-only bridge

### Fixed

- Hash non-Guid CorrelationId in PartitionerMiddleware for consistent partitioning

### Changed

- Automatic GitHub Release with release notes on tag push (CI)
- MIT license added

## [1.2.3] - 2026-04-06

### Added

- MassTransit envelope serializer with per-endpoint activation
- User documentation for inbox, custom serializers, and MassTransit interop
- MassTransit interop package with envelope deserialization and E2E tests
- NuGet package metadata, per-package README, and icon

### Fixed

- Stabilized E2E-008 RetryAndDlq flaky test (task 10.21)
- Release workflow runs only unit and contract tests

### Changed

- Enhanced unit tests for service collection extensions and pipeline components

[1.5.1]: https://github.com/Wizard-Software/BareWire/compare/v1.5.0...v1.5.1
[1.5.0]: https://github.com/Wizard-Software/BareWire/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/Wizard-Software/BareWire/compare/v1.3.1...v1.4.0
[1.3.1]: https://github.com/Wizard-Software/BareWire/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/Wizard-Software/BareWire/compare/v1.2.7...v1.3.0
[1.2.7]: https://github.com/Wizard-Software/BareWire/compare/v1.2.6...v1.2.7
[1.2.6]: https://github.com/Wizard-Software/BareWire/compare/v1.2.5...v1.2.6
[1.2.5]: https://github.com/Wizard-Software/BareWire/compare/v1.2.4...v1.2.5
[1.2.4]: https://github.com/Wizard-Software/BareWire/compare/v1.2.3...v1.2.4
[1.2.3]: https://github.com/Wizard-Software/BareWire/releases/tag/v1.2.3
