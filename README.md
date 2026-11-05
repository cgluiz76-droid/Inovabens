# Inovabens — Gestão Imobiliária e Financeira

Projeto Controle de Projetos e Processos da Inovabens.

Sistema web para a Inovabens (investimento imobiliário): ficha dos imóveis, custo de aquisição (CAPEX) e despesas recorrentes (OPEX) com checklist mensal, retorno do investimento corrigido por índice (IPCA/INCC/IGP-M/Selic), memorandos, agenda com Google Calendar, alertas, CRM em kanban e controle de acesso por perfil.

## Arquitetura

| Camada | Tecnologia |
|---|---|
| Aplicação | C# / .NET 10, Blazor Server (telas e regras no servidor, sem API HTTP intermediária) |
| Banco | PostgreSQL 16 + EF Core (Npgsql); esquema em scripts SQL versionados em `dotnet/src/Inovabens.Core/Dados/Migracoes` |
| Arquivos | Google Drive (drive compartilhado do Workspace): uma pasta por imóvel com as subpastas 01 a 06 |
| Relatórios | PDF (QuestPDF) e Excel (ClosedXML) no servidor |
| Testes | xUnit + Testcontainers (PostgreSQL real) e Playwright (navegador) |

Detalhes de código, configuração e testes: **[dotnet/README.md](dotnet/README.md)**.

### Regras de negócio garantidas no banco (não só na tela)
- `Total de aquisição = valor pago + impostos + taxas + despesas diversas`: coluna gerada; a tela nunca a envia. Itens detalhados recalculam os componentes por trigger.
- Despesa **Não Paga exige justificativa** (≥ 10 caracteres): validada no serviço e por `CHECK` no banco.
- Um alerta aberto por compromisso vencido; um por despesa vencida e usuário (índices únicos parciais).
- Auditoria (`audit_log`) append-only: triggers bloqueiam UPDATE/DELETE.
- Soma dos proprietários vigentes = 100%.

### Retorno do investimento
`custo total = aquisição + despesas pagas` · `receita líquida = bruto − custos − tributos` · ROI, **ROI real** (custo corrigido pelo índice entre a data de cada saída e a data final), **MOIC**, **TIR anual** (XIRR) e prazo. Despesas **Não Pagas** ficam como *passivo pendente*, fora do custo.

## Rodando localmente

### Opção A — tudo em contêineres (mais simples)
Requer Docker Desktop. Na pasta do projeto:
```bash
docker compose up --build
```
Abra http://localhost:5051 e entre com `admin@inovabens.com.br` / `Trocar@123456` (troque em *Minha conta*). Sem a configuração do Google Drive o envio de fotos/documentos fica **desligado** (não é necessário para testar o restante). Para ligá-lo, crie um arquivo `.env` ao lado do `docker-compose.yml` com o ID do drive compartilhado e o conteúdo da chave JSON da conta de serviço (veja *Fotos e documentos no Google Drive* abaixo):
```
GOOGLE_DRIVE_ID=0A...
GOOGLE_DRIVE_CREDENCIAIS_JSON={"type":"service_account",...}
```
Com `MAGNIFIC_API_KEY=...` no mesmo `.env`, as fotos enviadas são melhoradas automaticamente pelo Magnific.
Com as variáveis `R2_*`, as fotos marcadas como públicas são copiadas para o Cloudflare R2 (veja *Fotos públicas* abaixo).

Com `dotnet run` (opção B), no PowerShell, antes do `dotnet run`:
```powershell
$env:GOOGLE_DRIVE_ID="0A..."; $env:GOOGLE_DRIVE_CREDENCIAIS="C:\caminho\da\chave.json"
$env:MAGNIFIC_API_KEY="sua-chave"   # opcional: melhoria automática das fotos
```

### Fotos e documentos no Google Drive
Os arquivos ficam num **drive compartilhado** do Google Workspace, acessado por uma **conta de serviço** do Google Cloud
(com a Google Drive API ativada) que é membro do drive como **Administrador de conteúdo**. Conta de serviço não tem
espaço próprio no Drive, por isso precisa ser um drive compartilhado.

