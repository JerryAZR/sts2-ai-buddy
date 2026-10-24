using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using Xunit;

namespace AiBuddy.Tests;

/// <summary>
/// Structural completeness test: EVERY method in the game assembly that awaits a
/// remote player's choice (calls PlayerChoiceSynchronizer.WaitForRemoteChoice)
/// must have a corresponding prompt-capture patch in the mod. A wait site without
/// a capture is a phantom crash/hang we don't know about — after the v0.111.0
/// audit missed FromHandForUpgrade/FromDeckForEnchantment/RelicSelectCmd/
/// MendRestSiteOption/CardReward, this test makes the audit mechanical.
/// EA adding a new wait site turns this test red instead of stalling a run.
/// </summary>
public class WaitSiteCompletenessTests
{
    [Fact]
    public void EveryRemoteChoiceWaitSite_HasACapturePatch()
    {
        MethodInfo waitMethod = typeof(PlayerChoiceSynchronizer).GetMethod(nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice))
            ?? throw new MissingMethodException("PlayerChoiceSynchronizer.WaitForRemoteChoice");

        var waitSites = FindCallers(waitMethod)
            .Select(site => (site.DeclaringType.FullName, site.Name))
            .ToHashSet();

        var covered = PatchedMethods(typeof(ModEntry).Assembly)
            .Select(site => (site.DeclaringType.FullName, site.Name))
            .ToHashSet();

