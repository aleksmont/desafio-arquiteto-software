# Validação após a revisão do desafio

## Escopo e correções

Esta rodada responde aos pontos da revisão do commit `dd15c77`: sincronização do teste de saturação, exemplo histórico, isolamento de contas, evidência de carga e perda de conexão na confirmação. As alterações mantêm uma única fronteira transacional para diário e saldo.

| Ponto da revisão | Tratamento |
| --- | --- |
| Sondagem do teste podia observar outra sessão | A fábrica HTTP recebe `ApplicationName` exclusivo; a sondagem filtra por essa identidade, além da consulta e da espera de lock |
| Data fixa no README retornava zero após um crédito novo | O exemplo usa `recordedAt` da resposta e codifica o parâmetro; o smoke test verifica saldo histórico de R$ 10,25 no instante do próprio crédito |
| Uma credencial podia operar qualquer conta | Cada credencial identifica um serviço e cada conta tem `owner_id`; criação idempotente, saldo atual/histórico e movimentações verificam a propriedade |
| Semântica histórica ambígua | Mantida explicitamente como data de registro de movimentos confirmados visíveis à consulta; não foi convertida em histórico por instante de commit |
| Não havia evidência de carga | Acrescentado ensaio HTTP com três perfis, percentis, distribuição de status e validação de saldo após recuperação |
| Não havia perda de rede no COMMIT | Acrescentado proxy TCP de teste que corta a conexão antes de encaminhar COMMIT ou depois da confirmação do PostgreSQL, sem entregá-la ao driver |

## Como reproduzir

Pré-requisito: Docker com Compose em execução. Não é necessário configurar `.env` para os comandos abaixo.

```sh
bash scripts/test.sh
bash scripts/load.sh
```

O primeiro executa os 36 testes automatizados, incluindo os dois cenários de falha durante confirmação e isolamento entre serviços. O segundo constrói API e banco em ambiente descartável separado e salva `artifacts/load/load.json` no host. Os dois comandos limpam seus próprios contêineres e redes. Aumente ou reduza o lote com `LOAD_REQUESTS=... bash scripts/load.sh` (de 1 a 100000 solicitações por cenário).

O CI executa ambos e disponibiliza o relatório do job `load` como artefato `load-results`. Os resultados mudam com máquina, recursos do Docker, driver e concorrência externa. Não há limiar de desempenho fixo de aprovação: o ensaio exige consistência e respostas previstas, sem transformar velocidade variável de runner em um SLA.

## Perda de conexão durante confirmação: resultado

Os dois casos passaram na execução local da suíte com PostgreSQL 18.3 e .NET 10.0.13. O `CommitProxy` reside somente no projeto de testes e não adiciona um ponto de injeção de falhas ao código da API.

| Falha injetada | Estado observado antes da repetição | Repetição com mesma chave | Resultado final verificado |
| --- | --- | --- | --- |
| COMMIT interceptado e não encaminhado ao PostgreSQL | Movimento não confirmado; saldo R$ 0,00 | Cria a intenção uma vez | Um registro, saldo atual e histórico R$ 17,25 |
| PostgreSQL enviou CommandComplete de COMMIT, mas a conexão é cortada antes de entregá-lo ao driver | Movimento confirmado; saldo R$ 17,25; aplicação recebeu falha de conexão | Recupera o movimento existente | Mesmo identificador, um registro, saldo atual e histórico R$ 17,25 |

Cada caso exige que a injeção tenha ocorrido e que o `Store` tenha recebido `NpgsqlException`. Depois consulta o estado por conexão direta, repete a intenção e repete novamente para verificar idempotência. O primeiro caso funciona como controle negativo; no segundo há confirmação real no servidor ocultada da aplicação.

