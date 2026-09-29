---
name: PCBoost
description: Native Windows 11 Fluent optimizer, built as a measuring instrument with an undo button.
colors:
  accent-ramp-dark3: "#053A40"
  accent-ramp-dark2: "#07525A"
  accent-ramp-dark1: "#0B6E75"
  accent-ramp-base: "#11919A"
  accent-ramp-light1: "#3FB3BA"
  accent-ramp-light2: "#6CCBD0"
  accent-ramp-light3: "#A2E1E4"
  fluide-teal: "#0B6E75"
  fluide-teal-text: "#07525A"
  fluide-teal-subtle: "#0B6E7514"
  fluide-teal-track: "#0B6E751F"
  fluide-teal-dark: "#5FC4C9"
  fluide-teal-text-dark: "#8FD9DD"
  fluide-teal-subtle-dark: "#5FC4C91F"
  fluide-teal-track-dark: "#5FC4C92E"
  chart-fill: "#0B6E751A"
  chart-grid: "#00000014"
  chart-fill-dark: "#5FC4C924"
  chart-grid-dark: "#FFFFFF1A"
  session-indigo: "#34318A"
  session-indigo-dark: "#2D2A80"
  session-on-band: "#FFFFFF"
  session-secondary-text: "#D9D7FF"
  session-secondary-text-dark: "#CFCDFF"
  session-divider: "#FFFFFF40"
  session-divider-dark: "#FFFFFF33"
  session-accent-text-dark: "#B5B3FA"
  session-chart-line-dark: "#A9A6F7"
  session-button-bg: "#FFFFFF"
  session-button-bg-hover: "#EDEDFA"
  session-button-bg-pressed: "#DCDAF5"
  session-button-fg: "#2A2780"
  session-button-bg-dark: "#ECEBFF"
  session-button-bg-hover-dark: "#DCDAFF"
  session-button-bg-pressed-dark: "#C9C7FF"
  session-button-fg-dark: "#1F1C66"
  session-ghost-hover: "#FFFFFF1F"
  session-ghost-pressed: "#FFFFFF33"
  danger: "#C42B1C"
  danger-hover: "#B02718"
  danger-pressed: "#9A2215"
  danger-dark: "#D13438"
  danger-hover-dark: "#BF2E32"
  danger-pressed-dark: "#A8282B"
  on-danger: "#FFFFFF"
typography:
  score-numeral:
    fontFamily: "Segoe UI Variable Display, Segoe UI"
    fontSize: "72px"
    fontWeight: 600
    lineHeight: "76px"
  session-numeral:
    fontFamily: "Segoe UI Variable Display, Segoe UI"
    fontSize: "44px"
    fontWeight: 600
    fontFeature: "tnum"
  metric-value:
    fontFamily: "Segoe UI Variable Display, Segoe UI"
    fontSize: "28px"
    fontWeight: 600
    fontFeature: "tnum"
  page-title:
    fontFamily: "Segoe UI Variable Display, Segoe UI"
    fontSize: "28px"
    fontWeight: 600
    lineHeight: "36px"
  section-header:
    fontFamily: "Segoe UI Variable Display, Segoe UI"
    fontSize: "18px"
    fontWeight: 600
  group-header:
    fontFamily: "Segoe UI Variable Text, Segoe UI"
    fontSize: "14px"
    fontWeight: 600
    lineHeight: "20px"
  body:
    fontFamily: "Segoe UI Variable Text, Segoe UI"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: "20px"
  caption:
    fontFamily: "Segoe UI Variable Small, Segoe UI"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: "16px"
  mono-data:
    fontFamily: "Cascadia Mono, Consolas"
    fontSize: "12px"
    fontWeight: 400
rounded:
  control: "4px"
  card: "8px"
  pill: "10px"
  session-band: "12px"
  glyph-disc: "16px"
spacing:
  page: "20px 36px 40px 36px"
  card: "16px 20px 18px 20px"
  hero-card: "24px 28px 26px 28px"
  metric-card: "14px 16px"
  list-card: "4px 20px"
  row: "12px 0"
  section: "28px"
  stack: "12px"
  tile-gap: "12px"
