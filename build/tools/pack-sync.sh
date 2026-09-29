#!/bin/sh
# Empaquette le dépôt (sans bin/obj/artifacts) pour synchronisation vers le poste Windows.
# README.md et .gitignore ne sont jamais inclus : les versions du poste sont celles publiées sur GitHub.
# Le manifeste (pcboost-sync-manifest.txt, hors du dossier PCBoost) liste les fichiers synchronisés ;
# apply-sync.sh écarte uniquement les fichiers d'une synchronisation précédente qui n'existent plus.
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
find PCBoost -type d \( -name bin -o -name obj -o -name artifacts -o -name TestResults -o -name .vs \) -prune -o -type f -print \
    | grep -v -x -e 'PCBoost/README.md' -e 'PCBoost/.gitignore' | sort > pcboost-sync-manifest.txt
tar --exclude=bin --exclude=obj --exclude='artifacts' --exclude='TestResults' --exclude='.vs' \
    --exclude='PCBoost/README.md' --exclude='PCBoost/.gitignore' -czf "$OUT" PCBoost pcboost-sync-manifest.txt
rm -f pcboost-sync-manifest.txt
ls -la "$OUT"
