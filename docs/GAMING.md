# Mode Gaming — détection, optimisations, mesures

Module `src/PCBoost.Gaming` (net10.0, sans dépendance Windows directe : tout passe par les abstractions de `PCBoost.Core`).
Enregistrement : `services.AddPCBoostGaming()`.

Principes appliqués : **sécurité avant performance, montrer avant de modifier, consigner avant d'appliquer, tout restaurer,
ne jamais promettre de gain**. Aucune valeur (FPS, température, gain) n'est affichée si elle n'a pas été mesurée.

---

## 1. Détection des jeux

### 1.1 Bibliothèque installée (`GameDetectionService.GetInstalledGamesAsync`)

Chaque source est un `IGameLibraryScanner` isolé : une bibliothèque illisible (accès refusé, fichier corrompu) est journalisée
et ignorée, les autres continuent. Aucun chemin fixe n'est supposé : tout part du registre ou des dossiers connus.

| Source | Où PCBoost regarde | Données retenues |
|---|---|---|
| Steam | `HKCU\Software\Valve\Steam` **SteamPath** (repli `HKLM\SOFTWARE\Valve\Steam` **InstallPath**, vue 32 bits) → `steamapps\libraryfolders.vdf` (+ ancien `config\libraryfolders.vdf`) → `appmanifest_*.acf` | appid, name, `steamapps\common\<installdir>` (dossier existant). Outils exclus : redistribuables (228980), Proton, Steam Linux Runtime, SteamVR, SDK, serveurs dédiés. |
| Epic Games | `<ProgramData>\Epic\EpicGamesLauncher\Data\Manifests\*.item` (JSON) | DisplayName, InstallLocation, LaunchExecutable. Ignorés : `bIsIncompleteInstall`, contenus additionnels (AppName ≠ MainGameAppName), catégories sans `games`. |
| GOG | `HKLM\SOFTWARE\GOG.com\Games\*` (vue 32 bits) | gameName, path, exe. Contenus additionnels (`dependsOn`) ignorés. |
| Ubisoft Connect | `HKLM\SOFTWARE\Ubisoft\Launcher\Installs\*\InstallDir` (vue 32 bits) | Nom via la clé de désinstallation « Uplay Install &lt;id&gt; », sinon nom du dossier. |
| EA / Battle.net / Riot | Clés de désinstallation (HKLM 64 et 32 bits, HKCU) par éditeur : « Electronic Arts », « Blizzard Entertainment », « Riot Games, Inc » | DisplayName, InstallLocation. Lanceurs exclus (EA app, Origin, Battle.net, Riot Client, Riot Vanguard). Riot : aussi `<ProgramData>\Riot Games\Metadata\*\*.product_settings.yaml` (`product_install_full_path:`). |
| Xbox / Microsoft Store | Chaque disque fixe → `XboxGames\*\Content\MicrosoftGame.config` (XML, DTD interdite) | `ShellVisuals@DefaultDisplayName`, `ExecutableList/Executable@Name`, `Identity@Name`. |
| Windows (Game Bar) | `HKCU\System\GameConfigStore\Children\*` → **MatchedExeFullPath** | Exécutables existants hors dossier Windows ; nom dérivé du dossier (en ignorant `bin`, `Win64`…) ou de l'exécutable. |
| Jeux personnalisés | `GamingSettings.CustomGames` | Nom + chemin choisis par l'utilisateur. |

**Dédoublonnage** : même dossier d'installation, même exécutable, ou exécutable situé dans le dossier d'un jeu déjà listé
(ex. entrée Game Bar d'un jeu Steam). Priorité : personnalisé > Steam > Epic > GOG > Ubisoft > EA > Battle.net > Riot > Xbox > Game Bar ;
les noms d'exécutables sont fusionnés.

**Garde-fou** : un dossier d'installation trop large (racine de disque, Program Files, profil utilisateur, AppData, Windows,
`steamapps\common`, `XboxGames`) est ignoré pour la correspondance par chemin — une entrée de registre erronée ne peut pas
faire passer tous les programmes d'un dossier pour un jeu.

La bibliothèque est gardée en cache et rafraîchie **à la demande** (`refresh: true`) ou **toutes les heures**.

### 1.2 Base de signatures (`Data/game-signatures.json`, embarquée)

* `games` : ≈ 110 exécutables de jeux populaires (nom en minuscules → nom affiché), ex. `cs2.exe` → Counter-Strike 2,
  `valorant-win64-shipping.exe` → VALORANT, `tslgame.exe` → PUBG, `bg3.exe` / `bg3_dx11.exe` → Baldur's Gate 3.
