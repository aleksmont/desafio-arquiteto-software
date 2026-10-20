# Ledger — desafio de arquitetura

API em C#/.NET 10 e PostgreSQL para registrar créditos e débitos e consultar saldo atual ou histórico. Implementação inicial de uma única aplicação, com diário financeiro e saldo materializado atualizados na mesma transação.

## Entenda a solução

O problema central é preservar a relação entre o que aconteceu na conta e o saldo apresentado ao cliente, inclusive quando duas requisições chegam juntas ou uma resposta se perde. A implementação prioriza esse núcleo antes de adicionar serviços ou infraestrutura distribuída.

O **diário de movimentações** (`movements`) guarda cada crédito e débito. O **saldo atual** (`accounts.balance`) evita somar todo o histórico a cada consulta. Ambos são gravados na mesma transação: ou os dois mudam, ou nenhum muda. Uma chave enviada pelo consumidor identifica cada operação e permite repeti-la com segurança.

Exemplo: após um crédito de R$ 100,50 e um débito de R$ 30,25, o saldo atual é R$ 70,25. A consulta histórica entre os dois registros retorna R$ 100,50. Se a resposta do crédito se perder e o consumidor repetir sua chave, o saldo continua correto porque o crédito original é recuperado.

### Guia de leitura

| Documento | O que explica |
| --- | --- |
| [Requisitos e evidências](docs/requirements.md) | O que o enunciado pede, onde foi atendido e o que ainda precisa ser validado |
| [Arquitetura e decisões](docs/architecture.md) | Responsabilidade de cada componente, fluxo de uma operação e motivos das escolhas |
| [Melhorias e justificativas](docs/improvements.md) | Mudanças de confiabilidade, custos e evidências da revisão |
| [Contrato da API](docs/api.md) | Endpoints, exemplos de resposta e comportamento de repetição e erro |

Esta entrega usa uma aplicação com responsabilidades separadas em arquivos, sem impor várias camadas ou projetos a um domínio ainda pequeno. `Store` concentra a transação e o SQL; essa escolha reduz indireção, mas pode exigir separação de regras e persistência quando o domínio crescer.

## Premissas

- Uma conta por cliente, identificada por UUID, moeda BRL, precisão de centavos. A API recebe o identificador da conta; não há cadastro de clientes nem vínculo de titularidade persistido.
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

A API fica em `http://localhost:8080`. O banco persiste em volume; o schema e a proteção do diário são aplicados no primeiro início de um volume vazio. Alterações futuras exigem migrações versionadas; reiniciar um volume existente não reaplica o script.

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

Use `type: debit` para debitar. Reenvie o mesmo corpo e chave após timeout: o primeiro registro retorna 201 e repetições retornam 200 com o mesmo movimento. Chave reutilizada com valor ou tipo diferente retorna 409. A chave é única por conta e deve identificar uma intenção de negócio, sem dados pessoais. Valores e tipos inválidos retornam 400; credencial ausente/inválida, 401; falha transitória de persistência, 503; erro SQL permanente, 500; capacidade simultânea excedida, 429. Datas no parâmetro `at` devem incluir `Z` ou offset (codifique `+` como `%2B`).

`GET /health/live` verifica o processo sem autenticação. `GET /health/ready` exige chave e verifica acesso à tabela de contas. A API não cria tabelas ao iniciar.

## Desenvolvimento e testes

Pré-requisitos: SDK .NET 10, PostgreSQL 18 (ou o serviço `db` do Compose), cliente `psql`. Crie um banco exclusivo para testes para não escrever dados de teste no banco da aplicação.

```sh
docker compose up -d db
docker compose exec db createdb -U ledger ledger_test
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger_test < src/Ledger.Api/schema.sql
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger_test < database/migrations/002-protect-journal.sql
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

## Atualizar uma instalação existente

Volumes existentes não executam novamente os scripts de inicialização. Antes de subir a API atualizada, aplique a migração com o papel administrador/migrador (faça backup em ambientes com dados relevantes):

```sh
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger < database/migrations/002-protect-journal.sql
```

O arquivo é transacional e reaplicável. Ele bloqueia UPDATE, DELETE e TRUNCATE do diário; um administrador ainda pode remover essa proteção. Não substitui papéis de acesso restritos.

## Proteção de capacidade e smoke test

`MaxConcurrentRequests` configura o limite de operações de contas em execução por instância (padrão 32, sem fila). Requisições excedentes recebem 429 com `Retry-After: 1`. Esse valor é um ponto de partida a medir, não promessa de capacidade. Health checks ficam fora desse limite. Operações de movimentação usam espera de lock de até 3 segundos, timeout padrão de comando de 10 segundos e de conexão de 5 segundos; os dois últimos podem ser configurados na conexão.

Com a API em execução, `API_KEY=... bash scripts/smoke.sh` verifica criação de conta, crédito, repetição e saldo por HTTP. Requer curl e Python 3; grava uma conta exclusiva no banco de destino. O CI também inicia o Compose em um job próprio e executa esse fluxo, encerrando os contêineres ao terminar.
