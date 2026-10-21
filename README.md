# Ledger — desafio de arquitetura

API em C#/.NET 10 e PostgreSQL para registrar créditos e débitos e consultar saldo atual ou histórico. Implementação inicial de uma única aplicação, com diário financeiro e saldo materializado atualizados na mesma transação.

## Testar em um comando

Pré-requisito: Docker com Compose em execução (por exemplo, Docker Desktop). No Windows, execute o comando no Git Bash ou WSL.

Na pasta do repositório:

```sh
bash scripts/test.sh
```

O comando prepara PostgreSQL e .NET em contêineres, aplica o schema e as migrações, compila sem warnings e executa todos os testes. Não precisa de `.env`, SDK, cliente PostgreSQL ou banco instalado. O banco é temporário, sem portas expostas, e não usa os dados da aplicação. Ao terminar, o ambiente é removido automaticamente; imagens ficam em cache para acelerar a próxima execução.

O resultado esperado é `Passed!` com os 36 testes aprovados. Em caso de falha, os detalhes aparecem no terminal e o comando retorna erro. A primeira execução demora mais por causa dos downloads. Execute novamente o mesmo comando para repetir os testes com um banco novo. O GitHub Actions usa esse mesmo caminho.

O [passo a passo do script](docs/development.md#como-funciona-o-comando-de-testes) explica o que cada etapa faz e por que existe.

Se aparecer que Docker não está disponível, abra o Docker Desktop e aguarde a inicialização. Se o download das imagens ou pacotes falhar, verifique a conexão e execute novamente.

## Rodar a API localmente

```sh
cp .env.example .env
docker compose up --build -d
```

A API fica em `http://localhost:8080`. As credenciais do exemplo são exclusivas de desenvolvimento. Os dados da aplicação persistem em volume e ficam separados dos testes. Para parar sem apagar os dados:

```sh
docker compose down
```

Para instalações que já têm volume antigo, siga [atualização do banco](docs/operations.md#atualizar-volumes-existentes). Não é necessário aplicar SQL manualmente em uma instalação nova.

## Premissas

- Uma conta por cliente, identificada por UUID, moeda BRL, precisão de centavos. A API recebe o identificador da conta; o vínculo persistido é com o serviço criador; não há cadastro de titulares finais.
- Sem cheque especial: débitos que excedam o saldo retornam 409.
- O histórico representa o instante de registro (`recordedAt`, UTC), inclusive no limite `at`. Não há lançamentos retroativos ou futuros. A posição considera os registros já confirmados visíveis na consulta; não é uma reconstrução do instante exato de commit.
- Contas novas começam em zero. Conta inexistente retorna 404.
- Correções financeiras devem ser novos lançamentos compensatórios; não há edição ou remoção de movimentos.
- O consumidor é um serviço interno confiável. A credencial identifica um serviço e só permite acesso às contas que ele criou; não há autenticação do cliente final.

## Experimentar a API

Com a aplicação iniciada e as credenciais padrão do `.env.example`. O exemplo usa Bash, curl e Python 3 e retorna saldo atual e histórico de R$ 100,50:

```sh
export API_KEY=local-development-key-change-me-123456
export ACCOUNT="$(python3 -c 'import uuid; print(uuid.uuid4())')"
curl -i -X PUT "http://localhost:8080/accounts/$ACCOUNT" -H "X-Api-Key: $API_KEY"
MOVEMENT=$(curl -fsS -X POST "http://localhost:8080/accounts/$ACCOUNT/movements" \
  -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' \
  -d '{"key":"deposit-001","amount":100.50,"type":"credit"}')
printf '%s\n' "$MOVEMENT"
curl "http://localhost:8080/accounts/$ACCOUNT/balance" -H "X-Api-Key: $API_KEY"
RECORDED_AT=$(printf '%s' "$MOVEMENT" | python3 -c 'import json,sys; print(json.load(sys.stdin)["recordedAt"])')
curl -G "http://localhost:8080/accounts/$ACCOUNT/balance" \
  --data-urlencode "at=$RECORDED_AT" -H "X-Api-Key: $API_KEY"
```

Use `type: debit` para debitar. Reenvie o mesmo corpo e chave após timeout: o primeiro registro retorna 201 e repetições retornam 200 com o mesmo movimento. Chave reutilizada com valor ou tipo diferente retorna 409. A chave é única por conta e deve identificar uma intenção de negócio, sem dados pessoais. Valores e tipos inválidos retornam 400; credencial ausente/inválida, 401; falha transitória de persistência, 503; erro SQL permanente, 500; capacidade simultânea excedida, 429. Datas no parâmetro `at` devem incluir `Z` ou offset (codifique `+` como `%2B`).

`GET /health/live` verifica o processo sem autenticação. `GET /health/ready` exige chave e verifica acesso à tabela de contas. A API não cria tabelas ao iniciar.

## Ensaio de carga (opcional)

```sh
bash scripts/load.sh
```

Prepara API e banco descartáveis, mede três cenários de 6000 solicitações e valida saldos e repetições. Não precisa de `.env` nem usa dados da aplicação. O relatório fica em `artifacts/load/load.json`. Consulte [resultados e limitações](docs/validation-results.md) para interpretar as medições. O ensaio curto não certifica capacidade de produção.

## Desenvolver usando o SDK (opcional)

Para quem precisa depurar ou executar testes fora dos contêineres, os passos detalhados estão no [guia de desenvolvimento](docs/development.md). Para apenas avaliar a solução, use o comando de testes acima.

## Escopo da entrega

Inclui API, persistência transacional, testes automatizados, execução em contêiner, usuário de runtime restrito, reconciliação somente de leitura e integração contínua. É uma demonstração do núcleo de consistência; a preparação para operação bancária exige identidade por serviço, autorização, controles operacionais e validação de carga descritos na documentação. As credenciais do Compose servem somente ao desenvolvimento local.

## Entenda a solução

O problema central é preservar a relação entre o que aconteceu na conta e o saldo apresentado ao cliente, inclusive quando duas requisições chegam juntas ou uma resposta se perde. A implementação prioriza esse núcleo antes de adicionar serviços ou infraestrutura distribuída.

O **diário de movimentações** (`movements`) guarda cada crédito e débito. O **saldo atual** (`accounts.balance`) evita somar todo o histórico a cada consulta. Ambos são gravados na mesma transação: ou os dois mudam, ou nenhum muda. Uma chave enviada pelo consumidor identifica cada operação e permite repeti-la com segurança.

Exemplo: após um crédito de R$ 100,50 e um débito de R$ 30,25, o saldo atual é R$ 70,25. A consulta histórica entre os dois registros retorna R$ 100,50. Se a resposta do crédito se perder e o consumidor repetir sua chave, o saldo continua correto porque o crédito original é recuperado.

### Guia de leitura

| Documento | O que explica |
| --- | --- |
| [Resultados da revisão](docs/validation-results.md) | Correções, ensaio de carga, falhas no COMMIT e limites das evidências |
| [Visão do Arquiteto](docs/visao-do-arquiteto.md) | Considerações, motivações, alternativas e critérios para evolução da solução |
| [Requisitos e evidências](docs/requirements.md) | O que o enunciado pede, onde foi atendido e o que ainda precisa ser validado |
| [Arquitetura e decisões](docs/architecture.md) | Responsabilidade de cada componente, fluxo de uma operação e motivos das escolhas |
| [Melhorias e justificativas](docs/improvements.md) | Mudanças de confiabilidade, custos e evidências da revisão |
| [Operação e permissões](docs/operations.md) | Usuário restrito, migrações, reconciliação e cancelamento |
| [Contrato da API](docs/api.md) | Endpoints, exemplos de resposta e comportamento de repetição e erro |

Esta entrega usa uma aplicação com responsabilidades separadas em arquivos, sem impor várias camadas ou projetos a um domínio ainda pequeno. `Store` concentra a transação e o SQL; essa escolha reduz indireção, mas pode exigir separação de regras e persistência quando o domínio crescer.

## Operação avançada

Consulte [operação e permissões](docs/operations.md) para atualizar volumes existentes, executar a reconciliação e entender limites de capacidade. O [guia de desenvolvimento](docs/development.md) descreve a execução manual e o teste HTTP completo.
