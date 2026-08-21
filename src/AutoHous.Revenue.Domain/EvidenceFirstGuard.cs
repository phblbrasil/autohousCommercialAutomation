using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Domain;

/// <summary>
/// Validacao semantica que o JSON Schema nao consegue expressar: cada marca,
/// loja e sinal precisa apontar para uma evidencia REAL da lista, e cada
/// evidencia precisa de uma fonte utilizavel.
///
/// Isto e a Regra 1 da secao 25 tornada mecanica. Sem esta checagem, um
/// evidence_index fora do intervalo passaria pelo schema (que so sabe validar
/// "inteiro >= 0") e a conta receberia uma afirmacao sem lastro - exatamente o
/// caso do "vi que voces estao expandindo" que a secao 10 proibe.
/// </summary>
public static class EvidenceFirstGuard
{
    public static IReadOnlyList<SchemaViolation> Check(ResearchProfile profile)
    {
        var violations = new List<SchemaViolation>();
        var evidenceCount = profile.Evidence.Count;

        for (var i = 0; i < evidenceCount; i++)
        {
            var evidence = profile.Evidence[i];

            if (string.IsNullOrWhiteSpace(evidence.Source.Url))
            {
                violations.Add(new SchemaViolation(
                    $"/evidence/{i}/source/url",
                    "Evidencia sem URL de fonte: afirmacao nao auditavel."));
            }

            if (evidence.Confidence <= 0)
            {
                violations.Add(new SchemaViolation(
                    $"/evidence/{i}/confidence",
                    "Confianca zero equivale a nao ter evidencia."));
            }
        }

        CheckIndex(profile.Brands.Select((b, i) => ($"/brands/{i}", b.EvidenceIndex)), evidenceCount, violations);
        CheckIndex(profile.Locations.Select((l, i) => ($"/locations/{i}", l.EvidenceIndex)), evidenceCount, violations);
        CheckIndex(profile.Signals.Select((s, i) => ($"/signals/{i}", s.EvidenceIndex)), evidenceCount, violations);

        // store_count declarado precisa ter evidencia do tipo correspondente:
        // e um numero que vai direto para a mensagem comercial.
        if (profile.StoreCount is > 0 &&
            !profile.Evidence.Any(e => e.ClaimType.Contains("store", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add(new SchemaViolation(
                "/store_count",
                "store_count informado sem nenhuma evidencia de claim_type relacionado a lojas."));
        }

        return violations;
    }

    private static void CheckIndex(
        IEnumerable<(string Path, int Index)> items,
        int evidenceCount,
        List<SchemaViolation> violations)
    {
        foreach (var (path, index) in items)
        {
            if (index < 0 || index >= evidenceCount)
            {
                violations.Add(new SchemaViolation(
                    $"{path}/evidence_index",
                    $"Aponta para evidencia inexistente ({index}); evidence[] tem {evidenceCount} item(ns)."));
            }
        }
    }
}
