using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Domain.Tests;

/// <summary>
/// A Regra 1 aplicada ao Website Auditor.
///
/// Cada caso aqui e uma lacuna que o JSON Schema NAO fecha. O schema sabe exigir
/// "evidence_index e inteiro >= 0"; ele nao sabe quantas evidencias existem, nem
/// que um numero de veiculos precisa de lastro. Sem o guard, essas duas passam - e
/// viram uma frase dita a um dono de concessionaria que sabe exatamente quantos
/// carros tem.
/// </summary>
public class EvidenceFirstGuardAuditTests
{
    [Fact]
    public void Auditoria_bem_formada_nao_tem_violacao()
    {
        Assert.Empty(EvidenceFirstGuard.Check(Valid()));
    }

    [Fact]
    public void Indice_fora_do_intervalo_e_rejeitado_em_cada_colecao()
    {
        var profile = Valid() with
        {
            Portals = [new PortalClaim { Name = "Webmotors", EvidenceIndex = 9 }],
            Integrations =
            [
                new IntegrationClaim
                {
                    System = "Syonet", Category = "dms", Confidence = 0.6m, EvidenceIndex = 7
                }
            ],
            Issues =
            [
                new AuditIssue
                {
                    Area = "ux", Severity = "high", Title = "Vitrine nao abre", EvidenceIndex = 5
                }
            ]
        };

        var violations = EvidenceFirstGuard.Check(profile);

        Assert.Contains(violations, v => v.Location == "/portals/0/evidence_index");
        Assert.Contains(violations, v => v.Location == "/integrations/0/evidence_index");
        Assert.Contains(violations, v => v.Location == "/issues/0/evidence_index");
    }

    /// <summary>
    /// O analogo do <c>store_count</c> do Research Profile, e pelo mesmo motivo:
    /// e um numero que vai direto para a mensagem comercial.
    /// </summary>
    [Fact]
    public void Contagem_de_estoque_exige_evidencia_do_tipo_correspondente()
    {
        var semLastro = Valid() with
        {
            Evidence =
            [
                Evidence("conversion_path", "Botao de WhatsApp em todas as paginas.")
            ],
            Inventory = new InventoryClaim
            {
                PublishedOnline = true,
                ApproximateCount = 380,
                EvidenceIndex = 0
            }
        };

        Assert.Contains(
            EvidenceFirstGuard.Check(semLastro),
            v => v.Location == "/inventory/approximate_count");
    }

    [Theory]
    [InlineData("inventory_count")]
    [InlineData("estoque_publicado")]
    public void Contagem_de_estoque_com_evidencia_de_estoque_passa(string claimType)
    {
        var profile = Valid() with
        {
            Evidence = [Evidence(claimType, "Pagina 1 de 32, 12 por pagina.")],
            Inventory = new InventoryClaim
            {
                PublishedOnline = true,
                ApproximateCount = 380,
                EvidenceIndex = 0
            },
            // Valid() traz Conversion apontando para a evidencia 1, que deixa de
            // existir ao reduzir a lista a um item. Manter aqui testaria o indice
            // fora do intervalo de novo, e nao o que este caso quer isolar.
            Conversion = null
        };

        Assert.Empty(EvidenceFirstGuard.Check(profile));
    }

    /// <summary>
    /// Confianca zero equivale a nao ter evidencia - a mesma regra que ja valia
    /// para o Researcher. Vale a pena fixar nos dois: a checagem e compartilhada,
    /// e um teste so nao mostraria se alguem a duplicasse e endurecesse um lado.
    /// </summary>
    [Fact]
    public void Evidencia_sem_url_ou_com_confianca_zero_e_rejeitada()
    {
        var profile = Valid() with
        {
            Evidence =
            [
                Evidence("inventory_count", "Sem fonte.") with
                {
                    Source = new SourceRef
                    {
                        Type = "website",
                        Url = "   ",
                        ObservedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z")
                    }
                },
                Evidence("conversion_path", "Confianca zerada.") with { Confidence = 0m }
            ],
            Inventory = null,
            Conversion = null
        };

        var violations = EvidenceFirstGuard.Check(profile);

        Assert.Contains(violations, v => v.Location == "/evidence/0/source/url");
        Assert.Contains(violations, v => v.Location == "/evidence/1/confidence");
    }

    [Fact]
    public void Blocos_opcionais_tambem_apontam_para_evidencia_real()
    {
        var profile = Valid() with
        {
            Conversion = new ConversionClaim { HasWhatsApp = true, EvidenceIndex = 42 }
        };

        Assert.Contains(
            EvidenceFirstGuard.Check(profile),
            v => v.Location == "/conversion/evidence_index");
    }

    private static EvidenceClaim Evidence(string claimType, string text) => new()
    {
        ClaimType = claimType,
        ClaimText = text,
        Confidence = 0.8m,
        Source = new SourceRef
        {
            Type = "website",
            Url = "https://exemplo.com.br/estoque",
            ObservedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z")
        }
    };

    private static WebsiteAuditProfile Valid() => new()
    {
        Summary = "Site institucional com vitrine propria e estoque tambem em portal.",
        AuditedUrl = "https://exemplo.com.br",
        AuditCompleteness = 0.8m,
        Evidence =
        [
            Evidence("inventory_count", "Pagina 1 de 32, 12 veiculos por pagina."),
            Evidence("conversion_path", "Botao de WhatsApp em todas as paginas.")
        ],
        Inventory = new InventoryClaim
        {
            PublishedOnline = true,
            ApproximateCount = 380,
            EvidenceIndex = 0
        },
        Conversion = new ConversionClaim { HasWhatsApp = true, EvidenceIndex = 1 }
    };
}
