# Score de santé, constats et profil matériel

Ce document décrit précisément comment le module `PCBoost.Diagnostics` calcule le score affiché sur le tableau de bord,
quand il produit un constat de santé, comment il classe le matériel et sur quels critères reposent le diagnostic
« Pourquoi mon PC est lent ? » et les conseils matériels. Tout est mesuré : **une valeur qui n'a pas pu être lue
n'est jamais remplacée par une estimation**.

Le code de référence est `src/PCBoost.Diagnostics/Scoring/PerformanceScoreCalculator.cs`. Les exemples chiffrés
ci-dessous sont vérifiés par le test `PerformanceScoreCalculatorTests.Documented_examples_match`.

---

## 1. Score sur 100

### 1.1 Facteurs

| Facteur (`Id`) | Points max | Tous les points si… | 0 point si… | Page d'action |
|---|---:|---|---|---|
| Mémoire vive (`score.memory`) | 25 | utilisation ≤ 60 % | utilisation ≥ 95 % | `processes` |
| Charge du processeur (`score.cpu`) | 15 | moyenne ≤ 30 % | moyenne ≥ 90 % | `processes` |
| Espace libre du disque système (`score.storage`) | 20 | ≥ 25 % libre | ≤ 5 % libre | `storage` |
| Applications au démarrage (`score.startup`) | 15 | ≤ 5 activées | ≥ 20 activées | `startup` |
| Fichiers récupérables (`score.cleanable`) | 10 | ≤ 500 Mo | ≥ 10 Go | `cleanup` |
| Activité du disque (`score.disk`) | 10 | ≤ 30 % actif | ≥ 95 % actif | `performance` |
| Redémarrage récent (`score.uptime`) | 5 | uptime ≤ 3 jours | uptime ≥ 14 jours | `diagnosis` |
| **Total** | **100** | | | |

Tailles en unités binaires : 1 Mo = 1 024² octets, 1 Go = 1 024³ octets.

### 1.2 Calcul d'un facteur

Entre les deux bornes, les points sont interpolés linéairement puis arrondis à l'entier le plus proche
(0,5 est arrondi vers le haut) :

```
fraction = clamp((borne_mauvaise − valeur) / (borne_mauvaise − borne_bonne), 0, 1)
points   = arrondi(points_max × fraction)
```

La même formule s'applique que « plus bas » soit mieux (mémoire, CPU…) ou que « plus haut » soit mieux (espace libre).

Mesures utilisées :

- **Mémoire** : moyenne de l'échantillonnage de l'analyse (3 s par défaut, 5 s pour le diagnostic « PC lent ») ;
  si aucun échantillon n'a pu être pris, l'instantané de la mémoire lu au début de l'analyse.
- **CPU** et **activité disque** : moyennes de l'échantillonnage (pas de 500 ms), ou moyenne de l'historique du moniteur
  temps réel s'il couvre déjà la durée demandée.
- **Espace libre** : lecteur système (`IsSystemDrive`).
- **Démarrage** : entrées **activées** (les entrées désactivées ne comptent pas).
- **Fichiers récupérables** : somme des catégories de nettoyage **SAFE** disponibles.
- **Uptime** : temps écoulé depuis le dernier démarrage de Windows.

### 1.3 Statut d'un facteur

| Statut | Condition |
|---|---|
| `Good` | points ≥ 80 % des points max |
| `Fair` | points ≥ 40 % des points max |
| `Poor` | sinon |
| `Unknown` | mesure indisponible (voir 1.4) |

### 1.4 Facteurs non mesurables : exclusion et remise à l'échelle

