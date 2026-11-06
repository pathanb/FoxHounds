#!/bin/bash
# Builds the release folders (M8; Q-06: self-contained folders only): the app and the fh tool side by side with the
# .NET runtime, the presets and the user guide, so nothing has to be installed. One folder and one archive per runtime
# under artifacts/release; a .zip for Windows, a .tar.gz (which keeps the executable bits) for Linux and macOS.
#
#   tools/publish.sh                 # win-x64 linux-x64 osx-arm64
#   tools/publish.sh linux-x64       # one runtime
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
rids=("$@")
if [ ${#rids[@]} -eq 0 ]; then
    rids=(win-x64 linux-x64 osx-arm64)
fi

version=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repo/Directory.Build.props")
out="$repo/artifacts/release"
mkdir -p "$out"
for rid in "${rids[@]}"; do
    name="FoxHounds-$version-$rid"
    dir="$out/$name"
    rm -rf "$dir" "$out/$name.zip" "$out/$name.tar.gz"
    for project in FoxHounds.App FoxHounds.Cli; do
        # Both apps share one runtime: the second publish rewrites the shared files with identical ones.
        # English only (no satellite resource folders); the .pdb files stay, so a crash report has line numbers.
        dotnet publish "$repo/src/$project" -c Release -r "$rid" --self-contained -o "$dir" --nologo -v quiet \
            -p:PublishDocumentationFile=false -p:PublishReferencesDocumentationFiles=false \
            -p:SatelliteResourceLanguages=en
    done

    cp "$repo/docs/user-guide.md" "$dir/USER-GUIDE.md"
    cp -r "$repo/experiments" "$dir/experiments"   # the research examples of the guide, section 9
    if [[ $rid == win-* ]]; then
        python3 -c 'import shutil, sys; shutil.make_archive(sys.argv[1], "zip", root_dir=sys.argv[2], base_dir=sys.argv[3])' \
            "$out/$name" "$out" "$name"
    else
        tar -C "$out" -czf "$out/$name.tar.gz" "$name"
    fi

    echo "$name: $(find "$dir" -type f | wc -l) files, $(du -sh "$dir" | cut -f1); $(ls "$out" | grep -E "^$name\.(zip|tar\.gz)$")"
done

(cd "$out" && sha256sum FoxHounds-"$version"-*.zip FoxHounds-"$version"-*.tar.gz 2>/dev/null > SHA256SUMS.txt || true)
