namespace AutoHous.Revenue.Application;

/// <summary>Um arquivo publicado pela Receita Federal dentro de um release.</summary>
public sealed record ReceitaArchiveFile(string Name, long Length, DateTimeOffset LastModified);

/// <summary>
/// Acesso ao repositorio de Dados Abertos CNPJ da Receita Federal.
///
/// Porta e nao classe concreta porque o adaptador fala HTTP, e a Application nao
/// pode fazer rede por conta propria - <c>DependencyRuleTests</c> proibe
/// <c>System.Net.Http</c> nesta camada. O efeito pratico e que a orquestracao da
/// carga e testavel com um arquivo local no lugar do repositorio inteiro.
/// </summary>
public interface IReceitaFederalArchive
{
    /// <summary>Competencias publicadas, em ordem crescente (<c>2023-05</c> … <c>2026-08</c>).</summary>
    Task<IReadOnlyList<string>> ListReleasesAsync(CancellationToken ct = default);

    /// <summary>Arquivos do release, com tamanho declarado pela origem.</summary>
    Task<IReadOnlyList<ReceitaArchiveFile>> ListFilesAsync(string release, CancellationToken ct = default);

    /// <summary>
    /// Abre o arquivo a partir de <paramref name="offset"/> bytes.
    ///
    /// O offset existe porque <c>Estabelecimentos0.zip</c> tem 2 GB: sem retomada,
    /// qualquer queda de conexao reinicia o download do zero, e a carga mensal
    /// vira uma aposta na estabilidade da rede.
    /// </summary>
    Task<Stream> OpenAsync(string release, string fileName, long offset, CancellationToken ct = default);
}
