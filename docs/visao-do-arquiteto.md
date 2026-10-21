# Visão do Arquiteto

## Intenção da proposta

A decisão central deste projeto é manter o registro da movimentação e o saldo da conta dentro de uma única fronteira transacional. Em um sistema financeiro, aprovar um débito incorreto ou registrar a mesma intenção duas vezes pode causar um dano maior que recusar temporariamente uma operação. A proposta parte dessa prioridade e busca demonstrá-la no código, no banco e nos testes.

O desafio descreve lentidão, instabilidade e dificuldade de manutenção, mas não fornece métricas do legado. Por isso, esta implementação é uma proposta de substituição do núcleo, com escolhas verificáveis. Não há evidência que permita afirmar que ela supera o sistema anterior em desempenho ou que suporta determinado volume de produção.

Este documento registra o raciocínio arquitetural. O [detalhamento técnico](architecture.md) explica os componentes; a [matriz de requisitos](requirements.md) relaciona a entrega ao enunciado; o [guia de operação](operations.md) trata de permissões, atualização e reconciliação.

## Perguntas que orientam o desenho

Antes de decidir por serviços, banco ou framework, as perguntas relevantes são:

- Como impedir que duas operações gastem simultaneamente o mesmo saldo?
- Como responder quando a gravação ocorreu, mas o cliente não recebeu a confirmação?
- Como preservar o histórico e tornar a leitura atual eficiente?
- Como limitar o impacto de falhas e de demanda maior que a capacidade?
- Como outra pessoa verifica essas garantias e identifica o que ainda falta?

A prioridade adotada é integridade financeira, seguida de comportamento previsível diante de falhas, facilidade de verificação e evolução. Desempenho deve ser medido dentro dessas restrições.

## Premissas e pontos a esclarecer com o negócio

A solução assume BRL, precisão de centavos, uma conta por cliente e ausência de saldo negativo. O identificador de conta é recebido de um consumidor interno; cadastro, titularidade e autorização por cliente não estão implementados. Esses pontos são simplificações do projeto e precisam de confirmação em um contexto real.

A expressão “saldo em determinado momento” exige uma definição de negócio. Aqui, o histórico usa a data de registro atribuída pelo banco e considera somente movimentos confirmados e visíveis à consulta. Não representa data econômica, lançamentos retroativos nem uma reconstrução exata da visibilidade no instante do commit. Um registro ainda não confirmado pode aparecer depois em uma nova consulta para o mesmo instante histórico.

Também faltam informações para dimensionar infraestrutura: operações por segundo, proporção entre leituras e escritas, concentração de tráfego por conta, retenção do diário, metas de latência e tolerância a indisponibilidade. RPO, que define a perda de dados tolerável, e RTO, que define o tempo de recuperação esperado, precisam ser acordados com negócio e operação.

## 1. Uma aplicação, com uma fronteira de consistência clara

Foi escolhida uma API ASP.NET Core com PostgreSQL. C# atende ao requisito obrigatório. O runtime .NET 10 segue a versão adotada pelo projeto; usar o SDK em contêiner reduz diferenças entre quem desenvolve, quem avalia e o CI. PostgreSQL oferece transações, bloqueios e restrições que se ajustam às invariantes financeiras implementadas.

A aplicação tem responsabilidades separadas em arquivos, mas não é apresentada como um monólito modular completo ou como uma implementação completa de Clean Architecture. O `Store` reúne SQL, transação e regras dependentes do estado da conta. Isso deixa a operação crítica próxima e revisável, com o custo de acoplar essas regras ao acesso a dados.

Criar vários projetos, interfaces e serviços agora aumentaria a quantidade de pontos a entender sem uma necessidade demonstrada de implantação independente. Essa escolha deve ser revista quando surgirem regras de domínio mais extensas, integrações com ciclos de vida distintos ou equipes que precisem de autonomia de entrega.

Microserviços seriam uma possibilidade de evolução, mas separar a gravação do diário da atualização do saldo introduziria coordenação distribuída em uma operação que hoje precisa ser atômica. A separação exige uma necessidade concreta e um contrato explícito de consistência.

## 2. Diário como registro dos fatos e saldo como projeção síncrona

A tabela `movements` preserva os créditos e débitos. A tabela `accounts` mantém o saldo atual para evitar uma soma completa a cada leitura. O saldo é uma projeção síncrona: seu valor é atualizado na mesma transação do movimento.

Guardar duas representações tem um custo. Se alguém atualizar apenas uma delas, a conta poderá divergir. A decisão só é aceitável porque a escrita normal é atômica, há testes de rollback e existe uma reconciliação somente de leitura que compara a projeção ao diário. Essa reconciliação detecta diferenças; não as corrige nem substitui controle de acesso.

