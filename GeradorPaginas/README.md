# GeradorPaginas

Ferramenta local, de uso pessoal do administrador, para gerar trechos de HTML/CSS com IA e colar na página do imóvel
no Inovabens (imóvel → aba **Página** → **Página personalizada (HTML e CSS)**).

A IA é chamada **somente** pelo Claude Code CLI (`claude -p`), com o login da sua assinatura Claude (Pro). Nenhuma chave
de API é usada.

## Pré-requisitos

- **.NET 8 SDK** ou mais novo. Com só o .NET 10 instalado, também funciona: o projeto aceita rodar no runtime mais novo.
- **Claude Code** instalado e **logado com a conta da assinatura** no mesmo usuário do Windows que vai rodar a ferramenta.
  Instalação: https://docs.claude.com/pt/docs/claude-code (depois de instalar, `claude --version` precisa funcionar no terminal).

## Como rodar

```powershell
cd C:\Claude\inovabens\GeradorPaginas
dotnet run
```

Abra http://localhost:5080 no navegador. A ferramenta escuta **somente** em localhost (não aparece na rede).
Para encerrar, tecle `Ctrl+C` no terminal.

> Não hospede no IIS nem como serviço: o login do Claude Code fica no perfil do seu usuário do Windows, e o pool do IIS
> (ou uma conta de serviço) não tem acesso a ele.

## Buscar o imóvel pelo código (opcional)

Para gerar a página só com o código (ex.: `CA0060`), a ferramenta busca os dados **públicos** do imóvel no Inovabens
(sem proprietário, sem valores internos; endereço exato só se for público; fotos só as publicadas).

1. No Inovabens: **Administração → Integrações → GeradorPaginas**, crie uma chave longa e aleatória (40 caracteres ou mais)
   e salve.
2. Aqui, copie `appsettings.Local.exemplo.json` para `appsettings.Local.json` e cole a mesma chave em `Inovabens:Chave`.
   Esse arquivo não vai para o Git (está no `.gitignore`).

Sem a chave, a ferramenta funciona com descrições livres; com um código, avisa que não conseguiu buscar o imóvel.

## "Login necessário"

A tela aparece quando alguma verificação falha (o `claude` não está no PATH, ou não está logado pela assinatura). Para resolver:

1. Abra um terminal (PowerShell) com o mesmo usuário do Windows.
2. Rode `claude`.
3. Dentro do Claude Code, digite `/login` e escolha a conta da **assinatura Claude** (não "API key"); conclua no navegador.
4. Saia com `/exit` e clique em **Verificar novamente** na ferramenta.

Para conferir pelo terminal: `claude auth status --text` deve mostrar o login pela conta Claude (claude.ai).

## Aviso: variável ANTHROPIC_API_KEY

Se a variável de ambiente `ANTHROPIC_API_KEY` (ou `ANTHROPIC_AUTH_TOKEN`) estiver definida, o Claude Code usa a chave
e o uso passa a ser **cobrado pela API**, fora da assinatura. Por isso a ferramenta:

- remove a variável do próprio processo ao iniciar e **nunca** a repassa ao `claude` (nem nenhuma outra `ANTHROPIC_*` ou `CLAUDE_*`);
- mostra um aviso na tela enquanto a variável existir no Windows.

Para apagá-la de vez: Configurações do Windows → Sistema → Sobre → Configurações avançadas do sistema → Variáveis de
Ambiente → remova `ANTHROPIC_API_KEY` (em "Variáveis de usuário" e em "Variáveis do sistema") e abra um terminal novo.

## Como usar

- **Descrição**: o que você quer (uma página inteira, uma faixa, um card…) ou só o código do imóvel.
- **Paleta / estilo**: texto livre (ex.: "azul marinho e dourado, moderno").
- **Prefixo das classes** (padrão `inb-`): todas as classes começam com ele e o CSS fica preso ao container `.inb-root`,
  para não mexer no resto do site.
- **Formato**: HTML único com `<style>` embutido, ou HTML e CSS separados.
- **Resultado**: pré-visualização (sem scripts), abas HTML/CSS, botões de copiar e baixar, e **Refinar** para pedir ajustes.
- **Histórico**: cada geração fica em `historico\AAAA-MM-DD_HHMMSS\` (`pedido.txt`, `pagina.html`, `estilo.css` e
  `dados.json`) e pode ser reaberta pela lista na tela.

O código gerado não tem JavaScript. Mesmo assim, ao salvar no Inovabens, script, eventos (`onclick`…), formulários e
iframes são removidos.

## Configuração (`appsettings.json`)

- `Gerador:TimeoutSegundos`: tempo máximo de cada chamada ao Claude (padrão 180).
- `Gerador:Modelo`: modelo do Claude Code (vazio = o padrão da sua assinatura).
- `Inovabens:Url`: endereço do Inovabens (padrão https://inovabens.fly.dev).
