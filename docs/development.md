# Desenvolvimento e testes manuais (opcional)

Para avaliar o projeto, prefira `bash scripts/test.sh`, descrito no [README](../README.md). Este guia é para quem quer depurar com SDK e IDE ou controlar um banco próprio.

## Como funciona o comando de testes

```sh
bash scripts/test.sh
```

O script automatiza a preparação e a execução sem mudar o que os testes verificam. Ele coordena o Docker; o [Compose de testes](../compose.test.yaml) descreve o banco e suas dependências; o [Dockerfile de testes](../Dockerfile.test) descreve a compilação. A execução segue estas etapas:

### 1. Localizar os arquivos e verificar os pré-requisitos

O script calcula a raiz do repositório a partir da própria localização, sem depender da pasta atual do terminal. Em seguida, verifica se Docker e Compose estão instalados e se o serviço Docker responde.

**Por quê:** um erro de instalação deve aparecer como uma mensagem clara antes de tentar criar qualquer recurso. Não é necessário instalar SDK .NET, PostgreSQL ou cliente `psql` no computador. A primeira execução precisa de rede para baixar imagens e pacotes.

### 2. Criar uma identificação exclusiva e registrar a limpeza

Cada execução recebe um nome de projeto como `ledger-tests-12345-6789`, composto pelo identificador do processo e um número aleatório. Esse nome separa os contêineres e a rede dos demais projetos Compose. O script também registra a função de limpeza para a saída normal, falhas e interrupções tratáveis, como Ctrl+C.

**Por quê:** os testes não devem parar a aplicação local nem reutilizar o banco de outra execução. Registrar a limpeza antes de iniciar o Compose permite remover recursos mesmo se a preparação falhar pela metade. Uma interrupção forçada do processo ou indisponibilidade do Docker pode impedir a limpeza; nesse último caso, o script informa o comando manual correspondente.

### 3. Preparar a imagem com o SDK e compilar

O Docker usa `Dockerfile.test`, que contém o SDK .NET 10. Primeiro copia os arquivos de projeto e executa `dotnet restore` para baixar as dependências. Depois copia o código, os testes e o SQL e executa `dotnet build -c Release --no-restore`.

**Por quê:** todos usam o mesmo ambiente de compilação, sem depender do SDK instalado no host. Copiar os arquivos de projeto antes do código permite aproveitar o cache de dependências quando apenas a implementação muda. O projeto trata warnings como erros: a execução não avança com uma compilação inválida. Docker pode reutilizar etapas em cache quando seus arquivos não mudaram.

### 4. Iniciar e preparar o PostgreSQL temporário

O serviço `db` usa PostgreSQL 18, cria o banco `ledger_test` e executa os arquivos de inicialização em ordem:

| Arquivo | O que prepara | Por que é necessário |
| --- | --- | --- |
| `001-schema.sql`, montado a partir de `src/Ledger.Api/schema.sql` | Tabelas de contas e movimentos, constraints e índice | Os testes exercitam o mesmo modelo de dados da aplicação |
| `002-protect-journal.sql` | Trigger contra alteração, exclusão e truncamento do diário | Permite verificar a proteção dos registros financeiros |
| `003-runtime-permissions.sql` | Papel restrito `ledger_runtime` | Permite testar quais operações um login de runtime pode executar |

O banco usa armazenamento temporário (`tmpfs`) e não publica a porta 5432 no computador. A conexão é interna à rede desse projeto Compose. O arquivo de testes não usa as credenciais do `.env` nem o volume persistente da aplicação; a conexão `TEST_DATABASE` é definida no próprio serviço de testes.

**Por quê:** cada execução começa com dados novos, não conflita com PostgreSQL já instalado e não modifica os dados usados para experimentar a API. A credencial administrativa é exclusiva desse banco descartável, necessária para os testes que criam logins e injetam falhas. O provisionamento do login da API (`004`) não é executado aqui: o teste de permissões cria seu próprio login temporário.

### 5. Esperar o banco ficar pronto

O Compose só inicia o serviço `tests` depois de o health check do banco passar. A sondagem usa `pg_isready` por TCP em `127.0.0.1`, com intervalo de 1 segundo, timeout de 3 segundos e até 60 falhas consecutivas.

