using System.CodeDom.Compiler;
using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using BareWire.Transport.InMemory;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryAssemblyReferenceTests
{
    // Types whose static fields are safe under the "no mutable static state" rule (CLAUDE.md Hard
    // Rules): immutable value types, string, Type handles, and cached reflection metadata (MethodInfo).
    // Anything else — a collection, a mutable reference type — must NOT be static.
    private static readonly Type[] AllowedStaticFieldTypes =
    [
        typeof(string), typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double),
        typeof(decimal), typeof(char), typeof(TimeSpan), typeof(Type), typeof(MethodInfo),
    ];

    [Fact]
    public void TransportInMemoryAssembly_ReferencedBareWireAssemblies_AreOnlyAbstractions()
    {
        string[] bareWireRefs = typeof(InMemoryTransportAdapter).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("BareWire", StringComparison.Ordinal))
            .ToArray();
        bareWireRefs.Should().BeEquivalentTo(["BareWire.Abstractions"]);
    }

    // Proves the "no static mutable state" hard rule for the whole assembly, rather than relying
    // on a shell grep, which cannot reliably tell a mutable static collection from an immutable
    // `static readonly string`/`MethodInfo` cache.
    [Fact]
    public void TransportInMemoryAssembly_StaticFields_AreImmutable()
    {
        List<string> violations = [];

        foreach (Type type in typeof(InMemoryTransportAdapter).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
            {
                continue;
            }

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            foreach (FieldInfo field in fields)
            {
                if (field.IsLiteral || field.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                {
                    continue;
                }

                // The [LoggerMessage] source generator emits `static readonly` delegate-typed fields
                // (an Action cache for a fixed-level message, a Func in a nested state struct for a
                // dynamic-level one). They carry only GeneratedCodeAttribute (on the field or its
                // declaring type) rather than CompilerGeneratedAttribute, but are immutable and
                // generator-owned, so they do not violate the "no mutable static state" rule.
                if (field.IsInitOnly
                    && typeof(Delegate).IsAssignableFrom(field.FieldType)
                    && IsGeneratedByLoggingMessageGenerator(field))
                {
                    continue;
                }

                bool isAllowedType = AllowedStaticFieldTypes.Contains(field.FieldType) || field.FieldType.IsEnum;

                if (!field.IsInitOnly || !isAllowedType)
                {
                    violations.Add(
                        $"{type.FullName}.{field.Name} (type={field.FieldType.Name}, IsInitOnly={field.IsInitOnly})");
                }
            }
        }

        violations.Should().BeEmpty();
    }

    private static bool IsGeneratedByLoggingMessageGenerator(FieldInfo field)
    {
        GeneratedCodeAttribute? attribute = field.GetCustomAttribute<GeneratedCodeAttribute>()
            ?? field.DeclaringType?.GetCustomAttribute<GeneratedCodeAttribute>();
        return attribute is not null
            && string.Equals(attribute.Tool, "Microsoft.Extensions.Logging.Generators", StringComparison.Ordinal);
    }
}
