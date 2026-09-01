#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Entrypoint do gateway do Hermes no Railway.
#
# E o analogo em container do scripts/hermes-setup.sh, com uma diferenca de
# fundo: o setup local e idempotente sobre uma maquina que persiste, enquanto
# aqui o sistema de arquivos morre a cada deploy. Portanto nada e "preservado se
# ja existir" — tudo e derivado das variaveis a cada boot, e as variaveis sao a
# unica fonte da verdade.
# ---------------------------------------------------------------------------
set -euo pipefail

HERMES_HOME="${HERMES_HOME:-/root/.hermes}"
SECRET_FILE="${REVENUE_API_KEY_FILE:-/run/secrets/revenue-api-key}"
MCP_DIR="${AUTOHOUS_MCP_DIR:-/opt/autohous/revenue-mcp}"
SKILLS_DIR="${AUTOHOUS_SKILLS_DIR:-/opt/autohous/hermes/skills}"

die() { printf '\033[0;31mFATAL\033[0m %s\n' "$1" >&2; exit 1; }
say() { printf '\033[0;36m→\033[0m %s\n' "$1"; }

# --- 1. Validacao, antes de qualquer efeito colateral -----------------------
# O gateway carrega a plataforma api_server quando encontra uma API_SERVER_KEY
# UTILIZAVEL — 16 caracteres ou mais. Com chave curta ele nao reclama: sobe sem
# a plataforma e o watcher de reconexao gira em erro, o que no Railway aparece
# como um servico "verde" que nunca responde. Melhor morrer aqui.
[ -n "${HERMES_API_SERVER_KEY:-}" ] || die "HERMES_API_SERVER_KEY ausente."
[ "${#HERMES_API_SERVER_KEY}" -ge 16 ] \
  || die "HERMES_API_SERVER_KEY tem ${#HERMES_API_SERVER_KEY} caracteres; o piso do Hermes e 16."

[ -n "${REVENUE_API_KEY:-}" ] || die "REVENUE_API_KEY ausente: o MCP nao alcancaria a Revenue API."
[ "${#REVENUE_API_KEY}" -ge 24 ] \
  || die "REVENUE_API_KEY tem ${#REVENUE_API_KEY} caracteres; o piso de RevenueApiKeys e 24."

[ -n "${REVENUE_API_URL:-}" ] || die "REVENUE_API_URL ausente. Ver docs/deploy-railway.md."

case "$REVENUE_API_URL" in
  *.railway.internal*|http://localhost*|http://127.0.0.1*|http://\[::1\]*) ;;
  *) printf '\033[0;33m!\033[0m REVENUE_API_URL=%s nao e endereco da rede privada; o trafego do MCP sai para a internet.\n' \
       "$REVENUE_API_URL" >&2 ;;
esac

# --- 2. Segredo da Revenue API em arquivo ----------------------------------
# O bloco env: do MCP no config.yaml nao interpola ${VAR}, entao a alternativa
# seria o segredo literal num YAML. O arquivo tambem tira a chave do
# /proc/{pid}/environ do processo do gateway — que e justamente o processo com
# ferramenta de terminal exposta.
mkdir -p "$(dirname "$SECRET_FILE")"
printf '%s' "$REVENUE_API_KEY" > "$SECRET_FILE"
chmod 600 "$SECRET_FILE"
say "segredo da Revenue API materializado em $SECRET_FILE (0600)"

# --- 3. ~/.hermes/.env ------------------------------------------------------
mkdir -p "$HERMES_HOME"

# :: e nao 127.0.0.1. A rede privada do Railway e IPv6-only: ligado ao loopback,
# o gateway nao atenderia o worker, e ligado a 0.0.0.0 tambem nao. O que substitui
# o loopback como fronteira aqui e a AUSENCIA de dominio publico neste servico —
# ver o cabecalho do deploy/Dockerfile.hermes.
cat > "$HERMES_HOME/.env" <<EOF
API_SERVER_KEY=$HERMES_API_SERVER_KEY
API_SERVER_ENABLED=true
API_SERVER_HOST=::
API_SERVER_PORT=${PORT:-8642}
EOF
chmod 600 "$HERMES_HOME/.env"

# Credencial de provider de modelo. Sem ela o gateway sobe e todo run falha na
# primeira chamada, entao o aviso e explicito em vez de virar 500 no worker.
if [ -n "${HERMES_PORTAL_API_KEY:-}" ]; then
  echo "PORTAL_API_KEY=$HERMES_PORTAL_API_KEY" >> "$HERMES_HOME/.env"
elif [ -n "${OPENROUTER_API_KEY:-}" ]; then
  echo "OPENROUTER_API_KEY=$OPENROUTER_API_KEY" >> "$HERMES_HOME/.env"
else
  printf '\033[0;33m!\033[0m Nenhuma credencial de modelo (HERMES_PORTAL_API_KEY / OPENROUTER_API_KEY): os runs vao falhar.\n' >&2
fi

# --- 4. config.yaml ---------------------------------------------------------
# Escrito inteiro, e nao remendado como no setup local. La o alvo e o template de
# ~1.900 linhas comentadas que o instalador gera, e reserializar apagaria a
# documentacao; aqui nao ha ninguem para ler comentario dentro do container, e um
# arquivo derivado por completo das variaveis nao tem estado oculto entre deploys.
cat > "$HERMES_HOME/config.yaml" <<EOF
# Gerado por deploy/hermes-entrypoint.sh a cada boot. Editar aqui nao tem efeito:
# o proximo deploy sobrescreve. A fonte da verdade sao as variaveis do servico.

mcp_servers:
  autohous_revenue:
    command: "dotnet"
    args:
      - "$MCP_DIR/AutoHous.Revenue.Mcp.dll"
    env:
      REVENUE_API_URL: "$REVENUE_API_URL"
      REVENUE_API_KEY_FILE: "$SECRET_FILE"
    timeout: 30
    connect_timeout: 10
    enabled: true
    # O filtro de ferramentas do Hermes e por SERVIDOR, e nao por agente: nao
    # existe escopo por agente, entao esta allowlist e a unica fronteira efetiva
    # entre o modelo e a Revenue API. Tudo aqui e leitura.
    tools:
      include:
        - get_account_context
        - list_account_evidence
        - get_product_catalog
      exclude: []
      prompts: false
      resources: false

skills:
  external_dirs:
    - $SKILLS_DIR

delegation:
  max_concurrent_children: 3
  max_spawn_depth: 1
EOF

say "config.yaml gerado (MCP -> $REVENUE_API_URL, skills <- $SKILLS_DIR)"
say "subindo o gateway em [::]:${PORT:-8642}"

exec hermes gateway
