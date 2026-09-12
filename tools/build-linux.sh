#!/usr/bin/env bash
# Restore and build OpenAC on Linux without requiring a writable home directory.
#
# Usage:
#   bash tools/build-linux.sh [--test]
#
# The cache locations may be overridden by the environment. They deliberately
# live outside the checkout so a restore cannot modify tracked lock files.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cache_root="${XDG_CACHE_HOME:-/tmp}/openac-dotnet"

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$cache_root/cli}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$cache_root/packages}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$cache_root/http}"
export MSBuildUserExtensionsPath="${MSBuildUserExtensionsPath:-$cache_root/msbuild}"
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
export DOTNET_NOLOGO="${DOTNET_NOLOGO:-1}"

test_after_build=false
if [[ "${1:-}" == "--test" ]]; then
    test_after_build=true
elif [[ $# -ne 0 ]]; then
    echo "Usage: bash tools/build-linux.sh [--test]" >&2
    exit 64
fi

cd "$repo_root"
dotnet --version
# Keep generated lock files outside the checkout. The lock-update script owns
# the committed neutral and RID-specific files.
mkdir -p "$cache_root/locks"
lock_path="$cache_root/locks/\$(MSBuildProjectName).lock.json"
dotnet restore AcDream.slnx "-p:NuGetLockFilePath=$lock_path"
dotnet build AcDream.slnx -c Release --no-restore -p:CoDeployBakeToolOnBuild=false

if [[ "$test_after_build" == true ]]; then
    dotnet test AcDream.slnx -c Release --no-build --no-restore \
        --filter 'Lane!=InstalledDat&Lane!=PreparedPackage&Lane!=Live&Lane!=Manual&Lane!=Timing&Lane!=Windows&Lane!=Linux&Lane!=MacOS&Lane!=Unix&Lane!=Vulkan&Lane!=SystemFont&Purpose!=Diagnostic&Status!=KnownFailure'
fi
