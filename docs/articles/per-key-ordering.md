# Per-Key Consumer Ordering

Konsumenci konkurujący (ang. *competing consumers* — wiele instancji czytających z tej samej kolejki) dają poziomą skalowalność, ale rozbijają kolejność: wiadomości tej samej encji mogą trafić do różnych instancji i zostać przetworzone równolegle, a więc **nie po kolei**. BareWire pozwala odzyskać kolejność **w obrębie klucza** zachowując równoległość **między kluczami** — wzorzec „równoległość MIĘDZY kluczami, kolejność W kluczu".

Funkcja jest **domyślnie wyłączona**. Bez `OrderedBy`/`OrderedByHeader` na endpoincie ścieżka konsumpcji jest bit-identyczna z czystym competing-consumers — zero regresji dla istniejących wdrożeń.

## Why per-key ordering

Rozważ zdarzenia zamówienia: `OrderPlaced`, `OrderShipped`, `OrderDelivered` dla tego samego zamówienia MUSZĄ zostać przetworzone w kolejności. Przy wielu instancjach konsumenta bez afinicji klucza dwie instancje mogą jednocześnie przetwarzać dwa zdarzenia tego samego zamówienia — i zapisać stan w złej kolejności.

Rozwiązanie polega na **przypięciu wszystkich wiadomości danego klucza do tej samej, sekwencyjnej ścieżki przetwarzania**, podczas gdy różne klucze nadal płyną równolegle. „Klucz" to zwykle identyfikator encji, agregatu albo sagi (np. `OrderId`, `CustomerId`, `AccountId`).

## Quick start (one-liner)

Najprostsza forma to jeden z dwóch one-linerów na `IReceiveEndpointConfigurator`.

Wariant nagłówkowy (raw / cross-language) — klucz pobierany z nagłówka transportowego:

```csharp
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedByHeader("ordering-key");
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

Wariant typowany — klucz pobierany z właściwości zdeserializowanej wiadomości:

```csharp
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedBy<OrderShipped>(m => m.AccountId);
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

