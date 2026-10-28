import { BadGatewayException, Body, Controller, Get, HttpCode, Inject, Injectable, Logger, Module, Param, Post, Put, Query } from '@nestjs/common';
import { Cron } from '@nestjs/schedule';
import { and, asc, eq, gte, lte, sql } from 'drizzle-orm';
import { z } from 'zod';
import { CODIGO_INDICE } from '@inovabens/shared';
import type { AuthUser } from '../common/auth.types';
import { CurrentUser, RequirePermission } from '../common/decorators';
import { dataISO } from '../common/schemas';
import { ZodPipe } from '../common/zod.pipe';
import { env } from '../config/env';
import { DB, Database } from '../db/db.module';
import { indices_serie } from '../db/schema';

type Codigo = (typeof CODIGO_INDICE)[number];
/** Séries SGS do Banco Central: variação mensal em %. */
const SGS: Record<Codigo, number> = { IPCA: 433, INCC: 192, IGPM: 189, SELIC: 4390 };

const codigoSchema = z.enum(CODIGO_INDICE);
const serieQuery = z.object({ de: dataISO.optional(), ate: dataISO.optional() });
const manualSchema = z.object({
  competencia: z.string().regex(/^\d{4}-(0[1-9]|1[0-2])(-01)?$/, 'Use AAAA-MM'),
  variacao_mensal_pct: z.union([z.string().regex(/^-?\d+(\.\d+)?$/), z.number()]).transform(String),
});
const syncSchema = z.object({ codigos: z.array(codigoSchema).optional() }).default({});

/** Fator acumulado F(mês) = Π(1 + v/100) até o mês. A correção entre t_i e t_v é F(t_v)/F(t_i). */
export interface FatoresIndice {
  fator(data: string): number;
  primeiroMes: string | null;
  ultimoMes: string | null;
}

@Injectable()
export class IndicesService {
  private readonly log = new Logger('Indices');
  constructor(@Inject(DB) private readonly db: Database) {}

  serie(codigo: Codigo, de?: string, ate?: string) {
    return this.db.select().from(indices_serie)
      .where(and(eq(indices_serie.codigo, codigo), de ? gte(indices_serie.competencia, de) : undefined, ate ? lte(indices_serie.competencia, ate) : undefined))
      .orderBy(asc(indices_serie.competencia));
  }

  async fatores(codigo: Codigo): Promise<FatoresIndice> {
    const rows = await this.serie(codigo);
    let acc = 1;
    const pontos = rows.map((r) => { acc *= 1 + Number(r.variacao_mensal_pct) / 100; return { mes: r.competencia.slice(0, 7), f: acc }; });
    return {
      primeiroMes: pontos[0]?.mes ?? null,
      ultimoMes: pontos.at(-1)?.mes ?? null,
      fator(data: string) {
        const mes = data.slice(0, 7);
        let f = 1;
        for (const p of pontos) { if (p.mes <= mes) f = p.f; else break; }
        return f;
      },
    };
  }

  async upsert(codigo: Codigo, pontos: { competencia: string; variacao_mensal_pct: string; fonte?: string }[]) {
    for (let i = 0; i < pontos.length; i += 500) {
      await this.db.insert(indices_serie)
        .values(pontos.slice(i, i + 500).map((p) => ({ codigo, competencia: p.competencia, variacao_mensal_pct: p.variacao_mensal_pct, fonte: p.fonte ?? 'MANUAL' })))
        .onConflictDoUpdate({
          target: [indices_serie.codigo, indices_serie.competencia],
          set: { variacao_mensal_pct: sql`excluded.variacao_mensal_pct`, fonte: sql`excluded.fonte`, atualizado_em: new Date() },
        });
    }
  }

  /** Busca as séries no BCB (SGS). Falha de rede vira 502 sem derrubar o que já foi gravado. */
  async sincronizar(codigos: Codigo[] = [...CODIGO_INDICE]) {
    const resultado: Record<string, number> = {};
    for (const codigo of codigos) {
      const ultimo = await this.db.select({ c: sql<string | null>`max(${indices_serie.competencia})` }).from(indices_serie).where(eq(indices_serie.codigo, codigo));
      const ini = ultimo[0]?.c ? `01/${ultimo[0].c.slice(5, 7)}/${ultimo[0].c.slice(0, 4)}` : '01/01/2000';
      const url = `https://api.bcb.gov.br/dados/serie/bcdata.sgs.${SGS[codigo]}/dados?formato=json&dataInicial=${ini}`;
      let json: { data: string; valor: string }[];
      try {
        const r = await fetch(url, { signal: AbortSignal.timeout(30_000) });
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        json = (await r.json()) as { data: string; valor: string }[];
      } catch (e) {
        throw new BadGatewayException(`Falha ao consultar o Banco Central (${codigo}): ${(e as Error).message}`);
      }
      const pontos = json.map((j) => ({ competencia: `${j.data.slice(6, 10)}-${j.data.slice(3, 5)}-01`, variacao_mensal_pct: j.valor, fonte: 'BCB-SGS' }));
      await this.upsert(codigo, pontos);
      resultado[codigo] = pontos.length;
    }
    return resultado;
  }

  @Cron('0 6 12 * *', { timeZone: 'America/Sao_Paulo' }) // dia 12, após as divulgações do IPCA/INCC
  async jobMensal() {
    if (env().DISABLE_JOBS) return;
    try { this.log.log(`Sync BCB: ${JSON.stringify(await this.sincronizar())}`); } catch (e) { this.log.error((e as Error).message); }
  }
}

@Controller('indices')
class IndicesController {
  constructor(private readonly s: IndicesService) {}

  @Post('sync') @HttpCode(200) @RequirePermission('admin:config')
  sync(@Body(new ZodPipe(syncSchema)) b: z.infer<typeof syncSchema>) { return this.s.sincronizar(b.codigos); }

  @Get(':codigo/serie') @RequirePermission('financeiro:ver')
  serie(@Param('codigo', new ZodPipe(codigoSchema)) codigo: Codigo, @Query(new ZodPipe(serieQuery)) q: z.infer<typeof serieQuery>) { return this.s.serie(codigo, q.de, q.ate); }

  /** Lançamento manual (ex.: índice ainda não publicado pelo BCB ou correção de valor). */
  @Put(':codigo/serie') @RequirePermission('admin:config')
  async manual(@Param('codigo', new ZodPipe(codigoSchema)) codigo: Codigo, @Body(new ZodPipe(manualSchema)) b: z.infer<typeof manualSchema>, @CurrentUser() _u: AuthUser) {
    await this.s.upsert(codigo, [{ competencia: `${b.competencia.slice(0, 7)}-01`, variacao_mensal_pct: b.variacao_mensal_pct, fonte: 'MANUAL' }]);
    return { ok: true };
  }
}

@Module({ controllers: [IndicesController], providers: [IndicesService], exports: [IndicesService] })
export class IndicesModule {}
