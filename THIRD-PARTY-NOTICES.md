# Composants tiers

PCBoost utilise les composants suivants. Les textes complets des licences sont disponibles aux adresses indiquées et dans les paquets NuGet correspondants.

## Redistribués avec l'application

| Composant | Version | Licence | Source |
|---|---|---|---|
| .NET Runtime (application autonome) | 10.0 | MIT | https://github.com/dotnet/runtime |
| Windows App SDK (WinUI 3, Foundation, InteractiveExperiences) | 2.x | MIT | https://github.com/microsoft/WindowsAppSDK |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection / Logging | 10.0.12 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Data.Sqlite | 10.0.12 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (dépendance de Microsoft.Data.Sqlite) | — | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | — | Domaine public | https://sqlite.org/copyright.html |
| System.Management | 10.0.12 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.2.6 | MIT | https://github.com/microsoft/perfview |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.4.0 / 10.0.0 / 7.0.0 | Apache-2.0 | https://github.com/serilog |
| WiX Toolset — actions personnalisées et dialogues intégrés au MSI | 5.0.2 | MS-RL | https://github.com/wixtoolset/wix |

## Identité visuelle

Le mot-symbole du logo (« PCBOOST ») est dessiné à partir de la police Barlow Semi Condensed ExtraBold (Copyright 2017 The Barlow Project Authors, licence SIL Open Font License 1.1, https://github.com/jpt/barlow). Seuls les contours vectorisés figurent dans les images ; la police elle-même n'est pas distribuée.

## Outils de compilation et de test (non redistribués)

| Composant | Licence |
|---|---|
| Microsoft.Windows.SDK.BuildTools | Licence Microsoft (outils du SDK Windows) |
| WiX Toolset SDK 5.0.2 | MS-RL |
| xUnit.net 2.9.3, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |

Les icônes de l'interface proviennent de la police système Segoe Fluent Icons / Segoe MDL2 Assets fournie par Windows (non redistribuée). Le logo et l'icône de PCBoost sont générés par `build/tools/make_brand_assets.py`.
