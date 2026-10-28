# Contrato da API — Inovabens (v1)

Base: `/api/v1` · JSON · datas ISO-8601 (`YYYY-MM-DD` para datas, `…Z` para instantes) · valores monetários como **string decimal** (`"1234.50"`) · campos em `snake_case`.
O front (Next.js) fala com a API por **mesma origem**: `next.config` faz rewrite de `/api/v1/*` → `API_INTERNAL_URL`. Assim o cookie de refresh é first-party.

## Convenções
- **Auth:** `Authorization: Bearer <access_token>` (JWT curto, 15 min). Refresh token rotativo em cookie `httpOnly` `refresh_token` (path `/api/v1/auth`).
- **Listas:** `?page=1&pageSize=20` → `{ data: T[], total, page, pageSize }`. Ordenação `?sort=campo:asc|desc` quando aplicável.
- **Erros** (RFC 7807 simplificado): `{ status, title, detail?, errors?: { campo: string[] } }`. `401` não autenticado · `403` sem permissão · `404` · `409` conflito · `422` validação/regra de negócio.
- **Permissões:** cada rota exige `recurso:ação` (catálogo em `@inovabens/shared`). Perfis com `escopo_proprio=true` (Corretor) só enxergam imóveis onde `corretor_responsavel_id = eu`, e leads/compromissos em que são responsáveis.
- **Auditoria:** toda escrita em financeiro (e CRUD de imóveis/usuários) grava `audit_log` (antes/depois).
- **Exclusão:** lógica (`excluido_em`) nas entidades de negócio.

## Auth
| Rota | Corpo → Resposta |
|---|---|
| `POST /auth/login` | `{ email, senha, totp? }` → `LoginResposta` (+ set-cookie refresh). Se o usuário tem 2FA e falta `totp`: `401 {title:"TOTP_REQUIRED"}`. Bloqueio progressivo após 5 falhas. |
| `POST /auth/refresh` | (cookie) → `LoginResposta` (rotaciona o refresh) |
| `POST /auth/logout` | revoga refresh, limpa cookie → `204` |
| `GET /auth/me` | → `UsuarioLogado` |
| `POST /auth/alterar-senha` | `{ senha_atual, nova_senha }` → `204` |
| `POST /auth/forgot-password` · `/auth/reset-password` | `{email}` / `{token, nova_senha}` (token por e-mail; sem SMTP configurado só loga) |
| `POST /auth/2fa/setup` · `/auth/2fa/enable` · `/auth/2fa/disable` | setup → `{ otpauth_url, secret }`; enable `{codigo}` |

## Administração
| Rota | Perm. |
|---|---|
| `GET /permissoes` → `[{id,codigo,descricao}]` | `rbac:gerir` |
| `GET/POST /roles`, `GET/PATCH/DELETE /roles/:id` — `{nome, descricao, escopo_proprio, permissoes: string[]}` | `rbac:gerir` |
| `GET/POST /users`, `GET/PATCH /users/:id`, `PATCH /users/:id/status {ativo}` — `{nome,email,senha?,perfil_id,ativo}` | `usuarios:gerir` |
| `GET /users/options` → `[{id,nome}]` (corretores ativos, p/ selects) | autenticada |
| `GET/POST/PATCH /categorias-despesa` `{nome, ativo}` | leitura: `financeiro:ver`; escrita: `admin:config` |
| `GET /auditoria?entidade=&entidade_id=&page=` | `auditoria:ver` |

## Pessoas (clientes, proprietários, envolvidos)
`GET /pessoas?q=&papel=&page=` · `POST /pessoas` · `GET/PATCH/DELETE /pessoas/:id`
Corpo/resposta: `{ id, tipo, nome_razao, cpf_cnpj, rg, email, telefone, whatsapp, cep, logradouro, numero, complemento, bairro, cidade, uf, observacoes, lgpd_consentimento_em, papeis: [{papel, especialidade}] }`
Permissões `pessoas:ver|criar|editar|excluir`.

