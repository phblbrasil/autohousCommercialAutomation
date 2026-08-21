namespace AutoHous.Revenue.ReceitaFederal;

/// <summary>Configuracao do acesso ao repositorio de Dados Abertos CNPJ.</summary>
public sealed class ReceitaOptions
{
    public const string EnvShareToken = "RECEITA_SHARE_TOKEN";
    public const string EnvCacheDir = "RECEITA_CACHE_DIR";

    public string BaseUrl { get; set; } = "https://arquivos.receitafederal.gov.br";

    /// <summary>
    /// Token do compartilhamento publico. Quando nulo, e descoberto do redirect
    /// 302 da raiz do site.
    ///
    /// Descobrir em vez de fixar importa: o repositorio da Receita migrou para
    /// Nextcloud e os caminhos antigos (<c>/dados/cnpj/...</c>) hoje respondem
    /// 404. Um token embutido no codigo garante que a proxima migracao quebre a
    /// carga de novo.
    /// </summary>
    public string? ShareToken { get; set; }

    /// <summary>Caminho do CNPJ dentro do compartilhamento.</summary>
    public string BasePath { get; set; } = "Dados/Cadastros/CNPJ";

    /// <summary>Onde os zips ficam. Reexecutar a carga nao rebaixa o que ja esta integro.</summary>
    public string CacheDirectory { get; set; } = ".receita-cache";

    /// <summary>
    /// Trabalha so com o que ja esta em <see cref="CacheDirectory"/>, sem
    /// consultar a origem.
    ///
    /// Existe porque baixar 7,3 GB por HTTP nem sempre e a melhor forma de obter
    /// os arquivos: link instavel, gerenciador de download externo, maquina sem
    /// saida para a internet, ou uma copia do release que ja circula na equipe.
    /// </summary>
    public bool OfflineOnly { get; set; }

    /// <summary>Onde o spool da carga vive.</summary>
    public string WorkDirectory { get; set; } = ".receita-work";

    /// <summary>
    /// Timeout de uma requisicao de download. Generoso de proposito:
    /// Estabelecimentos0.zip tem 2 GB, e cortar no meio so troca uma espera longa
    /// por varias retomadas.
    /// </summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