        var uncovered = waitSites.Where(site => !covered.Contains(site)).OrderBy(s => s.FullName).ToList();
        Assert.True(uncovered.Count == 0,
            "WaitForRemoteChoice call sites without a capture patch: " +
            string.Join(", ", uncovered.Select(s => $"{s.FullName}.{s.Name}")));
    }
    [Fact]
    public void EventAndCardCode_HasNoUnpinnedBlockingWaits()
    {
        // Event option selects and card OnPlay run inside the deterministic sim on
        // every peer. A blocking primitive there (sync-over-async, thread waits) is a
        // phantom wait we cannot fabricate — fail red instead of softlocking a live run.
        var flagged = new SortedSet<string>(StringComparer.Ordinal);
        Type[] types;
        try
        {
            types = typeof(PlayerChoiceSynchronizer).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }
        foreach (Type type in types)
        {
            bool inScope = type == typeof(MegaCrit.Sts2.Core.Models.EventModel)
                || type == typeof(MegaCrit.Sts2.Core.Models.CardModel)
                || type.Namespace?.Contains(".Models.Events") == true
                || IsSubclassOf(type, typeof(MegaCrit.Sts2.Core.Models.CardModel));
            if (!inScope)
            {
                continue;
            }
            foreach (MethodInfo method in AllMethods(type))
            {
                foreach (MethodBase called in CalledMethods(method))
                {
                    if (!IsBlockingPrimitive(called))
                    {
                        continue;
                    }
                    var logical = LogicalMethod(method);
                    flagged.Add($"{logical.Item1!.Name}.{logical.Item2} -> {called.DeclaringType!.Name}.{called.Name}");
                }
            }
        }
        Assert.True(flagged.Count == 0,
            "Blocking primitives in event/card code (adjudicate each: real wait = missing fabrication; benign = justify + allowlist):\n"
            + string.Join("\n", flagged));
    }

    private static bool IsSubclassOf(Type type, Type baseType)
    {
        for (Type? t = type.BaseType; t != null; t = t.BaseType)
        {
            if (t == baseType)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsBlockingPrimitive(MethodBase called)
    {
        string? decl = called.DeclaringType?.FullName;
        return (decl == "System.Threading.Tasks.Task" && called.Name is "Wait" or "WaitAll" or "WaitAny" or "get_Result")
            || (decl?.StartsWith("System.Threading.Tasks.Task`1") == true && called.Name == "get_Result")
            || (decl == "System.Threading.Thread" && called.Name == "Sleep")
            || (decl == "System.Threading.WaitHandle" && called.Name == "WaitOne")
            || (called.Name == "SpinUntil" && decl?.Contains("SpinWait") == true);
    }



    /// <summary>Logical (declaringType, methodName) of every method whose IL calls <paramref name="callee"/> — async state machines resolve to their logical method.</summary>
    private static IEnumerable<(Type DeclaringType, string Name)> FindCallers(MethodInfo callee)
    {
        Type[] types;
        try
        {
            types = typeof(PlayerChoiceSynchronizer).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }
        foreach (Type type in types)
        {
            foreach (MethodInfo method in AllMethods(type))
            {
                foreach (MethodBase called in CalledMethods(method))
                {
                    if (called == callee || (called.Name == callee.Name && called.DeclaringType == callee.DeclaringType))
                    {
                        yield return LogicalMethod(method);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Async state machine MoveNext → the logical method that generated it.</summary>
    private static (Type, string) LogicalMethod(MethodInfo method)
    {
        Type? declaring = method.DeclaringType!;
        if (method.Name == "MoveNext" && declaring.Name.StartsWith('<') && declaring.DeclaringType != null)
        {
            int end = declaring.Name.IndexOf('>');
            string logical = declaring.Name.Substring(1, end - 1);
            return (declaring.DeclaringType, logical);
        }
        return (declaring, method.Name);
    }

    private static IEnumerable<MethodInfo> AllMethods(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodInfo m in type.GetMethods(Flags))
        {
            yield return m;
        }
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (MethodInfo m in AllMethods(nested))
            {
                yield return m;
            }
        }
    }

    /// <summary>(declaringType, methodName) of every Harmony patch target in the mod assembly.</summary>
    private static IEnumerable<(Type DeclaringType, string Name)> PatchedMethods(Assembly modAssembly)
    {
        foreach (Type type in modAssembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                foreach (HarmonyPatch patch in method.GetCustomAttributes<HarmonyPatch>())
                {
                    if (patch.info.declaringType != null && patch.info.methodName != null)
                    {
                        yield return (patch.info.declaringType, patch.info.methodName);
                    }
                }
            }
            foreach (HarmonyPatch patch in type.GetCustomAttributes<HarmonyPatch>())
            {
                if (patch.info.declaringType != null && patch.info.methodName != null)
                {
                    yield return (patch.info.declaringType, patch.info.methodName);
                }
            }
        }
    }

    /// <summary>Minimal IL walk: yields the target of every call/callvirt/newobj in the method body.</summary>
    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch
        {
            yield break;
        }
        byte[]? il = body?.GetILAsByteArray();
        if (il == null)
        {
            yield break;
        }
        Module module = method.Module;
        Type[]? typeArgs = method.DeclaringType?.GetGenericArguments();
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        int i = 0;
        while (i < il.Length)
        {
            OpCode op;
            byte b = il[i++];
            if (b == 0xFE)
            {
                op = TwoByteOpCodes[il[i++]];
            }
            else
            {
                op = OneByteOpCodes[b];
            }
            int token = -1;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                case OperandType.ShortInlineBrTarget: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI:
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineR: i += 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineString:
                case OperandType.InlineSig:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                    token = BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
            }
            if (token >= 0 && (op == OpCodes.Call || op == OpCodes.Callvirt))
            {
                MethodBase? called = null;
                try
                {
                    called = module.ResolveMethod(token, typeArgs, methodArgs);
                }
                catch
                {
                    // unresolvable token (generic edge cases) — ignore
                }
                if (called != null)
                {
                    yield return called;
                }
            }
        }
    }

    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static WaitSiteCompletenessTests()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op)
            {
                ushort value = unchecked((ushort)op.Value);
                if (value < 0x100)
                {
                    OneByteOpCodes[value] = op;
                }
                else if ((value & 0xFF00) == 0xFE00)
                {
                    TwoByteOpCodes[value & 0xFF] = op;
                }
            }
        }
    }
}