## Imóveis
| Rota | Perm. |
|---|---|
| `GET /imoveis?q=&situacao=&tipo=&finalidade=&cidade=&corretor_id=&page=` → lista resumida `{id,codigo,titulo,tipo,situacao,finalidade,cidade,bairro,uf,preco_venda,preco_locacao,corretor:{id,nome}|null,capa_url|null}` | `imoveis:ver` |
| `POST /imoveis` · `GET/PATCH/DELETE /imoveis/:id` — `GET` devolve todos os campos da tabela `imoveis` + `caracteristicas[]`, `midias[]` (com `url` assinada), `proprietarios[]` (com `pessoa`), `envolvidos[]` (com `pessoa`), `corretor` | `imoveis:ver/criar/editar/excluir` |
| `PUT /imoveis/:id/caracteristicas` — `[{chave,valor,publico}]` (substitui o conjunto) | `imoveis:editar` |
| `PUT /imoveis/:id/proprietarios` — `[{pessoa_id,percentual,principal,inicio_em?,fim_em?}]` (soma dos vigentes = 100, senão `422`) | `imoveis:editar` |
| `PUT /imoveis/:id/envolvidos` — `[{pessoa_id,papel,observacao}]` | `imoveis:editar` |
| `POST /imoveis/:id/midias` (multipart `arquivo`, `tipo`, `legenda`, `publica`) · `PATCH /midias/:id {legenda,ordem,capa,publica}` · `DELETE /midias/:id` | `imoveis:editar` |
| `GET /imoveis/:id/ficha.pdf` · `/ficha.xlsx` (download) | `imoveis:exportar` |
| `GET/POST /imoveis/:id/regularizacoes` · `PATCH/DELETE /regularizacoes/:id` | `imoveis:ver` / `editar` |

## Financeiro do imóvel (`financeiro:*`)
| Rota | Notas |
|---|---|
| `GET /imoveis/:id/aquisicao` → `{ …campos, total_aquisicao, itens: [...] }` ou `404` | `financeiro:ver` |
| `PUT /imoveis/:id/aquisicao` `{data_aquisicao, valor_pago, impostos, taxas, despesas_diversas, forma_pagamento, observacao}` (cria/atualiza) | `financeiro:editar`; cliente **nunca** envia `total_aquisicao` |
| `POST /imoveis/:id/aquisicao/itens` · `PATCH/DELETE /aquisicao/itens/:id` `{tipo, descricao, valor, data_pagamento?}` | trigger recalcula totais |
| `GET/POST /imoveis/:id/despesas` · `PATCH/DELETE /despesas/:id` `{categoria_id, descricao, recorrencia, dia_vencimento?, valor_previsto, inicio_em, fim_em?, regularizacao_id?, ativa}` | |
| `GET /imoveis/:id/despesas/checklist?mes=YYYY-MM` → `{ mes, resumo:{pago,nao_pago,nao_se_aplica,a_marcar}, itens:[{despesa_id, lancamento_id, descricao, categoria, vencimento, valor, status|null, data_pagamento, justificativa}] }` — gera sob demanda os lançamentos do mês das despesas ativas | |
| `PATCH /despesas/lancamentos/:id/status` `{status, justificativa?, data_pagamento?, valor?}` | `NAO_PAGO` sem justificativa ⇒ `422 {errors:{justificativa:[…]}}` |
| `GET/POST /imoveis/:id/receitas` · `PATCH/DELETE /receitas/:id` `{tipo,data,valor_bruto,custos_venda,tributos,contraparte_id?,observacao}` | |
| `GET /imoveis/:id/retorno?indice=IPCA&ate=YYYY-MM-DD` → `{ imovel_id, ate, indice, custo_total, passivo_pendente, custo_corrigido, receita_liquida, resultado, roi, roi_real, tir_anual, moic, prazo_dias, fluxo:[{data, tipo:'SAIDA'|'ENTRADA', descricao, valor}] }` (percentuais em fração: `0.153` = 15,3%) | |
| `GET /indices/:codigo/serie?de=&ate=` · `POST /indices/sync` (admin:config; busca BCB-SGS) | |
| `GET /imoveis/:id/ficha-financeira.pdf` · `.xlsx` | `financeiro:exportar` |

