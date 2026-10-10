#!/usr/bin/env bash
# mirror_release.sh —— 把一版客户端包放到玩家下得到的两处：公开发布仓库 + 官网 /download/
#
# 用法（仓库根目录）：
#   tools/mirror_release.sh client-2026-10-12     # publish_release.sh 发完私有仓库会自己调它
#   DRY=1 tools/mirror_release.sh client-x         # 只核本机包、gh 登录与服务器写权限，不真发
#
# 为什么有这一步：2026-10-10 代码仓库改成私有，私有仓库的 Release 玩家打不开。
# 那份照发、当存档（包和提交绑在一起）；玩家下载改走两处 ——
#   · 公开仓库 Kevinnb66699/Cell-War-Releases（只放安装包，不放代码），官网 GitHub 按钮指 releases/latest/download/
#   · 官网本站 https://cellwar.jiling.chat/download/（国内连 GitHub 慢时用这个）
# 游戏里「请到 cellwar.jiling.chat 下载新客户端」也落到这两处。
#
# 为什么单拎一个脚本：国内连 GitHub 常断（2026-09-09 gh 报 EOF 退出码却是 0）。私有仓库发完、这里断了的话，
# publish_release.sh 已经发不了第二次（tag 已占用）—— 单独重跑这个脚本补发。每一步都能重跑：
# 公开仓库已有这个 tag 就覆盖上传，官网 rsync 先写临时文件再改名（正在下载的人拿不到半个文件）。
#
# 发之前核：本机两个包和私有仓库那份存档逐字节一样（比 GitHub 给的 sha256），发的就是存档的那一版。
# 发完核：公开仓库 latest 指向这个 tag、两处下载地址的大小都对，官网再在服务器上比一遍 sha256。
set -eu

cd "$(dirname "$0")/.."
DRY="${DRY:-0}"
TAG="${1:-}"
WIN="dist/win/CellWar.exe"
MAC="dist/mac/CellWar.zip"
PUBLIC_REPO="${PUBLIC_REPO:-Kevinnb66699/Cell-War-Releases}"
SITE_HOST="${SITE_HOST:-cellwar}"     # ssh 别名
SITE_DIR=/var/www/cellwar/download   # nginx 的 /download/ 指到这里（官网文件夹 _deploy/cellwar.conf）
SITE_URL=https://cellwar.jiling.chat/download

die() { echo "✘ $1" >&2; exit 1; }
sha_of() { shasum -a 256 "$1" | cut -d' ' -f1; }
size_of() { stat -c %s "$1" 2>/dev/null || stat -f %z "$1"; }
has_both() {   # $1 = gh 列出的「资源名 sha256:…」行 → 两个包都在、且和本机逐字节一样
	echo "$1" | grep -qx "CellWar.exe sha256:$WIN_SHA" && echo "$1" | grep -qx "CellWar.zip sha256:$MAC_SHA"
}
asset_digests() {   # gh release view 的额外参数（-R …）→ 「资源名 sha256:…」行
	gh release view "$TAG" "$@" --json assets -q '.assets[] | .name+" "+(.digest // "")'
}
remote_size() {   # $1 = 下载地址 → 跟完跳转后最后一跳的 Content-Length（取不到就空）
	curl -sIL --max-time 120 --retry 3 "$1" | tr -d '\r' | awk 'tolower($1)=="content-length:"{n=$2} END{print n}'
}

[ -n "$TAG" ] || die "要给 tag：tools/mirror_release.sh client-YYYY-MM-DD"
for f in "$WIN" "$MAC"; do
	[ -f "$f" ] || die "找不到 $f"
done
command -v gh >/dev/null 2>&1 || die "没装 gh：装好并 gh auth login 之后再跑（登录那步要你自己来）"
WIN_SHA="$(sha_of "$WIN")"
MAC_SHA="$(sha_of "$MAC")"

if [ "$DRY" = "1" ]; then
	gh auth status >/dev/null 2>&1 || die "gh 没登录（gh auth login，要你自己来）"
	ssh -o BatchMode=yes "$SITE_HOST" "test -w $SITE_DIR" || die "$SITE_HOST:$SITE_DIR 连不上或写不进"
	echo "✔ 镜像前检查通过（DRY=1，没真发）：gh 已登录、$SITE_HOST:$SITE_DIR 可写"
	exit 0
