#!/usr/bin/env bash
# Roda a suite inteira via `dotnet test`, com contingencia.
#
# NUNCA passe --nologo. No modo Microsoft.Testing.Platform - eleito no
# global.json - a flag faz o host localizar os modulos e nao iniciar nenhum:
#
#   dotnet test            -> 433 testes, codigo 0
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

# Os modulos que a suite tem que exercitar. tests/fixtures nao tem csproj e fica
# de fora sozinho.
modulos=()
for projeto in tests/*/; do
  nome="$(basename "$projeto")"
  [[ -f "$projeto$nome.csproj" ]] && modulos+=("$nome")
done

# Executa um modulo pelo dotnet, e nao pelo apphost .exe gerado ao lado do dll.
#
# Sob uma politica de Application Control - o Smart App Control do Windows, por
# exemplo - o .exe recem-compilado nao tem reputacao e e bloqueado no start
# ("An Application Control policy has blocked this file"), enquanto o dotnet,
# assinado, passa. Mesmo host de teste, mesmos testes, sem o bloqueio.
executar_modulo() {
  local nome="$1"; shift
  local dll="tests/$nome/bin/$CONFIG/net10.0/$nome.dll"

  if [[ ! -f "$dll" ]]; then
    echo "ERRO: binario de teste ausente: $dll" >&2
    return 1
  fi

  dotnet exec "$dll" "$@"
  local codigo=$?

  # Codigo 8 = zero testes casados neste modulo. So e aceitavel com filtro.
  if [[ "$codigo" -eq 8 && -n "$filtrado" ]]; then return 0; fi
  return "$codigo"
}

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
falhas="$(grep -oE 'failed: *[0-9]+' <<<"$saida" | tail -1 | grep -oE '[0-9]+' || true)"

if [[ "${total:-0}" -gt 0 ]]; then
  # Um modulo cujo host nao inicia NAO aparece no sumario: a excecao sai no topo
  # da saida e a contagem final simplesmente ignora o modulo inteiro. Sem esta
  # verificacao a suite fica verde com uma bateria desligada - foi o que
  # aconteceu com a Architecture.Tests, justamente a que impoe as direcoes de
  # dependencia. Contar os testes nao basta; e preciso conferir quem reportou.
  ausentes=()
  for nome in "${modulos[@]}"; do
    grep -qE "${nome}\.dll \(net[^)]*\) (passed|failed)" <<<"$saida" || ausentes+=("$nome")
  done

  contingencia=0

  if [[ "${#ausentes[@]}" -gt 0 ]]; then
    echo >&2
    echo "AVISO: modulo(s) ausente(s) do sumario: ${ausentes[*]}" >&2
    echo "       O host nao iniciou. Executando cada um direto, pelo dotnet." >&2
    echo >&2

    for nome in "${ausentes[@]}"; do
      executar_modulo "$nome" "$@" || contingencia=1
    done
  fi

  # O veredito vem da contagem reportada mais o resultado da contingencia, e nao
  # do codigo de saida cru: `dotnet test` tambem sai diferente de zero quando um
  # host nao inicia - exatamente o caso que a contingencia acabou de cobrir. Ler
  # so o codigo deixaria a suite vermelha com 433 testes verdes.
  #
  # "falhas" vazio conta como falha: sumario que nao se deixa ler nao vira verde.
  if [[ "${falhas:-1}" -eq 0 && "$contingencia" -eq 0 ]]; then
    exit 0
  fi

  # Guarda contra o efeito colateral de ignorar o 8: um filtro com erro de
  # digitacao nao casaria nada em projeto nenhum e a execucao sairia verde com
  # zero testes - o falso-positivo que este script existe para nao repetir.
  [[ "$codigo" -ne 0 ]] && exit "$codigo"
  exit 1
fi

echo >&2
echo "AVISO: 'dotnet test' nao iniciou nenhum modulo (host MTP nao conectou)." >&2
echo "       Executando os binarios de teste direto." >&2
echo >&2

dotnet build -c "$CONFIG" -v q --nologo || exit 1

encontrados=0
falhou=0

for nome in "${modulos[@]}"; do
  [[ -f "tests/$nome/bin/$CONFIG/net10.0/$nome.dll" ]] || continue
  encontrados=$((encontrados + 1))

  executar_modulo "$nome" "$@" || falhou=1
done

if [[ "$encontrados" -eq 0 ]]; then
  echo "ERRO: nenhum binario de teste encontrado em tests/*/bin/$CONFIG/net10.0/." >&2
  exit 1
fi

exit "$falhou"
