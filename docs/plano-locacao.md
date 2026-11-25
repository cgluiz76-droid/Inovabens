# Plano de implantação — Locação

Versão 2 · 25/11/2026 · com as decisões do usuário (seção 7).

## 1. Resumo

O sistema hoje cobre a venda de ponta a ponta. Para locação já existem peças soltas:

- lead e proposta de locação no CRM (aluguel, garantia, prazo, índice, carência);
- situação "Locado" no imóvel;
- índices IGP-M/IPCA atualizados pelo Banco Central;
- financeiro com recorrência e conciliação por extrato OFX;
- agenda com Google Agenda, controle de chaves, modelos Word e pastas no Drive.

O que falta é o coração da administração de aluguéis: **contrato de locação** e **cobrança mensal com recibo**, depois **repasse ao proprietário**, **reajuste**, **vistoria**, **manutenção agendada**, **rescisão**, e os **relatórios e obrigações fiscais**.

A proposta é implantar em 7 fases. Cada fase entrega algo que já pode ser usado. As fases 1 e 2 juntas já permitem administrar aluguéis com recibo e extrato do proprietário, no mesmo nível do Imobibrasil. A partir da fase 3, o sistema passa a fazer o que o Imobibrasil não faz.

## 2. Ponto central: imóvel locado e à venda ao mesmo tempo

Hoje a situação do imóvel é um campo só. "Locado" e "Vendido" são exclusivos, e o site só mostra imóveis "Disponíveis". Proposta:

- **Separar ocupação de disponibilidade comercial.**
  - Situação continua sendo o ciclo de vida: captação, aquisição, regularização, retrofit, ativo, vendido, arquivado.
  - Duas marcas independentes: **à venda** e **para alugar**.
  - A **ocupação** passa a ser calculada pelo contrato ativo: vago, locado ou em desocupação.
  - Assim um imóvel pode estar "locado + à venda".
  - O site passa a mostrar "à venda · locado". Para investidor, a renda atual do aluguel é um atrativo e pode aparecer no anúncio, se você quiser.
- **Regras da Lei 8.245/91 que o sistema passa a lembrar na venda de imóvel locado:**
  - **Direito de preferência (art. 27):** antes de fechar a venda com terceiro, o locatário precisa ser notificado da proposta nas mesmas condições. Ele tem 30 dias para aceitar.
    - O sistema gera a notificação (PDF/WhatsApp), conta o prazo e bloqueia a "venda concluída" até o prazo vencer ou haver renúncia registrada.
    - Locatário preterido pode pedir perdas e danos. Ou, com contrato averbado, pode depositar o preço e haver o imóvel para si em 6 meses (art. 33).
  - **Cláusula de vigência / averbação (art. 8):** o contrato registra se tem cláusula de vigência e se foi averbado na matrícula.
    - Com cláusula e averbação, o comprador precisa respeitar o contrato.
    - Sem elas, o comprador pode pedir a desocupação em 90 dias.
    - A proposta de venda mostra essa informação ao corretor.
  - **Visitas (art. 23, IX):** visita de interessado em imóvel locado exige dia e hora combinados com o inquilino. A visita agendada no CRM/Agenda para imóvel locado envia aviso ao inquilino.

## 3. O que o mercado oferece e o que o Imobibrasil tem

