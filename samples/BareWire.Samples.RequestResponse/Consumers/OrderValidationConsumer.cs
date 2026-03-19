using BareWire.Abstractions;
using BareWire.Samples.RequestResponse.Data;
using BareWire.Samples.RequestResponse.Messages;
using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.RequestResponse.Consumers;

// IConsumer<T> is resolved per-message from DI — must be stateless or transient/scoped.
public sealed class OrderValidationConsumer(ValidationDbContext db) : IConsumer<ValidateOrder>
{
    public async Task ConsumeAsync(ConsumeContext<ValidateOrder> context)
    {
        ValidateOrder request = context.Message;

        (bool isValid, string? reason) = Validate(request);

        // Persist the validation outcome to PostgreSQL before responding.
        db.ValidationRecords.Add(new ValidationRecord
        {
            OrderId = request.OrderId,
            IsValid = isValid,
            Reason = reason,
            ValidatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        // Send the response back to the originating IRequestClient via the ReplyTo header.
        await context.RespondAsync(
            new OrderValidationResult(request.OrderId, isValid, reason),
            context.CancellationToken).ConfigureAwait(false);
    }

    private static (bool isValid, string? reason) Validate(ValidateOrder request)
    {
        if (string.IsNullOrWhiteSpace(request.OrderId))
        {
            return (false, "OrderId must not be empty.");
        }

        if (request.Amount <= 0)
        {
            return (false, $"Amount must be greater than zero (received: {request.Amount}).");
        }

        return (true, null);
    }
}