components:
  button-primary:
    backgroundColor: "{colors.accent-ramp-dark1}"
    textColor: "#FFFFFF"
    rounded: "{rounded.control}"
    padding: "6px 16px 7px 16px"
    height: "36px"
  button-hero:
    backgroundColor: "{colors.accent-ramp-dark1}"
    textColor: "#FFFFFF"
    rounded: "{rounded.control}"
    padding: "8px 22px 10px 22px"
    height: "44px"
  button-secondary:
    rounded: "{rounded.control}"
    padding: "6px 14px 7px 14px"
    height: "36px"
  button-quiet:
    backgroundColor: "transparent"
    rounded: "{rounded.control}"
    padding: "5px 10px 6px 10px"
  button-danger:
    backgroundColor: "{colors.danger}"
    textColor: "{colors.on-danger}"
    rounded: "{rounded.control}"
    padding: "6px 14px 7px 14px"
    height: "36px"
  button-danger-hover:
    backgroundColor: "{colors.danger-hover}"
  button-session:
    backgroundColor: "{colors.session-button-bg}"
    textColor: "{colors.session-button-fg}"
    rounded: "{rounded.control}"
    padding: "8px 22px 10px 22px"
    height: "44px"
  button-session-hover:
    backgroundColor: "{colors.session-button-bg-hover}"
  button-session-secondary:
    backgroundColor: "transparent"
    textColor: "{colors.session-on-band}"
    rounded: "{rounded.control}"
    padding: "6px 16px 7px 16px"
    height: "36px"
  button-session-secondary-hover:
    backgroundColor: "{colors.session-ghost-hover}"
  card:
    rounded: "{rounded.card}"
    padding: "{spacing.card}"
  session-band:
    backgroundColor: "{colors.session-indigo}"
    textColor: "{colors.session-on-band}"
    rounded: "{rounded.session-band}"
    padding: "{spacing.hero-card}"
  status-badge-accent:
    backgroundColor: "{colors.fluide-teal-subtle}"
    textColor: "{colors.fluide-teal-text}"
    rounded: "{rounded.pill}"
    padding: "2px 9px 3px 8px"
    typography: "{typography.caption}"
---

# Design System: PCBoost

## Overview

**Creative North Star: "The Instrument with an Undo Button"**

PCBoost is native Windows 11 Fluent and nothing else: Mica window (`MicaKind.Base`), layered Fluent card and layer brushes, Segoe UI Variable, Segoe Fluent Icons, left NavigationView. The world was pinned by the brief (seed `brief-pinned-fluent`). No roll, no comp round. The app adds only three things to stock Fluent: one deep-teal brand accent ("Fluide") that replaces the Windows accent across the app, one saturated indigo field that belongs to the Gaming session, and a strict honesty grammar. Every figure is either measured or reads "Non disponible". Every change carries a Réversible/Irréversible tag. No state is shown by colour alone.

Density is calm and product-grade: one card per section, hairline-divided rows inside it, large tabular numerals only where a measurement is the point (score, live metrics, session band). Copy is French first, English second, and every string comes from `Strings.i18n.json` resources with `fr`/`en` pairs.

**Key Characteristics:**
- Stock Fluent controls and brushes; custom templates only where a colour must change (Danger, Session buttons).
- One brand accent (teal ramp) mapped onto `SystemAccentColor*`, so toggles, progress, selection and accent buttons all agree.
- One saturated surface (indigo session band) on the Gaming page; the title-bar gaming indicator reuses it.
- State = glyph + words, always; unavailable = words, never a fake zero.
- Theme dictionaries for Light, Dark and HighContrast on every custom brush.

## Colors

A neutral Fluent canvas with one teal voice and one indigo exception.

### Primary
- **Fluide Teal** (`fluide-teal` light / `fluide-teal-dark` dark): brand accent for primary and hero actions, nav selection indicator, toggles, progress, score-ledger points bars, the Accent badge and the active-profile pill. Accent buttons use the Fluent accent mapping (light theme `accent-ramp-dark1`, dark theme `accent-ramp-light2` with dark text).
- **Fluide Teal Text** (`fluide-teal-text` / `fluide-teal-text-dark`): teal used as text or glyph on the subtle teal fill (badges, recommendation glyph disc).
- **Teal Subtle / Track** (`fluide-teal-subtle`, `fluide-teal-track`, and the dark variants): tinted pill and disc backgrounds, and bar tracks.
- **Accent ramp** (`accent-ramp-dark3` to `accent-ramp-light3`): overrides `SystemAccentColor`, `Light1-3` and `Dark1-3` in `Colors.xaml`. This is the single rebrand point for the accent.

