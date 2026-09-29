# Libellés statiques des vues (UI_STRINGS)

Source unique : `src/PCBoost.Presentation/Resources/Strings.i18n.json` (fr puis en), .resx générés par
`python3 build/tools/i18n.py src/PCBoost.Presentation/Resources/Strings.i18n.json`. La source de ressources
`PCBoost.Presentation.Resources.Strings` est enregistrée par `AddPCBoostPresentation()`.

Ce document liste, page par page, les clés **statiques** destinées au XAML (titres, sous-titres, en-têtes de section,
boutons, états vides, info-bulles, noms accessibles). Les textes dépendant des données sont déjà exposés
formatés par les ViewModels (propriétés `…Text`, `Title`, `Description`, `AccessibleName`…) : ne pas les relire depuis ces clés.

Règles :

- aucune chaîne visible en dur dans le XAML : résoudre ces clés via `ILocalizer.Get(clé)` (extension de balisage ou convertisseur de l'App) ;
- recharger la page après `ShellViewModel.PageReloadRequested` (changement de langue) ;
- `Common_NotAvailable`, `Error_*`, `Cleanup_{id}_*` et `Protection_*` sont fournis par d'autres modules ;
- les glyphes dépendant des données sont exposés par les ViewModels (`IconGlyph`, `StatusGlyph`…, constantes dans `PCBoost.Presentation.Common.Glyphs`) ;
  les icônes purement décoratives sont choisies dans la vue.

## Actions communes (boutons réutilisables)

| Clé | Texte (fr) |
|---|---|
| `Common_Action_Apply` | Appliquer |
| `Common_Action_Cancel` | Annuler |
| `Common_Action_Restore` | Restaurer |
| `Common_Action_Close` | Fermer |
| `Common_Action_Refresh` | Actualiser |
| `Common_Action_Retry` | Réessayer |
| `Common_Action_Back` | Retour |
| `Common_Action_Continue` | Continuer |
| `Common_Action_Open` | Ouvrir |
| `Common_Action_OpenFolder` | Ouvrir le dossier |
| `Common_Action_ShowDetails` | Afficher les détails |
| `Common_Action_HideDetails` | Masquer les détails |
| `Common_Action_SelectAll` | Tout sélectionner |
| `Common_Action_SelectNone` | Tout désélectionner |
| `Common_Action_SelectRecommended` | Sélection recommandée |
| `Common_Action_Dismiss` | Masquer |
| `Common_Action_Keep` | Conserver |
| `Common_Action_Ignore` | Ignorer |
| `Common_Action_Activate` | Activer |
| `Common_Action_Deactivate` | Désactiver |
| `Common_Action_Stop` | Arrêter |
| `Common_Action_Save` | Enregistrer |
| `Common_Action_Done` | Terminé |
| `Common_Action_Search` | Rechercher |
| `Common_Action_CloseMessage` | Fermer le message |

## Libellés communs

| Clé | Texte (fr) |
|---|---|
| `Common_Label_Loading` | Chargement… |
| `Common_Label_Working` | Traitement en cours… |
| `Common_Label_ErrorTitle` | Un problème est survenu |
| `Common_Label_SuccessTitle` | Terminé |
| `Common_Label_InfoTitle` | Information |
| `Common_Label_SearchPlaceholder` | Rechercher |
| `Common_Label_AdminRequired` | Autorisation administrateur requise |
| `Common_Label_RestartRequired` | Redémarrage nécessaire |
| `Common_Label_Reversible` | Réversible |
| `Common_Label_Irreversible` | Irréversible |

## Navigation

| Clé | Texte (fr) |
|---|---|
| `Nav_AccessibleName` | Navigation principale |

## Fenêtre principale (Shell)

| Clé | Texte (fr) |
|---|---|
| `Shell_Protection_ToolTip` | Chaque modification réversible peut être annulée depuis l'Historique. |
| `Shell_Support` | Support |
| `Shell_Journal` | Journal |
| `Shell_Privacy` | Confidentialité |
| `Shell_About` | À propos |
| `Shell_Back_ToolTip` | Retour |
| `Shell_Recovery_Title` | Optimisation interrompue |
| `Shell_Recovery_Restore` | Restaurer |
| `Shell_Recovery_Keep` | Conserver |
| `Shell_GameBanner_Activate` | Activer le mode Gaming |
| `Shell_GameBanner_Ignore` | Ignorer |
| `Shell_Gaming_ToolTip` | Ouvrir le mode Gaming |
| `Shell_ActiveProfile_ToolTip` | Profil d'utilisation actif |

## Accueil — premier lancement (WelcomePage)

| Clé | Texte (fr) |
|---|---|
| `Welcome_Title` | Bienvenue |
| `Welcome_Subtitle` | PCBoost va analyser votre PC pour repérer ce qui peut le ralentir. Rien n'est modifié pendant l'analyse. |
| `Welcome_Intro_Local` | L'analyse est locale : aucune donnée ne quitte votre ordinateur. |
| `Welcome_Intro_Preview` | Aucune modification n'est faite sans que vous l'ayez vue et acceptée. |
| `Welcome_Intro_Undo` | Chaque modification réversible peut être annulée en un clic. |
| `Welcome_Action_Start` | Analyser mon PC |
| `Welcome_Action_Skip` | Passer |
| `Welcome_Action_Finish` | Aller au tableau de bord |
| `Welcome_Action_Optimize` | Optimiser mon PC |
| `Welcome_Analyzing_Title` | Analyse en cours… |
| `Welcome_Analyzing_Subtitle` | Vous pouvez continuer à utiliser votre PC pendant l'analyse. |
| `Welcome_Steps_AccessibleName` | Étapes de l'analyse |

## Tableau de bord (HomePage)

| Clé | Texte (fr) |
|---|---|
| `Home_Title` | Accueil |
| `Home_Score_Header` | Score de performance |
| `Home_Score_Max` | /100 |
| `Home_Score_HowComputed` | Comment ce score est-il calculé ? |
| `Home_Score_Explanation` | Chaque point provient d'un facteur mesuré sur votre PC. Sélectionnez un facteur pour agir. |
| `Home_Factors_Header` | Détail du score |
| `Home_Live_Header` | En ce moment |
| `Home_Temperatures_Header` | Températures |
| `Home_Temperatures_None` | Les températures ne sont pas disponibles sur ce PC. |
| `Home_System_Header` | Votre système |
| `Home_StartupApps_Label` | Applications au démarrage |
| `Home_SystemDrive_Label` | Disque système |
| `Home_BackgroundProcesses_Label` | Processus en arrière-plan |
| `Home_Windows_Label` | Windows |
| `Home_Windows_Unsupported` | Cette version de Windows n'est pas prise en charge (Windows 10 version 1809 minimum). |
| `Home_Recommendations_Header` | Recommandations principales |
| `Home_Recommendations_Empty` | Aucune recommandation pour le moment. |
| `Home_Recommendations_SeeAll` | Voir toutes les recommandations |
| `Home_QuickActions_Header` | Actions rapides |
| `Home_Action_Optimize` | Optimiser |
| `Home_Action_Cleanup` | Nettoyer |
| `Home_Action_Gaming` | Mode Gaming |
| `Home_Action_Analyze` | Analyser |
| `Home_Action_Performance` | Performances |
| `Home_WhySlow` | Pourquoi mon PC est lent ? |
| `Home_WhySlow_Description` | Voir les facteurs de ralentissement actuellement observés. |
| `Home_Empty_Title` | Aucune analyse pour le moment |
| `Home_Empty_Message` | Lancez une analyse pour obtenir le score de votre PC et des recommandations. |
| `Home_Analyzing` | Analyse en cours… |

## Analyse (AnalysisPage)

| Clé | Texte (fr) |
|---|---|
| `Analysis_Title` | Analyse du système |
| `Analysis_Subtitle` | Un bilan complet de votre PC, en lecture seule : rien n'est modifié. |
| `Analysis_Action_Start` | Lancer l'analyse |
| `Analysis_Action_Restart` | Relancer l'analyse |
| `Analysis_Action_Storage` | Voir le détail du stockage |
| `Analysis_Action_Diagnosis` | Pourquoi mon PC est lent ? |
| `Analysis_Action_Optimize` | Optimiser mon PC |
| `Analysis_Action_ManageDismissed` | Gérer dans les Paramètres |
| `Analysis_NotStarted_Title` | Aucune analyse |
| `Analysis_NotStarted_Message` | Lancez l'analyse pour obtenir un bilan complet de votre PC. |
| `Analysis_Analyzing_Title` | Analyse en cours… |
| `Analysis_Section_Hardware` | Matériel |
| `Analysis_Section_Gpu` | Carte graphique |
| `Analysis_Section_Storage` | Stockage |
| `Analysis_Section_Temperatures` | Températures |
| `Analysis_Section_Software` | Logiciels |
| `Analysis_Section_TopMemory` | Plus gros consommateurs de mémoire |
| `Analysis_Section_TopCpu` | Plus gros consommateurs de processeur |
| `Analysis_Section_Findings` | Constats |
| `Analysis_Section_Recommendations` | Recommandations |
| `Analysis_Section_Profile` | Profil matériel |
| `Analysis_Section_Advice` | Conseils matériels |
| `Analysis_Advice_Empty` | Aucun conseil matériel particulier. |
| `Analysis_Advice_Disclaimer` | Ces conseils sont factuels et basés sur les mesures : PCBoost ne vend aucun matériel. |
| `Analysis_Recommendations_Empty` | Aucune recommandation : aucun point d'amélioration n'a été identifié. |
| `Analysis_Recommendation_Why` | Pourquoi |
| `Analysis_Column_Name` | Nom |
| `Analysis_Column_Usage` | Utilisation |

## « Pourquoi mon PC est lent ? » (DiagnosisPage)

| Clé | Texte (fr) |
|---|---|
| `Diagnosis_Title` | Pourquoi mon PC est lent ? |
| `Diagnosis_Intro` | Les facteurs actuellement observés sont : |
| `Diagnosis_Why` | Pourquoi ? |
| `Diagnosis_WhatToDo` | Que peut-on faire ? |
| `Diagnosis_Impact` | Impact estimé |
| `Diagnosis_Evidence` | Constat |
| `Diagnosis_NoFactor_Title` | Aucun facteur important observé |
| `Diagnosis_NoFactor_Message` | Aucun facteur de ralentissement important n'a été observé pour le moment. Si votre PC vous semble lent à un moment précis, relancez ce diagnostic à ce moment-là. |
| `Diagnosis_Action_Refresh` | Relancer le diagnostic |
| `Diagnosis_Diagnosing` | Diagnostic en cours… |

## Optimisation (OptimizationPage)

| Clé | Texte (fr) |
|---|---|
| `Optimization_Title` | Optimisation |
| `Optimization_Section_OneClick` | Optimiser mon PC |
| `Optimization_Section_Profiles` | Profils |
| `Optimization_Section_OldPc` | Ancien PC |
| `Optimization_Idle_Title` | Optimiser mon PC |
| `Optimization_Idle_Message` | PCBoost analyse votre PC puis vous montre les modifications prévues avant de les appliquer. Les modifications réversibles pourront être annulées à tout moment. |
| `Optimization_Action_Scan` | Optimiser mon PC |
| `Optimization_Preview_Title` | Voici les modifications prévues |
| `Optimization_Preview_Subtitle` | Décochez ce que vous ne souhaitez pas modifier. Rien n'est appliqué avant votre confirmation. |
| `Optimization_Preview_Empty` | Aucune modification n'est nécessaire pour le moment. |
| `Optimization_Preview_NotApplicableHeader` | Non applicable sur ce PC |
| `Optimization_Preview_ChangesHeader` | Modifications |
| `Optimization_Preview_Target` | Cible |
| `Optimization_Action_Apply` | Appliquer les modifications |
| `Optimization_Action_CancelPreview` | Annuler |
| `Optimization_Applying_Title` | Optimisation en cours… |
| `Optimization_Applying_Message` | Chaque modification est enregistrée avant d'être appliquée, pour pouvoir être annulée. |
| `Optimization_Steps_AccessibleName` | Étapes de l'optimisation |
| `Optimization_Report_Title` | Rapport |
| `Optimization_Report_ResultsHeader` | Détail par optimisation |
| `Optimization_Report_WarningsHeader` | Avertissements |
| `Optimization_Action_Restore` | Restaurer les paramètres précédents |
| `Optimization_Action_BackToStart` | Terminé |
| `Optimization_Action_History` | Voir l'historique |

## Profils (section de l'Optimisation)

| Clé | Texte (fr) |
|---|---|
| `Profiles_Title` | Profils |
| `Profiles_Subtitle` | Un profil regroupe des réglages adaptés à un usage. Le profil précédent est restauré avant d'en activer un autre. |
| `Profiles_Action_Preview` | Voir les modifications |
| `Profiles_Action_Activate` | Activer le profil |
| `Profiles_Action_Deactivate` | Revenir à l'état d'avant le profil |
| `Profiles_Action_EditCustom` | Personnaliser |
| `Profiles_Action_SaveCustom` | Enregistrer le profil personnalisé |
| `Profiles_Action_BackToList` | Retour aux profils |
| `Profiles_Custom_Header` | Profil personnalisé |
| `Profiles_Custom_Description` | Choisissez les optimisations à inclure. Elles seront prévisualisées avant chaque activation. |
| `Profiles_Previewing` | Préparation de l'aperçu… |
| `Profiles_Applying` | Application du profil… |

## Ancien PC (section de l'Optimisation)

| Clé | Texte (fr) |
|---|---|
| `OldPc_Title` | Optimiser un ancien PC |
| `OldPc_Subtitle` | Des réglages adaptés aux configurations modestes, par niveaux. Chaque niveau est prévisualisé avant d'être appliqué. |
| `OldPc_Action_Assess` | Évaluer mon PC |
| `OldPc_Assessing` | Évaluation en cours… |
| `OldPc_Section_Assessment` | Évaluation |
| `OldPc_Section_Findings` | Constats |
| `OldPc_Section_Levels` | Niveaux d'optimisation |
| `OldPc_Action_ChooseLevel` | Voir les modifications |
| `OldPc_Action_Apply` | Appliquer ce niveau |
| `OldPc_Action_BackToLevels` | Retour aux niveaux |

## Nettoyage (CleanupPage)

| Clé | Texte (fr) |
|---|---|
| `Cleanup_Page_Title` | Nettoyage |
| `Cleanup_Page_Subtitle` | Libérez de l'espace en supprimant des fichiers temporaires. Vos documents, images, vidéos et téléchargements ne sont jamais concernés. |
| `Cleanup_Page_Action_Scan` | Analyser |
| `Cleanup_Page_Action_Clean` | Nettoyer |
| `Cleanup_Page_Action_SelectSafe` | Sélection recommandée |
| `Cleanup_Page_Action_ScanAgain` | Analyser à nouveau |
| `Cleanup_Page_Action_Storage` | Voir le stockage |
| `Cleanup_Page_Scanning` | Analyse en cours… |
| `Cleanup_Page_Cleaning` | Nettoyage en cours… |
| `Cleanup_Page_IrreversibleNotice` | Le nettoyage supprime définitivement les fichiers : il ne peut pas être annulé. |
| `Cleanup_Page_SafetyLegend_Header` | Niveaux de sûreté |
| `Cleanup_Page_Selected_Label` | Sélection |
| `Cleanup_Page_Result_Header` | Résultat du nettoyage |

## Démarrage (StartupPage)

| Clé | Texte (fr) |
|---|---|
| `Startup_Title` | Démarrage |
| `Startup_Subtitle` | Choisissez les programmes lancés à l'ouverture de session. La désactivation est réversible et visible dans le Gestionnaire des tâches. |
| `Startup_Search_Placeholder` | Rechercher un programme |
| `Startup_Filter_Label` | Afficher |
| `Startup_Column_Name` | Programme |
| `Startup_Column_Publisher` | Éditeur |
| `Startup_Column_Status` | État |
| `Startup_Detail_Location` | Emplacement |
| `Startup_Detail_Signature` | Signature |
| `Startup_Detail_Command` | Commande |
| `Startup_Detail_Recommendation` | Recommandation |
| `Startup_Detail_Evidence` | Mesure |
| `Startup_Toggle_AccessibleName` | Lancer au démarrage |
| `Startup_Action_History` | Restaurer depuis l'historique |

## Processus (ProcessesPage)

| Clé | Texte (fr) |
|---|---|
| `Processes_Title` | Processus |
| `Processes_Subtitle` | Les programmes en cours d'exécution. Les processus essentiels de Windows sont protégés. |
| `Processes_Search_Placeholder` | Rechercher un processus |
| `Processes_Column_Name` | Nom |
| `Processes_Column_Pid` | PID |
| `Processes_Column_Cpu` | Processeur |
| `Processes_Column_Memory` | Mémoire |
| `Processes_Column_Disk` | Disque |
| `Processes_Column_Publisher` | Éditeur |
| `Processes_Column_Trust` | Signature |
| `Processes_Sort_ToolTip` | Trier par cette colonne |
| `Processes_Detail_Header` | Détails |
| `Processes_Detail_Path` | Emplacement |
| `Processes_Detail_Trust` | Signature |
| `Processes_Detail_Protection` | Protection |
| `Processes_Detail_StartTime` | Démarré le |
| `Processes_Detail_Description` | Description |
| `Processes_Hint_Select` | Sélectionnez un processus pour afficher ses détails et les actions possibles. |
| `Processes_Badge_Critical` / `Processes_Badge_Sensitive` | Protégé / Système |
| `Processes_Action_Close` | Fermer l'application |
| `Processes_Action_Close_ToolTip` | Demande à l'application de se fermer normalement (elle peut proposer d'enregistrer). |
| `Processes_Action_Terminate` | Terminer le processus |
| `Processes_Action_Terminate_ToolTip` | Arrête le processus immédiatement : les données non enregistrées sont perdues. |
| `Processes_Action_OpenLocation` | Ouvrir l'emplacement |
| `Processes_Action_Properties` | Propriétés |
| `Processes_Action_SearchOnline` | Rechercher en ligne |
| `Processes_Action_SearchOnline_ToolTip` | Ouvre une recherche web sur le nom du processus dans votre navigateur. |
| `Processes_AutoRefresh` | Liste actualisée toutes les 2 secondes. |
| `Processes_Paused` | Actualisation suspendue pendant l'action en cours. |

