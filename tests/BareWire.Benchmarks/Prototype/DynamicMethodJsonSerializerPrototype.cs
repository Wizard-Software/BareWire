// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
//
// SPIKE-ONLY CODE — NOT FOR PRODUCTION USE.
// This file lives in the Benchmarks project and is linked into BareWire.UnitTests via
// <Compile Include> for unit-testability (D-1). It must not reference any
// BareWire.Benchmarks-specific types (e.g. BenchmarkOrder) so the generic API
// compiles in both host projects.
//
// Design notes:
//  - DynamicMethod(skipVisibility: true) is used so that the emitted IL can access
//    internal/private members of record types. This deliberately bypasses member
//    accessibility — it is an explicit trade-off noted in D-6b and is a SPIKE-ONLY
//    decision; a production serializer should prefer source-gen (no skipVisibility,
//    AOT-compatible, no encapsulation bypass).
//  - Native AOT / trimming: DynamicMethod is JIT-only. Any production path derived
//    from this spike would be incompatible with Native AOT (D-4 hard gate).
//  - static ConcurrentDictionary cache is an accepted CONSTITUTION violation for
//    spike code in a test project. A production implementation must use a
//    Singleton-scoped resolver (D-PROD).

using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;

namespace BareWire.Benchmarks.Prototype;

/// <summary>
/// Research-spike serializer that emits a per-type <c>Action&lt;Utf8JsonWriter, T&gt;</c>
/// delegate via <see cref="DynamicMethod"/> + <see cref="ILGenerator"/> and caches it in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// <para>
/// Supported property types: <see cref="string"/>, <see cref="int"/>, <see cref="decimal"/>,
/// and <c>List&lt;TItem&gt;</c> where <typeparamref name="T"/> is a class whose items are
/// handled recursively through the same delegate cache.
/// </para>
/// <para>
/// JSON output is byte-identical to <c>SystemTextJsonSerializer</c> with
/// <c>BareWireJsonSerializerOptions.Default</c>:
/// camelCase property names, <c>WhenWritingNull</c> omits null values,
/// <c>JsonSerializerDefaults.Web</c>.
/// </para>
/// </summary>
/// <remarks>
/// SPIKE ONLY — see file header for production constraints.
/// </remarks>
internal sealed class DynamicMethodJsonSerializerPrototype
{
    // Build-counter exposed for falsifiable cache tests (D-1 test requirement).
    // Incremented inside BuildWriter<T> which is called at most once per type.
    private static int s_buildCount;

    private static readonly ConcurrentDictionary<Type, Delegate> s_writers = new();

    private static readonly JsonWriterOptions s_writerOptions = new() { SkipValidation = true };

    // Thread-local pooling of Utf8JsonWriter — mirrors SystemTextJsonSerializer (D-5, critical PERF-1).
    // Avoids the ~448 B-per-call allocation so the benchmark measures emit-dispatch overhead, not writer cost.
    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    /// <summary>
    /// Returns the number of <see cref="DynamicMethod"/> delegates built so far.
    /// Used by unit tests to assert the cache is hit on subsequent calls (D-1).
    /// </summary>
    internal static int BuildCount => s_buildCount;

    /// <summary>
    /// Resets the build counter. Call this in test setup to get a clean baseline.
    /// </summary>
    internal static void ResetBuildCount() => s_buildCount = 0;

