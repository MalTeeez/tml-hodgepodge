#!/usr/bin/env bash
# Builds both mods, into out/ and into tModLoader's Mods folder. Defaults to Release: profiling a
# Debug build measures the wrong thing. Pass a configuration to override, e.g. ./build.sh Debug
#
# Each mod is packaged twice on purpose. tModLoader's build takes no output path -- ModCompile
# writes to <save directory>/Mods and nowhere else -- so the only way to steer it is to move the
# whole save directory with -tmlsavedirectory, which tMLMod.targets forwards from
# ExtraBuildModFlags. The alternative, copying out of the Mods folder, means reimplementing
# tModLoader's per platform save path and coping with Documents being redirected. Asking it to
# write where we want is shorter and has nothing to keep in sync.
#
# out/ is filled first because that pass writes to a scratch save directory, which succeeds even
# while the game holds the installed copy open. Both passes reuse the compiled dll, so the second
# costs only the packaging step.
set -e
cd "$(dirname "$0")"

configuration="${1:-Release}"

# tModLoader is a .NET process, so on Windows it needs a Windows path. `pwd -W` gives one under
# Git Bash and fails everywhere else, where plain pwd is already right.
scratch="$(pwd -W 2>/dev/null || pwd)/out/.tmlsave"
mkdir -p out

for mod in HodgepodgeClient HodgepodgeServer; do
    dotnet build "$mod/$mod.csproj" -c "$configuration" --nologo \
        -p:ExtraBuildModFlags="-tmlsavedirectory $scratch"
    mv "$scratch/Mods/$mod.tmod" "out/$mod.tmod"
    echo "  -> out/$mod.tmod"

    # A build machine has no Mods folder worth filling, so out/ is the whole job there.
    if [ -n "$CI" ]; then
        continue
    fi

    # tModLoader refuses to overwrite a .tmod the running game has loaded. out/ is already
    # written by then, so that is worth a warning rather than losing the build.
    if installed=$(dotnet build "$mod/$mod.csproj" -c "$configuration" --nologo 2>&1); then
        echo "  -> installed $mod in the Mods folder"
    else
        echo "$installed" | grep -oE "TML[0-9]+[^[]*" | head -1 >&2 || true
        echo "  !! $mod not installed in the Mods folder, out/$mod.tmod is still current" >&2
    fi
done
