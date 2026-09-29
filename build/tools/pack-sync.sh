#!/bin/sh
# Empaquette le dépôt (sans bin/obj/artifacts) pour synchronisation vers le poste Windows.
set -e
OUT=${1:-/mnt/user-data/outputs/pcboost-sync.tar.gz}
cd /home/claude
# Garde-fou : XAML bien formé (le compilateur XAML ne tourne que sous Windows).
python3 - <<'PY'
import glob, sys, xml.dom.minidom
bad = []
for f in glob.glob('PCBoost/src/**/*.xaml', recursive=True):
    if '/obj/' in f or '/bin/' in f: continue
    try: xml.dom.minidom.parse(f)
    except Exception as e: bad.append(f"{f}: {e}")
if bad:
    print("\n".join(bad)); sys.exit(1)
PY
tar --exclude=bin --exclude=obj --exclude='artifacts' --exclude='TestResults' --exclude='.vs' -czf "$OUT" PCBoost
ls -la "$OUT"
