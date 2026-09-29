#!/usr/bin/env python3
"""
Identité visuelle de PCBoost : la « tuile propulsée ».

Une grille de quatre tuiles (les fenêtres du PC) dont la quatrième, indigo, décolle vers le haut à droite en
laissant deux traînées : le PC qui retrouve de l'élan. Sarcelle = couleur « Fluide » de l'interface ;
indigo = couleur de la session Gaming.

Une seule géométrie produit tous les fichiers :
  build/brand/pcboost-mark.svg              marque seule (fichier vectoriel maître)
  build/brand/pcboost-logo.svg              logo vertical, fond clair
  build/brand/pcboost-logo-dark.svg         logo vertical, fond sombre
  build/brand/pcboost-logo-horizontal.svg   logo horizontal, fond clair
  build/brand/png/*.png                     les mêmes en PNG transparent, 1024 px de large
  src/PCBoost.App/Assets/Branding/app.ico   icône 16 à 256 px (petites tailles alignées sur la grille de pixels)
  src/PCBoost.App/Assets/Branding/logo-256.png, logo-64.png, logo-16/20/24/32.png (barre de titre)
  installer/PCBoost.Installer/Assets/banner.bmp (493 x 58) et dialog.bmp (493 x 312)

Le mot-symbole « PCBOOST » est lu dans build/brand/wordmark.svg (glyphes vectorisés de Barlow Semi Condensed
ExtraBold, licence SIL OFL 1.1). Pour le régénérer (autre nom de produit) :
  python3 build/tools/make_brand_assets.py --wordmark-font BarlowSemiCondensed-ExtraBold.ttf --wordmark-split 2 PCBOOST

Dépendances : pip install cairosvg pillow   (mot-symbole : + fonttools uharfbuzz)
Usage : python3 build/tools/make_brand_assets.py
"""
import argparse
import io
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BRAND_DIR = os.path.join(ROOT, "build", "brand")
APP_DIR = os.path.join(ROOT, "src", "PCBoost.App", "Assets", "Branding")
INSTALLER_DIR = os.path.join(ROOT, "installer", "PCBoost.Installer", "Assets")
WORDMARK = os.path.join(BRAND_DIR, "wordmark.svg")

# Couleurs (reprises de src/PCBoost.App/Styles/Colors.xaml).
TEAL = "#11919A"          # tuiles : lisible sur fond clair comme sur fond sombre
TEAL_DARK = "#0B6E75"     # « PC » sur fond clair (BrandAccentColor, thème clair)
INDIGO = "#5B55E6"        # tuile propulsée, « BOOST » sur fond clair
TRAIL = "#8B87F0"         # traînées : indigo éclairci, visible sur les deux fonds
ON_DARK = "#E8F7F8"       # « PC » sur fond sombre
INDIGO_ON_DARK = "#A9A6F7"  # « BOOST » sur fond sombre

# Géométrie maîtresse (boîte 100 x 100) : tuile 38, espace 8, décollage 8, rayon 5,5.
MASTER = dict(n=100.0, t=38.0, g=8.0, d=8.0, ox=4.0, oy=4.0)

# Petites tailles : valeurs entières (bords nets). n: (tuile, espace, décollage).
PIXEL_GRID = {16: (6, 1, 2), 20: (8, 1, 2), 24: (9, 2, 2), 32: (12, 2, 3), 40: (15, 3, 3), 48: (18, 3, 4), 64: (24, 5, 5)}
TRAILS_FROM = 48          # en dessous, les traînées ne sont plus que du bruit : marque simplifiée
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]


def fmt(v):
    return f"{v:.2f}".rstrip("0").rstrip(".")


def mark_shapes(n, t, g, d, ox, oy, trails=True):
    """Éléments SVG de la marque dans une boîte n x n. (ox, oy) : coin haut-gauche de l'encombrement."""
    r = t * 0.145
    base_y = oy + d                      # la rangée du haut commence sous la tuile décollée
    tiles = [
        (ox, base_y), (ox, base_y + t + g), (ox + t + g, base_y + t + g),
    ]
    out = [f'<rect x="{fmt(x)}" y="{fmt(y)}" width="{fmt(t)}" height="{fmt(t)}" rx="{fmt(r)}" fill="{TEAL}"/>' for x, y in tiles]
    lx, ly = ox + t + g + d, oy          # tuile propulsée
    if trails:
        # Deux traînées parallèles au déplacement (haut-droite), symétriques de part et d'autre de la diagonale
        # qui passe par le coin bas-gauche de la tuile, détachées de celle-ci.
        kx, ky = lx, ly + t
        s = 0.70710678
        p, a0, a1, w = t * 0.184, t * 0.053, t * 0.237, t * 0.09
        for side in (-1, 1):
            cx, cy = kx + side * p * s, ky + side * p * s
            x1, y1 = cx - a0 * s, cy + a0 * s
            x2, y2 = cx - a1 * s, cy + a1 * s
            out.append(f'<line x1="{fmt(x1)}" y1="{fmt(y1)}" x2="{fmt(x2)}" y2="{fmt(y2)}" stroke="{TRAIL}" '
                       f'stroke-width="{fmt(w)}" stroke-linecap="round"/>')
    out.append(f'<rect x="{fmt(lx)}" y="{fmt(ly)}" width="{fmt(t)}" height="{fmt(t)}" rx="{fmt(r)}" fill="{INDIGO}"/>')
    return "".join(out)


