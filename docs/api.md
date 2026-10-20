# Contrato e exemplos da API

Base local: `http://localhost:8080`. Exceto `/health/live`, os endpoints exigem `X-Api-Key`. Exemplos completos de execução estão no [README](../README.md). Valores monetários são números JSON com até duas casas decimais; integrações devem usar representação decimal, evitando cálculos com ponto flutuante binário.

## Endpoints

| Método e rota | Finalidade | Resposta de sucesso |
| --- | --- | --- |
| `PUT /accounts/{id}` | Criar conta com UUID definido pelo consumidor | 204, inclusive se já existir; não redefine o saldo |
| `POST /accounts/{id}/movements` | Registrar crédito ou débito | 201 para novo registro; 200 para repetição idêntica |
| `GET /accounts/{id}/balance` | Obter saldo atual | 200 com saldo |
| `GET /accounts/{id}/balance?at=...` | Somar registros até um instante, inclusive | 200 com saldo histórico |
| `GET /health/live` | Verificar se o processo responde | 200; não consulta o banco |
| `GET /health/ready` | Verificar acesso à tabela de contas | 200; depende do banco e da autenticação |

`id` deve ser UUID; conta com UUID vazio é rejeitada na criação. Rotas com identificador malformado não correspondem ao endpoint. `PUT` é um recurso auxiliar para preparar a conta sem depender de outro sistema de cadastro.

## Registrar uma movimentação

```json
{"key":"deposit-001","amount":100.50,"type":"credit"}
```

Os três campos são obrigatórios; campos desconhecidos são rejeitados com 400. Corpos acima de 16 KiB são rejeitados pelo Kestrel com 413.

- `key`: de 1 a 100 caracteres, não pode conter apenas espaços. A comparação é exata; não há remoção de espaços ou normalização de caixa. Não use dados pessoais.
- `amount`: maior que zero, até `9999999999999999.99`, com precisão de centavos.
- `type`: exatamente `credit` ou `debit`.

Resposta ilustrativa (IDs e horário são gerados pelo sistema):

```json
{
  "id": "22222222-2222-2222-2222-222222222222",
  "accountId": "11111111-1111-1111-1111-111111111111",
  "key": "deposit-001",
  "amount": 100.50,
  "type": "credit",
  "recordedAt": "2026-10-20T12:00:00+00:00"
}
```

A chave é única dentro da conta. Mesma chave, valor e tipo retornam o mesmo movimento. Mesma chave com valor ou tipo diferente gera 409. Um débito recusado não consome a chave: após receber fundos, uma nova tentativa pode ser aceita. Uma repetição já confirmada é devolvida mesmo se o saldo tiver mudado depois.

Após timeout, o consumidor não sabe se houve commit. Deve repetir a mesma intenção com a mesma chave, respeitando prazo e backoff; gerar outra chave pode duplicar o lançamento. Uma nova intenção de negócio exige uma nova chave.

## Consultar o saldo

```json
{
  "accountId": "11111111-1111-1111-1111-111111111111",
  "amount": 100.50,
  "at": null,
  "currency": "BRL"
}
```

Sem `at`, a resposta usa o saldo materializado atual. Com `at`, o campo devolve o instante solicitado e o valor soma créditos menos débitos com `recorded_at <= at`. Use ISO 8601 com `Z` ou offset; em URLs, codifique `+` como `%2B`. Entradas sem offset, vazias ou fora do formato são rejeitadas com 400; o instante é normalizado para UTC.

Uma data futura inclui apenas movimentos já confirmados e visíveis na consulta; não prevê saldo futuro. O banco usa a fotografia da instrução SQL. Como `recordedAt` é anterior ao commit, uma movimentação ainda em andamento pode aparecer em consultas posteriores para o mesmo `at`, depois de confirmada. Isso impede interpretar a resposta como auditoria exata do que estava visível naquele instante.

## Erros

| Código | Situação | Ação do consumidor |
| --- | --- | --- |
| 400 | Valor, tipo ou chave inválidos; falha de vinculação de parâmetros | Corrigir requisição |
| 413 | Corpo excede 16 KiB no servidor HTTP | Reduzir requisição |
| 401 | Chave de acesso ausente ou inválida | Corrigir credencial; resposta em Problem Details |
| 404 | Conta não encontrada, ou rota inexistente | Conferir identificador e rota |
| 409 | Saldo insuficiente, chave conflitante ou limite de saldo excedido | Tratar conflito de negócio; não repetir cegamente |
| 429 | Limite de operações simultâneas atingido | Respeitar Retry-After e repetir a mesma chave |
| 503 | Falha transitória, timeout ou espera de lock excedida | Respeitar Retry-After e repetir a mesma chave com política limitada |
| 500 | Erro SQL permanente | Investigar configuração/schema; não repetir automaticamente |

Erros de negócio usam Problem Details, com `status` e `title`. Erros de autenticação e persistência também usam Problem Details com `traceId`, sem detalhes SQL. A classificação usa `NpgsqlException.IsTransient`, timeouts e SQLSTATEs de lock/cancelamento. Cancelamento pelo cliente é tratado separadamente (499 interno, sem tentar escrever corpo em conexão encerrada). Outros erros passam pelo tratamento global.
