#!/usr/bin/env sh
# Builds dist/ps1tl (Linux). Needs python3 (3.10+) with pip; on Debian/Ubuntu also: sudo apt install libxcb-cursor0
set -e
cd "$(dirname "$0")"
python3 -m pip install -r requirements.txt pyinstaller
python3 -m PyInstaller --noconfirm --onefile --windowed --name ps1tl --add-data "ps1tl/fonts:ps1tl/fonts" ps1tl_app.py
echo "Built dist/ps1tl"
