using System.Reflection;
using System.Reflection.Emit;

namespace ProjectC.Application.Tests.TestSupport;

// 讀出型別（含 async 狀態機、lambda closure 等編譯器產生的巢狀型別）方法本體 IL 裡呼叫到的方法，
// 用來斷言「不得直接呼叫某 API」這類原始碼搜尋會被註解、using static 或別名騙過的規則。
public static class IlMethodCallReader
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    public static IReadOnlyList<MethodBase> GetCalledMethodsIncludingNestedTypes(Type type)
    {
        var calledMethods = new List<MethodBase>();
        foreach (var currentType in EnumerateTypeAndNestedTypes(type))
        {
            var methodBodies = currentType.GetMethods(AllDeclared).Cast<MethodBase>()
                .Concat(currentType.GetConstructors(AllDeclared));
            foreach (var method in methodBodies)
                calledMethods.AddRange(GetCalledMethods(method));
        }

        return calledMethods;
    }

    private static IEnumerable<Type> EnumerateTypeAndNestedTypes(Type type)
    {
        yield return type;
        foreach (var nestedType in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        foreach (var descendant in EnumerateTypeAndNestedTypes(nestedType))
            yield return descendant;
    }

    private static IEnumerable<MethodBase> GetCalledMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            yield break;

        var typeArguments = method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            var opCode = ReadOpCode(il, ref position);
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, position);
                // 不吞解析失敗：解析不到的呼叫若被略過，「不得呼叫」的斷言會空洞通過。
                yield return method.Module.ResolveMethod(token, typeArguments, methodArguments)!;
            }

            position += GetOperandSize(opCode.OperandType, il, position);
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int position)
    {
        short value = il[position++];
        if (value == 0xFE)
            value = unchecked((short)(0xFE00 | il[position++]));

        return OpCodesByValue.TryGetValue(value, out var opCode)
            ? opCode
            : throw new InvalidOperationException($"Unknown IL opcode 0x{value:X4} at offset {position}.");
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int position) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, position) * 4,
        _ => 4,
    };
}