## Gaming (GamingPage)

| Clé | Texte (fr) |
|---|---|
| `Gaming_Title` | Mode Gaming |
| `Gaming_Subtitle` | Des réglages temporaires pendant vos parties, restaurés automatiquement ensuite. |
| `Gaming_Status_Label` | Statut |
| `Gaming_Action_Activate` | Activer le mode Gaming |
| `Gaming_Action_Deactivate` | Désactiver et restaurer |
| `Gaming_Action_Detect` | Rechercher un jeu |
| `Gaming_Action_RefreshGames` | Actualiser la liste |
| `Gaming_Action_Benchmark` | Mesurer avant / après |
| `Gaming_Metrics_Header` | Mesures en direct |
| `Gaming_Planned_Header` | Optimisations prévues |
| `Gaming_Planned_Description` | Aperçu : rien n'est modifié avant l'activation. |
| `Gaming_Active_Header` | Optimisations actives |
| `Gaming_Checks_Header` | Paramètres graphiques de Windows |
| `Gaming_Games_Header` | Jeux installés détectés |
| `Gaming_AutoActivation_Header` | Activation automatique |
| `Gaming_AutoActivation_Description` | Que faire lorsqu'un jeu est détecté ? |

## Mesure avant / après (BenchmarkPage)

| Clé | Texte (fr) |
|---|---|
| `Benchmark_Title` | Mesure avant / après |
| `Benchmark_Subtitle` | Mesurez les performances avant et après un changement. Seuls les écarts réellement mesurés sont affichés. |
| `Benchmark_Duration_Label` | Durée de la mesure |
| `Benchmark_Label_Label` | Libellé (facultatif) |
| `Benchmark_Label_Placeholder` | Ex. : même scène, mêmes réglages |
| `Benchmark_TrackGame_Label` | Suivre le jeu détecté |
| `Benchmark_Action_Before` | Mesurer AVANT |
| `Benchmark_Action_After` | Mesurer APRÈS |
| `Benchmark_Action_Cancel` | Arrêter la mesure |
| `Benchmark_Action_Reset` | Nouvelle comparaison |
| `Benchmark_Action_DetectGame` | Rechercher un jeu |
| `Benchmark_Tip` | Pour une comparaison fiable, gardez la même durée et la même activité (même jeu, même scène). |
| `Benchmark_Before_Header` | Avant |
| `Benchmark_After_Header` | Après |
| `Benchmark_Comparison_Header` | Comparaison |
| `Benchmark_Column_Metric` | Mesure |
| `Benchmark_Column_Before` | Avant |
| `Benchmark_Column_After` | Après |
| `Benchmark_Column_Difference` | Écart |
| `Benchmark_Column_Verdict` | Résultat |
| `Benchmark_History_Header` | Historique des mesures |
| `Benchmark_History_Empty` | Aucune mesure enregistrée. |
| `Benchmark_Run_CpuLabel` | Processeur |
| `Benchmark_Run_GpuLabel` | Carte graphique |
| `Benchmark_Run_RamLabel` | Mémoire |
| `Benchmark_Run_DiskLabel` | Disque |
| `Benchmark_Run_FpsLabel` | FPS moyens |
| `Benchmark_Run_OnePercentLowLabel` | 1 % low |

