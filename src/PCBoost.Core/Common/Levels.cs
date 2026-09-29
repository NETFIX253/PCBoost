namespace PCBoost.Core.Common;

/// <summary>Niveau de risque d'une action (classification §59).</summary>
public enum RiskLevel { Low = 0, Medium = 1, High = 2 }

/// <summary>Impact estimé d'une action ou d'un problème.</summary>
public enum ImpactLevel { Low = 0, Medium = 1, High = 2 }

/// <summary>Confiance dans un diagnostic ou une recommandation.</summary>
public enum ConfidenceLevel { Low = 0, Medium = 1, High = 2 }

/// <summary>Catégorie de sûreté d'une opération de nettoyage (§11).</summary>
public enum SafetyCategory { Safe = 0, Caution = 1, Advanced = 2 }

/// <summary>Sévérité d'un constat de santé.</summary>
public enum Severity { Info = 0, Low = 1, Medium = 2, High = 3, Critical = 4 }

/// <summary>Disponibilité d'une mesure. Une valeur absente n'est jamais inventée.</summary>
public enum Availability { Available = 0, Unavailable = 1, RequiresElevation = 2, NotSupported = 3, NoSensor = 4 }
