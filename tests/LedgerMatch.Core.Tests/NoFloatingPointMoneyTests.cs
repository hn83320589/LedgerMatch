using System.Reflection;

namespace LedgerMatch.Core.Tests;

/// <summary>Guards the rule that money is never handled with double/float anywhere in Core's surface.</summary>
public class NoFloatingPointMoneyTests
{
    [Fact]
    public void CoreAssembly_ExposesNoDoubleOrFloatMembers()
    {
        var offenders = FloatingPointMembers(typeof(Money).Assembly).ToList();
        Assert.True(offenders.Count == 0, "Floating point members: " + string.Join(", ", offenders));
    }

    internal static IEnumerable<string> FloatingPointMembers(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assembly.GetTypes())
        {
            foreach (var field in type.GetFields(all).Where(f => IsFloating(f.FieldType)))
            {
                yield return $"{type.Name}.{field.Name}";
            }

            foreach (var method in type.GetMethods(all))
            {
                if (IsFloating(method.ReturnType) || method.GetParameters().Any(p => IsFloating(p.ParameterType)))
                {
                    yield return $"{type.Name}.{method.Name}()";
                }
            }
        }
    }

    private static bool IsFloating(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(double) || t == typeof(float) || (t.IsArray && IsFloating(t.GetElementType()!));
    }
}
