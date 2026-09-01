namespace AutoHous.Revenue.Infrastructure;

/// <summary>
/// Normaliza instantes na fronteira da persistencia.
///
/// O Npgsql RECUSA gravar um <see cref="DateTimeOffset"/> com offset diferente de
/// zero em coluna <c>timestamptz</c>:
///
///     Cannot write DateTimeOffset with Offset=-03:00:00 to PostgreSQL type
///     'timestamp with time zone', only offset 0 (UTC) is supported.
///
/// Isso e um detalhe do driver, e nao do dominio - por isso a conversao mora aqui
/// e nao nos contratos. Mas e um detalhe que quebra escrita, entao nao pode ficar
/// implicito em cada chamada.
///
/// **Por que isto existe.** Os agentes devolvem <c>observed_at</c> em ISO-8601, e
/// o schema aceita qualquer offset valido - como deve. Um agente pesquisando
/// empresa brasileira responde <c>2026-08-31T14:20:00-03:00</c> com naturalidade,
/// e essa string atravessa o validador, o guard e a desserializacao sem um arranhao.
/// A falha so aparece no INSERT, e como o dispatcher captura a excecao e reagenda,
/// ela se apresenta como "o evento nao processa" - sem nenhuma mencao a fuso.
///
/// Os fixtures gravados usavam <c>Z</c>, entao a bateria inteira passava verde
/// sobre um caminho que nunca teria funcionado com o Hermes real. E o mesmo tipo
/// de defeito do transporte de /v1/runs: invisivel sob fixture, fatal na ativacao.
///
/// A conversao preserva o INSTANTE e descarta o offset, que e exatamente o que
/// <c>timestamptz</c> guarda - o Postgres nunca armazenou o fuso de origem.
/// </summary>
internal static class Timestamps
{
    public static DateTimeOffset ForPostgres(DateTimeOffset value) => value.ToUniversalTime();

    public static DateTimeOffset? ForPostgres(DateTimeOffset? value) => value?.ToUniversalTime();
}
