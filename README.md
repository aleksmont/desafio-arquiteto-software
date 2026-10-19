# Ledger — desafio de arquitetura

API em C#/.NET 10 e PostgreSQL para registrar créditos e débitos e consultar saldo atual ou histórico. Implementação inicial de uma única aplicação, com diário financeiro e saldo materializado atualizados na mesma transação.

## Premissas

- Uma conta por cliente, identificada por UUID, moeda BRL, precisão de centavos.
- Sem cheque especial: débitos que excedam o saldo retornam 409.
- O histórico representa o instante de registro (`recordedAt`, UTC), inclusive no limite `at`. Não há lançamentos retroativos ou futuros. A posição considera os registros já confirmados visíveis na consulta; não é uma reconstrução do instante exato de commit.
- Contas novas começam em zero. Conta inexistente retorna 404.
- Correções financeiras devem ser novos lançamentos compensatórios; não há edição ou remoção de movimentos.
- O consumidor é um serviço interno confiável. A chave compartilhada desta demonstração não implementa autorização por cliente.

## Rodar com Docker

Pré-requisitos: Docker com Compose. Não é necessário instalar o SDK no host.

```sh
cp .env.example .env
docker compose up --build -d
```

A API fica em `http://localhost:8080`. O banco persiste em volume; o schema é aplicado no primeiro início de um volume vazio. Alterações futuras exigem migrações versionadas; reiniciar um volume existente não reaplica o script.

```sh
export API_KEY=local-development-key-change-me-123456
export ACCOUNT=11111111-1111-1111-1111-111111111111
curl -i -X PUT "http://localhost:8080/accounts/$ACCOUNT" -H "X-Api-Key: $API_KEY"
curl -i -X POST "http://localhost:8080/accounts/$ACCOUNT/movements" \
  -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' \
  -d '{"key":"deposit-001","amount":100.50,"type":"credit"}'
curl "http://localhost:8080/accounts/$ACCOUNT/balance" -H "X-Api-Key: $API_KEY"
curl "http://localhost:8080/accounts/$ACCOUNT/balance?at=2026-10-19T23:00:00Z" -H "X-Api-Key: $API_KEY"
```

Use `type: debit` para debitar. Reenvie o mesmo corpo e chave após timeout: o primeiro registro retorna 201 e repetições retornam 200 com o mesmo movimento. Chave reutilizada com valor ou tipo diferente retorna 409. A chave é única por conta e deve identificar uma intenção de negócio, sem dados pessoais. Valores e tipos inválidos retornam 400; credencial ausente/inválida, 401; falha de persistência, 503. Datas no parâmetro `at` devem incluir `Z` ou offset (codifique `+` como `%2B`).

`GET /health/live` verifica o processo sem autenticação. `GET /health/ready` exige chave e verifica acesso à tabela de contas. A API não cria tabelas ao iniciar.

## Desenvolvimento e testes

Pré-requisitos: SDK .NET 10, PostgreSQL 18 (ou o serviço `db` do Compose), cliente `psql`. Crie um banco exclusivo para testes para não escrever dados de teste no banco da aplicação.

```sh
docker compose up -d db
docker compose exec db createdb -U ledger ledger_test
docker compose exec -T db psql -U ledger -d ledger_test < src/Ledger.Api/schema.sql
export TEST_DATABASE='Host=localhost;Database=ledger_test;Username=ledger;Password=local-only'
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

Os testes de integração exigem `TEST_DATABASE` e falham se a configuração faltar. Usam UUIDs exclusivos, não removem dados e verificam duplicatas concorrentes, débitos concorrentes, saldo histórico, conflito de chave e rollback de débito recusado. Recrie periodicamente o banco de testes.

Para executar a API com o SDK:

```sh
export ConnectionStrings__Ledger='Host=localhost;Database=ledger;Username=ledger;Password=local-only;Timeout=5;Command Timeout=10'
export ApiKey=local-development-key-change-me-123456
dotnet run --project src/Ledger.Api --urls http://localhost:8080
```

`dotnet build` trata warnings como erros. O workflow do GitHub aplica o schema a um PostgreSQL real e executa build e testes. Consulte [decisões e operação](docs/architecture.md).

## Escopo da entrega

Inclui API, persistência transacional, testes automatizados, execução em contêiner e integração contínua. É uma demonstração do núcleo de consistência; a preparação para operação bancária exige identidade por serviço, autorização, controles operacionais e validação de carga descritos na documentação. As credenciais do Compose servem somente ao desenvolvimento local.
