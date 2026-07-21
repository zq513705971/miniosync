#!/usr/bin/env bash
# ------------------------------------------------------------------------------
# publish-mac.sh — macOS / Linux 版发布脚本（等价于 publish.ps1）
# ------------------------------------------------------------------------------
#
# 流程对齐 publish.ps1（Windows / PowerShell 版），但在 macOS / Linux 环境下
# 用 bash 重写。其语义、产物命名、deploy/ 输出结构、版本覆盖、配置清理等
# 行为完全一致：
#
#   1. dotnet restore <sln> -r <RID>            还原 NuGet 包 + 生成 RID-specific assets
#   2. dotnet build   <sln> -c <Config> --no-restore   Release 编译
#   3. 清空 deploy/ 目录
#   4. 对 4 个 Exe 项目分别执行 dotnet publish，输出到 deploy/，self-contained +
#      single file + 压缩 + 包含原生库
#   5. 把每个发布产物的 EXE 重命名为短名（mms / mfs / mss / m2ms）
#   6. 把 config.json 拷贝到 deploy/ 根
#
# 与 publish.ps1 的差异：
#   - 默认 RID 自动检测当前机器的 macOS / Linux 架构：
#       Apple Silicon   → osx-arm64
#       Intel mac       → osx-x64
#       其他 Linux      → linux-x64
#     用 -r <RID> 可覆盖（如 -r linux-arm64）
#   - dotnet 路径探测：PATH 里找不到时，依次尝试 brew (/opt/homebrew/bin/dotnet,
#     /usr/local/bin/dotnet)、dotnet-install 安装位置 (~/.dotnet/dotnet)
#   - .ps1 里用 PowerShell 的 Get-ChildItem 列出部署文件大小，这里用 du -sh + awk
#
# 用法：
#   ./publish-mac.sh                   # 默认：Config=Release, RID=自动检测
#   ./publish-mac.sh -c Debug          # 调试配置
#   ./publish-mac.sh -r linux-x64      # 显式指定 RID
#   ./publish-mac.sh -V 1.2.3          # 版本号覆盖（写入 AssemblyInfo）
#   ./publish-mac.sh -h                # 显示帮助
# ------------------------------------------------------------------------------

set -euo pipefail

# ======================== 参数解析 ========================
Configuration="Release"
Runtime=""          # 自动检测
Solution="MinioSync.sln"
Version=""
DotnetPath=""       # 自动检测
PrintHelp=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration)
      Configuration="$2"; shift 2 ;;
    -r|--runtime)
      Runtime="$2"; shift 2 ;;
    -s|--solution)
      Solution="$2"; shift 2 ;;
    -V|--version)
      Version="$2"; shift 2 ;;
    --dotnet)
      DotnetPath="$2"; shift 2 ;;
    -h|--help)
      PrintHelp=true; shift ;;
    *)
      echo "[错误] 未知参数: $1" >&2
      echo "用法: $0 [-c Release|Debug] [-r <RID>] [-s <sln>] [-V <version>] [--dotnet <path>]" >&2
      exit 1 ;;
  esac
done

if $PrintHelp; then
  cat <<'EOF'
publish-mac.sh — macOS/Linux 版发布脚本（等价于 publish.ps1）

用法:
  ./publish-mac.sh [options]

选项:
  -c, --configuration <name>  Release 或 Debug  (默认: Release)
  -r, --runtime <RID>         目标 RID (默认: 自动检测当前架构)
                              常用 RID: osx-arm64, osx-x64, linux-x64, linux-arm64
  -s, --solution <file>       解决方案文件名 (默认: MinioSync.sln)
  -V, --version <ver>         版本号覆盖 (写入 AssemblyInfo)
      --dotnet <path>         dotnet CLI 可执行文件路径 (默认: 自动探测)
  -h, --help                  显示本帮助

示例:
  ./publish-mac.sh                                 # 默认 Release + 当前架构
  ./publish-mac.sh -c Debug                        # Debug 配置
  ./publish-mac.sh -r linux-x64                    # 强制输出 linux-x64
  ./publish-mac.sh -V 1.2.3                        # 带版本号

产物:
  deploy/ 下生成 mms.exe / mfs.exe / mss.exe / m2ms.exe 四个 self-contained
  单文件 EXE（每个约 35–40MB 压缩），含 .NET 8 runtime + 全部依赖。
EOF
  exit 0
fi

# ======================== 路径与工具探测 ========================
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SolutionFull="$SCRIPT_DIR/$Solution"
DeployDir="$SCRIPT_DIR/deploy"

if [[ ! -f "$SolutionFull" ]]; then
  echo "[错误] 找不到解决方案文件: $SolutionFull" >&2
  exit 1
fi