| Função | Mercado (Superlógica, Loft, Vista, Alterdata, Group) | Imobibrasil (Gestão de Aluguéis) | Proposta Inovabens |
|---|---|---|---|
| Contrato (residencial, comercial, temporada) | sim | sim, 9 etapas, vários locatários, garantias | sim (fase 1) |
| Boleto/Pix com baixa automática | sim, com banco integrado | **não** emite boleto nem Pix; baixa manual | baixa manual + conciliação OFX (fase 2); boleto/Pix integrado (fase 7) |
| Régua de cobrança | sim | sim, e-mail e WhatsApp, 6 modelos | sim, WhatsApp/e-mail (fase 2) |
| Multa e juros por atraso | sim | sim | sim (fase 2) |
| Reajuste por índice | automático | índice só registrado; **aplicação manual** | automático com aviso e carta (fase 3) |
| Repasse e taxa de administração | sim, inclusive repasse garantido | sim, rateio entre proprietários, intermediação no 1º mês | sim (fase 2) |
| Recibo discriminado / extrato do proprietário | sim | faturas e extrato de repasses | sim (fase 2 e fase 6) |
| Rescisão com cálculo | sim (alguns) | **não**; só cancela o contrato | sim, com multa proporcional e acerto (fase 3) |
| Vistoria com laudo e fotos | parcial / apps à parte | **não** há módulo | sim, entrada × saída (fase 4) |
| Manutenção com prestador, orçamento e agenda | chamados (Vista), raro com prestador | chamados simples, sem orçamento nem prestador | chamado → orçamento → aprovação → agenda → custo no repasse (fase 5) |
| Portal do inquilino/proprietário | sim | sim (área privada) | link seguro sem senha (fase 7) |
| DIMOB | gera arquivo | só "ajuda a organizar" | gera o arquivo (fase 6) |
| Informe de rendimentos | sim | sim | sim (fase 6) |
| Assinatura eletrônica | parcial | não mencionada | opcional (fase 7) |
| Limite de contratos | conforme plano | 5 / 30 / 90 por plano | sem limite |

Fontes do Imobibrasil: página do produto, central de ajuda (régua, configurações, chamados) e planos. Links no fim do documento.

## 4. Regras legais e fiscais que o sistema precisa respeitar

- **Recibo discriminado (art. 22, VI):** cada valor pago em linha própria (aluguel, condomínio, IPTU, seguro, multa, juros), com mês de referência. Quitação genérica é vedada.
- **Garantia única (art. 37, parágrafo único):** só uma garantia por contrato: caução, fiança, seguro-fiança ou título de capitalização. A caução em dinheiro é de até 3 aluguéis (art. 38).
- **Multa rescisória proporcional (art. 4):** o inquilino que sai antes do prazo paga a multa contratual proporcional ao tempo que falta.
- **Encargos:**
  - O inquilino paga as despesas ordinárias de condomínio (art. 23, XII).
  - O proprietário paga as extraordinárias, como obras, fundo de reserva e pintura de fachada (art. 22, X).
  - O sistema sugere de quem é cada despesa e cada chamado de manutenção.
- **Vistoria (art. 22, V e art. 23, III):** descrição do estado do imóvel na entrega. O inquilino devolve no mesmo estado, salvo desgaste normal.
- **Reajuste:** periodicidade mínima anual (Lei 10.192/2001). A revisional judicial é possível após 3 anos (art. 19). Locação comercial pode ter renovatória (art. 51).
- **Imposto de renda:**
  - Inquilino PJ que paga a proprietário PF retém IR na fonte pela tabela progressiva. O contrato registra isso, o repasse desconta e o informe de rendimentos mostra.
  - Inquilino PF: o proprietário recolhe via carnê-leão. O extrato mensal traz o valor base.
  - A taxa de administração é dedutível para o proprietário PF.
- **DIMOB:** obrigatória para a imobiliária/administradora (PJ). É entregue até o último dia útil de fevereiro com os dados do ano anterior. Para 2026, o prazo é 26/02/2027. Multa por atraso ou erro.
- **Reforma tributária (LC 214/2025):**
  - Locação passa a ter IBS/CBS com redutor de 70%, e redutor social de R$ 600/mês por imóvel residencial.
  - Locador PF só é contribuinte com mais de 3 imóveis locados **e** receita acima de R$ 240 mil/ano.
  - Calendário de transição a partir de 2026/2027; as fontes divergem sobre o início para PF.
  - O sistema já guarda o regime do locador (PF/PJ) e separa a base de cálculo, para não refazer depois.
  - **Confirmar com o contador.**
- **NFS-e:** a taxa de administração e a intermediação são receita da imobiliária, com nota fiscal de serviço. Entra no financeiro da empresa que já existe.

## 5. Fases

### Fase 1 — Contrato de locação e ocupação
- **Contrato:**
  - Imóvel e locadores (vêm dos proprietários do imóvel, com percentual).
  - Locatários (um ou mais) e fiadores.
  - Tipo: residencial, comercial ou temporada.
  - Início, prazo e dia de vencimento.
  - Aluguel, índice e mês de reajuste.
  - Encargos cobrados junto: condomínio, IPTU, seguro incêndio.
  - Taxa de administração (%), taxa de intermediação, dia do repasse e repasse garantido (sim/não).
  - Multa por atraso (%), juros (% a.m.) e multa rescisória (nº de aluguéis).
  - Garantia: tipo, valor, apólice/título, vencimento.
  - Cláusula de vigência / averbação.
