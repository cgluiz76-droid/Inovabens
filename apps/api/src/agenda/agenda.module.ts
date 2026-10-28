import { Body, Controller, Delete, Get, Headers, HttpCode, Param, ParseUUIDPipe, Patch, Post, Query, Res } from '@nestjs/common';
import type { Response } from 'express';
import { z } from 'zod';
import { Module } from '@nestjs/common';
import { AlertasModule } from '../alertas/alertas.module';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, Public, RequirePermission } from '../common/decorators';
import { ZodPipe } from '../common/zod.pipe';
import { AgendaService, compromissoAtualizar, compromissoCriar, listaSchema, reagendarSchema } from './agenda.service';
import { GoogleService } from './google.service';

const UUID = new ParseUUIDPipe();
const callbackQuery = z.object({ code: z.string().min(1), state: z.string().min(1) });

@Controller()
class AgendaController {
  constructor(private readonly s: AgendaService) {}

  @Get('compromissos') @RequirePermission('agenda:ver')
  listar(@CurrentUser() u: AuthUser, @Query(new ZodPipe(listaSchema)) q: z.infer<typeof listaSchema>) { return this.s.listar(u, q); }
  @Get('imoveis/:id/compromissos') @RequirePermission('agenda:ver')
  doImovel(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.s.listarDoImovel(u, id); }
  @Post('imoveis/:id/compromissos') @RequirePermission('agenda:criar')
  criar(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(compromissoCriar)) b: z.infer<typeof compromissoCriar>, @ClientIp() ip: string) { return this.s.criar(u, id, b, ip); }
  @Get('compromissos/:id') @RequirePermission('agenda:ver')
  obter(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.s.obter(u, id); }
  @Patch('compromissos/:id') @RequirePermission('agenda:editar')
  atualizar(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(compromissoAtualizar)) b: z.infer<typeof compromissoAtualizar>, @ClientIp() ip: string) { return this.s.atualizar(u, id, b, ip); }
  @Post('compromissos/:id/concluir') @HttpCode(200) @RequirePermission('agenda:editar')
  concluir(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string) { return this.s.concluir(u, id, ip); }
  @Delete('compromissos/:id') @HttpCode(204) @RequirePermission('agenda:excluir')
  excluir(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string) { return this.s.excluir(u, id, ip); }
  @Patch('alertas/:id/reagendar') @RequirePermission('agenda:editar')
  reagendar(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(reagendarSchema)) b: z.infer<typeof reagendarSchema>, @ClientIp() ip: string) { return this.s.reagendarPorAlerta(u, id, b, ip); }
}

@Controller()
class GoogleController {
  constructor(private readonly g: GoogleService) {}
  @Get('integracoes/google/status') status(@CurrentUser() u: AuthUser) { return this.g.status(u.id); }
  @Get('integracoes/google/auth-url') @RequirePermission('agenda:ver') authUrl(@CurrentUser() u: AuthUser) { return this.g.urlAutorizacao(u.id); }

  /** O Google redireciona o navegador para cá; a identidade vem do `state` assinado (não há Bearer neste salto). */
  @Public() @Get('integracoes/google/callback')
  async callback(@Query(new ZodPipe(callbackQuery)) q: z.infer<typeof callbackQuery>, @Res() res: Response) {
    try { res.redirect(await this.g.callback(q.code, q.state)); } catch { res.redirect('/agenda?google=erro'); }
  }
  @Post('integracoes/google/sync') @HttpCode(200) @RequirePermission('agenda:editar') sync(@CurrentUser() u: AuthUser) { return this.g.sincronizar(u.id); }
  @Delete('integracoes/google') @HttpCode(204) desconectar(@CurrentUser() u: AuthUser) { return this.g.desconectar(u.id); }

  @Public() @Post('webhooks/google/calendar') @HttpCode(200)
  async webhook(@Headers('x-goog-channel-id') canal?: string, @Headers('x-goog-channel-token') token?: string, @Headers('x-goog-resource-state') estado?: string) {
    await this.g.webhook(canal, token, estado);
    return {};
  }
}

@Module({
  imports: [AlertasModule],
  controllers: [AgendaController, GoogleController],
  providers: [AgendaService, GoogleService],
  exports: [AgendaService, GoogleService],
})
export class AgendaModule {}