* `nonGames` : lanceurs, utilitaires et installateurs jamais considérés comme des jeux (steam.exe, steamwebhelper.exe,
  EpicGamesLauncher.exe, Battle.net.exe, RiotClientServices.exe, EADesktop.exe, upc.exe, GalaxyClient.exe, XboxPcApp.exe,
  gamingservices.exe, UnityCrashHandler64.exe, CrashReportClient.exe, `vc_redist*`, `*setup*.exe`, `*installer*.exe`,
  `*uninstall*.exe`, `*launcher*.exe`…). Motifs « * » et « ? » autorisés.
* `antiCheat` : processus anti-triche (vgc, EasyAntiCheat, BEService, FACEIT, `*anticheat*.exe`…), jamais considérés comme des
  jeux et **jamais modifiés**.

Extension utilisateur : `%LOCALAPPDATA%\PCBoost\game-signatures.user.json` (même format, lu via `IFileSystemProvider`,
dossier de données issu de `BrandingOptions.DataFolderName`). Elle **ajoute** des entrées, sans pouvoir retirer celles de la base.
Un jeu déclaré par l'utilisateur l'emporte sur un motif générique (`*setup*.exe`), jamais sur un nom exact de lanceur ou d'anti-triche.

### 1.3 Jeu en cours (`DetectRunningGameAsync`) et surveillance (`StartWatching`)

1. Énumération **légère** des processus (`IProcessProvider.GetProcessIdentities` : PID + nom).
2. Exclusions : PID ≤ 4, PCBoost, lanceurs/utilitaires/anti-triche (signatures), processus critiques (`ICriticalProcessProtection`),
   exécutables du dossier Windows.
