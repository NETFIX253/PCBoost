# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

Native Windows desktop (none of web/ios/android/adaptive applies) (WinUI 3 / Windows App SDK), Windows 10 1809+ and Windows 11, x64 first, ARM64-ready. The design language is Windows 11 Fluent; there is no web surface.

## Stack

C# / .NET 10, WinUI 3 (Windows App SDK 2.x, unpackaged, self-contained), XAML + MVVM (CommunityToolkit.Mvvm), Microsoft.Extensions DI/Logging, Serilog, SQLite. Win32 / WMI / PDH / ETW for system access. WiX MSI installer. Chosen by the user in the brief.

## Users

- Non-technical owners of old, modest, slow or process-overloaded PCs who want their machine to feel responsive again without understanding Windows internals.
- Players on modest configurations who want the best performance reasonably available during a game session, then everything put back.

## Product Purpose

Analyse the PC, explain in plain language what is slowing it down, and apply only safe, previewed, reversible improvements (cleanup, startup, power, background load, gaming session). Success = the user understands what changed, can undo it, and gains are only claimed when measured.

## Positioning

An honest optimizer: every figure is measured or marked "Non disponible", every system change is previewed before it happens and recorded for one-click restore, and security protections (Defender, Firewall, Windows Update, SmartScreen, BitLocker) are never touched. It refuses "turbo/boost" marketing, registry "tweaks" and invented gains.

## Operating Context

Runs locally and offline, as a normal user; asks for UAC only for a specific privileged action. Lives in the tray, light at rest, adaptive monitoring. Local SQLite history of scans, sessions, benchmarks and restore data.

## Capabilities and Constraints

Dashboard with explainable health score, system analysis, "Why is my PC slow?", cleanup (SAFE/CAUTION/ADVANCED), startup manager, process manager with critical-process protection, one-click optimization with dry-run preview, rollback history and crash recovery, profiles (Balanced, Productivity, Gaming, Power saver, Custom), Gaming mode with game detection and automatic restore, FPS via ETW (no injection), before/after benchmark, real-time monitoring, notifications, French/English, light/dark/system themes, MSI installer.

## Brand Commitments

Working name "PCBoost" and slogan "Redonnez de la fluidité à votre PC." — both must be replaceable (name, logo, icon, colors, slogan, version, publisher centralised). French first, English available. Fluent / Windows 11 look, minimal, professional, reassuring; the Gaming page is visually distinct.

## Evidence on Hand

No customers, testimonials, benchmarks or performance claims exist. None may be invented; the UI shows only measured values.

## Product Principles

1. Security and stability before performance.
2. Show before changing; record before applying; always offer undo.
3. Measure, don't promise: unmeasured means "Non disponible".
4. Plain language for the user, technical detail for Expert mode.
5. The optimizer must itself stay light.

## Accessibility & Inclusion

Keyboard navigation, screen-reader labels, sufficient contrast in both themes, readable text sizes, no information conveyed by colour alone.
