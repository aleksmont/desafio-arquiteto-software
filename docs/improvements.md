# Revisão da solução: o que melhorou e por quê

A revisão mantém uma aplicação e PostgreSQL. O foco é tornar as garantias verificáveis, reduzir a chance de sobrecarga e impedir falhas silenciosas; distribuir o sistema agora não resolveria esses problemas.

| Mudança implementada | Problema anterior | Por que melhora | Custo ou limite |
| --- | --- | --- | --- |
| Testes HTTP com pipeline real | Somente testes diretos do Store e validação | Detecta problemas de autenticação, serialização, rotas e mapeamento de erros | Não substitui ensaio com rede real |
| Teste de falha após INSERT e antes de UPDATE | Não havia injeção nesse ponto crítico | Demonstra que diário e saldo voltam ao estado anterior juntos | Não simula commit ambíguo na rede |
| Teste e proteção SQL do diário | Ausência de endpoints de edição era a única proteção específica | Trigger rejeita UPDATE, DELETE e TRUNCATE, preservando rastreabilidade contra operações acidentais | Exige migração; administrador ainda pode remover o trigger |
| Controle de admissão | Requisições podiam acumular esperando recursos | Limite configurável, sem fila, devolve 429 e evita espera sem limite na aplicação | Limite por processo; 32 é padrão inicial sem benchmark |
| Espera de lock limitada | Conta concentradora podia prender operações por mais tempo | Timeout de 3 segundos libera capacidade e orienta repetição com a mesma chave | Pode exigir ajuste conforme latência e perfil de carga |
| Classificação de falhas de persistência | Qualquer erro Npgsql virava 503 | Erros transitórios indicam repetição; erro permanente vira 500 sem sugerir retry | Depende da classificação do driver e dos SQLSTATEs tratados |
| Datas com formato e fuso explícitos | Binding aceitava interpretações implícitas | Consulta histórica deixa de depender do fuso do servidor | Consumidores precisam enviar Z ou offset válido |
| Readiness com leitura limitada | COUNT exigia contar as contas | Custo da sondagem deixa de crescer com a contagem inteira | Não é diagnóstico completo de schema e privilégios |
| Middleware próprio | Autenticação e falhas estavam misturadas aos endpoints | Facilita revisar a política de acesso e erro em um lugar | Chave compartilhada continua sendo limitação |
| SQL com colunas explícitas e isolamento explícito | INSERT dependia da ordem física das colunas e isolamento dependia do padrão | Intenção fica visível e adição de colunas é menos frágil | Mudanças de schema ainda exigem migração planejada |
| Smoke test e job de contêineres | Empacotamento não tinha verificação automática | CI inicia aplicação e banco e testa operação repetida por HTTP | Resultado deve ser conferido no Actions; Docker não foi executado localmente |

## Como reproduzir a validação

1. Preparar banco exclusivo de teste com `schema.sql` e `database/migrations/002-protect-journal.sql`.
2. Configurar `TEST_DATABASE` e executar os comandos de restore/build/test do README.
3. Executar o smoke test com a API iniciada; ele cria uma conta exclusiva e verifica crédito e repetição sem duplicidade.
4. Para volumes já existentes, aplicar a migração antes de publicar a nova versão da API.

O teste de proteção SQL exige credencial com permissão suficiente para alcançar o trigger, e o teste de rollback cria uma constraint temporária restrita à conta exclusiva do teste, removida no finally. Por isso os testes devem usar um banco dedicado, nunca o banco de produção.

## O que permanece para uma operação bancária

A melhoria aumenta a confiança no núcleo transacional, mas não demonstra capacidade de produção. Ainda são necessárias autorização por conta, identidade e rotação gerenciada de credenciais, agendamento e acompanhamento da reconciliação, backup/PITR testado, failover, métricas e validação de carga. Esses itens exigem requisitos operacionais e ambiente apropriado. O histórico continua representando data de registro, não data econômica ou instante de commit.

Referências da implementação: [testes de integração ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0) e [controle de concorrência HTTP](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit).

## Continuação: reduzir privilégios e detectar divergências

A migração 003 e o provisionamento 004 separam o login usado pela API do administrador. Isso reduz o impacto de uma falha na aplicação: os testes comprovam que o runtime opera créditos/débitos, mas não apaga registros, altera schema ou desativa o trigger. A proteção não impede o runtime de atualizar saldo diretamente; por isso a reconciliação adicionada compara a projeção com o diário sem corrigir dados.

Também foram acrescentados contrato JSON estrito, limite de corpo no Kestrel e teste de cancelamento em espera de lock. O objetivo é rejeitar requisições ambíguas e comprovar que uma operação cancelada antes da gravação pode ser repetida com a mesma chave. Não há garantia de cancelamento de operações já confirmadas. O guia de [operação](operations.md) detalha configuração, atualizações e tratamento de divergências.