# ----- 自动探测 dotnet -----
detect_dotnet() {
  # 1. 用户显式传 --dotnet
  if [[ -n "$DotnetPath" && -x "$DotnetPath" ]]; then
    echo "$DotnetPath"; return
  fi
  # 2. PATH 里有没有
  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return
  fi
  # 3. Homebrew (Apple Silicon → /opt/homebrew, Intel mac → /usr/local)
  for candidate in /opt/homebrew/bin/dotnet /usr/local/bin/dotnet; do
    if [[ -x "$candidate" ]]; then echo "$candidate"; return; fi
  done
  # 4. 官方 dotnet-install.sh 安装位置
  for candidate in "$HOME/.dotnet/dotnet" "$HOME/.dotnet/tools/dotnet"; do
    if [[ -x "$candidate" ]]; then echo "$candidate"; return; fi
  done
  # 5. 找不到
  echo ""
}

DOTNET=$(detect_dotnet)
if [[ -z "$DOTNET" ]]; then
  echo "[错误] 找不到 dotnet CLI。请安装 .NET 8 SDK:" >&2
  echo "       brew install --cask dotnet-sdk       (macOS, 推荐)" >&2
  echo "       或访问 https://dotnet.microsoft.com/download/dotnet/8.0" >&2
  exit 1
fi

# ----- 自动探测 RID -----
detect_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"
  case "$os" in
    Darwin)
      case "$arch" in
        arm64) echo "osx-arm64" ;;
        x86_64) echo "osx-x64" ;;
        *)     echo "osx-${arch}" ;;
      esac ;;
    Linux)
      case "$arch" in
        x86_64)  echo "linux-x64" ;;
        aarch64|arm64) echo "linux-arm64" ;;
        *)       echo "linux-${arch}" ;;
      esac ;;
    *)
      echo ""
      ;;
  esac
}

if [[ -z "$Runtime" ]]; then
  Runtime="$(detect_rid)"
  if [[ -z "$Runtime" ]]; then
    echo "[错误] 无法自动识别 RID，请显式传 -r <RID>" >&2
    exit 1
  fi
fi

# ======================== 信息打印 ========================
echo "============================================================"
echo "MinioSync Publish (Configuration=$Configuration, Runtime=$Runtime)"
echo "============================================================"
echo "  dotnet:      $DOTNET"
echo "  dotnet 版本:  $($DOTNET --version)"
echo "  解决方案:    $SolutionFull"
echo "  deploy 目录:  $DeployDir"
if [[ -n "$Version" ]]; then
  echo "  版本覆盖:    $Version"
fi
echo ""

dotnet_major="$($DOTNET --version | cut -d. -f1)"
if [[ "$dotnet_major" != "8" ]]; then
  echo "[警告] 项目目标 net8.0，但 dotnet SDK 主版本是 $dotnet_major。Build 可能失败。" >&2
fi

# ======================== Step 1: restore ========================
# -r $Runtime 必须传: 触发 NuGet 写入 RID-specific assets (NETSDK1047 的关键)
echo "[1/4] dotnet restore (-r $Runtime)"
"$DOTNET" restore "$SolutionFull" -r "$Runtime"
if [[ $? -ne 0 ]]; then
  echo "[错误] dotnet restore 失败" >&2
  exit 1
fi
echo ""

# ======================== Step 2: build ========================
# 不能在 sln 级传 -r，会触发 NETSDK1134。Step 1 的 restore 已经准备好 assets。
echo "[2/4] dotnet build ($Configuration)"
"$DOTNET" build "$SolutionFull" -c "$Configuration" --no-restore
if [[ $? -ne 0 ]]; then
  echo "[错误] dotnet build 失败" >&2
  exit 1
fi
echo ""

# ======================== Step 3: 清空 deploy/ ========================
echo "[3/4] 清理 $DeployDir"
if [[ -d "$DeployDir" ]]; then
  rm -rf "$DeployDir"
fi
mkdir -p "$DeployDir"
echo ""

# ======================== Step 4: publish ========================
# Publish-time 属性。CLI 传入而不是写到 Directory.Build.props，
# 以避免类库项目（MinioCommon）被强制指定 RID。
# ----- 根据 RID 自动选择 PlatformTarget -----
# Directory.Build.props 里固定写了 <PlatformTarget>x64</PlatformTarget>，
# 那是面向 Windows x64 的旧设定。发布到非 x64 RID（osx-arm64 / linux-arm64）
# 会触发 NETSDK1032 (PlatformTarget x64 跟 osx-arm64 不兼容)。
# 所以按 RID 显式覆盖：
case "$Runtime" in
  *-arm64)     platform_target="arm64" ;;
  *-x64|*x64)  platform_target="x64"   ;;
  *-x86|*x86)  platform_target="x86"   ;;
  *-arm|*arm)  platform_target="arm"   ;;
  *)           platform_target=""      ;;  # 不传则用项目默认