### Secondary
- **Session Indigo** (`session-indigo` light / `session-indigo-dark` dark): background of the Gaming session band and the title-bar "Mode Gaming actif" indicator. Text on it is `session-on-band`, with `session-secondary-text` for labels and `session-divider` for the rule and ghost-button outline. The light-theme `SessionAccentTextBrush` and `SessionChartLineBrush` are also `#34318A` (on neutral surfaces). In dark theme they are `session-accent-text-dark` and `session-chart-line-dark`.

### Tertiary
- **Danger Red** (`danger` / `danger-dark`): only for the explicit destructive button (end a process). It has its own hover and pressed steps.

### Neutral
- All neutrals are Fluent system brushes, never hex values: `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` (cards), `SubtleFillColorSecondaryBrush` (neutral badge, subtle surface), `DividerStrokeColorDefaultBrush` (row hairlines), `TextFillColorPrimary/Secondary/TertiaryBrush` (text tiers), and `SystemFillColor{Success,Caution,Critical,Attention}[Background]Brush` for states. The chart grid is `chart-grid` / `chart-grid-dark`.
- **HighContrast:** every custom brush is remapped to a `SystemColor*` value (Highlight, WindowText, Window, ButtonFace, ButtonText, GrayText). The chart fill becomes transparent.

### Named Rules
**The One Accent Rule.** Teal marks what the user can act on or what is selected, plus the score and points fills. It is never used for decoration, headings or status.

**The Single Saturated Field Rule.** Indigo appears only on the Gaming session band and on the indicator that links back to it. No other page gets a coloured field.

**The Glyph-Plus-Words Rule.** Success, caution and critical colours always come with a glyph and a text label. A StatusBadge with no text collapses itself.

## Typography

**Display Font:** Segoe UI Variable Display (with Segoe UI)
**Body Font:** Segoe UI Variable, through the stock Fluent text styles
**Label/Mono Font:** Cascadia Mono (with Consolas), for selectable technical data only

**Character:** This is the system voice of Windows 11. The only departure is the large numerals, which carry the measurements.

### Hierarchy
- **Score numeral** (600, 72/76): the Home score only, followed by "/ 100" in Subtitle, secondary colour.
- **Session numeral** (600, 44, tabular): FPS, 1 % low, 0.1 % low and frame time on the session band. Drops to 20 when the value is unavailable.
- **Metric value** (600, 28, tabular): MetricCard values and session secondary metrics. Drops to 18 when unavailable.
- **Page title** (Fluent Title, 600, 28/36, wrap whole words): one per page.
- **Section header** (Fluent Subtitle at 18, 600): sits above each section card.
- **Group header** (BodyStrong 14): row and card titles, the ledger heading.
- **Body / Secondary** (14/20): secondary text uses `TextFillColorSecondaryBrush`. Page subtitles are capped at 720 wide.
- **Caption / Field label** (12/16, secondary colour): metadata, hints, the labels above numbers.

### Named Rules
**The Tabular Numeral Rule.** Every live or compared number uses `Typography.NumeralAlignment="Tabular"`, so values don't jitter as they update.

**The Non Disponible Rule.** An unmeasured value is written out ("Non disponible", "Non disponible sur ce PC") at the reduced size and at 0.55 opacity in tiles. It is never truncated and never shown as 0.

## Layout

