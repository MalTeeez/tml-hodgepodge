using System;
using Terraria.ModLoader;

namespace HodgepodgeClient;

// Every patch resolves another mod's members by name and rewrites its IL, so every patch can fail
// on a dependency update. Failing has to cost this patch alone: an escaping exception here takes
// the game down during mod loading, which is worse than any patch is worth.
public abstract class Patch : ModSystem
{
    protected abstract bool Enabled { get; }

    protected abstract void Apply();

    public override void PostSetupContent()
    {
        if (!Enabled)
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
}
