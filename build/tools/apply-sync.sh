#!/bin/sh
# À lancer depuis le dossier parent de PCBoost, après extraction d'une archive de synchronisation.
# Déplace dans _to_delete/obsolete-<date>/ les fichiers présents dans le manifeste précédent mais absents du nouveau
# (fichiers renommés ou supprimés côté développement). Les fichiers jamais synchronisés (README.md, .gitignore, LICENSE,
# ajouts manuels) ne sont jamais touchés. Rien n'est supprimé.
set -e
new=pcboost-sync-manifest.txt
old=_sync/last-manifest.txt
[ -f "$new" ] || { echo "Manifeste absent : rien à faire."; exit 0; }
mkdir -p _sync
if [ -f "$old" ]; then
    stamp=$(date +%Y%m%d-%H%M%S)
    sort "$old" > /tmp/pcboost-old.txt
    sort "$new" > /tmp/pcboost-new.txt
    comm -23 /tmp/pcboost-old.txt /tmp/pcboost-new.txt | while IFS= read -r f; do
        case "$f" in PCBoost/README.md|PCBoost/.gitignore|PCBoost/LICENSE) continue ;; esac
        [ -f "$f" ] || continue
        dest="_to_delete/obsolete-$stamp/$f"
        mkdir -p "$(dirname "$dest")"
        mv -n "$f" "$dest" && echo "écarté : $f"
    done
    rm -f /tmp/pcboost-old.txt /tmp/pcboost-new.txt
fi
mv -f "$new" "$old"
