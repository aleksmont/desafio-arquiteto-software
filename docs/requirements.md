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
| Comportar-se diante de falha parcial | Falhas Npgsql retornam 503; a API não confirma escrita apenas em memória | Comportamento previsto em código; failover e indisponibilidade não têm teste automatizado nesta versão |

Os caminhos da tabela são relativos à raiz do repositório. Os testes ficam em `tests/Ledger.Tests/LedgerTests.cs`.

## Requisitos obrigatórios

| Requisito | Entrega | Como conferir |
| --- | --- | --- |
| C# | API ASP.NET Core e testes em C# | `src/Ledger.Api` e `tests/Ledger.Tests` |
| Testes automatizados | xUnit com validações e PostgreSQL real | `dotnet test -c Release --no-build`, após preparar o banco |
| GitHub público | [Repositório do desafio](https://github.com/aleksmont/desafio-arquiteto-software) | Visibilidade pública confirmada na publicação inicial; configuração externa ao código |
| Execução local documentada | Instruções com Docker e SDK | README e arquivos `compose.yaml`, `Dockerfile`, `.env.example` |
| Documentação no repositório | README e diretório `docs` | Nenhum documento externo é necessário para compreender a proposta |
| Compilar sem erros e warnings | `TreatWarningsAsErrors` em `Directory.Build.props` | `dotnet build -c Release --no-restore`; o CI executa o mesmo comando |

## Premissas que precisam de validação com o negócio

- **Uma conta por cliente e BRL:** simplifica o exemplo; o cadastro e a titularidade são externos e não são verificados pela API.
- **Sem saldo negativo:** protege contra gasto acima do saldo, mas cheque especial ou reserva de valores exigiriam outras regras.
- **Data de registro:** `at` filtra movimentos pela data atribuída pelo banco, não pela data econômica de um evento externo nem pelo instante de commit.
- **Sem retroatividade:** o consumidor não informa data de lançamento. Uma consulta anterior à criação da conta retorna zero se a conta já existe no momento da consulta.
- **Consumidor interno:** a chave compartilhada identifica acesso ao serviço, sem distinguir clientes ou permissões por conta.
- **Correções por compensação:** preservar o diário facilita investigação, mas ainda não existe vínculo formal entre uma correção e o movimento original.

## O que os testes demonstram

A suíte inicial contém 11 casos executados: quatro combinações inválidas de valor/tipo, uma chave vazia e seis cenários de integração. Os cenários de concorrência disparam 20 solicitações ao `Store`, usando conexões reais ao PostgreSQL. Isso exercita o bloqueio e a restrição única do banco, que um repositório simulado não reproduziria.

A compilação inicial passou sem erros e warnings e os 11 casos passaram. Também houve verificação manual do fluxo HTTP. Isso é evidência daquela execução, não garantia de que alterações futuras passarão. O workflow configura a repetição dos checks a cada push/PR; sua existência não prova que uma execução remota específica passou.

Ainda faltam testes automatizados de contrato HTTP, autenticação, cancelamento e falha de persistência. Os testes atuais não interrompem o processo entre INSERT e UPDATE, não derrubam o banco durante commit e não medem latência, throughput ou recuperação após desastre. Docker foi configurado, mas não foi executado na validação inicial.

## Evolução priorizada

1. **Reproduzir a entrega:** validar build e inicialização pelo Compose e acrescentar testes HTTP para autenticação, erros e contrato. Critério: ambiente vazio inicia e o fluxo completo passa automaticamente.
2. **Endurecer acesso e persistência:** identidade por serviço, autorização por conta, papel SQL restrito e migrações versionadas. Critério: consumidor sem permissão não movimenta a conta e runtime não altera o schema ou remove o diário.
3. **Validar recuperação:** simular resposta perdida, queda do banco e commit ambíguo; ensaiar backup e restauração. Critério: nenhuma duplicata após repetição e saldo reconciliado com o diário.
4. **Medir e controlar demanda:** estabelecer metas com negócio, medir contas distribuídas e conta concentradora, adicionar métricas e controle de admissão. Critério: limites e degradação conhecidos, sem promessas de capacidade sem medição.
5. **Expandir domínio conforme necessidade:** múltiplas contas/moedas, transferências, reservas e partidas dobradas. Cada expansão exige invariantes e testes próprios; não é requisito explicitado no desafio.
