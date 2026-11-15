#!/usr/bin/env bash
# FileHub 构建脚本
# 自带资源护栏：nice + 限制 MSBuild 并行度 + 不留常驻 worker 节点。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# 允许用 FILEHUB_DOTNET 指定 SDK；否则优先本机 x64 SDK（arm64 CodeNet SDK
# 在 Avalonia XAML 编译任务上会 139），再退回 PATH 里的 dotnet。
if [[ -n "${FILEHUB_DOTNET:-}" ]]; then
  DOTNET="$FILEHUB_DOTNET"
elif [[ -x "$HOME/dotnet11x64/dotnet" ]]; then
  DOTNET="$HOME/dotnet11x64/dotnet"
else
  DOTNET="dotnet"
fi

echo "使用 SDK: $("$DOTNET" --version 2>/dev/null || echo unknown) ($DOTNET)"
exec nice -n 20 env MSBUILDDISABLENODEREUSE=1 \
  "$DOTNET" build "$ROOT/FileHub.slnx" -m:2 -nodeReuse:false "$@"