def master_shapes(trails=True):
    m = MASTER
    return mark_shapes(m["n"], m["t"], m["g"], m["d"], m["ox"], m["oy"], trails)


def pixel_shapes(n):
    t, g, d = PIXEL_GRID[n]
    w = 2 * t + g + d
    ox, oy = (n - w) // 2, (n - w + 1) // 2
    return mark_shapes(n, t, g, d, ox, oy, trails=n >= TRAILS_FROM)


def svg(width, height, body, view=None):
    view = view or f"0 0 {fmt(width)} {fmt(height)}"
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{fmt(width)}" height="{fmt(height)}" viewBox="{view}">'
            f"{body}</svg>")


def icon_svg(n):
    """Icône de n pixels : grille de pixels jusqu'à 64 px, géométrie maîtresse au-delà."""
    if n in PIXEL_GRID:
        return svg(n, n, pixel_shapes(n))
    return svg(n, n, master_shapes(), view="0 0 100 100")


# ---------- Mot-symbole ----------

def build_wordmark(font_path, text, split):
    """Vectorise le texte (mise en forme HarfBuzz : crénage compris) ; deux chemins : text[:split] et text[split:]."""
    import uharfbuzz as hb
    from fontTools.ttLib import TTFont
    from fontTools.pens.svgPathPen import SVGPathPen
    from fontTools.pens.transformPen import TransformPen

    font = TTFont(font_path)
    glyphs = font.getGlyphSet()
    upm = font["head"].unitsPerEm
    cap = font["OS/2"].sCapHeight
    blob = hb.Blob.from_file_path(font_path)
    hbfont = hb.Font(hb.Face(blob))
    buf = hb.Buffer()
    buf.add_str(text)
    buf.guess_segment_properties()
    hb.shape(hbfont, buf, {"kern": True, "liga": False})
    tracking = upm * 0.03
    names = font.getGlyphOrder()
    pens = [SVGPathPen(glyphs), SVGPathPen(glyphs)]
    x = 0.0
    for i, (info, pos) in enumerate(zip(buf.glyph_infos, buf.glyph_positions)):
        pen = pens[0 if info.cluster < split else 1]
        glyphs[names[info.codepoint]].draw(TransformPen(pen, (1, 0, 0, -1, x + pos.x_offset, cap - pos.y_offset)))
        x += pos.x_advance + (tracking if i < len(buf.glyph_infos) - 1 else 0)
    width = x
    body = (f'<path id="first" d="{pens[0].getCommands()}"/>'
            f'<path id="second" d="{pens[1].getCommands()}"/>')
    os.makedirs(BRAND_DIR, exist_ok=True)
    with open(WORDMARK, "w", encoding="utf-8") as fh:
        fh.write(f"<!-- Mot-symbole « {text} » : Barlow Semi Condensed ExtraBold (SIL OFL 1.1), glyphes vectorisés, "
                 f"hauteur = hauteur des capitales. Régénéré par build/tools/make_brand_assets.py --wordmark-font. -->\n")
        fh.write(svg(width, cap, body) + "\n")
    print("Mot-symbole écrit :", os.path.relpath(WORDMARK, ROOT))


def load_wordmark():
    with open(WORDMARK, encoding="utf-8") as fh:
        text = fh.read()
    w, h = (float(v) for v in re.search(r'viewBox="0 0 ([\d.]+) ([\d.]+)"', text).groups())
    first = re.search(r'<path id="first" d="([^"]*)"', text).group(1)
    second = re.search(r'<path id="second" d="([^"]*)"', text).group(1)
    return w, h, first, second


def wordmark_group(x, y, cap_height, first_color, second_color):
    w, h, first, second = load_wordmark()
    k = cap_height / h
    return (w * k, f'<g transform="translate({fmt(x)} {fmt(y)}) scale({k:.6f})">'
                   f'<path d="{first}" fill="{first_color}"/><path d="{second}" fill="{second_color}"/></g>')


# ---------- Logos complets ----------

MARK_BOX = (4.0, 4.0, 92.0, 92.0)   # encombrement réel de la marque maîtresse dans sa boîte 100


def mark_group(x, y, size):
    bx, by, bw, _ = MARK_BOX
    k = size / bw
    return f'<g transform="translate({fmt(x)} {fmt(y)}) scale({k:.6f}) translate({-bx} {-by})">{master_shapes()}</g>'


