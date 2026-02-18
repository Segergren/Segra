#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo "=== Building Frontend ==="
(cd Frontend && npm run build)

echo "=== Copying Frontend to wwwroot ==="
rm -rf wwwroot
mkdir wwwroot
cp -r Frontend/dist/* wwwroot/

echo "=== Publishing Backend ==="
dotnet publish -c Release --self-contained -r win-x64 -o publish

echo ""
echo "=== Done! ==="
WIN_DIR=$(echo "$SCRIPT_DIR" | sed 's|^/\([a-zA-Z]\)/|\1:/|' | sed 's|/|\\|g')
echo "Output: $WIN_DIR\\publish\\"
echo "Executable: $WIN_DIR\\publish\\Segra.exe"
echo ""
read -n 1 -s -r -p "Press any key to exit..."
