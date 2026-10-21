# Roteiro de apresentação da solução

Duração sugerida: 15 minutos de apresentação e 10 minutos de perguntas. As falas abaixo são sugestões para ensaio; adapte ao seu modo de falar. Antes de apresentar, execute os comandos e confira os resultados no ambiente que será usado.

## Mensagem central

> A proposta mantém movimentação e saldo na mesma transação, trata concorrência e repetição como parte do contrato e demonstra essas decisões com testes. A arquitetura é pequena o suficiente para ser compreendida e tem limites explícitos para sua evolução.

## Preparação

- Tenha abertos o [README](../README.md), os dois diagramas da [Visão do Arquiteto](visao-do-arquiteto.md), o [Store](../src/Ledger.Api/Store.cs) e os [resultados](validation-results.md).
- Execute `bash scripts/test.sh` antes da reunião e confira o CI. A suíte atual tem 36 casos; confirme a quantidade se houver mudanças posteriores.
- Para a demonstração, inicie Docker e a aplicação antes da reunião. Na raiz do repositório: `[ -f .env ] || cp .env.example .env`, depois `docker compose up --build -d`.
- Verifique `/health/ready` com a credencial configurada. Se o banco estiver iniciando, aguarde antes de demonstrar. Os exemplos abaixo pressupõem a chave padrão de desenvolvimento.
- Deixe os relatórios de carga e o CI disponíveis. Evite gastar o tempo da apresentação baixando imagens ou executando o benchmark inteiro.

## Sequência de 15 minutos

| Tempo | Tema | Material para mostrar |
| --- | --- | --- |
| 0–1 min | Problema e prioridade | Contexto do desafio |
| 1–3 min | Arquitetura e escolhas | Diagrama de componentes |
| 3–6 min | Concorrência e idempotência | Diagrama de sequência e transação no Store |
| 6–9 min | Demonstração funcional | Terminal com crédito, débito, repetição e histórico |
| 9–12 min | Evidências e falhas | Testes, proxy de COMMIT e resultados de carga |
| 12–14 min | Segurança e evolução | Identidade por serviço e limitações |
| 14–15 min | Encerramento | Síntese das decisões e espaço para perguntas |

### 1. Problema e prioridade — 1 minuto

**Fala sugerida:**

> O desafio pede registrar créditos e débitos e consultar o saldo ao longo do tempo, com confiabilidade diante de concorrência e falhas. Minha primeira decisão foi definir o que não pode acontecer: aplicar a mesma intenção duas vezes, aprovar débitos usando um saldo antigo ou gravar um movimento sem atualizar o saldo correspondente. Priorizei essas invariantes e depois tratei desempenho e operação dentro dessa fronteira.

Explique as premissas: BRL, centavos, sem cheque especial, conta identificada por UUID e consumidor interno. O histórico usa data de registro; titularidade de pessoa física é externa ao projeto.

### 2. Arquitetura e escolhas — 2 minutos

Mostre o diagrama de componentes da Visão do Arquiteto.

**Fala sugerida:**

> Escolhi uma API ASP.NET Core em C#, conforme o requisito, e PostgreSQL. Uma aplicação e um banco permitem confirmar diário e saldo na mesma transação. Separar essa gravação entre serviços introduziria coordenação distribuída sem uma necessidade demonstrada de implantação independente.
>
> O diário guarda os fatos e permite consultar o histórico. O saldo armazenado atende à leitura atual sem somar todos os movimentos. Essa duplicação tem um custo: preciso mantê-los juntos e verificar divergências. Por isso há transação e reconciliação somente de leitura.
>
> No código, o middleware cuida da credencial e dos erros, os endpoints expõem o contrato e o Store concentra a operação transacional. Para este domínio pequeno, preferi deixar esse fluxo visível. Se as regras crescerem, separaria responsabilidades de domínio e persistência com base nessa complexidade.

Declare o custo: o Store depende de Npgsql e SQL; a solução não pretende representar Clean Architecture completa, microserviços ou event sourcing completo.

### 3. Concorrência e idempotência — 3 minutos

Mostre o diagrama de sequência e, no Store, `BEGIN`, `FOR UPDATE`, consulta de chave, INSERT, UPDATE e COMMIT.

**Fala sugerida:**

