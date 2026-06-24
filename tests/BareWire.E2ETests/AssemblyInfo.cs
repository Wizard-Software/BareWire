using Xunit;

// E2E tests drive real Docker containers (PostgreSQL + RabbitMQ via Aspire), and several
// stand up multiple in-process hosts plus polling OutboxDispatchers. Running the classes in
// parallel oversubscribes the container host and causes non-deterministic timeouts in the
// slow, poll-based tests (transactional outbox, competing consumers). Each test passes
// reliably in isolation, and the Aspire containers use a shared Session lifetime, so
// serializing the assembly gives deterministic E2E runs at the cost of wall-clock time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
