using AutoHous.Revenue.Domain;
using AutoHous.Revenue.Domain.Contracts;

namespace AutoHous.Revenue.Agents.Tests;

/// <summary>
/// As fixtures do Product Matcher (A04) e do People Finder (A05) passam pelo
/// caminho REAL: extractor, JSON Schema de disco e EvidenceFirstGuard.
///
/// Isto nao testa os agentes - testa que os cinco cenarios de cada um exercitam
/// de fato o que o nome deles promete. Uma fixture `missing-evidence` que passa
/// no guard e pior que fixture nenhuma: ela faz o ciclo de reparo parecer
/// coberto por teste quando ninguem nunca o percorreu.
///
/// O schema vem de <see cref="RepoPaths"/>, e nao de uma copia no projeto de
/// teste, pela mesma razao de sempre: uma copia diverge do que roda em producao
/// exatamente no dia em que alguem edita so um dos dois.
/// </summary>
public class NewAgentFixtureTests
{
    private static StructuredOutputValidator PitchValidator() =>
        new(new Dictionary<Type, string>
        {
            [typeof(ProductPitchProfile)] = File.ReadAllText(RepoPaths.Schema("product-pitch.schema.json"))
        });

    private static StructuredOutputValidator PeopleValidator() =>
        new(new Dictionary<Type, string>
        {
            [typeof(ContactDiscoveryProfile)] =
                File.ReadAllText(RepoPaths.Schema("contact-discovery.schema.json"))
        });

    private static string Read(string agent, string scenario) =>
        File.ReadAllText(RepoPaths.Fixture(agent, scenario));

    // --------------------------------------------------------- product matcher

    [Theory]
    [InlineData("success")]
    [InlineData("malformed")]
    [InlineData("malformed-repaired")]
    [InlineData("missing-evidence-repaired")]
    public void Pitch_valido_passa_no_schema_e_no_guard(string scenario)
    {
        var outcome = PitchValidator().Validate<ProductPitchProfile>(Read("product-matcher", scenario));

        Assert.True(outcome.IsValid,
            $"Cenario '{scenario}' reprovado no schema: {string.Join("; ", outcome.Violations)}");

        Assert.Empty(EvidenceFirstGuard.Check(outcome.Value!));
    }

