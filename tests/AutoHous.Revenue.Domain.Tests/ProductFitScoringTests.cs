using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Domain.Tests;

/// <summary>
/// O fit de produto e deterministico (ADR-0005), e o que estes testes fixam nao
/// sao os numeros e sim as PROPRIEDADES que fazem o numero significar alguma
/// coisa.
///
/// Testar "MotorHub da 78 para esta conta" congelaria a tabela de pesos e
/// quebraria a cada ajuste legitimo. Testar "conta com seis lojas e tres portais
/// pontua MotorHub acima de conta com uma loja" sobrevive ao ajuste e falha
/// quando a regra inverte - que e o defeito que importa.
/// </summary>
public class ProductFitScoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static ProductFitInputs Base() => new() { ReferenceDate = Now };

    private static decimal ScoreOf(IReadOnlyList<ProductFit> fits, string product) =>
        fits.Single(f => f.Product == product).Score;

    // ----------------------------------------------------- ausencia de dado

    [Fact]
    public void Conta_sem_nenhum_fato_nao_recomenda_entrada()
    {
        var fits = ProductFitScoring.Calculate(Base());

        Assert.DoesNotContain(fits, f => f.RecommendedEntry);
    }

    /// <summary>
    /// A propriedade central do desenho: ausencia de auditoria NAO vira "site
    /// bom". Se virasse, uma conta nunca auditada pontuaria zero em FrontCar e
    /// desceria na fila justamente por falta de informacao - o oposto do que
    /// deveria acontecer.
    /// </summary>
    [Fact]
    public void Sem_auditoria_os_criterios_de_site_ficam_nao_observados()
    {
        var fits = ProductFitScoring.Calculate(Base());
        var frontCar = fits.Single(f => f.Product == ProductCatalog.FrontCar);

        Assert.All(
            frontCar.Reasons.Where(r => r.Criterion is "vitrine" or "desempenho" or "achabilidade" or "conversao"),
            r => Assert.False(r.Observed));

        Assert.True(frontCar.Coverage < 0.5m);
    }

    /// <summary>
    /// A porta de entrada satisfaz SEMPRE as duas condicoes - nota e cobertura.
    ///
    /// O teste varre formas de conta bem diferentes em vez de fixar um caso,
    /// porque a propriedade e universal e um exemplo so nao a demonstraria.
    /// </summary>
    [Fact]
    public void Porta_de_entrada_satisfaz_nota_e_cobertura()
    {
        ProductFitInputs[] formas =
        [
            Base(),
            Base() with { StoreCount = 12, CnpjCount = 6, BrandCount = 5, InventoryEstimate = 900 },
            Base() with { StoreCount = 1, Audit = new WebsiteAuditDetail { Inventory = 0.1m, Performance = 0.2m } },
            Base() with { StoreCount = 6, Audit = new WebsiteAuditDetail { Reachable = false } },
            Base() with
            {
                StoreCount = 4, CnpjCount = 2, BrandCount = 2, InventoryEstimate = 250,
                Audit = new WebsiteAuditDetail
                {
                    Inventory = 0.4m, Performance = 0.3m, Seo = 0.4m, Conversion = 0.6m,
                    MultiplePortals = true, PortalCount = 2, ComplexIntegration = true
                }
            }
        ];

        foreach (var forma in formas)
        {
            var entry = ProductFitScoring.Calculate(forma).FirstOrDefault(f => f.RecommendedEntry);

            if (entry is null) continue;

            Assert.True(entry.Score >= ProductFitScoring.EntryThreshold);
            Assert.True(entry.Coverage >= ProductFitScoring.EntryMinimumCoverage);
        }
    }

    /// <summary>
    /// O piso de cobertura NAO decide nada com os pesos de hoje, e este teste
    /// existe para que a afirmacao pare de ser opiniao.
    ///
    /// A unica forma de a cobertura cair abaixo do piso e faltar a auditoria - e
    /// sem ela nenhum dos tres produtos afetados alcanca o corte de nota. Se
    /// alguem rebalancear os pesos e um deles passar a chegar a 45 so com o que
    /// a pesquisa observa, este teste falha, e a falha e o aviso de que o piso
    /// saiu da reserva e virou guarda ativa - com todo o comportamento que isso
    /// muda na fila.
    /// </summary>
    [Fact]
    public void Piso_de_cobertura_e_reserva_e_nao_filtro_ativo()
    {
        // Operacao com todos os fatos de PESQUISA no maximo e nenhuma auditoria.
        // E o cenario que mais favorece uma nota alta com cobertura baixa.
        var fits = ProductFitScoring.Calculate(Base() with
        {
            StoreCount = 50,
            CnpjCount = 20,
            BrandCount = 12,
            InventoryEstimate = 9999,
            HasAuthorizedBrand = true,
            Signals = [new ScoredSignal("replatform", 1m, Now)]
        });

        var abaixoDoPiso = fits.Where(f => f.Coverage < ProductFitScoring.EntryMinimumCoverage).ToList();

        Assert.NotEmpty(abaixoDoPiso);

        Assert.All(abaixoDoPiso, f => Assert.True(
            f.Score < ProductFitScoring.EntryThreshold,
            $"{f.Product} alcancou {f.Score:0} com cobertura {f.Coverage:P0}: o piso de cobertura " +
            "deixou de ser reserva e agora e a unica guarda contra diagnostico incompleto. " +
            "Reveja o comentario de EntryMinimumCoverage antes de ajustar este teste."));
    }

    // -------------------------------------------------- discriminacao de dor

    [Fact]
    public void Fragmentacao_de_estoque_favorece_motorhub_sobre_frontcar()
    {
        // Vitrine boa, mas o mesmo estoque em tres canais e seis lojas: a dor e
        // de distribuicao, nao de site.
        var fits = ProductFitScoring.Calculate(Base() with
        {
            StoreCount = 6,
            CnpjCount = 3,
            BrandCount = 4,
            InventoryEstimate = 380,
            Audit = new WebsiteAuditDetail
            {
                Inventory = 0.9m,
                Performance = 0.8m,
                Seo = 0.8m,
                Conversion = 0.8m,
                MultiplePortals = true,
                PortalCount = 3,
                ComplexIntegration = false
            }
        });

        Assert.True(ScoreOf(fits, ProductCatalog.MotorHub) > ScoreOf(fits, ProductCatalog.FrontCar));
        Assert.Equal(ProductCatalog.MotorHub, fits.Single(f => f.RecommendedEntry).Product);
    }

    [Fact]
    public void Vitrine_ruim_em_loja_unica_favorece_frontcar_sobre_motorhub()
    {
        var fits = ProductFitScoring.Calculate(Base() with
        {
            StoreCount = 1,
            CnpjCount = 1,
            BrandCount = 1,
            InventoryEstimate = 60,
            Audit = new WebsiteAuditDetail
            {
                Inventory = 0.1m,
                Performance = 0.2m,
                Seo = 0.15m,
                Conversion = 0.2m,
                MultiplePortals = false,
                PortalCount = 0,
                ComplexIntegration = false
            }
        });

        Assert.True(ScoreOf(fits, ProductCatalog.FrontCar) > ScoreOf(fits, ProductCatalog.MotorHub));
        Assert.Equal(ProductCatalog.FrontCar, fits.Single(f => f.RecommendedEntry).Product);
    }

    /// <summary>
    /// O achado que define o AutoFollow: o site CAPTURA lead e nao ha CRM. Com
    /// CRM detectado, o mesmo site vale muito menos - a dor deixa de ser
    /// "o lead some" e passa a ser outra coisa.
    /// </summary>
    [Fact]
    public void Captura_de_lead_sem_crm_vale_mais_que_com_crm()
    {
        var operation = Base() with
        {
            StoreCount = 4,
            InventoryEstimate = 200,
            Audit = new WebsiteAuditDetail { Conversion = 0.7m, Inventory = 0.6m }
        };

        var semCrm = ProductFitScoring.Calculate(operation);

        var comCrm = ProductFitScoring.Calculate(operation with
        {
            Technologies = [new AccountTechnology(TechnologyCategory.Crm, "RD Station", TechnologySource.Probe, 1m)]
        });

        Assert.True(
            ScoreOf(semCrm, ProductCatalog.AutoFollow) > ScoreOf(comCrm, ProductCatalog.AutoFollow));
    }

    [Fact]
    public void Chat_detectado_reduz_o_fit_de_autotalk()
    {
        var operation = Base() with
        {
            StoreCount = 5,
            InventoryEstimate = 250,
            Audit = new WebsiteAuditDetail { Conversion = 0.3m, Inventory = 0.7m }
        };

        var semChat = ProductFitScoring.Calculate(operation);

        var comChat = ProductFitScoring.Calculate(operation with
        {
            Technologies = [new AccountTechnology(TechnologyCategory.Chat, "JivoChat", TechnologySource.Probe, 1m)]
        });

        Assert.True(ScoreOf(semChat, ProductCatalog.AutoTalk) > ScoreOf(comChat, ProductCatalog.AutoTalk));
    }

    /// <summary>
    /// Site fora do ar e o caso MAIS forte para FrontCar, e nao a ausencia de
    /// dado. Tratar <c>Reachable=false</c> como "nao observado" mandaria a conta
    /// com o pior site do funil para o fim da fila.
    /// </summary>
    [Fact]
    public void Site_fora_do_ar_e_argumento_forte_para_frontcar()
    {
        var fits = ProductFitScoring.Calculate(Base() with
        {
            StoreCount = 2,
            InventoryEstimate = 90,
            Audit = new WebsiteAuditDetail { Reachable = false }
        });

        var frontCar = fits.Single(f => f.Product == ProductCatalog.FrontCar);
        var criterio = frontCar.Reasons.Single(r => r.Criterion == "site_fora_do_ar");

        Assert.True(criterio.Observed);
        Assert.Equal(criterio.MaxPoints, criterio.Points);
    }

    // ---------------------------------------------------------- reprodutibilidade

    /// <summary>
    /// Duas execucoes sobre os mesmos fatos dao o mesmo resultado, INCLUSIVE a
    /// porta de entrada. E o que o ADR-0005 exige e o que um desempate instavel
    /// quebraria em silencio: dois produtos empatados trocando de lugar entre
    /// execucoes fariam a conta receber uma abordagem diferente a cada
    /// recalculo, sem que nada tivesse mudado.
    /// </summary>
    [Fact]
    public void Mesmos_fatos_produzem_a_mesma_porta_de_entrada()
    {
        var inputs = Base() with
        {
            StoreCount = 5,
            CnpjCount = 3,
            BrandCount = 3,
            InventoryEstimate = 300,
            Audit = new WebsiteAuditDetail
            {
                Inventory = 0.5m, Performance = 0.5m, Seo = 0.5m, Conversion = 0.5m,
                MultiplePortals = true, PortalCount = 2, ComplexIntegration = true
            }
        };

        var primeira = ProductFitScoring.Calculate(inputs);
        var segunda = ProductFitScoring.Calculate(inputs);

        Assert.Equal(
            primeira.Single(f => f.RecommendedEntry).Product,
            segunda.Single(f => f.RecommendedEntry).Product);

        Assert.Equal(
            primeira.Select(f => (f.Product, f.Score)),
            segunda.Select(f => (f.Product, f.Score)));
    }

    [Fact]
    public void No_maximo_um_produto_e_porta_de_entrada()
    {
        var fits = ProductFitScoring.Calculate(Base() with
        {
            StoreCount = 15,
            CnpjCount = 8,
            BrandCount = 6,
            InventoryEstimate = 1200,
            HasAuthorizedBrand = true,
            Audit = new WebsiteAuditDetail
            {
                Reachable = true,
                Inventory = 0.05m, Performance = 0.05m, Seo = 0.05m,
                Conversion = 0.05m, Ux = 0.05m, Mobile = 0.05m, Tracking = 0.05m,
                MultiplePortals = true, PortalCount = 4, ComplexIntegration = true
            }
        });

        // Operacao com dor em tudo: varios produtos pontuam alto ao mesmo tempo,
        // e isso e o retrato correto. A PORTA e que e uma so.
        Assert.True(fits.Count(f => f.Score >= ProductFitScoring.EntryThreshold) > 1);
        Assert.Single(fits, f => f.RecommendedEntry);
    }

    [Fact]
    public void Sinal_antigo_pesa_menos_que_sinal_recente()
    {
        var recente = ProductFitScoring.Calculate(Base() with
        {
            Audit = new WebsiteAuditDetail { Inventory = 0.5m },
            Signals = [new ScoredSignal("replatform", 1m, Now.AddDays(-10))]
        });

        var antigo = ProductFitScoring.Calculate(Base() with
        {
            Audit = new WebsiteAuditDetail { Inventory = 0.5m },
            Signals = [new ScoredSignal("replatform", 1m, Now.AddDays(-300))]
        });

        Assert.True(ScoreOf(recente, ProductCatalog.FrontCar) > ScoreOf(antigo, ProductCatalog.FrontCar));
    }

    [Fact]
    public void Toda_nota_cabe_no_intervalo_declarado()
    {
        var fits = ProductFitScoring.Calculate(Base() with
        {
            StoreCount = 50,
            CnpjCount = 20,
            BrandCount = 12,
            InventoryEstimate = 9999,
            HasAuthorizedBrand = true,
            Audit = new WebsiteAuditDetail
            {
                Inventory = 0m, Performance = 0m, Seo = 0m, Conversion = 0m,
                MultiplePortals = true, PortalCount = 9, ComplexIntegration = true
            },
            Signals = [new ScoredSignal("replatform", 1m, Now)]
        });

        Assert.All(fits, f =>
        {
            Assert.InRange(f.Score, 0m, 100m);
            Assert.InRange(f.Coverage, 0m, 1m);
            Assert.All(f.Reasons, r => Assert.InRange(r.Points, 0m, r.MaxPoints));
        });
    }

    /// <summary>
    /// Os cinco produtos disputam a porta de entrada na MESMA escala.
    ///
    /// <c>RecommendedEntry</c> sai de um <c>OrderByDescending(Score)</c> entre os
    /// cinco, entao um produto cujos criterios somem menos de 100 chega a essa
    /// comparacao com desconto - estrutural, silencioso e sem nada no
    /// diagnostico que o explique. Foi o que aconteceu: o AutoFollow somava 95
    /// contra 100 dos outros quatro, e perdia empates que nao eram empates.
    ///
    /// O teste olha <c>MaxPoints</c> e nao a nota, porque o defeito nao aparece
    /// em nenhum cenario isolado - so na comparacao entre produtos, que e
    /// exatamente onde ninguem olha.
    /// </summary>
    [Fact]
    public void Os_cinco_produtos_pontuam_na_mesma_escala()
    {
        var fits = ProductFitScoring.Calculate(Base());

        Assert.All(fits, f => Assert.Equal(100m, f.Reasons.Sum(r => r.MaxPoints)));
    }

    [Fact]
    public void Todo_produto_vendavel_tem_personas_no_catalogo()
    {
        var fits = ProductFitScoring.Calculate(Base());

        Assert.All(fits, f => Assert.NotEmpty(f.Personas));
        Assert.DoesNotContain(fits, f => f.Product == ProductCatalog.PartnerProgram);
    }
}
