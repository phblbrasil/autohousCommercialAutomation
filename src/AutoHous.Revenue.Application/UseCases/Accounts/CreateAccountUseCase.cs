using AutoHous.Revenue.Domain;

namespace AutoHous.Revenue.Application;

public sealed record CreateAccountCommand
{
    public required string Cnpj { get; init; }
    public required string Name { get; init; }
    public string? RazaoSocial { get; init; }
    public string? Uf { get; init; }
    public string? Municipio { get; init; }
}

public enum CreateAccountOutcome
{
    Created,
    InvalidCnpj,
    MissingName
}

public sealed record CreateAccountResult(
    CreateAccountOutcome Outcome,
    Guid AccountId = default,
    string? Cnpj = null,
    string? Detail = null);

/// <summary>
/// Cria (ou reencontra) a conta a partir de um CNPJ.
///
/// A validacao dos digitos verificadores e a normalizacao acontecem aqui, e nao
/// no endpoint: o mesmo caso de uso e chamado pelo pipeline de ingestao, que nao
/// passa por HTTP nenhum.
/// </summary>
public sealed class CreateAccountUseCase(IAccountRepository accounts)
{
    public async Task<CreateAccountResult> ExecuteAsync(
        CreateAccountCommand command, CancellationToken ct = default)
    {
        if (!CnpjNormalizer.TryNormalize(command.Cnpj, out var cnpj))
        {
            return new CreateAccountResult(
                CreateAccountOutcome.InvalidCnpj,
                Detail: $"'{command.Cnpj}' nao passou na validacao dos digitos verificadores.");
        }

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return new CreateAccountResult(
                CreateAccountOutcome.MissingName,
                Detail: "Nome da conta e obrigatorio.");
        }

        var id = await accounts.CreateFromCnpjAsync(
            cnpj, command.Name.Trim(), command.RazaoSocial, command.Uf, command.Municipio, ct);

        return new CreateAccountResult(CreateAccountOutcome.Created, id, cnpj);
    }
}
