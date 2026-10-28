import PDFDocument from 'pdfkit';

export const COR = { tinta: '#0f172a', suave: '#64748b', linha: '#e2e8f0', marca: '#1e3a8a', zebra: '#f8fafc' };
type Doc = InstanceType<typeof PDFDocument>;

/** Cria o PDF, devolve o doc e uma função que finaliza e entrega o Buffer (com rodapé e numeração). */
export function novoPdf(titulo: string, subtitulo: string, rodape: string) {
  const doc = new PDFDocument({ size: 'A4', margin: 40, bufferPages: true, info: { Title: titulo, Author: 'Inovabens' } });
  const partes: Buffer[] = [];
  doc.on('data', (c: Buffer) => partes.push(c));
  doc.rect(0, 0, doc.page.width, 78).fill(COR.marca);
  doc.fillColor('#ffffff').font('Helvetica-Bold').fontSize(18).text(titulo, 40, 22, { width: 400 });
  doc.font('Helvetica').fontSize(10).text(subtitulo, 40, 48, { width: 515 });
  doc.fillColor('#ffffff').font('Helvetica-Bold').fontSize(13).text('INOVABENS', 400, 26, { width: 155, align: 'right' });
  doc.fillColor(COR.tinta).y = 96;
  const finalizar = () => new Promise<Buffer>((resolve) => {
    const n = doc.bufferedPageRange();
    for (let i = n.start; i < n.start + n.count; i++) {
      doc.switchToPage(i);
      doc.font('Helvetica').fontSize(8).fillColor(COR.suave)
        .text(`${rodape}  ·  Página ${i + 1} de ${n.count}`, 40, doc.page.height - 30, { width: 515, align: 'center', lineBreak: false });
    }
    doc.on('end', () => resolve(Buffer.concat(partes)));
    doc.end();
  });
  return { doc, finalizar };
}

const espaco = (doc: Doc, h: number) => { if (doc.y + h > doc.page.height - 50) { doc.addPage(); doc.y = 40; } };

export function secao(doc: Doc, titulo: string) {
  espaco(doc, 50);
  doc.moveDown(0.8).font('Helvetica-Bold').fontSize(11).fillColor(COR.marca).text(titulo.toUpperCase(), 40, doc.y);
  doc.moveTo(40, doc.y + 2).lineTo(555, doc.y + 2).strokeColor(COR.linha).lineWidth(1).stroke();
  doc.moveDown(0.6).fillColor(COR.tinta);
}

/** Grade de "rótulo: valor" em N colunas. */
export function campos(doc: Doc, pares: [string, string | number | null | undefined][], colunas = 2) {
  const w = 515 / colunas;
  for (let i = 0; i < pares.length; i += colunas) {
    espaco(doc, 30);
    const y = doc.y;
    let maior = 0;
    pares.slice(i, i + colunas).forEach(([rotulo, valor], j) => {
      const x = 40 + j * w;
      doc.font('Helvetica').fontSize(7.5).fillColor(COR.suave).text(rotulo.toUpperCase(), x, y, { width: w - 10 });
      doc.font('Helvetica-Bold').fontSize(10).fillColor(COR.tinta).text(String(valor ?? '—') || '—', x, doc.y, { width: w - 10 });
      maior = Math.max(maior, doc.y);
      doc.y = y;
    });
    doc.y = maior + 6;
  }
}

export function paragrafo(doc: Doc, texto: string | null | undefined) {
  espaco(doc, 30);
  doc.font('Helvetica').fontSize(10).fillColor(COR.tinta).text(texto?.trim() || '—', 40, doc.y, { width: 515, align: 'justify' });
  doc.moveDown(0.4);
}

export interface Coluna { h: string; w: number; align?: 'left' | 'right' | 'center' }
export function tabela(doc: Doc, cols: Coluna[], linhas: string[][], vazio = 'Nenhum registro.') {
  if (!linhas.length) { paragrafo(doc, vazio); return; }
  const cab = () => {
    espaco(doc, 30);
    const y = doc.y;
    doc.rect(40, y - 2, 515, 16).fill(COR.marca);
    let x = 40;
    doc.font('Helvetica-Bold').fontSize(8).fillColor('#ffffff');
    cols.forEach((c) => { doc.text(c.h, x + 3, y + 2, { width: c.w - 6, align: c.align ?? 'left', lineBreak: false }); x += c.w; });
    doc.y = y + 18; doc.fillColor(COR.tinta);
  };
  cab();
  linhas.forEach((l, idx) => {
    doc.font('Helvetica').fontSize(8.5);
    const h = Math.max(...l.map((t, i) => doc.heightOfString(t, { width: cols[i].w - 6 }))) + 6;
    if (doc.y + h > doc.page.height - 50) { doc.addPage(); doc.y = 40; cab(); }
    const y = doc.y;
    if (idx % 2) doc.rect(40, y - 1, 515, h).fill(COR.zebra);
    let x = 40;
    l.forEach((t, i) => { doc.fillColor(COR.tinta).text(t, x + 3, y + 2, { width: cols[i].w - 6, align: cols[i].align ?? 'left' }); x += cols[i].w; });
    doc.y = y + h;
  });
  doc.moveDown(0.4);
}
