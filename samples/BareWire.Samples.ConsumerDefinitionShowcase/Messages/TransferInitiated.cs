namespace BareWire.Samples.ConsumerDefinitionShowcase.Messages;

// Published with header BW-MessageType = "TransferInitiated" — the BareWire dispatcher resolves the
// type from the header and routes to TransferConsumer via the routing-key patterns declared on
// TransferConsumerDefinition ("transfer.eu.*" and the exact "transfer.eu.priority").
//
// RunId correlates the eventual observation with the specific POST /run invocation that published
// this message, isolating it from any stale observations left over by a previous run.
public sealed record TransferInitiated(
    string RunId,
    string TransferId,
    string Region,
    decimal Amount);
