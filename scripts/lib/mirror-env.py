"""Espelha no .env do projeto as chaves que scripts/hermes-setup.sh gerou.

Arquivo separado, e nao heredoc dentro do shell, porque o .env e a fonte da
verdade das duas credenciais: uma substituicao malfeita aqui deixa a API e o MCP
com chaves diferentes, e o sintoma disso e 401 sem explicacao.

Substitui a linha quando a chave ja existe (preservando ordem e comentarios do
arquivo) e acrescenta ao final quando nao existe.
"""

import os
import pathlib

path = pathlib.Path(os.environ["ENV_FILE"])

valores = {
    "HERMES_API_SERVER_KEY": os.environ["HERMES_KEY"],
    "REVENUE_API_KEY": os.environ["REVENUE_KEY"],
}

saida: list[str] = []
vistas: set[str] = set()

for linha in path.read_text(encoding="utf-8").splitlines(keepends=True):
    chave = linha.split("=", 1)[0]

    if chave in valores:
        saida.append(f"{chave}={valores[chave]}\n")
        vistas.add(chave)
    else:
        saida.append(linha)

faltando = [chave for chave in valores if chave not in vistas]

if faltando:
    if saida and not saida[-1].endswith("\n"):
        saida.append("\n")

    for chave in faltando:
        saida.append(f"{chave}={valores[chave]}\n")

path.write_text("".join(saida), encoding="utf-8")