3. Correspondance **par nom** (noms d'exécutables de la bibliothèque non ambigus, puis signatures) ou **par chemin**
   (`GetExecutablePath`, lu **une seule fois par PID** et mis en cache) situé sous un dossier d'installation connu
   (le plus spécifique d'abord). Une correspondance par chemin l'emporte sur une signature (informations plus riches).
4. Plusieurs processus pour le même jeu → celui au **plus gros working set** (l'instantané mémoire n'est lu que dans ce cas).

Surveillance : `PeriodicTimer` de **5 s** ; événements `GameStarted` (un seul par démarrage, suivi par jeu et par PID) et
`GameExited` (PID disparu ou réutilisé par un autre programme). Si le processus suivi se ferme alors qu'un autre processus du
même jeu tourne encore, `GameExited` puis `GameStarted` sont émis pour le nouveau PID.

---

## 2. Mode Gaming (`GamingService`)

Machine d'états : **Inactive → Activating → Active → Restoring → Inactive** (`StateChanged` à chaque transition).
Thread-safe (`SemaphoreSlim`), idempotent : une seconde activation renvoie la session en cours, une seconde désactivation ne fait rien.

### Activation (`ActivateAsync(game)`)
1. **Profil matériel et charge** (`ISystemInfoProvider`, `IPerformanceMonitor.Latest`) — journal technique, sans donnée personnelle.
2. **Jeu** : celui passé en paramètre, sinon la détection. Un jeu déjà fermé → échec `NotFound`, rien n'est modifié.
3. **Plan** selon `GamingSettings` : `SwitchPowerPlan` → `gaming-power`, `RaiseGamePriority` → `gaming-priority`,
   `ThrottleBackgroundApps` → `gaming-background` (aperçus seulement, rien n'est modifié).
4. **Instantané** : `IRollbackManager.BeginSessionAsync(SessionType.Gaming, …)` puis `GamingSession` « Active » persistée
   (`IGamingSessionRepository`) **avant toute modification**.
5. **Application** des modules : chaque modification est consignée (write-ahead, `IChangeRecorder`) avant d'être appliquée.
6. **Surveillance** : moniteur en mode Active (s'il ne l'était pas), capture d'images si `MeasureFrameRate` et PID connu,
   `LiveMetrics` recalculées chaque seconde (FPS de la fenêtre glissante de 60 s + échantillon système + températures).

La session de restauration reste **« en cours »** pendant la partie : si PCBoost s'arrête brutalement, le RecoveryManager
la propose au démarrage suivant, et `GamingSessionReconciler.ReconcileAsync()` marque la `GamingSession` « Interrupted ».

### Désactivation (`DeactivateAsync`)
Arrêt de la capture → clôture puis `RestoreSessionAsync` de la session de restauration (jamais interrompue à mi-chemin) →
`GamingSession` Restored / RestoreFailed avec les `FrameStats` de toute la session → moniteur rendu dans son état précédent
(seulement si le mode Gaming l'avait changé) → notification « Mode Gaming désactivé — Paramètres précédents restaurés. »

Restauration automatique (`AutoRestore`) : à la sortie du jeu (`GameExited` ou PID disparu, vérifié chaque seconde).
À la fermeture de l'application, `DisposeAsync` restaure la session en cours.

### Mode automatique (`AutoGamingMode`)
`Off` → rien (la surveillance n'est pas lancée) · `Ask` → événement `ActivationSuggested` + notification
« Jeu détecté : lancement du mode Gaming ? » avec l'action `gaming.activate` (gérée par `AutoGamingMode` : active le jeu proposé) ·
`Automatic` → activation + notification. Le changement de réglage démarre/arrête la surveillance.

---

## 3. Optimisations du mode Gaming

Exposées aussi comme `IOptimization` (OptimizationManager, profils). Contexte : `context.Items["GameProcessId"]` (int),
`["GameExecutablePath"]` (string), `["GameInstallDirectory"]` (string, facultatif).

| Id | Catégorie / risque | Ce qui est fait | Consigné (ChangeKind → état « avant ») | Conditions |
|---|---|---|---|---|
| `gaming-power` | Power / Low | « Performances optimales » si présent, sinon « Performances élevées ». | `power.scheme` → `PowerSchemeState` | Non applicable si aucun des deux n'existe ou si l'un est déjà actif. Sur batterie : proposé, **non sélectionné par défaut** (avertissement). |
| `gaming-priority` | Cpu / Low | Priorité du jeu **« Supérieure à la normale »**, jamais Haute ni Temps réel. | `process.priority` → `ProcessPriorityState` | Uniquement depuis une priorité inférieure. Jeu protégé (anti-triche, accès refusé) → **ignoré proprement** : message, rien de consigné, pas d'échec. |
| `gaming-background` | Background / Medium | Mode efficacité (Windows 11) sinon priorité « Inférieure à la normale », pour au plus **15** processus. | `process.efficiency` → `ProcessEfficiencyState` ou `process.priority` → `ProcessPriorityState` | Candidats : synchronisation cloud, mises à jour, navigateurs hors premier plan, et processus **sans fenêtre mesurés > 2 % CPU** (mesure sur 1 s). Seulement depuis la priorité Normale (l'annulation rétablit exactement l'état). |
| `gaming-gpu-preference` | Gpu / Low | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`, valeur = chemin de l'exe, donnée `GpuPreference=2;` (autres paires conservées). | `registry.value` → `RegistryValueState.Capture` | Seulement avec ≥ 2 GPU dont un dédié. **Jamais sélectionné par défaut.** Persistant, prend effet au prochain lancement du jeu : il n'est donc pas inclus dans la session Gaming (qui serait restaurée avant) mais proposé par la vérification « Carte graphique utilisée par le jeu ». |

Exclusions du module d'arrière-plan (jamais ralentis) : processus critiques ou sensibles (liste Core + System32), le jeu et
ses processus (même nom ou exécutable sous le dossier du jeu), `GamingSettings.BackgroundExclusions` (Discord, OBS, Spotify
par défaut), anti-triche, **tous les lanceurs** (donc celui du jeu), pilotes graphiques/audio (NVIDIA, AMD, Intel, Realtek,
Nahimic…), communication vocale (Discord, TeamSpeak, Teams, Zoom…), diffusion et superpositions (OBS, RTSS, Overwolf…),
logiciels de périphériques (G HUB, Synapse, iCUE…), accessibilité (clavier visuel, Narrateur, Loupe), processus d'autres
utilisateurs ou sessions, fenêtre au premier plan (revérifiée juste avant l'application). **Rien n'est jamais fermé.**

## 4. Vérifications des paramètres Windows (`CheckWindowsGameSettings`)

| Id | Lecture | Correction |
|---|---|---|
| `game-mode` | `HKCU\Software\Microsoft\GameBar` **AutoGameModeEnabled** : absent ou 1 = activé | Réversible (DWORD 1) |
| `windowed-optimizations` (Windows 11) | `…\DirectX\UserGpuPreferences` **DirectXUserGlobalSettings** contient `SwapEffectUpgradeEnable=1` | Réversible, autres paires `clé=valeur;` conservées |
| `hardware-gpu-scheduling` | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers` **HwSchMode** (2 = activée) | **Lecture seule**, jamais modifiée : conseil via Paramètres › Système › Écran › Graphiques |
| `gpu-preference` | Préférence graphique du jeu (voir `gaming-gpu-preference`) | Réversible |

Les corrections (`GamingService.FixWindowsGameSettingAsync` / `WindowsGameSettingsChecker.FixAsync`) sont consignées dans une
session **Manual** du journal de restauration, annulable depuis l'historique.

## 5. Ce que PCBoost ne fait jamais en mode Gaming

Pas d'injection de DLL ni de superposition dans le jeu · pas de contournement ni de modification des anti-triche ·
pas d'overclocking, pas de réglage BIOS/UEFI · pas de désactivation de l'antivirus, du pare-feu, de Windows Update ou d'un service ·
pas de « tweak » TCP/registre (Nagle, `SystemProfile`, `Win32PrioritySeparation`…) · aucun processus Windows tué, aucun
processus fermé · priorité jamais Haute ni Temps réel · aucun lanceur ni anti-triche modifié · **aucune promesse de FPS**.

## 6. Mesure des images (FPS) via ETW

**Source** : `IFrameTimeSource` (PCBoost.System) — l'assistant élevé `PCBoost.Elevator` ouvre une session ETW temps réel
filtrée sur le PID du jeu (Microsoft-Windows-DXGI `Present_Start` 42/55, Microsoft-Windows-D3D9 `Present_Start` 1) et transmet
les horodatages par canal nommé. **Aucune injection dans le jeu.**

* Nécessite une **autorisation administrateur ponctuelle** (invite UAC) à chaque démarrage de capture ; uniquement si
  `GamingSettings.MeasureFrameRate` est activé (mode Gaming) ou si un PID de jeu est fourni explicitement (benchmark).
  Refus → `FrameCaptureAvailability.RequiresElevation`, FPS « Non disponible ».
* **Couvert** : Direct3D 9, 10, 11, 12 (via DXGI). **Non couverts** : Vulkan et OpenGL (sauf si le pilote les présente via
  une chaîne DXGI) — les FPS restent alors « Non disponible ».
* Les horodatages mesurent la **cadence de soumission des images par le jeu** (appels Present), pas l'instant d'affichage à l'écran.

**Définitions** (`FrameMetricsCalculator`, pur) :
* intervalle = horodatage(i) − horodatage(i−1) ; ignorés si ≤ 0 ms ou > 5000 ms (pause, chargement, jeu réduit) ;
* **FPS moyens** = (n − 1) × 1000 / durée, n − 1 = nombre d'intervalles retenus, durée = leur somme ;
* **1 % low** = 1000 / moyenne des ⌈1 % × (n − 1)⌉ intervalles les plus longs (au moins 1) ; **0,1 % low** : idem avec 0,1 % ;
* **temps d'image moyen** = moyenne des intervalles ; **P99** = 99e centile (rang le plus proche) des intervalles ;
* moins de deux images mesurables → `FrameStats.Empty` (toutes les valeurs null).

Exemples vérifiés par les tests : 60 FPS réguliers → 60 / 60 / 60 ; 599 intervalles de 16,67 ms + un pic de 100 ms →
1 % low = 1000 / ((100 + 5 × 16,67) / 6) ≈ 32,7 FPS, 0,1 % low = 10 FPS.

En direct : **fenêtre glissante de 60 s** (`RollingFrameWindow`) ; sans nouvelle image depuis 3 s (jeu en pause, réduit) les
FPS en direct deviennent « non disponibles ». Pour toute la session : `FrameStatsAccumulator` (mémoire bornée, histogramme à
0,05 ms : FPS moyens exacts, lows et P99 à 0,05 ms près). Benchmarks : agrégation complète exacte.

## 7. Benchmark avant/après (`BenchmarkService`)

Pendant la durée demandée : moniteur en mode Active, échantillons CPU (moy./max), GPU (moy./max), RAM (moy.), disque (moy.) et
images si un PID est fourni. Enregistrement via `IBenchmarkRepository`. `Compare(avant, après)` : écart « après − avant »
**uniquement si les deux mesures existent** (sinon `Difference = null`) ; `HigherIsBetter` vrai pour FPS / 1 % / 0,1 % low,
faux pour les usages et les temps d'image. `SampleCount = 0` signale des usages non mesurés. Aucun gain n'est calculé sans mesure.

## 8. Intégration

* **Démarrage de l'application** : `GamingSessionReconciler.ReconcileAsync()` (après la récupération d'Optimization), puis
  `IAutoGamingMode.Start()`.
* **Fermeture** : libérer le conteneur de manière asynchrone (`DisposeAsync`) pour restaurer une session en cours.
* **Textes** : clés `Game_*` (fr/en) dans `Resources/Strings.i18n.json`, dont `Game_Frames_*` (disponibilité de la capture),
  `Game_Metric_*` (libellés du benchmark, noms de `BenchmarkMetrics`) et `Game_Source_*` (source d'un jeu).

## 9. Limites connues

* Xbox : seul le dossier par défaut `XboxGames` de chaque disque est lu (le fichier binaire `.GamingRoot` des dossiers
  personnalisés n'est pas interprété) ; ces jeux restent détectables par la Game Bar ou les signatures.
* Les exécutables Steam ne sont pas énumérés (coût disque) : un jeu Steam est reconnu par son dossier d'installation.
* Pas de relation parent/enfant entre processus dans les abstractions : les « processus du jeu » sont reconnus par leur
  nom et leur emplacement sous le dossier du jeu.
* Le mode efficacité exige Windows 11 (sinon repli sur la priorité « Inférieure à la normale »).
