# Historique des versions

Format inspiré de [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) ; versions selon [SemVer](https://semver.org/lang/fr/).

## [1.2.0] — 2026-10-04

### Ajouté
- Mises à jour de pilotes sécurisées (nouvelle page « Pilotes ») : recherche dans Windows Update uniquement (pilotes signés et validés par Microsoft, ciblés sur le matériel du PC) ; le serveur de l'organisation (WSUS), les stratégies qui excluent les pilotes et les mises à jour masquées sont respectés.
- Versions stables d'abord : seules les mises à jour recommandées par Windows, publiées depuis au moins 14 jours et plus récentes que le pilote installé sont cochées. Les mises à jour facultatives, récentes, les pilotes de démarrage ou de sécurité (contrôleurs de disque, processeur, TPM) et les versions non comparables sont affichés « à examiner », jamais cochés d'office.
- Jamais installés : microprogrammes (BIOS, UEFI, firmware), versions identiques ou plus anciennes que le pilote installé, mises à jour pour lesquelles aucun périphérique présent n'est identifié, mises à jour demandant une intervention ou l'acceptation d'une licence (à faire depuis Windows Update).
- Aperçu de ce qui va être modifié (pilote par pilote, version actuelle → nouvelle) avant toute installation ; une seule autorisation administrateur ; progression détaillée (point de restauration, téléchargement, installation, vérification).
- Revérification juste avant l'installation (configuration de Windows, état des périphériques, offre de Windows Update), puis point de restauration Windows neuf et obligatoire : s'il ne peut pas être créé, aucun pilote n'est installé. Si la protection du système est désactivée, PCBoost propose de l'activer, uniquement avec l'accord explicite de l'utilisateur ; l'activation est inscrite à l'Historique.
- Retour au pilote précédent en un clic, sur la page Pilotes ou depuis l'Historique (« Restaurer le pilote » de Windows), périphérique par périphérique, uniquement pour ceux qui utilisent encore la version installée par PCBoost : un pilote modifié depuis n'est jamais remplacé. Chaque pilote est inscrit au journal de restauration avant son installation, avec l'état relu de chacun de ses périphériques. Pour une extension ou un composant de pilote, le retour se fait avec le point de restauration (indiqué avant l'installation).
- Vérification de chaque périphérique après sa mise à jour : s'il signale un problème (hors « redémarrage nécessaire ») ou ne peut pas être vérifié, les installations suivantes ne sont pas faites et le retour au pilote précédent est proposé. Si l'assistant administrateur ne rend aucun résultat, l'installation est signalée « résultat inconnu » et le retour reste proposé.
- Autres sources fiables : selon la marque du PC (HP, Dell, Lenovo, ASUS, Acer, MSI, Dynabook/Toshiba, Fujitsu) et la carte graphique (NVIDIA, AMD, Intel), PCBoost indique l'outil et le site officiels du fabricant (liste fermée d'adresses vérifiées) ; il ne télécharge et n'installe rien depuis ces sources. Bouton « Créer un point de restauration maintenant » à utiliser avant l'outil d'un fabricant.
- Avertissements : connexion limitée (taille à télécharger), redémarrage en attente, Windows Update déjà occupé, service Windows Update désactivé (jamais réactivé par PCBoost).
- Lien « Rechercher des mises à jour de pilotes » dans la section Périphériques de la page Santé du matériel.

### Modifié
- La version de PCBoost s'affiche dans la barre de titre, à côté du nom, et le pied du menu est plus compact : les onze entrées du menu restent visibles sur un écran de 768 px de haut.

### Corrigé
- Les boutons qui ouvrent une page des Paramètres Windows (« Applications installées » de Windows, réglages de jeu) affichaient « lien non autorisé » : ils ouvrent désormais la page demandée, choisie dans une liste fermée.

### Sécurité
- Nouvelles opérations de l'assistant administrateur, à paramètres strictement validés : `drivers.install` (identifiants de mises à jour Windows Update uniquement ; configuration, périphériques et offre revérifiés juste avant l'installation), `driver.rollback` (identifiants de périphériques avec leur version précédente et la version installée ; aucun fichier INF ni identifiant matériel accepté) et `restorepoint.drivers`. Aucun chemin, aucune adresse et aucune commande ne sont acceptés.
- La limite de Windows d'un point de restauration par 24 heures est levée le temps d'une seule création, avant une installation de pilotes, puis rétablie à l'identique (valeur documentée `SystemRestorePointCreationFrequency`) ; un verrou empêche deux créations simultanées et une note de reprise permet de rétablir la valeur si l'assistant s'arrête brutalement.
- Aucune décision sur des données partielles : une lecture incomplète des périphériques est traitée comme un échec ; deux mises à jour d'un même périphérique ne sont jamais installées ensemble ; une annulation attend la fin de tout assistant administrateur encore actif.

## [1.1.0] — 2026-09-29

