# Architecture de PCBoost

## 1. Principes

- **Clean Architecture** : le cœur (`PCBoost.Core`) ne dépend de rien d'autre que de .NET ; les accès Windows sont derrière des interfaces (`Core/Abstractions/Platform`), implémentées dans `PCBoost.System`.
- **Sûreté avant performance** : les règles de sécurité (processus critiques, cibles interdites) vivent dans le cœur et sont vérifiées à chaque couche qui agit (optimiseur, fournisseurs, assistant administrateur).
- **Écriture anticipée (write-ahead)** : toute modification du système est enregistrée avec son état *avant* dans SQLite, puis appliquée, puis marquée appliquée. Un arrêt brutal ne laisse jamais une modification non documentée.
- **Testabilité** : tout ce qui touche Windows est simulable ; les ViewModels sont dans un projet `net10.0` testable sans interface.
- **Aucun texte en dur** : chaque module fournit ses propres ressources fr/en, agrégées par un localiseur unique.

## 2. Projets et dépendances

```
                        PCBoost.App (WinUI 3, net10.0-windows)
                               │  composition (DI), vues, styles, zone de notification
                               ▼
                        PCBoost.Presentation (net10.0)  ← ViewModels, navigation, abstractions d'interface
                               │
    ┌──────────────┬───────────┼──────────────┬─────────────────┬──────────────────┐
    ▼              ▼           ▼              ▼                 ▼                  ▼
Diagnostics   Optimization   Gaming     Infrastructure     Persistence      System (PCBoost.Platform)
    │              │           │              │                 │                  │ Win32, WMI, registre,
    └──────────────┴───────────┴──────────────┴─────────────────┴──────────────────┤ processus, ETW
                                                                                   ▼
                                     PCBoost.Core (net10.0) : contrats, modèles, sécurité, nettoyage
PCBoost.Elevator (requireAdministrator) → Core + System, liste blanche fermée d'opérations
```

| Projet | Rôle | Cible |
|---|---|---|
| `PCBoost.Core` | Modèles (`SystemInfo`, `ProcessInfo`, `ChangeRecord`, `OptimizationSession`…), contrats de services et de fournisseurs, `OperationResult`, `TextRef` (texte localisable différé), `CriticalProcessProtection` (+ `protected-processes.json` embarqué), `ForbiddenTargetPolicy`, catalogue et exécuteur du nettoyage. | net10.0 |
| `PCBoost.System` | Fournisseurs Windows réels (espace de noms `PCBoost.Platform`, pour ne pas masquer `System`) : processus, métriques (PDH/compteurs), matériel (WMI), alimentation (`powrprof`), registre, fichiers, signatures (Authenticode), tâches planifiées, démarrage (StartupApproved), effets visuels, corbeille, fenêtre au premier plan, élévation, capture ETW via l'Elevator. | net10.0-windows |
| `PCBoost.Elevator` | Exécutable séparé (`requireAdministrator`), lancé par UAC pour une seule opération de la liste blanche, puis terminé. | net10.0-windows |
| `PCBoost.Diagnostics` | `SystemAnalyzer`, `HealthScoreCalculator`, moteur de règles de santé, profil matériel, conseils, diagnostic « Pourquoi mon PC est lent ? », `PerformanceMonitor` adaptatif, enregistrement de l'historique. Voir [SCORING.md](SCORING.md). | net10.0 |
| `PCBoost.Optimization` | Modules d'optimisation (`IOptimization`), orchestrateur (aperçu → application → vérification → rapport), `ChangeRecorder`, gestionnaires d'annulation (`IChangeHandler`), `RollbackManager`, `RecoveryManager`, profils, assistant ancien PC, nettoyage, démarrage, optimisations automatiques sûres. Voir [OPTIMIZATIONS.md](OPTIMIZATIONS.md). | net10.0 |
| `PCBoost.Gaming` | Détection des jeux (scanners de bibliothèques + base de signatures), `GamingService`, `AutoGamingMode`, `GamingSessionReconciler`, calcul FPS/1 %/0,1 % low, `BenchmarkService`. Voir [GAMING.md](GAMING.md). | net10.0 |
| `PCBoost.Persistence` | SQLite (Microsoft.Data.Sqlite), WAL, migrations par `PRAGMA user_version`, dépôts (sessions, modifications, scans, instantanés, benchmarks, sessions de jeu, journal d'activité, clé/valeur). | net10.0 |
| `PCBoost.Infrastructure` | Journalisation Serilog avec masquage des données personnelles, réglages (JSON dans la base clé/valeur), localiseur, journal d'activité, service de mises à jour. | net10.0 |
| `PCBoost.Presentation` | ViewModels (CommunityToolkit.Mvvm), `PageRegistry`/`PageKeys`, `ViewModelContext` (localiseur, formateur, dialogues, navigation, répartiteur UI), éléments de liste prêts à afficher. | net10.0 |
| `PCBoost.App` | Point d'entrée (`Program.Main` : instance unique, arguments), `App` (composition, démarrage des services d'arrière-plan, arrêt propre), `MainWindow` (NavigationView, barre de titre, zone de notification), vues XAML, contrôles, styles Fluent, services d'hôte (navigation, dialogues, thème, notifications, captures). | net10.0-windows |