- **Shell:** custom 48 DIP title bar (logo 16, product name in Caption, active-profile pill, gaming indicator). Left NavigationView: open pane 264, compact below 720, expanded from 1100, built-in settings item hidden. The pane footer holds protection state, links (Journal, Confidentialité, Support, À propos) and the version. The content grid has an 8 DIP top-left corner and a 1,1,0,0 stroke. Shell InfoBars sit above the frame with a 24,12,24,0 margin.
- **Content column:** `ContentMaxWidth` 1120 for dashboard pages. Reading and settings pages (Settings, About, Privacy, Diagnosis) use 880. Processes uses 1320 for its table plus detail pane. All pages use `PagePadding` (20,36,40,36 as top, sides, bottom).
- **Rhythm:** section gap 28 on Home (24–26 on the 880-wide pages). Header-to-card gap 12. Tile gap 12. Row padding 12 top and bottom. Card padding 20,16,20,18. The hero and session band use 28,24,28,26.
- **Home breakpoint:** measured on the content area, not the window, at **900 DIP**. At Wide, the score block (5*) sits beside the ledger (6*) and system facts (3*) sit beside quick actions (2*). At Narrow, both pairs stack into one column.
- **Processes:** the detail column is width 0 until a process is selected. Then it becomes 320 (content 900 or wider) or 280.
- **Dialogs:** `ContentDialogMaxWidth` 640. Body and detail text is selectable.

## Elevation & Depth

Depth comes from Fluent tonal layering: Mica, then the layer surface, then the card fill with a 1 DIP card stroke. Cards and bands have no custom shadow. System flyouts, menus, tooltips and ContentDialogs keep their native Fluent shadow, because that is part of the platform world.

**The Flat Card Rule.** A card is fill plus hairline stroke. Depth between sections comes from spacing and headers, never from added shadows.

## Shapes

Fluent control corners (4, `ControlCornerRadius`) on all buttons. Cards and subtle surfaces use 8. Badges and title-bar pills use 10 (pill). The session band uses 12, its one larger silhouette. The recommendation glyph sits in a 32 DIP circle. Row separators are 1 DIP hairlines. Bars are thin: `PointsBarStyle` ProgressBar at 4 DIP min height, range 0–100.

### Brand mark

