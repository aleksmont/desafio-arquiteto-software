#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
  echo 'Instale Docker com Compose (por exemplo, Docker Desktop) para executar os testes.' >&2
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  echo 'Docker não está disponível. Inicie o Docker e execute novamente.' >&2
  exit 1
fi
# Cada execução tem seu próprio banco e rede, sem tocar no Compose da aplicação.
project="ledger-tests-$$-$RANDOM"
cleanup() {
  status=$?
  trap - EXIT
  if ! docker compose -p "$project" -f "$root/compose.test.yaml" down --volumes --remove-orphans; then
    echo "Não foi possível limpar o ambiente. Execute: docker compose -p $project -f $root/compose.test.yaml down --volumes --remove-orphans" >&2
    if [ "$status" = 0 ]; then status=1; fi
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
echo 'Preparando ambiente temporário e executando os testes...'
docker compose -p "$project" -f "$root/compose.test.yaml" up --build --abort-on-container-exit --exit-code-from tests
