# BareWire.Serialization.Json

Raw JSON serializer for BareWire using System.Text.Json. No envelope overhead by default.

## Installation

```bash
dotnet add package BareWire.Serialization.Json
```

## Usage

```csharp
builder.AddBareWire(wire =>
{
    wire.UseJsonSerializer(); // System.Text.Json, raw output
});
```

## Features

- Raw JSON output — no envelope wrapper by default
- Zero-copy serialization via `IBufferWriter<byte>`
- Envelope mode available as opt-in
- Customizable `JsonSerializerOptions`

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
