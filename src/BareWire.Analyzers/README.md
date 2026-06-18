# BareWire.Analyzers

Roslyn analyzers that enforce coding conventions from [CONSTITUTION.md](https://github.com/Wizard-Software/BareWire/blob/main/docs/architecture/CONSTITUTION.md) at compile time.

## Rules

### BW1001 — CancellationToken propagation

**Severity:** Warning  
**Category:** BareWire.Async

Every public method returning `Task`, `ValueTask`, `Task<T>`, or `ValueTask<T>` must accept
`CancellationToken` as its **last** parameter. This allows callers to cancel long-running
operations and propagate cancellation across the call stack.

#### Before (triggers BW1001)

```csharp
public class OrderService
{
    // BW1001: public async method missing CancellationToken
    public async Task PlaceOrderAsync(string orderId)
    {
        await Task.Delay(100);
    }
}
```

#### After (compliant)

```csharp
public class OrderService
{
    public async Task PlaceOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await Task.Delay(100, cancellationToken);
    }
}
```

#### Code fix

A code fix is provided. Apply it from the lightbulb menu in your IDE to automatically append
`CancellationToken cancellationToken = default` to the method's parameter list and add the
`using System.Threading;` import if needed.

#### Suppression

To suppress the warning for a specific method when CancellationToken is genuinely not applicable:

```csharp
#pragma warning disable BW1001
public Task FireAndForgetAsync() { ... }
#pragma warning restore BW1001
```
