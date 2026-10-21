# Desenvolvimento e testes manuais (opcional)

Para avaliar o projeto, prefira `bash scripts/test.sh`, descrito no [README](../README.md). Este guia é para quem quer depurar com SDK e IDE ou controlar um banco próprio.

## Como funciona o comando de testes

`scripts/test.sh` verifica Docker/Compose, cria um nome de projeto exclusivo e executa `compose.test.yaml`. A imagem `Dockerfile.test` instala as dependências e compila com warnings como erros. O PostgreSQL temporário recebe schema e migrações antes da execução da suíte.

O banco não publica portas no host, usa armazenamento temporário e é independente do Compose da aplicação. O script devolve o código de saída dos testes e remove contêineres e rede mesmo quando há falha. A limpeza não remove imagens em cache nem dados da aplicação. Se Docker deixar de responder durante a execução, o script informa o comando para limpar o ambiente que ficou pendente.

A máquina precisa conseguir baixar imagens e pacotes na primeira execução. O teste de permissões usa credencial administrativa apenas nesse banco temporário. Não há seleção de banco de produção nesse caminho.

## Executar testes com SDK

Pré-requisitos: SDK .NET 10, Docker com Compose. Copie `.env.example` para `.env` antes de usar o Compose da aplicação. O comando `createdb` abaixo precisa ser executado apenas uma vez; se o banco já existir, continue para a aplicação dos scripts.

```sh
docker compose up -d db
docker compose exec db createdb -U ledger ledger_test
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger_test < src/Ledger.Api/schema.sql
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger_test < database/migrations/002-protect-journal.sql
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger_test < database/migrations/003-runtime-permissions.sql
export TEST_DATABASE='Host=localhost;Database=ledger_test;Username=ledger;Password=local-only'
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

A suíte falha se `TEST_DATABASE` não estiver configurada. Use um banco dedicado: os testes criam contas, logins e uma constraint temporária para simular falha. Não apontar para o banco da aplicação ou de produção.

## Executar a API com SDK

Após inicializar o banco pelo Compose, com a senha padrão do `.env.example`:

```sh
export ConnectionStrings__Ledger='Host=localhost;Database=ledger;Username=ledger_app;Password=local-runtime-password-change-me;Timeout=5;Command Timeout=10'
export ApiKey=local-development-key-change-me-123456
dotnet run --project src/Ledger.Api --urls http://localhost:8080
```

Se a senha tiver sido alterada, ajustar a configuração. Se a API já estiver em contêiner na porta 8080, interrompê-la antes ou escolher outra porta.

## Smoke test HTTP real

Com a API em execução:

```sh
API_KEY=local-development-key-change-me-123456 bash scripts/smoke.sh
```

Requer Bash, curl e Python 3. Cria uma conta exclusiva e verifica crédito, repetição idempotente, saldo e recusa de corpo acima de 16 KiB. Pode usar `BASE_URL` para outra porta. Esse script escreve no banco da API indicada; o comando principal `scripts/test.sh` não depende dele e usa seu próprio banco isolado.
