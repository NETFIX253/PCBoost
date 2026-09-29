# PCBoost — conventions de développement (obligatoires)

Dépôt : `/home/claude/PCBoost`. Lire d'abord `PRODUCT.md` (produit et principes) et parcourir `src/PCBoost.Core` (contrats partagés).

## Environnement
- SDK : `export PATH=/opt/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`
- Cette machine est **Linux**. Les projets `net10.0-windows…` compilent (EnableWindowsTargeting) mais le code Win32 ne s'exécute pas ici.
- **Plusieurs développeurs travaillent en parallèle** dans ce dépôt. `PCBoost.Core` et `tests/PCBoost.TestUtilities` sont déjà compilés en Release.
  Compilez **uniquement votre projet**, sans recompiler les références :
  `dotnet build src/PCBoost.X -c Release -p:BuildProjectReferences=false`
  `dotnet build tests/PCBoost.X.Tests -c Release -p:BuildProjectReferences=false && dotnet test tests/PCBoost.X.Tests -c Release --no-build`
  (compilez d'abord votre projet `src`, puis le projet de tests).
- **Ne modifiez jamais** `src/PCBoost.Core`, `tests/PCBoost.TestUtilities`, `Directory.*.props`, la solution, ni les projets d'un autre module.
  Si un contrat Core manque, définissez le type dans votre propre projet (`internal` de préférence) et signalez-le dans votre rapport final.
- Paquets : versions centralisées dans `Directory.Packages.props` (référencez sans `Version`). Si un paquet manque, signalez-le plutôt que d'éditer ce fichier.

## Règles produit (non négociables)
- Sécurité et stabilité avant performance. Aucune désactivation de Defender, pare-feu, Windows Update, SmartScreen, BitLocker, services.
  `PCBoost.Core.Security.ForbiddenTargetPolicy` fait autorité.
- Aucune donnée inventée : une mesure indisponible est `null` / `SensorReading.Unavailable(...)` / `Availability.*`, jamais une estimation déguisée.
- Toute modification système passe par `IChangeRecorder` (write-ahead) avec un état « avant » sérialisé via `ChangeStateSerializer`
  et les types de `PCBoost.Core.Optimization.ChangeStates` ; types dans `ChangeKinds`.
- Les échecs attendus (accès refusé, fichier verrouillé, capteur absent) renvoient `OperationResult` ; pas d'exception qui remonte.
  `OperationResult.FromException(ex)` classe les exceptions.
- Pas de `// TODO`, pas de méthodes vides, pas de valeurs factices. Code de production réel.

## Code
- C# latest, nullable activé (les avertissements nullable sont des erreurs), `async`/`await` + `CancellationToken`, `ILogger<T>`.
  Ne journalisez jamais de données personnelles (contenu de fichiers, noms d'utilisateur en clair) : chemins sous le profil → remplacez le préfixe par `%USERPROFILE%` si vous journalisez un chemin.
- Espaces de noms = nom du projet (sauf `PCBoost.System` → espace de noms **`PCBoost.Platform`**, pour ne pas masquer `System`).
- Chaque module expose une méthode d'enregistrement DI : `public static IServiceCollection AddPCBoostXxx(this IServiceCollection services)`
  dans une classe `ServiceCollectionExtensions` de l'espace de noms du module. Services sans état partagé → Singleton.
- Horloge : injectez `IClock` (Core) plutôt que `DateTimeOffset.UtcNow` dans la logique testable.

## Textes et localisation (§43)
- Les moteurs ne produisent **jamais** de texte affiché : ils renvoient `TextRef.Of("Cle", args…)`.
- Chaque module possède ses ressources : `src/PCBoost.X/Resources/Strings.i18n.json` au format
  `{ "Cle": { "fr": "…", "en": "…" } }` (arguments `{0}`, `{1:N1}`…), puis générez les .resx :
  `python3 build/tools/i18n.py src/PCBoost.X/Resources/Strings.i18n.json` (produit `Strings.resx` et `Strings.en.resx`, à conserver).
- Enregistrez la source dans votre méthode DI :
  `services.AddSingleton<IStringResourceSource>(ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, "<RootNamespace>.Resources.Strings"));`
- Préfixes de clés par module pour éviter les collisions : `Diag_`, `Opt_`, `Game_`, `Sys_`, `Infra_`. Exceptions : les clés imposées par Core
  (`Cleanup_{id}_Name`, `Cleanup_{id}_Description`, `Protection_*`, `Error_*`) — voir le brief de chaque module pour savoir qui les fournit.
- Français d'abord, clair, non technique, sans promesse chiffrée non mesurée. Pas de « turbo », « boost ultime », « +200 FPS ».
  PCBoost n'est pas un antivirus : jamais « virus », « malware » ; utiliser « Inconnu », « Non signé », « À examiner ».

## Tests (xUnit 2)
- Utilisez les faux de `tests/PCBoost.TestUtilities` (registre, système de fichiers, processus, alimentation, dépôts en mémoire, horloge…).
- Couvrez les chemins nominaux, les erreurs (accès refusé, verrouillage), et les garde-fous de sécurité.

## Rapport final attendu
Liste des fichiers créés, API publique (classes, méthode DI), résultats `dotnet build`/`dotnet test`, limites connues honnêtes,
et toute demande de modification de Core.