## Performances (PerformancePage)

| Clé | Texte (fr) |
|---|---|
| `Performance_Title` | Performances |
| `Performance_Subtitle` | Charge de votre PC en temps réel et historique. |
| `Performance_Period_Label` | Période |
| `Performance_Current` | Actuel |
| `Performance_Average` | Moyenne |
| `Performance_Maximum` | Maximum |
| `Performance_AxisEnd` | Maintenant |
| `Performance_Paused` | La surveillance est en pause : les graphiques ne sont plus mis à jour. |
| `Performance_NoHistory` | Aucun historique enregistré pour cette période. |
| `Performance_Section_Usage` | Utilisation |
| `Performance_Section_Network` | Réseau |
| `Performance_Section_Temperatures` | Températures |

## Historique (HistoryPage)

| Clé | Texte (fr) |
|---|---|
| `History_Title` | Historique |
| `History_Subtitle` | Toutes les modifications faites par PCBoost, avec la possibilité de les annuler. |
| `History_Tab_Sessions` | Sessions |
| `History_Tab_Journal` | Journal des modifications |
| `History_Empty` | Aucune modification n'a encore été effectuée. |
| `History_Session_Details` | Détails |
| `History_Change_TargetLabel` | Cible |
| `History_Change_StatusLabel` | État |