## 3. Composition (injection de dépendances)

`App.ConfigureServices` assemble, dans cet ordre : `AddPCBoostLogging`, `AddPCBoostPlatform`, `AddPCBoostInfrastructure`, `AddPCBoostPersistence(chemin)`, `AddPCBoostDiagnostics`, `AddPCBoostOptimization`, `AddPCBoostGaming`, `AddPCBoostPresentation`, puis les services propres à WinUI. Le test `Core.Tests/Composition/ServiceGraphTests` construit le graphe complet avec `ValidateOnBuild` et `ValidateScopes` pour détecter toute dépendance manquante ou captive.

Séquence de démarrage (`App.OnLaunched`) : journalisation → initialisation/migration de la base → chargement des réglages → liaison réglages ↔ services (langue, journalisation détaillée, surveillance) → fenêtre (thème appliqué avant le premier rendu) → services d'arrière-plan : surveillance (si activée), enregistreur d'historique, réconciliation d'une session Gaming interrompue, mode Gaming automatique, optimisations automatiques sûres (si autorisées). La récupération d'une session d'optimisation interrompue est proposée par un bandeau (restaurer ou conserver).

## 4. Flux principaux

### 4.1 Analyse
`SystemAnalyzer` interroge les fournisseurs en parallèle avec délais, chaque mesure étant `SensorReading` (valeur, ou indisponible avec raison : pas de capteur, autorisation requise, non pris en charge). `HealthScoreCalculator` exclut les facteurs non mesurables et remet l'échelle à 100 ; le moteur de règles produit des constats (sévérité, preuve, action). Le résultat est persisté (`scans`).

### 4.2 Optimisation et restauration
1. **Aperçu** : chaque module `IOptimization` vérifie sa pertinence et produit un aperçu (`PreviewAsync` → `OptimizationPreview`) sans rien modifier (dry-run).
2. **Validation** : `IOptimizationSafetyValidator` refuse toute action visant un processus critique, une cible interdite (`ForbiddenTargetPolicy`) ou non réversible sans consentement explicite.
3. **Sauvegarde** : pour chaque modification, `ChangeRecorder` écrit un `ChangeRecord` *Pending* avec l'état avant sérialisé (`ChangeStateSerializer`), dans une session (`OptimizationSession`).
4. **Application** par le gestionnaire du type (`registry.value` — dont StartupApproved —, `power.scheme`, `process.priority`, `process.efficiency`, `visual.effects`, `scheduledtask.enabled`), puis passage à *Applied* ou *Failed*.
5. **Vérification** et rapport final (appliqué, ignoré, échoué, redémarrage nécessaire, espace libéré).
6. **Restauration** : `RollbackManager.UndoChange` / `RestoreSession` rejouent l'état avant en ordre inverse ; `CanRollback` indique si c'est possible. Au démarrage, `RecoveryManager` détecte les sessions restées *InProgress* (arrêt brutal) et propose de les annuler.

Les suppressions de fichiers (nettoyage, corbeille) sont par nature irréversibles : elles sont signalées comme telles et confirmées explicitement.

### 4.3 Élévation ponctuelle
L'application tourne en utilisateur standard (`asInvoker`). Une opération privilégiée est envoyée à `PCBoost.Elevator.exe` (UAC) sous forme d'une requête typée : `cleanup.category` (le chemin est résolu par l'Elevator à partir de l'identifiant de catégorie, jamais transmis), `registry.set`/`registry.delete` (uniquement les clés HKLM de `ForbiddenTargetPolicy.ElevatedRegistryAllowList`), `task.setenabled` (tâches non Microsoft), `frames.capture` (session ETW pour un PID), et quatre opérations sans paramètre : `disk.reliability`, `boot.performance`, `apps.lastrun` (lectures seules) et `restorepoint.create`. Les résultats reviennent dans `ElevatedResponse.Data` (dictionnaire texte, format `HealthElevatedData`, valeurs revalidées à la lecture). Toute autre demande est refusée. Voir [SECURITY.md](SECURITY.md).