esac

PublishProps=(
  "-p:SelfContained=true"
  "-p:PublishSingleFile=true"
  "-p:EnableCompressionInSingleFile=true"
  "-p:IncludeNativeLibrariesForSelfExtract=true"
  "-p:PublishReadyToRun=false"      # 守护进程启动频率低，省 ~30MB
  "-p:PublishTrimmed=false"         # Minio SDK 未标 IsTrimmable，trim 会崩
)
if [[ -n "$platform_target" ]]; then
  PublishProps+=("-p:PlatformTarget=$platform_target")
fi
if [[ -n "$Version" ]]; then
  PublishProps+=("-p:Version=$Version")
fi

# 注意：不要传 --no-build！PublishSingleFile 需要 RID-specific 中间产物
# (obj/Release/net8.0/<RID>/singlefilehost.exe) 才能成功打包。
# 项目名 → 输出 EXE 短名 (与 publish.ps1 完全一致)
# 注：macOS 默认是 bash 3.2，不支持关联数组 (declare -A)，
# 所以改用两个平行索引数组 + 一个 lookup 函数。
EXE_PROJECTS=("MinioSync" "FullSync" "SyncWorker" "Minio2MinioSync")
EXE_SHORTNAMES=("mms" "mfs" "mss" "m2ms")

# 通过项目名查短名，找不到时回退为项目名小写
short_name_for() {
  local proj="$1"
  for i in "${!EXE_PROJECTS[@]}"; do
    if [[ "${EXE_PROJECTS[$i]}" == "$proj" ]]; then
      echo "${EXE_SHORTNAMES[$i]}"
      return
    fi
  done
  echo "$proj" | tr '[:upper:]' '[:lower:]'
}

echo "[4/4] 发布 self-contained 单文件 EXE"
for proj in "${EXE_PROJECTS[@]}"; do
  csproj="$SCRIPT_DIR/$proj/$proj.csproj"
  if [[ ! -f "$csproj" ]]; then
    echo "[错误] 找不到 csproj: $csproj" >&2
    exit 1
  fi
  echo "  -> $proj (-r $Runtime, self-contained, single-file)"
  "$DOTNET" publish "$csproj" \
    --configuration "$Configuration" \
    --runtime "$Runtime" \
    --output "$DeployDir" \
    "${PublishProps[@]}"
  if [[ $? -ne 0 ]]; then
    echo "[错误] dotnet publish $proj 失败" >&2
    exit 1
  fi
done
echo ""

# ======================== 重命名 EXE ========================
echo "[4.5/5] 重命名发布的 EXE"
for proj in "${EXE_PROJECTS[@]}"; do
  short="$(short_name_for "$proj")"
  src="$DeployDir/$proj.exe"
  dst="$DeployDir/${short}.exe"
  if [[ -n "$short" && -f "$src" ]]; then
    mv -f "$src" "$dst"
    echo "  $proj.exe → $(basename "$dst")"
  else
    # osx-x64 的 EXE 可能是 unix 可执行文件（无 .exe 后缀），回退到无扩展名
    src_no_ext="$DeployDir/$proj"
    if [[ -f "$src_no_ext" ]]; then
      mv -f "$src_no_ext" "$dst"
      echo "  $proj → $(basename "$dst")"
    else
      echo "[警告] 找不到发布产物 $src（或 $src_no_ext）" >&2
    fi
  fi
done

# ======================== 拷贝 config.json ========================
sample_config="$SCRIPT_DIR/config.json"
if [[ -f "$sample_config" ]]; then
  cp -f "$sample_config" "$DeployDir/"
  echo ""
  echo "  已拷贝 config.json 到 deploy/"
fi

# ======================== 摘要 ========================
echo ""
echo "============================================================"
echo "发布完成。deploy 目录: $DeployDir"
echo "============================================================"
echo ""
echo "deploy/ 文件："
# 等价 PowerShell 的 "{0,8:N2} MB"
for f in "$DeployDir"/*; do
  if [[ -f "$f" ]]; then
    name="$(basename "$f")"
    size_bytes=$(stat -f%z "$f" 2>/dev/null || stat -c%s "$f")
    size_mb=$(awk -v b="$size_bytes" 'BEGIN { printf "%.2f", b/1048576 }')
    printf "  %8s MB  %s\n" "$size_mb" "$name"
  fi
done
echo ""
echo "分发方式：把整个 deploy/ 目录复制到目标机器即可。"
echo "目标机器要求：无需任何 .NET runtime（已打包进 EXE）。"