## Memorandos
`GET/POST /imoveis/:id/memorandos` · `PATCH/DELETE /memorandos/:id` — `{tipo,titulo,corpo,fixado}` + `autor:{id,nome}`, `criado_em`. Perm. `memorandos:*`.

## Agenda, Google e alertas
| Rota | Notas |
|---|---|
| `GET /compromissos?de=&ate=&imovel_id=&responsavel_id=&status=` (agenda geral) · `GET/POST /imoveis/:id/compromissos` · `PATCH/DELETE /compromissos/:id` · `POST /compromissos/:id/concluir` | `{tipo,titulo,descricao,local,inicio_em,fim_em,status,lead_id?,participantes:[{email}]}` + `imovel:{id,codigo,titulo}`, `responsavel:{id,nome}`. Escrever sincroniza com o Google se o usuário conectou. |
| `GET /integracoes/google/status` → `{conectado, google_email, ultima_sync_em}` · `GET /integracoes/google/auth-url` → `{url}` · `GET /integracoes/google/callback?code&state` (redireciona p/ `/agenda?google=ok`) · `POST /integracoes/google/sync` · `DELETE /integracoes/google` · `POST /webhooks/google/calendar` (público, valida headers do canal) | sync bidirecional por `syncToken`; conflito: vence `updated` mais recente |
| `GET /alertas?status=aberto|todos` · `GET /alertas/contagem` → `{abertos}` · `PATCH /alertas/:id/lido` · `PATCH /alertas/:id/reagendar {inicio_em, fim_em}` | alerta `{id,tipo,mensagem,gerado_em,lido_em,resolvido_em,compromisso?:{id,titulo,fim_em,imovel_id}}` |

Job (a cada 5 min): compromissos `PENDENTE` com `fim_em < now()` ⇒ cria 1 alerta aberto por compromisso; resolve ao concluir/cancelar/reagendar. Job diário: gera lançamentos do mês e alerta `DESPESA_NAO_PAGA` p/ lançamentos vencidos marcados `NAO_PAGO`.

## CRM
| Rota | Notas |
|---|---|
| `GET /crm/pipeline?funil=VENDA|LOCACAO` → `{ funil, estagios:[{id,nome,ordem,cor,tipo,leads:[{id,pessoa:{id,nome_razao,telefone,email},finalidade,origem,responsavel:{id,nome}|null,orcamento_max,proxima_acao_em,ordem}]}] }` | `crm:ver` |
| `GET/POST /leads` · `GET/PATCH/DELETE /leads/:id` | `POST` aceita `pessoa_id` **ou** `pessoa:{nome_razao,telefone,email}` (cria Pessoa com papel CLIENTE). Estágio default = 1º do funil. |
| `PATCH /leads/:id/estagio` `{estagio_id, ordem?, motivo_perda?}` | grava `lead_historico_estagio`; estágio `PERDIDO` exige `motivo_perda` |
| `GET/POST /leads/:id/interacoes` `{canal,resumo}` · `GET/PUT /leads/:id/imoveis-interesse` `[imovel_id]` · `GET /leads/:id/historico` | |
| `GET/POST/PATCH/DELETE /crm/estagios` `{funil,nome,ordem,cor,tipo,ativo}` · `PUT /crm/estagios/ordem` `[{id,ordem}]` | `admin:config` (leitura `crm:ver`) |

## Dashboard
`GET /dashboard` → `{ imoveis_por_situacao:[{situacao,total}], leads_por_estagio:[{estagio,total,cor}], alertas_abertos, lancamentos_nao_pagos, proximos_compromissos:[…5] }` (respeita escopo do perfil).

## Saúde
`GET /health` → `{status:"ok", db:"up"}` (sem auth; usado pelo health check da nuvem). Swagger em `/api/docs`.