## Journal des modifications (onglet de l'Historique)

| Clé | Texte (fr) |
|---|---|
| `Journal_Title` | Journal des modifications |
| `Journal_Subtitle` | Ce que PCBoost a fait, du plus récent au plus ancien. |
| `Journal_Empty` | Le journal est vide. |

## Paramètres (SettingsPage)

| Clé | Texte (fr) |
|---|---|
| `Settings_Title` | Paramètres |
| `Settings_Section_General` | Général |
| `Settings_Language_Label` | Langue |
| `Settings_Language_Description` | Le changement s'applique immédiatement. |
| `Settings_LaunchAtStartup_Label` | Lancer PCBoost avec Windows |
| `Settings_Theme_Label` | Thème |
| `Settings_Notifications_Label` | Notifications |
| `Settings_Notifications_Description` | Afficher des notifications Windows (analyse terminée, jeu détecté…). |
| `Settings_Monitoring_Label` | Surveillance en arrière-plan |
| `Settings_Monitoring_Description` | Mesure légère de la charge pour l'historique et les alertes. |
| `Settings_AutoOptimizations_Label` | Optimisations automatiques sûres |
| `Settings_AutoOptimizations_Description` | Autoriser PCBoost à appliquer seul des optimisations à faible risque et réversibles. Elles restent visibles dans l'Historique. |
| `Settings_MinimizeToTray_Label` | Réduire dans la zone de notification |
| `Settings_MinimizeToTray_Description` | Fermer la fenêtre laisse PCBoost actif dans la zone de notification. |
| `Settings_ExpertMode_Label` | Mode Expert |
| `Settings_ExpertMode_Description` | Afficher les détails techniques (cibles, états avant/après, journaux). |
| `Settings_Section_Gaming` | Gaming |
| `Settings_AutoGaming_Label` | Activation automatique du mode Gaming |
| `Settings_AutoRestore_Label` | Restaurer automatiquement à la fermeture du jeu |
| `Settings_MonitorSession_Label` | Surveiller pendant la session de jeu |
| `Settings_MeasureFps_Label` | Mesurer les FPS |
| `Settings_MeasureFps_Description` | Nécessite une autorisation administrateur ponctuelle au début de la mesure. Aucune injection dans le jeu. |
| `Settings_PowerPlan_Label` | Changer de mode d'alimentation pendant le jeu |
| `Settings_Priority_Label` | Augmenter la priorité du jeu |
| `Settings_BackgroundApps_Label` | Réduire l'activité des applications en arrière-plan |
| `Settings_PreferredGame_Label` | Jeu prioritaire |
| `Settings_Section_Security` | Sécurité |
| `Settings_ConfirmSensitive_Label` | Demander confirmation avant les opérations sensibles |
| `Settings_ConfirmSensitive_Description` | Les actions irréversibles ou à risque élevé sont toujours confirmées. |
| `Settings_VerboseLogging_Label` | Journalisation détaillée |
| `Settings_VerboseLogging_Description` | Utile pour le support ; aucune donnée personnelle n'est enregistrée. |
| `Settings_Retention_Label` | Conserver l'historique |
| `Settings_Recommendations_Label` | Recommandations masquées |
| `Settings_Action_RestoreRecommendations` | Réafficher |
| `Settings_Section_Diagnostic` | Diagnostic |
| `Settings_Logs_Label` | Journaux de l'application |
| `Settings_Action_OpenLogs` | Ouvrir le dossier des journaux |
| `Settings_Section_Updates` | Mises à jour |
| `Settings_Action_CheckUpdates` | Rechercher des mises à jour |
| `Settings_Action_DownloadUpdate` | Télécharger |
| `Settings_Action_InstallUpdate` | Installer |
| `Settings_Update_Integrity` | L'intégrité du fichier est vérifiée (SHA-256) avant l'installation. |
| `Settings_Update_ReleaseNotes` | Nouveautés |
| `Settings_Section_About` | À propos |
| `Settings_Action_Privacy` | Confidentialité |
| `Settings_Action_About` | À propos de PCBoost |