> A validação ocorre antes da transação. Dentro dela, o banco bloqueia a linha da conta e só então verifico o estado. Com R$ 100 e dois débitos simultâneos de R$ 80, o primeiro pode confirmar; o segundo lê R$ 20 e é recusado. Contas diferentes não compartilham esse mesmo bloqueio, embora disputem os recursos gerais do banco.
>
> O custo é serializar uma conta concentradora. Colocar mais réplicas da API não elimina esse limite. A espera por bloqueio é limitada e há controle de operações simultâneas para não acumular trabalho sem limite na aplicação.
>
> A chave idempotente representa a intenção do consumidor. Se a resposta se perder, ele repete a mesma chave. Com o mesmo valor e tipo, devolvo o movimento original; com outro conteúdo, retorno conflito. Essa verificação vem antes de calcular um novo débito, porque o saldo pode ter sido alterado pela operação original.

Ressalte: falha de negócio sem lançamento não consome a chave. A repetição só evita duplicação se o consumidor preservar a chave original.

### 4. Demonstração funcional — 3 minutos

Use o passo a passo abaixo. Explique o resultado antes de executar cada etapa; não leia todos os detalhes do JSON.

**Fala sugerida:**

> Vou criar uma conta, creditar R$ 100,50 e debitar R$ 30,25. Espero R$ 70,25 no saldo atual e R$ 100,50 no instante do crédito. Depois vou repetir o crédito com a mesma chave: o saldo deve continuar R$ 70,25.

### 5. Evidências e falhas — 3 minutos

Mostre o CI e o documento de resultados. Para testes de confirmação, abra `CommitFailureTests.cs` apenas se houver interesse no mecanismo.

**Fala sugerida:**

> A suíte testa mais que respostas de sucesso. Há operações concorrentes, falha depois do INSERT e antes do UPDATE, permissões e isolamento de contas. O banco é real porque um mock não reproduziria locks e constraints.
>
> Para confirmar o comportamento em uma falha ambígua, o projeto de testes contém um proxy TCP. Em um caso, ele não entrega o COMMIT ao banco; em outro, o banco confirma e o proxy não entrega essa confirmação ao driver. A aplicação recebe falha de conexão. Ao repetir a mesma chave, o primeiro caso cria a intenção e o segundo recupera a já confirmada. Ambos terminam com um movimento e saldo correto.
>
> Na carga, medi lotes de 6000 créditos em contas distribuídas, uma conta concentradora e concorrência oferecida acima do limite. Em uma execução documentada do CI, foram recuperadas 845 intenções recusadas com 429, mantendo saldo e histórico corretos. Esse número pertence àquela execução e varia entre ambientes.

Explique a diferença entre respostas HTTP/s e novos movimentos/s: respostas 429 também contam na primeira taxa. Os resultados são de lotes curtos, não capacidade sustentada ou SLA de produção.

### 6. Segurança e evolução — 2 minutos

**Fala sugerida:**

> A credencial identifica um serviço, e as contas são vinculadas a ele. Outra identidade não pode ler, movimentar ou assumir a conta. No banco, a API usa um login restrito; o diário tem proteção contra alteração, exclusão e truncamento. Essas camadas reduzem o impacto de erros, mas não substituem segurança operacional.
>
> Antes de produção, eu validaria a definição de histórico e os objetivos operacionais com o negócio. Depois priorizaria identidade e rotação gerenciadas, auditoria, migrações rastreáveis, observabilidade e recuperação de backup e failover. Os testes atuais não comprovam essas capacidades.

Distinga autorização de serviço de autenticação do cliente final. Não afirme que o papel SQL implementa isolamento entre titulares: a filtragem por proprietário está na aplicação e o papel PostgreSQL é compartilhado.

### 7. Encerramento — 1 minuto

**Fala sugerida:**

> A entrega tem uma fronteira de consistência pequena e explícita. As decisões de transação, bloqueio e idempotência podem ser verificadas no código e nos testes. Também documentei os custos: serialização por conta, dependência do primário e histórico com semântica definida. A evolução está orientada por requisitos e medições, preservando essas invariantes financeiras.

Convide o avaliador a escolher um aprofundamento: concorrência, falhas de confirmação, segurança ou capacidade.

## Demonstração: comandos e resultados esperados

