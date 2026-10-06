#!/bin/sh
# Builds the bridge and the demo inside the xulj-mono image (Mono 6.12 + Xvfb).
# The browser client is embedded from the xul-j base repo: XULJ_BASE (default ../xul-j).
# On Windows the same sources build with the .NET Framework csc; see the flags below.
set -e
cd "$(dirname "$0")"
BASE="$(cd "${XULJ_BASE:-../xul-j}" && pwd -P)"
[ -f "$BASE/public/xulj.js" ] || { echo "xul-j base not found at $BASE (set XULJ_BASE)"; exit 1; }
mkdir -p out lib
HARMONY_VERSION=2.3.3
if [ ! -f lib/0Harmony.dll ]; then
  echo "fetching Lib.Harmony $HARMONY_VERSION (MIT) from nuget.org"
  curl -sSfL -o lib/harmony.nupkg "https://www.nuget.org/api/v2/package/Lib.Harmony/$HARMONY_VERSION"
  python3 -c "import zipfile; z=zipfile.ZipFile('lib/harmony.nupkg'); open('lib/0Harmony.dll','wb').write(z.read('lib/net472/0Harmony.dll'))"
fi
cp lib/0Harmony.dll out/
docker run --rm -v "$PWD":/src -v "$BASE/public":/client:ro -w /src xulj-mono sh -c '
  set -e
  csc -nologo -warn:4 -langversion:7.3 -target:exe -out:out/xulj-host.exe \
    -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Net.dll -r:lib/0Harmony.dll \
    -resource:/client/index.html,public/index.html \
    -resource:/client/xulj.js,public/xulj.js \
    -resource:/client/xul.css,public/xul.css \
    src/*.cs
  csc -nologo -warn:4 -langversion:7.3 -target:winexe -out:out/LegacyOrders.exe \
    -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:Microsoft.VisualBasic.dll demo/LegacyOrders.cs
'
ls -la out
