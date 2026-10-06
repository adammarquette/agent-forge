using System.Reflection;
using System.Reflection.Emit;

namespace AgentForge.UnitTests.TestSupport;

/// <summary>
/// Reads the string literals a method compares its input against, straight from the compiled IL -
/// for a <c>switch</c> on a string, the set of case labels. The compiler lowers every string case
/// to an ordinal <c>string ==</c> against the literal, whatever dispatch it builds in front of it
/// (a hash jump table, or a length-and-character one), so an <c>ldstr</c> immediately consumed by
/// <c>string.op_Equality</c> is a case label and nothing else in the method is.
/// </summary>
/// <remarks>
/// Fails closed: a method that stops being a string switch yields an empty set, which cannot equal
/// a non-empty expectation. Used where the routing table has no other enumerable form and adding
/// one would mean a second, hand-kept list of the same names.
/// </remarks>
internal static class StringSwitchCases
{
    private static readonly MethodInfo StringEquality =
        typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    /// <summary>Every literal <paramref name="method"/> tests its input against with <c>==</c>.</summary>
    public static IReadOnlySet<string> Of(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var module = method.Module;
        var labels = new HashSet<string>(StringComparer.Ordinal);
        string? pendingLiteral = null;

        for (var offset = 0; offset < il.Length;)
        {
            var opCode = ReadOpCode(il, ref offset);
            var operandStart = offset;
            offset += OperandSize(opCode, il, operandStart);

            if (opCode == OpCodes.Ldstr)
            {
                pendingLiteral = module.ResolveString(BitConverter.ToInt32(il, operandStart));
                continue;
            }

            if (pendingLiteral is not null && opCode == OpCodes.Call &&
                module.ResolveMethod(BitConverter.ToInt32(il, operandStart)) == StringEquality)
            {
                labels.Add(pendingLiteral);
            }

            pendingLiteral = null;
        }

        return labels;
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var value = (short)il[offset++];
        if (value == 0xFE)
        {
            value = unchecked((short)(0xFE00 | il[offset++]));
        }

        return OpCodesByValue[value];
    }

    private static int OperandSize(OpCode opCode, byte[] il, int operandStart) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandStart)),
        _ => 4,
    };
}
