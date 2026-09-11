using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// GoozmaAuricSoulScene and YharonAuricSoulScene are scene effects, so IsSceneEffectActive runs
// once per player per tick, and each asks whether its soul item lies anywhere in the world:
//
//     bool num = Main.item.Any((Item n) => n.active && n.type == ModContent.ItemType<GoozmaSoul>());
//
// The id lookup sits inside the predicate, so it runs for each of the 400 item slots on every
// call. 0.22% of the lower-end client's thread for the pair, resolving a number fixed at load.
//
// The lookup folds to its constant inside the compiler's lambda body. The predicate, the scan and
// everything the result drives are untouched.
//
// Read against CalamityHunt 1.2.3.
public class AuricSoulSceneItemIds : Patch
{
    private const string TargetMod = "CalamityHunt";
    private const string SceneNamespace = "CalamityHunt.Common.Graphics.SceneEffects.";

    private static readonly string[] Scenes = ["GoozmaAuricSoulScene", "YharonAuricSoulScene"];

    protected override bool Enabled =>
        ModContent.GetInstance<ClientConfig>().AuricSoulSceneItemIds;

    protected override void Apply()
    {
        if (!ModLoader.TryGetMod(TargetMod, out Mod hunt))
            return;

        foreach (string scene in Scenes)
        {
            MethodInfo predicate = ScenePredicate(hunt.Code.GetType(SceneNamespace + scene));
            if (predicate == null)
            {
                Mod.Logger.Error($"Auric soul scene item ids: {SceneNamespace}{scene}." +
                    "IsSceneEffectActive no longer tests items through a lambda, left as it is");
                continue;
            }

            MonoModHooks.Modify(predicate, il =>
            {
                List<string> skipped = [];
                int folded = ContentIdFolding.FoldContentLookups(il,
                    ContentIdFolding.InAssembly(hunt.Code), skipped.Add);
                ContentIdFolding.Report(Mod, "Auric soul scene item ids",
                    $"{scene}.IsSceneEffectActive", folded, skipped);
            });
        }
    }

    // The lambda compiles to a method on a nested closure class, named after the method that
    // declares it. It is found by that name and by its shape, an Item in and a bool out.
    private static MethodInfo ScenePredicate(Type scene) => scene?.GetNestedTypes(
            BindingFlags.NonPublic | BindingFlags.Public)
        .SelectMany(closure => closure.GetMethods(BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
        .FirstOrDefault(method => method.Name.StartsWith("<IsSceneEffectActive>b__")
            && method.ReturnType == typeof(bool)
            && method.GetParameters() is [{ ParameterType: var item }] && item == typeof(Item));
}
