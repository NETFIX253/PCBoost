# Sécurité et confidentialité de PCBoost

> La stabilité et la sécurité passent **avant** la performance. PCBoost préfère ne rien faire plutôt que risquer d'abîmer un PC.

## 1. Engagements

1. Aucune optimisation n'est appliquée sans vérification de sa pertinence sur ce PC.
2. L'utilisateur voit ce qui va être modifié **avant** l'application (aperçu), et peut refuser.
3. Chaque modification du système est **réversible** et enregistrée avant d'être faite ; les rares actions irréversibles (suppression de fichiers temporaires, vidage de la corbeille, désinstallation d'un programme par son programme officiel) sont signalées comme telles et confirmées. Les fichiers personnels choisis sur la page « Gros fichiers et doublons » vont uniquement à la Corbeille ; si Windows ne peut pas les y placer, il demande avant toute suppression définitive.
4. Aucun gain n'est promis : seuls les écarts mesurés (avant / après) sont affichés.
5. Aucune modification permanente non documentée : tout est visible dans l'Historique et le Mode Expert.

## 2. Ce que PCBoost ne fait jamais

| Interdit | Garde-fou dans le code |
|---|---|
| Désactiver Microsoft Defender, le Pare-feu, Windows Update, SmartScreen, BitLocker, Secure Boot, les protections mémoire, les mécanismes de récupération | `ForbiddenTargetPolicy` refuse toute clé de registre contenant `\Windows Defender`, `\Microsoft Antimalware`, `\WindowsUpdate`, `\SharedAccess\Parameters\FirewallPolicy`, `\SecurityHealthService`, `\SmartScreen`, `\BitLocker`, `\FVE`, `\SecureBoot`, `\Control\DeviceGuard`, `\Memory Management`, `\Control\Lsa`, `\Winlogon`, `\Image File Execution Options`… |
| Désactiver ou modifier des services Windows | Toute clé sous `\CurrentControlSet\Services` est interdite ; aucun module ne touche aux services. |
| « Tweaks » réseau TCP, BCD, HPET, MMCSS non documentés | `\Tcpip\Parameters`, `\BCD00000000`, `\Multimedia\SystemProfile` interdits ; aucun appel `bcdedit`. |
| Supprimer des fichiers système | Chemins interdits : `\Windows\System32\`, `\SysWOW64\`, `\WinSxS\`, `\Boot\`, `\servicing\`, `\SoftwareDistribution\DataStore\`, `\System Volume Information\`, `\Recovery\`, données de Defender, corbeille système. Le nettoyage ne travaille que dans des dossiers de catalogue, sans sortir de leur racine (liens et jonctions exclus), en conservant les fichiers récents et ceux en cours d'utilisation. |
| Fermer ou ralentir un processus essentiel | `CriticalProcessProtection` : 56 processus critiques (dont `explorer.exe`, `csrss.exe`, `wininit.exe`, `winlogon.exe`, `services.exe`, `lsass.exe`, `smss.exe`, `dwm.exe`, `svchost.exe`, `MsMpEng.exe`, `SecurityHealthService.exe`) et 20 processus sensibles (pilotes audio/graphiques, anti-triche…), plus les processus d'autres sessions et de PCBoost lui-même. Vérifié par l'optimiseur, le gestionnaire de processus et un test de sécurité dédié. |
| Modifier le BIOS/UEFI, overclocker CPU/GPU/RAM | Aucune fonctionnalité ni dépendance pour cela. Les mises à jour de pilotes excluent toute mise à jour de microprogramme (classe `Firmware`, titres « BIOS », « UEFI », « Firmware », « Capsule »), à l'affichage puis de nouveau dans l'Elevator. |
| Installer des pilotes de source inconnue, revenir à une version plus ancienne | Seule source : l'agent Windows Update (catalogue Microsoft, pilotes signés WHQL). Aucun téléchargement direct, aucun « pack de pilotes ». `DriverUpdatePolicy` refuse toute version identique ou plus ancienne que le pilote installé ; les sites des fabricants sont seulement ouverts dans le navigateur (liste fermée d'adresses HTTPS). |
| Injecter une DLL dans un jeu, contourner un anti-triche | Les FPS sont mesurés par ETW (événements de présentation Windows) depuis l'extérieur du jeu ; les processus anti-triche ne sont jamais modifiés (ni priorité, ni mode efficacité). |
| Qualifier un logiciel de « virus » ou affirmer « aucun virus » | Vocabulaire limité à « Signé par… », « Non signé », « Signature invalide », « Inconnu », « À examiner ». PCBoost n'est pas un antivirus. |
| Modifications de registre en masse ou arbitraires | Seules des valeurs précises, documentées par module, sont modifiées ; l'état avant est sauvegardé. |

## 3. Modèle d'élévation

- L'application s'exécute en **utilisateur standard** (`asInvoker`).
- Une opération privilégiée passe par `PCBoost.Elevator.exe` (manifeste `requireAdministrator`), lancé par UAC pour **une seule** requête, qui se termine ensuite.
- Liste blanche **fermée** d'opérations :
  - `cleanup.category` — l'identifiant d'une catégorie du catalogue ; le chemin est résolu par l'Elevator lui-même (jamais transmis par l'appelant) et les règles du nettoyage s'appliquent.
  - `registry.set` / `registry.delete` — uniquement sous `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run, Run32, StartupFolder}` (activation d'un élément de démarrage « tous les utilisateurs », comme le Gestionnaire des tâches).
  - `task.setenabled` — activer/désactiver une tâche planifiée de démarrage **non Microsoft**.
  - `frames.capture` — session ETW de présentation d'images pour un PID donné, diffusée par canal nommé à usage unique.
  - `disk.reliability` — lecture seule des compteurs de fiabilité des disques (`MSFT_StorageReliabilityCounter` : usure, température, heures de fonctionnement, erreurs de lecture) ; aucun paramètre.
  - `boot.performance` — lecture seule du journal *Diagnostics-Performance* (durées de démarrage, éléments ayant ralenti le démarrage) ; aucun paramètre.
  - `apps.lastrun` — lecture seule des **noms** des fichiers du dossier Prefetch et de leur date (dernière exécution d'un programme) ; le contenu des fichiers n'est pas lu ; aucun paramètre.
  - `restorepoint.create` — création d'un point de restauration Windows (« PCBoost - avant optimisation avancée ») avant le niveau Avancé de l'assistant PC ancien ; un point de moins de 24 heures est réutilisé, la création est vérifiée ; la protection du système n'est jamais activée ni modifiée ; aucun paramètre.
  - `drivers.install` — installation de mises à jour de pilotes désignées par leur **identifiant Windows Update** (GUID, 16 au plus), dans cet ordre : configuration de Windows relue (stratégie excluant les pilotes, service Windows Update désactivé, redémarrage en attente, restauration interdite → rien n'est fait) ; périphériques relus (liste illisible → rien n'est fait), nouvelle recherche Windows Update et revérification de chaque mise à jour par `DriverUpdatePolicy` ; s'il reste une mise à jour installable, point de restauration neuf « PCBoost - avant mise à jour des pilotes » (sans lui, rien n'est installé) ; téléchargement et installation une par une par l'agent Windows Update, vérification des périphériques après chacune (arrêt si un périphérique signale un nouveau problème — le code 14 « redémarrage nécessaire » n'en est pas un — ou ne peut pas être relu). Paramètre `enableProtection` : activer la protection du système du lecteur Windows seulement si l'utilisateur l'a explicitement cochée alors que le choix lui était proposé (refusé si une stratégie la désactive) ; l'activation est inscrite à l'Historique. Les conditions de licence ne sont jamais acceptées à la place de l'utilisateur. Progression facultative par canal nommé à usage unique (`PCBoost.Progress.<guid>`, écriture seule par l'Elevator, niveau d'emprunt d'identité limité à l'identification).
  - `driver.rollback` — retour au pilote précédent pour des **identifiants d'instance de périphériques** (8 au plus), chacun avec **sa** version précédente, et la version installée par la mise à jour (tous obligatoires ; aucun fichier, chemin ni identifiant matériel accepté). « Restaurer le pilote » de Windows (`DiRollbackDriver`) n'est appliqué qu'aux périphériques encore sur la version installée par PCBoost : déjà sur la version précédente → rien ; pilote modifié depuis → jamais remplacé ; périphérique absent ou liste illisible → rien n'est tenté (jamais compté comme réussi). Aucune réinstallation forcée d'un fichier INF. Résultat vérifié par la version du pilote de chaque périphérique.
  - `restorepoint.drivers` — point de restauration neuf à la demande (avant l'utilisation de l'outil d'un fabricant), même création que pour `drivers.install` ; seul paramètre `enableProtection`.
- Pour qu'un point de restauration soit réellement créé juste avant une installation de pilotes, la limite de Windows (un point par 24 heures, valeur documentée `SystemRestorePointCreationFrequency` sous `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore`) est levée le temps de cette seule création, puis rétablie à l'identique (valeur DWORD précédente ou absence de valeur ; une valeur d'un autre type n'est jamais touchée). Un verrou système nommé empêche deux créations simultanées. L'état d'origine est d'abord noté sous `HKLM\SOFTWARE\PCBoost\RestorePointFrequency` (modifiable par les seuls administrateurs) ; si l'assistant s'arrête avant de rétablir la valeur, l'assistant suivant la rétablit au démarrage de n'importe quelle opération, puis supprime la note.
- Toute autre demande est refusée ; l'annulation de l'invite UAC est traitée comme un refus (aucune action).
- La désinstallation assistée ne passe pas par l'assistant administrateur : PCBoost lance le programme officiel de l'éditeur (Windows gère l'invite d'autorisation). Seules deux formes de commande sont acceptées — Windows Installer avec un code produit, toujours en mode interactif (`msiexec /x {code}`), ou un exécutable désigné par un chemin absolu — ; les interpréteurs de commandes et hôtes de scripts (`cmd`, PowerShell, `rundll32`, `mshta`…) sont refusés, la commande est relue dans le registre juste avant le lancement et aucune option silencieuse n'est ajoutée. Mises à jour, pilotes, composants d'exécution, logiciels de sécurité et PCBoost ne sont jamais proposés.

## 4. Mises à jour de pilotes

- **Source** : l'agent Windows Update de Windows, sans serveur imposé (celui de l'organisation, WSUS, est utilisé s'il est configuré). Critère de recherche : pilotes non installés et non masqués. La recherche ne demande aucune autorisation et ne télécharge rien.
- **Stratégies respectées, jamais contournées** : « Ne pas inclure les pilotes avec les mises à jour Windows » (stratégie de groupe ou MDM) → aucune recherche ; service Windows Update désactivé → expliqué, jamais réactivé ; restauration du système désactivée par stratégie → aucune installation. Ces réglages (et le redémarrage en attente) sont relus à la demande d'installation puis par l'Elevator juste avant d'installer.
- **Classement** (`DriverUpdatePolicy`) : recommandée (présélectionnée) = proposée automatiquement par Windows, publiée depuis au moins 14 jours, plus récente que le pilote installé (même éditeur : numéro de version ; éditeurs différents : date du pilote) ; à examiner (jamais présélectionnée) = facultative, récente, pilote de démarrage ou de sécurité, version non comparable ; exclue = microprogramme, version identique ou plus ancienne, aucun périphérique présent identifié (son état « avant » ne pourrait pas être noté), interaction ou licence requise.
- **Réversibilité** : point de restauration Windows neuf et obligatoire ; périphériques relus au moment de la demande, puis chaque pilote inscrit au journal de restauration **avant** l'installation (version installée et, par périphérique, pilote précédent : version, date, éditeur, INF), puis marqué installé ou en échec. Sans résultat de l'assistant (délai dépassé, arrêt inattendu), la modification reste « en attente » — jamais « en échec » — et son annulation reste proposée. Annulation pilote par pilote depuis la page Pilotes ou l'Historique ; pour une extension ou un composant logiciel de pilote, une version inconnue ou un périphérique sans pilote précédent, le retour se fait par le point de restauration (indiqué avant l'installation et dans l'Historique). L'installation, l'annulation et le point de restauration à la demande ne s'exécutent jamais en même temps ; une annulation attend la fin de tout assistant administrateur de PCBoost encore actif (installation dont le résultat n'a pas été reçu) avant d'agir ou de conclure.
- **Données fiables uniquement** : une lecture incomplète des périphériques (requête WMI interrompue, délai dépassé) est un échec — jamais une liste partielle — : ni état « avant », ni revérification, ni annulation ne s'appuient sur des données incomplètes. Deux mises à jour visant un même périphérique ne sont jamais installées ensemble (refus dans l'application et dans l'Elevator).
- **Autres sources** : outils et sites officiels des fabricants du PC et de la carte graphique, ouverts dans le navigateur à la demande (liste fermée d'adresses HTTPS vérifiées). PCBoost ne télécharge et n'exécute rien depuis ces sources ; il recommande de créer d'abord un point de restauration et de ne faire une mise à jour du BIOS proposée par ces outils que sur secteur et sur conseil du service informatique.

## 5. Restauration et récupération

- Écriture anticipée : l'état avant est enregistré dans SQLite **avant** chaque modification (`ChangeRecorder`), avec l'identifiant de session.
- Annulation d'une modification ou d'une session entière (ordre inverse) depuis l'Historique.
- Après un arrêt brutal, les sessions restées « en cours » sont détectées au démarrage et l'utilisateur choisit : restaurer ou conserver. Une session Gaming interrompue est restaurée automatiquement.
- Les optimisations automatiques (désactivées par défaut) sont limitées aux actions à faible risque et réversibles, et apparaissent dans l'Historique.

## 6. Intégrité des mises à jour

- Aucun serveur n'est configuré par défaut : **aucune connexion réseau**.
- `LocalUpdateProvider` lit un flux local ou d'entreprise (`update.json` + MSI) si `updateFeedPath` est renseigné dans `branding.json` ; le paquet doit se trouver dans le même dossier que le flux (nom simple, pas de chemin), le flux est de taille bornée et l'empreinte **SHA-256** du paquet est vérifiée avant toute proposition d'installation. L'installation reste une action explicite de l'utilisateur.
- `PlaceholderRemoteUpdateProvider` documente l'emplacement d'un futur fournisseur distant ; il ne contacte rien et répond « non configuré ».
- Les livrables publiés sont accompagnés de `SHA256SUMS.txt`. La signature de code (Authenticode) de `PCBoost.exe`, `PCBoost.Elevator.exe` et du MSI est recommandée avant diffusion publique (voir la liste de publication).

## 7. Confidentialité

- **Télémétrie : désactivée**, et aucun code d'envoi n'existe. Aucune donnée personnelle ne quitte le PC.
- Données stockées localement dans `%LOCALAPPDATA%\PCBoost` : réglages, historique des modifications (pour pouvoir les annuler), résultats d'analyse, historique des performances agrégé, mesures avant/après, journal d'activité, journaux techniques (7 fichiers de 10 Mo au maximum).
- Les journaux masquent le chemin du profil (`%USERPROFILE%`), le nom d'utilisateur (`<user>`) et le nom du PC (`<machine>`) ; si le filtrage échoue, le message est remplacé plutôt qu'écrit en clair.
- PCBoost ne lit pas le contenu des documents, ne collecte ni historique de navigation ni identifiants ; le nettoyage des caches de navigateurs ne touche ni favoris, ni mots de passe, ni historique.
- La page *Confidentialité* de l'application liste ces données et ouvre les dossiers correspondants.
- Le **rapport de diagnostic** est produit localement et n'est jamais envoyé : l'utilisateur en voit l'aperçu, choisit son contenu (nom du PC exclu par défaut, journaux anonymisés et historique inclus) et l'enregistre lui-même. Le nom d'utilisateur, le dossier personnel et le nom du PC sont masqués ; les chemins des processus ne figurent pas dans le rapport. L'aperçu utilise un moteur WebView2 isolé (dossier de données propre à PCBoost, scripts, menus contextuels, outils de développement et navigation externe désactivés).
- La recherche de doublons lit le contenu des fichiers des dossiers personnels uniquement pour en calculer l'empreinte (SHA-256), localement ; les fichiers cachés, système, en ligne (OneDrive non téléchargés) et les liens ne sont pas lus.

## 8. Signaler un problème de sécurité

Écrire à l'éditeur (adresse de support indiquée dans *À propos* une fois renseignée dans `branding.json`), avec la version, le système et les étapes de reproduction. Merci de ne pas publier le détail d'une vulnérabilité avant correction.
