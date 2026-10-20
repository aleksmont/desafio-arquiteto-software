# Permissões, reconciliação e atualização

## Três responsabilidades no banco

O administrador/migrador cria estruturas e aplica SQL versionado. O papel `ledger_runtime` não tem login: agrupa apenas permissões necessárias. O login `ledger_app` herda essas permissões e é usado pela API no Compose.

A migração 003 concede leitura de contas e movimentos, inserção de identificador de conta, atualização de saldo e inserção no diário. Não concede DELETE, TRUNCATE, alteração de schema, edição de movimentos ou mudança do identificador da conta. O bloqueio `FOR UPDATE` funciona com a permissão de atualização de saldo. A aplicação ainda consegue atualizar saldo diretamente: a correspondência com o diário é preservada pelo Store e verificada pela reconciliação, não por uma garantia completa do papel SQL.

O script de provisionamento 004 recebe `LEDGER_APP_PASSWORD` por ambiente. O arquivo `.env.example` contém apenas valores de desenvolvimento; não use essas credenciais em produção. O administrador do Compose continua sendo o dono do banco, mas sua credencial não é mais utilizada pela API em contêiner.

Em produção, use um cofre, rotação e papéis administrados pela infraestrutura. Não execute testes com privilégios de administração no banco da aplicação. O teste de permissões cria e remove um login temporário exclusivamente no banco de testes.

## Atualizar volumes existentes

Adicionar `LEDGER_APP_PASSWORD` ao `.env` antes de iniciar. A API deverá usar a nova credencial. Os scripts de inicialização não são reaplicados automaticamente a um volume existente.

```sh
docker compose up -d db
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger < database/migrations/002-protect-journal.sql
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger < database/migrations/003-runtime-permissions.sql
docker compose exec -T db bash /docker-entrypoint-initdb.d/004-configure-runtime.sh
docker compose up --build -d api
```

Faça backup antes de alterações em ambientes com dados relevantes. A migração 003 revoga CREATE de PUBLIC no schema public: em um banco compartilhado, avaliar o impacto para outros consumidores antes de aplicar. Esta solução assume banco dedicado. 002 e 003 são transacionais e reaplicáveis; 004 cria o login se necessário e atualiza sua senha, permitindo rotação manual. Ainda não existe histórico de checksums e execução de migrações.

## Reconciliação

O saldo atual é uma projeção do diário. A consulta `database/operations/reconcile.sql` compara ambos dentro de uma transação somente de leitura, com fotografia consistente e timeout de 60 segundos. Escritas concorrentes não causam diferença apenas por leitura em instantes distintos.

Requisitos: `psql` e Bash, acesso de leitura às duas tabelas. Use as variáveis padrão do cliente PostgreSQL; prefira arquivo de senha com permissões restritas ou secret manager em vez de senha no histórico do shell.

```sh
export PGHOST=localhost PGPORT=5432 PGDATABASE=ledger PGUSER=ledger_app
bash scripts/reconcile.sh
```

Saída sem diferença: mensagem de sucesso e código 0. Com diferença: imprime `account_id,stored_balance,journal_balance,difference` e sai com código 10. Falha de conexão ou SQL mantém o código de falha do psql. A saída contém dados financeiros e deve ter acesso e retenção controlados.

O script não agenda execuções e não modifica saldo ou diário. Pode ser integrado a um job operacional, com alerta quando o código for diferente de zero. Em grande volume, medir custo antes de definir frequência: uma reconciliação global lê o diário e pode consumir recursos do primário.

Ao encontrar diferença:

1. Preservar evidência e identificar quais contas foram afetadas; restringir acesso ao relatório.
2. Investigar escrita externa, mudança de código, permissões e restaurações recentes.
3. Definir a correção com responsáveis de negócio e operação; registrar a decisão e sua trilha de auditoria.
4. Reexecutar a reconciliação e confirmar a consistência antes de encerrar o incidente.

Não recalcular saldos automaticamente nem apagar movimentos para ocultar uma divergência.

## Cancelamento e repetição

O teste cancela uma operação enquanto ela espera o bloqueio da conta e verifica que não houve lançamento nem consumo de chave. Isso exercita o token de cancelamento propagado ao Npgsql. Se o consumidor desistir durante ou depois do commit, o resultado pode continuar incerto; repetir a mesma chave continua sendo necessário. Não se promete que todo cancelamento impeça uma movimentação já confirmada.

## Entrada HTTP

Campos `key`, `amount` e `type` são obrigatórios; propriedades desconhecidas são rejeitadas. Isso evita aceitar um nome digitado incorretamente e ignorar informação que o consumidor julgava relevante. O servidor Kestrel limita o corpo a 16 KiB, suficiente para o contrato pequeno; corpos excedentes retornam 413. O teste de contêiner usa HTTP real para verificar esse limite, que o servidor de testes em memória não implementa da mesma forma.
