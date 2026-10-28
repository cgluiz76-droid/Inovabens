import { Module } from '@nestjs/common';
import { APP_GUARD } from '@nestjs/core';
import { ScheduleModule } from '@nestjs/schedule';
import { ThrottlerGuard, ThrottlerModule } from '@nestjs/throttler';
import { DbModule } from './db/db.module';
import { JwtAuthGuard } from './auth/jwt-auth.guard';
import { PermissionsGuard } from './rbac/permissions.guard';
// Núcleo
import { AuthModule } from './auth/auth.module';
import { RbacModule } from './rbac/rbac.module';
import { UsersModule } from './users/users.module';
import { AuditoriaModule } from './auditoria/auditoria.module';
import { ArquivosModule } from './arquivos/arquivos.module';
import { HealthModule } from './health/health.module';
// Domínio
import { PessoasModule } from './pessoas/pessoas.module';
import { ImoveisModule } from './imoveis/imoveis.module';
import { FinanceiroModule } from './financeiro/financeiro.module';
import { IndicesModule } from './indices/indices.module';
import { MemorandosModule } from './memorandos/memorandos.module';
import { AgendaModule } from './agenda/agenda.module';
import { AlertasModule } from './alertas/alertas.module';
import { CrmModule } from './crm/crm.module';
import { DashboardModule } from './dashboard/dashboard.module';
import { RelatoriosModule } from './relatorios/relatorios.module';

@Module({
  imports: [
    ScheduleModule.forRoot(),
    ThrottlerModule.forRoot({ throttlers: [{ ttl: 60_000, limit: 300 }], skipIf: () => process.env.NODE_ENV === 'test' }),
    DbModule,
    AuthModule, RbacModule, UsersModule, AuditoriaModule, ArquivosModule, HealthModule,
    PessoasModule, ImoveisModule, FinanceiroModule, IndicesModule, MemorandosModule,
    AgendaModule, AlertasModule, CrmModule, DashboardModule, RelatoriosModule,
  ],
  providers: [
    { provide: APP_GUARD, useClass: ThrottlerGuard },
    { provide: APP_GUARD, useClass: JwtAuthGuard },
    { provide: APP_GUARD, useClass: PermissionsGuard },
  ],
})
export class AppModule {}
