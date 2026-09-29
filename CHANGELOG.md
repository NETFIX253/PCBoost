# Historique des versions

Format inspiré de [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) ; versions selon [SemVer](https://semver.org/lang/fr/).

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
