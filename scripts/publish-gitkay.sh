#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/GitKay.UI/GitKay.UI.csproj"
CONFIGURATION="Release"
RUNTIME_IDS=()
OUTPUT_DIR="$ROOT_DIR/artifacts/publish/gitkay"
USE_AOT="true"
HOST_OS="$(uname -s)"
EXTRA_ARGS=()

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Publishes GitKay as a self-contained single-file app.

Options:
  --rid <runtime>         Runtime identifier to publish for (repeatable)
  --all                   Publish both win-x64 and linux-x64 (default)
  --output <dir>          Output directory base (default: artifacts/publish/gitkay)
  --configuration <name>  Build configuration (default: Release)
  --aot                   Try NativeAOT publishing instead of single-file bundling
  -p:<Name>=<value>       Extra MSBuild property passed to dotnet publish (repeatable)
  -h, --help              Show this help

Examples:
  $(basename "$0")
  $(basename "$0") --rid win-x64 --output /tmp/gitkay-publish
  $(basename "$0") --all
  $(basename "$0") --aot --rid win-x64
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      if [[ -z "${2:-}" ]]; then
        echo "Missing runtime identifier after --rid." >&2
        exit 1
      fi
      RUNTIME_IDS+=("$2")
      shift 2
      ;;
    --all)
      RUNTIME_IDS=("win-x64" "linux-x64")
      shift
      ;;
    --linux)
      RUNTIME_IDS=("linux-x64")
      shift
      ;;
    --windows)
      RUNTIME_IDS=("win-x64")
      shift
      ;;
    --output)
      OUTPUT_DIR="${2:-}"
      shift 2
      ;;
    --configuration)
      CONFIGURATION="${2:-}"
      shift 2
      ;;
    --aot)
      USE_AOT="true"
      shift
      ;;
    -p:*)
      EXTRA_ARGS+=("$1")
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ "${#RUNTIME_IDS[@]}" -eq 0 ]]; then
  #RUNTIME_IDS=("win-x64" "linux-x64")
  RUNTIME_IDS=("linux-x64")
fi

if [[ "$USE_AOT" == "true" ]]; then
  for rid in "${RUNTIME_IDS[@]}"; do
    case "$HOST_OS:$rid" in
      Linux:win-*)
        echo "NativeAOT cannot cross-compile Windows binaries from Linux. Use --aot --linux, or drop --aot for Windows output." >&2
        exit 1
        ;;
      Darwin:linux-*)
        echo "NativeAOT cannot cross-compile Linux binaries from macOS. Use a matching host RID." >&2
        exit 1
        ;;
      Windows_NT:linux-*|Windows_NT:osx-*)
        echo "NativeAOT requires a matching host RID on Windows." >&2
        exit 1
        ;;
    esac
  done
fi

publish_one() {
  local rid="$1"
  local output_dir="$2"
  local publish_args=(
    dotnet publish "$PROJECT"
    -c "$CONFIGURATION"
    -r "$rid"
    -o "$output_dir"
  )

  if [[ "$USE_AOT" == "true" ]]; then
    publish_args+=(
      -p:PublishAot=true
    )
    # The .NET 11 RC's classic macOS x64 linker rejects NativeAOT's split-debug object for
    # System.Net.Security. Link with symbols embedded, then strip debug sections after linking.
    if [[ "$rid" == "osx-x64" ]]; then
      publish_args+=( -p:StripSymbols=false )
    fi
  else
    publish_args+=(
      -p:PublishSingleFile=true
      -p:IncludeNativeLibrariesForSelfExtract=true
      -p:EnableCompressionInSingleFile=true
    )
  fi

  if [[ "${#EXTRA_ARGS[@]}" -gt 0 ]]; then
    publish_args+=("${EXTRA_ARGS[@]}")
  fi

  mkdir -p "$output_dir"
  echo "Publishing GitKay for $rid to $output_dir"
  "${publish_args[@]}"

  if [[ "$USE_AOT" == "true" && "$rid" == "osx-x64" ]]; then
    strip -S "$output_dir/gitkay"
  fi

  # gitkay-gui opens the commit window directly, as `git gui` does next to `gitk`.
  case "$rid" in
    win-*) cp "$ROOT_DIR/assets/launchers/gitkay-gui.cmd" "$output_dir/" ;;
    *) cp "$ROOT_DIR/assets/launchers/gitkay-gui" "$output_dir/" && chmod +x "$output_dir/gitkay-gui" ;;
  esac
}

for rid in "${RUNTIME_IDS[@]}"; do
  if [[ "${#RUNTIME_IDS[@]}" -eq 1 ]]; then
    publish_one "$rid" "$OUTPUT_DIR"
  else
    publish_one "$rid" "$OUTPUT_DIR/$rid"
  fi
done

if [[ "$USE_AOT" == "true" ]]; then
  echo "NativeAOT mode is experimental for this codebase."
  echo "Only same-OS publishing is supported in AOT mode."
fi
