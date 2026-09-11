using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Three StarsAbove damage fields open their AI with the same unconditional particle spawn:
//
//     ParticleOrchestrator.RequestParticleSpawn(clientOnly: false, ParticleOrchestraType.NightsEdge,
//         new ParticleOrchestraSettings { PositionInWorld = ... }, Projectile.owner);
//
// One per tick, per field, and the fields last eight seconds and overlap. Two patches rewrite that
// call from different angles, so finding it lives here rather than in either of them.
public static class LingeringFieldSpawns
{
    public static readonly string[] TargetTypes =
    [
        "StarsAbove.Projectiles.Bosses.Thespian.ThespianLingeringDamageField",
        "StarsAbove.Projectiles.Ranged.InheritedCaseM4A1.InheritedCaseFireField",
        "StarsAbove.Projectiles.StellarNovas.GuardiansLight.SilenceSquallDamageField",
    ];

    // The flag is the call's first argument, so it is pushed first and the particle type follows it
    // immediately. Walking back further than the argument list could reach a zero belonging to
    // something else, and all three methods reach the call in seven instructions.
    private const int ArgumentSearchDepth = 16;

    public static MethodInfo FindAI(Mod starsAbove, string type) =>
        starsAbove.Code.GetType(type)?.GetMethod("AI",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

    public static bool IsParticleSpawn(Instruction instruction) =>
        instruction.OpCode == OpCodes.Call
        && instruction.Operand is MethodReference call
        && call.Name == "RequestParticleSpawn"
        && call.DeclaringType.Name == "ParticleOrchestrator";

    // Walks back from the call for the `false` that opens its argument list, which is also where
    // the whole call begins on the stack. Requiring the particle type to be the constant right
    // after it is what separates the flag from any other zero: the two are adjacent only because
    // they are arguments one and two of this call.
    public static Instruction FindClientOnlyArgument(Instruction call)
    {
        Instruction instruction = call.Previous;
        for (int step = 0; step < ArgumentSearchDepth && instruction != null; step++)
        {
            if (instruction.OpCode == OpCodes.Ldc_I4_0 && IsInt32Constant(instruction.Next))
                return instruction;

            instruction = instruction.Previous;
        }

        return null;
    }

    private static bool IsInt32Constant(Instruction instruction) => instruction != null
        && (instruction.OpCode == OpCodes.Ldc_I4 || instruction.OpCode == OpCodes.Ldc_I4_S
            || (instruction.OpCode.Code >= Code.Ldc_I4_M1
                && instruction.OpCode.Code <= Code.Ldc_I4_8));
}
