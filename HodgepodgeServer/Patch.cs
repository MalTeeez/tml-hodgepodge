using System;
using System.Reflection;
using System.Reflection.Emit;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeServer;

// Every patch resolves another mod's members by name and rewrites its IL, so every patch can fail
// on a dependency update. Failing has to cost this patch alone: an escaping exception here takes
// the server down during mod loading, which is worse than any patch is worth.
//
// This mod is side = Both, so it also loads on every client that joins. Nothing here is meant to
// run there -- the client patches live in their own mod, and applying both to one process would
// have the second rewrite look for IL the first already replaced -- so the dedicated server test
// belongs in one place rather than in each patch.
public abstract class Patch : ModSystem
{
    private const byte TwoByteOpCodePrefix = 0xFE;

    // Built once from the runtime's own opcode table so ReadsField can step instruction by
    // instruction. Scanning for a bare opcode byte at every offset instead would let operand data
    // pose as an instruction, and a guard that answers yes on a coincidence is worse than none.
    private static readonly OpCode[] OneByteOpCodes = new OpCode[256];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[256];

    static Patch()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Static
            | BindingFlags.Public))
        {
            OpCode opCode = (OpCode)field.GetValue(null);
            if (opCode.Size == 1)
                OneByteOpCodes[opCode.Value & 0xFF] = opCode;
            else
                TwoByteOpCodes[opCode.Value & 0xFF] = opCode;
        }
    }

    protected abstract bool Enabled { get; }

    protected abstract void Apply();

    public override void PostSetupContent()
    {
        if (!Main.dedServ || !Enabled)
            return;

        try
        {
            Apply();
        }
        catch (Exception exception)
        {
            Mod.Logger.Error($"{GetType().Name}: disabled, {exception}");
        }
    }

    // Walks a method's IL for a load of a given field, static or instance. A patch that replaces a
    // method with a cheaper equivalent is only equivalent while the original still consults the
    // state the argument rests on, and this is how that gets pinned.
    //
    // This says the field is read, not that it still decides the outcome. A refactor that keeps
    // the read but changes what it governs passes this check, so it narrows the ways a patch can
    // go stale rather than closing them. Where the argument has a recognisable shape, matching
    // that shape in the manipulator is the stronger guard.
    protected static bool ReadsField(MethodBase method, FieldInfo field)
    {
        byte[] body = method.GetMethodBody()?.GetILAsByteArray();
        if (body == null || field == null)
            return false;

        int offset = 0;
        while (offset < body.Length)
        {
            OpCode opCode = body[offset] == TwoByteOpCodePrefix && offset + 1 < body.Length
                ? TwoByteOpCodes[body[offset + 1]]
                : OneByteOpCodes[body[offset]];

            // An opcode the table does not know means the walk has lost the instruction boundary,
            // and a guess past that point is worthless. Report no read, which disables the caller.
            if (opCode.Size == 0)
                return false;

            offset += opCode.Size;
            if (IsFieldLoad(opCode) && ResolveField(method, body, offset) == field)
                return true;

            int operand = OperandLength(opCode, body, offset);
            if (operand < 0)
                return false;

            offset += operand;
        }

        return false;
    }

    // Address loads count as reads. A field passed by reference is consulted by whatever receives
    // it just as a value load is, and pinning on the value load alone would miss every field a
    // method hands out that way.
    private static bool IsFieldLoad(OpCode opCode) => opCode == OpCodes.Ldsfld
        || opCode == OpCodes.Ldfld || opCode == OpCodes.Ldsflda || opCode == OpCodes.Ldflda;

    private static FieldInfo ResolveField(MethodBase method, byte[] body, int offset)
    {
        if (offset + 4 > body.Length)
            return null;

        // A token inside a generic method or type only resolves against its type arguments.
        return method.Module.ResolveField(BitConverter.ToInt32(body, offset),
            method.DeclaringType?.IsGenericType == true
                ? method.DeclaringType.GetGenericArguments() : null,
            method.IsGenericMethod ? method.GetGenericArguments() : null);
    }

    // Bytes of operand following the opcode. A switch carries a jump count and then that many
    // four byte targets; everything else is fixed by its operand type.
    private static int OperandLength(OpCode opCode, byte[] body, int offset) => opCode.OperandType
        switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => offset + 4 > body.Length
                ? -1 : 4 + 4 * BitConverter.ToInt32(body, offset),
            _ => -1,
        };
}
