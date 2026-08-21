using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Application;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Agents.Tests;

/// <summary>
/// A Regra 1 da secao 25 ("nenhuma afirmacao especifica sem evidencia") so vale
/// alguma coisa se for verificavel por maquina. Estes testes sao o contrato.
/// </summary>
public class EvidenceFirstGuardTests
{
    private static EvidenceClaim Claim(string type = "store_count", decimal confidence = 0.9m, string url = "https://exemplo.com.br/lojas") => new()
    {
        ClaimType = type,
        ClaimText = "Afirmacao com tamanho suficiente para o schema.",
        Confidence = confidence,
        Source = new SourceRef
        {
            Type = "website",
            Url = url,
            ObservedAt = DateTimeOffset.Parse("2026-08-19T10:00:00Z")
        }
    };

    private static ResearchProfile Profile(
        IReadOnlyList<EvidenceClaim>? evidence = null,
        IReadOnlyList<BrandClaim>? brands = null,
        IReadOnlyList<SignalClaim>? signals = null,
        int? storeCount = null) => new()
    {
        Summary = "Resumo suficientemente longo para o contrato do Research Profile.",
        Segment = "dealer_group",
        ResearchCompleteness = 0.8m,
        Evidence = evidence ?? [Claim()],
        Brands = brands ?? [],
        Signals = signals ?? [],
        StoreCount = storeCount
    };

    [Fact]
    public void Aceita_perfil_integralmente_lastreado()
    {
        var profile = Profile(
            evidence: [Claim()],
            brands: [new BrandClaim { Name = "GWM", Confidence = 0.9m, EvidenceIndex = 0 }],
            storeCount: 6);

        Assert.Empty(EvidenceFirstGuard.Check(profile));
    }

    [Fact]
    public void Rejeita_marca_apontando_para_evidencia_inexistente()
    {
        // O schema aceita qualquer inteiro >= 0; so o guard sabe quantas
        // evidencias existem de fato.
        var profile = Profile(
            evidence: [Claim()],
            brands: [new BrandClaim { Name = "Toyota", Confidence = 0.9m, EvidenceIndex = 7 }]);

        var violations = EvidenceFirstGuard.Check(profile);

        Assert.Contains(violations, v => v.Location.Contains("/brands/0/evidence_index"));
    }

    [Fact]
    public void Rejeita_indice_negativo()
    {
        var profile = Profile(
            brands: [new BrandClaim { Name = "Fiat", Confidence = 0.9m, EvidenceIndex = -1 }]);

        Assert.NotEmpty(EvidenceFirstGuard.Check(profile));
    }

    [Fact]
    public void Rejeita_sinal_sem_lastro()
    {
        var profile = Profile(
            signals:
            [
                new SignalClaim
                {
                    SignalType = "expansion",
                    Strength = 0.8m,
                    ObservedAt = DateTimeOffset.Parse("2026-08-18T09:30:00Z"),
                    EvidenceIndex = 3
                }
            ]);

        var violations = EvidenceFirstGuard.Check(profile);

        Assert.Contains(violations, v => v.Location.Contains("/signals/0/evidence_index"));
    }

    [Fact]
    public void Rejeita_evidencia_sem_url()
    {
        var profile = Profile(evidence: [Claim(url: "   ")]);

        Assert.Contains(EvidenceFirstGuard.Check(profile), v => v.Location.Contains("source/url"));
    }

    [Fact]
    public void Rejeita_confianca_zero()
    {
        var profile = Profile(evidence: [Claim(confidence: 0m)]);

        Assert.Contains(EvidenceFirstGuard.Check(profile), v => v.Location.Contains("confidence"));
    }

    [Fact]
    public void Rejeita_store_count_sem_evidencia_de_lojas()
    {
        // "Vi que voces tem 12 lojas" e exatamente o tipo de afirmacao que a
        // secao 10 proibe sem lastro - e o numero vai direto para a mensagem.
        var profile = Profile(evidence: [Claim(type: "company_overview")], storeCount: 12);

        Assert.Contains(EvidenceFirstGuard.Check(profile), v => v.Location == "/store_count");
    }

    [Fact]
    public void Aceita_store_count_com_evidencia_correspondente()
    {
        var profile = Profile(evidence: [Claim(type: "store_count")], storeCount: 6);

        Assert.DoesNotContain(EvidenceFirstGuard.Check(profile), v => v.Location == "/store_count");
    }

    [Fact]
    public void Reprova_o_fixture_sem_lastro()
    {
        var raw = File.ReadAllText(RepoPaths.Fixture("researcher", "missing-evidence"));
        var validator = StructuredOutputValidator.FromFile(
            RepoPaths.Schema("research-profile.schema.json"));

        var outcome = validator.Validate<ResearchProfile>(raw);

        // Passa no schema (formato correto)...
        Assert.True(outcome.IsValid, outcome.Describe());

        // ...mas nao no guard: a marca aponta para evidence[7], que nao existe,
        // e store_count=12 nao tem evidencia de lojas.
        var violations = EvidenceFirstGuard.Check(outcome.Value!);

        Assert.Contains(violations, v => v.Location.Contains("/brands/0/evidence_index"));
        Assert.Contains(violations, v => v.Location == "/store_count");
    }
}
