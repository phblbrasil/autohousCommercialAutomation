#!/usr/bin/env bash
# Roda a suite inteira via `dotnet test`, com contingencia.
#
# NUNCA passe --nologo. No modo Microsoft.Testing.Platform - eleito no
# global.json - a flag faz o host localizar os modulos e nao iniciar nenhum:
#
#   dotnet test            -> 416 testes, codigo 0
#   dotnet test --nologo   -> "Zero testes executados", codigo 5
#
# O sintoma parece suite vazia ou ambiente quebrado, e nao e nem um nem outro.
# Por isso este script existe: ele fixa a invocacao correta num lugar so.
#
# A contingencia - invocar o binario de cada projeto direto - fica como rede de
# seguranca para qualquer outra falha de host que produza o mesmo sintoma.
#
# Argumentos extras vao para o runner. Exemplos:
#   ./scripts/test.sh --filter "FullyQualifiedName~AccountStatusTransitionsTests"
#   CONFIG=Release ./scripts/test.sh
#
# Nota sobre a CLI no modo MTP: um projeto especifico se passa com `--project`,
# nao posicionalmente.
#   ok:   dotnet test --project tests/AutoHous.Revenue.Domain.Tests/AutoHous.Revenue.Domain.Tests.csproj
#   erro: dotnet test tests/AutoHous.Revenue.Domain.Tests/AutoHous.Revenue.Domain.Tests.csproj
set -uo pipefail

cd "$(dirname "$0")/.."

CONFIG="${CONFIG:-Debug}"

filtrado=""
for arg in "$@"; do
  case "$arg" in
    --filter*) filtrado=1; break ;;
  esac
done

# No MTP, um projeto que executa zero testes sai com codigo 8. Rodando a solucao
# inteira com um filtro isso e esperado - o filtro casa em um projeto e os outros
# ficam vazios -, e sem tratamento a execucao inteira falharia mesmo com todos os
# testes casados passando.
#
# Por isso o codigo 8 so e ignorado quando ha filtro. Sem filtro ele continua
# sendo falha: suite vazia de verdade tem que aparecer.
if [[ -n "$filtrado" ]]; then
  saida="$(dotnet test -c "$CONFIG" "$@" --ignore-exit-code 8 2>&1 | tee /dev/stderr)"
else
  saida="$(dotnet test -c "$CONFIG" "$@" 2>&1 | tee /dev/stderr)"
fi
codigo="${PIPESTATUS[0]}"

# O token "total:" e igual em pt-BR e em ingles, entao a leitura nao depende do
# idioma da CLI.
total="$(grep -oE 'total: *[0-9]+' <<<"$saida" | tail -1 | grep -oE '[0-9]+' || true)"

if [[ "${total:-0}" -gt 0 ]]; then
  if [[ "$codigo" -ne 0 ]]; then exit "$codigo"; fi

  # Guarda contra o efeito colateral de ignorar o 8: um filtro com erro de
  # digitacao nao casaria nada em projeto nenhum e a execucao sairia verde com
  # zero testes - o falso-positivo que este script existe para nao repetir.
  exit 0
fi

echo >&2
echo "AVISO: 'dotnet test' nao iniciou nenhum modulo (host MTP nao conectou)." >&2
echo "       Executando os binarios de teste direto." >&2
echo >&2

dotnet build -c "$CONFIG" -v q --nologo || exit 1

encontrados=0
falhou=0

for projeto in tests/*/; do
  nome="$(basename "$projeto")"
  binario="$projeto/bin/$CONFIG/net10.0/$nome"

  [[ -x "$binario" ]] || continue
  encontrados=$((encontrados + 1))

  "$binario" "$@"
  codigo=$?

  # Codigo 8 = zero testes casados neste projeto. So e aceitavel com filtro.
  if [[ "$codigo" -eq 8 && -n "$filtrado" ]]; then continue; fi
  if [[ "$codigo" -ne 0 ]]; then falhou=1; fi
done

if [[ "$encontrados" -eq 0 ]]; then
  echo "ERRO: nenhum binario de teste encontrado em tests/*/bin/$CONFIG/net10.0/." >&2
  exit 1
fi

exit "$falhou"