Un facteur est `Unknown` (0 point, explication « Cette mesure n'est pas disponible sur ce PC ») dans les cas suivants :

| Facteur | Considéré comme non mesuré si… |
|---|---|
| Mémoire | la mémoire totale n'a pas pu être lue (0) |
| CPU | aucun échantillon de charge n'a pu être pris |
| Espace libre | aucun lecteur système, ou taille totale nulle |
| Démarrage | la liste des entrées de démarrage est vide (le rapport ne distingue pas « aucune entrée » de « énumération impossible » ; Windows en liste presque toujours au moins une) |
| Fichiers récupérables | scan de nettoyage non effectué ou en échec |
| Activité disque | compteur de disque indisponible, ou aucun échantillon |
| Uptime | valeur non lue (nulle) |

Les facteurs inconnus sont **exclus**, et le score est remis à l'échelle sur les facteurs mesurés :

```
score = arrondi(100 × Σ points des facteurs mesurés / Σ points max des facteurs mesurés)
```

Quand tout est mesuré, le dénominateur vaut 100 et **le score est exactement la somme des points affichés**.
Un facteur inconnu ne compte donc ni comme « parfait » ni comme « nul ». Si aucun facteur n'est mesurable,
le score vaut 0 et tous les facteurs sont `Unknown` : l'interface doit alors afficher « Non disponible ».

### 1.5 Bornes fixes

Les bornes du score sont fixes : le score reste comparable d'une analyse à l'autre et dans l'historique.
Les seuils personnalisables (`HealthThresholds`) n'agissent que sur les constats de santé (section 2),
les recommandations et le diagnostic « PC lent ».

### 1.6 Exemples

**PC sain** — mémoire 37 %, CPU 10 %, 39 % d'espace libre, 3 applications au démarrage, 100 Mo récupérables,
disque actif 5 %, redémarré hier : chaque facteur obtient tous ses points → **100**.

**PC modeste** — 8 Go de RAM utilisés à 78 %, CPU moyen 35 %, 12 % d'espace libre, 9 applications au démarrage,
2,5 Go récupérables, disque actif 20 %, uptime 5 jours :

| Facteur | Calcul | Points | Statut |
|---|---|---:|---|
| Mémoire | 25 × (95 − 78) / 35 = 12,1 | 12 | Fair |
| CPU | 15 × (90 − 35) / 60 = 13,75 | 14 | Good |
| Espace libre | 20 × (12 − 5) / 20 = 7 | 7 | Poor |
| Démarrage | 15 × (20 − 9) / 15 = 11 | 11 | Fair |
| Récupérable | 10 × (10 − 2,5) / (10 − 0,49) = 7,9 | 8 | Good |
| Activité disque | 20 % ≤ 30 % | 10 | Good |
| Uptime | 5 × (14 − 5) / 11 = 4,1 | 4 | Good |
| **Score** | somme | **66** | |

**Même PC, mesures partielles** — le compteur d'activité disque est indisponible et le scan de nettoyage a échoué :
ces deux facteurs (10 + 10 points) sont exclus. Score = arrondi(100 × (12 + 14 + 7 + 11 + 4) / 80) = **60**.

---

## 2. Constats de santé (moteur de règles)

Chaque règle implémente `IHealthRule` ; le moteur exécute toutes les règles enregistrées en DI et trie les constats
par sévérité décroissante. Une règle ne produit rien si sa mesure est absente. Seuils par défaut (`HealthThresholds`,
modifiables dans les réglages) :

| Règle (`RuleId`) | Catégorie | Déclenchement (défaut) | Sévérité |
|---|---|---|---|
| `health.memory` | memory | ≥ 80 % / ≥ 90 % | Medium / High |
| `health.storage.system-free` | storage | ≤ 15 % libre / ≤ 10 % libre | Medium / High |
| `health.startup.count` | startup | ≥ 8 / ≥ 15 applications activées | Medium / High |
| `health.cpu.sustained` | cpu | moyenne ≥ 70 % / ≥ 90 % | Medium / High |
| `health.disk.active` | disk | moyenne ≥ 80 % / ≥ 95 % | Medium / High |
| `health.thermal.cpu` | thermal | ≥ 85 °C (si mesurée) | High |
| `health.thermal.gpu` | thermal | ≥ 85 °C (si mesurée) | High |
| `health.thermal.storage` | thermal | ≥ 65 °C (si mesurée) | High |
| `health.system.uptime` | system | ≥ 7 jours | Low |
| `health.cleanup.recoverable` | cleanup | ≥ 2 Go récupérables (SAFE) | Low |
| `health.processes.background` | processes | ≥ 180 processus en arrière-plan | Low |
| `health.power.saver-on-ac` | power | plan « Économie d'énergie » actif sur secteur | Info |
| `health.system.unsupported-build` | system | build < 17763 (Windows 10 1809) ; ignoré si la build n'a pas pu être lue | High |

`Severity.Critical` n'est émis par aucune règle intégrée (réservé aux règles ajoutées).
« Processus en arrière-plan » = processus de l'utilisateur courant, dans sa session, sans fenêtre, non critiques
(PCBoost lui-même exclu).

---

## 3. Profil matériel

Critères évalués **dans cet ordre** ; le premier qui s'applique détermine le niveau :

| Niveau | Critère |
|---|---|
| `Unknown` | mémoire totale illisible (0) |
| `LegacyLowResource` | RAM ≤ 4 Go, **ou** disque système HDD avec ≤ 2 cœurs physiques |
| `Entry` | RAM ≤ 6 Go, **ou** ≤ 2 cœurs physiques |
| `LowEnd` | RAM ≤ 8 Go **et** (≤ 4 cœurs **ou** aucun GPU dédié) |
| `HighEnd` | RAM ≥ 16 Go **et** ≥ 8 cœurs **et** GPU dédié avec ≥ 6 Go de mémoire vidéo |
| `MidRange` | tous les autres cas |

- **RAM nominale** : Windows signale la mémoire *utilisable*, toujours un peu inférieure à la mémoire installée
  (réservations matérielles). Elle est arrondie au Go supérieur : 7,8 Gio → 8 Go, 15,8 Gio → 16 Go. Même règle pour la mémoire vidéo.
- **Cœurs illisibles** (0) : jamais considérés comme « peu de cœurs » ; seuls les critères mesurés s'appliquent.
- **GPU dédié** : adaptateur ni logiciel (« Microsoft Basic Render Driver ») ni probablement intégré.
- Indicateurs : `LowMemory` = RAM ≤ 6 Go ; `FewCores` = ≤ 2 cœurs mesurés ; `SystemDriveIsHdd` ;
  `LowSystemDriveSpace` = moins de 15 % libre ; `HasDedicatedGpu`.
- `Reasons` : critères décisifs d'abord, puis les autres faits mesurés (RAM, cœurs, type de disque, GPU, espace faible).

Exemples : 4 Go → LegacyLowResource ; HDD + 2 cœurs + 8 Go → LegacyLowResource ; 16 Go + 2 cœurs → Entry ;
8 Go + 4 cœurs → LowEnd ; 32 Go + 8 cœurs + GPU 8 Go → HighEnd ; 16 Go + 8 cœurs + GPU 4 Go → MidRange.

---

## 4. Recommandations

Les recommandations liées à un seuil ne sont produites que si le constat correspondant existe : elles respectent donc
les seuils personnalisés. Identifiants stables (masquables par l'utilisateur) :

| Id | Condition | Type | Impact | Action |
|---|---|---|---|---|
| `rec.startup.heavy` | constat démarrage | Recommendation | High | « Voir les applications » → `startup` (`startup-apps`) |
| `rec.memory.high` | constat mémoire | Recommendation | High si critique, sinon Medium | `processes` |
| `rec.cpu.high` | constat CPU | Recommendation | idem | `processes` |
| `rec.disk.busy` | constat disque actif | Recommendation | idem | `performance` |
| `rec.storage.low-space` | constat espace libre | Recommendation | idem | `storage` |
| `rec.cleanup.recoverable` | constat fichiers récupérables | Recommendation | Medium si l'espace manque, sinon Low | `cleanup` (`temp-files`) |
| `rec.processes.background` | constat arrière-plan | Recommendation | Medium | `processes` (`background-apps`) |
| `rec.power.balanced` | plan Économie d'énergie sur secteur | Recommendation | Medium | `optimization` (`power-plan`) |
| `rec.visual-effects.modest-pc` | profil Legacy/Entry, ou LowEnd avec constat mémoire/CPU | Recommendation | Medium (Legacy) / Low | `optimization` (`visual-effects`) |
| `rec.system.restart` | constat uptime | Recommendation | Medium | — |
| `rec.system.update-windows` | build non prise en charge | Recommendation | Low | — |
| `rec.thermal.check` | constat de température | Possibility | Medium | `performance` |
| `rec.hardware.ssd` | disque système HDD | Possibility | High | `oldpc` |
| `rec.hardware.ram` | RAM ≤ 8 Go et constat mémoire | Possibility | Medium | — |

Tri : recommandations concrètes avant les pistes (`Possibility`), puis impact et confiance décroissants.
Les pistes matérielles n'ont jamais d'action d'achat et ne sont jamais présentées comme nécessaires.

---

## 5. « Pourquoi mon PC est lent ? »

Réutilise la dernière analyse si elle a moins de 2 minutes, sinon en lance une (échantillonnage de 5 s, scan de nettoyage inclus).
Un facteur n'est retenu que s'il atteint le seuil « warning » configuré. `NoSignificantFactor` = aucun facteur retenu.

| Facteur | Condition | Impact | Confiance |
|---|---|---|---|
| Mémoire (nomme les 3 plus gros processus) | ≥ seuil RAM warning | High si ≥ critique, sinon Medium | High |
| Démarrage chargé | ≥ seuil démarrage warning | High si ≥ critique, sinon Medium | High |
| Disque saturé | activité ≥ seuil warning | High | Medium (mesure courte) |
| Windows sur HDD | disque système HDD | High | High |
| CPU élevé (nomme le processus principal) | ≥ seuil CPU warning | High si ≥ critique, sinon Medium | Medium |
| Espace disque faible | ≤ seuil espace warning | High si ≤ critique, sinon Medium | High |
| Température CPU / GPU / disque | ≥ seuil (si mesurée) | High | Medium |
| Plan Économie d'énergie sur secteur | constaté | Medium | High |
| Uptime long | ≥ seuil jours | Low | Medium |
| Nombreux processus en arrière-plan | ≥ seuil | Low | Medium |

Facteurs triés par impact puis confiance décroissants.

---

## 6. Conseils matériels

Observation factuelle mesurée, puis suggestion au conditionnel ; rien n'est vendu.

| Id | Condition | Confiance |
|---|---|---|
| `hardware.ram` | RAM ≤ 8 Go **et** mémoire > 85 % sur au moins 25 % des instantanés de l'historique (au moins 10 instantanés) | Low < 30 instantanés, Medium < 240, High au-delà |
| `hardware.ssd` | disque système HDD | High |
| `hardware.storage.full` | disque système rempli à plus de 90 % | High |
| `hardware.gpu.integrated` | uniquement des GPU intégrés (pertinent pour le jeu) | Medium |
| `hardware.cpu.saturated` | CPU > 90 % sur au moins 25 % des instantanés (au moins 10) | comme la RAM |

Sans historique suffisant, aucun conseil RAM ou CPU n'est donné.

---

## 7. Surveillance et historique

- Moniteur temps réel : `Active` = 1 échantillon/s, `Background` = 1 toutes les 5 s, `Paused` = aucun échantillonnage
  (minuterie suspendue). Températures toutes les 5 s en Active, 30 s en Background. Historique en mémoire : 30 minutes.
- Historique persistant : une moyenne par minute (`PerformanceSnapshot`), purge des instantanés de plus de 30 jours
  une fois par jour. Aucun instantané n'est créé pendant une pause (pas de données, pas de valeur supposée).