- **Situações do contrato:** rascunho, ativo, em rescisão, encerrado, cancelado. Histórico e auditoria em tudo.
- **Proposta de locação aceita no CRM** cria o rascunho do contrato já preenchido.
- **Documento do contrato:** gerado pelos modelos Word (marcadores `locacao.*` já existem; ampliar).
- **Drive:** nova subpasta "08 - Locação" no imóvel (contrato, garantias, vistorias).
- **Imóvel:** marcas "à venda" e "para alugar" + ocupação calculada (seção 2); site e filtros ajustados.
- **Permissões novas:** `locacao:ver`, `locacao:editar`, `locacao:financeiro`, `locacao:rescindir`.
- **Pessoas:** papéis locatário, fiador e prestador de serviço.

### Fase 2 — Cobrança, recibo e repasse (o "dia a dia")
- **Fatura mensal por contrato:** gerada automaticamente X dias antes do vencimento, com itens discriminados. Na primeira e na última, aluguel pró-rata.
- **Pagamento:**
  - baixa manual ou pela **conciliação OFX** que já existe;
  - multa e juros calculados pela data do pagamento;
  - pagamento parcial e acordo (parcelamento de atraso).
- **Recibo discriminado** em PDF (art. 22, VI), enviado por WhatsApp/e-mail.
- **Régua de cobrança:** lembrete antes, no dia e após o vencimento, por WhatsApp (serviço já existente) e e-mail, com modelos editáveis.
- **Repasse ao proprietário:**
  - **Cálculo:** recebido − taxa de administração − despesas do proprietário (manutenção, IPTU se for dele, IR retido).
  - **Proprietários:** rateio pelo percentual de cada um.
  - **Saldo:** o saldo devedor do proprietário fica registrado quando as despesas passam do aluguel.
  - **Entrega:** extrato mensal do proprietário.
  - **Repasses a fazer:** aparecem como contas a pagar no financeiro da imobiliária, com conferência e comprovante.
- **Contabilidade correta:**
  - A taxa de administração e a intermediação entram como **receita da imobiliária**.
  - O aluguel é **dinheiro de terceiros em trânsito**, com controle de saldo por proprietário, separado da receita da empresa.
  - No imóvel próprio ou de sócio, o aluguel entra no financeiro do imóvel e no cálculo de retorno que já existe (yield real).

### Fase 3 — Reajuste, renovação e rescisão
- **Reajuste:**
  - aviso 60 e 30 dias antes do aniversário;
  - cálculo do índice acumulado em 12 meses pelo serviço de índices existente, com regra para índice negativo (manter o valor);
  - carta de reajuste em PDF e aplicação em um clique;
  - histórico de valores.
- **Renovação/aditivo:** novo prazo e novo valor, mantendo o histórico do contrato.
- **Vencimentos:** alertas do contrato, da garantia, do seguro-fiança e do seguro incêndio, no sino e na agenda.
- **Rescisão:**
  - aviso prévio;
  - cálculo da multa proporcional (art. 4) e do aluguel pró-rata até a entrega;
  - encargos pendentes;
  - débitos da vistoria de saída;
  - devolução da caução corrigida;
  - termo de entrega de chaves, integrado ao **controle de chaves** que já existe.
  - O imóvel volta para "vago" e, se marcado, "para alugar" no site.
- **Notificação de preferência (art. 27)** na venda de imóvel locado, com contagem dos 30 dias.

### Fase 4 — Vistorias
- **Tipos:** entrada, saída e periódica.
- **Conteúdo:** cômodo por cômodo, itens com estado (novo, bom, regular, ruim), observação e **fotos pelo celular** (Drive), além de leituras de água, luz e gás e chaves entregues.
- **Laudo:** PDF com fotos; aceite/assinatura do inquilino; prazo para contestação.
- **Comparativo saída × entrada:** gera a lista de reparos e os débitos da rescisão.

### Fase 5 — Manutenção com agendamento
- **Chamado:**
  - aberto pela equipe ou pelo inquilino (link ou WhatsApp);
  - categoria (hidráulica, elétrica etc.), urgência e fotos;
  - responsável sugerido pela lei (locador ou locatário).
