# Historique des versions

Format inspiré de [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) ; versions selon [SemVer](https://semver.org/lang/fr/).

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
