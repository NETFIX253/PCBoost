# PCBoost — optimisations, nettoyage et restauration

Ce document décrit **exactement** ce que le module `PCBoost.Optimization` peut modifier sur un PC, pourquoi, avec quel
risque, et comment chaque modification est annulée. Il liste aussi les « optimisations » volontairement **non
implémentées**. Il fait référence pour le support, les revues de sécurité et la rédaction des textes de l'interface.

Principes appliqués partout :

1. **Aperçu avant tout changement** : chaque module expose un `PreviewAsync` (dry-run) qui ne modifie rien.
2. **Consigner avant d'agir (write-ahead)** : toute modification passe par `IChangeRecorder`. L'état « avant » est
   persisté (statut `Pending`) **avant** l'exécution ; sans journal disponible, rien n'est modifié.
3. **Validation de sûreté** (`OptimizationSafetyValidator`) avant chaque module et avant chaque modification.
4. **Annulation** en un clic, en ordre inverse, par des gestionnaires idempotents (`IChangeHandler`).
5. **Rien d'inventé** : les impacts sont mesurés ou « non mesurés » ; aucun gain n'est promis.
6. **Aucune modification automatique**, sauf le nettoyage des fichiers temporaires de l'utilisateur de plus de 7 jours,
   uniquement si l'utilisateur l'a autorisé (réglage `AllowAutomaticSafeOptimizations`).

---

## 1. Restauration (§10) et récupération (§70)

| Élément | Rôle |
|---|---|
| `RollbackManager` | Ouvre une session `InProgress` persistée (`IOptimizationHistoryRepository`), la clôt (`Completed`, `PartiallyCompleted`, `Failed`), restaure une session ou une modification, purge l'historique au-delà de `HistoryRetentionDays` (90 jours par défaut). |
| `ChangeRecorder` | 1) `ValidateChange` → cible interdite ou type inconnu = `Blocked`, **rien n'est exécuté** (la tentative est consignée `Failed`) ; 2) persiste le `ChangeRecord` `Pending` + numéro de séquence ; 3) exécute ; 4) marque `Applied` ou `Failed` (+ détail technique). Les actions irréversibles sont consignées `Irreversible`. |
| Restauration | Annule en **ordre inverse** les modifications `Applied`, `Pending` (état incertain après un plantage) et `RollbackFailed` (nouvelle tentative). Les gestionnaires sont idempotents : annuler deux fois ne change rien. Statut final `RolledBack` ou `PartiallyRolledBack` ; les actions irréversibles sont comptées à part, jamais « annulées ». |
| `RecoveryManager` | Au démarrage, toute session restée `InProgress` (hors celles du processus courant) passe `Interrupted` et est proposée à la restauration ; `Dismiss` la conserve telle quelle (`Dismissed`). |

### Gestionnaires d'annulation

| Type (`ChangeKinds`) | Gestionnaire | Ce qui est rétabli | Cas particuliers |
|---|---|---|---|
| `registry.value` | `RegistryValueChangeHandler` | La valeur exacte d'origine (type + données) ou sa suppression si elle n'existait pas. | HKLM non inscriptible → PCBoost.Elevator (`registry.set` / `registry.delete`, paramètres `path`, `name`, `valueBase64`, `view`) **uniquement** pour une valeur binaire ≤ 16 octets d'une clé de `ForbiddenTargetPolicy.ElevatedRegistryAllowList` (StartupApproved). UAC refusé → `ElevationCancelled`, modification `RollbackFailed`, nouvelle tentative possible. |
| `power.scheme` | `PowerSchemeChangeHandler` | Le plan d'alimentation actif d'origine (`PowerSetActiveScheme`). | Plan supprimé entre-temps → échec explicite `NotFound`. |
| `process.priority` | `ProcessPriorityChangeHandler` | La priorité d'origine. | Seulement si le **même** processus (PID + heure de démarrage) tourne encore ; sinon succès « rien à restaurer » (l'effet a disparu avec le processus). Jamais « Temps réel ». |
| `process.efficiency` | `ProcessEfficiencyChangeHandler` | L'état d'origine du mode efficacité (EcoQoS). | Même règle PID + heure de démarrage. |
| `visual.effects` | `VisualEffectsChangeHandler` | L'état complet `VisualEffectsState` (9 réglages). | — |
| `scheduledtask.enabled` | `ScheduledTaskChangeHandler` | L'état activé/désactivé d'origine. | Accès refusé → Elevator `task.setenabled` (`path`, `enabled`). Tâches `\Microsoft\…` jamais modifiées. Tâche supprimée → « rien à restaurer ». |
| `file.delete`, `recyclebin.empty` | `IrreversibleChangeHandler` | — | `CanUndo = false` : consignées pour l'historique uniquement. |