Pré-requisitos para este trecho: aplicação já iniciada, Bash, curl e Python 3. Ajuste a chave se o `.env` tiver sido alterado. A conta é nova a cada execução.

### Criar a conta e registrar o crédito

```sh
export API_KEY=local-development-key-change-me-123456
export ACCOUNT="$(python3 -c 'import uuid; print(uuid.uuid4())')"
export BASE_URL=http://localhost:8080
curl -i -X PUT "$BASE_URL/accounts/$ACCOUNT" -H "X-Api-Key: $API_KEY"
CREDIT=$(curl -fsS -X POST "$BASE_URL/accounts/$ACCOUNT/movements" \
  -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' \
  -d '{"key":"presentation-credit","amount":100.50,"type":"credit"}')
printf '%s\n' "$CREDIT"
```

A criação da conta retorna 204. O crédito retorna um identificador e `recordedAt`; preserve essa resposta para consultar o instante histórico.

### Debitar e consultar o saldo atual

```sh
curl -i -X POST "$BASE_URL/accounts/$ACCOUNT/movements" \
  -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' \
  -d '{"key":"presentation-debit","amount":30.25,"type":"debit"}'
curl -fsS "$BASE_URL/accounts/$ACCOUNT/balance" -H "X-Api-Key: $API_KEY"
```

Resultado esperado: débito com 201 e saldo atual de R$ 70,25.

### Repetir o crédito e consultar o histórico

```sh
curl -i -X POST "$BASE_URL/accounts/$ACCOUNT/movements" \
  -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' \
  -d '{"key":"presentation-credit","amount":100.50,"type":"credit"}'
curl -fsS "$BASE_URL/accounts/$ACCOUNT/balance" -H "X-Api-Key: $API_KEY"
RECORDED_AT=$(printf '%s' "$CREDIT" | python3 -c 'import json,sys; print(json.load(sys.stdin)["recordedAt"])')
curl -G "$BASE_URL/accounts/$ACCOUNT/balance" \
  --data-urlencode "at=$RECORDED_AT" -H "X-Api-Key: $API_KEY"
```

Resultado esperado: repetição com 200 e o mesmo identificador do crédito, saldo atual R$ 70,25 e histórico R$ 100,50. O horário é o retornado pelo banco, não uma data fixa.

### Demonstrar a recusa de débito sem fundos

```sh
curl -i -X POST "$BASE_URL/accounts/$ACCOUNT/movements" \
  -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' \
  -d '{"key":"presentation-overdraft","amount":80.00,"type":"debit"}'
curl -fsS "$BASE_URL/accounts/$ACCOUNT/balance" -H "X-Api-Key: $API_KEY"
```

Resultado esperado: 409 e saldo mantido em R$ 70,25. Se o tempo for curto, essa etapa pode ser apresentada pelo teste automatizado.

Se o ambiente da demonstração falhar, mostre os testes e resultados do CI e explique o sintoma observado. Não atribua a falha à rede ou à infraestrutura sem evidência.

## Perguntas prováveis e respostas sugeridas

### 1. Por que uma única aplicação? Isso é arquitetura suficiente?

> A principal decisão arquitetural foi preservar a fronteira de consistência. Uma aplicação mantém diário e saldo em uma transação e reduz os modos de falha. Arquitetura inclui responsabilidades, contratos, implantação e operação; a quantidade de serviços não é um objetivo. Separaria componentes quando existissem fronteiras de domínio e necessidades de autonomia que justificassem o custo.

### 2. Por que PostgreSQL e Npgsql? Por que não Entity Framework?

> O banco relacional se ajusta às transações e restrições necessárias. Usei Npgsql para deixar SQL, bloqueio e escopo transacional visíveis. Entity Framework seria uma alternativa válida; eu precisaria manter explícita a estratégia de concorrência. Não atribuo ganho de desempenho ao driver sem uma comparação medida.

### 3. Por que não usar somente o diário para calcular qualquer saldo?

> Seria possível e reduziria a duplicação. O custo seria somar um histórico crescente em cada leitura atual. Escolhi uma projeção síncrona para essa leitura, assumindo a obrigação de atualizá-la na mesma transação e reconciliá-la com o diário.

### 4. O que garante que o saldo nunca ficará negativo?