fi

# ---- 发的必须是存档的那一版 ----
ARCHIVED="$(asset_digests)" || die "私有仓库没有 $TAG 这个 Release：先跑 tools/publish_release.sh"
has_both "$ARCHIVED" || die "本机的包和私有仓库 $TAG 里那份不一样（或那份没有 sha256）：别把另一版的包发给玩家"

# ---- ① 公开仓库 ----
# mac 只支持 Apple 芯片：sidecar 只带 osx-arm64 的 .NET 运行时（Kevin 2026-10-09 定，官网也这么写）。
# ⚠ 别写「已签名」，理由见 publish_release.sh 生成 NOTES 那一段。
PUBLIC_NOTES="$(printf 'Cell War 客户端（%s）。\n\n- **Windows**：CellWar.exe —— 首次打开如果弹「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」。\n- **macOS**（只支持 Apple 芯片的 Mac）：CellWar.zip —— 解压后右键 Cell War.app →「打开」；被拦就去「系统设置 → 隐私与安全性」点「仍要打开」。\n\n官网（网页试玩 + 下载）：https://cellwar.jiling.chat/\n\nSHA-256\n- CellWar.exe `%s`\n- CellWar.zip `%s`' "$TAG" "$WIN_SHA" "$MAC_SHA")"
publish_public() {
	local on_public
	if ! on_public="$(asset_digests -R "$PUBLIC_REPO" 2>/dev/null)"; then
		gh release create "$TAG" "$WIN" "$MAC" -R "$PUBLIC_REPO" --target main --title "$TAG" --notes "$PUBLIC_NOTES"
	elif has_both "$on_public"; then
		# 重跑时上一次已经传完：--clobber 是先删再传，传的那十几分钟里 GitHub 下载是 404，别白删
		echo "  公开仓库已有这两个包（sha256 一样），不重传"
	else
		gh release upload "$TAG" "$WIN" "$MAC" -R "$PUBLIC_REPO" --clobber
	fi
}
for i in 1 2 3; do
	publish_public && break
	[ "$i" = 3 ] && die "公开仓库连发三次都失败：网络好了重跑 tools/mirror_release.sh $TAG"
	echo "⚠ 公开仓库第 $i 次没发成，10 秒后重试"; sleep 10
done

# ---- ② 官网 /download/ ----
# 权限在服务器上 chmod：macOS 自带的 openrsync 不认 --chmod（2026-10-10 真跑时撞上）
export COPYFILE_DISABLE=1
rsync -t -e "ssh -o BatchMode=yes" "$WIN" "$MAC" "$SITE_HOST:$SITE_DIR/" \
	|| die "传官网失败：重跑 tools/mirror_release.sh $TAG（公开仓库那步已传完的不会重传）"
ON_SITE="$(ssh -o BatchMode=yes "$SITE_HOST" "cd $SITE_DIR && chmod 644 CellWar.exe CellWar.zip && sha256sum CellWar.exe CellWar.zip")"
echo "$ON_SITE" | grep -q "^$WIN_SHA  CellWar.exe$" && echo "$ON_SITE" | grep -q "^$MAC_SHA  CellWar.zip$" \
	|| die "官网上的包和本机 sha256 不一样：$ON_SITE"

# ---- 发完核：玩家点下载拿到的就是这一版 ----
LATEST="$(gh release view -R "$PUBLIC_REPO" --json tagName -q .tagName)"
[ "$LATEST" = "$TAG" ] || die "公开仓库的 latest 是 $LATEST 不是 $TAG：官网 GitHub 按钮会下到那一版"
fail=0
for f in "$WIN" "$MAC"; do
	name="$(basename "$f")"
	want="$(size_of "$f")"
	for url in "https://github.com/$PUBLIC_REPO/releases/latest/download/$name" "$SITE_URL/$name"; do
		got="$(remote_size "$url")"
		if [ "$got" = "$want" ]; then echo "  ✔ $url"; else echo "  ✘ $url（$got ≠ $want）"; fail=1; fi
	done
done
[ "$fail" = 0 ] || die "有下载地址拿到的不是这一版，见上"
echo "✔ 玩家下载两处都是 $TAG：公开仓库 + 官网 /download/"
echo "  官网页面上的版本号与包大小要手改（官网文件夹 README 的「发版当天」清单）"
