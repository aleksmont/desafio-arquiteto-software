# Requisitos, premissas e evidências

## Leitura do desafio

O [enunciado](../desafio-arquiteto-software.pdf) descreve um sistema bancário com lentidão, instabilidade e dificuldade de manutenção. Pede registro de créditos e débitos, consulta de saldo em determinado momento e decisões justificadas sobre confiabilidade, concorrência, escala e segurança. A avaliação considera tanto o código quanto os motivos das escolhas.

O documento exige C#, testes automatizados, repositório público, README para execução local, documentação dentro do repositório e compilação sem erros ou warnings. Não exige microserviços, fila, interface gráfica, provedor de nuvem ou framework específico. Esses elementos devem ser escolhidos pela necessidade, não acrescentados apenas para demonstrar ferramentas.

## Atendimento funcional

| Necessidade | Implementação e localização | Evidência e limite |
| --- | --- | --- |
| Registrar créditos e débitos | `Store.Record`, em `src/Ledger.Api/Store.cs`, e tabela `movements` | Teste `CreditDebitAndHistoricalBalance`; não implementa transferência entre contas |
| Consultar saldo consolidado | `Store.GetBalance`: saldo atual na conta ou soma do diário até `at` | Mesmo teste verifica saldo antes e depois dos registros; é histórico por data de registro |
| Evitar inconsistência em concorrência | Transação e `SELECT ... FOR UPDATE` por conta | `ConcurrentDebitsNeverOverdraw`; não é ensaio de carga |
| Tratar repetição após falha | Chave idempotente por conta, com comparação de valor e tipo | `ConcurrentDuplicatesOnlyApplyOnce` e `DifferentPayloadWithSameKeyConflicts`; queda de rede durante commit ainda não foi simulada |
| Impedir débito acima do saldo | Validação após bloqueio e constraint de saldo não negativo | `InsufficientFundsDoesNotReserveKey`; ausência de cheque especial é premissa adotada |
| Proteger acesso e entrada | Chave de serviço, SQL parametrizado e validação monetária | Testes de validação; autorização por titular/conta ainda não implementada |
| Comportar-se diante de falha parcial | Falhas transitórias retornam 503, permanentes retornam 500; sem confirmação de escrita apenas em memória | HTTP testa indisponibilidade do banco e timeout de lock; failover não é simulado |

Os caminhos da tabela são relativos à raiz do repositório. Os testes ficam em `tests/Ledger.Tests/LedgerTests.cs`.

## Requisitos obrigatórios

| Requisito | Entrega | Como conferir |
| --- | --- | --- |
| C# | API ASP.NET Core e testes em C# | `src/Ledger.Api` e `tests/Ledger.Tests` |
| Testes automatizados | xUnit com validações e PostgreSQL real | `bash scripts/test.sh`, sem preparação manual de banco |
| GitHub público | [Repositório do desafio](https://github.com/aleksmont/desafio-arquiteto-software) | Visibilidade pública confirmada na publicação inicial; configuração externa ao código |
| Execução local documentada | Instruções com Docker e SDK | README e arquivos `compose.yaml`, `Dockerfile`, `.env.example` |
| Documentação no repositório | README e diretório `docs` | Nenhum documento externo é necessário para compreender a proposta |
| Compilar sem erros e warnings | `TreatWarningsAsErrors` em `Directory.Build.props` | `bash scripts/test.sh` compila antes dos testes; o CI usa esse mesmo script |

## Premissas que precisam de validação com o negócio

- **Uma conta por cliente e BRL:** simplifica o exemplo; o cadastro e a titularidade são externos e não são verificados pela API.
- **Sem saldo negativo:** protege contra gasto acima do saldo, mas cheque especial ou reserva de valores exigiriam outras regras.
- **Data de registro:** `at` filtra movimentos pela data atribuída pelo banco, não pela data econômica de um evento externo nem pelo instante de commit.
- **Sem retroatividade:** o consumidor não informa data de lançamento. Uma consulta anterior à criação da conta retorna zero se a conta já existe no momento da consulta.
- **Consumidor interno:** a chave compartilhada identifica acesso ao serviço, sem distinguir clientes ou permissões por conta.
- **Correções por compensação:** preservar o diário facilita investigação, mas ainda não existe vínculo formal entre uma correção e o movimento original.

## O que os testes demonstram

A suíte ampliada contém 33 casos: regras monetárias, histórico, idempotência, débitos concorrentes, validação de datas, contrato HTTP, autenticação, capacidade simultânea, timeout de lock, indisponibilidade do banco, erro SQL permanente, proteção do diário, rollback após falha entre INSERT e UPDATE, permissões do login restrito, detecção de divergência, cancelamento e JSON inválido. O build trata warnings como erros.

Os testes HTTP usam WebApplicationFactory e o pipeline real da aplicação, com PostgreSQL real para os cenários de persistência. Os testes de falha injetam condições controladas, incluindo bloqueio por outra transação e constraint que impede a atualização de saldo após a inserção do diário. Os testes de proteção SQL exigem a migração 002 aplicada.

Ainda faltam simulações de rede durante commit, cancelamento durante commit, failover, recuperação de backup e ensaio de carga. A existência do workflow não comprova uma execução remota específica: conferir o resultado do GitHub Actions. Há um job separado de contêineres que executa o smoke test; Docker não está disponível no ambiente local usado nesta revisão.

## Evolução priorizada

1. **Reproduzir a entrega:** acompanhar o job de contêineres no CI e ampliar os testes HTTP já implementados, ampliar cobertura de cancelamento durante commit. Critério: ambiente vazio inicia e o fluxo completo passa automaticamente.
2. **Endurecer acesso e persistência:** identidade por serviço, autorização por conta e executor de migrações com checksums; o papel SQL restrito já foi implementado. Critério: consumidor sem permissão não movimenta a conta e runtime não altera o schema ou remove o diário.
3. **Validar recuperação:** simular resposta perdida, queda do banco e commit ambíguo; ensaiar backup e restauração. Critério: nenhuma duplicata após repetição e saldo reconciliado com o diário.
4. **Medir e controlar demanda:** estabelecer metas com negócio, medir contas distribuídas e conta concentradora, adicionar métricas e controle de admissão. Critério: limites e degradação conhecidos, sem promessas de capacidade sem medição.
5. **Expandir domínio conforme necessidade:** múltiplas contas/moedas, transferências, reservas e partidas dobradas. Cada expansão exige invariantes e testes próprios; não é requisito explicitado no desafio.