Limitações: o proxy entende os frames necessários ao caminho de teste do Npgsql e exige TLS e GSS desativados somente nessa conexão de teste. Ele intercepta COMMIT enviado como mensagem Query e confirma a interceptação; uma mudança do driver que altere esse caminho pode exigir ajuste do teste. Não simula queda do processo PostgreSQL, perda de WAL, promoção de réplica, recuperação de disco ou restauração de backup. Não é uma afirmação de disponibilidade sob falha de infraestrutura. Referência do caminho do driver: [NpgsqlTransaction](https://github.com/npgsql/npgsql/blob/v10.0.0/src/Npgsql/NpgsqlTransaction.cs).

## Carga HTTP: método

O gerador usa Python e biblioteca padrão, sem dependências adicionais. Cada cenário cria contas novas e envia 6000 créditos de R$ 0,01, com chaves exclusivas, em um lote de concorrência fechada. Cada trabalhador espera sua resposta antes de iniciar outra solicitação. A latência é medida no cliente e inclui abertura de conexão HTTP; os percentis usam o método nearest rank.

A janela medida contém somente o primeiro envio das intenções. Criação de contas, preparação da aplicação, recuperação e verificações finais não entram no cálculo. Fora da janela, todas as chaves são reenviadas: respostas já confirmadas devem manter o identificador, e recusas transitórias recebem tentativas limitadas. Depois são conferidos saldo atual e histórico de cada conta até o maior `recordedAt` retornado. Cada cenário termina com R$ 60,00 no conjunto de suas contas.

Não há fase de aquecimento controlada. O perfil com 96 trabalhadores oferece concorrência acima do limite de 32 operações da API, mas isso não garante que todos estejam simultaneamente dentro da região protegida. A ocorrência de 429 depende da execução; não é um requisito artificial do ensaio. O teste de saturação da suíte verifica o 429 de forma determinística mantendo uma operação bloqueada.

## Resultados medidos localmente

Execução de 21/10/2026, iniciada às 12:30:47 UTC (09:30:47 em São Paulo). [Relatório bruto](results/load-local.json).

Ambiente: MacBookPro18,3 ARM64, 8 CPUs lógicas, 16 GiB de RAM; API Release em Kestrel e PostgreSQL 18.3 no mesmo host, .NET/ASP.NET Core 10.0.13, gerador Python 3.9. Sem Docker neste ensaio local, sem TLS e com login de banco restrito. Limite de admissão: 32 por instância. O banco já tinha dados de verificações anteriores; cada cenário criou contas novas. A API já havia processado smoke tests e lotes curtos. Não há isolamento de CPU, controle de cache ou comparação com o legado.

| Cenário | Contas / trabalhadores | Duração medida | HTTP 201 / 429 / 503 | Respostas HTTP/s | Novos movimentos/s | p50 / p95 / p99 (ms) |
| --- | --- | --- | --- | --- | --- | --- |
| Distribuído | 32 / 16 | 2,003 s | 6000 / 0 / 0 | 2995,78 | 2995,78 | 4,951 / 8,382 / 10,625 |
| Conta concentradora | 1 / 16 | 5,685 s | 6000 / 0 / 0 | 1055,41 | 1055,41 | 15,599 / 23,084 / 40,272 |
| Concorrência oferecida acima do limite | 32 / 96 | 2,038 s | 5778 / 222 / 0 | 2944,30 | 2835,36 | 29,150 / 58,015 / 74,279 |

Os percentis incluem todos os status da janela medida. “Respostas HTTP/s” inclui recusas; por isso a coluna de novos movimentos/s é apresentada separadamente. As 222 intenções recusadas com 429 no terceiro cenário foram recuperadas fora da medição, com suas chaves originais. Nos três cenários, as verificações finais passaram: R$ 60,00 no saldo agregado, histórico correspondente e repetição sem crédito adicional.

Esses números mostram o custo da disputa por uma conta nesta execução: com os mesmos 16 trabalhadores, a conta concentradora teve menor vazão e p95 maior. O terceiro cenário mostrou admissão limitada com recuperação das recusas. Isso não estabelece a capacidade máxima do sistema.

## Resultado reproduzido em Docker no CI

O [workflow 39600602653](https://github.com/aleksmont/desafio-arquiteto-software/actions/runs/39600602653), do commit `f0843f7`, passou nos três jobs. O comando único de testes executou 36 casos sem falhas, a compilação registrou zero erros/warnings e o smoke test validou o histórico no instante retornado pela operação.

O ensaio iniciou às 12:34:25 UTC de 21/10/2026. O gerador observou Linux x86_64 e 4 CPUs lógicas. API, PostgreSQL 18 e gerador rodaram em contêineres no mesmo runner; o banco era novo e descartável. Hardware e recursos do runner não são equivalentes ao ambiente local. [Relatório bruto do CI](results/load-ci.json).

| Cenário | Duração medida | HTTP 201 / 429 / 503 | Respostas HTTP/s | Novos movimentos/s | p50 / p95 / p99 (ms) |
| --- | --- | --- | --- | --- | --- |
| Distribuído, 32 contas / 16 trabalhadores | 4,012 s | 6000 / 0 / 0 | 1495,54 | 1495,54 | 9,991 / 16,663 / 21,862 |
| Conta concentradora, 1 conta / 16 trabalhadores | 5,433 s | 6000 / 0 / 0 | 1104,42 | 1104,42 | 10,617 / 38,926 / 57,833 |
| Concorrência oferecida acima do limite, 32 contas / 96 trabalhadores | 3,545 s | 5155 / 845 / 0 | 1692,73 | 1454,34 | 51,962 / 100,214 / 127,187 |

As 845 intenções recusadas foram recuperadas fora da janela medida. Os três cenários terminaram com saldo atual e histórico conferidos e R$ 60,00 por cenário. A diferença em relação ao host local reforça que as taxas não devem ser adotadas como capacidade contratada. Os arquivos JSON desta documentação preservam essas execuções; novas execuções produzem novos valores em `artifacts/load/load.json`.

## Limitações e próximos experimentos

- São lotes curtos, com uma execução final por perfil, sem intervalo de confiança, ensaio de horas ou busca do ponto de saturação. Não há alegação de carga sustentada.
- Créditos homogêneos não representam a mistura real de débitos, consultas históricas extensas, transferências ou distribuição de valores. Débitos concorrentes são cobertos pela suíte funcional, não por este benchmark.
- Gerador, aplicação e banco compartilham recursos locais. Python e novas conexões HTTP podem limitar a carga oferecida; não são medidos espera de lock, CPU do banco, I/O, memória ou saturação do pool.
- O teste por TCP no COMMIT não simula todas as falhas possíveis da infraestrutura. Failover, backup/PITR e restauração permanecem pendentes.
- Autorização implementada é por identidade de serviço proprietária da conta; não há autenticação de pessoa física, delegação, transferência de propriedade por API ou isolamento por RLS. O papel SQL continua compartilhado entre serviços.
- O histórico continua sendo por registro, não por instante de commit. Uma movimentação ainda em andamento pode aparecer em consultas posteriores para o mesmo `at` depois de confirmada. Esse requisito precisa de validação com o negócio.

Antes de assumir metas de produção: acordar perfil real e SLOs, repetir medições em ambiente dimensionado com gerador separado, adicionar observabilidade e executar testes de recuperação com RPO/RTO definidos.
