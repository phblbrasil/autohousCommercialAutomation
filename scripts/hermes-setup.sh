#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Liga o Hermes ao Revenue Engine. Idempotente: rodar de novo nao duplica nada.
#
# O que este script faz, e por que cada passo existe:
#
#   1. publica o MCP           o servidor stdio nao pode ser `dotnet run` - a
#                              saida de build corromperia o protocolo
#   2. ~/.hermes/.env          API_SERVER_KEY e o que LIGA o API Server: o
#                              gateway carrega a plataforma quando existe chave
#                              usavel (>= 16 chars, nao placeholder), nao por
#                              causa de API_SERVER_ENABLED
#   3. ~/.hermes/config.yaml   MCP com allowlist de ferramentas (a fronteira e
#                              por servidor, nao por agente) + a skill lida do
#                              repositorio via skills.external_dirs
#   4. segredo da Revenue API  chave de borda em arquivo 0600, e nao no
#                              config.yaml: o filtro de ambiente do Hermes so
#                              repassa ao MCP o que esta no bloco env:, e nao
#                              interpola ${VAR} - sem arquivo, o segredo teria de
#                              ficar literal num YAML de 0644
#   5. .env do projeto         as mesmas chaves dos dois lados
#
# O que ele NAO faz: `hermes setup --portal`. O login do Nous Portal e
# interativo e a credencial e sua.
# ---------------------------------------------------------------------------
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HERMES_HOME="${HERMES_HOME:-$HOME/.hermes}"
MCP_DIR="${AUTOHOUS_MCP_DIR:-$HOME/.local/share/autohous/revenue-mcp}"
HERMES_BIN="${HERMES_BIN:-$HOME/.local/bin/hermes}"

say()  { printf '\033[0;36m→\033[0m %s\n' "$1"; }
ok()   { printf '\033[0;32m✓\033[0m %s\n' "$1"; }
warn() { printf '\033[0;33m!\033[0m %s\n' "$1"; }

# 1. Hermes instalado -------------------------------------------------------
if ! command -v hermes >/dev/null 2>&1 && [ ! -x "$HERMES_BIN" ]; then
  warn "hermes nao encontrado. Instale antes:"
  echo "    curl -fsSL https://hermes-agent.nousresearch.com/install.sh | bash -s -- --skip-setup"
  exit 1
fi
ok "hermes encontrado"

# 2. MCP publicado ----------------------------------------------------------
say "publicando o MCP em $MCP_DIR"
dotnet publish "$REPO/src/AutoHous.Revenue.Mcp/AutoHous.Revenue.Mcp.csproj" \
  -c Release -o "$MCP_DIR" --nologo -v q >/dev/null
ok "MCP publicado"

# 3. ~/.hermes/.env ---------------------------------------------------------
mkdir -p "$HERMES_HOME"
touch "$HERMES_HOME/.env"
chmod 600 "$HERMES_HOME/.env"

API_KEY="$(grep -m1 '^API_SERVER_KEY=' "$HERMES_HOME/.env" 2>/dev/null | cut -d= -f2- || true)"

# 16 chars e o piso do proprio Hermes (has_usable_secret). 48 hex passa longe
# dele e nao exige o operador inventar segredo.
if [ "${#API_KEY}" -lt 16 ]; then
  API_KEY="$(openssl rand -hex 24)"
  {
    echo ""
    echo "# AutoHous Revenue Engine - API Server do gateway."
    echo "# A presenca desta chave e o que habilita a plataforma api_server."
    echo "API_SERVER_KEY=$API_KEY"
    echo "API_SERVER_ENABLED=true"
    echo "API_SERVER_HOST=127.0.0.1"
    echo "API_SERVER_PORT=8642"
  } >> "$HERMES_HOME/.env"
  ok "API_SERVER_KEY gerada em $HERMES_HOME/.env"
else
  ok "API_SERVER_KEY ja existia; preservada"
fi

# 4. Segredo da Revenue API -------------------------------------------------
# Fonte da verdade: o .env do projeto. Assim a API e o MCP leem a MESMA chave sem
# ninguem precisar copiar valor entre dois arquivos.
REVENUE_KEY=""
if [ -f "$REPO/.env" ]; then
  # O `tr -d` nao e zelo: o .env e editado no Windows e e gitignored, entao o
  # `* text=auto` do .gitattributes nunca o alcanca. Um \r que sobrevivesse
  # ate aqui entraria no arquivo de segredo, o MCP mandaria `Bearer <chave>\r`,
  # e o Kestrel devolveria 400 sem corpo - sem nunca chegar na autenticacao.
  REVENUE_KEY="$(grep -m1 '^REVENUE_API_KEY=' "$REPO/.env" 2>/dev/null | cut -d= -f2- | tr -d '\r\n\t "'"'"'' || true)"
fi

# 24 e o piso que RevenueApiKeys impoe; 48 hex passa longe dele.
if [ "${#REVENUE_KEY}" -lt 24 ]; then
  REVENUE_KEY="$(openssl rand -hex 24)"
  say "chave nova da Revenue API gerada"
else
  ok "REVENUE_API_KEY do .env reaproveitada"
fi

