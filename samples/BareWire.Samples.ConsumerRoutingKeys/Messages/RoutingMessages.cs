namespace BareWire.Samples.ConsumerRoutingKeys.Messages;

// Typed message — published with header BW-MessageType = "TransferInitiated".
// The BareWire dispatcher resolves the type from the header and routes to the
// consumer whose routing-key pattern matches the delivery's routing key.
internal sealed record TransferInitiated(
    string RunId,
    string TransferId,
    string Region,
    string Kind,
    decimal Amount);

// Foreign message — published WITHOUT a BW-MessageType header (raw-first interop).
// Only consumers that have explicitly opted in via AcceptUntyped() and whose
// routing-key pattern matches the delivery are eligible to receive this message type.
internal sealed record LegacyNotification(
    string RunId,
    string NotificationId,
    string Source,
    string Detail);