O diário é protegido contra UPDATE, DELETE e TRUNCATE por trigger e por permissões de runtime. Isso reduz alterações acidentais, mas não o torna inviolável: um administrador pode modificar as proteções. A aplicação também não possui vínculo formal de estorno ou trilha completa de auditoria administrativa.

Esse desenho não é event sourcing completo e não implementa razão contábil de partidas dobradas. Se o escopo passar a incluir contabilidade, transferências ou conciliação com sistemas externos, será necessário modelar os lançamentos e suas contrapartidas, não apenas renomear as estruturas existentes.

## 3. Concorrência resolvida onde as escritas se encontram

As movimentações adquirem `FOR UPDATE` na linha da conta dentro de uma transação READ COMMITTED. O saldo é verificado depois de adquirir o bloqueio. Assim, duas réplicas da API não aprovam débitos com base na mesma posição antiga.

Exemplo: com R$ 100 disponíveis, dois débitos simultâneos de R$ 80 precisam ser avaliados em sequência. O primeiro deixa R$ 20; o segundo encontra esse valor e é recusado. O bloqueio é por conta, permitindo que outras contas avancem sem compartilhar esse mesmo bloqueio.

O custo assumido é a serialização de uma conta muito disputada. Escalar a API horizontalmente não elimina esse limite. Controle otimista com versão seria uma alternativa, mas exigiria detectar conflitos e repetir operações; em alta disputa, pode apenas substituir espera por tentativas adicionais. A escolha atual favorece um fluxo simples de revisar.

Não há promessa de ordem de chegada HTTP ou justiça entre consumidores. O banco coordena a aquisição do bloqueio. A espera tem limite de 3 segundos para evitar ocupar recursos por tempo excessivo; esse valor é inicial e precisa de medição.

## 4. Idempotência como parte do contrato financeiro

Uma resposta perdida não significa que a operação falhou. Por isso o consumidor fornece uma chave que representa sua intenção. A unicidade por conta, a comparação de valor e tipo e a consulta dentro da transação permitem retornar o movimento original em uma repetição.

Isso evita uma segunda aplicação da mesma intenção quando o consumidor mantém a chave. Não existe garantia de “exatamente uma vez” na rede. Se o cliente gerar outra chave após um timeout, o servidor não tem informação suficiente para reconhecer que se trata da mesma operação.

A chave é consultada antes da avaliação de um novo débito, pois o saldo pode ter mudado justamente pela operação original. Falhas de negócio que não gravaram movimento não consomem a chave. As chaves permanecem no diário; remover registros antigos exigiria preservar a política de idempotência e seus prazos.

Uma fila, sozinha, não resolveria esse problema: consumidores também podem receber mensagens repetidas. Caso eventos sejam necessários, a evolução indicada é publicação via outbox transacional e consumidores idempotentes, com semântica de entrega documentada.

## 5. Leitura rápida sem esconder a semântica do histórico

A consulta atual lê o saldo armazenado. A histórica soma créditos e débitos até o instante solicitado, usando índice por conta e data. Uma única instrução SQL fornece uma fotografia consistente da consulta, sem combinar leituras feitas em momentos diferentes.

O índice reduz o espaço de busca, mas não elimina o custo de percorrer um histórico extenso. Snapshots e projeções especializadas só devem ser introduzidos após medir volume e distribuição das consultas. Eles precisam de regras de reconstrução, atualização e verificação de consistência.

Não foi adicionado cache de saldo. Invalidar um cache financeiro e explicar seu atraso seriam responsabilidades adicionais. Uma réplica de leitura também pode estar atrasada: seu uso exigiria expor a posição de atualização ou aceitar explicitamente uma leitura desatualizada. Hoje as consultas usam o banco configurado, que no Compose é o mesmo primário da escrita.

## 6. Sobrecarga e indisponibilidade com resultado previsível

A API limita a quantidade de operações de contas simultâneas por instância, sem fila de espera na aplicação. Quando esse limite é atingido, retorna 429. Falhas transitórias de persistência recebem 503; erros SQL permanentes recebem 500. A aplicação não aceita lançamentos apenas em memória para aparentar disponibilidade.

O custo é recusar trabalho que talvez pudesse ser atendido após espera. A motivação é limitar a ocupação de recursos e permitir ao consumidor repetir com prazo, backoff e a mesma chave. Uma fila durável seria outro modelo de produto: o contrato precisaria distinguir solicitação recebida de movimentação efetivada.