SECRET_DIR="$HERMES_HOME/secrets"
SECRET_FILE="$SECRET_DIR/revenue-api-key"
mkdir -p "$SECRET_DIR"
chmod 700 "$SECRET_DIR"
printf '%s' "$REVENUE_KEY" > "$SECRET_FILE"
chmod 600 "$SECRET_FILE"
ok "segredo da Revenue API em $SECRET_FILE (0600)"

# 5. ~/.hermes/config.yaml --------------------------------------------------
# Edicao textual, e nao round-trip de YAML: o template do Hermes tem ~1.900
# linhas de comentario que documentam cada chave, e reserializar apagaria todas.
CONFIG="$HERMES_HOME/config.yaml"
REPO="$REPO" MCP_DIR="$MCP_DIR" CONFIG="$CONFIG" SECRET_FILE="$SECRET_FILE" python3 - <<'PY'
import os, pathlib, re

repo = os.environ["REPO"]
mcp_dir = os.environ["MCP_DIR"]
secret_file = os.environ["SECRET_FILE"]
path = pathlib.Path(os.environ["CONFIG"])
text = path.read_text(encoding="utf-8")

OPEN, CLOSE = "# >>> autohous-revenue", "# <<< autohous-revenue"

block = f"""{OPEN} - gerado por scripts/hermes-setup.sh
# O filtro de ferramentas do Hermes e por SERVIDOR, nao por agente: esta
# allowlist e a unica fronteira efetiva entre o modelo e a Revenue API.
mcp_servers:
  autohous_revenue:
    command: "dotnet"
    args:
      - "{mcp_dir}/AutoHous.Revenue.Mcp.dll"
    env:
      # O MCP fala HTTP com a Revenue API. Ele nunca recebe credencial de banco.
      REVENUE_API_URL: "http://127.0.0.1:5080"
      # Caminho, e nao o segredo: o Hermes nao interpola ${{VAR}} aqui, e este
      # arquivo tem 0644. O segredo fica em 0600, no arquivo apontado.
      REVENUE_API_KEY_FILE: "{secret_file}"
    timeout: 30
    connect_timeout: 10
    enabled: true
    tools:
      include:
        - get_account_context
        - list_account_evidence
        - get_product_catalog
      exclude: []
      prompts: false
      resources: false
{CLOSE}"""

# Regenerar em vez de pular: caminho de MCP e de segredo mudam, e um bloco
# desatualizado falharia na conexao em vez de aparecer aqui.
if OPEN in text and CLOSE in text:
    text = re.sub(
        re.escape(OPEN) + r".*?" + re.escape(CLOSE),
        lambda _: block,
        text,
        count=1,
        flags=re.S,
    )
    print("  bloco autohous do config.yaml regenerado")
else:
    text = text.rstrip("\n") + "\n\n" + block + "\n"
    print("  mcp_servers.autohous_revenue adicionado")

# A skill versionada mora no repositorio. external_dirs a le de la, em vez de
# copiar para ~/.hermes/skills - copia envelhece em silencio.
ext = f"{repo}/hermes/skills"
if ext not in text:
    text, n = re.subn(
        r"^skills:\n",
        "skills:\n"
        "  # AutoHous: a skill versionada e lida do repositorio (read-only).\n"
        "  external_dirs:\n"
        f"    - {ext}\n",
        text,
        count=1,
        flags=re.M,
    )
    print("  skills.external_dirs apontado para o repo" if n
          else "  ATENCAO: chave 'skills:' nao encontrada; external_dirs NAO configurado")

# Conservador de proposito (secao 10 do guia): arvore rasa antes de arvore funda.
if "max_concurrent_children:" not in text.replace("# max_concurrent_children:", ""):
    text, n = re.subn(
        r"^delegation:\n",
        "delegation:\n"
        "  max_concurrent_children: 3        # AutoHous: comeco conservador\n"
        "  max_spawn_depth: 1                # AutoHous: arvore rasa\n",
        text,
        count=1,
        flags=re.M,
    )
    print("  delegation limitada a 3 filhos e 1 nivel" if n
          else "  ATENCAO: chave 'delegation:' nao encontrada")

path.write_text(text, encoding="utf-8")
PY
ok "config.yaml ajustado"

# 6. .env do projeto --------------------------------------------------------
ENV_FILE="$REPO/.env"
if [ -f "$ENV_FILE" ]; then
  ENV_FILE="$ENV_FILE" HERMES_KEY="$API_KEY" REVENUE_KEY="$REVENUE_KEY" \
    python3 "$REPO/scripts/lib/mirror-env.py"
  ok "HERMES_API_SERVER_KEY e REVENUE_API_KEY espelhadas em .env"
else
  warn ".env do projeto nao existe; copie de .env.example e cole:"
  echo "    HERMES_API_SERVER_KEY=$API_KEY"
  echo "    REVENUE_API_KEY=$REVENUE_KEY"
fi

# 6. O que falta, e so voce pode fazer --------------------------------------
cat <<EOF

$(ok "setup local pronto")

Falta a parte interativa — credencial de modelo:

    hermes setup --portal      # login no Nous Portal
    hermes model               # escolher o modelo
    hermes doctor              # diagnostico

Depois, para subir e conferir:

    hermes gateway &
    curl -s http://127.0.0.1:8642/health
    curl -s http://127.0.0.1:8642/v1/models -H "Authorization: Bearer \$API_SERVER_KEY"

E so entao virar a chave do Revenue Engine (AGENT_RUNTIME=hermes no .env).
EOF