### Ajouté
- Santé du matériel (nouvelle page) : état des disques signalé par Windows, usure des SSD, température, heures de fonctionnement et erreurs de lecture (compteurs détaillés lus avec une autorisation administrateur ponctuelle) ; notification si un disque est signalé en mauvais état, au plus une fois par jour.
- Batterie des portables : capacité actuelle comparée à la capacité d'origine, cycles de charge, technologie.
- Surchauffe : détection d'un processeur ralenti sous forte charge (sur secteur) et des limitations de vitesse enregistrées par Windows, avec conseils de refroidissement (dépoussiérage, aération).
- Périphériques en erreur signalés par le Gestionnaire de périphériques (information uniquement, aucun pilote modifié).
- Durée du démarrage mesurée par Windows sur la page Démarrage : dernier démarrage complet, comparaison avant / après vos changements de programmes au démarrage, programmes signalés comme ayant ralenti le démarrage.
- Rapport de diagnostic : aperçu, puis enregistrement en PDF ou HTML à joindre à une demande d'assistance ; nom d'utilisateur, dossier personnel et (par défaut) nom du PC masqués ; produit localement, sans connexion.
- Point de restauration Windows créé automatiquement avant le niveau Avancé de l'assistant « PC ancien » (réglable dans Paramètres) ; un point de moins de 24 heures est réutilisé.
- Désinstallation assistée (Nettoyage → Programmes installés) : taille, date d'installation, dernière utilisation connue et repérage des programmes peu utilisés ; désinstallation par le programme officiel de l'éditeur après confirmation, inscrite au journal. Les mises à jour, pilotes, composants d'exécution, logiciels de sécurité et PCBoost ne sont jamais proposés.
- Gros fichiers et doublons (Stockage et Nettoyage) : fichiers de plus de 256 Mo et fichiers identiques (contenu vérifié par SHA-256) dans les dossiers personnels ; envoi à la Corbeille uniquement, une copie de chaque doublon toujours conservée, chaque fichier revérifié juste avant.
- Réglages par jeu pour le mode Gaming (alimentation, priorité, applications en arrière-plan, mesure des FPS) et historique des FPS mesurés par jeu.
- Nouvelle catégorie « Santé du matériel » dans l'analyse et dans « Pourquoi mon PC est lent ? ».

### Modifié
- Masquage des données personnelles dans les journaux : le nom du PC est masqué en entier même s'il contient le nom d'utilisateur (« BUREAU-AMIN » devient « <machine> »), et un nom d'utilisateur très court (ex. « hp ») ne masque plus les mots courants comme « HP ».

### Sécurité
- Nouvelles opérations de l'assistant administrateur, sans paramètre et en lecture seule sauf le point de restauration : compteurs de fiabilité des disques, mesures de démarrage, dernières exécutions des programmes, création d'un point de restauration.
- L'aperçu du rapport s'affiche dans un moteur WebView2 isolé (dossier de données propre à PCBoost, scripts et navigation externe désactivés).
- Désinstallation : seules les commandes Windows Installer (toujours interactives) et les exécutables de l'éditeur désignés par un chemin absolu sont lancés ; interpréteurs de commandes et hôtes de scripts refusés ; commande relue juste avant le lancement.

## [1.0.0] — 2026-09-28

Première version.

### Ajouté
- Tableau de bord : score de santé sur 100 explicable, charge en temps réel, recommandations principales, actions rapides.
- Analyse du système (lecture seule) : matériel, logiciels, constats par catégorie, profil matériel, conseils matériels factuels.
- « Pourquoi mon PC est lent ? » : facteurs observés, impact estimé et niveau de confiance.
- Optimisation en un clic avec aperçu, étapes (Analyse, Préparation, Sauvegarde, Optimisation, Vérification, Terminé) et rapport final.
- Profils Équilibré, Productivité, Gaming, Économie d'énergie, Personnalisé ; assistant « PC ancien » (Essentiel, Standard, Avancé).
- Nettoyage par catégories SÛR / PRUDENCE / AVANCÉ, fichiers récents et en cours d'utilisation conservés.
- Gestion du démarrage réversible (mécanisme du Gestionnaire des tâches), tâches planifiées de démarrage non Microsoft.
- Gestionnaire de processus avec confiance (signature) et protection des processus critiques.
- Restauration : historique des sessions, annulation d'une modification ou d'une session, récupération après arrêt brutal.
- Mode Gaming : détection des jeux (Steam, Epic, Xbox, Battle.net, Riot, Ubisoft, EA, GOG, Windows, ajouts manuels), activation manuelle / sur demande / automatique, restauration automatique.
- Mesure des FPS, 1 % low, 0,1 % low et temps d'image par ETW, sans injection ; mesure avant / après.
- Performances en temps réel et historique (30 s à 7 jours), stockage, mode Expert, journal d'activité.
- Zone de notification, notifications Windows, lancement avec Windows, thèmes Clair / Sombre / Système, français et anglais.
- Installateur MSI (raccourcis, lancement avec Windows facultatif), version portable, empreintes SHA-256.
- Architecture de mises à jour : flux local ou d'entreprise vérifié par SHA-256 ; aucun serveur distant configuré.

### Sécurité
- Aucune modification de Microsoft Defender, du Pare-feu, de Windows Update, de SmartScreen, de BitLocker ou des services Windows ; cibles de registre et de fichiers interdites vérifiées à chaque couche.
- Application exécutée en utilisateur standard ; assistant administrateur à liste blanche fermée, lancé ponctuellement par UAC.
- Aucune télémétrie ; journaux sans données personnelles.
