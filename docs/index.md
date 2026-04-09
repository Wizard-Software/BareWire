# BareWire

<div class="hero">
  <h1 class="display-2 fw-bold">BareWire</h1>
  <p class="lead">
    A high-performance async messaging library for <strong>.NET 10 / C# 14</strong>.
    Raw-first. Zero-copy. Deterministic. A MassTransit alternative that gets out of your way.
  </p>
  <p class="hero-cta">
    <a class="btn btn-primary btn-lg me-2" href="articles/getting-started.md">Get Started</a>
    <a class="btn btn-outline-primary btn-lg" href="api/index.md">API Reference</a>
  </p>
</div>

## Why BareWire

<div class="row feature-row">
  <div class="col-12 col-md-4">
    <div class="feature-card">
      <h3>Raw-first</h3>
      <p>Default serializer produces raw JSON — no envelope, no wrapping. The envelope is opt-in, not the other way around. Your wire format is your wire format.</p>
    </div>
  </div>
  <div class="col-12 col-md-4">
    <div class="feature-card">
      <h3>Zero-copy pipeline</h3>
      <p><code>IBufferWriter&lt;byte&gt;</code> and <code>ReadOnlySequence&lt;byte&gt;</code> with <code>ArrayPool</code> throughout. No <code>new byte[]</code> in the hot path. Deterministic memory usage under load.</p>
    </div>
  </div>
  <div class="col-12 col-md-4">
    <div class="feature-card">
      <h3>Manual topology</h3>
      <p>No auto-topology magic. You declare exchanges, queues, and bindings — or turn on opt-in auto-configuration. Predictable deployments, no surprises in production.</p>
    </div>
  </div>
</div>

## Core concepts

<div class="row concept-row">
  <div class="col-12 col-md-6 col-lg-4">
    <a class="concept-card" href="articles/getting-started.md">
      <h3>Getting Started</h3>
      <p>Install the package and publish your first message.</p>
    </a>
  </div>
  <div class="col-12 col-md-6 col-lg-4">
    <a class="concept-card" href="articles/publishing-and-consuming.md">
      <h3>Publishing &amp; Consuming</h3>
      <p>Producers, consumers, request/response.</p>
    </a>
  </div>
  <div class="col-12 col-md-6 col-lg-4">
    <a class="concept-card" href="articles/configuration.md">
      <h3>Configuration</h3>
      <p>Fluent API, options, dependency injection.</p>
    </a>
  </div>
  <div class="col-12 col-md-6 col-lg-4">
    <a class="concept-card" href="articles/topology.md">
      <h3>Topology</h3>
      <p>Exchanges, queues, bindings — manual or auto.</p>
    </a>
  </div>
  <div class="col-12 col-md-6 col-lg-4">
    <a class="concept-card" href="articles/flow-control.md">
      <h3>Flow Control</h3>
      <p>Credit-based backpressure, bounded channels.</p>
    </a>
  </div>
  <div class="col-12 col-md-6 col-lg-4">
    <a class="concept-card" href="articles/saga.md">
      <h3>Saga &amp; Outbox</h3>
      <p>Stateful workflows and transactional delivery.</p>
    </a>
  </div>
  <div class="col-12 col-md-6 col-lg-4">
    <a class="concept-card" href="articles/observability.md">
      <h3>Observability</h3>
      <p>OpenTelemetry, metrics, tracing.</p>
    </a>
  </div>
  <div class="col-12 col-md-6 col-lg-4">
    <a class="concept-card" href="articles/masstransit-interop.md">
      <h3>MassTransit Interop</h3>
      <p>Bridge to existing MassTransit services.</p>
    </a>
  </div>
</div>

## About

<p class="about-line">
  BareWire is developed by <a href="https://github.com/Wizard-Software">Wizard-Software</a> and hosted on GitHub at
  <a href="https://github.com/Wizard-Software/BareWire">Wizard-Software/BareWire</a>. MIT licensed.
</p>