The "tuile propulsée": a 2×2 grid of rounded tiles (the PC's windows) in `#11919A` teal, the fourth tile in `#5B55E6` indigo lifted up and to the right, with two `#8B87F0` trails in the freed corner. Teal is the app's "Fluide" accent, indigo the Gaming session colour. Master geometry in a 100 box: tile 38, gap 8, lift 8, radius 14.5 % of the tile. Below 48 px the trails are dropped and the tiles snap to whole pixels (16, 20, 24, 32, 40 px are drawn, not reduced). The title bar picks the 16/20/24/32 px image that matches the current scale. Wordmark: "PCBOOST" in Barlow Semi Condensed ExtraBold outlines, "PC" in deep teal and "BOOST" in indigo (`#E8F7F8` / `#A9A6F7` on dark). Sources: `build/brand/*.svg`; every raster is regenerated by `build/tools/make_brand_assets.py`.

## Components

### Buttons
All buttons use Fluent control corners. Custom templates use a `BrushTransition` of 83 ms and system focus visuals (`FocusVisualMargin` -3).
- **Primary** (Fluent accent, min height 36): the main action inside a card or InfoBar.
- **Hero / HeroSecondary** (min height 44, 15 px; Hero is SemiBold with a leading glyph): the page's one main action, for example "Optimiser mon PC" + "Analyser". Use at most one Hero per view state.
- **Secondary** (Fluent default, 36): the most common button, used for row actions and alternatives.
- **Quiet** (transparent fill and border): tertiary list actions such as "Ignorer" or "Détails".
- **Danger** (red fill, white text, own hover and pressed): explicit destructive actions only. One use in the build.
- **Session** (light fill, indigo text, 44, SemiBold): the main action on the indigo band (Activer / Désactiver et restaurer).
- **SessionSecondary** (transparent, white text, divider-colour outline, 36): the secondary action on the band.
- **Disabled:** Fluent disabled fill and text for every variant.

### Status badges
A pill (radius 10, padding 8,2,9,3) with an optional 11 px glyph and 12 px text. The text is required: the badge collapses when empty, and its text is its accessible name. Kinds:
- **Neutral:** subtle fill.
- **Accent:** teal subtle fill with teal text (score level, active state).
- **Success / Caution / Critical / Info:** Fluent state background, glyph in the state colour, text in the primary colour.

Mappings live in `Ui.cs`: FactorKind, SeverityKind and SafetyKind (SAFE → Success, CAUTION → Caution, ADVANCED → Critical).

### Cards / Containers
- **CardStyle:** card fill, card stroke, radius 8, padding 20,16,20,18. Cards are never nested.
- **List card:** padding 20,4,20,4. Each item's root carries a bottom hairline, and `ListDividers.HideLast` removes the last one. Static stacks use `RowDividerStyle` (top hairline, 12 padding).
- **MetricCard:** padding 16,14, min width 150. Glyph + field label, then value, then a thin gauge, then detail, then an optional secondary reading (temperature).
- **RecommendationCard** (a row, not a card): glyph disc, title, description, reason, then a badge row (kind, impact, risk, Réversible/Irréversible). Secondary and Quiet actions sit on the right.
- **SubtleSurface:** subtle fill, radius 8, padding 16,12 (inline notes).

### Score ledger (signature)
One transparent full-width button per factor: glyph, label, then a "Bon" note when the factor is at its maximum, then a 120 DIP points bar, then tabular points text ("9 / 25"), then a chevron. A factor below maximum adds a second line with a StatusBadge and a one-line explanation. The accessible name and tooltip carry the full explanation.

### Session band (signature)
The Gaming page only. `SessionBandStyle` uses the indigo fill, a game title and state line, Session buttons on the right, a divider-colour rule, and a grid of session numerals and metrics with secondary-text labels.

### Settings rows
`SettingRow`: min height 52, padding 10 top and bottom. An 18 px secondary glyph, a header (Body) with a description (Caption secondary), and the control right-aligned.

### Lists
`StretchListViewItemStyle` (padding 12,0, min height 44) for selectable lists. `PlainItemContainerStyle` (zero padding) when the template draws its own row.

### Empty state
A centred stack (max width 460, padding 28 top and bottom): a 32 px tertiary glyph and a secondary message. The glyph is Raw for accessibility. Inline "all good" variant: success check glyph + secondary text inside the card.

### InfoBar
Show and hide it with `InfoBarHelper.IsShown`, which toggles IsOpen and collapses the element so no gap is left. Severity use:
- **Error:** an operation failed (most uses).
- **Success:** a finished result.
- **Warning:** crash recovery.
- **Informational:** game detected.

Shell banners that need a decision are `IsClosable="False"` and hold a Primary + Secondary pair inside.

### Navigation
Stock NavigationView (Left) with Segoe Fluent Icons glyphs. The selection indicator follows the teal accent. Top-level items, in order: Accueil, Analyse, Optimisation, Nettoyage, Démarrage, Processus, Gaming, Performances, Historique. Paramètres is a footer item.

## Do's and Don'ts

### Do:
- **Do** take colours from `Colors.xaml` theme dictionaries or Fluent system brushes. Every new brush needs Light, Dark and HighContrast entries.
- **Do** pair every state colour with a glyph and words (StatusBadge, or glyph + text row).
- **Do** write "Non disponible" for any unmeasured value, at the reduced size (18 for tiles, 20 for session numerals), and let it wrap.
- **Do** tag every previewed change Réversible or Irréversible, and keep "Restaurer" reachable from reports and History.
- **Do** use the resource plural syntax `{n:plural:singulier|pluriel}` in both `fr` and `en`. Never concatenate counts in code.
- **Do** give composite buttons an `AutomationProperties.Name`, mark decorative glyphs and bars that duplicate adjacent text as `AccessibilityView="Raw"`, and keep system focus visuals.
- **Do** switch layouts on content-area width (Home at 900 DIP), not window width.
- **Do** use Segoe Fluent Icons for all glyphs. It is the platform's own icon system.

### Don't:
- **Don't** use teal for headings, decoration or status. Don't put indigo anywhere except the Gaming session band and its indicator.
- **Don't** nest cards. Separate rows with hairlines and hide the last one.
- **Don't** add custom shadows to cards or bands.
- **Don't** lay out cards of different heights with `UniformGridLayout`: it gives every item the size of the first one and clips the rest. Use `controls:CardGridLayout` (rows as tall as their tallest card). Keep `UniformGridLayout` for fixed-height tiles such as the installed-games grid.
- **Don't** show a fake zero, placeholder number or invented gain when a value wasn't measured.
- **Don't** use a red fill for anything except an explicit destructive action.
- **Don't** hard-code the product name, slogan, publisher or version. They come from `build/Branding.props`, `Assets/Branding/branding.json` and the `Assets/Branding` images. `BrandUpgradeCode` never changes after the first release.
