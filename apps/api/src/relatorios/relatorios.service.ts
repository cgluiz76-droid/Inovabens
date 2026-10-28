import { Inject, Injectable } from '@nestjs/common';
import ExcelJS from 'exceljs';
import { asc, eq } from 'drizzle-orm';
import type { CODIGO_INDICE } from '@inovabens/shared';
import { AuditService } from '../auditoria/audit.service';
import { StorageService } from '../arquivos/storage.service';
import type { AuthUser } from '../common/auth.types';
import { fmtData, fmtDataHora } from '../common/datas';
import { brl } from '../common/money';
import { DB, Database } from '../db/db.module';
import { regularizacoes, pessoas } from '../db/schema';
import { AquisicaoService } from '../financeiro/aquisicao.service';
import { DespesasService } from '../financeiro/despesas.service';
import { RetornoService } from '../financeiro/retorno.service';
import { ImoveisService } from '../imoveis/imoveis.service';
import { campos, COR, novoPdf, paragrafo, secao, tabela } from './pdf.util';

type Codigo = (typeof CODIGO_INDICE)[number];
const STATUS_TXT: Record<string, string> = { PAGO: 'Pago', NAO_PAGO: 'Não pago', NAO_SE_APLICA: 'Não se aplica' };
const pct = (v: number | null | undefined) => (v === null || v === undefined ? '—' : `${(v * 100).toFixed(2).replace('.', ',')}%`);
const num = (v: unknown) => (v === null || v === undefined || v === '' ? null : Number(v));
const enderecoDe = (i: { logradouro?: string | null; numero?: string | null; complemento?: string | null; bairro?: string | null; cidade?: string | null; uf?: string | null; cep?: string | null }) =>
  [[i.logradouro, i.numero].filter(Boolean).join(', '), i.complemento, i.bairro, [i.cidade, i.uf].filter(Boolean).join('/'), i.cep].filter(Boolean).join(' · ') || '—';
const MOEDA = '"R$" #,##0.00';

@Injectable()
export class RelatoriosService {
  constructor(
    @Inject(DB) private readonly db: Database, private readonly imoveis: ImoveisService, private readonly aq: AquisicaoService,
    private readonly desp: DespesasService, private readonly retorno: RetornoService, private readonly storage: StorageService,
    private readonly audit: AuditService,
  ) {}

  /* ============================ Ficha do Imóvel ============================ */
  private async dadosFicha(user: AuthUser, id: string) {
    const im = await this.imoveis.detalhe(user, id);
    const regs = await this.db.select({ r: regularizacoes, prof: pessoas.nome_razao }).from(regularizacoes)
      .leftJoin(pessoas, eq(pessoas.id, regularizacoes.profissional_id)).where(eq(regularizacoes.imovel_id, id)).orderBy(asc(regularizacoes.criado_em));
    return { im, regs: regs.filter((x) => !x.r.excluido_em).map((x) => ({ ...x.r, profissional: x.prof })) };
  }