- Cada imóvel tem o campo **Nome para Drive do Empreendimento**: ao cadastrar, o sistema cria a pasta com esse nome na
  raiz do drive e, dentro dela, `01 - Matrícula`, `02 - Projetos`, `03 - Comercial`, `04 - Fotos e Vídeos`,
  `05 - Documentos em Geral` e `06 - Protocolos`. Se a pasta já existir com esse nome, ela é reaproveitada.
  Mudar o nome no cadastro renomeia a pasta.
- No envio de arquivos, escolhe-se a subpasta de destino. Só imagens e vídeos em `04 - Fotos e Vídeos` entram na
  galeria do imóvel (capa, limite de fotos, site); nas outras pastas tudo é documento.
- O Drive é privado: as telas mostram os arquivos pelo próprio sistema (`/imoveis/midias/...`), que confere o acesso.
- Excluir um arquivo no sistema manda para a **lixeira** do Drive (recuperável por 30 dias).

### Fotos públicas no Cloudflare R2 (site e portais)
Ao enviar para `04 - Fotos e Vídeos`, a caixa **Fotos públicas** decide se a foto vai para o site (padrão: desmarcada).
Cada foto também tem o botão **Tornar pública / Tornar privada**. Só as fotos públicas ganham cópia no bucket R2; as
demais ficam apenas no Drive.

- A rotina de publicação roda a cada minuto. Gera três versões WebP (400, 1200 e 1920 px de largura, sem ampliar e sem
  dados EXIF/GPS) com o nome `imoveis/{imóvel}/{foto}-{resumo}-{largura}.webp`.
- Foto trocada (ex.: melhorada pelo Magnific) ganha endereço novo e a cópia antiga é apagada. Foto desmarcada, excluída
  ou de imóvel excluído sai do R2.
- Variáveis: `R2_ENDPOINT`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET` (padrão `inovabens-site`) e
  `R2_URL_PUBLICA` (domínio público do bucket, ex.: `https://fotos.inovabens.com.br`). Em vez das três primeiras,
  `R2_CREDENCIAIS` pode apontar para o texto copiado do painel da Cloudflare (rótulo numa linha, valor na seguinte).
- Sem R2 configurado, a marcação fica guardada e as fotos são publicadas quando ele for ligado.

### Opção B — desenvolvimento (Visual Studio / `dotnet run`)
Requer o SDK do .NET 10 e um PostgreSQL 16 (o compose fornece um: `docker compose up -d db`).
```bash
cd dotnet/src/Inovabens.Web
dotnet run                       # http://localhost:5051
```
No Visual Studio: abra `dotnet/Inovabens.slnx`, defina **Inovabens.Web** como projeto de inicialização e pressione F5. No Windows, `EXECUTE.BAT` faz o mesmo e abre o Chrome. A aplicação cria as tabelas e o administrador inicial na primeira subida.

**O que conferir:** (1) login; (2) *Imóveis → Novo imóvel*; (3) aba *Financeiro*: lance a aquisição e um item de imposto e veja o total somar; cadastre uma despesa e marque "Não pago" (a justificativa é obrigatória); (4) lance uma receita de venda e veja ROI/TIR; (5) *Agenda*: crie um compromisso no passado e aguarde até 5 minutos para o alerta (sino no topo); (6) *CRM*: arraste um lead entre as colunas; (7) baixe a Ficha do Imóvel e a Ficha Financeira (PDF/Excel).

## Testes
```bash
cd dotnet
dotnet test                                                   # unidade + integração + navegador (requer o Docker)
dotnet test --filter "Categoria!=Integracao&Categoria!=E2E"   # só os de unidade, sem Docker
```

## Publicação em nuvem
Veja **[DEPLOY.md](DEPLOY.md)** (Render com `render.yaml`, Google Cloud Run ou qualquer host de contêineres).

> A primeira versão do sistema foi escrita em Node.js (NestJS + Next.js) e foi aposentada após a migração para .NET.
> O código dela continua no histórico do git; bancos criados por ela são reconhecidos e atualizados pela versão .NET.
