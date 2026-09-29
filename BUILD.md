# Compiler, tester et publier PCBoost

## Prérequis

| Outil | Version |
|---|---|
| Windows | 10 (1809+) ou 11, x64 — la compilation XAML (WinUI 3) n'est possible que sous Windows |
| SDK .NET | 10.0.100 ou ultérieur (`global.json` : `rollForward: latestFeature`) |
| Visual Studio (facultatif) | 2026 avec la charge « Développement d'applications Windows » |

Aucune autre installation n'est nécessaire : le Windows App SDK (2.x, composants WinUI / Foundation / InteractiveExperiences) et WiX Toolset 5 sont des paquets NuGet restaurés automatiquement.

## Commandes

Depuis la racine du dépôt :

```powershell
# Compilation Release x64 + tous les tests
powershell -ExecutionPolicy Bypass -File build\build.ps1

# Compilation seule
powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipTests

# Publication autonome (self-contained) + version portable (dist\*.zip)
powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipTests -Publish

# Publication + installateur MSI + empreintes SHA-256 (dist\)
powershell -ExecutionPolicy Bypass -File build\build.ps1 -Publish -Installer

# ARM64
powershell -ExecutionPolicy Bypass -File build\build.ps1 -Platform ARM64 -Publish -Installer
```

Équivalents `dotnet` :

```powershell
dotnet build PCBoost.sln -c Release -p:Platform=x64
dotnet test  PCBoost.sln -c Release -p:Platform=x64 --no-build
dotnet publish src\PCBoost.App\PCBoost.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o artifacts\publish\win-x64
dotnet build installer\PCBoost.Installer\PCBoost.Installer.wixproj -c Release -p:PublishDir=artifacts\publish\win-x64 -p:InstallerPlatform=x64
```

`build.ps1` écrit un résumé dans `artifacts\logs\summary.txt` et les journaux détaillés (`restore.log`, `build.log`, `test.log`, `publish.log`, `installer.log`) dans le même dossier ; les résultats de tests (TRX) vont dans `artifacts\test-results`.

## Livrables (`dist\`)

| Fichier | Contenu |
|---|---|
| `PCBoost-<version>-x64.msi` | Installateur par machine (WiX 5) : Program Files, menu Démarrer ; raccourci Bureau et lancement avec Windows facultatifs ; mise à niveau majeure automatique. |
| `PCBoost-<version>-x64-portable.zip` | Application autonome (runtime .NET et Windows App SDK inclus). |
| `SHA256SUMS.txt` | Empreintes des livrables. |
| `README.md`, `CHANGELOG.md` | Notice utilisateur et historique des versions. |

L'application est publiée **self-contained** (aucun runtime à installer), **non empaquetée** (pas de MSIX), avec ReadyToRun en Release.

## Tests

- 6 projets xUnit (`tests\`), plus de 1 300 tests : règles de sécurité (dont « l'optimiseur ne ferme jamais explorer.exe, csrss.exe, wininit.exe, winlogon.exe, services.exe, lsass.exe, smss.exe, dwm.exe »), score, constats, optimisations et annulation, récupération après interruption, nettoyage, démarrage, Gaming, FPS, benchmark, persistance, ViewModels, graphe d'injection de dépendances complet.
- Les fournisseurs Windows sont derrière des interfaces : les tests utilisent des doubles (`PCBoost.TestUtilities`). Les tests d'intégration réels (`[WindowsFact]`, projet `PCBoost.System.Tests`) ne s'exécutent que sous Windows ; ailleurs ils sont ignorés.
- Les projets non graphiques (tout sauf `PCBoost.App` et `PCBoost.Elevator`) compilent et se testent aussi sous Linux/macOS : `dotnet test PCBoost.Portable.slnf`.

## Textes et traductions

Chaque module possède `Resources/Strings.i18n.json` (source unique fr/en). Après modification :

```powershell
python build\tools\i18n.py src\PCBoost.Presentation\Resources\Strings.i18n.json
```

L'outil régénère `Strings.resx` (français, langue neutre) et `Strings.en.resx`, et échoue si une traduction manque. Aucun texte d'interface n'est écrit en dur : les vues utilisent `{h:Str Key=...}`.

## Réseau restreint (flux NuGet local)

Si `dotnet restore` échoue à télécharger les gros paquets (packs d'exécution, `Microsoft.Windows.SDK.NET.Ref`), placez les `.nupkg` dans un dossier et ajoutez-le comme source dans un `nuget.config` au-dessus du dépôt :

```xml
<configuration>
  <packageSources>
    <add key="pcboost-local" value="C:\chemin\vers\_packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

`Directory.Build.props` désactive déjà le téléchargement des packs des frameworks non utilisés (`DisableTransitiveFrameworkReferenceDownloads`).

## Boucle de développement assistée (facultatif)

`build\dev-agent.cmd` démarre un agent local qui exécute **uniquement** une liste fermée d'actions déposées dans `artifacts\devloop\request.json` (`build`, `test`, `publish`, `installer`, `msi`, `verify-msi`, `launch`, `selfcapture`, `logs`, `crashinfo`, `stop`, `capture`, `install-msi`, `uninstall-msi`, `status`). Il sert à piloter compilation et captures d'écran depuis un autre environnement ; il n'est pas livré dans l'installateur.

Captures automatiques de l'interface (fonctionne même écran verrouillé, rendu XAML interne) :

```powershell
PCBoost.exe --capture-dir C:\temp\captures --capture-pages home,analysis,gaming --theme dark --lang fr
```

La pseudo-page `gaming-active` active réellement le mode Gaming (réglages temporaires enregistrés), capture la bande de session puis le désactive et vérifie la restauration (`gaming-session.txt`) : c'est un test de bout en bout, à n'utiliser que sur un poste de test.

Les pseudo-pages `oldpc-essential`, `oldpc-standard` et `oldpc-advanced` ouvrent l'aperçu du niveau correspondant de l'assistant « PC ancien », le capturent et décrivent son contenu (`oldpc-<niveau>.txt`) : lecture seule, rien n'est appliqué.

Autres arguments : `--page <clé>` (page d'ouverture), `--theme light|dark|system`, `--lang fr|en` (pour la session uniquement), `--background` (démarrage dans la zone de notification).
