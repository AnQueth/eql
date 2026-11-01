using System.Reflection;
using System.Reflection.Emit;
using EqLog.Core;
using Xunit;

namespace EqLog.Overlay.Tests;

/// <summary>Checks compiled calls, including generated lambdas, so renaming a domain variable cannot bypass the boundary.</summary>
public sealed class PresentationBoundaryTests
{
    private static readonly IReadOnlyDictionary<short, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    [Fact]
    public void BoundaryDistinguishesPortableValuesFromCalculatedRules()
    {
        Assert.True(IsDataAccess(typeof(EqLog.Core.Tracking.FightSnapshot).GetProperty("Total")!.GetMethod!));
        Assert.False(IsDataAccess(typeof(EqLog.Core.Tracking.FightSnapshot).GetProperty("You")!.GetMethod!));
        Assert.False(IsDataAccess(typeof(QuestLookupResult).GetProperty("NeedsRefresh")!.GetMethod!));
        Assert.False(IsDataAccess(typeof(LogEngine).GetMethod("ResetSession")!));
    }

    [Fact]
    public void PresentationCallsNoDomainRulesOrLiveState()
    {
        var violations = new List<string>();
        foreach (var type in typeof(MainWindow).Assembly.GetTypes())
        {
            if (IsComposition(type))
            {
                continue;
            }
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (IsLiveDomainType(field.FieldType))
                {
                    violations.Add($"{type.Name}.{field.Name} holds {field.FieldType.Name}");
                }
            }
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
            foreach (var method in methods)
            {
                foreach (var called in Calls(method))
                {
                    if (called.DeclaringType?.Assembly == typeof(LogEngine).Assembly && !IsDataAccess(called))
                    {
                        violations.Add($"{type.Name}.{method.Name} calls {called.DeclaringType.Name}.{called.Name}");
                    }
                }
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Distinct().Order()));
    }

    private static bool IsComposition(Type type)
    {
        return type.Name is "InventoryComposition" or "LogSessionComposition" or "SettingsComposition" or
            "StartupComposition" or "ThemeComposition" or "TrackingComposition" or "WikiComposition" ||
            type == typeof(MainWindow.StartupState) ||
            type.DeclaringType is { } parent && IsComposition(parent);
    }

    private static bool IsLiveDomainType(Type type)
    {
        return type.Assembly == typeof(LogEngine).Assembly &&
            (type.Name.EndsWith("Store", StringComparison.Ordinal) || type.Name.EndsWith("Tracker", StringComparison.Ordinal) ||
                type.Name is "LogEngine" or "NpcCompendium" or "QuestInventory" or "TriggerMatcher");
    }

    private static bool IsDataAccess(MethodBase method)
    {
        var type = method.DeclaringType!;
        if (IsLiveDomainType(type))
        {
            return false;
        }
        if (method.IsConstructor || method.Name is "Equals" or "GetHashCode" or "ToString" or "<Clone>$" or "op_Equality" or "op_Inequality" ||
            method.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            return true;
        }
        return method.Name.StartsWith("get_", StringComparison.Ordinal) &&
            type.GetField($"<{method.Name[4..]}>k__BackingField", BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic) is not null;
    }

    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null)
        {
            yield break;
        }
        int index = 0;
        while (index < bytes.Length)
        {
            short value = bytes[index++];
            if (value == 0xfe)
            {
                value = (short)(0xfe00 | bytes[index++]);
            }
            var code = Codes[value];
            if (code.OperandType == OperandType.InlineMethod)
            {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, index),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (called is not null)
                {
                    yield return called;
                }
            }
            index += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, index),
                _ => 4,
            };
        }
    }
}
