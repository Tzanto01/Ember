using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Ember.Core.Services;
using FluentAssertions;

namespace Ember.Api.Tests;

/// <summary>
/// The reminder dispatch in <c>ReminderService.Dispatch.cs</c> loads and writes habits and routines by id regardless of owner, so an API
/// handler calling one of those methods would be an IDOR. This walks the IL of every method in <c>Ember.Api</c> (lambdas and async state
/// machines included) and fails on any call to them. The methods are found by their source file through <c>Ember.Core.pdb</c>, so a new
/// loader in that file is covered without changing this test.
/// </summary>
public class OwnerAgnosticLoaderTests
{
    private const string DispatchFile = "ReminderService.Dispatch.cs";

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    [Fact]
    public void No_type_in_Ember_Api_calls_a_loader_that_ignores_the_owner()
    {
        var loaders = OwnerAgnosticLoaders();

        var calls = typeof(Program).Assembly.GetTypes()
            .SelectMany(Methods)
            .SelectMany(m => Callees(m).Where(c => loaders.Contains(c)).Select(c => $"{m.DeclaringType}.{m.Name} -> {c.Name}"))
            .Distinct()
            .ToList();

        calls.Should().BeEmpty();
    }

    [Fact]
    public void The_loaders_are_found_from_the_dispatch_file()
    {
        OwnerAgnosticLoaders().Select(m => m.Name).Should().Contain(
            [nameof(ReminderService.GetHabitWithUserAsync), nameof(ReminderService.GetRoutineWithUserAsync), nameof(ReminderService.SaveChangesAsync)])
            .And.NotContain(nameof(ReminderService.SnoozeReminderAsync));
    }

    [Fact]
    public void The_scan_sees_a_loader_call_inside_an_async_lambda()
    {
        var loaders = OwnerAgnosticLoaders();

        WithNested(typeof(LoaderCaller)).SelectMany(Methods).SelectMany(Callees).Should().Contain(c => loaders.Contains(c));
    }

    private static HashSet<MethodBase> OwnerAgnosticLoaders()
    {
        var core = typeof(ReminderService).Assembly;
        using var stream = File.OpenRead(Path.ChangeExtension(core.Location, ".pdb"));
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var pdb = provider.GetMetadataReader();

        var loaders = new HashSet<MethodBase>();
        foreach (var handle in pdb.MethodDebugInformation)
        {
            var info = pdb.GetMethodDebugInformation(handle);
            if (!info.GetSequencePoints().Any(p => pdb.GetString(pdb.GetDocument(p.Document).Name).EndsWith(DispatchFile, StringComparison.Ordinal)))
                continue;

            var kickoff = info.GetStateMachineKickoffMethod();
            var definition = kickoff.IsNil ? handle.ToDefinitionHandle() : kickoff;
            if (core.ManifestModule.ResolveMethod(MetadataTokens.GetToken(definition)) is { IsPublic: true } method
                && method.DeclaringType == typeof(ReminderService))
                loaders.Add(method);
        }
        return loaders;
    }

    private static IEnumerable<MethodBase> Methods(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
    }

    private static IEnumerable<Type> WithNested(Type type)
        => type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested).Prepend(type);

    private static IEnumerable<MethodBase> Callees(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;

        var typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var i = 0; i < il.Length;)
        {
            var value = (short)il[i++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[i++]));
            var op = OpCodesByValue[value];
            if (op.OperandType == OperandType.InlineMethod
                && method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs) is { } callee)
                yield return callee;
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
    }

    private static class LoaderCaller
    {
        public static Func<ReminderService, Task> Call => async reminders => await reminders.GetHabitWithUserAsync(1);
    }
}