def logo_vertical(dark=False):
    mark, cap, gap = 120.0, 30.0, 22.0
    ww, _, _, _ = load_wordmark()
    word_w = ww * cap / load_wordmark()[1]
    width = max(mark, word_w)
    first, second = (ON_DARK, INDIGO_ON_DARK) if dark else (TEAL_DARK, INDIGO)
    _, word = wordmark_group((width - word_w) / 2, mark + gap, cap, first, second)
    return svg(width, mark + gap + cap, mark_group((width - mark) / 2, 0, mark) + word)


def logo_horizontal():
    mark, cap, gap = 64.0, 26.0, 16.0
    word_w, word = wordmark_group(mark + gap, (mark - cap) / 2, cap, TEAL_DARK, INDIGO)
    return svg(mark + gap + word_w, mark, mark_group(0, 0, mark) + word)


# ---------- Rendu ----------

def render(svg_text, width, height=None):
    import cairosvg
    from PIL import Image
    png = cairosvg.svg2png(bytestring=svg_text.encode("utf-8"), output_width=width, output_height=height or width)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def installer_bitmaps():
    """Images de l'assistant d'installation WiX (24 bits, sans transparence)."""
    from PIL import Image
    os.makedirs(INSTALLER_DIR, exist_ok=True)
    # Bandeau des pages intermédiaires : titre à gauche (dessiné par Windows Installer), marque à droite.
    banner = Image.new("RGB", (493, 58), (255, 255, 255))
    m = render(svg(40, 40, mark_group(0, 0, 40)), 40)
    banner.paste(m, (493 - 40 - 12, 9), m)
    banner.save(os.path.join(INSTALLER_DIR, "banner.bmp"))
    # Pages d'accueil et de fin : panneau gauche teinté avec le logo vertical ; texte à droite (Windows Installer).
    dialog = Image.new("RGB", (493, 312), (255, 255, 255))
    panel = Image.new("RGB", (164, 312), (236, 246, 247))
    dialog.paste(panel, (0, 0))
    logo = logo_vertical()
    w, h = (float(v) for v in re.search(r'viewBox="0 0 ([\d.]+) ([\d.]+)"', logo).groups())
    lw = 112
    lh = round(lw * h / w)
    img = render(logo, lw, lh)
    dialog.paste(img, ((164 - lw) // 2, (312 - lh) // 2 - 12), img)
    dialog.save(os.path.join(INSTALLER_DIR, "dialog.bmp"))


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    parser.add_argument("--wordmark-font", help="police TTF pour régénérer le mot-symbole")
    parser.add_argument("--wordmark-split", type=int, default=2, help="nombre de lettres de la première couleur")
    parser.add_argument("text", nargs="?", default="PCBOOST")
    args = parser.parse_args()
    if args.wordmark_font:
        build_wordmark(args.wordmark_font, args.text, args.wordmark_split)

    os.makedirs(BRAND_DIR, exist_ok=True)
    files = {
        "pcboost-mark.svg": svg(512, 512, master_shapes(), view="0 0 100 100"),
        "pcboost-logo.svg": logo_vertical(),
        "pcboost-logo-dark.svg": logo_vertical(dark=True),
        "pcboost-logo-horizontal.svg": logo_horizontal(),
    }
    png_dir = os.path.join(BRAND_DIR, "png")
    os.makedirs(png_dir, exist_ok=True)
    for name, text in files.items():
        with open(os.path.join(BRAND_DIR, name), "w", encoding="utf-8") as fh:
            fh.write(text + "\n")
        # Exports PNG transparents (documents, présentations) : 1024 px de large.
        w, h = (float(v) for v in re.search(r'viewBox="0 0 ([\d.]+) ([\d.]+)"', text).groups())
        render(text, 1024, round(1024 * h / w)).save(os.path.join(png_dir, name.replace(".svg", ".png")))

    os.makedirs(APP_DIR, exist_ok=True)
    frames = {n: render(icon_svg(n), n) for n in ICO_SIZES}
    # Pillow écrit chaque taille à partir de l'image fournie la plus proche : on lui passe les rendus dédiés.
    frames[256].save(os.path.join(APP_DIR, "app.ico"), format="ICO", sizes=[(n, n) for n in ICO_SIZES],
                     append_images=[frames[n] for n in ICO_SIZES if n != 256])
    frames[256].save(os.path.join(APP_DIR, "logo-256.png"))
    frames[64].save(os.path.join(APP_DIR, "logo-64.png"))
    # Barre de titre (16 DIP) : une image par mise à l'échelle courante (100, 125, 150, 200 %), sans rééchantillonnage.
    for n in (16, 20, 24, 32):
        frames[n].save(os.path.join(APP_DIR, f"logo-{n}.png"))
    installer_bitmaps()
    print("Identité visuelle générée :")
    for d in (BRAND_DIR, APP_DIR, INSTALLER_DIR):
        print("  ", os.path.relpath(d, ROOT))


if __name__ == "__main__":
    sys.exit(main())