**Por quê:** durante a inicialização, a imagem PostgreSQL usa um servidor temporário por socket para aplicar o SQL. A sondagem TCP evita confundir esse estágio com o servidor definitivo pronto para receber os testes. Isso reduz falhas de preparação por disputa de tempo.

### 6. Executar a suíte e exibir o resultado

O serviço `tests` executa `dotnet test -c Release --no-build --no-restore`. A compilação e a restauração já ocorreram na construção da imagem. As verificações incluem validação, HTTP, transações, concorrência, permissões, cancelamento e reconciliação com PostgreSQL real.

**Por quê:** reutilizar o build evita trabalho duplicado e garante que a suíte rode o código que acabou de compilar. Os testes HTTP usam o pipeline real em um servidor de teste em memória; o teste do servidor HTTP real continua no smoke test separado. O comando principal não inicia a API do Compose da aplicação.

O terminal mostra o progresso e o resumo da suíte. O resultado esperado é `Passed!` com 33 casos aprovados. Com falha, o framework mostra o teste e sua mensagem. A quantidade de casos pode aumentar com a evolução do projeto.

### 7. Encerrar os serviços e preservar o resultado

O Compose recebe `--abort-on-container-exit` para encerrar os demais serviços quando um deles termina e `--exit-code-from tests` para usar o resultado do serviço de testes como resultado da execução. Falhas de build ou preparação também fazem o comando falhar.

Depois, a função de limpeza executa `down --volumes --remove-orphans` apenas para o projeto exclusivo desta execução. Ela preserva o código de saída original. Se os testes passarem mas a limpeza falhar, o script retorna erro e mostra o comando de limpeza manual. Ctrl+C e término tratável retornam, respectivamente, 130 e 143.

**Por quê:** o mesmo comando precisa servir para avaliação local e automação. Uma falha não pode parecer sucesso, e a limpeza não deve esconder o resultado dos testes. Contêineres e rede temporários são removidos; imagens e cache de build permanecem para acelerar a próxima execução. Os dados da aplicação não fazem parte dessa limpeza.

### Relação com o CI

O job `test` do GitHub Actions executa o mesmo `bash scripts/test.sh`. O job `containers` verifica separadamente a inicialização da API completa e o smoke test por HTTP real.

**Por quê:** manter um único caminho para preparar a suíte reduz diferenças entre a máquina do avaliador e o CI. Separar o smoke test permite verificar também o empacotamento e o limite de corpo do Kestrel, que os testes HTTP em memória não reproduzem integralmente.

## Executar testes com SDK

Pré-requisitos: SDK .NET 10, Docker com Compose. Copie `.env.example` para `.env` antes de usar o Compose da aplicação. O comando `createdb` abaixo precisa ser executado apenas uma vez; se o banco já existir, continue para a aplicação dos scripts.

```sh
docker compose up -d db
docker compose exec db createdb -U ledger ledger_test
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger_test < src/Ledger.Api/schema.sql
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger_test < database/migrations/002-protect-journal.sql
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U ledger -d ledger_test < database/migrations/003-runtime-permissions.sql
export TEST_DATABASE='Host=localhost;Database=ledger_test;Username=ledger;Password=local-only'
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

A suíte falha se `TEST_DATABASE` não estiver configurada. Use um banco dedicado: os testes criam contas, logins e uma constraint temporária para simular falha. Não apontar para o banco da aplicação ou de produção.

## Executar a API com SDK

Após inicializar o banco pelo Compose, com a senha padrão do `.env.example`:

```sh
export ConnectionStrings__Ledger='Host=localhost;Database=ledger;Username=ledger_app;Password=local-runtime-password-change-me;Timeout=5;Command Timeout=10'
export ApiKey=local-development-key-change-me-123456
dotnet run --project src/Ledger.Api --urls http://localhost:8080
```

Se a senha tiver sido alterada, ajustar a configuração. Se a API já estiver em contêiner na porta 8080, interrompê-la antes ou escolher outra porta.

## Smoke test HTTP real

Com a API em execução:

```sh
API_KEY=local-development-key-change-me-123456 bash scripts/smoke.sh
```

Requer Bash, curl e Python 3. Cria uma conta exclusiva e verifica crédito, repetição idempotente, saldo e recusa de corpo acima de 16 KiB. Pode usar `BASE_URL` para outra porta. Esse script escreve no banco da API indicada; o comando principal `scripts/test.sh` não depende dele e usa seu próprio banco isolado.