    /// <summary>
    /// Serializes <paramref name="message"/> to <paramref name="output"/> using a
    /// per-type emitted delegate. The delegate is built once and cached for subsequent calls.
    /// </summary>
    public static void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);

        // Thread-static pool — same pattern as SystemTextJsonSerializer (D-5).
        Utf8JsonWriter writer = t_writer ??= new Utf8JsonWriter(Stream.Null, s_writerOptions);
        writer.Reset(output);

        try
        {
            var action = (Action<Utf8JsonWriter, T>)s_writers.GetOrAdd(typeof(T), static _ => BuildWriter<T>());
            action(writer, message);
            writer.Flush();
        }
        finally
        {
            // Return writer to pool state so the next Reset(output) is safe.
            writer.Reset(Stream.Null);
        }
    }

    // -------------------------------------------------------------------------
    // Emit helpers — called at most once per type (cached after first build).
    // -------------------------------------------------------------------------

    private static Action<Utf8JsonWriter, T> BuildWriter<T>()
    {
        System.Threading.Interlocked.Increment(ref s_buildCount);

        Type type = typeof(T);

        // skipVisibility: true — allows the emitted IL to call property getters on
        // types that are internal/sealed. This is a spike-only trade-off (D-6b).
        var method = new DynamicMethod(
            name: $"BareWire_EmitJson_{type.Name}",
            returnType: typeof(void),
            parameterTypes: [typeof(Utf8JsonWriter), type],
            owner: type,
            skipVisibility: true);

        ILGenerator il = method.GetILGenerator();
        EmitObjectWrite(il, type);

        return method.CreateDelegate<Action<Utf8JsonWriter, T>>();
    }

    /// <summary>
    /// Emits IL for: WriteStartObject → per-property writes → WriteEndObject.
    /// </summary>
    private static void EmitObjectWrite(ILGenerator il, Type type)
    {
        // writer.WriteStartObject()
        il.Emit(OpCodes.Ldarg_0);
        il.EmitCall(OpCodes.Callvirt, s_writeStartObject, null);

        foreach (PropertyInfo prop in GetSerializableProperties(type))
        {
            EmitPropertyWrite(il, prop);
        }

        // writer.WriteEndObject()
        il.Emit(OpCodes.Ldarg_0);
        il.EmitCall(OpCodes.Callvirt, s_writeEndObject, null);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits IL for a single property. Scalar types write directly;
    /// nullable scalars and reference types emit null-checks (WhenWritingNull D-2);
    /// <c>List&lt;TItem&gt;</c> emits a call to <see cref="WriteList{TItem}"/>.
    /// </summary>
    private static void EmitPropertyWrite(ILGenerator il, PropertyInfo prop)
    {
        Type propType = prop.PropertyType;
        string camelName = ToCamelCase(prop.Name);

        MethodInfo getter = prop.GetGetMethod(nonPublic: true)
            ?? throw new InvalidOperationException($"Property {prop.Name} has no getter.");

        // ---- string ----
        if (propType == typeof(string))
        {
            // if (value != null) writer.WriteString(name, value)
            Label skipLabel = il.DefineLabel();

            // Load property value (for null check)
            il.Emit(OpCodes.Ldarg_1);
            il.EmitCall(OpCodes.Callvirt, getter, null);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Brtrue, skipLabel);

            // writer.WriteString(camelName, value)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, camelName);
            il.Emit(OpCodes.Ldarg_1);
            il.EmitCall(OpCodes.Callvirt, getter, null);
            il.EmitCall(OpCodes.Callvirt, s_writeStringByStringString, null);

            il.MarkLabel(skipLabel);
            return;
        }

        // ---- int ----
        if (propType == typeof(int))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, camelName);
            il.Emit(OpCodes.Ldarg_1);
            il.EmitCall(OpCodes.Callvirt, getter, null);
            il.EmitCall(OpCodes.Callvirt, s_writeNumberInt32, null);
            return;
        }

        // ---- decimal ----
        if (propType == typeof(decimal))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, camelName);
            il.Emit(OpCodes.Ldarg_1);
            il.EmitCall(OpCodes.Callvirt, getter, null);
            il.EmitCall(OpCodes.Callvirt, s_writeNumberDecimal, null);
            return;
        }

        // ---- List<TItem> (nullable reference type) ----
        Type? listItemType = GetListItemType(propType);
        if (listItemType != null)
        {
            // if (value != null) WriteList<TItem>(writer, camelName, list)
            Label skipLabel = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_1);
            il.EmitCall(OpCodes.Callvirt, getter, null);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Brtrue, skipLabel);

            // Call WriteList<TItem>(writer, name, list)
            MethodInfo writeListMethod = s_writeListMethod.MakeGenericMethod(listItemType);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, camelName);
            il.Emit(OpCodes.Ldarg_1);
            il.EmitCall(OpCodes.Callvirt, getter, null);
            il.EmitCall(OpCodes.Call, writeListMethod, null);

            il.MarkLabel(skipLabel);
            return;
        }

        // ---- Nested class / record (nullable) ----
        if (propType.IsClass)
        {
            // if (value != null) { writer.WritePropertyName(name); WriteObject<TItem>(writer, value) }
            Label skipLabel = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_1);
            il.EmitCall(OpCodes.Callvirt, getter, null);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Brtrue, skipLabel);

            MethodInfo writeObjectMethod = s_writeObjectMethod.MakeGenericMethod(propType);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, camelName);
            il.Emit(OpCodes.Ldarg_1);
            il.EmitCall(OpCodes.Callvirt, getter, null);
            il.EmitCall(OpCodes.Call, writeObjectMethod, null);

            il.MarkLabel(skipLabel);
            return;
        }

        // Unsupported type — skip silently in the spike (spike limitation is acceptable).
    }

    // -------------------------------------------------------------------------
    // Static helper methods called from emitted IL
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called from emitted IL to write a <c>List&lt;TItem&gt;</c> property.
    /// Writes: propertyName → StartArray → per-item delegate call → EndArray.
    /// </summary>
    internal static void WriteList<TItem>(Utf8JsonWriter writer, string propertyName, List<TItem> list)
        where TItem : class
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        // Resolve or build the per-item delegate from the shared cache.
        var itemWriter = (Action<Utf8JsonWriter, TItem>)s_writers.GetOrAdd(
            typeof(TItem), static _ =>
            {
                System.Threading.Interlocked.Increment(ref s_buildCount);
                return BuildItemWriter<TItem>();
            });

        foreach (TItem item in list)
        {
            if (item is null)
                continue;
            itemWriter(writer, item);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Called from emitted IL to write a nested class/record as a JSON object property.
    /// </summary>
    internal static void WriteObject<T>(Utf8JsonWriter writer, string propertyName, T value)
        where T : class
    {
        writer.WritePropertyName(propertyName);
        var itemWriter = (Action<Utf8JsonWriter, T>)s_writers.GetOrAdd(
            typeof(T), static _ =>
            {
                System.Threading.Interlocked.Increment(ref s_buildCount);
                return BuildItemWriter<T>();
            });
        itemWriter(writer, value);
    }

    /// <summary>
    /// Builds a write delegate for list items / nested types (same IL generation, different entry point).
    /// </summary>
    private static Action<Utf8JsonWriter, T> BuildItemWriter<T>()
    {
        Type type = typeof(T);

        var method = new DynamicMethod(
            name: $"BareWire_EmitJson_{type.Name}_Item",
            returnType: typeof(void),
            parameterTypes: [typeof(Utf8JsonWriter), type],
            owner: type,
            skipVisibility: true);

        ILGenerator il = method.GetILGenerator();
        EmitObjectWrite(il, type);

        return method.CreateDelegate<Action<Utf8JsonWriter, T>>();
    }

    // -------------------------------------------------------------------------
    // Reflection metadata (resolved once at class init)
    // -------------------------------------------------------------------------

    private static readonly MethodInfo s_writeStartObject =
        typeof(Utf8JsonWriter).GetMethod(nameof(Utf8JsonWriter.WriteStartObject), Type.EmptyTypes)!;

    private static readonly MethodInfo s_writeEndObject =
        typeof(Utf8JsonWriter).GetMethod(nameof(Utf8JsonWriter.WriteEndObject), Type.EmptyTypes)!;

    private static readonly MethodInfo s_writeStringByStringString =
        typeof(Utf8JsonWriter).GetMethod(
            nameof(Utf8JsonWriter.WriteString),
            [typeof(string), typeof(string)])!;

    private static readonly MethodInfo s_writeNumberInt32 =
        typeof(Utf8JsonWriter).GetMethod(
            nameof(Utf8JsonWriter.WriteNumber),
            [typeof(string), typeof(int)])!;

    private static readonly MethodInfo s_writeNumberDecimal =
        typeof(Utf8JsonWriter).GetMethod(
            nameof(Utf8JsonWriter.WriteNumber),
            [typeof(string), typeof(decimal)])!;

    private static readonly MethodInfo s_writeListMethod =
        typeof(DynamicMethodJsonSerializerPrototype).GetMethod(
            nameof(WriteList),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    private static readonly MethodInfo s_writeObjectMethod =
        typeof(DynamicMethodJsonSerializerPrototype).GetMethod(
            nameof(WriteObject),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns public instance properties (including those from init-only record members)
    /// in declaration order, excluding compiler-synthesized members such as <c>EqualityContract</c>.
    /// </summary>
    private static IEnumerable<PropertyInfo> GetSerializableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.Name != "EqualityContract");

    /// <summary>
    /// Converts a PascalCase property name to camelCase, mirroring
    /// <see cref="System.Text.Json.JsonNamingPolicy.CamelCase"/>.
    /// </summary>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            return name;

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Returns the element type if <paramref name="type"/> is <c>List&lt;T&gt;</c>; otherwise null.
    /// </summary>
    private static Type? GetListItemType(Type type)
    {
        if (!type.IsGenericType)
            return null;
        if (type.GetGenericTypeDefinition() != typeof(List<>))
            return null;
        return type.GetGenericArguments()[0];
    }
}
