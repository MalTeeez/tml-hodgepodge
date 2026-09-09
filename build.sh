#!/usr/bin/env bash
# Builds both mods. tModLoader writes each .tmod straight into its Mods folder, so there is
# nothing to copy afterwards. Defaults to Release: profiling a Debug build measures the wrong
# thing. Pass a configuration to override, e.g. ./build.sh Debug
set -e
cd "$(dirname "$0")"

configuration="${1:-Release}"
for mod in HodgepodgeClient HodgepodgeServer; do
    dotnet build "$mod/$mod.csproj" -c "$configuration" --nologo
done
