#!/usr/bin/env bash
# ブラウザ版のオセロを発行(AOT)し、ふらるラボの works/othello/ に置く。
# 使い方(リポジトリのどこからでも): bash OthelloWeb/deploy-to-lab.sh <ふらるラボのフォルダ>
#   例: bash OthelloWeb/deploy-to-lab.sh /c/15_MyProjects/Others/huraru_Lab
# 必要なもの: .NET 10 SDK と wasm-tools ワークロード(dotnet workload install wasm-tools)、gzip
# Windows では Git Bash、Mac ではターミナルで動かす
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "使い方: bash OthelloWeb/deploy-to-lab.sh <ふらるラボのフォルダ>" >&2
  exit 1
fi

lab_dir="$1"
if [ ! -f "$lab_dir/labWorks.json" ]; then
  echo "$lab_dir はふらるラボのフォルダではありません(labWorks.json がありません)" >&2
  exit 1
fi

web_dir="$(cd "$(dirname "$0")" && pwd)"
repo_dir="$(cd "$web_dir/.." && pwd)"
publish_dir="$repo_dir/publish/web-aot"
target_dir="$lab_dir/works/othello"

# 1. 評価ファイルを gzip にする(ブラウザの DecompressionStream で戻す)。元は OthelloConsole/weights/
mkdir -p "$web_dir/wwwroot/weights"
for name in pattern_eval.bin move_ordering.bin; do
  gzip -9 -n -c "$repo_dir/OthelloConsole/weights/$name" > "$web_dir/wwwroot/weights/$name.gz"
done

# 2. AOT で発行する(Release のときだけ AOT になる。数分かかる)
rm -rf "$publish_dir"
dotnet publish "$web_dir/OthelloWeb.csproj" -c Release -o "$publish_dir"

# 3. works/othello/ を作り直す。対局に要るものだけを置く
#    (計測ページ bench.*、.NET が作る圧縮済みの .br / .gz は置かない。GitHub Pages はそれらを使わないため)
rm -rf "$target_dir"
mkdir -p "$target_dir/_framework" "$target_dir/weights"
source_dir="$publish_dir/wwwroot"
cp "$source_dir/index.html" "$source_dir/game.js" "$source_dir/worker.js" "$target_dir/"
cp "$source_dir"/weights/*.bin.gz "$target_dir/weights/"
for file in "$source_dir"/_framework/*; do
  case "$file" in
    *.br|*.gz) ;;
    *) cp "$file" "$target_dir/_framework/" ;;
  esac
done
cp "$web_dir/notes.md" "$target_dir/notes.md"

echo "置きました: $target_dir"
du -sh "$target_dir"