- **Orçamentos de prestadores** cadastrados (Pessoas com papel "prestador").
- **Aprovação do proprietário:** por link (aprovar ou recusar) e com limite de valor pré-aprovado.
- **Agendamento:**
  - na **Agenda** (Google Agenda), com prestador e inquilino avisados por WhatsApp;
  - retirada e devolução de chaves no **controle de chaves**;
  - lembrete na véspera.
- **Conclusão:** fotos do serviço, nota/recibo do prestador e avaliação.
- **Custo:**
  - lançado automaticamente: desconta no repasse (locador), entra na próxima fatura (locatário) ou é despesa da imobiliária;
  - pagamento ao prestador no financeiro.
- **Manutenção preventiva recorrente** por imóvel (ex.: limpeza de caixa d'água, dedetização, AVCB, revisão de ar-condicionado), com agendamento automático.

### Fase 6 — Recibos, documentos e relatórios
- **Documentos:**
  - recibo de aluguel discriminado;
  - 2ª via de fatura;
  - extrato mensal do proprietário;
  - carta de cobrança;
  - notificação extrajudicial de atraso;
  - carta de reajuste;
  - notificação de preferência (art. 27);
  - laudo de vistoria;
  - termo de rescisão e de entrega de chaves;
  - declaração de quitação anual de aluguéis.
- **Anuais:**
  - **informe de rendimentos do proprietário**: aluguéis brutos, taxa de administração, IR retido, IPTU e condomínio pagos;
  - **comprovante de pagamentos ao inquilino** (para o IR dele);
  - **arquivo da DIMOB** no layout da Receita.
- **Relatórios gerenciais (Excel e PDF):**
  - inadimplência por idade da dívida;
  - contratos a vencer e reajustes do mês;
  - repasses pendentes e realizados;
  - receita da administração (taxas e intermediação);
  - vacância e tempo médio para alugar;
  - garantias e seguros a vencer;
  - chamados abertos e tempo de atendimento;
  - custo de manutenção por imóvel;
  - rentabilidade (yield) por imóvel;
  - carteira por proprietário.
- **Painel inicial com indicadores de locação:** inadimplência, vacância e repasses do mês.

### Fase 7 — Integrações e portal
- **Boleto/Pix com baixa automática por webhook**, e split da taxa de administração se o banco oferecer.
  - Opções a comparar: Asaas, Efí, Banco Inter, Sicoob.
  - A escolha depende do banco que a imobiliária já usa e das tarifas negociadas.
- **Área do inquilino e do proprietário por link seguro**, sem senha:
  - 2ª via, recibos, extratos, informe e abertura de chamado.
- **Assinatura eletrônica** do contrato, laudo e termos (opcional; avaliar custo por documento).
- **NFS-e automática** da taxa de administração (depende do município de São José do Rio Preto e do emissor).

## 6. Ordem, entregas e testes

| Fase | Entrega usável | Banco |
|---|---|---|
| 1 | Contrato + imóvel locado e à venda | migração nova (contratos, partes, garantias) |
| 2 | Fatura, recibo, régua, repasse, extrato do proprietário | migração (faturas, itens, repasses) |
| 3 | Reajuste automático, rescisão, preferência | ajustes |
| 4 | Vistoria com fotos e laudo | migração (vistorias, itens) |
| 5 | Chamados, orçamentos, agenda, preventiva | migração (chamados, orçamentos) |
| 6 | Informe, DIMOB, relatórios | sem mudança de estrutura |
| 7 | Boleto/Pix, portal, assinatura | integrações externas |

- Cada fase entra com testes de integração da própria funcionalidade. A suíte completa roda antes de cada deploy com migração.
- Cálculos sensíveis têm testes com exemplos conferidos à mão: pró-rata, multa e juros, multa rescisória, reajuste, repasse com rateio e IR retido.

## 7. Decisões tomadas (25/11/2026)

1. **Carteira:** as duas formas — imóveis de terceiros (Inova administra e repassa) e imóveis próprios/de sócios. O contrato
   registra a modalidade: administração de terceiros gera taxa de administração, repasse e entra na DIMOB; imóvel próprio
   lança o aluguel direto no financeiro do imóvel (retorno do investimento), sem repasse.
2. **Contratos existentes:** nenhum para importar. Volume esperado: 15 a 30 contratos em 12 meses (baixa manual de
   pagamentos com conciliação OFX atende bem nesse volume).
3. **Taxas padrão** (editáveis por contrato): administração 10%; intermediação = 1º aluguel; atraso com multa de 10% e
   juros de 1% ao mês; **sem repasse garantido** (repasse só depois do recebimento).
4. **Banco:** Cora. Integração de boleto/Pix fica para a fase 7.
5. **Temporada:** por último (fase 8).
6. **Vistoria terceirizada:** a fase 4 vira registro da vistoria — agendamento com a empresa de vistoria na Agenda,
   chaves, anexo do laudo (PDF) na pasta "08 - Locação", custo da vistoria e lista de pendências apontadas no laudo, que
   alimenta a rescisão e os chamados de manutenção. Sem laudo próprio com fotos.
7. **DIMOB:** feita pelo escritório contábil — o sistema gera a planilha/arquivo com os dados do ano para o escritório.
   **Nota fiscal:** fica para depois (fora das fases atuais).

**Ordem:** fases 1 e 2 juntas (contrato + cobrança/recibo/repasse), depois 3 (reajuste e rescisão), 5 (manutenção),
4 (vistoria terceirizada), 6 (relatórios, informe e dados da DIMOB), 7 (boleto/Pix Cora, portal) e 8 (temporada).

## 8. Fontes

- Imobibrasil — Gestão de Aluguéis: https://www.imobibrasil.com.br/gestao-de-alugueis
- Imobibrasil — central de ajuda (módulo de locações): https://ajuda.imobibrasil.com.br/central-ajuda/o-que-e-o-modulo-de-locacoes-e-para-quem-e-indicado/
- Imobibrasil — régua de cobrança: https://ajuda.imobibrasil.com.br/central-ajuda/regua-de-cobranca/
- Imobibrasil — chamados: https://ajuda.imobibrasil.com.br/central-ajuda/chamados-gestao-de-alugueis/
- Imobibrasil — planos: https://www.imobibrasil.com.br/planos
- Imobibrasil — blog sobre gestão de aluguel: https://www.imobibrasil.com.br/blog/melhor-sistema-de-gestao-de-aluguel-locacao-2026/
- Lei do Inquilinato (texto compilado): https://www.planalto.gov.br/ccivil_03/leis/l8245.htm
- Art. 27 (preferência): https://modeloinicial.com.br/lei/L-8245-1991/lei-inquilinato/art-27
- Averbação e cláusula de vigência: https://jus.com.br/artigos/99489/a-importancia-da-averbacao-do-contrato-de-locacao-do-imovel-no-rgi
- Recibo discriminado (art. 22): https://modeloinicial.com.br/lei/6351/lei-inquilinato/art-22
- Funcionalidades do mercado: https://www.groupsoftware.com.br/blog/administracao-de-imoveis/ · https://loft.com.br/para-imobiliarias/adm-imoveis/ · https://vistasoft.com.br/10-motivos-para-contratar-vista-office · https://ajuda.alterdata.com.br/loc/administradora-locacao-desktop
- Taxas de administração: https://www.quintoandar.com.br/guias/?p=2434760 · https://geracontratos.com.br/recursos/calculadora-taxa-administracao-aluguel
- DIMOB: https://www.gov.br/pt-br/servicos/declarar-atividades-imobiliarias · https://www.contabilizei.com.br/contabilidade-online/dimob-2026/
- IR sobre aluguel: https://www.contabeis.com.br/noticias/37823/retencao-de-irrf-no-pagamento-de-aluguel/ · https://www.infomoney.com.br/?p=3300208
- Reforma tributária e locação: https://ibdfam.org.br/artigos/2501/A+Loca%C3%A7%C3%A3o+de+Im%C3%B3veis+na+Reforma+Tribut%C3%A1ria · https://online.crcsp.org.br/portal/noticias/noticia.asp?c=11008 · https://www.migalhas.com.br/depeso/449823/a-tributacao-da-locacao-de-imoveis-na-reforma-tributaria
- Split de pagamento (exemplo Asaas): https://docs.asaas.com/docs/split
