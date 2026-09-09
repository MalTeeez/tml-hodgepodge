using System;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// NoxusBoss sizes its constellation star buffer by capacity rather than contents: every frame a
// constellation is on screen it allocates and uploads 32,768 vertices (768 KiB) and draws all
// 16,384 triangles, while the shapes in use fill a quarter of that at most. The live count is
// sitting in ActiveStarCount immediately above both sites.
public class ConstellationStarBuffer : Patch
{
    private const string RendererTypeName = "NoxusBoss.Content.NPCs.Bosses.NamelessDeity" +
        ".SpecificEffectManagers.ConstellationProjectileRenderer";

    private static FieldInfo _activeStarCountField;

    // Shrinking the upload leaves stale vertices in the tail of the buffer, so it is only safe
    // once the draw call has stopped reading past the live stars.
    private static bool _drawCallNarrowed;

    protected override bool Enabled => ModContent.GetInstance<ClientConfig>().ConstellationStarBuffer;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod("NoxusBoss", out Mod noxusBoss))
            return;

        Type rendererType = noxusBoss.Code.GetType(RendererTypeName);
        MethodInfo renderStars = rendererType?.GetMethod("RenderStars",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo updateVertexBuffer = rendererType?.GetMethod("UpdateVertexBuffer",
            BindingFlags.Static | BindingFlags.NonPublic);
        _activeStarCountField = rendererType?.GetField("ActiveStarCount",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (renderStars == null || updateVertexBuffer == null || _activeStarCountField == null)
        {
            Mod.Logger.Error("Constellation star buffer: RenderStars, UpdateVertexBuffer or " +
                $"ActiveStarCount missing from {RendererTypeName}, patch disabled");
            return;
        }

        MonoModHooks.Modify(renderStars, NarrowDrawCall);
        if (_drawCallNarrowed)
            MonoModHooks.Modify(updateVertexBuffer, ShrinkStarArray);
    }

    // Draw the live stars instead of the whole buffer. Both arguments are replaced together or
    // not at all: a narrowed vertex range with the original primitive count draws past the data.
    private void NarrowDrawCall(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);

        if (!cursor.TryGotoNext(MoveType.After,
                i => i.MatchCallvirt(out MethodReference called) && called.Name == "get_VertexCount"))
        {
            Mod.Logger.Error("Constellation star buffer: vertex count anchor not found in " +
                "RenderStars, patch disabled");
            return;
        }
        int vertexCount = cursor.Index;

        if (!cursor.TryGotoNext(MoveType.After, i => i.OpCode == OpCodes.Div))
        {
            Mod.Logger.Error("Constellation star buffer: primitive count anchor not found in " +
                "RenderStars, patch disabled");
            return;
        }

        // Later argument first, so replacing it does not shift the index of the earlier one.
        ReplaceIntOnStack(cursor, cursor.Index, ActiveStarTriangles);
        ReplaceIntOnStack(cursor, vertexCount, ActiveStarVertices);
        _drawCallNarrowed = true;
    }

    // Allocate only the vertices in use; SetData uploads the whole array it is handed.
    private void ShrinkStarArray(ILContext il)
    {
        ILCursor cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before,
                i => i.MatchNewarr(out TypeReference element)
                    && element.Name == "VertexPositionColorTexture"))
        {
            Mod.Logger.Error("Constellation star buffer: star array anchor not found in " +
                "UpdateVertexBuffer, upload left at full size");
            return;
        }
        ReplaceIntOnStack(cursor, cursor.Index, ActiveStarVertices);
    }

    private static void ReplaceIntOnStack(ILCursor cursor, int index, Func<int> replacement)
    {
        cursor.Index = index;
        cursor.Emit(OpCodes.Pop);
        cursor.EmitDelegate(replacement);
    }

    private static int ActiveStarVertices() => (int)_activeStarCountField.GetValue(null) * 4;

    private static int ActiveStarTriangles() => (int)_activeStarCountField.GetValue(null) * 2;
}
