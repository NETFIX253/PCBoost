#!/usr/bin/env python3
"""
Génère les fichiers .resx (fr neutre + en) à partir d'une source JSON unique :
  { "Cle": { "fr": "Texte", "en": "Text" }, ... }
Usage : python3 build/tools/i18n.py path/to/Strings.i18n.json
Produit Strings.resx (fr) et Strings.en.resx à côté du JSON. Échoue si une traduction manque.
"""
import json, sys, os
from xml.sax.saxutils import escape

HEADER = '''<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
'''

def write(path, entries):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER)
        for k in sorted(entries):
            f.write(f'  <data name="{escape(k)}" xml:space="preserve"><value>{escape(entries[k])}</value></data>\n')
        f.write("</root>\n")

def main(src):
    with open(src, encoding="utf-8") as f:
        data = json.load(f)
    base = src[:-len(".i18n.json")]
    fr, en, missing = {}, {}, []
    for key, tr in data.items():
        if key.startswith("$"):
            continue
        if not isinstance(tr, dict) or not tr.get("fr") or not tr.get("en"):
            missing.append(key); continue
        fr[key], en[key] = tr["fr"], tr["en"]
    if missing:
        print("Traductions manquantes :", ", ".join(missing)); sys.exit(1)
    write(base + ".resx", fr)
    write(base + ".en.resx", en)
    print(f"{os.path.basename(base)}: {len(fr)} clés -> .resx / .en.resx")

if __name__ == "__main__":
    for p in sys.argv[1:]:
        main(p)