O limite inicial de 32 operações, os timeouts e o `Retry-After` são parâmetros de partida, não resultados de benchmark. O controle é local a cada processo e não substitui quota global, proteção no gateway ou planejamento de capacidade. Health checks ficam fora desse limite e também exigem proteção no ambiente real.

A API pode ter múltiplas réplicas sem compartilhar memória de saldo, mas o banco primário continua sendo uma dependência central. Alta disponibilidade precisa de topologia, política de failover e durabilidade de réplica definidas e testadas. Não é fornecida apenas pelo uso de Docker ou PostgreSQL.

## 7. Segurança em camadas, com limites reconhecidos

O projeto combina validação de entrada, SQL parametrizado, limite de corpo, credencial de serviço e usuário de banco restrito. O login de runtime pode operar contas e inserir movimentos, mas não deve administrar tabelas, apagar registros ou desativar o trigger do diário. Migrações são responsabilidade separada.

A motivação é reduzir o impacto de um defeito ou comprometimento da aplicação. Ainda assim, o runtime precisa atualizar saldo e pode fazê-lo diretamente; as permissões não garantem, isoladamente, que toda atualização corresponda a um movimento. A atomicidade do fluxo e a reconciliação continuam necessárias.

A chave compartilhada é um mecanismo de demonstração para consumidor interno. Ela não distingue titulares, não autoriza por conta e não fornece rotação gerenciada. Antes de exposição real, é necessário definir identidade por serviço, autorização, TLS, gestão de segredos, auditoria de acesso e retenção de dados. O identificador UUID não é uma credencial.

A aplicação evita registrar corpo, credenciais e conteúdo SQL nos erros tratados de persistência. Relatórios de reconciliação contêm valores e identificadores financeiros e precisam de acesso restrito. A avaliação de segurança deve incluir também logs de infraestrutura e procedimentos de operação.

## 8. Testabilidade e experiência de avaliação são decisões de arquitetura

A suíte usa PostgreSQL real porque mocks não reproduzem locks, constraints, permissões ou rollback do banco. Os testes verificam invariantes financeiras e cenários de falha controlada, além do pipeline HTTP. O smoke test separado exercita a aplicação empacotada e o servidor HTTP real.

O comando `bash scripts/test.sh` prepara dependências, banco temporário e migrações, compila e executa a suíte, devolvendo seu resultado e limpando o ambiente. O CI usa o mesmo caminho. Essa escolha reduz o trabalho manual de quem avalia e a diferença entre ambientes, ao custo de exigir Docker e downloads na primeira execução.

Os testes são evidência dos cenários executados. Não demonstram vazão de produção, recuperação após desastre ou ausência de todas as condições de corrida. Ainda faltam simulações de perda de conexão durante commit, failover, restauração e carga sustentada.

## Como decidir as próximas evoluções

| Sinal observado ou necessidade confirmada | Evolução a avaliar | Evidência esperada antes de assumir a solução |
| --- | --- | --- |
| Espera alta em poucas contas | Rever regras de reserva, admissão e estratégia de concorrência | Latência e espera de locks por distribuição de contas |
| Histórico com custo elevado | Snapshots, particionamento ou projeções de leitura | Planos de consulta, volume, retenção e requisito de frescor |
| Integração por eventos | Outbox e consumo idempotente | Testes de reentrega, atraso e recuperação sem perda de eventos |
| Exposição a outros serviços ou clientes | Identidade, escopos e autorização por conta | Testes de isolamento e recusa de acesso indevido |
| Necessidade de continuidade operacional | Réplicas, failover e backup/PITR | Exercícios de recuperação que atendam RPO e RTO acordados |
| Regras e equipes com ciclos distintos | Separar módulos e, se necessário, serviços | Fronteiras de domínio e contratos que justifiquem a coordenação adicional |

Antes de produção, priorizaria identidade e autorização, gestão de migrações com histórico, observabilidade, recuperação testada e reconciliação operacional acompanhada. Instrumentaria latência, erros, saturação do pool e espera de locks antes de afirmar que uma mudança resolve o desempenho.

## Critério de sucesso da arquitetura

A proposta é adequada ao desafio se o avaliador conseguir compreender a fronteira de consistência, reproduzir os testes e identificar tanto as garantias quanto os custos. Cada evolução deve preservar as invariantes financeiras ou declarar claramente uma nova semântica de negócio.

O compromisso arquitetural é manter as decisões proporcionais ao problema e revisáveis por evidência. A simplicidade atual é uma escolha com limites explícitos; a evolução deve responder a necessidades medidas, sem perder a capacidade de explicar como o dinheiro é registrado e protegido.
