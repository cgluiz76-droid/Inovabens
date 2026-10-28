import { Controller, Get, Inject, Injectable, Module } from '@nestjs/common';
import { and, asc, eq, isNull, sql } from 'drizzle-orm';
import { AgendaModule } from '../agenda/agenda.module';
import { AgendaService } from '../agenda/agenda.service';
import { AlertasModule } from '../alertas/alertas.module';
import { AlertasService } from '../alertas/alertas.module';
import type { AuthUser } from '../common/auth.types';
import { CurrentUser } from '../common/decorators';
import { DB, Database } from '../db/db.module';
import { despesa_lancamentos, despesas, funil_estagios, imoveis, leads } from '../db/schema';

@Injectable()
export class DashboardService {
  constructor(@Inject(DB) private readonly db: Database, private readonly agenda: AgendaService, private readonly alertas: AlertasService) {}

  /** Resumo respeitando o escopo do perfil (Corretor vê só os seus) e as permissões. */
  async resumo(user: AuthUser) {
    const tem = (p: string) => (user.permissoes as string[]).includes(p);
    const imoveisPorSituacao = tem('imoveis:ver')
      ? await this.db.select({ situacao: imoveis.situacao, total: sql<number>`count(*)::int` }).from(imoveis)
        .where(and(isNull(imoveis.excluido_em), user.escopo_proprio ? eq(imoveis.corretor_responsavel_id, user.id) : undefined)).groupBy(imoveis.situacao)
      : [];
    const leadsPorEstagio = tem('crm:ver')
      ? await this.db.select({ estagio: funil_estagios.nome, cor: funil_estagios.cor, total: sql<number>`count(${leads.id})::int` }).from(funil_estagios)
        .leftJoin(leads, and(eq(leads.estagio_id, funil_estagios.id), isNull(leads.excluido_em), user.escopo_proprio ? eq(leads.responsavel_id, user.id) : undefined))
        .where(and(eq(funil_estagios.funil, 'VENDA'), eq(funil_estagios.ativo, true))).groupBy(funil_estagios.id).orderBy(asc(funil_estagios.ordem))
      : [];
    let naoPagos: number | null = null;
    if (tem('financeiro:ver')) {
      const [r] = await this.db.select({ n: sql<number>`count(*)::int` }).from(despesa_lancamentos)
        .innerJoin(despesas, eq(despesas.id, despesa_lancamentos.despesa_id)).where(and(isNull(despesas.excluido_em), eq(despesa_lancamentos.status, 'NAO_PAGO')));
      naoPagos = r.n;
    }
    const proximos = tem('agenda:ver') ? (await this.agenda.listar(user, { de: new Date(), status: 'PENDENTE' })).slice(0, 5) : [];
    return {
      imoveis_por_situacao: imoveisPorSituacao, leads_por_estagio: leadsPorEstagio,
      alertas_abertos: (await this.alertas.contagem(user)).abertos, lancamentos_nao_pagos: naoPagos, proximos_compromissos: proximos,
    };
  }
}

@Controller('dashboard')
class DashboardController {
  constructor(private readonly s: DashboardService) {}
  @Get() resumo(@CurrentUser() u: AuthUser) { return this.s.resumo(u); }
}

@Module({ imports: [AgendaModule, AlertasModule], controllers: [DashboardController], providers: [DashboardService] })
export class DashboardModule {}
