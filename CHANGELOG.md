# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.5.0]: https://github.com/Wizard-Software/BareWire/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/Wizard-Software/BareWire/compare/v1.3.1...v1.4.0
[1.3.1]: https://github.com/Wizard-Software/BareWire/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/Wizard-Software/BareWire/compare/v1.2.7...v1.3.0
[1.2.7]: https://github.com/Wizard-Software/BareWire/compare/v1.2.6...v1.2.7
[1.2.6]: https://github.com/Wizard-Software/BareWire/compare/v1.2.5...v1.2.6
[1.2.5]: https://github.com/Wizard-Software/BareWire/compare/v1.2.4...v1.2.5
[1.2.4]: https://github.com/Wizard-Software/BareWire/compare/v1.2.3...v1.2.4
[1.2.3]: https://github.com/Wizard-Software/BareWire/releases/tag/v1.2.3
