using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// CalPaintOverride.FindFrame opens with `if (!TexturesActive) return;` and runs per NPC per frame,
// so TexturesActive is evaluated for every NPC on screen every frame. It reads DateTime.Now up to
// five times -- each an expensive timezone conversion -- to work out whether it is April 1st, and
// only then reaches its last line, `return Main.netMode == 0;`.
//
// That final line makes the whole property false whenever netMode is not 0, so the date work can
// never change the answer outside single player. Returning false up front when netMode != 0 is
// therefore exactly equivalent, not merely close: with the big condition true the original returns
// `netMode == 0` which is false, and with it false the original returns false.
//
// Preferred over CalValEX's AprilFoolsContent config, which switches the feature off everywhere.
// This keeps April Fools working in single player, where it is the only place it can activate.
public class AprilFoolsDateCheck : Patch
{
    private const string TargetMod = "CalValEX";
    private const string TargetType = "CalValEX.AprilFools.CalPaintOverride";
    private const string TargetProperty = "TexturesActive";

    private const byte Ldsfld = 0x7E;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().AprilFoolsDateCheck;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calValEx))
            return;

        MethodInfo getter = calValEx.Code.GetType(TargetType)
            ?.GetProperty(TargetProperty, BindingFlags.Static | BindingFlags.Public)
            ?.GetMethod;

        if (getter == null || getter.ReturnType != typeof(bool))
        {
            Mod.Logger.Error($"April fools date check: {TargetType}.{TargetProperty} is not a " +
                "static bool property, patch disabled");
            return;
        }

        // The equivalence argument above rests entirely on the getter still consulting netMode.
        // If CalValEX drops that term the property can be true in multiplayer, and an early
        // false would silently disable a working feature instead of skipping dead work.
        if (!ReadsNetMode(getter))
        {
            Mod.Logger.Error($"April fools date check: {TargetProperty} no longer reads " +
                "Main.netMode, so returning false early would change behaviour, patch disabled");
            return;
        }

        MonoModHooks.Modify(getter, SkipDateWorkOutsideSinglePlayer);
    }

    private static void SkipDateWorkOutsideSinglePlayer(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        ILLabel singlePlayer = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldsfld, typeof(Main).GetField(nameof(Main.netMode)));
        cursor.Emit(OpCodes.Brfalse, singlePlayer);
        cursor.Emit(OpCodes.Ldc_I4_0);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(singlePlayer);
    }

    // Walks the getter's IL for a static field load of Main.netMode. Scanning for the opcode is
    // enough here because ldsfld is fixed width: one byte and a four byte metadata token.
    private static bool ReadsNetMode(MethodInfo getter)
    {
        byte[] body = getter.GetMethodBody()?.GetILAsByteArray();
        if (body == null)
            return false;

        FieldInfo netMode = typeof(Main).GetField(nameof(Main.netMode));
        for (int offset = 0; offset + 5 <= body.Length; offset++)
        {
            if (body[offset] != Ldsfld)
                continue;

            try
            {
                if (getter.Module.ResolveField(BitConverter.ToInt32(body, offset + 1)) == netMode)
                    return true;
            }
            catch (ArgumentException)
            {
                // Not a field token: this byte was operand data, not an opcode. Keep scanning.
            }
        }

        return false;
    }
}
