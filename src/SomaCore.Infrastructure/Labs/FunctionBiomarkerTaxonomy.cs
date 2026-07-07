namespace SomaCore.Infrastructure.Labs;

/// <summary>
/// The known-biomarker checksum (session-function-health-integration.md
/// §1.3): every extracted canonical name must be in this set or the parse
/// fails for admin review. This is the second defense against hallucinated
/// biomarkers — the model can only "find" markers we know Function panels
/// carry. Grow the set when a real panel legitimately carries something
/// missing (that's a code change on purpose: additions get reviewed).
/// </summary>
public static class FunctionBiomarkerTaxonomy
{
    public static readonly IReadOnlySet<string> KnownNames = new HashSet<string>(StringComparer.Ordinal)
    {
        // Heart / lipids
        "total_cholesterol", "ldl_cholesterol", "hdl_cholesterol", "triglycerides",
        "apolipoprotein_b", "lipoprotein_a", "non_hdl_cholesterol", "ldl_particle_number",
        "hs_crp", "homocysteine",
        // Metabolic
        "glucose_fasting", "hba1c", "insulin_fasting", "uric_acid", "adiponectin",
        "leptin", "c_peptide", "homa_ir",
        // Thyroid
        "tsh", "free_t4", "free_t3", "reverse_t3", "tpo_antibodies", "thyroglobulin_antibodies",
        // Hormones
        "testosterone_total", "testosterone_free", "estradiol", "progesterone",
        "dhea_sulfate", "shbg", "cortisol_am", "fsh", "lh", "igf_1", "prolactin",
        // Nutrients
        "vitamin_d_25_hydroxy", "vitamin_b12", "folate", "ferritin", "iron_total",
        "tibc", "transferrin_saturation", "magnesium_rbc", "zinc", "copper",
        "selenium", "omega_3_index", "vitamin_a", "vitamin_e",
        // Liver
        "alt", "ast", "ggt", "alkaline_phosphatase", "bilirubin_total", "albumin",
        "total_protein",
        // Kidney
        "creatinine", "egfr", "bun", "cystatin_c", "urine_albumin_creatinine_ratio",
        // Blood
        "hemoglobin", "hematocrit", "wbc", "rbc", "platelets", "mcv", "mch", "mchc",
        "rdw", "neutrophils", "lymphocytes", "monocytes", "eosinophils", "basophils",
        // Electrolytes
        "sodium", "potassium", "chloride", "calcium", "co2_bicarbonate", "phosphorus",
        // Pancreas / other
        "lipase", "amylase", "ldh", "ck_creatine_kinase", "galectin_3", "nt_probnp",
        "psa_total", "tmao",
    };

    public static readonly IReadOnlySet<string> KnownCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "heart", "metabolic", "thyroid", "hormones", "nutrients",
        "liver", "kidney", "blood", "electrolytes", "pancreas", "immune", "other",
    };
}