## Confidentialité (PrivacyPage)

| Clé | Texte (fr) |
|---|---|
| `Privacy_Title` | Confidentialité |
| `Privacy_Headline` | Vos données restent sur votre ordinateur. |
| `Privacy_Intro` | PCBoost fonctionne hors ligne. Il n'envoie aucune donnée, n'utilise aucun compte et n'intègre aucune télémétrie. |
| `Privacy_Stored_Header` | Ce qui est enregistré sur ce PC |
| `Privacy_Location_Label` | Emplacement des données |
| `Privacy_NotCollected_Header` | Ce que PCBoost ne collecte jamais |
| `Privacy_Telemetry_Label` | Télémétrie |
| `Privacy_Telemetry_Value` | Désactivée : PCBoost n'en contient aucune. |
| `Privacy_Action_OpenData` | Ouvrir le dossier des données |
| `Privacy_Action_OpenLogs` | Ouvrir le dossier des journaux |
| `Privacy_Deletion_Note` | Pour tout effacer, désinstallez PCBoost puis supprimez ce dossier. |

## À propos (AboutPage)

| Clé | Texte (fr) |
|---|---|
| `About_Title` | À propos |
| `About_Action_Website` | Site web |
| `About_Action_Support` | Support |
| `About_Honesty_Note` | PCBoost n'affiche que des valeurs mesurées ; une mesure indisponible est indiquée « Non disponible ». |