  async fichaImovelPdf(user: AuthUser, id: string, ip: string) {
    const { im, regs } = await this.dadosFicha(user, id);
    const { doc, finalizar } = novoPdf('Ficha do Imóvel', `${im.codigo} · ${im.titulo}`, `Ficha do Imóvel ${im.codigo} · gerada em ${fmtDataHora(new Date())}`);
    const capa = im.midias.find((m) => m.capa && m.tipo === 'FOTO') ?? im.midias.find((m) => m.tipo === 'FOTO');
    const foto = capa ? await this.storage.baixar(capa.arquivo_chave) : null;
    if (foto) { try { doc.image(foto, 400, 92, { fit: [155, 105] }); } catch { /* formato não suportado pelo PDF (ex.: webp): segue sem foto */ } }

    secao(doc, 'Identificação');
    campos(doc, [['Código', im.codigo], ['Situação', im.situacao.replace(/_/g, ' ')], ['Finalidade', im.finalidade], ['Tipo', [im.tipo, im.subtipo].filter(Boolean).join(' / ')], ['Responsável', im.corretor?.nome], ['Divulgar no site', im.divulgar_site ? 'Sim' : 'Não']], 3);
    secao(doc, 'Localização');
    campos(doc, [['Endereço', enderecoDe(im)], ['Endereço público no site', im.endereco_publico ? 'Sim' : 'Não']], 1);
    secao(doc, 'Dimensões e composição');
    campos(doc, [['Área total (m²)', im.area_total_m2], ['Área construída (m²)', im.area_construida_m2], ['Área útil (m²)', im.area_util_m2], ['Quartos', im.quartos], ['Suítes', im.suites], ['Banheiros', im.banheiros], ['Vagas', im.vagas]], 4);
    secao(doc, 'Valores de divulgação');
    campos(doc, [['Venda', im.preco_venda ? brl(im.preco_venda) : '—'], ['Locação', im.preco_locacao ? brl(im.preco_locacao) : '—']], 2);
    secao(doc, 'Descrição pública');
    paragrafo(doc, im.descricao_publica);
    secao(doc, 'Características');
    tabela(doc, [{ h: 'Característica', w: 200 }, { h: 'Valor', w: 215 }, { h: 'Divulgação', w: 100 }],
      im.caracteristicas.map((c) => [c.chave, c.valor ?? 'Sim', c.publico ? 'Pública' : 'Interna']));

    secao(doc, 'Dados internos (não divulgar)');
    campos(doc, [['Matrícula', im.matricula], ['Cartório', im.cartorio], ['Inscrição municipal', im.inscricao_municipal]], 3);
    paragrafo(doc, im.descricao_interna);
    secao(doc, 'Proprietários');
    tabela(doc, [{ h: 'Nome', w: 215 }, { h: 'CPF/CNPJ', w: 110 }, { h: 'Contato', w: 120 }, { h: '%', w: 70, align: 'right' }],
      im.proprietarios.map((p) => [p.pessoa.nome_razao, p.pessoa.cpf_cnpj ?? '—', p.pessoa.telefone ?? p.pessoa.email ?? '—', Number(p.percentual).toFixed(2)]));
    secao(doc, 'Envolvidos (fiadores, parceiros, prestadores, terceiros)');
    tabela(doc, [{ h: 'Nome', w: 215 }, { h: 'Papel', w: 100 }, { h: 'Contato', w: 200 }], im.envolvidos.map((e) => [e.pessoa.nome_razao, e.papel, e.pessoa.telefone ?? e.pessoa.email ?? '—']));
    secao(doc, 'Regularização');
    tabela(doc, [{ h: 'Tipo', w: 110 }, { h: 'Status', w: 85 }, { h: 'Profissional', w: 130 }, { h: 'Prazo', w: 60 }, { h: 'Custo est.', w: 130, align: 'right' }],
      regs.map((r) => [r.tipo.replace(/_/g, ' '), r.status.replace(/_/g, ' '), r.profissional ?? '—', fmtData(r.prazo_previsto), r.custo_estimado ? brl(r.custo_estimado) : '—']));
    secao(doc, 'Mídias');
    paragrafo(doc, `${im.midias.filter((m) => m.tipo === 'FOTO').length} foto(s) · ${im.midias.filter((m) => m.tipo !== 'FOTO').length} outro(s) arquivo(s) (vídeos, plantas, documentos).`);
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'EXPORTAR', depois: { relatorio: 'ficha-imovel', formato: 'pdf' }, ip });
    return { buffer: await finalizar(), nome: `ficha-${im.codigo}.pdf` };
  }

  async fichaImovelXlsx(user: AuthUser, id: string, ip: string) {
    const { im, regs } = await this.dadosFicha(user, id);
    const wb = new ExcelJS.Workbook(); wb.creator = 'Inovabens';
    const ws = wb.addWorksheet('Ficha do Imóvel', { views: [{ showGridLines: false }] });
    ws.columns = [{ width: 28 }, { width: 60 }, { width: 28 }, { width: 40 }];
    ws.mergeCells('A1:D1'); ws.getCell('A1').value = `FICHA DO IMÓVEL — ${im.codigo}`;
    ws.getCell('A1').font = { bold: true, size: 14, color: { argb: 'FFFFFFFF' } }; ws.getCell('A1').fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FF1E3A8A' } };
    const linha = (a: string, b: unknown, c?: string, d?: unknown) => { const r = ws.addRow([a, b ?? '—', c ?? '', d ?? '']); r.getCell(1).font = { bold: true }; r.getCell(3).font = { bold: true }; return r; };
    const titulo = (t: string) => { const r = ws.addRow([t.toUpperCase()]); r.font = { bold: true, color: { argb: 'FF1E3A8A' } }; r.border = { bottom: { style: 'thin' } }; };
    ws.addRow([]);
    titulo('Identificação');
    linha('Título', im.titulo, 'Código', im.codigo); linha('Situação', im.situacao, 'Finalidade', im.finalidade);
    linha('Tipo', [im.tipo, im.subtipo].filter(Boolean).join(' / '), 'Responsável', im.corretor?.nome); linha('Divulgar no site', im.divulgar_site ? 'Sim' : 'Não');
    titulo('Localização'); linha('Endereço', enderecoDe(im)); linha('Latitude/Longitude', im.latitude && im.longitude ? `${im.latitude}, ${im.longitude}` : '—');
    titulo('Dimensões');
    linha('Área total (m²)', num(im.area_total_m2), 'Quartos', im.quartos); linha('Área construída (m²)', num(im.area_construida_m2), 'Suítes', im.suites);
    linha('Área útil (m²)', num(im.area_util_m2), 'Banheiros', im.banheiros); linha('', '', 'Vagas', im.vagas);
    titulo('Valores de divulgação');
    const v = linha('Venda', num(im.preco_venda), 'Locação', num(im.preco_locacao)); v.getCell(2).numFmt = MOEDA; v.getCell(4).numFmt = MOEDA; v.getCell(2).alignment = { horizontal: 'left' }; v.getCell(4).alignment = { horizontal: 'left' };
    titulo('Descrição pública'); ws.mergeCells(`A${ws.rowCount + 1}:D${ws.rowCount + 1}`); const d = ws.addRow([im.descricao_publica ?? '—']); d.alignment = { wrapText: true, vertical: 'top' }; d.height = 80;
    titulo('Dados internos'); linha('Matrícula', im.matricula, 'Cartório', im.cartorio); linha('Inscrição municipal', im.inscricao_municipal);
    ws.mergeCells(`A${ws.rowCount + 1}:D${ws.rowCount + 1}`); const di = ws.addRow([im.descricao_interna ?? '—']); di.alignment = { wrapText: true, vertical: 'top' }; di.height = 60;

    const tab = (nome: string, cab: string[], rows: unknown[][], larg: number[]) => {
      const s = wb.addWorksheet(nome); s.columns = cab.map((h, i) => ({ header: h, width: larg[i] }));
      s.getRow(1).font = { bold: true, color: { argb: 'FFFFFFFF' } }; s.getRow(1).fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FF1E3A8A' } };
      rows.forEach((r) => s.addRow(r)); s.views = [{ state: 'frozen', ySplit: 1 }]; return s;
    };
    tab('Características', ['Característica', 'Valor', 'Divulgação'], im.caracteristicas.map((c) => [c.chave, c.valor ?? 'Sim', c.publico ? 'Pública' : 'Interna']), [32, 40, 14]);
    tab('Pessoas', ['Vínculo', 'Nome', 'CPF/CNPJ', 'Contato', '%'], [
      ...im.proprietarios.map((p) => ['Proprietário', p.pessoa.nome_razao, p.pessoa.cpf_cnpj ?? '', p.pessoa.telefone ?? p.pessoa.email ?? '', Number(p.percentual)]),
      ...im.envolvidos.map((e) => [e.papel, e.pessoa.nome_razao, '', e.pessoa.telefone ?? e.pessoa.email ?? '', '']),
    ], [16, 40, 20, 30, 8]);
    const sr = tab('Regularização', ['Tipo', 'Status', 'Profissional', 'Prazo', 'Custo estimado', 'Descrição'],
      regs.map((r) => [r.tipo, r.status, r.profissional ?? '', fmtData(r.prazo_previsto), num(r.custo_estimado), r.descricao ?? '']), [22, 16, 28, 12, 18, 50]);
    sr.getColumn(5).numFmt = MOEDA;
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'EXPORTAR', depois: { relatorio: 'ficha-imovel', formato: 'xlsx' }, ip });
    return { buffer: Buffer.from(await wb.xlsx.writeBuffer()), nome: `ficha-${im.codigo}.xlsx` };
  }

  /* =========================== Ficha Financeira =========================== */
  private async dadosFinanceiros(user: AuthUser, id: string, indice: Codigo) {
    const [im, ret, lanc] = await Promise.all([this.imoveis.detalhe(user, id), this.retorno.calcular(user, id, indice), this.desp.historico(user, id)]);
    const aq = await this.aq.obter(user, id).catch(() => null);
    return { im, ret, lanc, aq };
  }

  async fichaFinanceiraPdf(user: AuthUser, id: string, indice: Codigo, ip: string) {
    const { im, ret, lanc, aq } = await this.dadosFinanceiros(user, id, indice);
    const { doc, finalizar } = novoPdf('Ficha Financeira', `${im.codigo} · ${im.titulo}`, `Ficha Financeira ${im.codigo} · gerada em ${fmtDataHora(new Date())}`);
    secao(doc, 'Resumo do investimento');
    campos(doc, [
      ['Custo de aquisição', brl(ret.custo_aquisicao)], ['Custo total investido', brl(ret.custo_total)], ['Passivo pendente (não pago)', brl(ret.passivo_pendente)],
      ['Receita líquida', brl(ret.receita_liquida)], ['Resultado', brl(ret.resultado)], [`Custo corrigido (${ret.indice})`, ret.custo_corrigido ? brl(ret.custo_corrigido) : '—'],
      ['ROI nominal', pct(ret.roi)], ['ROI real (acima do índice)', pct(ret.roi_real)], ['TIR anualizada', pct(ret.tir_anual)],
      ['MOIC', ret.moic === null ? '—' : ret.moic.toFixed(2).replace('.', ',') + '×'], ['Prazo (dias)', ret.prazo_dias], ['Data de corte', fmtData(ret.data_final)],
    ], 3);
    ret.avisos.forEach((a) => { doc.font('Helvetica-Oblique').fontSize(8.5).fillColor(COR.suave).text(`• ${a}`, 40, doc.y, { width: 515 }); });
    secao(doc, 'Aquisição (CAPEX)');
    if (!aq) paragrafo(doc, 'Aquisição não cadastrada.');
    else {
      campos(doc, [['Data', fmtData(aq.data_aquisicao)], ['Valor pago', brl(aq.valor_pago)], ['Impostos', brl(aq.impostos)], ['Taxas', brl(aq.taxas)], ['Despesas diversas', brl(aq.despesas_diversas)], ['TOTAL DE AQUISIÇÃO', brl(aq.total_aquisicao)]], 3);
      tabela(doc, [{ h: 'Tipo', w: 70 }, { h: 'Descrição', w: 235 }, { h: 'Data', w: 70 }, { h: 'Valor', w: 140, align: 'right' }],
        aq.itens.map((i) => [i.tipo, i.descricao, fmtData(i.data_pagamento), brl(i.valor)]), 'Sem itens detalhados.');
    }
    secao(doc, 'Despesas e status de pagamento (OPEX)');
    tabela(doc, [{ h: 'Competência', w: 60 }, { h: 'Categoria', w: 85 }, { h: 'Descrição', w: 110 }, { h: 'Venc.', w: 55 }, { h: 'Valor', w: 65, align: 'right' }, { h: 'Status', w: 55 }, { h: 'Justificativa', w: 85 }],
      lanc.map((l) => [`${l.competencia.slice(5, 7)}/${l.competencia.slice(0, 4)}`, l.categoria, l.descricao, fmtData(l.vencimento), brl(l.valor), l.status ? STATUS_TXT[l.status] : 'A marcar', l.justificativa ?? '']));
    secao(doc, 'Fluxo de caixa');
    tabela(doc, [{ h: 'Data', w: 60 }, { h: 'Tipo', w: 55 }, { h: 'Descrição', w: 270 }, { h: 'Valor', w: 130, align: 'right' }], ret.fluxo.map((f) => [fmtData(f.data), f.tipo === 'SAIDA' ? 'Saída' : 'Entrada', f.descricao, `${f.tipo === 'SAIDA' ? '-' : ''}${brl(f.valor)}`]));
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'EXPORTAR', depois: { relatorio: 'ficha-financeira', formato: 'pdf', indice }, ip });
    return { buffer: await finalizar(), nome: `ficha-financeira-${im.codigo}.pdf` };
  }

  async fichaFinanceiraXlsx(user: AuthUser, id: string, indice: Codigo, ip: string) {
    const { im, ret, lanc, aq } = await this.dadosFinanceiros(user, id, indice);
    const wb = new ExcelJS.Workbook(); wb.creator = 'Inovabens';
    const cab = (s: ExcelJS.Worksheet) => { s.getRow(1).font = { bold: true, color: { argb: 'FFFFFFFF' } }; s.getRow(1).fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FF1E3A8A' } }; s.views = [{ state: 'frozen', ySplit: 1 }]; };

    const r = wb.addWorksheet('Resumo'); r.columns = [{ header: 'Indicador', width: 38 }, { header: 'Valor', width: 24 }]; cab(r);
    const m = (k: string, v: unknown, fmt?: string) => { const x = r.addRow([k, v]); if (fmt) x.getCell(2).numFmt = fmt; x.getCell(2).alignment = { horizontal: 'right' }; };
    m('Imóvel', `${im.codigo} — ${im.titulo}`); m('Data de corte', fmtData(ret.data_final)); m('Índice de correção', ret.indice);
    m('Custo de aquisição', num(ret.custo_aquisicao), MOEDA); m('Custo total investido', num(ret.custo_total), MOEDA); m('Passivo pendente (não pago)', num(ret.passivo_pendente), MOEDA);
    m('Custo corrigido', num(ret.custo_corrigido), MOEDA); m('Receita líquida', num(ret.receita_liquida), MOEDA); m('Resultado', num(ret.resultado), MOEDA);
    m('ROI nominal', ret.roi, '0.00%'); m('ROI real', ret.roi_real, '0.00%'); m('TIR anualizada', ret.tir_anual, '0.00%'); m('MOIC', ret.moic, '0.00"×"'); m('Prazo (dias)', ret.prazo_dias);
    ret.avisos.forEach((a) => r.addRow(['Aviso', a]));

    const a = wb.addWorksheet('Aquisição'); a.columns = [{ header: 'Tipo', width: 14 }, { header: 'Descrição', width: 44 }, { header: 'Data', width: 12 }, { header: 'Valor', width: 18 }]; cab(a);
    if (aq) {
      aq.itens.forEach((i) => a.addRow([i.tipo, i.descricao, fmtData(i.data_pagamento), num(i.valor)]));
      a.addRow([]); [['Valor pago', aq.valor_pago], ['Impostos', aq.impostos], ['Taxas', aq.taxas], ['Despesas diversas', aq.despesas_diversas]].forEach(([k, v]) => a.addRow(['', k, '', num(v)]));
      const t = a.addRow(['', 'TOTAL DE AQUISIÇÃO', '', num(aq.total_aquisicao)]); t.font = { bold: true };
      a.getColumn(4).numFmt = MOEDA;
    }

    const d = wb.addWorksheet('Despesas'); d.columns = [{ header: 'Competência', width: 13 }, { header: 'Categoria', width: 22 }, { header: 'Descrição', width: 34 }, { header: 'Vencimento', width: 12 }, { header: 'Valor', width: 16 }, { header: 'Status', width: 14 }, { header: 'Pago em', width: 12 }, { header: 'Justificativa', width: 50 }]; cab(d);
    lanc.forEach((l) => d.addRow([`${l.competencia.slice(5, 7)}/${l.competencia.slice(0, 4)}`, l.categoria, l.descricao, fmtData(l.vencimento), num(l.valor), l.status ? STATUS_TXT[l.status] : 'A marcar', fmtData(l.data_pagamento), l.justificativa ?? '']));
    d.getColumn(5).numFmt = MOEDA; d.autoFilter = 'A1:H1';

    const f = wb.addWorksheet('Fluxo de caixa'); f.columns = [{ header: 'Data', width: 12 }, { header: 'Tipo', width: 10 }, { header: 'Descrição', width: 60 }, { header: 'Valor', width: 18 }, { header: `Valor corrigido (${ret.indice})`, width: 24 }]; cab(f);
    ret.fluxo.forEach((x) => f.addRow([fmtData(x.data), x.tipo === 'SAIDA' ? 'Saída' : 'Entrada', x.descricao, num(x.valor), num(x.valor_corrigido)]));
    f.getColumn(4).numFmt = MOEDA; f.getColumn(5).numFmt = MOEDA;
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'EXPORTAR', depois: { relatorio: 'ficha-financeira', formato: 'xlsx', indice }, ip });
    return { buffer: Buffer.from(await wb.xlsx.writeBuffer()), nome: `ficha-financeira-${im.codigo}.xlsx` };
  }
}
