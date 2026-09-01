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

        CheckEvidenceList(profile.Evidence, violations);

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

    /// <summary>
    /// A mesma Regra 1 aplicada ao Website Auditor (A03).
    ///
    /// Sobrecarga e nao classe propria: a regra e uma so, e duplica-la seria
    /// abrir a porta para as duas versoes divergirem no dia em que alguem
    /// endurecer uma delas.
    ///
    /// A checagem especifica daqui e a de <c>approximate_count</c>, pelo mesmo
    /// motivo que <c>store_count</c> tem a dela: um numero de veiculos em
    /// estoque sai desta estrutura direto para a abordagem comercial, e "vi que
    /// voces tem 380 carros no site" sem lastro e exatamente a frase que a
    /// Regra 1 existe para impedir.
    /// </summary>
    public static IReadOnlyList<SchemaViolation> Check(WebsiteAuditProfile profile)
    {
        var violations = new List<SchemaViolation>();
        var evidenceCount = profile.Evidence.Count;

        CheckEvidenceList(profile.Evidence, violations);

        CheckIndex(profile.Portals.Select((p, i) => ($"/portals/{i}", p.EvidenceIndex)), evidenceCount, violations);
        CheckIndex(profile.Integrations.Select((g, i) => ($"/integrations/{i}", g.EvidenceIndex)), evidenceCount, violations);
        CheckIndex(profile.Issues.Select((s, i) => ($"/issues/{i}", s.EvidenceIndex)), evidenceCount, violations);
        CheckIndex(profile.Strengths.Select((s, i) => ($"/strengths/{i}", s.EvidenceIndex)), evidenceCount, violations);

        if (profile.Inventory is { } inventory)
        {
            CheckIndex([("/inventory", inventory.EvidenceIndex)], evidenceCount, violations);

            if (inventory.ApproximateCount is > 0 &&
                !profile.Evidence.Any(e =>
                    e.ClaimType.Contains("inventory", StringComparison.OrdinalIgnoreCase) ||
                    e.ClaimType.Contains("estoque", StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(new SchemaViolation(
                    "/inventory/approximate_count",
                    "Contagem de estoque informada sem evidencia de claim_type relacionado a estoque."));
            }
        }

        if (profile.Conversion is { } conversion)
        {
            CheckIndex([("/conversion", conversion.EvidenceIndex)], evidenceCount, violations);
        }

        return violations;
    }

    /// <summary>
    /// A mesma Regra 1 aplicada ao Product Matcher (A04).
    ///
    /// A checagem especifica daqui e a de PERSONA: o agente pode restringir a
    /// lista do catalogo, e nao pode inventar cargo. Sem isto, um
    /// "recommended_personas: ['Dono do grupo']" entraria, o People Finder
    /// procuraria por um cargo que nao existe na taxonomia e a busca voltaria
    /// vazia - sem que ninguem soubesse que o problema nasceu tres etapas antes.
    ///
    /// O que NAO se checa aqui: se o produto do pitch e conhecido (o schema o
    /// resolve com <c>enum</c>) e se um pitch tem ao menos um motivo (o schema o
    /// resolve com <c>minItems</c>). Regra que o schema ja impoe nao se duplica:
    /// duas checagens da mesma coisa divergem no dia em que so uma for
    /// atualizada, e a que sobra passa a mentir sobre o que garante.
    /// </summary>
    public static IReadOnlyList<SchemaViolation> Check(ProductPitchProfile profile)
    {
        var violations = new List<SchemaViolation>();
        var evidenceCount = profile.Evidence.Count;

        CheckEvidenceList(profile.Evidence, violations);

        CheckIndex(
            profile.Disqualifiers.Select((d, i) => ($"/disqualifiers/{i}", d.EvidenceIndex)),
            evidenceCount, violations);

        for (var p = 0; p < profile.Pitches.Count; p++)
        {
            var pitch = profile.Pitches[p];

            CheckIndex(
                pitch.Reasons.Select((r, i) => ($"/pitches/{p}/reasons/{i}", r.EvidenceIndex)),
                evidenceCount, violations);

            CheckIndex(
                pitch.Objections.Select((o, i) => ($"/pitches/{p}/objections/{i}", o.EvidenceIndex)),
                evidenceCount, violations);

            var known = ProductCatalog.Find(pitch.Product);

            if (known is null) continue;

            foreach (var persona in pitch.RecommendedPersonas)
            {
                if (!known.Personas.Contains(persona, StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add(new SchemaViolation(
                        $"/pitches/{p}/recommended_personas",
                        $"'{persona}' nao e persona de {pitch.Product}. " +
                        $"Use uma de: {string.Join(", ", known.Personas)}."));
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// A mesma Regra 1 aplicada ao People Finder (A05), mais dura que as outras
    /// tres porque o que esta em jogo e diferente.
    ///
    /// Nos outros contratos, uma afirmacao sem lastro produz um argumento
    /// comercial fraco. Aqui produz o nome, o cargo e o e-mail de uma PESSOA. Um
    /// <c>evidence_index</c> errado num pitch de MotorHub e uma frase que nao se
    /// sustenta; num contato, e afirmar que alguem trabalha em algum lugar sem
    /// ter onde isso foi visto - e depois escrever para essa pessoa.
    ///
    /// Por isso as duas checagens que so existem nesta sobrecarga:
    ///
    /// 1. **Cada canal carrega evidencia propria.** Achar o nome de um diretor e
    ///    achar o e-mail dele sao descobertas distintas. Deixar o canal herdar a
    ///    evidencia do contato deixaria passar um e-mail deduzido do padrao da
    ///    empresa - <c>nome.sobrenome@</c> - com o lastro da noticia que so
    ///    citava o nome.
    /// 2. **Piso de confianca do <see cref="ContactPolicy"/>.** Abaixo dele o run
    ///    e recusado em vez de a linha ser silenciosamente descartada: se o
    ///    modelo esta devolvendo palpite, quem precisa saber e quem le o erro do
    ///    run, nao um <c>where confidence &gt;= 0.5</c> escondido no persister.
    /// </summary>
    public static IReadOnlyList<SchemaViolation> Check(ContactDiscoveryProfile profile)
    {
        var violations = new List<SchemaViolation>();
        var evidenceCount = profile.Evidence.Count;

        CheckEvidenceList(profile.Evidence, violations);

        for (var c = 0; c < profile.Contacts.Count; c++)
        {
            var contact = profile.Contacts[c];

            CheckIndex([($"/contacts/{c}", contact.EvidenceIndex)], evidenceCount, violations);

            if (contact.Confidence < ContactPolicy.MinimumContactConfidence)
            {
                violations.Add(new SchemaViolation(
                    $"/contacts/{c}/confidence",
                    $"Confianca {contact.Confidence:0.00} abaixo do minimo " +
                    $"({ContactPolicy.MinimumContactConfidence:0.00}) para gravar uma pessoa. " +
                    "Omita o contato em vez de registrar um palpite."));
            }

            for (var h = 0; h < contact.Channels.Count; h++)
            {
                var channel = contact.Channels[h];
                var path = $"/contacts/{c}/channels/{h}";

                CheckIndex([(path, channel.EvidenceIndex)], evidenceCount, violations);

                if (channel.Confidence < ContactPolicy.MinimumChannelConfidence)
                {
                    violations.Add(new SchemaViolation(
                        $"{path}/confidence",
                        $"Confianca {channel.Confidence:0.00} abaixo do minimo " +
                        $"({ContactPolicy.MinimumChannelConfidence:0.00}) para gravar um canal. " +
                        "Canal errado manda mensagem para um terceiro."));
                }

                // Canal deduzido do padrao da empresa costuma reaproveitar a
                // evidencia do NOME - a noticia que citava a pessoa. A evidencia
                // do canal tem que ser a pagina onde o canal aparece.
                if (channel.EvidenceIndex == contact.EvidenceIndex &&
                    channel.Channel is ContactChannel.Email or ContactChannel.Mobile or ContactChannel.Whatsapp)
                {
                    violations.Add(new SchemaViolation(
                        $"{path}/evidence_index",
                        $"Canal '{channel.Channel}' aponta para a mesma evidencia do contato. " +
                        "Registre a fonte em que o CANAL aparece; um endereco deduzido do " +
                        "padrao da empresa nao tem lastro."));
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Toda evidencia precisa de fonte utilizavel. O schema sabe exigir que a
    /// URL seja string com formato de URI; ele nao sabe exigir que ela exista.
    /// </summary>
    private static void CheckEvidenceList(
        IReadOnlyList<EvidenceClaim> evidence, List<SchemaViolation> violations)
    {
        for (var i = 0; i < evidence.Count; i++)
        {
            var item = evidence[i];

            if (string.IsNullOrWhiteSpace(item.Source.Url))
            {
                violations.Add(new SchemaViolation(
                    $"/evidence/{i}/source/url",
                    "Evidencia sem URL de fonte: afirmacao nao auditavel."));
            }

            if (item.Confidence <= 0)
            {
                violations.Add(new SchemaViolation(
                    $"/evidence/{i}/confidence",
                    "Confianca zero equivale a nao ter evidencia."));
            }
        }
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
