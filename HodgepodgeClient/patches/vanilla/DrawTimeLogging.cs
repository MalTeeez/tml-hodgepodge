using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// TimeLogger records how long each draw phase took, for the frame time log Terraria writes when
// you ask it to. Its cold entry points check first:
//
//     public static void MapDrawTime(double timeElapsed) { if (currentlyLogging) { ... } }
//
// The hot ones do not. DetailedDrawTime reads detailedDrawTimer.Elapsed and stores the result
// whether anyone is logging or not, and it is called once per draw phase -- around thirty times a
// frame. Stopwatch.GetRawElapsedTicks alone is 0.75% of the client thread, 0.107 ms/frame, and the
// arrays it feeds are private: nothing reads them back except the log writer, behind the same flag
// the cold methods already test.
//
// So this is the shape Hodgepodge exists for, in vanilla rather than a mod: a producer running
// while its consumer is switched off.
//
// The one visible difference is at the moment logging starts. timeMax carries a hundred frame
// decay, so the first logged frame's "New Maximum" annotations are measured against a maximum that
// was not being maintained. The timings themselves are unaffected.
public class DrawTimeLogging : Patch
{
    private const string LoggingFlag = "currentlyLogging";

    // Everything TimeLogger exposes that is called per frame and is not already gated. The first
    // two read the stopwatch themselves and are where the cost is; the rest only store a value the
    // caller already measured, and are included so the whole set behaves consistently.
    private static readonly (string Method, System.Type[] Parameters)[] Targets =
    [
        ("DetailedDrawTime", [typeof(int)]),
        ("DetailedDrawReset", []),
        ("RenderTime", [typeof(int), typeof(double)]),
        ("DrawTime", [typeof(int), typeof(double)]),
        ("LightingTime", [typeof(int), typeof(double)]),
    ];

    private static FieldInfo _currentlyLogging;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().DrawTimeLogging;

    protected override void Apply()
    {
        _currentlyLogging = typeof(TimeLogger).GetField(LoggingFlag,
            BindingFlags.Static | BindingFlags.NonPublic);

        if (_currentlyLogging?.FieldType != typeof(bool))
        {
            Mod.Logger.Error($"Draw time logging: TimeLogger.{LoggingFlag} is not a static bool, " +
                "patch disabled");
            return;
        }

        foreach ((string name, System.Type[] parameters) in Targets)
        {
            MethodInfo method = typeof(TimeLogger).GetMethod(name,
                BindingFlags.Static | BindingFlags.Public, binder: null, parameters,
                modifiers: null);

            if (method?.ReturnType != typeof(void))
            {
                Mod.Logger.Error($"Draw time logging: TimeLogger.{name} is not a static void " +
                    "method with the expected parameters, left as it is");
                continue;
            }

            MonoModHooks.Modify(method, SkipUnlessLogging);
        }
    }

    private void SkipUnlessLogging(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        ILLabel logging = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldsfld, _currentlyLogging);
        cursor.Emit(OpCodes.Brtrue, logging);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(logging);
    }
}
