# Decisões de arquitetura e operação

## Uma aplicação e um banco relacional

ASP.NET Core mantém a superfície pequena e usa I/O assíncrono. PostgreSQL é a fonte de verdade. Uma transação engloba o diário e o saldo materializado, evitando atualização dupla entre serviços. Microserviços, mensageria e cache de saldo foram adiados: aumentariam a superfície de falhas sem requisito que justifique consistência eventual. .NET 10 é LTS. O acesso explícito via Npgsql torna o SQL e o escopo transacional revisáveis.

Referências: [suporte .NET](https://dotnet.microsoft.com/en-us/platform/support/policy), [Npgsql](https://www.npgsql.org/doc/basic-usage.html).

## Consistência e concorrência

1. Validar entrada antes da transação.
2. Bloquear a linha da conta com `FOR UPDATE`.
3. Procurar chave idempotente e comparar o conteúdo original.
4. Verificar saldo e limites, inserir movimento e atualizar saldo.
5. Confirmar ambos atomicamente antes de responder.

READ COMMITTED com bloqueio por conta evita perdas de atualização e gasto concorrente do mesmo saldo. Contas diferentes não compartilham esse bloqueio. A restrição única por conta/chave reforça a idempotência no banco. Operações abortadas liberam o bloqueio por descarte da transação. A repetição de um movimento já confirmado devolve o registro original antes de avaliar o saldo atual.

Se a confirmação ocorreu e a resposta se perdeu, a mesma chave recupera o resultado. Não se promete entrega exatamente uma vez na rede. Não há retry automático de escrita com resultado ambíguo. O cliente repete com a mesma chave e backoff com jitter, limitado por prazo e número de tentativas.

`numeric` e `decimal` evitam arredondamento binário. O banco também valida valores positivos, tipo e saldo não negativo. A consulta atual usa a linha da conta; a histórica soma o diário usando índice por conta/data. Uma instrução SQL garante uma fotografia consistente. O horário vem do banco após adquirir o bloqueio. NTP e monitoramento de relógio continuam necessários; o horário é de registro, não de commit nem de validade econômica externa.

## Falhas e alta demanda

Em indisponibilidade do banco, retornar 503 sem aceitar lançamentos apenas em memória. A disponibilidade de escrita depende do primário: prioriza-se consistência financeira. O pool limita conexões, mas não substitui controle de admissão. Configurar timeouts, limites de requisição e rate limiting no gateway antes de produção. Escalar réplicas stateless da API; a conta muito concorrida continua sendo um ponto serial por definição da regra de saldo.

Consultas históricas crescem com o diário. Medir volume e latência antes de introduzir snapshots, particionamento ou projeções. Projeções assíncronas devem expor seu ponto de atualização; não responder como se fossem saldo autoritativo. Se houver eventos, usar outbox transacional e consumidores idempotentes.

## Segurança

A API exige chave de serviço e falha ao iniciar sem configuração. A comparação usa hashes de tamanho fixo em tempo constante. Não há logs de corpo, credencial ou dados financeiros. SQL parametrizado evita interpolação de entrada. O exemplo mantém portas do host em loopback e executa a API como usuário sem privilégios no contêiner.

Antes de produção: TLS entre cliente/gateway/API/banco, autenticação OIDC/mTLS por serviço, autorização por conta/escopo, rotação de segredos via cofre, criptografia de discos/backups, auditoria com acesso restrito, política de retenção/LGPD e gestão de incidentes. UUID não é autorização. O usuário PostgreSQL do Compose é de desenvolvimento; criar papel de runtime sem DDL e sem UPDATE/DELETE no diário, e papel separado para migrações. O diário atual é imutável pela API, não protegido contra um administrador do banco.

## Operação e próximos passos

- Migrar schema por etapa de implantação, com compatibilidade entre versões e papel dedicado.
- Alta disponibilidade PostgreSQL, política de failover e durabilidade de réplica explicitamente escolhida. Não promover réplica atrasada sem analisar perda de dados.
- Backup contínuo/PITR e ensaios de restauração; definir RPO/RTO com negócio.
- Monitorar p95/p99, erros, timeouts, saturação do pool, espera de locks e atraso de réplica. Acrescentar tracing/OpenTelemetry sem payload sensível.
- Reconciliar periodicamente saldo materializado com soma do diário em snapshot consistente; divergência exige investigação, não reparo silencioso.
- Executar ensaio de carga com contas distribuídas e conta concentradora; testar queda de conexão no commit, reinício do primário e disco cheio.
- Implementar partidas dobradas e contas contábeis caso o sistema assuma papel de razão contábil; esta API registra movimentos de clientes, não substitui contabilidade completa.

Não há benchmark ou evidência de capacidade para tráfego de produção nesta entrega. Os testes atuais verificam invariantes com PostgreSQL real; failover, restauração e carga precisam de ambiente próprio.