Oba one-linery ustawiają wyłącznie **źródło klucza** i zostawiają strategię na `Auto`. Strategia `Auto` jest *capability-driven*: na RabbitMQ wymaga zadeklarowania afinicji transportowej (zob. [Transport affinity](#transport-affinity-rabbitmq)) dla kolejności między instancjami, w przeciwnym razie kończy się fail-fast przy starcie. Dla porządku w obrębie jednej instancji wybierz strategię `LocalPartitioned` w bloku konfiguratora (poniżej).

## The configurator block

Przeciążenie `OrderedBy(Action<IConsumerOrderingConfigurator>)` udostępnia pełną kontrolę: źródło klucza, równoległość, strategię, afinicję transportową i politykę poison.

```csharp
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.ConcurrentMessageLimit = 16;
    e.OrderedBy(o =>
    {
        o.ByHeader("ordering-key");                              // key source: transport header
        o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
        o.MaxDeliveryAttempts(2);                               // poison / anti-starvation
    });
    e.Consumer<OrderShippedConsumer, OrderShipped>();
    e.Consumer<InventoryAdjustedConsumer, InventoryAdjusted>();
});
```

Metody bloku `IConsumerOrderingConfigurator`:

| Metoda | Działanie |
|--------|-----------|
| `ByHeader(string)` | Źródło klucza: nagłówek transportowy (raw / cross-language). |
| `By<TMessage>(Func<TMessage, object?>)` | Źródło klucza: selektor po zdeserializowanej wiadomości. |
| `ByCorrelationId()` | Źródło klucza: automatycznie stemplowany correlation-id (fallback w łańcuchu). |
| `Concurrency(int)` | Równoległość między kluczami w warstwie lokalnej (liczba pasów). |
| `Strategy(ConsumerOrderingStrategy)` | Wybór strategii. Domyślnie `Auto`. |
| `TransportAffinity(TransportAffinity)` | Deklaracja afinicji transportowej (czytana przy starcie; bez round-tripu do brokera). |
| `MaxDeliveryAttempts(int)` | Próg prób przed zaparkowaniem trującego heada i zwolnieniem klucza. Domyślnie `0` (wyłączone). |

## Strategies

`ConsumerOrderingStrategy` wybiera, jak egzekwowana jest kolejność:

| Strategia | Co robi | Gwarancja |
|-----------|---------|-----------|
| `Auto` (domyślna) | Czyta zdolności transportu oraz zadeklarowaną `TransportAffinity`; wybiera ścieżkę transport-native gdy jest zadeklarowana, w przeciwnym razie **rzuca przy starcie** | Intra + inter-instance (gdy ścieżka zadeklarowana) |
| `LocalPartitioned` | **Wyłącznie** kluczowany dispatch w obrębie procesu (fixed-lane hashing) | **Tylko intra-instance** — jawnie „single-instance only"; nie zachowuje kolejności między konkurującymi instancjami |
| `TransportNative` | Wymusza afinicję klucz→konsument na poziomie transportu (RabbitMQ SAC lub consistent-hash) | Intra + inter-instance |

`LocalPartitioned` MUSI być wybrany jawnie — `Auto` nigdy go nie wybierze samoczynnie, bo nie daje gwarancji między instancjami. Wybór `LocalPartitioned` to świadoma akceptacja braku afinicji cross-process.

## Transport affinity (RabbitMQ)

Dla kolejności **między instancjami** trzeba przypiąć każdy klucz do jednej instancji na poziomie brokera. RabbitMQ oferuje dwie ścieżki, obie **opt-in** w jawnie deklarowanej topologii (BareWire używa manualnej topologii domyślnie):

### Single-active-consumer (rekomendowane)

`x-single-active-consumer` promuje dokładnie jednego aktywnego konsumenta na kolejkę — uporządkowane przetwarzanie, zero równoległości w obrębie kolejki. To **rekomendowana, domyślna best-practice ścieżka**. Zadeklaruj argument kolejki przez `IQueueConfigurator.SingleActiveConsumer()` i zadeklaruj zamiar na endpoincie przez `TransportAffinity.SingleActiveConsumer`:

```csharp
rmq.ConfigureTopology(t =>
{
    t.DeclareQueue("ordered-processing", durable: true, autoDelete: false, configure: q => q
        .SingleActiveConsumer()
        .DeadLetterExchange("ordered-events-dlx")
        .DeadLetterRoutingKey("ordered-events-dlq"));
});

rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedBy(o =>
    {
        o.ByHeader("ordering-key");
        o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
    });
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

### Consistent-hash exchange (opt-in)

`ExchangeType.ConsistentHash` rozdziela klucze na wiele związanych kolejek (ten sam klucz → ta sama kolejka), dając równoległość po kluczach przy zachowaniu kolejności w kluczu. Wymaga włączonego pluginu brokera `rabbitmq_consistent_hash_exchange`. Wybierz tę ścieżkę gdy potrzebujesz maksymalnej równoległości po kluczach i akceptujesz **okno utraty kolejności przy re-mapie**: dodanie/usunięcie związanej kolejki lub restart węzła re-hashuje klucze i chwilowo łamie kolejność per-klucz. Okno jest udokumentowane i wykrywalne (warstwa transportowa znakuje strumień epoką mapowania, więc konsument może wykryć i zalogować re-map), ale jest realne — dlatego SAC pozostaje ścieżką domyślną.

## The two-tier model

BareWire składa kolejność per klucz z dwóch warstw, które działają razem:

- **Warstwa lokalna (intra-instance).** Kluczowany dispatch w obrębie procesu: wiadomości tego samego klucza biegną sekwencyjnie po jednym pasie (FIFO wg kolejności przybycia), różne klucze biegną równolegle po różnych pasach. Liczbą pasów steruje `Concurrency(n)` (a w braku — `ConcurrentMessageLimit`). Klucze mapowane są na **stałą liczbę N pasów** (fixed-lane hashing) — różne klucze mogą dzielić pas, jak partycje w modelu partycyjnym brokerów. To trwale ogranicza pamięć (N pasów, nie jeden pas na klucz). Bufory pasów są bounded **liczbą wiadomości** (głębokość pasa × N pasów) — nie ma nieograniczonych buforów.
- **Warstwa transportowa (inter-instance).** Afinicja klucz→instancja tak, by ten sam klucz docierał do tej samej instancji (RabbitMQ SAC albo consistent-hash, zob. wyżej).

Globalny kredyt inflight (`MaxInFlightMessages`) i głębokość pasa per-klucz to **dwa odrębne wymiary ograniczeń** — globalny kredyt bramkuje pobór z brokera, głębokość pasa chroni przed zdominowaniem budżetu przez jeden gorący klucz.

## Fail-fast

Gdy ordering jest włączony, ale ani transport, ani zadeklarowana topologia nie gwarantują kolejności (np. RabbitMQ bez SAC i bez consistent-hash), BareWire **rzuca `BareWireConfigurationException` przy starcie** — nigdy nie degraduje po cichu, przepuszczając nieuporządkowane wiadomości. Zasada: domyślnie OFF; gdy ON — pełna gwarancja albo fail-fast. Decyzja zapada deterministycznie przy starcie, na podstawie konfiguracji (bez odpytywania brokera).

In-process partitioner bez gwarancji cross-process jest dostępny **wyłącznie** jako jawny `Strategy(ConsumerOrderingStrategy.LocalPartitioned)`.

## Poison handling / key release

Kolejność per klucz niesie ryzyko head-of-line: trująca wiadomość na czele klucza mogłaby zablokować cały strumień tego klucza. Kontrakt anty-starvation: **ograniczone ponawianie → park/DLQ → zwolnienie klucza**.

- Wiadomość na czele jest ponawiana do `MaxDeliveryAttempts` (z reużyciem `RetryCount`/`RetryInterval` endpointu).
- Po przekroczeniu progu wiadomość trafia do dead-letter (zob. [Retry and Dead Letter Queues](retry-and-dlq.md)) i **schodzi z czoła** klucza.
- Strumień klucza **wznawia się** — kolejne wiadomości są dostarczane. Pominięcie zaparkowanej wiadomości (gap kolejności) jest **logowane**; nie ma ścieżki „blokady na zawsze".

Zwolnienie klucza następuje **dopiero po trwałym potwierdzeniu** odłożenia heada przez brokera — gdy settlement zawiedzie, klucz nie jest zwalniany (head zostaje na czole, porządek niezłamany), a niepowodzenie jest ponawiane.

> **Bezpieczeństwo:** kod konsumenta NIE powinien umieszczać wartości klucza porządkującego w komunikatach wyjątków ani logach. Trzymaj stały komunikat:
>
> ```csharp
> if (context.Headers.TryGetValue("poison-head-demo", out string? flag) && flag == "true")
> {
>     const string poisonHeadMessage =
>         "Simulated poison-head failure. Ordering-key value is omitted from this message.";
>     throw new InvalidOperationException(poisonHeadMessage);
> }
> ```

## Key source and caveats

Łańcuch źródła klucza: jawny selektor/nagłówek (`OrderedBy`/`OrderedByHeader`/`By`/`ByHeader`) → fallback na correlation-id (`ByCorrelationId()` lub domyślnie) → brak klucza (wiadomość keyless, przetwarzana równolegle bez gwarancji porządku).

Fallback na correlation-id jest **świadomym ustępstwem na rzecz ergonomii** (one-liner ma „po prostu działać"), ale wymaga ostrożności:

- **Kardynalność.** Zbyt mała kardynalność (jedna wartość dla całego ruchu) tworzy gorący klucz, który dusi równoległość; nadmiernie zmienny klucz (inna wartość per wiadomość) daje brak realnej afinicji — każda wiadomość to osobna „grupa" i kolejność niczego nie wnosi.
- **Stabilność.** Klucz ma sens tylko, gdy jest stabilny per agregat/encja przez cały cykl życia.
- **Dostępność.** Correlation-id NIE jest stemplowany dla zwykłego `PublishAsync`/`SendAsync` — przy takim ruchu fallback daje brak klucza, więc wiadomość płynie bez orderingu (passthrough).

**Selektor typowany a kolejność między instancjami.** Selektor `OrderedBy(m => m.X)` czyta właściwość CLR **po deserializacji**, która może różnić się od klucza, po którym transport routował wiadomość do instancji. Dlatego:

- selektor typowany jest **bezpieczny dla `LocalPartitioned`** (afinicja czysto lokalna) albo gdy selektor zwraca dokładnie wartość użytą do routingu;
- dla `TransportNative`/`Auto` między instancjami **preferuj `OrderedByHeader(name)`** z nazwą nagłówka symetryczną do strony producenta — to jedyna ścieżka z gwarancją „klucz konsumenta == klucz routingu".

## End-to-end with the outbox

Klucz porządkujący po stronie konsumenta domyka się z producenckim outboxem. Outbox w trybie `OrderingMode.PerKey` gwarantuje uporządkowane przekazanie do brokera per klucz, a konsument z `OrderedByHeader` zachowuje tę kolejność przy przetwarzaniu. **Symetryczna nazwa nagłówka** spina obie strony w jedną historię:

```csharp
// Producer — the outbox stamps and orders by the "ordering-key" header
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(connectionString),
    configureOutbox: outbox =>
    {
        outbox.OrderingMode = OrderingMode.PerKey;
        outbox.OrderingKeyHeaderName = "ordering-key";
    });

// Consumer — reads the same header
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedBy(o =>
    {
        o.ByHeader("ordering-key");
        o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
    });
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

Wynik: uporządkowane przekazanie do brokera **i** uporządkowane przetwarzanie u konsumenta — pełna kolejność per klucz end-to-end. Zob. [Transactional Outbox](outbox.md) po stronę producencką.

## Running the sample

Działający pokaz end-to-end (wiele konkurujących instancji przez Aspire `WithReplicas(2)`, outbox `OrderingMode.PerKey`, parkowanie trującego heada przez DLX, oba warianty strategii) znajduje się w katalogu sampla:

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

> See: `samples/BareWire.Samples.OrderedConsumers/`
