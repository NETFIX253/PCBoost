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
| Modifier le BIOS/UEFI, overclocker CPU/GPU/RAM | Aucune fonctionnalité ni dépendance pour cela. |
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
- Toute autre demande est refusée ; l'annulation de l'invite UAC est traitée comme un refus (aucune action).
- La désinstallation assistée ne passe pas par l'assistant administrateur : PCBoost lance le programme officiel de l'éditeur (Windows gère l'invite d'autorisation). Seules deux formes de commande sont acceptées — Windows Installer avec un code produit, toujours en mode interactif (`msiexec /x {code}`), ou un exécutable désigné par un chemin absolu — ; les interpréteurs de commandes et hôtes de scripts (`cmd`, PowerShell, `rundll32`, `mshta`…) sont refusés, la commande est relue dans le registre juste avant le lancement et aucune option silencieuse n'est ajoutée. Mises à jour, pilotes, composants d'exécution, logiciels de sécurité et PCBoost ne sont jamais proposés.

## 4. Restauration et récupération

- Écriture anticipée : l'état avant est enregistré dans SQLite **avant** chaque modification (`ChangeRecorder`), avec l'identifiant de session.
- Annulation d'une modification ou d'une session entière (ordre inverse) depuis l'Historique.
- Après un arrêt brutal, les sessions restées « en cours » sont détectées au démarrage et l'utilisateur choisit : restaurer ou conserver. Une session Gaming interrompue est restaurée automatiquement.
- Les optimisations automatiques (désactivées par défaut) sont limitées aux actions à faible risque et réversibles, et apparaissent dans l'Historique.

## 5. Intégrité des mises à jour

- Aucun serveur n'est configuré par défaut : **aucune connexion réseau**.
- `LocalUpdateProvider` lit un flux local ou d'entreprise (`update.json` + MSI) si `updateFeedPath` est renseigné dans `branding.json` ; le paquet doit se trouver dans le même dossier que le flux (nom simple, pas de chemin), le flux est de taille bornée et l'empreinte **SHA-256** du paquet est vérifiée avant toute proposition d'installation. L'installation reste une action explicite de l'utilisateur.
- `PlaceholderRemoteUpdateProvider` documente l'emplacement d'un futur fournisseur distant ; il ne contacte rien et répond « non configuré ».
- Les livrables publiés sont accompagnés de `SHA256SUMS.txt`. La signature de code (Authenticode) de `PCBoost.exe`, `PCBoost.Elevator.exe` et du MSI est recommandée avant diffusion publique (voir la liste de publication).

## 6. Confidentialité

- **Télémétrie : désactivée**, et aucun code d'envoi n'existe. Aucune donnée personnelle ne quitte le PC.
- Données stockées localement dans `%LOCALAPPDATA%\PCBoost` : réglages, historique des modifications (pour pouvoir les annuler), résultats d'analyse, historique des performances agrégé, mesures avant/après, journal d'activité, journaux techniques (7 fichiers de 10 Mo au maximum).
- Les journaux masquent le chemin du profil (`%USERPROFILE%`), le nom d'utilisateur (`<user>`) et le nom du PC (`<machine>`) ; si le filtrage échoue, le message est remplacé plutôt qu'écrit en clair.
- PCBoost ne lit pas le contenu des documents, ne collecte ni historique de navigation ni identifiants ; le nettoyage des caches de navigateurs ne touche ni favoris, ni mots de passe, ni historique.
- La page *Confidentialité* de l'application liste ces données et ouvre les dossiers correspondants.
- Le **rapport de diagnostic** est produit localement et n'est jamais envoyé : l'utilisateur en voit l'aperçu, choisit son contenu (nom du PC exclu par défaut, journaux anonymisés et historique inclus) et l'enregistre lui-même. Le nom d'utilisateur, le dossier personnel et le nom du PC sont masqués ; les chemins des processus ne figurent pas dans le rapport. L'aperçu utilise un moteur WebView2 isolé (dossier de données propre à PCBoost, scripts, menus contextuels, outils de développement et navigation externe désactivés).
- La recherche de doublons lit le contenu des fichiers des dossiers personnels uniquement pour en calculer l'empreinte (SHA-256), localement ; les fichiers cachés, système, en ligne (OneDrive non téléchargés) et les liens ne sont pas lus.

## 7. Signaler un problème de sécurité

Écrire à l'éditeur (adresse de support indiquée dans *À propos* une fois renseignée dans `branding.json`), avec la version, le système et les étapes de reproduction. Merci de ne pas publier le détail d'une vulnérabilité avant correction.