> No caminho implementado, a aplicação lê o saldo depois de bloquear a conta, valida o débito e grava na mesma transação. A constraint do banco também rejeita saldo negativo. Isso protege a regra de saldo, mas não prova que qualquer escrita administrativa manterá a correspondência com o diário; essa diferença é tratada por permissões e reconciliação.

### 5. Por que bloqueio pessimista em vez de otimista ou isolamento SERIALIZABLE?

> O bloqueio da linha protege a regra de uma conta em READ COMMITTED, desde que todas as escritas financeiras usem esse caminho. É uma estratégia direta para a disputa prevista. Controle otimista ou SERIALIZABLE exigiriam tratar conflitos e retries. Reavaliaria com medições; operações envolvendo várias contas ou outras invariantes precisariam de outro desenho transacional.

### 6. O bloqueio não vira um gargalo?

> Sim, especialmente em uma conta concentradora. É um custo assumido para ordenar as alterações daquele saldo. Os ensaios mostram esse efeito. Mais réplicas da API não removem a disputa por essa linha; seria necessário revisar o domínio e a estratégia com base no tráfego real.

### 7. O que acontece se o cliente repetir uma movimentação?

> Com a mesma chave, conta, valor e tipo, ele recebe o registro original. Outro conteúdo com a mesma chave retorna 409. Se gerar uma nova chave para a mesma intenção, ela pode ser aplicada novamente; essa obrigação faz parte do contrato do consumidor.

### 8. Você garante processamento exatamente uma vez?

> Não na rede. Há uma proteção idempotente para a mesma intenção identificada pela mesma chave. Uma confirmação perdida continua ambígua para o cliente, e a repetição resolve o resultado consultando o registro persistido.

### 9. O teste realmente interrompe o COMMIT ou apenas lança uma exceção artificial?

> Ele corta uma conexão TCP real entre Npgsql e PostgreSQL. No controle negativo, o COMMIT não é encaminhado. No segundo caso, o proxy recebe a confirmação do banco e a oculta do driver. O teste exige que a interceptação tenha ocorrido e confere estado e repetição por outra conexão. Não há um hook de falha na aplicação de produção.

### 10. E se o PostgreSQL cair durante o commit?

> Os testes entregues não simulam essa falha nem recuperação do servidor. O cliente deve tratar o resultado como incerto e repetir a chave, mas a preservação do dado também depende da durabilidade e da recuperação configuradas no banco. Eu validaria crash recovery, failover e perda tolerável com RPO/RTO acordados.

### 11. Cancelar a requisição garante que nenhum dinheiro se movimente?

> Não. Há teste de cancelamento durante espera de lock, antes da gravação. Se a operação já confirmou ou o cancelamento coincidir com a confirmação, pode haver resultado incerto. O consumidor deve reconciliar o resultado pela mesma chave.

### 12. O saldo histórico é imutável para uma data passada?

> Não necessariamente enquanto existem transações em andamento. A consulta usa data de registro e os movimentos confirmados visíveis naquele momento. Uma transação pode ter recebido horário anterior ao commit e aparecer numa consulta posterior para o mesmo instante. A semântica está documentada e precisa ser confirmada com o negócio antes de uma exigência de auditoria temporal estrita.

### 13. Isso é event sourcing? E partidas dobradas?

> Há um diário de movimentos e uma projeção síncrona de saldo, mas não uma plataforma completa de event sourcing. Também não há razão de partidas dobradas. Se o sistema assumir responsabilidade contábil, será necessário modelar contrapartidas, lançamentos e invariantes adequadas.

### 14. O que acontece quando o banco fica indisponível?

> A API não confirma lançamentos apenas em memória. Falhas transitórias e timeouts tratados retornam 503, orientando repetição limitada com a mesma chave. Erros SQL permanentes recebem 500. Isso prioriza integridade, com dependência explícita da disponibilidade do primário.

### 15. Por que não colocar uma fila para continuar aceitando operações?

> Uma fila mudaria o contrato: recebido não significaria efetivado. Seriam necessários status, acompanhamento e tratamento de duplicatas. Ela pode fazer sentido, mas não elimina a regra de saldo nem a idempotência. Para publicação de eventos após uma gravação, avaliaria outbox transacional.

### 16. Como a aplicação se comporta sob excesso de carga?

