#!/usr/bin/env bash
set -euo pipefail
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
DOTNET=${DOTNET:-dotnet}
case $(uname -s) in Darwin|Linux) ;; *) printf '仅在 Mac/Linux 运行；Windows 测试宿主初始化可能自动下载 FFmpeg。\n' >&2; exit 2 ;; esac
RUN="$ROOT/artifacts/outro-tail/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$RUN/temp"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export TMPDIR="$RUN/temp" NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
"$DOTNET" --info > "$RUN/sdk-info.txt"
pnpm --dir "$ROOT/web" install --frozen-lockfile > "$RUN/web-build.log" 2>&1
pnpm --dir "$ROOT/web" build >> "$RUN/web-build.log" 2>&1
"$DOTNET" restore "$ROOT/IntroSkipper.Tests/IntroSkipper.Tests.csproj" \
  --configfile "$ROOT/nuget/NuGet.Stable.Config" --locked-mode > "$RUN/restore.log" 2>&1
"$DOTNET" test "$ROOT/IntroSkipper.Tests/IntroSkipper.Tests.csproj" -c Release --no-restore \
  -p:SkipWebBuild=true -p:Version=12.0.4.0 -p:AssemblyVersion=12.0.4.0 -p:FileVersion=12.0.4.0 \
  --filter 'FullyQualifiedName~TestPlaybackOutroTail|FullyQualifiedName~TestTimeAdjustmentHelper|FullyQualifiedName~TestConfigHasher|FullyQualifiedName~TestAnalysisConfigHash|FullyQualifiedName~TestSegmentsApiController|FullyQualifiedName~SegmentEditorControllerTests|FullyQualifiedName~TestMediaSegmentMirror|FullyQualifiedName~TestPluginConfiguration|FullyQualifiedName~TestLegacyAnalysisCompatibility|FullyQualifiedName~TestSeasonReanalysis|FullyQualifiedName~TestDatabaseServiceRegistration' \
  --logger 'trx;LogFileName=regression.trx' --results-directory "$RUN" > "$RUN/tests.log" 2>&1
cp "$ROOT/IntroSkipper/bin/Release/net10.0/IntroSkipper.dll" "$RUN/IntroSkipper.dll"
if [[ -n "${HOST_REFERENCE_DIR:-}" ]]; then
  "$DOTNET" restore "$ROOT/patches/outro-tail/compat/CompatibilitySmoke.csproj" \
    --configfile "$ROOT/nuget/NuGet.Stable.Config" > "$RUN/compat-build.log" 2>&1
  "$DOTNET" build "$ROOT/patches/outro-tail/compat/CompatibilitySmoke.csproj" --no-restore -c Release >> "$RUN/compat-build.log" 2>&1
  "$DOTNET" "$ROOT/patches/outro-tail/compat/bin/Release/net10.0/CompatibilitySmoke.dll" \
    "$RUN/IntroSkipper.dll" "$HOST_REFERENCE_DIR" "$NUGET_PACKAGES" > "$RUN/host-compatibility.json"
  printf '实际宿主类型兼容 smoke 通过。\n'
else
  printf '未提供 HOST_REFERENCE_DIR：实际宿主类型 smoke 已跳过，不宣称已验证。\n'
fi
printf '定向测试通过；产物/日志/TRX：%s\n' "$RUN"
printf '不连接或修改任何运行中的 Jellyfin 服务。\n'