## Stockage (StoragePage)

| Clé | Texte (fr) |
|---|---|
| `Storage_Title` | Stockage |
| `Storage_Subtitle` | Répartition de l'espace du disque système. |
| `Storage_Reminder` | PCBoost ne supprime jamais vos fichiers personnels. |
| `Storage_Used_Label` | Utilisé |
| `Storage_Free_Label` | Libre |
| `Storage_Total_Label` | Total |
| `Storage_Categories_Header` | Catégories |
| `Storage_LargestFolders_Header` | Dossiers les plus volumineux |
| `Storage_Action_Open` | Ouvrir |
| `Storage_Action_Analyze` | Analyser à nouveau |
| `Storage_Action_Cleanup` | Nettoyer les fichiers temporaires |
| `Storage_Unavailable` | L'analyse du stockage n'est pas disponible. |
| `Storage_Analyzing` | Analyse du stockage en cours… |

## Mode Expert (ExpertPage)

| Clé | Texte (fr) |
|---|---|
| `Expert_Title` | Mode Expert |
| `Expert_Subtitle` | Détails techniques pour les utilisateurs avancés. |
| `Expert_Tab_Sessions` | Sessions et modifications |
| `Expert_Tab_Log` | Journal de l'application |
| `Expert_Tab_Errors` | Erreurs techniques |
| `Expert_Change_Kind` | Type |
| `Expert_Change_Optimization` | Optimisation |
| `Expert_Change_Target` | Cible |
| `Expert_Change_Status` | État |
| `Expert_Change_Before` | État avant |
| `Expert_Change_After` | État après |
| `Expert_Change_Error` | Erreur |
| `Expert_Change_RecordedAt` | Consignée le |
| `Expert_Change_RolledBackAt` | Annulée le |
| `Expert_Log_File` | Fichier |
| `Expert_Log_Folder` | Dossier des journaux |
| `Expert_Errors_Empty` | Aucune erreur technique consignée. |
| `Expert_Sessions_Empty` | Aucune session enregistrée. |
| `Expert_Action_OpenLogs` | Ouvrir le dossier des journaux |