### 4.4 Mode Gaming
Détection (bibliothèques installées + processus au premier plan) → activation : sauvegarde puis application des optimisations de session (mode d'alimentation, priorité du jeu, activité des applications en arrière-plan, préférence GPU) → surveillance → fermeture du jeu détectée → restauration automatique. `GamingSessionReconciler` restaure au démarrage une session laissée active par un arrêt brutal. Les FPS proviennent des événements de présentation DXGI/D3D9 (ETW) capturés par l'Elevator et diffusés par un canal nommé (`PCBoost.Frames.<guid>`) ; aucune DLL n'est injectée, aucun anti-triche n'est touché.

### 4.5 Santé du matériel, démarrage et rapport
`HardwareHealthService` relève disques (`MSFT_PhysicalDisk`), batteries (IOCTL batterie), périphériques en erreur (PnP) et limitations de vitesse du processeur (journal Système) sans autorisation ; les compteurs de fiabilité lus avec autorisation sont conservés dans `kv` avec leur date. Une vérification toutes les 6 heures notifie un disque signalé en mauvais état (au plus une fois par jour et par disque). `ThermalThrottlingDetector` suit les échantillons du moniteur (charge ≥ 70 %, performance ≤ 60 % de la fréquence de base, sur secteur, ≥ 20 s). `BootTimeService` associe les démarrages (Kernel-Boot 27) aux mesures Windows (Diagnostics-Performance 100 à 110) et compare les démarrages mesurés avant et après le dernier changement de programmes au démarrage enregistré dans l'historique. Les règles `health.hardware.*` produisent la catégorie « hardware » sans modifier le score. `DiagnosticReportViewModel` assemble ces données, l'historique, le journal et les journaux techniques relus (`IDiagnosticLogSource`) ; `DiagnosticReportHtml` produit un document autonome, masqué par `SensitiveDataRedactor` (Core.Privacy), enregistré en HTML ou en PDF par l'aperçu WebView2 (`ReportFileService`).

### 4.6 Programmes, fichiers et réglages par jeu
`ProgramInventoryService` lit les clés de désinstallation (64 bits, 32 bits, utilisateur) via `IRegistryProvider`, écarte les entrées protégées (`ProgramClassifier`) et estime la dernière utilisation (Prefetch lu avec autorisation, sinon dernier accès aux fichiers) ; `UninstallCommandParser` n'accepte que Windows Installer ou un exécutable absolu, lancé par `IUninstallerLauncher`. `FileCleanupService` analyse les dossiers personnels (taille, échantillons début/fin, SHA-256), puis envoie les fichiers choisis à la Corbeille (`IFileSystemProvider.MoveToRecycleBin`, avertissement Windows avant toute suppression définitive) après revérification. Les réglages du mode Gaming propres à un jeu sont stockés dans `GamingSettings.GameProfiles` et appliqués par `GamingSettings.ForGame` ; l'historique des FPS par jeu provient de `IGamingSessionRepository.GetByGameAsync`.

## 5. Persistance

Base `%LOCALAPPDATA%\PCBoost\pcboost.db` (WAL). Tables : `sessions`, `changes`, `gaming_sessions`, `scans`, `snapshots` (historique des performances, agrégé), `benchmarks`, `kv` (réglages, recommandations masquées), `activity` (journal lisible). Rétention configurable (30, 90, 180 ou 365 jours). Migrations transactionnelles et idempotentes (version 2 : index `gaming_sessions (game_id, started_at)` pour l'historique par jeu).

## 6. Interface

- WinUI 3, Windows App SDK 2.x non empaqueté et autonome ; `Program.Main` personnalisé (instance unique via `AppInstance`, redirection de la deuxième instance).
- MVVM : `x:Bind` compilé partout ; fonctions d'affichage dans `Helpers/Ui` (visibilité, badges) ; textes via l'extension de balisage `{h:Str Key=...}`.
- Styles centralisés (`Styles/*.xaml`) : couleurs de marque par thème (Clair, Sombre, Contraste élevé), typographie, boutons, cartes, navigation. La page Gaming utilise une « bande de session » distincte.
- Services d'hôte : `NavigationService` (clés de page → vues), `DialogService` (confirmations, actions destructives), `ThemeService`, `AppNotificationService` (notifications Windows), `TrayIcon` (Win32), `DevCaptureService` (captures `--capture-dir`).
- Accessibilité : noms automation sur les éléments composés, navigation clavier, contraste élevé pris en charge par les dictionnaires de thème.

## 7. Threading et performance

Les fournisseurs sont appelés hors du thread UI ; les ViewModels publient leurs mises à jour via `IUiDispatcher`. La surveillance est adaptative : fréquence réduite quand la fenêtre est masquée, suspendue si l'utilisateur la met en pause, et les pages ne s'abonnent qu'à l'affichage (`MonitorLease`). Les listes volumineuses (processus) sont synchronisées par différence (`CollectionSync`) plutôt que reconstruites.

## 8. Tests

| Projet | Portée |
|---|---|
| `PCBoost.Core.Tests` | Sécurité (processus critiques, cibles interdites), nettoyage, infrastructure, persistance, graphe DI complet. |
| `PCBoost.Diagnostics.Tests` | Score, règles, profil matériel, surveillance, historique. |
| `PCBoost.Optimization.Tests` | Modules, orchestrateur, annulation, récupération, profils, démarrage, nettoyage. |
| `PCBoost.Gaming.Tests` | Détection, signatures, mode Gaming, réconciliation, FPS, benchmark. |
| `PCBoost.System.Tests` | Fournisseurs Windows (tests d'intégration `[WindowsFact]` sous Windows). |
| `PCBoost.Presentation.Tests` | ViewModels (pages, navigation, confirmations, textes). |
