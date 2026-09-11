using System;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// LoopingTextureForeground.PostDrawTiles fades Intensity towards zero when its effect should not
// show, and then draws anyway:
//
//     Intensity = MathHelper.Lerp(Intensity, 0f, 0.1f);
//     ...
//     Main.spriteBatch.Begin();
//     Draw();
//     PostDraw();
//     Main.spriteBatch.End();
//
// Draw covers the screen with 54 sprites coloured (Intensity, Intensity, Intensity, 0) and PostDraw
// draws every nuclear raindrop at Intensity / IntensityMaximum / 4 -- so once the fade has reached
// zero, which it does within a few hundred frames, every frame paints nothing at all. On the
// lower-end client that was about 1% of the thread while idle. Each raindrop also asks the asset
// repository for its texture by path, per drop per frame.
//
// With Intensity exactly zero every colour above is transparent black, so the batch is skipped at
// that value and only that value: a frame where the effect is fading in or out draws as before.
// The texture lookups are answered from a table.
//
// NuclearTorrentForeground.Update pays a second, larger bill at the same time. It spawns a raindrop
// every tick and advances every drop in a static list, and a drop that has fallen past the screen
// removes itself by List.Remove, a linear search of that list plus a shift of everything behind it,
// from inside an indexed loop over the same list. Nearly all of what Update costs is that removal,
// and all of it simulates rain nobody can see.
//
// So the spawn and the advance are skipped while the torrent is both faded out and not asking to
// show. Intensity alone would very nearly do -- it is raised off zero the moment the effect shows --
// but Update runs from PostUpdateEverything and Intensity is raised in PostDrawTiles, so the tick
// an activation starts on would skip its simulation and the rain would resume a tick late every
// time. DoesThisShow answers that tick, and it is only ever asked while Intensity is zero, which is
// exactly when the alternative is advancing a screen's worth of drops for nothing.
//
// The drops already in the list are kept rather than cleared. Frozen drops are spread across the
// screen exactly as they were when the torrent faded out, so reactivating resumes full rain at
// once, where an emptied list would leave the screen bare for the second a drop takes to fall from
// its spawn point into view.
//
// That second is still spent on the first activation of a session, which today is hidden by the
// list having filled since world load: the torrent fades in over roughly half a second against an
// empty list and the rain arrives behind it. That is the one visible change here, it happens once
// per world load, and it is the price of not simulating rain for the hours before it.
//
// The Old Duke wind and Main.rain handling at the top of Update is left alone: it runs whenever Old
// Duke is alive, including when the config that stops boss weather keeps the torrent itself hidden.
//
// Read against CalamityMod 2.2.4, where NuclearTorrentForeground is the only subclass.
public class NuclearTorrentInvisibleDraw : Patch
{
    private const string TargetMod = "CalamityMod";
    private const string ForegroundType =
        "CalamityMod.ForegroundDrawing.LoopingTextures.LoopingTextureForeground";
    private const string TorrentType =
        "CalamityMod.ForegroundDrawing.LoopingTextures.NuclearTorrentForeground";
    private const string RaindropType = TorrentType + "+NuclearRaindrop";

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().NuclearTorrentInvisibleDraw;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod calamity))
            return;

        Type foreground = calamity.Code.GetType(ForegroundType);
        MethodInfo postDrawTiles = Declared(foreground, "PostDrawTiles");
        MethodInfo draw = Declared(foreground, "Draw");
        FieldInfo intensity = foreground?.GetField("Intensity",
            BindingFlags.Instance | BindingFlags.Public);
        MethodInfo doesThisShow = Declared(foreground, "DoesThisShow");
        MethodInfo update = Declared(calamity.Code.GetType(TorrentType), "Update");
        MethodInfo drawRaindrop = calamity.Code.GetType(RaindropType)?.GetMethod("Draw",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        if (postDrawTiles == null || draw == null || intensity?.FieldType != typeof(float)
            || doesThisShow?.ReturnType != typeof(bool) || update == null || drawRaindrop == null)
        {
            Mod.Logger.Error($"Nuclear torrent invisible draw: {ForegroundType}.PostDrawTiles, " +
                $"Draw, a float Intensity, a bool DoesThisShow, {TorrentType}.Update or " +
                $"{RaindropType}.Draw is missing, patch disabled");
            return;
        }

        // Skipping at zero is only invisible while both draws still scale their colour by it.
        if (!ReadsField(draw, intensity) || !ReadsField(drawRaindrop, intensity))
        {
            Mod.Logger.Error("Nuclear torrent invisible draw: a draw no longer reads Intensity, " +
                "so skipping it at zero could hide something, patch disabled");
            return;
        }

        MonoModHooks.Modify(postDrawTiles, il => SkipWhenInvisible(il, intensity));
        MonoModHooks.Modify(update, il => SkipSimulationWhenHidden(il, intensity, doesThisShow));
        MonoModHooks.Modify(draw, il => CacheTexture(il, "Draw"));
        MonoModHooks.Modify(drawRaindrop, il => CacheTexture(il, "NuclearRaindrop.Draw"));
    }

    private static MethodInfo Declared(Type owner, string name) => owner?.GetMethod(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, binder: null,
        Type.EmptyTypes, modifiers: null);

    // `if (Intensity != 0f) { Begin(); Draw(); PostDraw(); End(); }`. The jump lands after End
    // rather than on a return, so whatever follows the batch keeps running.
    private void SkipWhenInvisible(ILContext il, FieldInfo intensity)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before,
                instruction => instruction.MatchLdsfld(out FieldReference batch)
                    && batch.Name == "spriteBatch" && batch.DeclaringType.Name == "Main",
                instruction => IsSpriteBatchCall(instruction, "Begin")))
        {
            Mod.Logger.Error("Nuclear torrent invisible draw: PostDrawTiles no longer begins " +
                "Main.spriteBatch, patch disabled");
            return;
        }

        ILCursor afterEnd = cursor.Clone();
        if (!afterEnd.TryGotoNext(MoveType.After,
                instruction => IsSpriteBatchCall(instruction, "End")))
        {
            Mod.Logger.Error("Nuclear torrent invisible draw: PostDrawTiles no longer ends the " +
                "batch it began, patch disabled");
            return;
        }

        ILLabel skipDraw = afterEnd.MarkLabel();
        ILLabel draw = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, intensity);
        cursor.Emit(OpCodes.Ldc_R4, 0f);
        cursor.Emit(OpCodes.Bne_Un, draw);
        cursor.Emit(OpCodes.Br, skipDraw);
        cursor.MarkLabel(draw);
    }

    // `if (Intensity == 0f && !DoesThisShow()) return;` in front of the raindrop spawn, which opens
    // the part of Update that only the torrent uses and runs to the end of the method.
    private void SkipSimulationWhenHidden(ILContext il, FieldInfo intensity, MethodInfo showing)
    {
        ILCursor cursor = new ILCursor(il);

        // The spawn starts with `new Vector2(Main.screenWidth / 2f, -100f)`, and the Old Duke wind
        // handling above it reads no screen width, so this is the boundary between the two. The
        // branch that skips that handling lands here, so the guard has to take its place as the
        // target -- AfterLabel -- or a tick without Old Duke would jump straight over it.
        if (!cursor.TryGotoNext(MoveType.AfterLabel,
                instruction => instruction.MatchLdsfld(out FieldReference width)
                    && width.Name == "screenWidth" && width.DeclaringType.Name == "Main",
                instruction => instruction.OpCode == OpCodes.Conv_R4,
                instruction => instruction.MatchLdcR4(2f))
            || !cursor.Clone().TryGotoNext(instruction
                => instruction.MatchNewobj(out MethodReference constructor)
                    && constructor.DeclaringType.Name == "NuclearRaindrop"))
        {
            Mod.Logger.Error("Nuclear torrent invisible draw: Update no longer spawns a raindrop " +
                "at half the screen width, so the wind handling cannot be told apart from the " +
                "simulation, simulation left as it is");
            return;
        }

        ILLabel simulate = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, intensity);
        cursor.Emit(OpCodes.Ldc_R4, 0f);
        cursor.Emit(OpCodes.Bne_Un, simulate);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Callvirt, showing);
        cursor.Emit(OpCodes.Brtrue, simulate);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(simulate);
    }

    private void CacheTexture(ILContext il, string method)
    {
        if (TextureRequestCache.RewriteRequests(il) == 0)
            Mod.Logger.Error($"Nuclear torrent invisible draw: {method} no longer requests a " +
                "texture by name, left as it is");
    }

    private static bool IsSpriteBatchCall(Instruction instruction, string name) =>
        instruction.MatchCallvirt(out MethodReference called)
        && called.Name == name && called.DeclaringType.Name == "SpriteBatch";
}