### Validation de sûreté (§57)

Refus bloquants (messages `Opt_Safety_*`) :

- build Windows inférieur à `IOptimization.MinimumWindowsBuild` (17763 = Windows 10 1809 pour tous les modules) ;
- aperçu non applicable ou ne correspondant pas au module ;
- risque élevé sans `plan.HighRiskActionsConfirmed` ;
- changement irréversible sélectionné sans `plan.IrreversibleActionsConfirmed` (c'est le cas de `temp-files`) ;
- cible interdite (`ForbiddenTargetPolicy.IsForbiddenTarget` sur le libellé **et** sur l'état sérialisé : clé de registre réelle, tâche Microsoft) ;
- type de modification inconnu ; modification réversible sans état « avant ».

---

## 2. Modules d'optimisation

Chaque module applique **uniquement** les changements sélectionnés (`OptimizationContext.IsSelected`) et les consigne
via `IChangeRecorder`. Un module qui échoue n'interrompt pas les autres (`OptimizationManager`).

### `temp-files` — Fichiers temporaires

| | |
|---|---|
| Pourquoi | Les dossiers temporaires et rapports d'erreurs grossissent indéfiniment ; sur un petit disque, le manque d'espace ralentit Windows (mises à jour, fichier d'échange). |
| Ce qui est modifié | Suppression des fichiers des catégories **SAFE** du catalogue fermé `CleanupCatalog` (voir §3), via `CleanupExecutor` ; catégories système par **une seule** demande Elevator `cleanup.category` (identifiants uniquement, jamais de chemin). |
| Aperçu | Un changement par catégorie SAFE disponible et non vide (`temp-files:<id>`, taille estimée), tous présélectionnés ; `RequiresElevation` si une catégorie système est concernée. |
| Risque / impact | Faible / selon la taille mesurée (≥ 2 Gio élevé, ≥ 500 Mio moyen, sinon faible). |
| Annulation | **Irréversible** (`Reversible = false`) : confirmation explicite exigée ; chaque catégorie est consignée `Irreversible` avec le nombre d'octets libérés. |

### `startup-apps` — Applications au démarrage

| | |
|---|---|
| Pourquoi | Chaque programme lancé à l'ouverture de session retarde le démarrage et occupe mémoire et processeur en permanence. |
| Ce qui est modifié | La valeur **StartupApproved** de l'entrée, comme le Gestionnaire des tâches — l'entrée Run / le raccourci / la tâche ne sont **jamais** supprimés. Désactivé = `03 00 00 00` + FILETIME UTC (12 octets) ; activé = `02 00 … 00` (12 octets). Clés : `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, `…\StartupFolder` ; HKLM : `SOFTWARE\…\StartupApproved\Run`, `\Run32`, `\StartupFolder` (vue 64 bits). Tâches planifiées : `Enabled` via le Planificateur. |
| Aperçu | Entrées activées recommandées « Peut être désactivé » ; présélectionnées si l'impact **mesuré** est élevé ou moyen. Jamais les entrées « À conserver » ni les logiciels de sécurité. |
| Risque / impact | Faible / élevé. |
| Annulation | Valeur StartupApproved d'origine restaurée à l'octet près (ou supprimée si absente) ; HKLM non inscriptible → Elevator. |

### `power-plan` — Mode d'alimentation

| | |
|---|---|
| Pourquoi | Le mode « Économie d'énergie » bride le processeur ; inversement, le profil Économie d'énergie le demande explicitement. |
| Ce qui est modifié | Le plan actif (`PowerSetActiveScheme`) — aucun plan n'est créé, modifié ou supprimé. |
| Cible | Sans profil / « one-click » : seulement si « Économie d'énergie » est actif **et** le PC est sur secteur → « Utilisation normale ». `balanced` / `productivity` → Utilisation normale ; `power-saver` → Économie d'énergie (si présent) ; `gaming` → Performances optimales si présent, sinon Performances élevées, sinon non applicable (fréquent sur les PC à veille moderne). Déjà actif → non applicable. |
| Risque / impact | Faible / moyen. |
| Annulation | Plan d'origine réactivé (`power.scheme`). |

### `visual-effects` — Effets visuels

| | |
|---|---|
| Pourquoi | Sur un processeur graphique intégré ancien ou avec peu de mémoire, animations et transparence coûtent de la fluidité. |
| Ce qui est modifié | `SystemParametersInfo` (utilisateur courant) : animations de fenêtres, menus, listes déroulantes, info-bulles, zone cliente, ombre du curseur, défilement fluide ; transparence (`HKCU\…\Themes\Personalize\EnableTransparency`). « Afficher le contenu des fenêtres pendant le déplacement » est conservé. |
| Condition | `HardwareProfile.Tier` ∈ {LegacyLowResource, Entry, LowEnd}, ou `context.Items["force"] = true` (assistant ancien PC niveau Standard, profil Économie d'énergie). |
| Risque / impact | Faible / moyen sur les petites configurations. |
| Annulation | État complet d'origine (`visual.effects`). |

### `background-apps` — Applications en arrière-plan

| | |
|---|---|
| Pourquoi | Synchronisation, messageries et outils de mise à jour consomment processeur et disque même inutilisés. |
| Ce qui est modifié | Mode efficacité (EcoQoS, `SetProcessInformation`, Windows 11) ou, à défaut, priorité « Inférieure à la normale ». **Aucun processus n'est fermé.** Au plus 15 processus. |
| Sélection | Processus de l'utilisateur, session courante, non au premier plan : connus comme lourds (OneDrive, Dropbox, GoogleDriveFS, Teams, Skype, AdobeCollabSync, outils de mise à jour Adobe/Java/Google/Edge, jusched, iTunesHelper…) **ou** mesurés > 5 % du processeur **sans fenêtre**. Exclus : processus critiques/sensibles et ceux de System32, liste « jamais touchés » (explorer, csrss, wininit, winlogon, services, lsass, smss, dwm, svchost…), logiciels de sécurité, `GamingSettings.BackgroundExclusions`, PCBoost, priorités déjà non normales. Les garde-fous sont revérifiés juste avant la modification. |
| Risque / impact | Moyen (une synchronisation peut être ralentie) / moyen. Effet limité à la vie du processus. |
| Annulation | État d'origine si le même processus tourne encore (`process.efficiency` / `process.priority`). |

### Profils (§15), assistant ancien PC (§23), surveillance (§29)

- **Profils** : Équilibré (`power-plan`), Productivité (`power-plan`, `background-apps`, `visual-effects` si petite config),
  Jeu (`power-plan` gaming, `background-apps`), Économie d'énergie (`power-plan` saver, `background-apps`,
  `visual-effects` forcé), Personnalisé (`profiles.custom`). Activer un profil **restaure d'abord** la session du profil
  précédent, puis applique le nouveau (`SessionType.Profile`). État : `profiles.state`.
- **Assistant ancien PC** — constats mesurés : RAM ≤ 4 Go ou > 85 % utilisée ; ≤ 2 cœurs, ou ≤ 4 threads avec une
  fréquence de base < 2 GHz ; disque système mécanique ; < 15 % d'espace libre ; > 8 applications au démarrage ;
  > 120 processus en arrière-plan. Niveaux cumulatifs : Essentiel = `temp-files` + `startup-apps` (impact élevé seulement) ;
  Standard = + `visual-effects` (forcé) + `power-plan` ; Avancé = + `background-apps`, traité comme risque élevé
  (`HighRiskActionsConfirmed` exigé).
- **Surveillance intelligente** (toutes les 30 min, rien pendant une session Jeu) : notification « Votre PC semble
  ralentir » si la mémoire est restée > 90 % sur les 5 dernières minutes (au plus toutes les 4 h) ; notification
  « {0} Go peuvent être récupérés » au-delà du seuil (au plus une fois par jour, analyse au plus toutes les 6 h).
  Seule action automatique, si autorisée : fichiers temporaires de l'utilisateur de plus de **7 jours** (session
  `Automatic`, consignée, notifiée, au plus une fois par jour).

---

## 3. Catégories de nettoyage (§11)

Le catalogue est **fermé** : aucun chemin arbitraire, racines issues de dossiers connus, liens non suivis, chemins
interdits exclus (`ForbiddenTargetPolicy`), fichiers en lecture seule/système/verrouillés conservés. Documents, images,
vidéos, musique, bureau et téléchargements ne figurent dans **aucune** catégorie. Toute suppression est irréversible et
consignée `Irreversible`.

| Id | Emplacement | Sûreté | Âge min. | Élévation | Défaut | Pourquoi / précautions |
|---|---|---|---|---|---|---|
| `user-temp` | `%TEMP%` | SAFE | 1 j | non | oui | Fichiers laissés par les applications. |
| `windows-temp` | `%WINDIR%\Temp` | SAFE | 1 j | oui | oui | Restes d'installations et de Windows. |
| `internet-cache` | `%LOCALAPPDATA%\Microsoft\Windows\INetCache` | SAFE | 1 j | non | oui | Cache des composants web ; retéléchargé si besoin. |
| `user-error-reports` | `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive`, `ReportQueue` | SAFE | 7 j | non | oui | Rapports déjà traités. |
| `user-crash-dumps` | `%LOCALAPPDATA%\CrashDumps\*.dmp` | SAFE | 7 j | non | oui | Images mémoire d'applications. |
| `system-error-reports` | `%PROGRAMDATA%\Microsoft\Windows\WER\…` | SAFE | 7 j | oui | oui | Rapports système. |
| `system-crash-dumps` | `%WINDIR%\Minidump\*.dmp`, `%WINDIR%\MEMORY.DMP` | CAUTION | 7 j | oui | non | Utiles à un technicien pour un écran bleu récurrent. |
| `recycle-bin` | Corbeille (tous lecteurs, `SHEmptyRecycleBin`) | CAUTION | — | non | non | Fichiers supprimés par l'utilisateur : perte définitive. |
| `thumbnail-cache` | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db` | CAUTION | — | non | non | Recréées ; dossiers photo plus lents au début. |
| `browser-edge` | `…\Edge\User Data\*\Cache\Cache_Data`, `Code Cache`, `GPUCache` | CAUTION | — | non | non | Favoris, mots de passe et historique non touchés. **Edge fermé** exigé. |
| `browser-chrome` | idem pour Chrome | CAUTION | — | non | non | **Chrome fermé** exigé. |
| `browser-firefox` | `…\Mozilla\Firefox\Profiles\*\cache2` | CAUTION | — | non | non | **Firefox fermé** exigé. |
| `directx-shader-cache` | `%LOCALAPPDATA%\D3DSCache` | ADVANCED | 1 j | non | non | Les jeux peuvent saccader pendant la reconstruction. |

Navigateur ouvert → catégorie indisponible (« Fermez {navigateur} ») à l'analyse **et** au nettoyage. Refus UAC →
`ElevationCancelled` pour les seules catégories système ; les autres continuent. Annulation en cours de route → les
catégories restantes sont renvoyées « annulées » et ce qui a été supprimé reste consigné.

---

## 4. Démarrage (§12) — recommandations

Sources lues : `HKCU\…\Run`, `HKLM\…\Run` (vue 64), `HKLM\…\Run` vue 32 bits (WOW6432Node), dossiers Démarrage utilisateur
et commun (raccourcis résolus, `desktop.ini` ignoré), tâches d'ouverture de session **non Microsoft**. Impact **mesuré**
sur le(s) processus lancé(s) depuis le même exécutable : élevé si mémoire > 300 Mo, processeur cumulé > 60 s ou lecture
disque > 500 Mo ; moyen au-delà de 100 Mo / 15 s / 100 Mo ; sinon faible ; non lancé → **non mesuré**.

Ordre des recommandations : sécurité → À conserver ; pilotes (audio Realtek, pavé tactile Synaptics/ELAN, graphiques
Intel/NVIDIA/AMD) → À conserver ; exécutable introuvable → À examiner ; non signé / signature invalide / éditeur inconnu
→ À examiner ; OneDrive → Facultatif ; applications connues (Discord, Spotify, Steam, Epic, EA, Ubisoft Connect,
Battle.net, Teams, Skype, Zoom, Dropbox, Google Drive, outils de mise à jour Adobe/Java/Apple, iTunesHelper, CCleaner,
préchargement Edge…) → Peut être désactivé ; autre composant Microsoft → À conserver ; sinon Facultatif. Les logiciels
de sécurité ne peuvent pas être désactivés par PCBoost. Rien n'est jamais désactivé automatiquement.

---

## 5. Processus (§13) et confiance (§36)

- CPU % = Δ temps processeur / (Δ temps écoulé × processeurs logiques) ; disque = Δ octets lus + écrits / s. Premier appel
  (ou instantané de plus de 30 s) : deux échantillons à 500 ms.
- Fermeture (`WM_CLOSE`) et terminaison : refusées (`Blocked`) pour les processus critiques de la liste de protection,
  la liste « jamais touchés » et PCBoost lui-même ; journalisées.
- Confiance : signature Authenticode + métadonnées + emplacement. « Composant Windows » = signé Microsoft **et** sous
  `%WINDIR%`. L'éditeur n'est affiché que s'il est attesté par une signature valide. Jamais « malveillant » :
  au pire « Non signé », « Signature invalide », « Inconnu ».

---

## 6. Point de restauration, désinstallation assistée, gros fichiers et doublons

- **Point de restauration Windows** (réglage activé par défaut) : avant le niveau Avancé de l'assistant PC ancien,
  `RestorePointService` demande `restorepoint.create` à l'assistant administrateur. Un point de moins de 24 heures est
  réutilisé (comme Windows) ; protection du système désactivée ou refus d'autorisation → l'utilisateur choisit de continuer
  sans point (la restauration propre à PCBoost reste active) ou d'annuler. Le résultat figure au journal et au rapport.
- **Désinstallation assistée** : inventaire des clés de désinstallation sans mises à jour, composants système, entrées
  non désinstallables, composants d'exécution (Visual C++, .NET, Windows App Runtime, WebView2…), pilotes, logiciels de
  sécurité ni PCBoost. « Peu utilisé » = dernière utilisation connue il y a plus de 90 jours (Prefetch lu avec
  autorisation, sinon dernier accès aux fichiers, qui ne peut que surestimer l'usage récent). Le programme officiel est
  lancé après confirmation (action définitive), puis PCBoost attend la disparition de la clé (10 minutes au plus,
  l'utilisateur peut arrêter d'attendre ; le programme de désinstallation n'est jamais interrompu).
- **Gros fichiers et doublons** : Documents, Téléchargements, Bureau, Images, Vidéos, Musique ; fichiers ≥ 256 Mo (100 au
  plus) et doublons ≥ 1 Mo confirmés par taille, échantillons de début et de fin puis SHA-256 complet (20 Go comparés au
  plus). Rien n'est présélectionné ; une copie de chaque groupe est toujours conservée ; chaque fichier est revérifié
  (présence, taille, date) avant l'envoi à la Corbeille.

---

## 7. Optimisations volontairement NON implémentées

| « Optimisation » | Pourquoi PCBoost refuse |
|---|---|
| « Nettoyeur de RAM » (vidage des working sets, `EmptyWorkingSet`, purge de la liste d'attente) | Contre-productif : Windows utilise la mémoire libre comme cache ; la vider force des relectures disque (lenteurs, surtout sur disque dur) et le gain affiché est illusoire et temporaire. |
| Désactivation de services Windows (SysMain/Superfetch, Windows Search, Spooler, DiagTrack…) | Effets de bord imprévisibles (recherche, impression, mises à jour), gains non mesurables sur un PC normal ; `ForbiddenTargetPolicy` interdit toute cible `service:` et `…\CurrentControlSet\Services`. |
| Désactivation de Defender, du pare-feu, de SmartScreen, de Windows Update, de BitLocker | Sécurité avant performance : interdit par la politique produit et par `ForbiddenTargetPolicy` (défense en profondeur jusque dans l'Elevator). |
| « Hacks » TCP/IP (`Tcpip\Parameters`, TCPNoDelay, fenêtre TCP, `NetworkThrottlingIndex`) | Mythes hérités de Windows XP ; la pile réseau moderne s'ajuste seule ; risques de régression. Clés interdites. |
| MMCSS / `Multimedia\SystemProfile` (SystemResponsiveness, priorités « Games ») | Gains non démontrés, risques de saccades audio ; clé interdite. |
| HPET / `useplatformclock`, `disabledynamictick`, autres modifications du BCD | Peut dégrader la minuterie et la stabilité ; modification du démarrage non réversible simplement ; BCD interdit. |
| Réglages `Memory Management` (DisablePagingExecutive, LargeSystemCache, ClearPageFileAtShutdown), taille du fichier d'échange | Windows gère mieux ; risque d'instabilité ou de plantage par manque de mémoire ; clé interdite. |
| Désactivation de la sécurité basée sur la virtualisation / HVCI / Core Isolation | Réduit la protection du système ; `DeviceGuard` interdit. |
| Modification de `Image File Execution Options`, `Winlogon`, `Lsa` | Vecteurs classiques de détournement ; interdits. |
| « Nettoyeur de registre » | Aucun gain mesurable, risque réel de casser des applications. |
| Défragmentation / TRIM forcés | Windows les planifie déjà (et ne défragmente pas les SSD) ; forcer peut user un SSD. |
| Terminaison automatique de processus | Perte de travail non enregistré ; seule une action explicite de l'utilisateur peut terminer un processus non critique. |
| Priorité « Temps réel » | Peut figer le système (entrées, audio, pilotes). |
| Création ou déverrouillage du plan « Performances optimales » (`powercfg -duplicatescheme`) | Modification persistante hors du périmètre réversible ; consommation accrue ; le plan n'est utilisé que s'il existe déjà. |
| Désinstallation d'applications préinstallées, suppression de fichiers système, `WinSxS`, points de restauration | Risque pour la réparation du système ; hors périmètre (chemins interdits). |
| Toute promesse chiffrée (« +200 FPS », « PC 2× plus rapide ») | Aucune mesure ne la justifie : seuls les résultats mesurés (benchmark avant/après) sont affichés. |
