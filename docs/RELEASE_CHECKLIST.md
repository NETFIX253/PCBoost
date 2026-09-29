# Liste de vérification avant publication

À dérouler pour chaque version. Cocher chaque ligne ; toute ligne non satisfaite bloque la publication.

## 1. Identité et version
- [ ] `build/Branding.props` : `BrandVersion` incrémentée ; `BrandUpgradeCode` **inchangé** depuis la première publication.
- [ ] `src/PCBoost.App/Assets/Branding/branding.json` : nom et éditeur identiques à `Branding.props` (vérifié par `BrandingConsistencyTests`), slogan (fr/en), liens de support et site (ou `null`), dossier de données.
- [ ] `CHANGELOG.md` : section de la version datée, changements visibles décrits en langage simple.

## 2. Compilation et tests
- [ ] `build\build.ps1` : compilation Release x64 **sans avertissement**, tous les tests réussis (tests d'intégration Windows compris).
- [ ] `build\build.ps1 -Platform ARM64 -SkipTests` compile (si ARM64 est distribué).
- [ ] Aucune chaîne d'interface en dur ; `python build\tools\i18n.py` ne signale aucune traduction manquante.

## 3. Vérifications fonctionnelles (PC réel, utilisateur standard)
- [ ] Premier lancement : bienvenue, analyse, score affiché avec le détail de chaque facteur ; valeurs non mesurables marquées « Non disponible ».
- [ ] Optimisation en un clic : l'aperçu liste les modifications ; annuler à l'aperçu ne modifie rien ; appliquer puis restaurer la session remet l'état initial (mode d'alimentation, effets visuels, démarrage, priorités).
- [ ] Arrêt forcé pendant une optimisation (Gestionnaire des tâches) : au redémarrage, le bandeau de récupération propose la restauration et elle aboutit.
- [ ] Nettoyage : seules les catégories SÛR sont cochées par défaut ; confirmation affichée ; espace libéré cohérent ; les fichiers en cours d'utilisation sont conservés.
- [ ] Démarrage : désactiver puis réactiver un programme ; l'état est identique dans le Gestionnaire des tâches.
- [ ] Processus : les processus critiques sont marqués protégés et ne proposent pas « Terminer ».
- [ ] Mode Gaming : lancer un jeu → détection ; activation → optimisations listées ; fermeture du jeu → restauration automatique. FPS affichés si l'autorisation est accordée, « Non disponible » sinon.
- [ ] Mesure avant / après : comparaison affichée uniquement pour deux mesures comparables.
- [ ] Zone de notification : Ouvrir, Mode Gaming, Optimiser, Pause surveillance, Paramètres, Quitter.
- [ ] Thèmes Clair / Sombre / Système et langues français / anglais sur toutes les pages (`PCBoost.exe --capture-dir … --theme … --lang …`).
- [ ] Clavier seul : toutes les actions principales accessibles ; Narrateur lit les cartes et les boutons.
- [ ] Journaux (`%LOCALAPPDATA%\PCBoost\logs`) : aucun nom d'utilisateur, nom de PC ou chemin de profil en clair.

## 4. Livrables
- [ ] `build\build.ps1 -Publish -Installer` produit dans `dist\` : MSI, ZIP portable, `SHA256SUMS.txt`, `README.md`, `CHANGELOG.md`.
- [ ] Signature Authenticode de `PCBoost.exe`, `PCBoost.Elevator.exe` et du MSI (certificat de l'éditeur), puis recalcul de `SHA256SUMS.txt`.
- [ ] MSI : installation, lancement depuis le menu Démarrer, options raccourci Bureau et lancement avec Windows, mise à niveau depuis la version précédente, désinstallation complète (Program Files et raccourcis supprimés ; `%LOCALAPPDATA%\PCBoost` conservé).
- [ ] Version portable : décompression et lancement sur un PC sans .NET installé.
- [ ] Analyse des livrables par Microsoft Defender : aucune détection.

## 5. Sécurité et confidentialité
- [ ] Aucune nouvelle cible hors des règles de `docs/SECURITY.md` ; nouvelles optimisations documentées dans `docs/OPTIMIZATIONS.md` avec leur annulation.
- [ ] Télémétrie toujours absente ; aucune connexion réseau au démarrage (vérifier avec le Moniteur de ressources).
- [ ] Dépendances à jour, sans vulnérabilité connue (`dotnet list package --vulnerable --include-transitive`).