    /// <summary>
    /// O cenario `malformed` traz prosa, cerca de codigo e virgula sobrando. Ele
    /// tem que passar: quem o tolera e o <see cref="JsonPayloadExtractor"/>, e a
    /// razao de ele existir e que modelo devolve texto, nao JSON.
    ///
    /// A distincao entre `malformed` e `missing-evidence` e o que cada um
    /// exercita. O primeiro e sujeira de formato e o extractor resolve sozinho;
    /// o segundo e violacao de contrato e so o ciclo de reparo resolve.
    /// </summary>
    [Fact]
    public void Pitch_malformado_e_recuperado_pelo_extractor_sem_reparo()
    {
        var raw = Read("product-matcher", "malformed");

        Assert.Contains("```json", raw);

        var outcome = PitchValidator().Validate<ProductPitchProfile>(raw);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public void Pitch_sem_lastro_e_recusado_pelo_guard()
    {
        var outcome = PitchValidator().Validate<ProductPitchProfile>(
            Read("product-matcher", "missing-evidence"));

        // O schema passa: os indices sao inteiros >= 0 e as personas sao
        // strings de tamanho valido. Que o indice APONTE para uma evidencia
        // existente, e que a persona pertenca AQUELE produto, sao as duas coisas
        // que nenhum JSON Schema sabe expressar - e sao exatamente o que o guard
        // existe para cobrir.
        Assert.True(outcome.IsValid,
            $"A fixture deveria falhar no GUARD, nao no schema: {string.Join("; ", outcome.Violations)}");

        var violations = EvidenceFirstGuard.Check(outcome.Value!);

        Assert.NotEmpty(violations);

        Assert.Contains(violations, v => v.Location.Contains("evidence_index", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Location.Contains("recommended_personas", StringComparison.Ordinal));
    }

    [Fact]
    public void Pitch_so_recomenda_produto_do_catalogo()
    {
        var outcome = PitchValidator().Validate<ProductPitchProfile>(Read("product-matcher", "success"));

        foreach (var pitch in outcome.Value!.Pitches)
        {
            Assert.True(ProductCatalog.IsKnown(pitch.Product), $"Produto desconhecido: {pitch.Product}");

            // O Partner Program e canal, e nao produto para a conta prospectada:
            // oferece-lo a uma concessionaria seria propor que ela revenda a
            // AutoHous. O schema o exclui por enum; isto guarda a intencao.
            Assert.NotEqual(ProductCatalog.PartnerProgram, pitch.Product);
        }
    }

    // ----------------------------------------------------------- people finder

    [Theory]
    [InlineData("success")]
    [InlineData("malformed")]
    [InlineData("malformed-repaired")]
    [InlineData("missing-evidence-repaired")]
    public void Contatos_validos_passam_no_schema_e_no_guard(string scenario)
    {
        var outcome = PeopleValidator().Validate<ContactDiscoveryProfile>(Read("people-finder", scenario));

        Assert.True(outcome.IsValid,
            $"Cenario '{scenario}' reprovado no schema: {string.Join("; ", outcome.Violations)}");

        Assert.Empty(EvidenceFirstGuard.Check(outcome.Value!));
    }

    /// <summary>
    /// As tres violacoes que so este contrato tem, todas na mesma fixture: canal
    /// de e-mail reusando a evidencia do contato, confianca de contato abaixo do
    /// piso, e indice de canal fora do intervalo.
    ///
    /// A primeira e a que mais importa. Um e-mail deduzido do padrao da empresa
    /// - <c>nome.sobrenome@</c> - passa em qualquer schema, tem formato valido e
    /// aponta para uma evidencia real: a noticia que citava o nome. So a regra de
    /// escopo o pega, e sem ela a plataforma escreveria para um endereco que
    /// ninguem nunca viu.
    /// </summary>
    [Fact]
    public void Contatos_sem_lastro_proprio_sao_recusados_pelo_guard()
    {
        var outcome = PeopleValidator().Validate<ContactDiscoveryProfile>(
            Read("people-finder", "missing-evidence"));

        Assert.True(outcome.IsValid,
            $"A fixture deveria falhar no GUARD, nao no schema: {string.Join("; ", outcome.Violations)}");

        var violations = EvidenceFirstGuard.Check(outcome.Value!);

        Assert.Contains(violations, v =>
            v.Message.Contains("mesma evidencia do contato", StringComparison.Ordinal));

        Assert.Contains(violations, v =>
            v.Message.Contains("abaixo do minimo", StringComparison.Ordinal));

        Assert.Contains(violations, v =>
            v.Message.Contains("evidencia inexistente", StringComparison.Ordinal));
    }

    /// <summary>
    /// O reparo REMOVE o contato abaixo do piso em vez de inflar a confianca -
    /// e registra o cargo em <c>searched_without_result</c>.
    ///
    /// E o comportamento que o prompt de reparo pede explicitamente, e a fixture
    /// existe para fixa-lo: um reparo que "conserta" subindo a confianca de 0.35
    /// para 0.6 satisfaz o guard e produz exatamente o dado que o piso existe
    /// para impedir.
    /// </summary>
    [Fact]
    public void Reparo_de_contatos_remove_em_vez_de_inflar_confianca()
    {
        var rejected = PeopleValidator()
            .Validate<ContactDiscoveryProfile>(Read("people-finder", "missing-evidence")).Value!;

        var repaired = PeopleValidator()
            .Validate<ContactDiscoveryProfile>(Read("people-finder", "missing-evidence-repaired")).Value!;

        var dropped = rejected.Contacts.Single(c => c.Confidence < ContactPolicy.MinimumContactConfidence);

        Assert.DoesNotContain(repaired.Contacts, c => c.FullName == dropped.FullName);
        Assert.Contains(dropped.JobTitle, repaired.SearchedWithoutResult);
        Assert.True(repaired.SearchCompleteness < rejected.SearchCompleteness);
    }

    /// <summary>
    /// Todo cargo das fixtures cai numa persona do catalogo. Uma fixture com
    /// cargo que o <see cref="PersonaCatalog"/> nao reconhece gravaria
    /// <c>contacts.persona</c> nulo, e a fila de abordagem por persona nao
    /// acharia a linha - o contato existiria no banco sem nunca ser usado.
    /// </summary>
    [Fact]
    public void Cargos_das_fixtures_sao_classificaveis()
    {
        var profile = PeopleValidator()
            .Validate<ContactDiscoveryProfile>(Read("people-finder", "success")).Value!;

        foreach (var contact in profile.Contacts)
        {
            var match = PersonaCatalog.Classify(contact.JobTitle);

            Assert.NotNull(match);
            Assert.Contains(match!.Persona, PersonaCatalog.Canonical);
        }
    }
}
