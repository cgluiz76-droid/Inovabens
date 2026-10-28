import { Body, Controller, Delete, Get, HttpCode, Module, Param, ParseUUIDPipe, Patch, Post, Put, Query } from '@nestjs/common';
import { z } from 'zod';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, RequirePermission } from '../common/decorators';
import { ZodPipe } from '../common/zod.pipe';
import * as C from './crm.service';
import { CrmService } from './crm.service';

const UUID = new ParseUUIDPipe();

@Controller()
class CrmController {
  constructor(private readonly s: CrmService) {}

  @Get('crm/pipeline') @RequirePermission('crm:ver')
  pipeline(@CurrentUser() u: AuthUser, @Query(new ZodPipe(C.pipelineQuery)) q: z.infer<typeof C.pipelineQuery>) { return this.s.pipeline(u, q.funil); }

  @Get('leads') @RequirePermission('crm:ver')
  listar(@CurrentUser() u: AuthUser, @Query(new ZodPipe(C.listaLeads)) q: z.infer<typeof C.listaLeads>) { return this.s.listar(u, q); }
  @Post('leads') @RequirePermission('crm:criar')
  criar(@CurrentUser() u: AuthUser, @Body(new ZodPipe(C.leadCriar)) b: z.infer<typeof C.leadCriar>, @ClientIp() ip: string) { return this.s.criar(u, b, ip); }
  @Get('leads/:id') @RequirePermission('crm:ver')
  obter(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.s.obter(u, id); }
  @Patch('leads/:id') @RequirePermission('crm:editar')
  atualizar(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(C.leadAtualizar)) b: z.infer<typeof C.leadAtualizar>, @ClientIp() ip: string) { return this.s.atualizar(u, id, b, ip); }
  @Delete('leads/:id') @HttpCode(204) @RequirePermission('crm:excluir')
  excluir(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string) { return this.s.excluir(u, id, ip); }
  @Patch('leads/:id/estagio') @RequirePermission('crm:editar')
  mover(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(C.moverSchema)) b: z.infer<typeof C.moverSchema>, @ClientIp() ip: string) { return this.s.mover(u, id, b, ip); }

  @Get('leads/:id/historico') @RequirePermission('crm:ver') historico(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.s.historico(u, id); }
  @Get('leads/:id/interacoes') @RequirePermission('crm:ver') interacoes(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.s.interacoes(u, id); }
  @Post('leads/:id/interacoes') @RequirePermission('crm:editar')
  criarInteracao(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(C.interacaoSchema)) b: z.infer<typeof C.interacaoSchema>) { return this.s.criarInteracao(u, id, b); }
  @Get('leads/:id/imoveis-interesse') @RequirePermission('crm:ver')
  async interesse(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return (await this.s.obter(u, id)).imoveis_interesse; }
  @Put('leads/:id/imoveis-interesse') @RequirePermission('crm:editar')
  setInteresse(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(C.interesseSchema)) b: z.infer<typeof C.interesseSchema>) { return this.s.setInteresse(u, id, b); }

  @Get('crm/estagios') @RequirePermission('crm:ver')
  estagios(@Query(new ZodPipe(C.estagiosQuery)) q: z.infer<typeof C.estagiosQuery>) { return this.s.estagios(q.funil); }
  @Put('crm/estagios/ordem') @RequirePermission('admin:config')
  ordem(@Body(new ZodPipe(C.ordemSchema)) b: z.infer<typeof C.ordemSchema>) { return this.s.reordenar(b); }
  @Post('crm/estagios') @RequirePermission('admin:config')
  criarEstagio(@Body(new ZodPipe(C.estagioCriar)) b: z.infer<typeof C.estagioCriar>) { return this.s.criarEstagio(b); }
  @Patch('crm/estagios/:id') @RequirePermission('admin:config')
  atualizarEstagio(@Param('id', UUID) id: string, @Body(new ZodPipe(C.estagioAtualizar)) b: z.infer<typeof C.estagioAtualizar>) { return this.s.atualizarEstagio(id, b); }
  @Delete('crm/estagios/:id') @HttpCode(204) @RequirePermission('admin:config')
  excluirEstagio(@Param('id', UUID) id: string) { return this.s.excluirEstagio(id); }
}

@Module({ controllers: [CrmController], providers: [CrmService], exports: [CrmService] })
export class CrmModule {}