> Há limite simultâneo por instância, sem fila na aplicação, e 429 ao exceder. O limite inicial é 32 e precisa de ajuste por medição. Espera de lock e comandos também têm timeouts. Isso não equivale a quota distribuída, proteção completa contra abuso ou capacidade de produção certificada.

### 17. Quantas transações por segundo o sistema suporta?

> Só posso afirmar as taxas observadas nos ambientes e lotes documentados. Respostas por segundo incluem recusas, então também registro novos movimentos por segundo. Não há medição sustentada ou ambiente equivalente à produção para prometer um número contratual.

### 18. Como evita que um serviço consulte a conta de outro?

> A credencial é mapeada para uma identidade, e o Store filtra por owner_id nas operações de conta. A criação idempotente não transfere propriedade de um UUID existente. O teste usa duas identidades e verifica leitura, histórico, movimentação e tentativa de assumir a conta. A API retorna 404 para não revelar sua existência ao outro serviço.

### 19. Então a solução já está segura para um banco real?

> Não apresento essa conclusão. Existem controles demonstráveis, mas faltam identidade e rotação gerenciadas, TLS no ambiente real, auditoria operacional e requisitos do titular final. O isolamento é aplicado pela API; o papel SQL é compartilhado e não há RLS. Os limites estão documentados.

### 20. O diário é inviolável? Um administrador pode mudar dados?

> O runtime não pode editar ou apagar movimentos e há um trigger que também bloqueia mutações. Um administrador pode remover proteções; não existe promessa de inviolabilidade contra ele. Controles administrativos e auditoria são necessários. O runtime ainda atualiza saldo, por isso a reconciliação é importante.

### 21. O que a reconciliação faz quando encontra um erro?

> Compara saldo e diário em uma fotografia consistente, produz um relatório e retorna um código de falha. Não corrige automaticamente. A investigação e a correção precisam preservar evidência e ter decisão operacional e de negócio rastreável.

### 22. Como você colocaria isso em produção ou substituiria o legado?

> O cutover não foi implementado. Primeiro acordaria a semântica, mapearia contas e titularidade, ensaiaria migração e reconciliaria saldos e histórico. Planejaria uma estratégia de transição com um ponto de corte verificável e rollback operacional; evitaria escrita dupla independente sem mecanismo de consistência. A decisão final depende das restrições do legado e da tolerância a indisponibilidade.

### 23. O que faria primeiro com mais tempo?

> Confirmaria metas operacionais e semântica do histórico; depois priorizaria recuperação testada, observabilidade e gestão de identidade e migrações. A instrumentação deve permitir decidir se há necessidade de particionar, criar snapshots ou separar serviços. Não adicionaria infraestrutura distribuída sem uma demanda demonstrada.

### 24. Por que tantos testes de integração e Docker?

> As garantias críticas estão no banco e no pipeline HTTP. O ambiente descartável permite reproduzi-las sem preparação manual. O custo é baixar imagens e depender de Docker. Testes de validação simples continuam diretos; não faria sentido simular locks do PostgreSQL com um mock e chamar isso de evidência de concorrência.

## Respostas curtas para memorizar

| Tema | Frase de apoio |
| --- | --- |
| Transação | “Diário e saldo confirmam juntos.” |
| Concorrência | “A conta é bloqueada antes de avaliar o saldo.” |
| Idempotência | “A repetição mantém a identidade da intenção.” |
| Resultado incerto | “Falha de resposta não prova ausência de commit.” |
| Carga | “Medi cenários; não certifiquei capacidade sustentada.” |
| Segurança | “Há isolamento por serviço, com limites explícitos.” |
| Evolução | “Preservo as invariantes e mudo a arquitetura por evidência.” |

## Como responder quando a pergunta ultrapassar a entrega

Use a sequência: comportamento atual, evidência disponível, limite e próximo experimento. Por exemplo:

> Hoje o histórico consulta registros confirmados por data de registro. Os testes verificam os limites de consulta e os saldos. Não existe reconstrução estrita por instante de commit. Se esse fosse o requisito, eu alinharia a semântica com o negócio e projetaria a solução temporal correspondente antes de prometer essa garantia.

Se não souber um detalhe, diga que precisa verificá-lo no código ou na documentação. Diferencie o que você implementou, o que consegue demonstrar e o que está propondo como evolução.