## Clés dynamiques utiles aux vues

Les ViewModels exposent déjà ces textes résolus ; ils sont listés pour référence (enum → clé) :

- `Analysis_Arch_{valeur}`
- `Analysis_Bus_{valeur}`
- `Analysis_Category_{valeur}`
- `Analysis_Component_{valeur}`
- `Analysis_Media_{valeur}`
- `Analysis_StepRunning_{valeur}`
- `Analysis_Step_{valeur}`
- `Analysis_TierDescription_{valeur}`
- `Analysis_Tier_{valeur}`
- `Benchmark_Metric_{valeur}`
- `Benchmark_Phase_{valeur}`
- `Cleanup_Page_SafetyDescription_{valeur}`
- `Cleanup_Page_Safety_{valeur}`
- `Common_Label_Availability_{valeur}`
- `Common_Label_Confidence_{valeur}`
- `Common_Label_Impact_{valeur}`
- `Common_Label_Risk_{valeur}`
- `Common_Label_Severity_{valeur}`
- `Common_Label_Step_{valeur}`
- `Gaming_Capture_{valeur}`
- `Gaming_Source_{valeur}`
- `Gaming_State_{valeur}`
- `History_ChangeStatus_{valeur}`
- `History_Status_{valeur}`
- `History_Type_{valeur}`
- `Home_FactorStatus_{valeur}`
- `Home_ScoreLevel_{valeur}`
- `Journal_Kind_{valeur}`
- `OldPc_Level_{valeur}`
- `Optimization_StepRunning_{valeur}`
- `Optimization_Step_{valeur}`
- `Performance_AxisStart_{valeur}`
- `Processes_Protection_{valeur}`
- `Profiles_Category_{valeur}`
- `Startup_Impact_{valeur}`
- `Startup_Location_{valeur}`
- `Startup_Recommendation_{valeur}`
- `Storage_Category_{valeur}`
